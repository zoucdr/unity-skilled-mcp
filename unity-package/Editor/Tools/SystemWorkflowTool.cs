using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UniMcp.Models;

namespace UniMcp.Tools
{
    [ToolModule("system", GroupEn = "System", GroupZh = "系统工具")]
    public sealed class SystemWorkflowTool : ToolModuleBase, IStrictToolModule
    {
        private sealed class BatchTask
        {
            public string id, status, error, created, completed;
            public JsonClass[] operations;
            public JsonClass[] before;
            public bool active;
        }
        private static readonly Dictionary<string, BatchTask> Tasks = new Dictionary<string, BatchTask>();
        private static readonly Queue<object> Audit = new Queue<object>();
        public override string Description => "任务查询/取消与仅限批量启停的预演、审计、排队执行；不代理任意工具";
        protected override MethodKey[] CreateKeys() => new MethodKey[] {
            new MethodStr("task_id", "排队任务ID"), new MethodArr("instance_ids", "对象ID列表，1至256项", true, "integer"),
            new MethodBool("active", "目标启用状态"), new MethodBool("dry_run", "只预演，默认true"),
            new MethodStr("asset_path", "Assets下JSON存档路径"), new MethodObj("data", "通用JSON对象，不推断业务模型"),
            new MethodArr("required_keys", "必需的顶层字段名"), new MethodBool("overwrite", "显式允许覆盖，默认false") };

        [ToolAction("tasks", "task_id")]
        private object Query(JsonClass a) => ExtensionSafety.Run(() => {
            if (a.ContainsKey("task_id")) {
                string id = ExtensionSafety.Text(a, "task_id");
                BatchTask batch;
                if (Tasks.TryGetValue(id, out batch)) return ExtensionSafety.Ok(State(batch));
                // 工具调用的 task_id（request_id）：结论已落盘，Unity 重启后仍可读。
                var persisted = ToolTaskStore.Instance.Query(id);
                if (persisted != null) return ExtensionSafety.Ok(persisted);
                throw new ArgumentException("未找到该 task_id：既不在本域批任务中，也不在落盘任务库中");
            }
            ToolTaskStore.Instance.EnsureOrphansMarked();
            return ExtensionSafety.Ok(new {
                batches = Tasks.Values.Select(State).ToArray(),
                tool_calls = ToolTaskStore.Instance.All().Select(e => new {
                    task_id = e.id, tool = e.tool, status = e.status, error = e.error,
                    started_at = e.startedAt, completed_at = e.completedAt, cancellable = false
                }).ToArray()
            });
        });

        [ToolAction("cancel", "task_id=require")]
        private object Cancel(JsonClass a) => ExtensionSafety.Run(() => {
            var t = Find(ExtensionSafety.Text(a, "task_id"));
            if (t.status != "queued") throw new InvalidOperationException("只能取消尚未执行的排队任务；同步执行中的Unity操作不能强行终止");
            t.status = "cancelled"; t.completed = DateTime.UtcNow.ToString("o"); AddAudit(t.id, "cancelled", null); return ExtensionSafety.Ok(State(t));
        });

        [ToolAction("batch_active", "instance_ids=require", "active=require", "dry_run")]
        private object Batch(JsonClass a) => ExtensionSafety.Run(() => {
            var ids = a["instance_ids"] as JsonArray;
            if (ids == null || ids.Count == 0 || ids.Count > 256) throw new ArgumentException("instance_ids长度应为1至256");
            bool active = ExtensionSafety.Flag(a, "active"), dry = ExtensionSafety.Flag(a, "dry_run", true);
            var operations = new List<JsonClass>(); var before = new List<JsonClass>(); var seen = new HashSet<int>();
            for (int i = 0; i < ids.Count; i++) {
                var arg = new JsonClass(); arg.Add("instance_id", ids[i]); var go = ExtensionSafety.Target(arg);
                if (!seen.Add(go.GetInstanceID())) throw new ArgumentException("instance_ids不能重复");
                operations.Add(arg); var b = new JsonClass(); b.Add("instance_id", new JsonData(go.GetInstanceID())); b.Add("active", new JsonData(go.activeSelf)); before.Add(b);
            }
            if (dry) { AddAudit(null, "preview", new { before, active }); return ExtensionSafety.Ok(new { dry_run = true, before, active, operations = "SetActive，仅场景对象，无任意工具执行" }); }
            if (Application.isPlaying) throw new InvalidOperationException("批处理写入仅限编辑模式，保证Undo回滚");
            if (Tasks.Count >= 100) {
                var removable = Tasks.Values.Where(t => t.status != "queued" && t.status != "running").OrderBy(t => t.created).FirstOrDefault();
                if (removable == null) throw new InvalidOperationException("排队任务数已达上限100"); Tasks.Remove(removable.id);
            }
            var task = new BatchTask { id = Guid.NewGuid().ToString("N"), status = "queued", created = DateTime.UtcNow.ToString("o"), operations = operations.ToArray(), before = before.ToArray(), active = active };
            Tasks.Add(task.id, task); AddAudit(task.id, "queued", new { before, active });
            EditorApplication.delayCall += () => Execute(task);
            return ExtensionSafety.Ok(State(task));
        });

