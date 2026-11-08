using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using UnityEditor;
using UnityEngine;
using UniMcp.Models;
using UniMcp.Utils;

namespace UniMcp.Tools
{
    /// <summary>
    /// UI Toolkit（UIElements）编辑：UXML/USS 资源、UIDocument 挂接、PanelSettings 配置与界面结构检查。
    /// UXML/USS 以文本方式读写，避免依赖特定 Unity 版本的序列化 API；
    /// UIDocument / PanelSettings 通过反射访问，未安装相关模块时给出明确提示。
    /// 对应方法名: ui_toolkit
    /// </summary>
    [ToolModule("ui", GroupEn = "UI", GroupZh = "界面",
        Tags = new[] { "ui-authoring", "ui-toolkit", "uxml", "event-binding" })]
    public class UiToolkitTool : ToolModuleBase
    {
        private const string UxmlNamespace = "UnityEngine.UIElements";
        private const string EditorNamespace = "UnityEditor.UIElements";
        private const string DocumentType = "UnityEngine.UIElements.UIDocument";
        private const string PanelSettingsType = "UnityEngine.UIElements.PanelSettings";

        public override string Description => L.T(
            "Create and edit UI Toolkit assets (UXML/USS), attach UIDocument, configure PanelSettings and inspect the visual tree",
            "创建与编辑 UI Toolkit 资源（UXML/USS），挂接 UIDocument，配置 PanelSettings 并检查可视化树");

        protected override MethodKey[] CreateKeys()
        {
            return new MethodKey[]
            {
                new MethodStr("action", L.T("Operation type", "操作类型"), false)
                    .SetEnumValues(
                        "create_uxml", "read_uxml", "write_uxml",
                        "add_element", "set_element", "remove_element", "list_elements",
                        "create_uss", "read_uss", "append_uss",
                        "create_panel_settings", "update_panel_settings",
                        "attach_document", "detach_document", "get_document",
                        "screenshot"),

                new MethodStr("path", L.T("UXML or USS asset path", "UXML 或 USS 资源路径"))
                    .AddExample("Assets/UI/Main.uxml"),

                new MethodStr("content", L.T("Full file content used by write_uxml or append_uss", "write_uxml 或 append_uss 使用的完整内容"), true),

                new MethodStr("name", L.T("Element name attribute; the target of element operations", "元素 name 属性；元素操作的目标"))
                    .AddExample("title"),

                new MethodStr("element_type", L.T("UI Toolkit element type used by add_element, for example VisualElement, Label, Button, TextField", "add_element 使用的元素类型，例如 VisualElement、Label、Button、TextField"))
                    .AddExamples("VisualElement", "Label", "Button")
                    .SetDefault("VisualElement"),

                new MethodStr("parent", L.T("Parent element name; the element is appended to it, otherwise to the root", "父元素名称；元素会追加到该元素下，否则追加到根节点"))
                    .AddExample("root"),

                new MethodStr("attribute", L.T("Attribute name to write, for example style, class, text or a custom attribute", "要写入的属性名，例如 style、class、text 或自定义属性"))
                    .AddExamples("style", "class", "text")
                    .SetDefault("style"),

                new MethodStr("value", L.T("Attribute value written by set_element", "set_element 写入的属性值"))
                    .AddExample("background-color: #1b1b1b; flex-grow: 1;"),

                new MethodBool("replace", L.T("Replace the whole attribute value instead of merging it", "替换整个属性值而不是合并"))
                    .SetDefault(false),

                new MethodBool("recursive", L.T("List descendants recursively", "递归列出后代"))
                    .SetDefault(true),

                new MethodStr("game_object", L.T("GameObject path or instance ID that hosts the UIDocument", "承载 UIDocument 的对象路径或 instanceID"))
                    .AddExample("Canvas/UI"),

                new MethodStr("uxml", L.T("UXML asset path assigned to the UIDocument", "赋给 UIDocument 的 UXML 资源路径"))
                    .AddExample("Assets/UI/Main.uxml"),

                new MethodStr("panel_settings", L.T("PanelSettings asset path", "PanelSettings 资源路径"))
                    .AddExample("Assets/UI/PanelSettings.asset"),

                new MethodStr("scale_mode", L.T("Panel scale mode: ConstantPixelSize, ScaleWithScreenSize, ConstantPhysicalSize", "面板缩放模式：ConstantPixelSize、ScaleWithScreenSize、ConstantPhysicalSize"))
                    .SetEnumValues("ConstantPixelSize", "ScaleWithScreenSize", "ConstantPhysicalSize"),

                new MethodFloat("match", L.T("Match factor between width and height when scaling with screen size, 0-1", "按屏幕尺寸缩放时的宽高匹配系数，0-1"))
                    .SetRange(0f, 1f),

                new MethodInt("reference_dpi", L.T("Reference DPI", "参考 DPI"))
                    .SetRange(1, 2000),

                new MethodInt("sorting_order", L.T("Panel sorting order", "面板排序顺序"))
                    .SetRange(-1000, 1000),

                new MethodInt("target_display", L.T("Target display index", "目标显示器索引"))
                    .SetRange(0, 7),

                new MethodStr("clear_color", L.T("Clear color written as #RRGGBB or #RRGGBBAA", "清屏颜色，格式 #RRGGBB 或 #RRGGBBAA"))
                    .AddExample("#00000000"),

                new MethodStr("save_path", L.T("Screenshot output path used by screenshot", "screenshot 使用的输出路径"))
                    .AddExample("Assets/Screenshots/ui.png")
            };
        }

