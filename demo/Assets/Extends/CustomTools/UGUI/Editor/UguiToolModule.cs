using System;
using System.Collections.Generic;
using UnityEngine;
using UniMcp;
using UniMcp.Ugui;
using UniMcp.Models;

namespace UniMcp.Tools
{
    // ============================================================================
    // UGUI 工具模块（扩展 ugui 工具集，对齐 figma 的 [ToolModule] 范式）
    // 提供：list_templates（列出模板）/ generate（一键生成整屏）/ create_component（单控件）。
    // 底层生成逻辑全部在 UguiGenerator（纯 Unity UGUI），坐标/配色由 UguiTheme 设计令牌驱动。
    // ============================================================================
    [ToolModule("ugui", GroupEn = "UI", GroupZh = "界面",
        Tags = new[] { "ui-authoring", "ui-layout", "code-generation", "templates" })]
    public class UguiToolModule : ToolModuleBase
    {
        public override string Description => L.T(
            "UGUI template generator: one-click templated UI (dashboard/login/settings/cardGrid/statPanel) and single-component creation, driven by design tokens.",
            "UGUI 模板生成器：一键模板化界面（dashboard/login/settings/cardGrid/statPanel）与单控件创建，由设计令牌驱动。");

        protected override MethodKey[] CreateKeys()
        {
            return new MethodKey[]
            {
                new MethodStr("action", L.T("Action type", "操作类型"), false)
                    .SetEnumValues("list_templates", "generate", "create_component"),
                new MethodStr("template", L.T("Template name", "模板名"))
                    .AddExample("dashboard").AddExample("login").AddExample("settings").AddExample("cardGrid").AddExample("statPanel"),
                new MethodStr("component", L.T("Component type", "组件类型"))
                    .SetEnumValues("panel","image","text","button","kpi","barchart","ring","alert","progress","toggle","slider","input","card"),
                new MethodStr("parent", L.T("Parent object name (must be created in same session, or leave empty for root)", "父对象名（同一会话内已创建的，留空则用根）")),
                new MethodStr("name", L.T("New object name", "新对象名")),
                new MethodInt("x", L.T("X (top-left, canvas coords)", "X（画布左上坐标）")),
                new MethodInt("y", L.T("Y (top-left, canvas coords)", "Y（画布左上坐标）")),
                new MethodInt("w", L.T("Width", "宽")),
                new MethodInt("h", L.T("Height", "高")),
                new MethodStr("label", L.T("Label / text / title for the component", "控件文本/标题")),
                new MethodFloat("value", L.T("Numeric value (percent / fill / slider 0-1)", "数值（百分比/填充/滑块 0-1）")),
                new MethodStr("root", L.T("Root canvas name override", "根画布名覆盖")),
            };
        }

        [ToolAction("list_templates")]
        private object ListTemplates(ToolContext ctx)
        {
            var list = new List<string> { "dashboard", "login", "settings", "cardGrid", "statPanel" };
            return Response.Success(L.T("UGUI templates", "UGUI 模板列表"), list);
        }

        [ToolAction("generate", "template=require")]
        private object Generate(ToolContext ctx)
        {
            string tpl = ctx["template"]?.ToString();
            string root = ctx["root"]?.ToString();
            if (string.IsNullOrEmpty(tpl))
                return Response.Error(L.T("template is required", "template 为必填"));
            try
            {
                var go = UguiGenerator.Build(tpl, string.IsNullOrEmpty(root) ? null : root);
                if (go == null) return Response.Error(L.T("Unknown template: ", "未知模板: ") + tpl);
                return Response.Success(L.T("Generated UGUI template ", "已生成 UGUI 模板 ") + tpl,
                    new Dictionary<string, object> { { "root", go.name }, { "template", tpl } });
            }
            catch (Exception e)
            {
                return Response.Error(L.T("Generate failed: ", "生成失败: ") + e.Message);
            }
        }

        [ToolAction("create_component", "component=require", "name=require")]
        private object CreateComponent(ToolContext ctx)
        {
            string comp = ctx["component"]?.ToString();
            string name = ctx["name"]?.ToString();
            string parent = ctx["parent"]?.ToString();
            string label = ctx["label"]?.ToString() ?? name;
            int x = ctx["x"] != null ? Convert.ToInt32(ctx["x"]) : 100;
            int y = ctx["y"] != null ? Convert.ToInt32(ctx["y"]) : 100;
            int w = ctx["w"] != null ? Convert.ToInt32(ctx["w"]) : 200;
            int h = ctx["h"] != null ? Convert.ToInt32(ctx["h"]) : 60;
            float value = ctx["value"] != null ? Convert.ToSingle(ctx["value"]) : 0f;

            try
            {
                string rootKey = UguiGenerator.EnsureRoot();
                string p = string.IsNullOrEmpty(parent) ? rootKey : parent;
                var go = UguiGenerator.AddControl(comp, p, name, new Rect(x, y, w, h), label, value);
                if (go == null) return Response.Error(L.T("Failed to create component: ", "控件创建失败: ") + comp);
                return Response.Success(L.T("Created component ", "已创建控件 ") + comp,
                    new Dictionary<string, object> { { "name", go.name }, { "parent", p } });
            }
            catch (Exception e)
            {
                return Response.Error(L.T("Create component failed: ", "控件创建失败: ") + e.Message);
            }
        }
    }
}
