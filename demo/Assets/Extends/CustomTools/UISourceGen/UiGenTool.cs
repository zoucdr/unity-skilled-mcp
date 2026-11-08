using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UniMcp;
using UnityEngine;
using UniMcp.Models;



#if UNITY_EDITOR
using UnityEditor;
#endif

namespace UniMcp.UiGen
{
    /// <summary>
    /// UISourceGen: a TexGen-style tool that draws UI control images with SkiaSharp and
    /// emits a matching layout description. Exposed to MCP exactly like TexGen.
    /// </summary>
    [ToolModule("uisourcegen", GroupEn = "UI", GroupZh = "界面",
        Tags = new[] { "ui-authoring", "procedural-generation", "code-generation" })]
    public partial class UiGenTool : ToolModuleBase
    {
        public override string Description => L.T(
            "Procedurally draw UI control images (buttons, panels, sliders, toggles, progress, inputs, icons, tabs...) with SkiaSharp and emit a matching layout JSON, for use as game UI assets.",
            "用 SkiaSharp 程序化绘制 UI 控件图（按钮/面板/滑块/开关/进度/输入/图标/选项卡…）并输出对应的 UI 布局 JSON，用作游戏 UI 资产。");

        private const string DefaultOutDir = "Assets/UISourceGen";

        // Application.dataPath 只能主线程读取。ToolCatalog 在主线程反射调用 CreateKeys 时预热，
        // 之后后台渲染线程只读缓存，避免 Unity API 跨线程异常。
        private static string _dataPath;
        private static string DataPath
        {
            get
            {
                if (_dataPath == null) _dataPath = Application.dataPath;
                return _dataPath;
            }
        }

