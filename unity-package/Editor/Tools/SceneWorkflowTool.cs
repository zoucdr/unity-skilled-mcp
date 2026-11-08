using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UniMcp.Models;

namespace UniMcp.Tools
{
    [ToolModule("scene", GroupEn = "Scene", GroupZh = "场景层级",
        Tags = new[] { "scene-management", "hierarchy", "transform", "build-settings" })]
    public sealed class SceneWorkflowTool : ToolModuleBase, IStrictToolModule
    {
        public override string Description => "Transform、批量对象、多场景与缺失引用检查";
        protected override MethodKey[] CreateKeys() => new MethodKey[] {
            new MethodInt("instance_id", "场景对象ID"), new MethodArr("instance_ids", "场景对象ID数组", true, "integer"),
            new MethodVector("position", "位置"), new MethodVector("rotation", "欧拉角"), new MethodVector("scale", "缩放"),
            new MethodBool("local", "使用局部坐标，默认true"), new MethodBool("active", "启用状态"),
            new MethodStr("scene_path", "Assets下已有场景路径"), new MethodInt("scene_handle", "已加载场景句柄"),
            new MethodBool("dry_run", "只预演，默认true"), new MethodBool("save_dirty", "关闭前保存脏场景，默认false") };

        [ToolAction("transform_get", "instance_id=require")]
        private object Get(JsonClass a) => ExtensionSafety.Run(() => {
            var t = ExtensionSafety.Target(a).transform;
            return ExtensionSafety.Ok(new { position = V(t.position), rotation = V(t.eulerAngles), localPosition = V(t.localPosition), localRotation = V(t.localEulerAngles), localScale = V(t.localScale) });
        });

        [ToolAction("transform_set", "instance_id=require", "position", "rotation", "scale", "local")]
        private object Set(JsonClass a) => ExtensionSafety.Run(() => {
            var t = ExtensionSafety.Target(a).transform;
            bool local = ExtensionSafety.Flag(a, "local", true);
            Vector3? p = a.ContainsKey("position") ? ExtensionSafety.Vector(a, "position") : (Vector3?)null;
            Vector3? r = a.ContainsKey("rotation") ? ExtensionSafety.Vector(a, "rotation") : (Vector3?)null;
            Vector3? s = a.ContainsKey("scale") ? ExtensionSafety.Vector(a, "scale") : (Vector3?)null;
            if (!p.HasValue && !r.HasValue && !s.HasValue) throw new ArgumentException("至少提供一个变换属性");
            Undo.RecordObject(t, "MCP 修改变换");
            if (p.HasValue) { if (local) t.localPosition = p.Value; else t.position = p.Value; }
            if (r.HasValue) { if (local) t.localEulerAngles = r.Value; else t.eulerAngles = r.Value; }
            if (s.HasValue) t.localScale = s.Value;
            ExtensionSafety.Dirty(t); return Get(a);
        });

        [ToolAction("batch_active", "instance_ids=require", "active=require", "dry_run")]
        private object Active(JsonClass a) => ExtensionSafety.Run(() => {
            var ids = a["instance_ids"] as JsonArray;
            if (ids == null || ids.Count == 0 || ids.Count > 256) throw new ArgumentException("instance_ids长度应为1至256");
            var objects = new List<GameObject>();
            for (int i = 0; i < ids.Count; i++) { var o = new JsonClass(); o.Add("instance_id", ids[i]); objects.Add(ExtensionSafety.Target(o)); }
            bool active = ExtensionSafety.Flag(a, "active"), dry = ExtensionSafety.Flag(a, "dry_run", true);
            if (!dry) {
                Undo.RecordObjects(objects.Distinct().ToArray(), "MCP 批量启停");
                foreach (var go in objects.Distinct()) { go.SetActive(active); ExtensionSafety.Dirty(go); }
            }
            return ExtensionSafety.Ok(new { dry_run = dry, active, objects = objects.Select(x => x.GetInstanceID()).ToArray() });
        });