        [ToolAction("create_uxml", Parameters = new string[] { "path", "content", "name" }, Inject = new string[] { "action=create_uxml" })]
        private object CreateUxml(JsonClass args)
        {
            string path = SanitizeAssetPath(args["path"]?.Value);
            if (string.IsNullOrEmpty(path))
            {
                return Response.Error("'path' is required for create_uxml.");
            }
            if (!path.EndsWith(".uxml", StringComparison.OrdinalIgnoreCase))
            {
                return Response.Error("UXML path must end with '.uxml'.");
            }
            if (File.Exists(ToFullPath(path)))
            {
                return Response.Error("UXML already exists: " + path);
            }
            string content = args["content"]?.Value;
            if (string.IsNullOrWhiteSpace(content))
            {
                content = BuildDefaultUxml(args["name"]?.Value);
            }
            if (!TryParseXml(content, out var parseError))
            {
                return Response.Error("Invalid UXML content: " + parseError);
            }
            EnsureDirectory(path);
            File.WriteAllText(ToFullPath(path), content);
            AssetDatabase.ImportAsset(path, (ImportAssetOptions)1);
            return Response.Success("UXML created.", DescribeUxml(path));
        }

        [ToolAction("read_uxml", Parameters = new string[] { "path" }, Required = new string[] { "path" }, Inject = new string[] { "action=read_uxml" })]
        private object ReadUxml(JsonClass args)
        {
            var path = SanitizeAssetPath(args["path"]?.Value);
            if (string.IsNullOrEmpty(path))
                return Response.Error("'path' is required for read_uxml.");

            var full = ToFullPath(path);
            if (!File.Exists(full))
                return Response.Error("UXML not found: " + path);

            var content = File.ReadAllText(full);
            return Response.Success("UXML read.", new
            {
                path = path,
                length = content.Length,
                content = content
            });
        }

        [ToolAction("write_uxml", Parameters = new string[] { "path", "content" }, Inject = new string[] { "action=write_uxml" })]
        private object WriteUxml(JsonClass args)
        {
            var path = SanitizeAssetPath(args["path"]?.Value);
            if (string.IsNullOrEmpty(path))
                return Response.Error("'path' is required for write_uxml.");

            var content = args["content"]?.Value;
            if (string.IsNullOrWhiteSpace(content))
                return Response.Error("'content' is required for write_uxml.");

            if (!TryParseXml(content, out var parseError))
                return Response.Error("Invalid UXML content: " + parseError);

            EnsureDirectory(path);
            File.WriteAllText(ToFullPath(path), content);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            return Response.Success("UXML written.", DescribeUxml(path));
        }

        [ToolAction("add_element", Parameters = new string[] { "path", "content", "name", "element_type", "parent", "attribute", "value" }, Inject = new string[] { "action=add_element" })]
        private object AddElement(JsonClass args)
        {
            XDocument document;
            string path;
            var error = LoadUxml(args, out document, out path);
            if (error != null)
                return error;

            var elementType = (args["element_type"]?.Value ?? "VisualElement").Trim();
            if (string.IsNullOrEmpty(elementType))
                elementType = "VisualElement";

            var name = (args["name"]?.Value ?? "").Trim();
            if (string.IsNullOrEmpty(name))
                return Response.Error("'name' is required for add_element.");