        protected override MethodKey[] CreateKeys()
        {
            if (_dataPath == null) _dataPath = Application.dataPath;
            return new MethodKey[]
            {
                new MethodObj("screen", L.T("Screen description: {name,width,height,theme,controls:[...]}.", "界面描述：{name,width,height,theme,controls:[...]}"), false),
                new MethodStr("output_dir", L.T("Folder for generated assets (project-relative). Default Assets/UISourceGen.", "生成资产目录（工程相对）。默认 Assets/UISourceGen。")).SetDefault(DefaultOutDir),
                new MethodStr("base_name", L.T("Sub-folder / base name for this screen. Defaults to screen name.", "本界面子目录/基名，默认取界面名。")),
                new MethodBool("preview", L.T("Also render a full-screen composite preview PNG. Default true.", "同时渲染整屏合成预览 PNG，默认 true。")).SetDefault(true),
                new MethodFloat("supersample", L.T("Anti-aliasing factor 1-4. Default 2.", "抗锯齿倍数 1-4，默认 2。")).SetDefault(2f).SetRange(1f, 4f),
                new MethodStr("template", L.T("Fetch a built-in template spec by name (used with templates action).", "按名称取内置模板 spec（配合 templates 动作）。")),
                new MethodStr("tag", L.T("Filter templates by tag.", "按标签筛选模板。")),
                new MethodStr("name", L.T("Fetch / filter a template by name (templates action).", "按名称取用或筛选模板（templates 动作）。")),
                new MethodArr("screens", L.T("Batch input: array of screen objects.", "批量输入：界面对象数组。"), true, "object"),
                new MethodBool("select_asset", L.T("Select the preview in the Project view. Default false.", "在 Project 视图选中预览图，默认 false。")).SetDefault(false),

                // --- generic 2D canvas actions (see UiGenTool.Canvas.cs) ---
                new MethodObj("canvas", L.T("Canvas document: {name,width,height,background,elements:[...]}.", "画布文档：{name,width,height,background,elements:[...]}"), true),
                new MethodArr("canvases", L.T("Array of canvas documents (batch / pdf input).", "画布文档数组（批量 / PDF 输入）。"), true, "object"),
                new MethodStr("format", L.T("Image encoder: png (default), jpg, webp, bmp, gif.", "图像编码：png（默认）、jpg、webp、bmp、gif。")).SetDefault("png"),
                new MethodInt("quality", L.T("Encoder quality 1-100 for lossy formats. Default 100.", "有损格式的编码质量 1-100，默认 100。")).SetDefault(100).SetRange(1, 100),
                new MethodStr("name_suffix", L.T("Suffix appended to the output file name.", "追加到输出文件名后的后缀。")),
                new MethodStr("text", L.T("Text to measure (measure action).", "待测量文本（measure 动作）。")),
                new MethodStr("font_family", L.T("Font family name instead of a file path.", "字体族名（替代文件路径）。")),
                new MethodFloat("font_size", L.T("Font size in pixels. Default 24.", "字号（像素），默认 24。")).SetDefault(24f).SetRange(1f, 2048f),
                new MethodStr("font_path", L.T("Absolute path to a .ttf/.ttc/.otf font file.", "字体文件（.ttf/.ttc/.otf）绝对路径。")),
                new MethodBool("bold", L.T("Request a bold weight. Default false.", "使用粗体，默认 false。")).SetDefault(false),
                new MethodFloat("letter_spacing", L.T("Extra letter spacing in pixels.", "额外字距（像素）。")).SetDefault(0f),
                new MethodFloat("text_scale_x", L.T("Horizontal text scale multiplier. Default 1.", "文字水平缩放倍数，默认 1。")).SetDefault(1f),
                new MethodStr("path", L.T("SVG path data, e.g. \"M0 0 L100 0 L50 80 Z\".", "SVG 路径数据，例如 \"M0 0 L100 0 L50 80 Z\"。")),
                new MethodStr("fill", L.T("Fill colour (#hex / r,g,b[,a]) or paint kind.", "填充色（#hex / r,g,b[,a]）或 paint 类型。")),
                new MethodStr("stroke", L.T("Stroke colour for the svg action.", "svg 动作的描边色。")),
                new MethodFloat("stroke_width", L.T("Stroke width in pixels.", "描边宽度（像素）。")).SetDefault(0f).SetRange(0f, 512f),
                new MethodFloat("padding", L.T("Padding around rasterised svg content. Default 4.", "SVG 栅格化的留白，默认 4。")).SetDefault(4f).SetRange(0f, 512f),
                new MethodStr("filter", L.T("Substring filter for font / template listing.", "字体 / 模板列表的子串过滤。")),
                new MethodInt("width", L.T("Explicit output width in pixels (0 = auto).", "显式输出宽度（0 = 自动）。")).SetDefault(0).SetRange(0, 16384),
                new MethodInt("height", L.T("Explicit output height in pixels (0 = auto).", "显式输出高度（0 = 自动）。")).SetDefault(0).SetRange(0, 16384),
            };
        }

        // ---------------------------------------------------------------- render
        [ToolAction("render", "screen=require", "output_dir", "base_name", "preview", "supersample", "select_asset", Background = true)]
        private object ActionRender(JsonClass args)
        {
            JsonNode screenNode = UiParser.Get(args, "screen");
            if (screenNode == null)
                return Response.Error(L.T("Missing required \"screen\" object.", "缺少必填 \"screen\" 对象。"));

            var result = UiParser.Parse(screenNode);
            if (!result.Ok)
                return Response.Error(L.T("Screen description is invalid.", "界面描述无效。"), ToNode(result));

            UiScreen screen = result.Screen;
            float ss = Mathf.Clamp(UiParser.GetFloat(args, "supersample", screen.Supersample), 1f, 4f);
            screen.Supersample = ss;

            var renderer = new UiGenRenderer { DefaultFontPath = ResolveFont() };

            string outDir = UiParser.GetString(args, "output_dir", DefaultOutDir);
            string baseName = UiParser.GetString(args, "base_name", Sanitize(screen.Name));
            if (string.IsNullOrEmpty(baseName)) baseName = "Screen";
            string folder = CombinePath(outDir, baseName);

            string fullFolder = ProjectPath(folder);
            try { Directory.CreateDirectory(fullFolder); }
            catch (Exception e)
            {
                return Response.Error(L.T("Cannot create output folder: " + e.Message, "无法创建输出目录：" + e.Message));
            }