        [ToolAction("scenes")]
        private object Scenes(JsonClass a) => ExtensionSafety.Run(() => {
            var scenes = new List<object>();
            for (int i = 0; i < SceneManager.sceneCount; i++) { var s = SceneManager.GetSceneAt(i); scenes.Add(new { s.handle, s.path, s.isLoaded, s.isDirty, active = s == SceneManager.GetActiveScene() }); }
            return ExtensionSafety.Ok(scenes);
        });

        [ToolAction("open_additive", "scene_path=require")]
        private object Open(JsonClass a) => ExtensionSafety.Run(() => {
            if (Application.isPlaying) throw new InvalidOperationException("此操作仅限编辑模式");
            var path = ExtensionSafety.AssetPath(ExtensionSafety.Text(a, "scene_path"), ".unity");
            var scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
            return ExtensionSafety.Ok(new { scene.handle, scene.path, undo_supported = false });
        });

        [ToolAction("set_active_scene", "scene_handle=require")]
        private object Activate(JsonClass a) => ExtensionSafety.Run(() => {
            var scene = Loaded(a); if (!SceneManager.SetActiveScene(scene)) throw new InvalidOperationException("激活场景失败");
            return ExtensionSafety.Ok(new { scene.handle });
        });

        [ToolAction("move_to_scene", "instance_id=require", "scene_handle=require")]
        private object Move(JsonClass a) => ExtensionSafety.Run(() => {
            var go = ExtensionSafety.Target(a); var scene = Loaded(a);
            if (go.transform.parent) throw new ArgumentException("只允许移动根对象");
            Undo.MoveGameObjectToScene(go, scene, "MCP 跨场景移动"); ExtensionSafety.Dirty(go); return ExtensionSafety.Ok(go.GetInstanceID());
        });

        [ToolAction("close_scene", "scene_handle=require", "save_dirty")]
        private object Close(JsonClass a) => ExtensionSafety.Run(() => {
            if (Application.isPlaying) throw new InvalidOperationException("此操作仅限编辑模式");
            var s = Loaded(a); bool save = ExtensionSafety.Flag(a, "save_dirty");
            if (s.isDirty && !save) throw new InvalidOperationException("场景有未保存改动；拒绝丢弃");
            if (s.isDirty) { ExtensionSafety.AssetPath(s.path, ".unity"); if (!EditorSceneManager.SaveScene(s)) throw new InvalidOperationException("保存失败，未关闭"); }
            if (!EditorSceneManager.CloseScene(s, true)) throw new InvalidOperationException("关闭失败");
            return ExtensionSafety.Ok(new { closed = true, undo_supported = false });
        });

        [ToolAction("missing_references", "instance_id=require")]
        private object Missing(JsonClass a) => ExtensionSafety.Run(() => ExtensionSafety.Ok(FindMissing(ExtensionSafety.Target(a)))) ;

        internal static List<object> FindMissing(GameObject root)
        {
            var issues = new List<object>();
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            foreach (var c in t.GetComponents<Component>()) {
                if (!c) { issues.Add(new { instance_id = t.gameObject.GetInstanceID(), issue = "missing_script" }); continue; }
                using (var so = new SerializedObject(c)) {
                    var p = so.GetIterator();
                    while (p.Next(true)) if (p.propertyType == SerializedPropertyType.ObjectReference && p.objectReferenceValue == null && p.objectReferenceInstanceIDValue != 0)
                        issues.Add(new { instance_id = c.GetInstanceID(), property = p.propertyPath, issue = "missing_reference" });
                }
            }
            return issues;
        }
        private static Scene Loaded(JsonClass a) {
            int handle = ExtensionSafety.Integer(a, "scene_handle");
            for (int i = 0; i < SceneManager.sceneCount; i++) { var s = SceneManager.GetSceneAt(i); if (s.handle == handle && s.isLoaded) return s; }
            throw new ArgumentException("场景未加载");
        }
        internal static float[] V(Vector3 v) => new[] { v.x, v.y, v.z };
    }
}
