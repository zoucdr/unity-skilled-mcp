using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SkiaSharp;
using UniMcp;
using UniMcp.Models;
using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace UniMcp.UiGen
{
    /// <summary>
    /// The generic 2D half of UISourceGen: free-form canvas drawing on top of
    /// SkiaSharp. The UI-control actions build pre-composed widgets; these actions
    /// expose the raw drawing surface so an agent can produce any 2D asset —
    /// sprites, icons, diagrams, charts, patterns, PDFs.
    ///
    /// Declared as a partial of <see cref="UiGenTool"/> so both halves share one
    /// "uisourcegen" prefix, one group, and one set of helper methods.
    /// </summary>
    public partial class UiGenTool
    {
        // ----------------------------------------------------------------- draw

        [ToolAction("draw", "canvas=require", "output_dir", "base_name", "format", "quality",
                    "name_suffix", "supersample", "select_asset")]
        private object ActionDraw(JsonClass args)
        {
            JsonNode canvasNode = UiParser.Get(args, "canvas");
            if (canvasNode == null)
                return Response.Error(L.T("Missing required \"canvas\" object.", "缺少必填 \"canvas\" 对象。"));

            CanvasParseResult parsed = CanvasParser.Parse(canvasNode);
            if (!parsed.Ok)
            {
                var err = new JsonClass();
                err["errors"] = ToJsonArray(parsed.Errors);
                if (parsed.Warnings.Count > 0) err["warnings"] = ToJsonArray(parsed.Warnings);
                return Response.Error(L.T("Canvas description is invalid.", "画布描述无效。"), err);
            }

            CanvasDoc doc = parsed.Doc;
            float ss = Mathf.Clamp(UiParser.GetFloat(args, "supersample", doc.Supersample), 1f, 4f);
            doc.Supersample = ss;

            string format = (UiParser.GetString(args, "format", "png") ?? "png").ToLowerInvariant();
            SKEncodedImageFormat enc;
            string ext;
            switch (format)
            {
                case "jpg":
                case "jpeg": enc = SKEncodedImageFormat.Jpeg; ext = "jpg"; break;
                case "webp": enc = SKEncodedImageFormat.Webp; ext = "webp"; break;
                case "bmp": enc = SKEncodedImageFormat.Bmp; ext = "bmp"; break;
                case "gif": enc = SKEncodedImageFormat.Gif; ext = "gif"; break;
                default: enc = SKEncodedImageFormat.Png; ext = "png"; break;
            }
            int quality = Mathf.Clamp(UiParser.GetInt(args, "quality", 100), 1, 100);

            string outDir = UiParser.GetString(args, "output_dir", DefaultOutDir);
            string baseName = UiParser.GetString(args, "base_name", Sanitize(doc.Name));
            if (string.IsNullOrEmpty(baseName)) baseName = "Canvas";
            string suffix = UiParser.GetString(args, "name_suffix", "");
            if (!string.IsNullOrEmpty(suffix)) baseName += "_" + Sanitize(suffix);

            string folder = CombinePath(outDir, baseName);
            string fullFolder = ProjectPath(folder);
            try { Directory.CreateDirectory(fullFolder); }
            catch (Exception e)
            {
                return Response.Error(L.T("Cannot create output folder: " + e.Message, "无法创建输出目录：" + e.Message));
            }

            var written = new List<string>();
            var renderer = new CanvasRenderer
            {
                DefaultFontPath = ResolveFont(),
                PathResolver = ResolveAssetPath
            };

            string rel = folder + "/" + Sanitize(baseName) + "." + ext;
            try
            {
                byte[] bytes = renderer.Render(doc, enc, quality);
                if (bytes == null || bytes.Length == 0)
                    return Response.Error(L.T("Renderer produced no image.", "渲染器未产出图像。"));
                File.WriteAllBytes(ProjectPath(rel), bytes);
                written.Add(rel);
            }
            catch (Exception e)
            {
                return Response.Error(L.T("Render failed: " + e.Message, "渲染失败：" + e.Message));
            }

            // optional JSON sidecar describing what was drawn, so a consumer can
            // rebuild the same canvas without re-reading the request
            string metaRel = folder + "/canvas.json";
            try
            {
                File.WriteAllText(ProjectPath(metaRel), BuildCanvasJson(doc));
                written.Add(metaRel);
            }
            catch (Exception e) { parsed.Warnings.Add("meta write failed: " + e.Message); }

            ImportAssets(written);

            var data = new JsonClass();
            data["output_dir"] = new JsonData(folder);
            data["image"] = new JsonData(rel);
            data["meta"] = new JsonData(metaRel);
            data["name"] = new JsonData(doc.Name);
            data["width"] = new JsonData(doc.Width);
            data["height"] = new JsonData(doc.Height);
            data["supersample"] = new JsonData(ss);
            data["pixel_width"] = new JsonData((int)Math.Round(doc.Width * ss));
            data["pixel_height"] = new JsonData((int)Math.Round(doc.Height * ss));
            data["format"] = new JsonData(ext);
            data["element_count"] = new JsonData(CountElements(doc.Elements));
            data["written"] = ToJsonArray(written);
            if (parsed.Warnings.Count > 0) data["warnings"] = ToJsonArray(parsed.Warnings);
            if (renderer.Warnings.Count > 0) data["render_warnings"] = ToJsonArray(renderer.Warnings);

            if (UiParser.GetBool(args, "select_asset", false))
            {
#if UNITY_EDITOR
                Selection.activeObject = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(rel);
#endif
            }

            return Response.Success(L.T(
                $"Drew {doc.Name} ({doc.Width}x{doc.Height}) to {rel}.",
                $"已绘制 {doc.Name}（{doc.Width}x{doc.Height}）到 {rel}。"), data);
        }

        // ---------------------------------------------------------------- batch

        [ToolAction("draw_batch", "canvases=require", "output_dir", "format", "quality", "supersample")]
        private object ActionDrawBatch(JsonClass args)
        {
            JsonNode listNode = UiParser.Get(args, "canvases");
            if (!(listNode is JsonArray list) || list.Count == 0)
                return Response.Error(L.T("\"canvases\" must be a non-empty array.", "\"canvases\" 必须为非空数组。"));

            string outDir = UiParser.GetString(args, "output_dir", DefaultOutDir);
            string format = (UiParser.GetString(args, "format", "png") ?? "png").ToLowerInvariant();
            SKEncodedImageFormat enc = format == "jpg" || format == "jpeg" ? SKEncodedImageFormat.Jpeg
                                    : format == "webp" ? SKEncodedImageFormat.Webp
                                    : SKEncodedImageFormat.Png;
            string ext = format == "jpg" || format == "jpeg" ? "jpg" : format == "webp" ? "webp" : "png";
            int quality = Mathf.Clamp(UiParser.GetInt(args, "quality", 100), 1, 100);

            var outputs = new JsonArray();
            var warnings = new List<string>();
            var written = new List<string>();
            int done = 0;

            var renderer = new CanvasRenderer
            {
                DefaultFontPath = ResolveFont(),
                PathResolver = ResolveAssetPath
            };

            for (int i = 0; i < list.Count; i++)
            {
                CanvasParseResult parsed = CanvasParser.Parse(list[i]);
                if (!parsed.Ok)
                {
                    warnings.Add("canvas " + i + ": " + string.Join("; ", parsed.Errors));
                    continue;
                }
                CanvasDoc doc = parsed.Doc;
                doc.Supersample = Mathf.Clamp(UiParser.GetFloat(args, "supersample", doc.Supersample), 1f, 4f);

                string name = Sanitize(doc.Name);
                if (string.IsNullOrEmpty(name)) name = "Canvas" + i;
                string folder = CombinePath(outDir, name);
                try { Directory.CreateDirectory(ProjectPath(folder)); }
                catch (Exception e) { warnings.Add(name + ": cannot create folder " + e.Message); continue; }

                string rel = folder + "/" + name + "." + ext;
                try
                {
                    byte[] bytes = renderer.Render(doc, enc, quality);
                    if (bytes == null || bytes.Length == 0) { warnings.Add(name + ": empty render"); continue; }
                    File.WriteAllBytes(ProjectPath(rel), bytes);
                    string metaRel = folder + "/canvas.json";
                    File.WriteAllText(ProjectPath(metaRel), BuildCanvasJson(doc));
                    written.Add(rel);
                    written.Add(metaRel);
                    outputs.Add(new JsonData(rel));
                    done++;
                }
                catch (Exception e) { warnings.Add(name + ": " + e.Message); }
                foreach (string w in renderer.Warnings) warnings.Add(name + ": " + w);
            }

            ImportAssets(written);

            var data = new JsonClass();
            data["output_dir"] = new JsonData(outDir);
            data["count"] = new JsonData(done);
            data["images"] = outputs;
            if (warnings.Count > 0) data["warnings"] = ToJsonArray(warnings);
            return Response.Success(L.T($"Drew {done} canvases.", $"已绘制 {done} 张画布。"), data);
        }

        // ------------------------------------------------------------------ pdf

        [ToolAction("pdf", "canvases=require", "output_dir", "base_name")]
        private object ActionPdf(JsonClass args)
        {
            JsonNode listNode = UiParser.Get(args, "canvases");
            if (!(listNode is JsonArray list) || list.Count == 0)
                return Response.Error(L.T("\"canvases\" must be a non-empty array.", "\"canvases\" 必须为非空数组。"));

            var docs = new List<CanvasDoc>();
            var warnings = new List<string>();
            for (int i = 0; i < list.Count; i++)
            {
                CanvasParseResult parsed = CanvasParser.Parse(list[i]);
                if (parsed.Ok) docs.Add(parsed.Doc);
                else warnings.Add("canvas " + i + ": " + string.Join("; ", parsed.Errors));
            }
            if (docs.Count == 0)
                return Response.Error(L.T("No valid canvas in the array.", "数组中没有有效画布。"));

            string outDir = UiParser.GetString(args, "output_dir", DefaultOutDir);
            string baseName = Sanitize(UiParser.GetString(args, "base_name", docs[0].Name));
            if (string.IsNullOrEmpty(baseName)) baseName = "Canvas";

            string rel = CombinePath(outDir, baseName) + "/" + baseName + ".pdf";
            try
            {
                var renderer = new CanvasRenderer
                {
                    DefaultFontPath = ResolveFont(),
                    PathResolver = ResolveAssetPath
                };
                byte[] bytes = renderer.RenderPdf(docs);
                if (bytes == null || bytes.Length == 0)
                    return Response.Error(L.T("PDF encoder produced nothing.", "PDF 编码器未产出内容。"));
                Directory.CreateDirectory(Path.GetDirectoryName(ProjectPath(rel)) ?? ProjectPath(outDir));
                File.WriteAllBytes(ProjectPath(rel), bytes);
                ImportAssets(new[] { rel });

                var data = new JsonClass();
                data["pdf"] = new JsonData(rel);
                data["pages"] = new JsonData(docs.Count);
                data["bytes"] = new JsonData(bytes.Length);
                if (warnings.Count > 0) data["warnings"] = ToJsonArray(warnings);
                foreach (string w in renderer.Warnings) warnings.Add(w);
                return Response.Success(L.T($"Exported a {docs.Count}-page PDF to {rel}.", $"已导出 {docs.Count} 页 PDF 到 {rel}。"), data);
            }
            catch (Exception e)
            {
                return Response.Error(L.T("PDF export failed: " + e.Message, "PDF 导出失败：" + e.Message));
            }
        }

        // ---------------------------------------------------------------- fonts

        [ToolAction("fonts", "filter")]
        private object ActionFonts(JsonClass args)
        {
            string filter = UiParser.GetString(args, "filter", "");
            var families = new List<string>();
            try
            {
                using var fm = SKFontManager.CreateDefault();
                foreach (string f in fm.GetFontFamilies())
                {
                    if (string.IsNullOrEmpty(f)) continue;
                    if (!string.IsNullOrEmpty(filter) && f.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    families.Add(f);
                }
            }
            catch (Exception e)
            {
                return Response.Error(L.T("Font enumeration failed: " + e.Message, "字体枚举失败：" + e.Message));
            }
            families.Sort(StringComparer.OrdinalIgnoreCase);

            var data = new JsonClass();
            data["count"] = new JsonData(families.Count);
            data["families"] = ToJsonArray(families);
            data["default_path"] = new JsonData(ResolveFont() ?? "");
            return Response.Success(L.T($"Found {families.Count} font families.", $"找到 {families.Count} 个字体族。"), data);
        }

        // -------------------------------------------------------------- measure

        [ToolAction("measure", "text", "font_size", "font_path", "font_family", "bold",
                    "letter_spacing", "text_scale_x", "path")]
        private object ActionMeasure(JsonClass args)
        {
            string text = UiParser.GetString(args, "text", null);
            string path = UiParser.GetString(args, "path", null);
            if (string.IsNullOrEmpty(text) && string.IsNullOrEmpty(path))
                return Response.Error(L.T("Pass \"text\" and/or \"path\" to measure.", "请传 \"text\" 和/或 \"path\" 进行测量。"));

            var data = new JsonClass();
            var renderer = new CanvasRenderer
            {
                DefaultFontPath = ResolveFont(),
                PathResolver = ResolveAssetPath
            };

            if (!string.IsNullOrEmpty(text))
            {
                var el = new CanvasElement
                {
                    Shape = CanvasShape.Text,
                    Text = text,
                    FontSize = UiParser.GetFloat(args, "font_size", 24f),
                    FontPath = UiParser.GetString(args, "font_path", null),
                    FontFamily = UiParser.GetString(args, "font_family", null),
                    Bold = UiParser.GetBool(args, "bold", false),
                    TextScaleX = UiParser.GetFloat(args, "text_scale_x", 1f)
                };
                using var paint = new SKPaint
                {
                    Typeface = ResolveMeasureTypeface(renderer, el),
                    TextSize = el.FontSize,
                    TextScaleX = el.TextScaleX,
                    IsAntialias = true
                };
                float width = paint.MeasureText(text);
                paint.GetFontMetrics(out SKFontMetrics m);

                var textInfo = new JsonClass();
                textInfo["width"] = new JsonData(width);
                textInfo["ascent"] = new JsonData(m.Ascent);
                textInfo["descent"] = new JsonData(m.Descent);
                textInfo["leading"] = new JsonData(m.Leading);
                textInfo["line_height"] = new JsonData(m.Descent - m.Ascent + m.Leading);
                textInfo["char_count"] = new JsonData(text.Length);
                textInfo["font_size"] = new JsonData(el.FontSize);
                data["text"] = textInfo;
            }

            if (!string.IsNullOrEmpty(path))
            {
                using SKPath p = SKPath.ParseSvgPathData(path);
                if (p == null)
                    return Response.Error(L.T("Could not parse svg path.", "无法解析 SVG 路径。"));
                using var measure = new SKPathMeasure(p, false);
                var pathInfo = new JsonClass();
                pathInfo["length"] = new JsonData(measure.Length);
                SKRect bounds = p.Bounds;
                pathInfo["bounds"] = new JsonData($"{bounds.Left},{bounds.Top},{bounds.Width},{bounds.Height}");
                data["path"] = pathInfo;
            }

            return Response.Success(L.T("Measurement complete.", "测量完成。"), data);
        }

        private static SKTypeface ResolveMeasureTypeface(CanvasRenderer renderer, CanvasElement el)
        {
            // reuse the renderer's own resolution by asking it to draw nothing:
            // simplest reliable path is a direct typeface load mirroring its rules.
            string path = el.FontPath;
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                try { return SKTypeface.FromFile(path, 0); } catch { }
            }
            if (!string.IsNullOrEmpty(el.FontFamily))
            {
                try
                {
                    SKTypeface tf = SKTypeface.FromFamilyName(el.FontFamily,
                        el.Bold ? SKFontStyle.Bold : SKFontStyle.Normal);
                    if (tf != null) return tf;
                }
                catch { }
            }
            if (!string.IsNullOrEmpty(renderer.DefaultFontPath) && File.Exists(renderer.DefaultFontPath))
            {
                try { return SKTypeface.FromFile(renderer.DefaultFontPath, renderer.DefaultFontIndex); } catch { }
            }
            return SKTypeface.Default;
        }

        // ----------------------------------------------------------------- svg

        [ToolAction("svg", "path=require", "output_dir", "base_name", "width", "height",
                    "fill", "stroke", "stroke_width", "supersample")]
        private object ActionSvg(JsonClass args)
        {
            string pathData = UiParser.GetString(args, "path", null);
            if (string.IsNullOrEmpty(pathData))
                return Response.Error(L.T("Missing required \"path\" (svg path data).", "缺少必填 \"path\"（SVG 路径数据）。"));

            SKPath probe = SKPath.ParseSvgPathData(pathData);
            if (probe == null)
                return Response.Error(L.T("Could not parse svg path data.", "无法解析 SVG 路径数据。"));
            SKRect bounds = probe.Bounds;
            probe.Dispose();

            float pad = Math.Max(2f, UiParser.GetFloat(args, "padding", 4f));
            int w = UiParser.GetInt(args, "width", 0);
            int h = UiParser.GetInt(args, "height", 0);

            bool hasSize = w > 0 && h > 0;
            if (!hasSize)
            {
                w = Math.Max(1, (int)Math.Ceiling(bounds.Width + pad * 2f));
                h = Math.Max(1, (int)Math.Ceiling(bounds.Height + pad * 2f));
            }

            var doc = new CanvasDoc
            {
                Name = UiParser.GetString(args, "base_name", "Svg") ?? "Svg",
                Width = w,
                Height = h,
                Supersample = Mathf.Clamp(UiParser.GetFloat(args, "supersample", 1f), 1f, 4f)
            };

            var el = new CanvasElement
            {
                Shape = CanvasShape.Path,
                PathData = pathData,
                // shift the artwork so its bounds start at the padding offset
                X = pad - bounds.Left,
                Y = pad - bounds.Top
            };
            el.Fill = new CanvasPaint
            {
                FillKind = ParseFillKind(args, "fill"),
                Color = UiParser.GetColor(args, "fill", new UiColor(0.13f, 0.6f, 0.96f, 1f)),
                Stroke = false
            };
            float strokeWidth = UiParser.GetFloat(args, "stroke_width", 0f);
            string strokeRaw = UiParser.GetString(args, "stroke", null);
            if (strokeWidth > 0f || !string.IsNullOrEmpty(strokeRaw))
            {
                el.Stroke = new CanvasPaint
                {
                    FillKind = CanvasFillKind.None,
                    Stroke = true,
                    StrokeWidth = strokeWidth > 0f ? strokeWidth : 1f,
                    Color = UiColor.TryParse(strokeRaw, out UiColor sc) ? sc : new UiColor(0f, 0f, 0f, 1f)
                };
            }
            doc.Elements.Add(el);

            string outDir = UiParser.GetString(args, "output_dir", DefaultOutDir);
            string baseName = Sanitize(doc.Name);
            string rel = CombinePath(outDir, baseName) + "/" + baseName + ".png";
            try
            {
                var renderer = new CanvasRenderer { DefaultFontPath = ResolveFont(), PathResolver = ResolveAssetPath };
                byte[] bytes = renderer.Render(doc);
                if (bytes == null || bytes.Length == 0)
                    return Response.Error(L.T("Rasterisation produced nothing.", "栅格化未产出内容。"));
                Directory.CreateDirectory(Path.GetDirectoryName(ProjectPath(rel)) ?? ProjectPath(outDir));
                File.WriteAllBytes(ProjectPath(rel), bytes);
                ImportAssets(new[] { rel });

                var data = new JsonClass();
                data["image"] = new JsonData(rel);
                data["width"] = new JsonData(w);
                data["height"] = new JsonData(h);
                data["bounds"] = new JsonData($"{bounds.Left},{bounds.Top},{bounds.Width},{bounds.Height}");
                return Response.Success(L.T($"Rasterised svg path to {rel}.", $"已将 SVG 路径栅格化到 {rel}。"), data);
            }
            catch (Exception e)
            {
                return Response.Error(L.T("SVG raster failed: " + e.Message, "SVG 栅格化失败：" + e.Message));
            }
        }

        private static CanvasFillKind ParseFillKind(JsonClass args, string key)
        {
            string raw = UiParser.GetString(args, key, null);
            if (string.IsNullOrEmpty(raw)) return CanvasFillKind.None;
            if (raw.StartsWith("#") || raw.IndexOf(',') >= 0) return CanvasFillKind.Solid;
            switch (raw.ToLowerInvariant())
            {
                case "none": return CanvasFillKind.None;
                case "linear": return CanvasFillKind.Linear;
                case "radial": return CanvasFillKind.Radial;
                case "sweep": return CanvasFillKind.Sweep;
                default: return CanvasFillKind.Solid;
            }
        }

        // ---------------------------------------------------------------- spec

        [ToolAction("canvas_spec")]
        private object ActionCanvasSpec(JsonClass args)
        {
            var data = new JsonClass();
            data["canvas"] = GrammarCanvas();
            data["shapes"] = ToJsonArray(Enum.GetNames(typeof(CanvasShape)));
            data["paints"] = ToJsonArray(Enum.GetNames(typeof(CanvasFillKind)));
            data["blend_modes"] = ToJsonArray(Enum.GetNames(typeof(CanvasBlend)));
            data["path_effects"] = ToJsonArray(Enum.GetNames(typeof(CanvasPathEffect)));
            data["image_filters"] = ToJsonArray(Enum.GetNames(typeof(CanvasImageFilter)));
            data["icons"] = new JsonData(string.Join(", ", IconLibrary.Names));
            data["encode_formats"] = new JsonData("png, jpg, webp, bmp, gif; pdf via the pdf action");
            data["note"] = new JsonData(L.T(
                "Element coordinates: rect/roundrect/image/text use x,y,w,h as a box; circle/oval/arc use x,y as the box origin with w,h as diameter; line uses x,y plus to:[dx,dy]; path/polygon/polyline/points use local point lists shifted by x,y. " +
                "Gradient anchors are relative to the shape box unless the paint sets \"space\":\"canvas\".",
                "元素坐标：rect/roundrect/image/text 用 x,y,w,h 作矩形；circle/oval/arc 用 x,y 作外框原点、w,h 作直径；line 用 x,y 加 to:[dx,dy]；path/polygon/polyline/points 用局部点列，由 x,y 整体偏移。" +
                "渐变锚点默认相对图形框，paint 设 \"space\":\"canvas\" 时改为画布坐标。"));
            return Response.Success(L.T("Canvas drawing grammar.", "画布绘制语法。"), data);
        }

        // ------------------------------------------------------------- examples

        [ToolAction("canvas_examples")]
        private object ActionCanvasExamples(JsonClass args)
        {
            var data = new JsonClass();
            data["example"] = new JsonData(ExampleCanvasJson());
            data["note"] = new JsonData(L.T(
                "Feed the \"canvas\" object straight into uisourcegen.draw.",
                "把 \"canvas\" 对象直接喂给 uisourcegen.draw。"));
            return Response.Success(L.T("A ready-to-use canvas document.", "一份可直接使用的画布文档。"), data);
        }

        // -------------------------------------------------------------- helpers

        private string ResolveAssetPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) || path == "Assets")
                return ProjectPath(path);
            return path;
        }

        private static int CountElements(List<CanvasElement> list)
        {
            if (list == null) return 0;
            int n = 0;
            foreach (CanvasElement e in list)
            {
                n++;
                if (e.Children != null) n += CountElements(e.Children);
            }
            return n;
        }

        private void ImportAssets(IEnumerable<string> rels)
        {
#if UNITY_EDITOR
            AssetDatabase.DisallowAutoRefresh();
            try
            {
                foreach (string rel in rels)
                {
                    if (string.IsNullOrEmpty(rel)) continue;
                    if (rel.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                        || rel.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                        || rel.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)
                        || rel.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase)
                        || rel.EndsWith(".gif", StringComparison.OrdinalIgnoreCase)
                        || rel.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                        || rel.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                        AssetDatabase.ImportAsset(ProjectPath(rel),
                            ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
                }
            }
            finally
            {
                EditorApplication.delayCall += () => AssetDatabase.AllowAutoRefresh();
            }
#endif
        }

        /// <summary>Serialises a canvas document back to the same JSON shape the parser reads.</summary>
        private static string BuildCanvasJson(CanvasDoc doc)
        {
            var sb = new StringBuilder();
            sb.Append('{');
            sb.Append(J("generator")).Append(':').Append(J("UISourceGen")).Append(',');
            sb.Append(J("name")).Append(':').Append(J(doc.Name)).Append(',');
            sb.Append(J("width")).Append(':').Append(doc.Width).Append(',');
            sb.Append(J("height")).Append(':').Append(doc.Height).Append(',');
            sb.Append(J("supersample")).Append(':').Append(doc.Supersample.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
            if (!string.IsNullOrEmpty(doc.Note))
                sb.Append(',').Append(J("note")).Append(':').Append(J(doc.Note));
            sb.Append(',').Append(J("elements")).Append(':').Append('[');
            for (int i = 0; i < doc.Elements.Count; i++)
            {
                if (i > 0) sb.Append(',');
                AppendElement(sb, doc.Elements[i]);
            }
            sb.Append(']').Append('}');
            return sb.ToString();
        }

        private static void AppendElement(StringBuilder sb, CanvasElement e)
        {
            sb.Append('{');
            sb.Append(J("shape")).Append(':').Append(J(e.Shape.ToString().ToLowerInvariant()));
            if (!string.IsNullOrEmpty(e.Id)) sb.Append(',').Append(J("id")).Append(':').Append(J(e.Id));
            sb.Append(',').Append(J("x")).Append(':').Append(F(e.X));
            sb.Append(',').Append(J("y")).Append(':').Append(F(e.Y));
            if (e.W != 0f) sb.Append(',').Append(J("w")).Append(':').Append(F(e.W));
            if (e.H != 0f) sb.Append(',').Append(J("h")).Append(':').Append(F(e.H));
            if (e.Radius != 0f) sb.Append(',').Append(J("radius")).Append(':').Append(F(e.Radius));
            if (e.X1 != 0f || e.Y1 != 0f)
                sb.Append(',').Append(J("to")).Append(':').Append('[').Append(F(e.X1)).Append(',').Append(F(e.Y1)).Append(']');
            if (!string.IsNullOrEmpty(e.PathData)) sb.Append(',').Append(J("path")).Append(':').Append(J(e.PathData));
            if (!string.IsNullOrEmpty(e.Text)) sb.Append(',').Append(J("text")).Append(':').Append(J(e.Text));
            if (!string.IsNullOrEmpty(e.Icon)) sb.Append(',').Append(J("icon")).Append(':').Append(J(e.Icon));
            if (!string.IsNullOrEmpty(e.ImagePath)) sb.Append(',').Append(J("image")).Append(':').Append(J(e.ImagePath));
            if (e.FontSize != 24f) sb.Append(',').Append(J("font_size")).Append(':').Append(F(e.FontSize));

            if (e.Points != null && e.Points.Count > 0)
            {
                sb.Append(',').Append(J("points")).Append(':').Append('[');
                for (int i = 0; i < e.Points.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('[').Append(F(e.Points[i].X)).Append(',').Append(F(e.Points[i].Y)).Append(']');
                }
                sb.Append(']');
            }

            if (e.Fill != null) AppendPaint(sb, "fill", e.Fill);
            if (e.Stroke != null) AppendPaint(sb, "stroke", e.Stroke);

            if (e.Tx != 0f || e.Ty != 0f)
                sb.Append(',').Append(J("translate")).Append(':').Append('[').Append(F(e.Tx)).Append(',').Append(F(e.Ty)).Append(']');
            if (e.RotateDeg != 0f) sb.Append(',').Append(J("rotate")).Append(':').Append(F(e.RotateDeg));
            if (e.ScaleX != 1f || e.ScaleY != 1f)
                sb.Append(',').Append(J("scale")).Append(':').Append('[').Append(F(e.ScaleX)).Append(',').Append(F(e.ScaleY)).Append(']');
            if (e.Opacity < 0.999f) sb.Append(',').Append(J("opacity")).Append(':').Append(F(e.Opacity));
            if (e.Clip)
            {
                sb.Append(',').Append(J("clip")).Append(':').Append('{');
                sb.Append(J("shape")).Append(':').Append(J(e.ClipShape.ToString().ToLowerInvariant()));
                if (e.ClipHasRect)
                {
                    sb.Append(',').Append(J("rect")).Append(':').Append('[')
                      .Append(F(e.ClipX)).Append(',').Append(F(e.ClipY)).Append(',')
                      .Append(F(e.ClipW)).Append(',').Append(F(e.ClipH)).Append(']');
                }
                if (e.ClipRadius != 0f) sb.Append(',').Append(J("radius")).Append(':').Append(F(e.ClipRadius));
                sb.Append('}');
            }

            if (e.Children != null && e.Children.Count > 0)
            {
                sb.Append(',').Append(J("children")).Append(':').Append('[');
                for (int i = 0; i < e.Children.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    AppendElement(sb, e.Children[i]);
                }
                sb.Append(']');
            }
            sb.Append('}');
        }

        private static void AppendPaint(StringBuilder sb, string key, CanvasPaint p)
        {
            sb.Append(',').Append(J(key)).Append(':').Append('{');
            sb.Append(J("paint")).Append(':').Append(J(p.FillKind.ToString().ToLowerInvariant()));
            sb.Append(',').Append(J("color")).Append(':').Append(J(ColorHex(p.Color)));
            if (p.Color2.HasValue) sb.Append(',').Append(J("color2")).Append(':').Append(J(ColorHex(p.Color2.Value)));
            if (p.Stroke)
            {
                sb.Append(',').Append(J("stroke")).Append(':').Append("true");
                sb.Append(',').Append(J("stroke_width")).Append(':').Append(F(p.StrokeWidth));
            }
            if (p.Blend != CanvasBlend.SrcOver)
                sb.Append(',').Append(J("blend")).Append(':').Append(J(p.Blend.ToString().ToLowerInvariant()));
            if (p.X0 != 0f) sb.Append(',').Append(J("x0")).Append(':').Append(F(p.X0));
            if (p.Y0 != 0f) sb.Append(',').Append(J("y0")).Append(':').Append(F(p.Y0));
            if (p.X1 != 0f) sb.Append(',').Append(J("x1")).Append(':').Append(F(p.X1));
            if (p.Y1 != 0f) sb.Append(',').Append(J("y1")).Append(':').Append(F(p.Y1));
            if (p.ShaderSpaceCanvas) sb.Append(',').Append(J("space")).Append(':').Append(J("canvas"));
            sb.Append('}');
        }

        private static string F(float v) => v.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);

        private static string GrammarCanvas()
        {
            var sb = new StringBuilder();
            sb.Append('{');
            sb.Append("\"name\":\"Card\",\"width\":400,\"height\":240,");
            sb.Append("\"background\":\"#101116\",");
            sb.Append("\"elements\":[");
            sb.Append("{\"shape\":\"roundrect\",\"x\":20,\"y\":20,\"w\":360,\"h\":200,\"radius\":18,");
            sb.Append("\"fill\":{\"paint\":\"linear\",\"color\":\"#232634\",\"color2\":\"#161822\"}},");
            sb.Append("{\"shape\":\"text\",\"x\":44,\"y\":70,\"text\":\"Hello\",\"font_size\":34,\"fill\":\"#ffffff\"},");
            sb.Append("{\"shape\":\"circle\",\"x\":300,\"y\":40,\"w\":40,\"h\":40,\"fill\":\"#3399f5\"}");
            sb.Append("]}");
            return sb.ToString();
        }

        private static string ExampleCanvasJson()
        {
            var sb = new StringBuilder();
            sb.Append('{');
            sb.Append("\"name\":\"IconCard\",\"width\":420,\"height\":260,");
            sb.Append("\"background\":{\"paint\":\"linear\",\"color\":\"#1b1d27\",\"color2\":\"#0d0e14\"},");
            sb.Append("\"elements\":[");
            sb.Append("{\"shape\":\"roundrect\",\"x\":24,\"y\":24,\"w\":372,\"h\":212,\"radius\":20,");
            sb.Append("\"fill\":{\"paint\":\"linear\",\"color\":\"#2a2e3d\",\"color2\":\"#1a1c26\"},");
            sb.Append("\"stroke\":{\"color\":\"#ffffff22\",\"stroke_width\":1}},");
            sb.Append("{\"shape\":\"icon\",\"icon\":\"star\",\"x\":56,\"y\":56,\"w\":56,\"h\":56,\"fill\":\"#f7b731\"},");
            sb.Append("{\"shape\":\"text\",\"x\":56,\"y\":136,\"w\":300,\"h\":40,\"text\":\"Achievement\",\"font_size\":28,\"fill\":\"#f2f3f7\"},");
            sb.Append("{\"shape\":\"text\",\"x\":56,\"y\":184,\"w\":300,\"h\":28,\"text\":\"Unlocked 12 minutes ago\",\"font_size\":16,\"fill\":\"#8a90a0\"},");
            sb.Append("{\"shape\":\"badge\",\"x\":318,\"y\":60,\"w\":64,\"h\":30},");
            sb.Append("]}");
            return sb.ToString();
        }
    }
}