            var written = new List<string>();
            var byKey = new Dictionary<string, string>();
            var nine = new Dictionary<string, int[]>();
            var images = renderer.RenderControls(screen);
            foreach (var img in images)
            {
                string rel = folder + "/" + img.Key + ".png";
                string full = ProjectPath(rel);
                try
                {
                    File.WriteAllBytes(full, img.Png);
                    written.Add(rel);
                    byKey[img.Key] = rel;
                    if (!nine.ContainsKey(img.ControlId))
                        nine[img.ControlId] = new[] { img.NineL, img.NineT, img.NineR, img.NineB };
                }
                catch (Exception e) { result.Warnings.Add("write failed " + img.Key + ": " + e.Message); }
            }

            string previewRel = folder + "/preview.png";
            if (UiParser.GetBool(args, "preview", true) && images.Count > 0)
            {
                try
                {
                    File.WriteAllBytes(ProjectPath(previewRel), renderer.RenderScreenPreview(screen));
                    written.Add(previewRel);
                }
                catch (Exception e) { result.Warnings.Add("preview failed: " + e.Message); }
            }

            string layoutRel = folder + "/layout.json";
            string layoutJson = BuildLayoutJson(screen, byKey, ss, nine);
            try { File.WriteAllText(ProjectPath(layoutRel), layoutJson); written.Add(layoutRel); }
            catch (Exception e) { result.Warnings.Add("layout failed: " + e.Message); }

#if UNITY_EDITOR
            // AssetDatabase / Selection / delayCall 只能在主线程调用；渲染本身在后台线程完成。
            var writtenSnapshot = new List<string>(written);
            bool selectAsset = UiParser.GetBool(args, "select_asset", false);
            CoroutineRunner.Enqueue(() =>
            {
                AssetDatabase.DisallowAutoRefresh();
                try
                {
                    // 逐文件 ForceSynchronousImport 每次约 600ms；批量编辑模式把多次导入合成一次刷新。
                    AssetDatabase.StartAssetEditing();
                    try
                    {
                        foreach (string rel in writtenSnapshot)
                        {
                            if (rel.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || rel.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                                // AssetDatabase 只接受工程相对路径；ProjectPath 返回的是磁盘绝对路径。
                                AssetDatabase.ImportAsset(AssetDbPath(rel), ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
                        }
                    }
                    finally { AssetDatabase.StopAssetEditing(); }
                }
                finally
                {
                    EditorApplication.delayCall += () => AssetDatabase.AllowAutoRefresh();
                }
                if (selectAsset)
                    Selection.activeObject = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(previewRel);
            });
#endif

            var data = new JsonClass();
            data["output_dir"] = new JsonData(folder);
            data["screen"] = new JsonData(screen.Name);
            data["width"] = new JsonData(screen.Width);
            data["height"] = new JsonData(screen.Height);
            data["control_count"] = new JsonData(screen.Controls.Count);
            data["written"] = ToJsonArray(written);
            data["layout"] = new JsonData(layoutRel);
            data["preview"] = new JsonData(previewRel);
            if (result.Warnings.Count > 0) data["warnings"] = ToJsonArray(result.Warnings);
            return Response.Success(L.T(
                $"Rendered {screen.Controls.Count} controls to {folder}.",
                $"已将 {screen.Controls.Count} 个控件渲染到 {folder}。"), data);
        }

        // ---------------------------------------------------------------- batch
        [ToolAction("batch", "screens=require", "output_dir", "preview", "supersample", Background = true)]
        private object ActionBatch(JsonClass args)
        {
            JsonNode screensNode = UiParser.Get(args, "screens");
            if (!(screensNode is JsonArray screens) || screens.Count == 0)
                return Response.Error(L.T("\"screens\" must be a non-empty array.", "\"screens\" 必须为非空数组。"));

            var renderer = new UiGenRenderer { DefaultFontPath = ResolveFont() };
            string outDir = UiParser.GetString(args, "output_dir", DefaultOutDir);
            float ss = Mathf.Clamp(UiParser.GetFloat(args, "supersample", 2f), 1f, 4f);
            bool preview = UiParser.GetBool(args, "preview", true);

            var summary = new JsonClass();
            int totalControls = 0;
            var screensOut = new JsonClass();
            for (int i = 0; i < screens.Count; i++)
            {
                JsonClass sc = screens[i] as JsonClass;
                if (sc == null) continue;
                var r = UiParser.Parse(sc);
                if (!r.Ok) { screensOut[Sanitize(r.Screen.Name) + "_" + i] = new JsonData("parse error"); continue; }
                r.Screen.Supersample = ss;
                string baseName = Sanitize(r.Screen.Name);
                if (string.IsNullOrEmpty(baseName)) baseName = "Screen" + i;
                string folder = CombinePath(outDir, baseName);
                Directory.CreateDirectory(ProjectPath(folder));
                var byKey = new Dictionary<string, string>();
                var nine = new Dictionary<string, int[]>();
                foreach (var img in renderer.RenderControls(r.Screen))
                {
                    File.WriteAllBytes(ProjectPath(folder + "/" + img.Key + ".png"), img.Png);
                    byKey[img.Key] = img.Key + ".png";
                    if (!nine.ContainsKey(img.ControlId))
                        nine[img.ControlId] = new[] { img.NineL, img.NineT, img.NineR, img.NineB };
                }
                if (preview) File.WriteAllBytes(ProjectPath(folder + "/preview.png"), renderer.RenderScreenPreview(r.Screen));
                File.WriteAllText(ProjectPath(folder + "/layout.json"), BuildLayoutJson(r.Screen, byKey, ss, nine));
                totalControls += r.Screen.Controls.Count;
                screensOut[baseName] = new JsonData(folder);
            }
            summary["screens"] = screensOut;
            summary["total_controls"] = new JsonData(totalControls);
            return Response.Success(L.T($"Batch rendered {totalControls} controls.", $"批量渲染 {totalControls} 个控件。"), summary);
        }

        // ---------------------------------------------------------------- spec
        [ToolAction("spec")]
        private object ActionSpec(JsonClass args)
        {
            var data = new JsonClass();
            data["screen"] = GrammarScreen();
            data["control_types"] = ToJsonArray(Enum.GetNames(typeof(UiControlType)));
            data["states"] = ToJsonArray(Enum.GetNames(typeof(UiState)));
            data["icons"] = new JsonData("check, cross, arrow, chevron-down, star, menu, play, plus, minus, search, gear, heart");
            data["note"] = new JsonData(L.T(
                "Coordinates are screen pixels; anchor=center means x,y is the centre. Each control also emits a 9-slice metadata block for scalable sprites.",
                "坐标为屏幕像素；anchor=center 表示 x,y 为中心点。每个控件另输出九宫格元数据，便于可伸缩精灵。"));
            return Response.Success(L.T("UISourceGen grammar.", "UISourceGen 语法。"), data);
        }

        // ---------------------------------------------------------------- examples
        [ToolAction("examples")]
        private object ActionExamples(JsonClass args)
        {
            var data = new JsonClass();
            data["example"] = new JsonData(ExampleScreenJson());
            return Response.Success(L.T("A ready-to-use login screen spec.", "一份可直接使用的登录界面 spec。"), data);
        }

        // ---------------------------------------------------------------- templates
        [ToolAction("templates", "tag", "name")]
        private object ActionTemplates(JsonClass args)
        {
            string tag = UiParser.GetString(args, "tag", "");
            string name = UiParser.GetString(args, "name", "");
            string dir = "Assets/Extends/CustomTools/UISourceGen/Templates";
            string full = ProjectPath(dir);
            var data = new JsonClass();
            var list = new JsonClass();
            int found = 0;
            if (!Directory.Exists(full))
            {
                data["templates"] = list;
                data["count"] = new JsonData(0);
                return Response.Success(L.T("No templates found.", "未发现模板。"), data);
            }
            foreach (string file in Directory.GetFiles(full, "*.json"))
            {
                string text = File.ReadAllText(file);
                string fname = Path.GetFileNameWithoutExtension(file);
                bool match = string.IsNullOrEmpty(name) || fname.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0;
                if (!match && !string.IsNullOrEmpty(tag))
                    match = text.IndexOf(tag, StringComparison.OrdinalIgnoreCase) >= 0;
                if (!match && !string.IsNullOrEmpty(name)) continue;
                if (!string.IsNullOrEmpty(tag) && string.IsNullOrEmpty(name) && text.IndexOf(tag, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (match)
                {
                    list[fname] = new JsonData(text);
                    found++;
                }
            }
            data["templates"] = list;
            data["count"] = new JsonData(found);
            return Response.Success(L.T($"Found {found} templates.", $"找到 {found} 个模板。"), data);
        }

        // ---------------------------------------------------------------- helpers
        private static JsonClass GrammarScreen()
        {
            var sb = new StringBuilder();
            sb.Append('{');
            sb.Append('\"').Append("name").Append("\":\"Screen\",");
            sb.Append('\"').Append("width").Append("\":1280,\"height\":720,");
            sb.Append('\"').Append("background").Append("\":\"#121319\",");
            sb.Append('\"').Append("theme").Append("\":{\"primary\":\"#3399F5\",\"surface\":\"#1c1d22\",\"on_surface\":\"#f2f3f7\",\"radius\":10},");
            sb.Append('\"').Append("controls").Append("\":[");
            sb.Append('{').Append("\"id\":\"btn_play\",\"type\":\"button\",\"anchor\":\"center\",\"rect\":[540,360,200,56],");
            sb.Append("\"style\":{\"background\":\"#3399F5\",\"corner_radius\":12},\"text\":\"Play\",\"states\":[\"hover\",\"pressed\"]}");
            sb.Append(']');
            sb.Append('}');
            return JsonClass.Parse(sb.ToString()) as JsonClass ?? new JsonClass();
        }

        private static string ExampleScreenJson()
        {
            var sb = new StringBuilder();
            sb.Append('{');
            sb.Append('\"').Append("name").Append("\":\"Login\",");
            sb.Append('\"').Append("width").Append("\":960,\"height\":600,");
            sb.Append('\"').Append("background").Append("\":\"#121319\",\"background_end\":\"#080a0e\",");
            sb.Append('\"').Append("theme").Append("\":{\"primary\":\"#3399F5\",\"primary_end\":\"#1f66da\",\"surface\":\"#1c1d22\",\"surface_end\":\"#15161a\",\"on_surface\":\"#f2f3f7\",\"border\":\"#ffffff22\",\"radius\":12},");
            sb.Append('\"').Append("controls").Append("\":[");
            sb.Append('{').Append("\"id\":\"card\",\"type\":\"card\",\"anchor\":\"center\",\"rect\":[480,300,520,440]}");
            sb.Append(',').Append('{').Append("\"id\":\"title\",\"type\":\"label\",\"anchor\":\"center\",\"rect\":[480,130,400,40],\"text\":\"Sign In\",\"style\":{\"text_color\":\"#ffffff\",\"font_size\":30}");
            sb.Append(',').Append('{').Append("\"id\":\"user\",\"type\":\"input\",\"anchor\":\"center\",\"rect\":[480,210,420,52],\"text\":\"Username\"}");
            sb.Append(',').Append('{').Append("\"id\":\"pass\",\"type\":\"input\",\"anchor\":\"center\",\"rect\":[480,280,420,52],\"text\":\"Password\"}");
            sb.Append(',').Append('{').Append("\"id\":\"login\",\"type\":\"button\",\"anchor\":\"center\",\"rect\":[480,360,420,56],\"text\":\"Login\",\"style\":{\"background\":\"#3399F5\",\"background_end\":\"#1f66da\",\"corner_radius\":12}}");
            sb.Append(',').Append('{').Append("\"id\":\"remember\",\"type\":\"toggle\",\"anchor\":\"center\",\"rect\":[310,490,56,30],\"checked\":true}");
            sb.Append(',').Append('{').Append("\"id\":\"agree\",\"type\":\"checkbox\",\"anchor\":\"center\",\"rect\":[270,520,30,30]}");
            sb.Append(',').Append('{').Append("\"id\":\"lang\",\"type\":\"dropdown\",\"anchor\":\"center\",\"rect\":[480,490,200,36],\"text\":\"English\"}");
            sb.Append(']');
            sb.Append('}');
            return sb.ToString();
        }

        private static string BuildLayoutJson(UiScreen screen, Dictionary<string, string> byKey, float ss,
            Dictionary<string, int[]> nineSlice = null)
        {
            var sb = new StringBuilder();
            sb.Append('{');
            sb.Append(J("generator")).Append(":").Append("\"UISourceGen\"").Append(',');
            sb.Append(J("version")).Append(":").Append("\"1.1\"").Append(',');
            sb.Append(J("screen")).Append(":").Append(J(screen.Name)).Append(',');
            sb.Append(J("width")).Append(":").Append(screen.Width).Append(',');
            sb.Append(J("height")).Append(":").Append(screen.Height).Append(',');
            sb.Append(J("supersample")).Append(":").Append(ss.ToString("F2")).Append(',');
            sb.Append(J("theme")).Append(":{");
            sb.Append(J("primary")).Append(":").Append(J(ColorHex(screen.Theme.Primary))).Append(',');
            sb.Append(J("surface")).Append(":").Append(J(ColorHex(screen.Theme.Surface))).Append(',');
            sb.Append(J("on_surface")).Append(":").Append(J(ColorHex(screen.Theme.OnSurface))).Append('}');
            sb.Append(',');
            sb.Append(J("controls")).Append(":[");
            for (int i = 0; i < screen.Controls.Count; i++)
            {
                UiControl c = screen.Controls[i];
                if (i > 0) sb.Append(',');
                float left = c.Left(screen.Width), top = c.Top(screen.Height);
                sb.Append('{');
                sb.Append(J("id")).Append(":").Append(J(c.Id)).Append(',');
                sb.Append(J("type")).Append(":").Append(J(c.Type.ToString().ToLower())).Append(',');
                sb.Append(J("anchor")).Append(":").Append(J(c.Anchor.ToString().ToLower())).Append(',');
                sb.Append(J("rect")).Append(":[").Append(left.ToString("F1")).Append(',').Append(top.ToString("F1")).Append(',').Append(c.W.ToString("F1")).Append(',').Append(c.H.ToString("F1")).Append(']').Append(',');
                sb.Append(J("text")).Append(":").Append(J(c.Text)).Append(',');
                sb.Append(J("icon")).Append(":").Append(J(c.Icon)).Append(',');
                sb.Append(J("value")).Append(":").Append(c.Value.ToString("F2")).Append(',');
                sb.Append(J("checked")).Append(":").Append(c.Checked.ToString().ToLower()).Append(',');
                sb.Append(J("selected")).Append(":").Append(c.Selected.ToString().ToLower());

                // image + nine-slice from the rendered normal-state image (or first)
                string baseKey = c.Id + "_normal";
                if (!byKey.ContainsKey(baseKey)) baseKey = c.Id + "_" + UiState.Normal.ToString().ToLower();
                sb.Append(',').Append(J("image")).Append(":").Append(J(byKey.ContainsKey(baseKey) ? byKey[baseKey] : ""));
                if (nineSlice != null && nineSlice.TryGetValue(c.Id, out int[] nine) && nine != null)
                {
                    sb.Append(',').Append(J("slice")).Append(":[").Append(nine[0]).Append(',').Append(nine[1]).Append(',')
                      .Append(nine[2]).Append(',').Append(nine[3]).Append(']');
                }
                sb.Append(',').Append(J("states")).Append(":{");
                bool first = true;
                string prefix = c.Id + "_";
                foreach (var kv in byKey)
                {
                    // match on the exact "<id>_<state>" pair; a plain StartsWith
                    // would mis-bind ids that are prefixes of other ids
                    if (!kv.Key.StartsWith(prefix, StringComparison.Ordinal)) continue;
                    string st = kv.Key.Substring(prefix.Length);
                    if (!IsStateName(st)) continue;
                    if (!first) sb.Append(',');
                    sb.Append(J(st)).Append(":").Append(J(kv.Value));
                    first = false;
                }
                sb.Append('}');
                sb.Append('}');
            }
            sb.Append(']');
            sb.Append('}');
            return sb.ToString();
        }

        private static bool IsStateName(string s)
        {
            switch (s)
            {
                case "normal": case "hover": case "pressed":
                case "disabled": case "selected": case "checked":
                    return true;
                default:
                    return false;
            }
        }

        private static string ColorHex(UiColor c)
        {
            int r = (int)(c.R * 255 + 0.5f), g = (int)(c.G * 255 + 0.5f), b = (int)(c.B * 255 + 0.5f);
            return string.Format("#{0:X2}{1:X2}{2:X2}", r, g, b);
        }

        private static string J(string s)
        {
            var sb = new StringBuilder();
            sb.Append('"');
            foreach (char ch in s)
            {
                if (ch == '"' || ch == '\\') sb.Append('\\');
                sb.Append(ch);
            }
            sb.Append('"');
            return sb.ToString();
        }

        private static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder();
            foreach (char ch in s)
                if (char.IsLetterOrDigit(ch) || ch == '_' || ch == '-') sb.Append(ch); else if (ch == ' ') sb.Append('_');
            return sb.ToString();
        }

        private static string CombinePath(string dir, string name)
        {
            dir = dir.Replace('\\', '/').TrimEnd('/');
            name = name.Replace('\\', '/').Trim('/');
            return string.IsNullOrEmpty(name) ? dir : dir + "/" + name;
        }

        /// <summary>工程相对路径（AssetDatabase 专用）。written 里的 rel 已是 Assets/... 形式。</summary>
        private static string AssetDbPath(string rel)
        {
            if (string.IsNullOrEmpty(rel)) return rel;
            string p = rel.Replace('\\', '/');
            if (p.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
                || p.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase)) return p;
            if (Path.IsPathRooted(p))
            {
                string root = DataPath.Replace('\\', '/');
                if (p.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    return "Assets/" + p.Substring(root.Length).TrimStart('/');
                return p;
            }
            return "Assets/" + p.TrimStart('/');
        }

        private static string ProjectPath(string assetsPath)
        {
            assetsPath = assetsPath.Replace('\\', '/');
            if (assetsPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) || assetsPath == "Assets")
            {
                // "Assets/".Length == 7; a "+1" past the prefix separator. Cutting 6
                // drops the leading separator and makes Combine treat the rest as
                // rooted, which silently redirects every write to the drive root.
                string tail = assetsPath.Length > 7 ? assetsPath.Substring(7) : string.Empty;
                return Path.Combine(DataPath, tail).Replace('\\', '/');
            }
            string projectRoot = Directory.GetParent(DataPath)?.FullName ?? DataPath;
            return Path.Combine(projectRoot, assetsPath).Replace('\\', '/');
        }

        private static string ResolveFont()
        {
            string[] cands =
            {
                "C:/Windows/Fonts/msyh.ttc",
                "C:/Windows/Fonts/arial.ttf",
                "C:/Windows/Fonts/segoeui.ttf",
                "/System/Library/Fonts/PingFang.ttc",
                "/System/Library/Fonts/HelveticaNeue.ttc",
                "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf"
            };
            foreach (string c in cands) if (File.Exists(c)) return c;
            return null;
        }

        private static JsonArray ToJsonArray(IEnumerable<string> items)
        {
            var arr = new JsonArray();
            foreach (string it in items) arr.Add(new JsonData(it));
            return arr;
        }

        private static JsonClass ToNode(UiParseResult r)
        {
            var d = new JsonClass();
            if (r.Errors.Count > 0) d["errors"] = ToJsonArray(r.Errors);
            if (r.Warnings.Count > 0) d["warnings"] = ToJsonArray(r.Warnings);
            return d;
        }
    }
}
