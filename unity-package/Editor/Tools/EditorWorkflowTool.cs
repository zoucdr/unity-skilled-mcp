using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UniMcp.Models;

namespace UniMcp.Tools
{
    [ToolModule("editor", GroupEn = "Editor", GroupZh = "编辑器",
        Tags = new[] { "editor-automation", "selection", "window-management", "inspector" })]
    public sealed class EditorWorkflowTool : ToolModuleBase, IStrictToolModule
    {
        public override string Description => "Selection、Inspector、窗口布局快照、辅助标记与资源预览";
        protected override MethodKey[] CreateKeys() => new MethodKey[] {
            new MethodInt("instance_id", "对象或资源ID"), new MethodStr("asset_path", "Assets下资源路径"),
            new MethodBool("active", "启用标记"), new MethodStr("window", "窗口类型").SetEnumValues("SceneView", "GameView", "InspectorWindow", "ProjectBrowser", "ConsoleWindow"),
            new MethodVector("position", "窗口左上角xy"), new MethodVector("size", "窗口宽高xy") };

        [ToolAction("selection_get")]
        private object SelectionGet(JsonClass a) => ExtensionSafety.Run(() => ExtensionSafety.Ok(Selection.objects.Select(x => new { id = x.GetInstanceID(), x.name, type = x.GetType().FullName }).ToArray()));

        [ToolAction("selection_set", "instance_id=require")]
        private object SelectionSet(JsonClass a) => ExtensionSafety.Run(() => { var obj = Find(a); Selection.activeObject = obj; EditorGUIUtility.PingObject(obj); return ExtensionSafety.Ok(obj.GetInstanceID()); });

        [ToolAction("inspector", "instance_id=require")]
        private object Inspector(JsonClass a) => ExtensionSafety.Run(() => {
            var obj = Find(a); var list = new System.Collections.Generic.List<object>();
            using (var so = new SerializedObject(obj)) {
                var p = so.GetIterator();
                while (p.NextVisible(true)) {
                    if (list.Count >= 1000) return ExtensionSafety.Ok(new { properties = list, truncated = true });
                    string value = null;
                    switch (p.propertyType) {
                        case SerializedPropertyType.Integer: value = p.longValue.ToString(); break;
                        case SerializedPropertyType.Float: value = p.doubleValue.ToString(System.Globalization.CultureInfo.InvariantCulture); break;
                        case SerializedPropertyType.Boolean: value = p.boolValue.ToString(); break;
                        case SerializedPropertyType.String: value = p.stringValue; break;
                        case SerializedPropertyType.ObjectReference: value = p.objectReferenceValue ? p.objectReferenceValue.GetInstanceID().ToString() : "null"; break;
                        case SerializedPropertyType.Enum: value = p.enumValueIndex >= 0 && p.enumValueIndex < p.enumNames.Length ? p.enumNames[p.enumValueIndex] : p.intValue.ToString(); break;
                    }
                    list.Add(new { path = p.propertyPath, type = p.propertyType.ToString(), value, p.editable });
                }
            }
            return ExtensionSafety.Ok(new { properties = list, truncated = false });
        });

        [ToolAction("layout_snapshot")]
        private object Layout(JsonClass a) => ExtensionSafety.Run(() => ExtensionSafety.Ok(Resources.FindObjectsOfTypeAll<EditorWindow>().Select(w => new { id = w.GetInstanceID(), type = w.GetType().FullName, title = w.titleContent.text, rect = new[] { w.position.x, w.position.y, w.position.width, w.position.height }, w.docked }).ToArray()));

        [ToolAction("window_show", "window=require", "position", "size")]
        private object Window(JsonClass a) => ExtensionSafety.Run(() => {
            string name = ExtensionSafety.Text(a, "window");
            if (!new[] { "SceneView", "GameView", "InspectorWindow", "ProjectBrowser", "ConsoleWindow" }.Contains(name)) throw new ArgumentException("窗口不在允许列表");
            var type = typeof(Editor).Assembly.GetType("UnityEditor." + name); if (type == null) throw new NotSupportedException("窗口类型在此Unity版本不可用");
            Vector3? p = a.ContainsKey("position") ? ExtensionSafety.Vector(a, "position") : (Vector3?)null;
            Vector3? size = a.ContainsKey("size") ? ExtensionSafety.Vector(a, "size") : (Vector3?)null;
            if (size.HasValue && (size.Value.x < 100 || size.Value.y < 100)) throw new ArgumentException("窗口宽高至少100");
            var w = EditorWindow.GetWindow(type); var rect = w.position;
            if (p.HasValue || size.HasValue) {
                if (w.docked) throw new InvalidOperationException("拒绝调整已停靠窗口；请先浮动窗口");
                if (p.HasValue) { rect.x = p.Value.x; rect.y = p.Value.y; }
                if (size.HasValue) { rect.width = size.Value.x; rect.height = size.Value.y; }
                w.position = rect;
            }
            w.Show(); w.Focus(); return ExtensionSafety.Ok(new { id = w.GetInstanceID(), undo_supported = false });
        });

        [ToolAction("wireframe", "instance_id=require", "active=require")]
        private object Wire(JsonClass a) => ExtensionSafety.Run(() => {
            var r = ExtensionSafety.Component<Renderer>(a); bool active = ExtensionSafety.Flag(a, "active");
            EditorUtility.SetSelectedRenderState(r, active ? EditorSelectedRenderState.Wireframe | EditorSelectedRenderState.Highlight : EditorSelectedRenderState.Hidden);
            SceneView.RepaintAll(); return ExtensionSafety.Ok(new { active, transient = true, undo_supported = false });
        });

        [ToolAction("preview", "asset_path=require")]
        private object Preview(JsonClass a) => ExtensionSafety.Run(() => {
            var obj = ExtensionSafety.Asset<UnityEngine.Object>(a); var texture = AssetPreview.GetAssetPreview(obj);
            if (!texture) return ExtensionSafety.Ok(new { pending = AssetPreview.IsLoadingAssetPreview(obj.GetInstanceID()), available = false });
            return ExtensionSafety.Ok(new { available = true, texture.width, texture.height, png_base64 = Convert.ToBase64String(texture.EncodeToPNG()) });
        });
        private static UnityEngine.Object Find(JsonClass a) { var obj = EditorUtility.InstanceIDToObject(ExtensionSafety.Integer(a, "instance_id")); if (!obj) throw new ArgumentException("对象不存在"); return obj; }
    }
}