        [ToolAction("audit")]
        private object AuditLog(JsonClass a) => ExtensionSafety.Run(() => ExtensionSafety.Ok(new { entries = Audit.ToArray(), capacity = 200, persistence = "当前Editor域；重载清空，不记录存档内容" }));

        [ToolAction("data_validate", "data=require", "required_keys")]
        private object Validate(JsonClass a) => ExtensionSafety.Run(() => {
            var data = Data(a); string[] required = a.ContainsKey("required_keys") ? ExtensionSafety.Strings(a, "required_keys") : new string[0];
            var missing = required.Where(k => !data.ContainsKey(k)).ToArray();
            return ExtensionSafety.Ok(new { valid = missing.Length == 0, missing, bytes = System.Text.Encoding.UTF8.GetByteCount(data.ToString()), validation = "JSON对象大小与必填顶层字段；不验证未声明业务规则" });
        });

        [ToolAction("save_write", "asset_path=require", "data=require", "overwrite")]
        private object Save(JsonClass a) => ExtensionSafety.Run(() => {
            var data = Data(a); string path = ExtensionSafety.AssetPath(ExtensionSafety.Text(a, "asset_path"), ".json", false); bool overwrite = ExtensionSafety.Flag(a, "overwrite");
            if (File.Exists(path) && !overwrite) throw new ArgumentException("文件已存在；必须显式overwrite=true");
            string temporary = path + ".mcp-" + Guid.NewGuid().ToString("N") + ".tmp";
            try {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false))) writer.Write(data.ToString());
                if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
            } finally { if (File.Exists(temporary)) File.Delete(temporary); }
            AssetDatabase.ImportAsset(path); AddAudit(null, "save_write", new { path, overwrite });
            return ExtensionSafety.Ok(new { path, undo_supported = false });
        });

        [ToolAction("save_read", "asset_path=require")]
        private object Load(JsonClass a) => ExtensionSafety.Run(() => {
            string path = ExtensionSafety.AssetPath(ExtensionSafety.Text(a, "asset_path"), ".json");
            if (new FileInfo(path).Length > 1024 * 1024) throw new ArgumentException("存档超过1MiB限制");
            var data = Json.Parse(File.ReadAllText(path)) as JsonClass; if (data == null) throw new ArgumentException("存档必须是JSON对象");
            return ExtensionSafety.Ok(new { path, data });
        });

        private static JsonClass Data(JsonClass a) {
            var data = a["data"] as JsonClass; if (data == null) throw new ArgumentException("data必须是JSON对象");
            if (System.Text.Encoding.UTF8.GetByteCount(data.ToString()) > 1024 * 1024) throw new ArgumentException("data超过1MiB限制"); return data;
        }
        private static void Execute(BatchTask task)
        {
            if (task.status != "queued") return;
            int group = -1;
            try {
                if (Application.isPlaying) throw new InvalidOperationException("执行时已进入运行模式，任务中止");
                var targets = task.operations.Select(a => ExtensionSafety.Target(a)).ToArray();
                for (int i = 0; i < targets.Length; i++) if (targets[i].activeSelf != task.before[i]["active"].AsBool) throw new InvalidOperationException("目标状态已改变，拒绝执行过期计划");
                task.status = "running";
                Undo.IncrementCurrentGroup(); group = Undo.GetCurrentGroup(); Undo.SetCurrentGroupName("MCP 安全批量启停"); Undo.RecordObjects(targets, "MCP 安全批量启停");
                foreach (var target in targets) { target.SetActive(task.active); ExtensionSafety.Dirty(target); }
                Undo.CollapseUndoOperations(group); task.status = "completed";
            } catch (Exception e) {
                if (group >= 0) Undo.RevertAllDownToGroup(group);
                task.status = "failed"; task.error = e.Message;
            }
            task.completed = DateTime.UtcNow.ToString("o"); AddAudit(task.id, task.status, task.error);
        }
        private static BatchTask Find(string id) { BatchTask t; if (!Tasks.TryGetValue(id, out t)) throw new ArgumentException("当前域没有此批任务；工具调用任务只能查询，不能取消"); return t; }
        private static object State(BatchTask t) => new { t.id, t.status, t.error, t.created, t.completed, count = t.operations.Length, cancellable = t.status == "queued" };
        private static void AddAudit(string id, string operation, object detail) { while (Audit.Count >= 200) Audit.Dequeue(); Audit.Enqueue(new { time = DateTime.UtcNow.ToString("o"), task_id = id, operation, detail }); }
    }
}