            if (FindElement(document, name) != null)
                return Response.Error("Element already exists: " + name);

            var root = document.Root;
            if (root == null)
                return Response.Error("UXML has no root element.");

            var parentName = (args["parent"]?.Value ?? "").Trim();
            var parent = string.IsNullOrEmpty(parentName) ? root : FindElement(document, parentName);
            if (parent == null)
                return Response.Error("Parent element not found: " + parentName);

            var child = new XElement(XName.Get(elementType, UxmlNamespace));
            child.SetAttributeValue("name", name);

            var text = args["content"]?.Value;
            if (!string.IsNullOrWhiteSpace(text))
                child.SetAttributeValue("text", text);

            var style = IsStyleAttribute(args["attribute"]?.Value) ? args["value"]?.Value : null;
            if (!string.IsNullOrWhiteSpace(style))
                child.SetAttributeValue("style", style);

            parent.Add(child);
            SaveUxml(path, document);

            return Response.Success("Element added.", DescribeUxml(path));
        }

        [ToolAction("set_element", Parameters = new string[] { "path", "name", "attribute", "value", "replace" }, Inject = new string[] { "action=set_element" })]
        private object SetElement(JsonClass args)
        {
            XDocument document;
            string path;
            var error = LoadUxml(args, out document, out path);
            if (error != null)
                return error;

            var name = (args["name"]?.Value ?? "").Trim();
            if (string.IsNullOrEmpty(name))
                return Response.Error("'name' is required for set_element.");

            var element = FindElement(document, name);
            if (element == null)
                return Response.Error("Element not found: " + name);

            var attribute = (args["attribute"]?.Value ?? "style").Trim();
            var value = args["value"]?.Value ?? "";
            var replace = args["replace"].AsBoolDefault(false);

            if (string.IsNullOrEmpty(attribute))
                attribute = "style";

            if (!replace && string.Equals(attribute, "style", StringComparison.OrdinalIgnoreCase))
            {
                var existing = element.Attribute("style")?.Value;
                value = string.IsNullOrWhiteSpace(existing) ? value : existing.TrimEnd(';', ' ') + "; " + value;
            }

            element.SetAttributeValue(attribute, value);
            SaveUxml(path, document);

            return Response.Success("Element attribute set.", new
            {
                path = path,
                element = name,
                attribute = attribute,
                value = value
            });
        }

        [ToolAction("remove_element", Parameters = new string[] { "path", "name" }, Inject = new string[] { "action=remove_element" })]
        private object RemoveElement(JsonClass args)
        {
            XDocument document;
            string path;
            var error = LoadUxml(args, out document, out path);
            if (error != null)
                return error;

            var name = (args["name"]?.Value ?? "").Trim();
            if (string.IsNullOrEmpty(name))
                return Response.Error("'name' is required for remove_element.");

            var element = FindElement(document, name);
            if (element == null)
                return Response.Error("Element not found: " + name);

            if (element.Parent == null)
                return Response.Error("Cannot remove the root element.");

            element.Remove();
            SaveUxml(path, document);
            return Response.Success("Element removed.", DescribeUxml(path));
        }

        [ToolAction("list_elements", Parameters = new string[] { "path", "recursive" }, Inject = new string[] { "action=list_elements" })]
        private object ListElements(JsonClass args)
        {
            XDocument document;
            string path;
            var error = LoadUxml(args, out document, out path);
            if (error != null)
                return error;

            var recursive = args["recursive"].AsBoolDefault(true);
            var items = new List<object>();
            CollectElements(document.Root, 0, recursive ? 32 : 1, items, 400);
            return Response.Success("Elements listed.", new { path = path, count = items.Count, items = items });
        }

        // ---------------- USS ----------------

        [ToolAction("create_uss", Parameters = new string[] { "path", "content" }, Inject = new string[] { "action=create_uss" })]
        private object CreateUss(JsonClass args)
        {
            var path = SanitizeAssetPath(args["path"]?.Value);
            if (string.IsNullOrEmpty(path))
                return Response.Error("'path' is required for create_uss.");

            if (!path.EndsWith(".uss", StringComparison.OrdinalIgnoreCase))
                return Response.Error("USS path must end with '.uss'.");

            if (File.Exists(ToFullPath(path)))
                return Response.Error("USS already exists: " + path);

            var content = args["content"]?.Value;
            if (string.IsNullOrWhiteSpace(content))
                content = "#root {\n    flex-grow: 1;\n}\n";

