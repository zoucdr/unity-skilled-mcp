using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Profiling;
using UniMcp.Models;

namespace UniMcp.Tools
{
    [ToolModule("runtime", GroupEn = "Runtime", GroupZh = "运行与调试",
        Tags = new[] { "profiling", "memory", "diagnostics", "runtime-control" })]
    public sealed class RuntimeWorkflowTool : ToolModuleBase, IStrictToolModule
    {
        private static readonly Dictionary<string, string> Snapshots = new Dictionary<string, string>();
        public override string Description => "Profiler计数、内存统计、对象快照、UI输入模拟及快照回归";
        protected override MethodKey[] CreateKeys() => new MethodKey[] {
            new MethodInt("instance_id", "场景对象ID"), new MethodStr("name", "快照名称"), new MethodBool("enabled", "开启Profiler采样"),
            new MethodStr("event", "UI输入事件").SetEnumValues("click", "down", "up", "submit", "select"),
            new MethodVector("position", "屏幕位置xy"), new MethodFloat("tolerance", "浮点比较容差，默认0.001") };

        [ToolAction("profiler", "enabled")]
        private object Profile(JsonClass a) => ExtensionSafety.Run(() => {
            if (a.ContainsKey("enabled")) Profiler.enabled = ExtensionSafety.Flag(a, "enabled");
            return ExtensionSafety.Ok(new { Profiler.enabled, supported = Profiler.supported, allocated = Profiler.GetTotalAllocatedMemoryLong(), reserved = Profiler.GetTotalReservedMemoryLong(), unused_reserved = Profiler.GetTotalUnusedReservedMemoryLong(), mono_used = Profiler.GetMonoUsedSizeLong(), mono_heap = Profiler.GetMonoHeapSizeLong(), frame = Time.frameCount, undo_supported = false });
        });

        [ToolAction("memory", "instance_id=require")]
        private object Memory(JsonClass a) => ExtensionSafety.Run(() => {
            var go = ExtensionSafety.Target(a); var components = go.GetComponentsInChildren<Component>(true).Where(c => c).ToArray();
            return ExtensionSafety.Ok(new { root = go.GetInstanceID(), components = components.Select(c => new { id = c.GetInstanceID(), type = c.GetType().FullName, bytes = Profiler.GetRuntimeMemorySizeLong(c) }).ToArray(), managed_gc_bytes = GC.GetTotalMemory(false) });
        });

        [ToolAction("snapshot", "instance_id=require", "name=require")]
        private object Snapshot(JsonClass a) => ExtensionSafety.Run(() => {
            string name = ExtensionSafety.Text(a, "name"); var state = Capture(ExtensionSafety.Target(a));
            if (!Snapshots.ContainsKey(name) && Snapshots.Count >= 32) throw new InvalidOperationException("快照上限32；请先删除快照");
            Snapshots[name] = state.ToString(); return ExtensionSafety.Ok(new { name, state, persistence = "当前Editor域，重载后清空" });
        });

        [ToolAction("snapshot_compare", "instance_id=require", "name=require", "tolerance")]
        private object Compare(JsonClass a) => ExtensionSafety.Run(() => {
            string name = ExtensionSafety.Text(a, "name"), saved;
            if (!Snapshots.TryGetValue(name, out saved)) throw new ArgumentException("快照不存在");
            float tolerance = ExtensionSafety.Number(a, "tolerance", 0.001f, 0, 1e5f);
            var before = Json.Parse(saved) as JsonArray; var after = Capture(ExtensionSafety.Target(a)); var changes = new List<object>();
            var old = new Dictionary<int, JsonNode>(); for (int i = 0; i < before.Count; i++) old[before[i]["id"].AsInt] = before[i];
            for (int i = 0; i < after.Count; i++) {
                var row = after[i]; int id = row["id"].AsInt; JsonNode b;
                if (!old.TryGetValue(id, out b)) { changes.Add(new { id, change = "added" }); continue; }
                old.Remove(id);
                bool different = b["name"].Value != row["name"].Value || b["active"].Value != row["active"].Value;
                foreach (string key in new[] { "position", "rotation", "scale" }) for (int j = 0; j < 3; j++) different |= Mathf.Abs(b[key][j].AsFloat - row[key][j].AsFloat) > tolerance;
                if (different) changes.Add(new { id, change = "modified", before = b, after = row });
            }
            foreach (var item in old) changes.Add(new { id = item.Key, change = "removed" });
            return ExtensionSafety.Ok(new { passed = changes.Count == 0, tolerance, changes });
        });

        [ToolAction("snapshot_remove", "name=require")]
        private object Remove(JsonClass a) => ExtensionSafety.Run(() => ExtensionSafety.Ok(new { removed = Snapshots.Remove(ExtensionSafety.Text(a, "name")) }));

        [ToolAction("input_ui", "instance_id=require", "event=require", "position")]
        private object Input(JsonClass a) => ExtensionSafety.Run(() => {
            if (!Application.isPlaying || !EventSystem.current) throw new InvalidOperationException("需要运行模式和活动EventSystem；不模拟操作系统输入");
            var go = ExtensionSafety.Target(a); string kind = ExtensionSafety.Text(a, "event");
            var p = a.ContainsKey("position") ? ExtensionSafety.Vector(a, "position") : Vector3.zero;
            bool handled;
            var pointer = new PointerEventData(EventSystem.current) { position = p, button = PointerEventData.InputButton.Left };
            switch (kind) {
                case "click": handled = ExecuteEvents.Execute(go, pointer, ExecuteEvents.pointerClickHandler); break;
                case "down": handled = ExecuteEvents.Execute(go, pointer, ExecuteEvents.pointerDownHandler); break;
                case "up": handled = ExecuteEvents.Execute(go, pointer, ExecuteEvents.pointerUpHandler); break;
                case "submit": handled = ExecuteEvents.Execute(go, new BaseEventData(EventSystem.current), ExecuteEvents.submitHandler); break;
                case "select": EventSystem.current.SetSelectedGameObject(go); handled = EventSystem.current.currentSelectedGameObject == go; break;
                default: throw new ArgumentException("未知输入事件");
            }
            return ExtensionSafety.Ok(new { handled, undo_supported = false });
        });

        private static JsonArray Capture(GameObject root)
        {
            var transforms = root.GetComponentsInChildren<Transform>(true);
            if (transforms.Length > 10000) throw new ArgumentException("快照最多10000个对象");
            var array = new JsonArray();
            foreach (var t in transforms) {
                var row = new JsonClass(); row.Add("id", new JsonData(t.gameObject.GetInstanceID())); row.Add("name", new JsonData(t.name)); row.Add("active", new JsonData(t.gameObject.activeSelf));
                row.Add("position", Vector(t.localPosition)); row.Add("rotation", Vector(t.localEulerAngles)); row.Add("scale", Vector(t.localScale)); array.Add(row);
            }
            return array;
        }
        private static JsonArray Vector(Vector3 v) { var a = new JsonArray(); a.Add(new JsonData(v.x)); a.Add(new JsonData(v.y)); a.Add(new JsonData(v.z)); return a; }
    }
}
