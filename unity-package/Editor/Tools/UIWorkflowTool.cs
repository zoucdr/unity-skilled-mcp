using System;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Events;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using UniMcp.Models;

namespace UniMcp.Tools
{
    [ToolModule("ui", GroupEn = "UI", GroupZh = "界面",
        Tags = new[] { "ui-authoring", "ui-toolkit", "uxml", "event-binding" })]
    public sealed class UIWorkflowTool : ToolModuleBase, IStrictToolModule
    {
        public override string Description => "UI控件创建、无参事件绑定、字体与Canvas适配检查";
        protected override MethodKey[] CreateKeys() => new MethodKey[] {
            new MethodInt("instance_id", "UI对象ID"), new MethodInt("parent_id", "Canvas下父对象ID"), new MethodInt("component_id", "接收事件的MonoBehaviour组件ID"),
            new MethodStr("control", "控件类型").SetEnumValues("panel", "image", "text", "button", "toggle", "slider", "inputfield", "scrollview"),
            new MethodStr("name", "对象名称"), new MethodStr("event", "事件名").SetEnumValues("click", "value_changed", "end_edit"),
            new MethodStr("method", "public void无参方法名"), new MethodStr("font_path", "字体资源路径"),
            new MethodVector("size", "宽高，第三项忽略"), new MethodVector("position", "局部位置") };

        [ToolAction("control_create", "control=require", "parent_id=require", "name", "size", "position")]
        private object Create(JsonClass a) => ExtensionSafety.Run(() => {
            var parent = ExtensionSafety.Target(a, "parent_id").transform;
            if (!parent.GetComponentInParent<Canvas>()) throw new ArgumentException("父对象必须位于Canvas下");
            var size = a.ContainsKey("size") ? ExtensionSafety.Vector(a, "size") : new Vector3(160, 30, 0);
            var position = a.ContainsKey("position") ? ExtensionSafety.Vector(a, "position") : Vector3.zero;
            if (size.x <= 0 || size.y <= 0) throw new ArgumentException("宽高必须大于0");
            string name = ExtensionSafety.Text(a, "name", "UI"); string control = ExtensionSafety.Text(a, "control");
            var resources = new DefaultControls.Resources(); GameObject go;
            switch (control) {
                case "panel": go = DefaultControls.CreatePanel(resources); break;
                case "image": go = DefaultControls.CreateImage(resources); break;
                case "text": go = DefaultControls.CreateText(resources); break;
                case "button": go = DefaultControls.CreateButton(resources); break;
                case "toggle": go = DefaultControls.CreateToggle(resources); break;
                case "slider": go = DefaultControls.CreateSlider(resources); break;
                case "inputfield": go = DefaultControls.CreateInputField(resources); break;
                case "scrollview": go = DefaultControls.CreateScrollView(resources); break;
                default: throw new ArgumentException("未知control");
            }
            go.name = name; go.transform.SetParent(parent, false);
            var rect = go.GetComponent<RectTransform>(); rect.sizeDelta = size; rect.localPosition = position;
            Undo.RegisterCreatedObjectUndo(go, "MCP 创建UI控件"); ExtensionSafety.Dirty(go);
            return ExtensionSafety.Ok(new { instance_id = go.GetInstanceID() });
        });

        [ToolAction("bind_event", "instance_id=require", "component_id=require", "method=require", "event=require")]
        private object Bind(JsonClass a) => ExtensionSafety.Run(() => {
            var go = ExtensionSafety.Target(a); var receiver = EditorUtility.InstanceIDToObject(ExtensionSafety.Integer(a, "component_id")) as MonoBehaviour;
            if (!receiver || EditorUtility.IsPersistent(receiver) || !receiver.gameObject.scene.isLoaded) throw new ArgumentException("component_id必须指向已加载场景中的MonoBehaviour");
            if (receiver.gameObject.scene != go.scene) throw new ArgumentException("持久化监听不支持跨场景引用");
            string methodName = ExtensionSafety.Text(a, "method"); string eventName = ExtensionSafety.Text(a, "event");
            var method = receiver.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
            if (method == null || method.ReturnType != typeof(void) || method.IsGenericMethod) throw new ArgumentException("方法必须是public void无参方法");
            UnityEventBase ev = null; Component owner = null;
            if (eventName == "click") { var b = go.GetComponent<Button>(); if (b) { ev = b.onClick; owner = b; } }
            else if (eventName == "value_changed") {
                var toggle = go.GetComponent<Toggle>(); var slider = go.GetComponent<Slider>(); var input = go.GetComponent<InputField>();
                if (toggle) { ev = toggle.onValueChanged; owner = toggle; }
                else if (slider) { ev = slider.onValueChanged; owner = slider; }
                else if (input) { ev = input.onValueChanged; owner = input; }
            } else if (eventName == "end_edit") { var input = go.GetComponent<InputField>(); if (input) { ev = input.onEndEdit; owner = input; } }
            if (ev == null) throw new ArgumentException("控件不支持此事件");
            for (int i = 0; i < ev.GetPersistentEventCount(); i++) if (ev.GetPersistentTarget(i) == receiver && ev.GetPersistentMethodName(i) == methodName) throw new ArgumentException("监听已存在");
            var handler = (UnityAction)Delegate.CreateDelegate(typeof(UnityAction), receiver, method);
            Undo.RecordObject(owner, "MCP 绑定UI事件"); UnityEventTools.AddVoidPersistentListener(ev, handler); ExtensionSafety.Dirty(owner);
            return ExtensionSafety.Ok(new { count = ev.GetPersistentEventCount() });
        });

        [ToolAction("audit", "instance_id=require")]
        private object Audit(JsonClass a) => ExtensionSafety.Run(() => {
            var c = ExtensionSafety.Component<Canvas>(a); var scaler = c.GetComponent<CanvasScaler>();
            var bounds = new Vector3[4]; c.GetComponent<RectTransform>().GetWorldCorners(bounds);
            return ExtensionSafety.Ok(new { render_mode = c.renderMode.ToString(), raycaster_missing = !c.GetComponent<GraphicRaycaster>(),
                event_system_missing = !UnityEngine.Object.FindObjectOfType<UnityEngine.EventSystems.EventSystem>(),
                scaling = scaler ? scaler.uiScaleMode.ToString() : "none", reference_resolution = scaler ? new[] { scaler.referenceResolution.x, scaler.referenceResolution.y } : null,
                missing_fonts = c.GetComponentsInChildren<Text>(true).Where(t => !t.font).Select(t => t.GetInstanceID()).ToArray(),
                invalid_rects = c.GetComponentsInChildren<RectTransform>(true).Where(r => r.rect.width <= 0 || r.rect.height <= 0).Select(r => r.GetInstanceID()).ToArray(),
                camera_missing = c.renderMode != RenderMode.ScreenSpaceOverlay && !c.worldCamera });
        });

        [ToolAction("font_apply", "font_path=require", "instance_id=require")]
        private object ApplyFont(JsonClass a) => ExtensionSafety.Run(() => {
            var font = ExtensionSafety.Asset<Font>(a, "font_path"); var texts = ExtensionSafety.Target(a).GetComponentsInChildren<Text>(true);
            if (texts.Length == 0) throw new ArgumentException("没有可设置字体的Text");
            Undo.RecordObjects(texts, "MCP 应用字体"); foreach (var t in texts) { t.font = font; ExtensionSafety.Dirty(t); }
            return ExtensionSafety.Ok(new { count = texts.Length, font = font.name });
        });
    }
}