            EnsureDirectory(path);
            File.WriteAllText(ToFullPath(path), content);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);

            return Response.Success("USS created.", new
            {
                path = path,
                length = content.Length,
                rule_count = CountUssRules(content)
            });
        }

        [ToolAction("read_uss", Parameters = new string[] { "path" }, Required = new string[] { "path" }, Inject = new string[] { "action=read_uss" })]
        private object ReadUss(JsonClass args)
        {
            var path = SanitizeAssetPath(args["path"]?.Value);
            if (string.IsNullOrEmpty(path))
                return Response.Error("'path' is required for read_uss.");

            var full = ToFullPath(path);
            if (!File.Exists(full))
                return Response.Error("USS not found: " + path);

            var content = File.ReadAllText(full);
            return Response.Success("USS read.", new
            {
                path = path,
                length = content.Length,
                rule_count = CountUssRules(content),
                content = content
            });
        }

        [ToolAction("append_uss", Parameters = new string[] { "path", "content" }, Inject = new string[] { "action=append_uss" })]
        private object AppendUss(JsonClass args)
        {
            var path = SanitizeAssetPath(args["path"]?.Value);
            if (string.IsNullOrEmpty(path))
                return Response.Error("'path' is required for append_uss.");

            var content = args["content"]?.Value;
            if (string.IsNullOrWhiteSpace(content))
                return Response.Error("'content' is required for append_uss.");

            var full = ToFullPath(path);
            EnsureDirectory(path);

            var existing = File.Exists(full) ? File.ReadAllText(full) : "";
            var combined = existing.TrimEnd();
            if (combined.Length > 0)
                combined += "\n\n";
            combined += content;

            File.WriteAllText(full, combined);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);

            return Response.Success("USS updated.", new
            {
                path = path,
                length = combined.Length,
                rule_count = CountUssRules(combined)
            });
        }

        // ---------------- PanelSettings / UIDocument ----------------

        [ToolAction("create_panel_settings", Parameters = new string[] { "path", "panel_settings", "scale_mode", "match", "reference_dpi", "sorting_order", "target_display", "clear_color" }, Inject = new string[] { "action=create_panel_settings" })]
        private object CreatePanelSettings(JsonClass args)
        {
            var path = SanitizeAssetPath(args["path"]?.Value);
            if (string.IsNullOrEmpty(path)) path = SanitizeAssetPath(args["panel_settings"]?.Value);
            if (string.IsNullOrEmpty(path))
                return Response.Error("'path' or 'panel_settings' is required for create_panel_settings.");

            var type = OptionalApi.FindType(PanelSettingsType);
            if (type == null)
                return Response.Error("PanelSettings is not available in this Unity version; UI Toolkit may be disabled.");

            if (File.Exists(ToFullPath(path)))
                return Response.Error("PanelSettings already exists: " + path);

            try
            {
                EnsureDirectory(path);
                var instance = ScriptableObject.CreateInstance(type);
                AssetDatabase.CreateAsset(instance, path);
                AssetDatabase.SaveAssets();
                ApplyPanelSettings(instance, args);
                AssetDatabase.SaveAssets();

                return Response.Success("PanelSettings created.", DescribePanelSettings(path));
            }
            catch (Exception e)
            {
                return Response.Error("Failed to create PanelSettings: " + e.Message);
            }
        }

        [ToolAction("update_panel_settings", Parameters = new string[] { "path", "panel_settings", "scale_mode", "match", "reference_dpi", "sorting_order", "target_display", "clear_color" }, Inject = new string[] { "action=update_panel_settings" })]
        private object UpdatePanelSettings(JsonClass args)
        {
            var path = SanitizeAssetPath(args["panel_settings"]?.Value);
            if (string.IsNullOrEmpty(path)) path = SanitizeAssetPath(args["path"]?.Value);
            if (string.IsNullOrEmpty(path))
                return Response.Error("'panel_settings' is required for update_panel_settings.");

            var type = OptionalApi.FindType(PanelSettingsType);
            if (type == null)
                return Response.Error("PanelSettings is not available in this Unity version.");

            var asset = AssetDatabase.LoadAssetAtPath(path, type);
            if (asset == null)
                return Response.Error("PanelSettings asset not found: " + path);

            try
            {
                var applied = ApplyPanelSettings(asset, args);
                EditorUtility.SetDirty(asset);
                AssetDatabase.SaveAssets();

                return Response.Success("PanelSettings updated.", new
                {
                    path = path,
                    applied = applied,
                    settings = DescribePanelSettings(path)
                });
            }
            catch (Exception e)
            {
                return Response.Error("Failed to update PanelSettings: " + e.Message);
            }
        }

        [ToolAction("attach_document", Parameters = new string[] { "game_object", "uxml", "panel_settings" }, Inject = new string[] { "action=attach_document" })]
        private object AttachDocument(JsonClass args)
        {
            var gameObject = GameObjectUtils.FindObjectByIdOrPath(args["game_object"]?.Value);
            if (gameObject == null)
                return Response.Error("GameObject not found: " + args["game_object"]?.Value);

            var type = OptionalApi.FindType(DocumentType);
            if (type == null)
                return Response.Error("UIDocument is not available in this Unity version.");

            var uxmlPath = SanitizeAssetPath(args["uxml"]?.Value);
            var settingsPath = SanitizeAssetPath(args["panel_settings"]?.Value);

            try
            {
                Component document = gameObject.GetComponent(type);
                if (document == null)
                    document = gameObject.AddComponent(type);

                if (document == null)
                    return Response.Error("Failed to add UIDocument to " + gameObject.name);

                Undo.RecordObject(document, "Attach UIDocument");

                if (!string.IsNullOrEmpty(uxmlPath))
                {
                    var asset = AssetDatabase.LoadAssetAtPath(uxmlPath, OptionalApi.FindType("UnityEngine.UIElements.VisualTreeAsset"));
                    if (asset == null)
                        return Response.Error("UXML asset not found: " + uxmlPath);
                    OptionalApi.Set(document, "visualTreeAsset", asset);
                }

                if (!string.IsNullOrEmpty(settingsPath))
                {
                    var settings = AssetDatabase.LoadAssetAtPath(settingsPath, OptionalApi.FindType(PanelSettingsType));
                    if (settings == null)
                        return Response.Error("PanelSettings asset not found: " + settingsPath);
                    OptionalApi.Set(document, "panelSettings", settings);
                }

                EditorUtility.SetDirty(document);
                EditorSceneDirty();

                return Response.Success("UIDocument attached.", DescribeDocument(document));
            }
            catch (Exception e)
            {
                return Response.Error("Failed to attach UIDocument: " + e.Message);
            }
        }

        [ToolAction("detach_document", Parameters = new string[] { "game_object" }, Required = new string[] { "game_object" }, Inject = new string[] { "action=detach_document" })]
        private object DetachDocument(JsonClass args)
        {
            var gameObject = GameObjectUtils.FindObjectByIdOrPath(args["game_object"]?.Value);
            if (gameObject == null)
                return Response.Error("GameObject not found: " + args["game_object"]?.Value);

            var type = OptionalApi.FindType(DocumentType);
            if (type == null)
                return Response.Error("UIDocument is not available in this Unity version.");

            var document = gameObject.GetComponent(type);
            if (document == null)
                return Response.Error("GameObject has no UIDocument: " + gameObject.name);

            Undo.DestroyObjectImmediate(document);
            EditorSceneDirty();
            return Response.Success("UIDocument removed from '" + gameObject.name + "'.");
        }

        [ToolAction("get_document", Parameters = new string[] { "game_object" }, Required = new string[] { "game_object" }, Inject = new string[] { "action=get_document" })]
        private object GetDocument(JsonClass args)
        {
            var gameObject = GameObjectUtils.FindObjectByIdOrPath(args["game_object"]?.Value);
            if (gameObject == null)
                return Response.Error("GameObject not found: " + args["game_object"]?.Value);

            var type = OptionalApi.FindType(DocumentType);
            if (type == null)
                return Response.Error("UIDocument is not available in this Unity version.");

            var document = gameObject.GetComponent(type);
            if (document == null)
                return Response.Error("GameObject has no UIDocument: " + gameObject.name);

            return Response.Success("UIDocument info retrieved.", DescribeDocument(document));
        }

        [ToolAction("screenshot", Parameters = new string[] { "save_path" }, Required = new string[] { "save_path" }, Inject = new string[] { "action=screenshot" })]
        private object Screenshot(JsonClass args)
        {
            var savePath = args["save_path"]?.Value;
            if (string.IsNullOrWhiteSpace(savePath))
                return Response.Error("'save_path' is required for screenshot.");

            var full = ToFullPath(savePath);
            var result = ScreenCaptureUtil.CaptureGameView(full);
            if (!result.success)
                return Response.Error("Screenshot failed: " + result.error);

            return Response.Success("UI screenshot captured.", new
            {
                path = savePath,
                width = result.width,
                height = result.height
            });
        }

        // ---------------- 辅助 ----------------

        private object LoadUxml(JsonClass args, out XDocument document, out string path)
        {
            document = null;
            path = SanitizeAssetPath(args["path"]?.Value);
            if (string.IsNullOrEmpty(path))
                return Response.Error("'path' is required.");

            var full = ToFullPath(path);
            if (!File.Exists(full))
                return Response.Error("UXML not found: " + path);

            try
            {
                document = XDocument.Load(full, LoadOptions.PreserveWhitespace);
            }
            catch (Exception e)
            {
                return Response.Error("Failed to parse UXML: " + e.Message);
            }

            return null;
        }

        private static bool TryParseXml(string content, out string error)
        {
            error = null;
            try
            {
                XDocument.Parse(content);
                return true;
            }
            catch (Exception e)
            {
                error = e.Message;
                return false;
            }
        }

        private static void SaveUxml(string path, XDocument document)
        {
            document.Save(ToFullPath(path), SaveOptions.DisableFormatting);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
        }

        private static XElement FindElement(XDocument document, string name)
        {
            if (document == null || document.Root == null || string.IsNullOrEmpty(name))
                return null;

            return document.Root.DescendantsAndSelf()
                .FirstOrDefault(element => string.Equals(element.Attribute("name")?.Value, name, StringComparison.Ordinal));
        }

        private static void CollectElements(XElement element, int depth, int maxDepth, List<object> items, int limit)
        {
            if (element == null || items.Count >= limit || depth > maxDepth)
                return;

            var attributes = element.Attributes()
                .Where(attribute => !attribute.IsNamespaceDeclaration)
                .Select(attribute => attribute.Name.LocalName + "=" + attribute.Value)
                .ToList();

            items.Add(new
            {
                tag = element.Name.LocalName,
                name = element.Attribute("name")?.Value,
                depth = depth,
                child_count = element.Elements().Count(),
                attributes = attributes
            });

            if (depth >= maxDepth)
                return;

            foreach (var child in element.Elements())
                CollectElements(child, depth + 1, maxDepth, items, limit);
        }

        private static object DescribeUxml(string path)
        {
            var full = ToFullPath(path);
            if (!File.Exists(full))
                return new { path = path };

            var content = File.ReadAllText(full);
            var elementCount = 0;
            var named = new List<string>();

            try
            {
                var document = XDocument.Parse(content);
                if (document.Root != null)
                {
                    var all = document.Root.DescendantsAndSelf().ToList();
                    elementCount = all.Count;
                    for (int i = 0; i < all.Count; i++)
                    {
                        var name = all[i].Attribute("name")?.Value;
                        if (!string.IsNullOrEmpty(name) && named.Count < 100)
                            named.Add(name);
                    }
                }
            }
            catch (Exception)
            {
                // 解析失败时仅返回文件信息
            }

            return new
            {
                path = path,
                length = content.Length,
                element_count = elementCount,
                named_elements = named
            };
        }

        private static object DescribePanelSettings(string path)
        {
            var type = OptionalApi.FindType(PanelSettingsType);
            if (type == null)
                return new { path = path };

            var asset = AssetDatabase.LoadAssetAtPath(path, type);
            if (asset == null)
                return new { path = path };

            return new
            {
                path = path,
                scale_mode = OptionalApi.Get(asset, "scaleMode")?.ToString(),
                reference_dpi = OptionalApi.Get(asset, "referenceDpi"),
                sorting_order = OptionalApi.Get(asset, "sortingOrder"),
                target_display = OptionalApi.Get(asset, "targetDisplay"),
                clear_color = OptionalApi.Get(asset, "clearColor")?.ToString()
            };
        }

        private static object DescribeDocument(Component document)
        {
            var uxml = OptionalApi.Get(document, "visualTreeAsset") as UnityEngine.Object;
            var settings = OptionalApi.Get(document, "panelSettings") as UnityEngine.Object;
            var root = OptionalApi.Get(document, "rootVisualElement");
            var childCount = -1;
            if (root != null)
            {
                object count;
                if (OptionalApi.TryGet(root, "childCount", out count) && count != null)
                    childCount = Convert.ToInt32(count);
            }

            return new
            {
                game_object = document.gameObject.name,
                enabled = (document as Behaviour) != null ? ((Behaviour)document).enabled : true,
                uxml = uxml != null ? AssetDatabase.GetAssetPath(uxml) : null,
                panel_settings = settings != null ? AssetDatabase.GetAssetPath(settings) : null,
                root_child_count = childCount,
                sorting_order = OptionalApi.Get(document, "sortingOrder")
            };
        }

        private static List<string> ApplyPanelSettings(object settings, JsonClass args)
        {
            var applied = new List<string>();

            var scaleMode = args["scale_mode"]?.Value;
            if (!string.IsNullOrWhiteSpace(scaleMode) && OptionalApi.Set(settings, "scaleMode", scaleMode.Trim()))
                applied.Add("scale_mode");

            if (args["match"] != null)
            {
                var node = args["match"];
                if (node.type == JsonNodeType.Float || node.type == JsonNodeType.Integer)
                {
                    if (OptionalApi.Set(settings, "match", node.AsFloat))
                        applied.Add("match");
                }
            }

            if (args["reference_dpi"] != null)
            {
                var node = args["reference_dpi"];
                if (node.type == JsonNodeType.Integer || node.type == JsonNodeType.Float)
                {
                    if (OptionalApi.Set(settings, "referenceDpi", node.AsInt))
                        applied.Add("reference_dpi");
                }
            }

            if (args["sorting_order"] != null)
            {
                var node = args["sorting_order"];
                if (node.type == JsonNodeType.Integer || node.type == JsonNodeType.Float)
                {
                    if (OptionalApi.Set(settings, "sortingOrder", node.AsInt))
                        applied.Add("sorting_order");
                }
            }

            if (args["target_display"] != null)
            {
                var node = args["target_display"];
                if (node.type == JsonNodeType.Integer || node.type == JsonNodeType.Float)
                {
                    if (OptionalApi.Set(settings, "targetDisplay", node.AsInt))
                        applied.Add("target_display");
                }
            }

            var clearColor = args["clear_color"]?.Value;
            if (!string.IsNullOrWhiteSpace(clearColor))
            {
                Color color;
                if (ColorUtility.TryParseHtmlString(clearColor.Trim(), out color) &&
                    OptionalApi.Set(settings, "clearColor", color))
                    applied.Add("clear_color");
            }

            return applied;
        }

        private static bool IsStyleAttribute(string attribute)
        {
            return string.IsNullOrWhiteSpace(attribute) ||
                   string.Equals(attribute, "style", StringComparison.OrdinalIgnoreCase);
        }

        private static int CountUssRules(string content)
        {
            if (string.IsNullOrEmpty(content))
                return 0;

            var count = 0;
            for (int i = 0; i < content.Length; i++)
            {
                if (content[i] == '{')
                    count++;
            }

            return count;
        }

        private static string BuildDefaultUxml(string rootName)
        {
            var name = string.IsNullOrWhiteSpace(rootName) ? "root" : rootName.Trim();
            return "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
                   "<ui:UXML xmlns:ui=\"" + UxmlNamespace + "\" xmlns:uie=\"" + EditorNamespace + "\" editor-extension-mode=\"False\">\n" +
                   "    <ui:VisualElement name=\"" + name + "\" style=\"flex-grow: 1;\">\n" +
                   "        <ui:Label name=\"title\" text=\"UI Toolkit\" />\n" +
                   "    </ui:VisualElement>\n" +
                   "</ui:UXML>\n";
        }

        private static void EditorSceneDirty()
        {
            var scene = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene();
            if (scene.IsValid())
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);
        }

        private static string SanitizeAssetPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            path = path.Trim().Replace('\\', '/');
            if (path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) || path.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
                return path;

            return "Assets/" + path.TrimStart('/');
        }

        private static string ToFullPath(string assetPath)
        {
            return Path.Combine(Directory.GetCurrentDirectory(), assetPath.Replace('/', Path.DirectorySeparatorChar));
        }

        private static void EnsureDirectory(string assetPath)
        {
            var directory = Path.GetDirectoryName(assetPath);
            if (string.IsNullOrEmpty(directory))
                return;

            var full = Path.Combine(Directory.GetCurrentDirectory(), directory);
            if (!Directory.Exists(full))
            {
                Directory.CreateDirectory(full);
                AssetDatabase.Refresh();
            }
        }
    }
}
