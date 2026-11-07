using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UniMcp;
using UniMcp.Models;
using UniMcp.TexGen;

namespace UniMcp.Tools.TexGen
{
    /// <summary>
    /// Procedural texture generation: an instruction-list drawing engine that
    /// rasterises vector primitives onto a Texture2D and exports PNG.
    ///
    /// This is not image synthesis — it is a vector drawing primitive toolkit the
    /// caller (typically an LLM) drives. The model translates intent into a list
    /// of shape / fill / blend / layer commands, and this tool paints the pixels.
    /// No network, no shaders, no third-party packages.
    /// </summary>
    [ToolModule("texgen", GroupEn = "Assets", GroupZh = "资源资产")]
    public class TexGenTool : ToolModuleBase
    {
        public override string Description => L.T(
            "Procedurally draw textures from a structured instruction list (shapes + fills + blends + layers) and export PNG. Use texgen.spec for the full grammar and texgen.examples for ready-made instruction lists.",
            "按结构化绘图指令表（图元 + 填充 + 混合 + 图层）程序化绘制贴图并导出 PNG。用 texgen.spec 取完整指令语法，用 texgen.examples 取现成示例指令表。");

        protected override MethodKey[] CreateKeys()
        {
            return new MethodKey[]
            {
                // --- Input ---
                new MethodArr("layers", L.T(
                    "Layer list: [{shape, fill, blend, ...}] (preferred). Each entry is one drawing operation.",
                    "图层列表：[{shape, fill, blend, ...}]（推荐）。每项为一次绘制操作。"), true, "object"),
                new MethodStr("layers_json", L.T(
                    "Same layer list as a JSON string, for clients that cannot nest arrays.",
                    "与 layers 等价的 JSON 字符串，供不便嵌套数组的客户端使用。")),
                new MethodArr("specs", L.T(
                    "Batch input: [{name, width, height, background, layers:[...]}] rendered as separate files.",
                    "批量输入：[{name, width, height, background, layers:[...]}]，各自渲染为独立文件。"), true, "object"),

                // --- Canvas ---
                new MethodInt("width", L.T("Canvas width in pixels", "画布宽度（像素）"))
                    .SetRange(1, TgSpec.MaxDimension).SetDefault(256),
                new MethodInt("height", L.T("Canvas height in pixels", "画布高度（像素）"))
                    .SetRange(1, TgSpec.MaxDimension).SetDefault(256),
                new MethodStr("background", L.T(
                    "Canvas background: #RRGGBB, #RRGGBBAA, \"r,g,b[,a]\" or a colour name. Default transparent.",
                    "画布底色：#RRGGBB、#RRGGBBAA、\"r,g,b[,a]\" 或颜色名。默认透明。"))
                    .AddExamples("#00000000", "#1a1a2e"),
                new MethodStr("origin", L.T(
                    "Coordinate origin: topLeft (default) or center. Y always grows downward.",
                    "坐标原点：topLeft（默认）或 center。Y 始终向下增长。"))
                    .SetEnumValues("topLeft", "center").SetDefault("topLeft"),
                new MethodInt("supersample", L.T(
                    "Antialiasing factor 1-4 (internal supersampling). Default 2.",
                    "抗锯齿倍数 1-4（内部超采样）。默认 2。"))
                    .SetRange(1, 4).SetDefault(2),
                new MethodBool("tiling", L.T(
                    "Make periodic fills wrap at the canvas border so the PNG tiles seamlessly. Aliases: tileable, seamless.",
                    "让周期性填充在画布边界闭合，使 PNG 可无缝平铺。别名：tileable、seamless。")),

                // --- Normal map ---
                new MethodBool("normal_map", L.T(
                    "Convert the rendered result into a tangent-space normal map.",
                    "把渲染结果转为切线空间法线贴图。")),
                new MethodFloat("normal_strength", L.T(
                    "Relief strength when normal_map is on. Default 4.",
                    "normal_map 开启时的凹凸强度。默认 4。"))
                    .SetRange(0f, 64f).SetDefault(4f),
                new MethodStr("image_path", L.T(
                    "Source PNG for normal_from_image (project-relative or absolute).",
                    "normal_from_image 的源 PNG（工程相对路径或绝对路径）"))
                    .AddExample("Assets/Textures/height.png"),

                // --- Output ---
                new MethodStr("output_path", L.T(
                    "PNG path to write, e.g. Assets/Textures/icon.png (project-relative or absolute).",
                    "PNG 输出路径，如 Assets/Textures/icon.png（工程相对或绝对）。"))
                    .AddExamples("Assets/Textures/icon.png", "Assets/Textures/magic_orb.png"),
                new MethodStr("output_dir", L.T(
                    "Folder for batch output. Default Assets/TexGen.",
                    "批量输出目录。默认 Assets/TexGen。"))
                    .AddExample("Assets/Textures/Icons"),
                new MethodStr("prefix", L.T(
                    "File name prefix for batch output.", "批量输出的文件名前缀。")),
                new MethodBool("overwrite", L.T(
                    "Allow replacing an existing PNG. Default true.",
                    "允许覆盖已存在的 PNG。默认 true。")).SetDefault(true),

                // --- Import settings (applied when the PNG lands under Assets/) ---
                new MethodStr("texture_type", L.T(
                    "Importer texture type: Default, Sprite, NormalMap, EditorGUIAndLegacy, Cursor, Cookie, Lightmap, HDR.",
                    "导入器纹理类型：Default、Sprite、NormalMap、EditorGUIAndLegacy、Cursor、Cookie、Lightmap、HDR。"))
                    .SetEnumValues("Default", "Sprite", "NormalMap", "EditorGUIAndLegacy", "Cursor", "Cookie", "Lightmap", "HDR"),
                new MethodStr("filter_mode", L.T("Importer filter mode.", "导入器过滤模式。"))
                    .SetEnumValues("Point", "Bilinear", "Trilinear"),
                new MethodStr("wrap_mode", L.T("Importer wrap mode.", "导入器包裹模式。"))
                    .SetEnumValues("Repeat", "Clamp", "Mirror", "MirrorOnce"),
                new MethodInt("pixels_per_unit", L.T("Sprite pixels-per-unit when texture_type is Sprite.", "texture_type 为 Sprite 时的像素/单位。"))
                    .SetRange(1, 2048).SetDefault(100),
                new MethodInt("max_size", L.T("Importer max texture size.", "导入器最大纹理尺寸。"))
                    .SetEnumValues("32", "64", "128", "256", "512", "1024", "2048", "4096", "8192"),
                new MethodBool("sprite_pivot_center", L.T("Keep the sprite pivot centred.", "精灵轴心保持居中。")).SetDefault(true),

                // --- Mesh-aware rendering (texgen.uv_info / texgen.for_mesh) ---
                new MethodStr("mesh_path", L.T(
                    "Mesh asset (Assets/... .fbx/.obj/.asset) or a scene GameObject hierarchy path to read UVs from.",
                    "要读取 UV 的 Mesh 资源路径（Assets/... .fbx/.obj/.asset），或场景中 GameObject 的层级路径。"))
                    .AddExamples("Assets/Models/Desk.fbx", "Props/Desk"),
                new MethodStr("group_by", L.T(
                    "How to split UV space into regions: submesh (default), island (UV-connected), all.",
                    "UV 空间分区方式：submesh（默认）、island（UV 连通岛）、all（不分区）。"))
                    .SetEnumValues("submesh", "island", "all").SetDefault("submesh"),
                new MethodArr("regions", L.T(
                    "Per-region instruction lists: [{id, layers:[...]}]. Regions without an entry use the global layers.",
                    "各分区的指令表：[{id, layers:[...]}]。未列出的分区使用全局 layers。"), true, "object"),
                new MethodStr("regions_json", L.T(
                    "Same region list as a JSON string, for clients that cannot nest arrays.",
                    "与 regions 等价的 JSON 字符串，供不便嵌套数组的客户端使用。")),
                new MethodInt("padding", L.T(
                    "Dilate edge colours outward by N pixels so mipmaps never sample empty space at UV seams. Default 4.",
                    "把边缘颜色向外扩张 N 像素，避免 mipmap 在 UV 接缝处采样到空白。默认 4。"))
                    .SetRange(0, 64).SetDefault(4),
                new MethodStr("fill_mode", L.T(
                    "How to treat pixels no UV triangle covers: 'background' (flat colour, default) or 'nearest' (extend the nearest UV region colour, producing a solid atlas without island silhouettes).",
                    "未被任何 UV 三角形覆盖的像素如何填充：'background'（纯底色，默认）或 'nearest'（用最近的 UV 分区颜色填满，图集不再出现一圈圈岛轮廓）。"))
                    .SetEnumValues("background", "nearest").SetDefault("background"),
                new MethodStr("material_path", L.T(
                    "Optional: assign the written PNG to this material's texture property.",
                    "可选：把写出的 PNG 挂到该材质的贴图属性上。"))
                    .AddExample("Assets/Materials/Desk.mat"),
                new MethodStr("texture_property", L.T(
                    "Shader property written when material_path is set. Default _MainTex.",
                    "设置 material_path 时要写入的着色器属性，默认 _MainTex。"))
                    .SetDefault("_MainTex").AddExamples("_MainTex", "_BaseMap"),

                // --- Misc ---
                new MethodBool("open_preview", L.T(
                    "Open the TexGen preview window with the rendered result.",
                    "用渲染结果打开 TexGen 预览窗口。")),
                new MethodBool("select_asset", L.T(
                    "Ping the written asset in the Project window.",
                    "在 Project 窗口中定位写出的资源。")),

                // --- PBR derivation ---
                new MethodFloat("roughness_bias", L.T(
                    "Mid-point of the derived roughness map (0 = mirror, 1 = fully rough). Default 0.55.",
                    "派生粗糙度的中值（0 = 镜面，1 = 全粗糙）。默认 0.55。"))
                    .SetRange(0f, 1f).SetDefault(0.55f),
                new MethodFloat("roughness_contrast", L.T(
                    "How strongly height drives roughness: high spots polish, crevices roughen. Default 0.6.",
                    "高度对粗糙度的影响强度：凸起更光滑、凹陷更粗糙。默认 0.6。"))
                    .SetRange(0f, 4f).SetDefault(0.6f),
                new MethodFloat("ao_strength", L.T(
                    "Ambient-occlusion darkening strength for the derived AO/ORM map. Default 0.7.",
                    "派生 AO/ORM 的环境光遮蔽强度，默认 0.7。"))
                    .SetRange(0f, 4f).SetDefault(0.7f),
                new MethodInt("ao_radius", L.T(
                    "Occlusion search radius in pixels. Default 6.",
                    "遮蔽采样半径（像素），默认 6。"))
                    .SetRange(1, 64).SetDefault(6),
                new MethodFloat("metallic", L.T(
                    "Constant metalness written into the ORM map's blue channel. Default 0.",
                    "写入 ORM 蓝通道的常量金属度，默认 0。"))
                    .SetRange(0f, 1f).SetDefault(0f),

                // --- Template library ---
                new MethodStr("tag", L.T(
                    "Filter the built-in template library by tag (ground, wall, fabric, metal, nature, ui, ...).",
                    "按标签筛选内置模板库（ground、wall、fabric、metal、nature、ui 等）。"))
                    .AddExamples("ground", "wall", "metal", "ui"),
                new MethodStr("name", L.T(
                    "Exact template name to return, e.g. brick_wall.",
                    "要返回的模板名，如 brick_wall。"))
                    .AddExample("brick_wall"),
                new MethodBool("include_spec", L.T(
                    "Attach each template's render spec. Defaults to true only when 'name' is given.",
                    "附带每个模板的渲染 spec。仅在指定 name 时默认为 true。"))
            };
        }

        // ------------------------------------------------------------------
        // Actions
        // ------------------------------------------------------------------

        [ToolAction("render",
            DescriptionEn = "Draw an instruction list onto a canvas and export it as a PNG (optionally converting to a normal map).",
            DescriptionZh = "把指令表绘制到画布并导出 PNG（可选转为法线贴图）。",
            Parameters = new[]
            {
                "layers", "layers_json", "width", "height", "background", "origin", "supersample",
                "normal_map", "normal_strength", "output_path", "overwrite",
                "texture_type", "filter_mode", "wrap_mode", "pixels_per_unit", "max_size",
                "sprite_pivot_center", "open_preview", "select_asset"
            },
            Inject = new[] { "action=render" })]
        private object ActionRender(JsonClass args)
        {
            var parse = ParseArguments(args);
            if (!parse.Ok) return ValidationError(parse);

            string outputPath = TgJson.GetString(args, "output_path");
            return RenderAndWrite(parse.Spec, outputPath, args, true);
        }

        [ToolAction("batch",
            DescriptionEn = "Render several independent specs into one folder, each as its own PNG.",
            DescriptionZh = "把多个独立 spec 渲染到同一目录，各自输出一个 PNG。",
            Parameters = new[]
            {
                "specs", "output_dir", "prefix", "width", "height", "background", "origin", "supersample",
                "overwrite", "select_asset", "texture_type", "filter_mode", "wrap_mode",
                "pixels_per_unit", "max_size"
            },
            Inject = new[] { "action=batch" })]
        private object ActionBatch(JsonClass args)
        {
            JsonNode specsNode = TgJson.Get(args, "specs");
            JsonArray specs = specsNode as JsonArray;
            if (specs == null || specs.Count == 0)
                return Response.Error(L.T(
                    "\"specs\" must be a non-empty array of {name, width, height, layers:[...]}.",
                    "\"specs\" 必须是非空数组，元素为 {name, width, height, layers:[...]}。"));

            string outputDir = TgJson.GetString(args, "output_dir", "Assets/TexGen");
            string prefix = TgJson.GetString(args, "prefix", "");

            var written = new JsonArray();
            var failed = new JsonArray();

            // Writing many PNGs in one call lets Unity's asset watcher refresh the
            // database mid-loop, reload the domain and cut the JSON-RPC response short.
            // Hold the refresh until every file is on disk.
            AssetDatabase.DisallowAutoRefresh();
            try
            {
                for (int i = 0; i < specs.Count; i++)
                {
                    JsonClass item = specs[i] as JsonClass;
                    string label = item != null ? TgJson.GetString(item, "name", "specs[" + i + "]") : "specs[" + i + "]";
                    if (item == null)
                    {
                        failed.Add(new JsonData(label + " is not an object"));
                        continue;
                    }

                    // Field-wise merge: item wins, args act as defaults.
                    var merged = new JsonClass();
                    CopyIfPresent(args, merged, "width", "height", "background", "origin", "supersample",
                        "texture_type", "filter_mode", "wrap_mode", "pixels_per_unit", "max_size",
                        "normal_map", "normal_strength");
                    foreach (var kv in item.AsEnumerable()) merged[kv.Key] = kv.Value;

                    var parse = ParseArguments(merged);
                    if (!parse.Ok)
                    {
                        failed.Add(new JsonData(label + ": " + string.Join("; ", parse.Errors.ToArray())));
                        continue;
                    }

                    string name = TgJson.GetString(item, "name", "tex_" + i);
                    string filePath = outputDir.TrimEnd('/', '\\') + "/" + prefix + name + ".png";

                    var result = RenderAndWrite(parse.Spec, filePath, merged, false) as JsonClass;
                    bool ok = result != null && result.TryGetValue("success", out JsonNode okNode) && okNode.AsBoolDefault(false);

                    if (ok)
                    {
                        var entry = new JsonClass();
                        entry["name"] = name;
                        entry["path"] = filePath;
                        entry["width"] = parse.Spec.Width;
                        entry["height"] = parse.Spec.Height;
                        written.Add(entry);
                    }
                    else
                    {
                        string err = "render failed";
                        if (result != null && result.TryGetValue("error", out JsonNode errNode) && errNode != null)
                            err = errNode.Value;
                        failed.Add(new JsonData(label + ": " + err));
                    }
                }
            }
            finally
            {
                AssetDatabase.AllowAutoRefresh();
            }


            if (written.Count == 0)
                return Response.Error(L.T("No spec rendered successfully.", "没有任何 spec 渲染成功。"), failed);

            var data = new JsonClass();
            data["output_dir"] = outputDir;
            data["written"] = written;
            data["count"] = written.Count;
            if (failed.Count > 0) data["failed"] = failed;

            if (TgJson.GetBool(args, "select_asset", false))
                SelectAsset(outputDir);

            return Response.Success(
                L.T($"Rendered {written.Count} texture(s) into '{outputDir}'.",
                    $"已渲染 {written.Count} 张贴图到 '{outputDir}'。"), data);
        }

        [ToolAction("validate",
            DescriptionEn = "Parse an instruction list and report resolution, layer summary and any problems, without rendering.",
            DescriptionZh = "只解析指令表，返回解析结果、图层摘要与问题，不渲染。",
            Parameters = new[] { "layers", "layers_json", "width", "height", "background", "origin", "supersample", "normal_map" },
            Inject = new[] { "action=validate" })]
        private object ActionValidate(JsonClass args)
        {
            var parse = ParseArguments(args);
            var data = new JsonClass();
            data["valid"] = parse.Ok;

            if (parse.Spec != null)
            {
                data["width"] = parse.Spec.Width;
                data["height"] = parse.Spec.Height;
                data["origin"] = parse.Spec.Origin.ToString();
                data["supersample"] = parse.Spec.Supersample;
                data["normal_map"] = parse.Spec.NormalMap;

                var layerInfo = new JsonArray();
                foreach (var layer in parse.Spec.Layers)
                {
                    var entry = new JsonClass();
                    entry["index"] = layerInfo.Count;
                    entry["name"] = layer.Name;
                    entry["shape"] = layer.Shape.ToString();
                    entry["fill"] = layer.Fill.ToString();
                    entry["blend"] = layer.Blend.ToString();
                    entry["opacity"] = layer.Opacity;
                    layerInfo.Add(entry);
                }
                data["layer_count"] = layerInfo.Count;
                data["layers"] = layerInfo;
            }

            if (parse.Errors.Count > 0) data["errors"] = ToJsonArray(parse.Errors);
            if (parse.Warnings.Count > 0) data["warnings"] = ToJsonArray(parse.Warnings);

            if (!parse.Ok)
                return Response.Error(L.T("Instruction list is invalid.", "指令表无效。"), data);

            return Response.Success(
                L.T($"Instruction list is valid: {parse.Spec.Layers.Count} layer(s), {parse.Spec.Width}x{parse.Spec.Height}.",
                    $"指令表有效：{parse.Spec.Layers.Count} 个图层，{parse.Spec.Width}x{parse.Spec.Height}。"), data);
        }

        [ToolAction("spec",
            DescriptionEn = "Return the full instruction grammar: canvas options, layer fields, enums, units and coordinate system.",
            DescriptionZh = "返回完整指令语法：画布选项、图层字段、枚举、单位与坐标系。",
            Parameters = new string[0],
            Inject = new[] { "action=spec" })]
        private object ActionSpec(JsonClass args)
        {
            var data = new JsonClass();
            data["canvas"] = CanvasGrammar();
            data["layer"] = LayerGrammar();
            data["coordinate_system"] = new JsonData(L.T(
                "Y grows downward. With origin=topLeft the canvas spans [0,width]x[0,height] and (0,0) is the top-left corner. " +
                "With origin=center the canvas spans [-w/2,w/2]x[-h/2,h/2] and (0,0) is the middle. All lengths are in pixels at the final (non-supersampled) resolution.",
                "Y 向下增长。origin=topLeft 时画布覆盖 [0,width]x[0,height]，(0,0) 为左上角；origin=center 时覆盖 [-w/2,w/2]x[-h/2,h/2]，(0,0) 为正中。所有长度均为最终（非超采样）分辨率下的像素。"));
            data["layers"] = new JsonData(L.T(
                "Layers are drawn in array order, each composited over the accumulated result. Put glows and shadows before their subject, highlights after.",
                "图层按数组顺序绘制，逐层叠加到已有结果之上。辉光/阴影放在主体之前，高光放在之后。"));
            data["tips"] = ToJsonArray(new List<string>
            {
                L.T("Alpha is supported everywhere; default canvas background is fully transparent.",
                    "全流程支持 Alpha；画布底色默认完全透明。"),
                L.T("blend \"mask\" keeps the image only inside that shape; blend \"erase\" punches a hole. Neither paints colour.",
                    "blend \"mask\" 只保留该形状内部的图像；blend \"erase\" 在该形状内挖洞。两者都不绘制颜色。"),
                L.T("fill \"radial\" picks colours from color to edgeColor (or a gradient) over the shape's radius.",
                    "fill \"radial\" 沿形状半径从 color 渐变到 edgeColor（或按 gradient 取值）。"),
                L.T("Everything is deterministic: the same instructions plus the same seed always produce identical pixels.",
                    "完全确定性：相同指令加相同 seed 永远产生相同像素。"),
                L.T("For a normal map, draw a greyscale/alpha height field and set normal_map=true (or call normal_from_image).",
                    "做法线贴图时，先绘制灰度/Alpha 高度场，再设 normal_map=true（或调用 normal_from_image）。"),
                L.T("Set tiling=true and the noise/pattern fills wrap at the border, so the PNG repeats without a seam. Draw the pattern layer as a full-canvas rect.",
                    "设 tiling=true 后噪波/图案填充会在边界闭合，PNG 可无缝重复。图案图层画成覆盖全画布的 rect 即可。"),
                L.T("fill \"pattern\" draws masonry/fabric: brick, plank, tile, checker, stripe, hex, scale, weave, dot, cobble. Give it cell, gap, gapColor, stagger, variation.",
                    "fill \"pattern\" 画砖石/织物：brick、plank、tile、checker、stripe、hex、scale、weave、dot、cobble。配 cell、gap、gapColor、stagger、variation 使用。"),
                L.T("Add \"warp\" to any layer to bend its fill: stripe + warp = wood grain, noise + warp = marble. Syntax: {\"noise\":\"fbm\",\"scale\":70,\"amount\":26}.",
                    "给任意图层加 \"warp\" 可弯折填充：stripe + warp = 木纹，noise + warp = 大理石纹。写法：{\"noise\":\"fbm\",\"scale\":70,\"amount\":26}。"),
                L.T("texgen.pbr turns one instruction list into BaseColor/Normal/Height/Roughness/AO/ORM in a single call — use it for any material rather than exporting channels one by one.",
                    "texgen.pbr 一次把指令表转成 BaseColor/Normal/Height/Roughness/AO/ORM，做材质时用它而不是逐张导出。"),
                L.T("texgen.templates lists 24+ ready surface recipes (grass, brick, wood, marble, metal, UI panels); pass a template's spec straight into render or pbr.",
                    "texgen.templates 列出 24+ 个现成表面配方（草地、砖墙、木纹、大理石、金属、UI 面板）；把模板的 spec 直接传给 render 或 pbr 即可。")
            });

            return Response.Success(L.T("Texture generation instruction grammar.", "程序化贴图指令语法。"), data);
        }

        [ToolAction("examples",
            DescriptionEn = "Return ready-to-use instruction lists (magic orb, skill icon, noise height field, star burst).",
            DescriptionZh = "返回可直接使用的指令表示例（魔法球、技能图标、噪波高度场、星爆）。",
            Parameters = new string[0],
            Inject = new[] { "action=examples" })]
        private object ActionExamples(JsonClass args)
        {
            var data = new JsonClass();

            data["magic_orb"] = Sample(L.T(
                "Blue magic orb: outer glow, radial orb, rim ring, cross flare, core highlight",
                "蓝色魔法球：外发光、径向球体、边缘圆环、十字光效、中心高光"), TexGenSamples.MagicOrb);

            data["skill_icon"] = Sample(L.T(
                "Skill icon: gradient plate, rounded cut-out frame, ring, chevron glyph",
                "技能图标：渐变底板、圆角镂空边框、圆环、折角图形"), TexGenSamples.SkillIcon);

            data["noise_height"] = Sample(L.T(
                "Fractal noise height field (good input for normal_map)",
                "分形噪波高度场（适合作为 normal_map 的输入）"), TexGenSamples.Noise);

            data["star_burst"] = Sample(L.T(
                "Six-point star burst with halo and spark",
                "六角星爆，带光晕与高光点"), TexGenSamples.Star);

            return Response.Success(
                L.T("4 ready-made instruction lists; pass an object's value straight into texgen.render as the whole argument set.",
                    "4 个现成指令表；把其中某个对象的值整体作为参数传给 texgen.render 即可。"), data);
        }

        private static JsonClass Sample(string title, string json)
        {
            var entry = new JsonClass();
            entry["title"] = title;
            entry["instructions"] = Json.Parse(json);
            return entry;
        }

        [ToolAction("preview",
            DescriptionEn = "Render an instruction list and open it in the TexGen preview window for interactive inspection.",
            DescriptionZh = "渲染指令表并在 TexGen 预览窗口中打开，便于交互查看。",
            Parameters = new[] { "layers", "layers_json", "width", "height", "background", "origin", "supersample", "normal_map", "normal_strength" },
            Inject = new[] { "action=preview" })]
        private object ActionPreview(JsonClass args)
        {
            var parse = ParseArguments(args);
            if (!parse.Ok) return ValidationError(parse);

            Texture2D tex = TgRasterizer.Render(parse.Spec, out int ss);
            TexGenPreviewWindow.Show(parse.Spec, tex);

            var data = new JsonClass();
            data["width"] = tex.width;
            data["height"] = tex.height;
            data["supersample"] = ss;
            data["layers"] = parse.Spec.Layers.Count;

            return Response.Success(
                L.T($"Rendered {tex.width}x{tex.height} (SSAA x{ss}) and opened the preview window.",
                    $"已渲染 {tex.width}x{tex.height}（超采样 x{ss}）并打开预览窗口。"), data);
        }

        [ToolAction("normal_from_image",
            DescriptionEn = "Convert an existing PNG into a tangent-space normal map and write it to disk.",
            DescriptionZh = "把已有 PNG 转为切线空间法线贴图并写入磁盘。",
            Parameters = new[] { "image_path", "output_path", "normal_strength", "overwrite", "texture_type", "filter_mode" },
            Required = new[] { "image_path" },
            Inject = new[] { "action=normal_from_image" })]
        private object ActionNormalFromImage(JsonClass args)
        {
            string imagePath = TgJson.GetString(args, "image_path");
            if (string.IsNullOrEmpty(imagePath))
                return Response.Error(L.T("'image_path' is required.", "'image_path' 必填。"));

            string source = ResolvePath(imagePath, false);
            if (!File.Exists(source))
                return Response.Error(L.T($"Source PNG not found: {source}", $"源 PNG 不存在：{source}"));

            var bytes = File.ReadAllBytes(source);
            var sourceTex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!sourceTex.LoadImage(bytes))
            {
                UnityEngine.Object.DestroyImmediate(sourceTex);
                return Response.Error(L.T("Source file is not a readable image.", "源文件不是可读图像。"));
            }

            var spec = new TgSpec
            {
                Width = sourceTex.width,
                Height = sourceTex.height,
                Background = Color.black,
                NormalMap = true,
                NormalStrength = TgJson.GetFloat(args, "normal_strength", 4f),
                Supersample = 1
            };

            // Feed the source pixels through the normal-map stage.
            var pixels = sourceTex.GetPixels();
            float[] rgba = new float[pixels.Length * 4];
            for (int i = 0; i < pixels.Length; i++)
            {
                rgba[i * 4] = pixels[i].r;
                rgba[i * 4 + 1] = pixels[i].g;
                rgba[i * 4 + 2] = pixels[i].b;
                rgba[i * 4 + 3] = pixels[i].a;
            }
            UnityEngine.Object.DestroyImmediate(sourceTex);

            string outputPath = TgJson.GetString(args, "output_path");
            if (string.IsNullOrEmpty(outputPath))
                outputPath = Path.ChangeExtension(imagePath, null) + "_n.png";

            Texture2D normal = TgRasterizer.NormalMapFromRgba(rgba, spec.Width, spec.Height, spec.NormalStrength);

            string absolute = ResolvePath(outputPath, true);
            if (!TgJson.GetBool(args, "overwrite", true) && File.Exists(absolute))
                return Response.Error(L.T($"File already exists: {absolute}", $"文件已存在：{absolute}"));

            TgRasterizer.WritePng(absolute, TgRasterizer.EncodePng(normal));
            UnityEngine.Object.DestroyImmediate(normal);

            ImportSingleAsset(absolute);
            ApplyImporterSettings(absolute, args, true);
            if (TgJson.GetBool(args, "select_asset", true)) SelectAsset(absolute);

            var data = new JsonClass();
            data["path"] = outputPath;
            data["width"] = spec.Width;
            data["height"] = spec.Height;
            data["strength"] = spec.NormalStrength;

            return Response.Success(
                L.T($"Normal map written to '{outputPath}'.", $"法线贴图已写入 '{outputPath}'。"), data);
        }

        // ------------------------------------------------------------------
        // PBR derivation
        // ------------------------------------------------------------------

        [ToolAction("pbr",
            DescriptionEn = "Render one instruction list once and derive a full PBR set from it: BaseColor, Normal, Height, Roughness, AO and a packed ORM map (R=AO, G=Roughness, B=Metallic).",
            DescriptionZh = "一次渲染指令表，派生整套 PBR 贴图：BaseColor、Normal、Height、Roughness、AO 与打包 ORM（R=AO, G=粗糙度, B=金属度）。",
            Parameters = new[]
            {
                "layers", "layers_json", "width", "height", "background", "origin", "supersample", "tiling",
                "output_path", "output_dir", "prefix", "overwrite", "select_asset",
                "normal_strength", "roughness_bias", "roughness_contrast", "ao_strength", "ao_radius",
                "metallic", "material_path"
            },
            Inject = new[] { "action=pbr" })]
        private object ActionPbr(JsonClass args)
        {
            var parse = ParseArguments(args);
            if (!parse.Ok) return ValidationError(parse);

            TgSpec spec = parse.Spec;
            // The source pass must be the albedo/height image, never a normal map.
            spec.NormalMap = false;
            spec.Tiling = spec.Tiling || TgJson.GetBool(args, "tiling", false);

            Texture2D source;
            int ss;
            try { source = TgRasterizer.Render(spec, out ss); }
            catch (Exception e) { return Response.Error(L.T("Render failed: ", "渲染失败：") + e.Message); }

            int w = source.width;
            int h = source.height;
            Color[] pixels = source.GetPixels();
            UnityEngine.Object.DestroyImmediate(source);

            // Height: luminance with alpha folded in, so transparent areas read as low.
            var height = new float[w * h];
            for (int i = 0; i < height.Length; i++)
            {
                Color c = pixels[i];
                height[i] = Mathf.Clamp01((0.299f * c.r + 0.587f * c.g + 0.114f * c.b) * c.a);
            }

            float bias = TgJson.GetFloat(args, "roughness_bias", 0.55f);
            float contrast = TgJson.GetFloat(args, "roughness_contrast", 0.6f);
            float aoStrength = TgJson.GetFloat(args, "ao_strength", 0.7f);
            int aoRadius = Mathf.Clamp(TgJson.GetInt(args, "ao_radius", 6), 1, 64);
            float metallic = Mathf.Clamp01(TgJson.GetFloat(args, "metallic", 0f));
            float normalStrength = TgJson.GetFloat(args, "normal_strength", 4f);

            float[] ao = BuildOcclusion(height, w, h, aoRadius, aoStrength);
            float[] rough = new float[w * h];
            for (int i = 0; i < rough.Length; i++)
                rough[i] = Mathf.Clamp01(bias + (0.5f - height[i]) * contrast);

            // Output paths: honour output_path as the base name when given.
            string basePath = TgJson.GetString(args, "output_path");
            string baseName;
            string outputDir;
            if (!string.IsNullOrEmpty(basePath))
            {
                baseName = Path.GetFileNameWithoutExtension(basePath);
                outputDir = Path.GetDirectoryName(basePath)?.Replace('\\', '/') ?? "Assets/TexGen";
            }
            else
            {
                outputDir = (TgJson.GetString(args, "output_dir", "Assets/TexGen")).TrimEnd('/', '\\');
                baseName = TgJson.GetString(args, "prefix", "");
                if (string.IsNullOrEmpty(baseName)) baseName = "texgen";
                if (baseName.EndsWith("_")) baseName = baseName.TrimEnd('_');
            }

            bool overwrite = TgJson.GetBool(args, "overwrite", true);
            var channels = new JsonArray();

            AssetDatabase.DisallowAutoRefresh();
            try
            {
                var baseTex = TgRasterizer.TextureFromColors(pixels, w, h);
                string baseFile = WriteChannel(outputDir, baseName + "_BaseColor", baseTex, overwrite,
                    TextureImporterType.Default, false, true);
                UnityEngine.Object.DestroyImmediate(baseTex);
                if (baseFile == null) return Response.Error(L.T("Cannot write the BaseColor channel.",
                    "无法写出 BaseColor 通道。"));
                channels.Add(ChannelEntry("BaseColor", baseFile, "albedo, sRGB"));

                float[] heightRgba = PackGrey(height);
                var heightTex = TgRasterizer.TextureFromColors(heightRgba, w, h);
                string heightFile = WriteChannel(outputDir, baseName + "_Height", heightTex, overwrite,
                    TextureImporterType.Default, true, true);
                UnityEngine.Object.DestroyImmediate(heightTex);
                if (heightFile != null) { channels.Add(ChannelEntry("Height", heightFile, "linear height field")); }

                var normalTex = TgRasterizer.NormalMapFromRgba(heightRgba, w, h, normalStrength);
                string normalFile = WriteChannel(outputDir, baseName + "_Normal", normalTex, overwrite,
                    TextureImporterType.NormalMap, true, true);
                UnityEngine.Object.DestroyImmediate(normalTex);
                if (normalFile != null) { channels.Add(ChannelEntry("Normal", normalFile, "tangent space, linear")); }

                var roughTex = TgRasterizer.TextureFromColors(PackGrey(rough), w, h);
                string roughFile = WriteChannel(outputDir, baseName + "_Roughness", roughTex, overwrite,
                    TextureImporterType.Default, true, true);
                UnityEngine.Object.DestroyImmediate(roughTex);
                if (roughFile != null) { channels.Add(ChannelEntry("Roughness", roughFile, "linear greyscale")); }

                var aoTex = TgRasterizer.TextureFromColors(PackGrey(ao), w, h);
                string aoFile = WriteChannel(outputDir, baseName + "_AO", aoTex, overwrite,
                    TextureImporterType.Default, true, true);
                UnityEngine.Object.DestroyImmediate(aoTex);
                if (aoFile != null) { channels.Add(ChannelEntry("AO", aoFile, "linear greyscale")); }

                var orm = new float[w * h * 4];
                for (int i = 0; i < w * h; i++)
                {
                    orm[i * 4 + 0] = ao[i];
                    orm[i * 4 + 1] = rough[i];
                    orm[i * 4 + 2] = metallic;
                    orm[i * 4 + 3] = 1f;
                }
                var ormTex = TgRasterizer.TextureFromColors(orm, w, h);
                string ormFile = WriteChannel(outputDir, baseName + "_ORM", ormTex, overwrite,
                    TextureImporterType.Default, true, true);
                UnityEngine.Object.DestroyImmediate(ormTex);
                if (ormFile != null) { channels.Add(ChannelEntry("ORM", ormFile, "R=AO G=Roughness B=Metallic")); }

                // Optional: bind the albedo + normal straight onto a material.
                string materialPath = TgJson.GetString(args, "material_path");
                string boundMaterial = null;
                if (!string.IsNullOrEmpty(materialPath))
                {
                    boundMaterial = BindMaterial(materialPath, baseFile, normalFile);
                }

                var data = new JsonClass();
                data["width"] = w;
                data["height"] = h;
                data["supersample"] = ss;
                data["tiling"] = spec.Tiling;
                data["layers"] = spec.Layers.Count;
                data["channels"] = channels;
                data["count"] = channels.Count;
                if (boundMaterial != null) data["material"] = boundMaterial;
                if (TgJson.GetBool(args, "select_asset", true))
                    SelectAsset(baseFile);

                return Response.Success(
                    L.T($"Derived {channels.Count} PBR maps into '{outputDir}'.",
                        $"已在 '{outputDir}' 派生 {channels.Count} 张 PBR 贴图。"), data);
            }
            finally
            {
                AssetDatabase.AllowAutoRefresh();
            }
        }

        private static JsonClass ChannelEntry(string channel, string path, string note)
        {
            var entry = new JsonClass();
            entry["channel"] = channel;
            entry["path"] = path;
            entry["note"] = note;
            return entry;
        }

        /// <summary>Pack a scalar field into an opaque greyscale RGBA buffer.</summary>
        private static float[] PackGrey(float[] values)
        {
            var rgba = new float[values.Length * 4];
            for (int i = 0; i < values.Length; i++)
            {
                float v = Mathf.Clamp01(values[i]);
                rgba[i * 4 + 0] = v;
                rgba[i * 4 + 1] = v;
                rgba[i * 4 + 2] = v;
                rgba[i * 4 + 3] = 1f;
            }
            return rgba;
        }

        /// <summary>
        /// Cheap crevice occlusion: for each pixel, compare its height against the
        /// tallest neighbour sampled along eight directions.
        /// </summary>
        private static float[] BuildOcclusion(float[] height, int w, int h, int radius, float strength)
        {
            var ao = new float[height.Length];
            int[] dirX = { 1, -1, 0, 0, 1, 1, -1, -1 };
            int[] dirY = { 0, 0, 1, -1, 1, -1, 1, -1 };
            int near = Mathf.Max(1, radius / 2);

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    float self = height[i];
                    float tallest = self;

                    for (int d = 0; d < 8; d++)
                    {
                        for (int step = 0; step < 2; step++)
                        {
                            int dist = step == 0 ? near : radius;
                            int sx = x + dirX[d] * dist;
                            int sy = y + dirY[d] * dist;
                            if (sx < 0 || sy < 0 || sx >= w || sy >= h) continue;
                            float s = height[sy * w + sx];
                            if (s > tallest) tallest = s;
                        }
                    }

                    ao[i] = Mathf.Clamp01(1f - (tallest - self) * strength);
                }
            }

            return ao;
        }

        /// <summary>Write one derived map, import it and give it the right importer settings.</summary>
        private string WriteChannel(string outputDir, string fileName, Texture2D tex, bool overwrite,
            TextureImporterType type, bool linear, bool repeat)
        {
            string assetPath = (outputDir + "/" + SafeFileName(fileName) + ".png").Replace('\\', '/');
            string absolute = ResolvePath(assetPath, true);
            if (!overwrite && File.Exists(absolute)) return null;

            try { TgRasterizer.WritePng(absolute, TgRasterizer.EncodePng(tex)); }
            catch (Exception e)
            {
                Debug.LogWarning("[TexGen] failed to write " + absolute + ": " + e.Message);
                return null;
            }

            if (!IsUnderAssets(absolute)) return assetPath;

            ImportSingleAsset(absolute);

            string unityPath = "Assets" + absolute.Substring(Application.dataPath.Length).Replace('\\', '/');
            var importer = AssetImporter.GetAtPath(unityPath) as TextureImporter;
            if (importer != null)
            {
                importer.textureType = type;
                if (type != TextureImporterType.NormalMap) importer.sRGBTexture = !linear;
                importer.wrapMode = repeat ? TextureWrapMode.Repeat : TextureWrapMode.Clamp;
                importer.filterMode = FilterMode.Bilinear;
                EditorUtility.SetDirty(importer);
                importer.SaveAndReimport();
            }

            return assetPath;
        }

        /// <summary>Attach albedo and normal to a material, matching whichever shader it uses.</summary>
        private string BindMaterial(string materialPath, string baseColorPath, string normalPath)
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (mat == null) return null;

            var baseTex = AssetDatabase.LoadAssetAtPath<Texture2D>(baseColorPath);
            var normalTex = string.IsNullOrEmpty(normalPath) ? null : AssetDatabase.LoadAssetAtPath<Texture2D>(normalPath);

            if (baseTex != null)
            {
                if (mat.HasProperty("_MainTex")) mat.SetTexture("_MainTex", baseTex);
                if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", baseTex);
            }
            if (normalTex != null && mat.HasProperty("_BumpMap"))
            {
                mat.SetTexture("_BumpMap", normalTex);
                mat.EnableKeyword("_NORMALMAP");
            }

            EditorUtility.SetDirty(mat);
            AssetDatabase.SaveAssets();
            return materialPath;
        }

        // ------------------------------------------------------------------
        // Template library
        // ------------------------------------------------------------------

        [ToolAction("templates",
            DescriptionEn = "List the built-in template library (materials, patterns, nature, UI). Filter by tag, or fetch one template's ready-to-render spec by name.",
            DescriptionZh = "列出内置模板库（材质、图案、自然、UI）。可按标签筛选，或按名称取单个模板的现成 spec。",
            Parameters = new[] { "tag", "name", "include_spec" },
            Inject = new[] { "action=templates" })]
        private object ActionTemplates(JsonClass args)
        {
            string tag = TgJson.GetString(args, "tag");
            string wanted = TgJson.GetString(args, "name");
            // A full listing would blow past the response size cap, so specs are
            // only attached when one template is asked for by name.
            bool includeSpec = TgJson.GetBool(args, "include_spec", !string.IsNullOrEmpty(wanted));
            var loaded = LoadTemplates();

            if (loaded.Count == 0)
                return Response.Error(L.T(
                    "Template folder not found: Assets/Extends/CustomTools/TexGen/Templates",
                    "未找到模板目录：Assets/Extends/CustomTools/TexGen/Templates"));

            var result = new JsonArray();
            foreach (JsonClass tpl in loaded)
            {
                string name = TgJson.GetString(tpl, "name");
                if (!string.IsNullOrEmpty(wanted) && !string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!string.IsNullOrEmpty(tag) && !HasTag(tpl, tag)) continue;

                var entry = new JsonClass();
                entry["name"] = name;
                entry["title"] = TgJson.GetString(tpl, "title", name);
                entry["tags"] = TgJson.Get(tpl, "tags") ?? new JsonArray();
                JsonNode spec = TgJson.Get(tpl, "spec");
                if (includeSpec)
                {
                    entry["spec"] = spec;
                }
                else
                {
                    var specObj = spec as JsonClass;
                    int w = specObj != null ? TgJson.GetInt(specObj, "width", 0) : 0;
                    int h = specObj != null ? TgJson.GetInt(specObj, "height", 0) : 0;
                    bool tiles = specObj != null && TgJson.GetBool(specObj, "tiling", false);
                    entry["size"] = w + "x" + h;
                    entry["tiling"] = tiles;
                }
                result.Add(entry);
            }

            if (result.Count == 0)
                return Response.Error(L.T(
                    $"No template matched tag='{tag}' name='{wanted}'.",
                    $"没有匹配 tag='{tag}' name='{wanted}' 的模板。"));

            var data = new JsonClass();
            data["count"] = result.Count;
            data["templates"] = result;
            if (!string.IsNullOrEmpty(wanted))
                data["usage"] = new JsonData(L.T(
                    "Pass the template's spec object straight into texgen.render (it already contains width/height/tiling/layers).",
                    "把模板的 spec 对象整体传给 texgen.render 即可（其中已含 width/height/tiling/layers）。"));

            return Response.Success(
                L.T($"{result.Count} template(s).", $"{result.Count} 个模板。"), data);
        }

        private static bool HasTag(JsonClass template, string tag)
        {
            var tags = TgJson.Get(template, "tags") as JsonArray;
            if (tags == null) return false;
            foreach (JsonNode t in tags)
            {
                if (t != null && string.Equals(t.Value, tag, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static List<JsonClass> LoadTemplates()
        {
            var list = new List<JsonClass>();
            const string folder = "Assets/Extends/CustomTools/TexGen/Templates";
            string[] guids = AssetDatabase.FindAssets("t:TextAsset", new[] { folder });
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;

                var asset = AssetDatabase.LoadAssetAtPath<TextAsset>(path);
                if (asset == null) continue;

                try
                {
                    var node = Json.Parse(asset.text) as JsonClass;
                    if (node != null && TgJson.Get(node, "spec") != null) list.Add(node);
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[TexGen] template " + path + " is not valid JSON: " + e.Message);
                }
            }

            list.Sort((a, b) => string.CompareOrdinal(
                TgJson.GetString(a, "name", ""), TgJson.GetString(b, "name", "")));
            return list;
        }

        // ------------------------------------------------------------------
        // Mesh-aware actions
        // ------------------------------------------------------------------

        [ToolAction("uv_info",
            DescriptionEn = "Inspect a mesh's UV layout: regions, pixel rectangles, coverage and warnings, before drawing.",
            DescriptionZh = "查看 Mesh 的 UV 布局：分区、像素矩形、覆盖率与警告，供绘制前参考。",
            Parameters = new[] { "mesh_path", "width", "height", "group_by" },
            Required = new[] { "mesh_path" },
            Inject = new[] { "action=uv_info" })]
        private object ActionUvInfo(JsonClass args)
        {
            string meshPath = TgJson.GetString(args, "mesh_path");
            Mesh mesh;
            string loadError = TryLoadMesh(meshPath, out mesh);
            if (loadError != null) return Response.Error(loadError);

            int width = Mathf.Clamp(TgJson.GetInt(args, "width", 512), 1, TgSpec.MaxDimension);
            int height = Mathf.Clamp(TgJson.GetInt(args, "height", 512), 1, TgSpec.MaxDimension);
            string groupBy = TgJson.GetString(args, "group_by", "submesh");

            TgUvLayout layout;
            try { layout = TgMeshUv.Analyze(mesh, width, height, groupBy); }
            catch (Exception e)
            {
                return Response.Error(L.T("Cannot analyse mesh UVs: ", "无法分析 Mesh UV：") + e.Message);
            }

            var data = BuildLayoutData(layout, groupBy);
            data["path"] = meshPath;
            data["mesh"] = mesh.name;

            return Response.Success(
                L.T($"UV layout of '{mesh.name}': {layout.Regions.Count} region(s), {layout.CoverageRatio:P1} covered.",
                    $"'{mesh.name}' 的 UV 布局：{layout.Regions.Count} 个分区，覆盖率 {layout.CoverageRatio:P1}。"), data);
        }

        [ToolAction("for_mesh",
            DescriptionEn = "Draw instruction lists into a mesh's UV space: per-submesh/island regions, UV coverage mask and seam padding. Optionally assign the PNG to a material.",
            DescriptionZh = "把指令表按 Mesh 的 UV 空间绘制：支持 submesh/UV 岛分区、UV 覆盖遮罩与接缝外扩，可选自动挂到材质。",
            Parameters = new[]
            {
                "mesh_path", "layers", "layers_json", "regions", "regions_json", "group_by", "padding", "fill_mode",
                "width", "height", "background", "origin", "supersample", "output_path", "overwrite",
                "texture_type", "filter_mode", "wrap_mode", "max_size", "select_asset",
                "material_path", "texture_property"
            },
            Required = new[] { "mesh_path" },
            Inject = new[] { "action=for_mesh" })]
        private object ActionForMesh(JsonClass args)
        {
            string meshPath = TgJson.GetString(args, "mesh_path");
            Mesh mesh;
            string loadError = TryLoadMesh(meshPath, out mesh);
            if (loadError != null) return Response.Error(loadError);

            int width = Mathf.Clamp(TgJson.GetInt(args, "width", 512), 1, TgSpec.MaxDimension);
            int height = Mathf.Clamp(TgJson.GetInt(args, "height", 512), 1, TgSpec.MaxDimension);
            string groupBy = TgJson.GetString(args, "group_by", "submesh");
            int padding = Mathf.Clamp(TgJson.GetInt(args, "padding", 4), 0, 64);

            TgUvLayout layout;
            try { layout = TgMeshUv.Analyze(mesh, width, height, groupBy); }
            catch (Exception e)
            {
                return Response.Error(L.T("Cannot analyse mesh UVs: ", "无法分析 Mesh UV：") + e.Message);
            }

            // Global instruction list + optional per-region overrides.
            JsonNode globalLayers;
            try { globalLayers = ResolveLayers(args); }
            catch (Exception e) { return Response.Error(L.T("Invalid layers_json: ", "layers_json 无效：") + e.Message); }

            JsonNode regionsNode = TgJson.Get(args, "regions");
            if (regionsNode == null)
            {
                string regionsJson = TgJson.GetString(args, "regions_json");
                if (!string.IsNullOrEmpty(regionsJson))
                {
                    try { regionsNode = Json.Parse(regionsJson); }
                    catch (Exception e) { return Response.Error(L.T("Invalid regions_json: ", "regions_json 无效：") + e.Message); }
                }
            }
            JsonArray regionArray = regionsNode as JsonArray;
            if (regionsNode != null && regionArray == null)
                return Response.Error(L.T("'regions' must be an array of {id, layers:[...]}.", "'regions' 必须是 [{id, layers:[...]}] 数组。"));

            var overrides = new Dictionary<int, JsonNode>();
            if (regionArray != null)
            {
                for (int i = 0; i < regionArray.Count; i++)
                {
                    JsonClass item = regionArray[i] as JsonClass;
                    if (item == null)
                        return Response.Error(L.T("Every region entry must be an object.", "regions 的每项必须是对象。"));
                    int id = TgJson.GetInt(item, "id", i);
                    JsonNode layers;
                    try { layers = ResolveLayers(item); }
                    catch (Exception e) { return Response.Error("region " + id + ": " + e.Message); }
                    if (layers != null) overrides[id] = layers;
                }
            }

            TgSpec baseSpec = null;
            if (globalLayers != null)
            {
                var parsed = ParseSpecWithLayers(args, globalLayers, width, height);
                if (!parsed.Ok) return ValidationError(parsed);
                baseSpec = parsed.Spec;
            }

            var specs = new Dictionary<int, TgSpec>();
            var problems = new List<string>();
            foreach (TgUvRegion region in layout.Regions)
            {
                JsonNode layers;
                if (overrides.TryGetValue(region.Id, out layers) && layers != null)
                {
                    var parsed = ParseSpecWithLayers(args, layers, width, height);
                    if (!parsed.Ok) problems.Add(region.Name + ": " + string.Join("; ", parsed.Errors.ToArray()));
                    else specs[region.Id] = parsed.Spec;
                }
                else if (baseSpec != null)
                {
                    specs[region.Id] = baseSpec;
                }
                else
                {
                    problems.Add(region.Name + L.T(" has no layers; pass global layers or a regions entry.",
                        " 没有指令表；请传全局 layers 或 regions 中的对应项。"));
                }
            }
            if (problems.Count > 0)
            {
                var problemData = new JsonClass();
                problemData["errors"] = ToJsonArray(problems);
                return Response.Error(L.T("Some regions have no valid instruction list.", "部分分区没有有效指令表。"),
                    problemData);
            }

            if (baseSpec == null) baseSpec = specs[layout.Regions[0].Id];

            // Render one canvas per distinct spec (the global one is shared).
            var pixels = new Dictionary<int, Color32[]>();
            var canvasCache = new Dictionary<TgSpec, Color32[]>();
            int renderedCanvases = 0;
            foreach (var pair in specs)
            {
                Color32[] buffer;
                if (!canvasCache.TryGetValue(pair.Value, out buffer))
                {
                    Texture2D rendered;
                    int ss;
                    try { rendered = TgRasterizer.Render(pair.Value, out ss); }
                    catch (Exception e)
                    {
                        return Response.Error(L.T("Render failed: ", "渲染失败：") + e.Message);
                    }
                    buffer = rendered.GetPixels32();
                    UnityEngine.Object.DestroyImmediate(rendered);
                    if (buffer.Length != width * height)
                        return Response.Error(L.T("Rendered canvas size does not match the UV mask.",
                            "渲染画布尺寸与 UV 遮罩不一致。"));
                    canvasCache[pair.Value] = buffer;
                    renderedCanvases++;
                }
                pixels[pair.Key] = buffer;
            }

            string fillMode = TgJson.GetString(args, "fill_mode", "background");
            bool fillNearest = fillMode.Equals("nearest", StringComparison.OrdinalIgnoreCase);
            Texture2D composed = TgMeshUv.Compose(layout, pixels, baseSpec.Background, padding, fillNearest);
            byte[] png = TgRasterizer.EncodePng(composed);

            string outputPath = TgJson.GetString(args, "output_path");
            if (string.IsNullOrEmpty(outputPath))
                outputPath = "Assets/Textures/" + SafeFileName(mesh.name) + "_uv.png";

            // Unity's file watcher refreshes the whole AssetDatabase the moment a PNG
            // under Assets/ is overwritten. That refresh can reload the domain on the
            // very main thread that writes the MCP response, which is why clients used
            // to see "invalid JSON-RPC response" although every side effect succeeded.
            AssetDatabase.DisallowAutoRefresh();
            try
            {
                string absolute = ResolvePath(outputPath, true);
                if (!TgJson.GetBool(args, "overwrite", true) && File.Exists(absolute))
                {
                    UnityEngine.Object.DestroyImmediate(composed);
                    return Response.Error(L.T($"File already exists: {absolute}", $"文件已存在：{absolute}"));
                }

                try { TgRasterizer.WritePng(absolute, png); }
                catch (Exception e)
                {
                    UnityEngine.Object.DestroyImmediate(composed);
                    return Response.Error(L.T($"Failed to write '{absolute}': {e.Message}",
                        $"写入 '{absolute}' 失败：{e.Message}"));
                }
                UnityEngine.Object.DestroyImmediate(composed);

                bool underAssets = IsUnderAssets(absolute);
                if (underAssets)
                {
                    // Import only this file (no project-wide Refresh).
                    ImportSingleAsset(absolute);
                    ApplyImporterSettings(absolute, args, false);
                    if (TgJson.GetBool(args, "select_asset", false)) SelectAsset(absolute);
                }

                var data = BuildLayoutData(layout, groupBy);
                data["path"] = outputPath;
                data["mesh"] = mesh.name;
                data["padding"] = padding;
                data["fill_mode"] = fillNearest ? "nearest" : "background";
                data["rendered_canvases"] = renderedCanvases;
                data["bytes"] = png.Length;
                data["written"] = true;

                var regionInfo = data["regions"] as JsonArray;
                if (regionInfo != null)
                {
                    for (int i = 0; i < regionInfo.Count; i++)
                    {
                        var entry = regionInfo[i] as JsonClass;
                        if (entry == null) continue;
                        entry["custom_layers"] = overrides.ContainsKey(layout.Regions[i].Id);
                    }
                }

                string materialPath = TgJson.GetString(args, "material_path");
                if (!string.IsNullOrEmpty(materialPath))
                {
                    string assignError = AssignTextureToMaterial(absolute, materialPath,
                        TgJson.GetString(args, "texture_property", "_MainTex"));
                    if (assignError != null)
                    {
                        // The PNG is already written; report the assignment failure without losing it.
                        data["material_error"] = assignError;
                        layout.Warnings.Add(assignError);
                        data["warnings"] = ToJsonArray(layout.Warnings);
                    }
                    else
                    {
                        data["material_assigned"] = materialPath + "." +
                            TgJson.GetString(args, "texture_property", "_MainTex");
                    }
                }
                else
                {
                    data["material_assigned"] = false;
                }

                if (!underAssets)
                    layout.Warnings.Add(L.T("PNG is outside Assets/, it was not imported and cannot be assigned to a material.",
                        "PNG 位于 Assets/ 之外，未导入为资源，也无法挂到材质。"));
                if (layout.Warnings.Count > 0) data["warnings"] = ToJsonArray(layout.Warnings);

                return Response.Success(
                    L.T($"Painted {layout.Regions.Count} UV region(s) of '{mesh.name}' into '{outputPath}' ({layout.CoverageRatio:P1} covered).",
                        $"已把 '{mesh.name}' 的 {layout.Regions.Count} 个 UV 分区绘制到 '{outputPath}'（覆盖率 {layout.CoverageRatio:P1}）。"), data);
            }
            finally
            {
                // Restore on the NEXT editor update, after this HTTP response has
                // been written. AllowAutoRefresh() applies pending refreshes at
                // once, so calling it synchronously here would still reload the
                // domain before the client receives the reply. Each registration
                // pairs one DisallowAutoRefresh() call; a domain reload resets the
                // flag itself, so no state can leak.
                EditorApplication.delayCall += () => AssetDatabase.AllowAutoRefresh();
            }
        }

        // ------------------------------------------------------------------
        // Mesh-aware helpers
        // ------------------------------------------------------------------

        private static JsonClass BuildLayoutData(TgUvLayout layout, string groupBy)
        {
            var data = new JsonClass();
            data["width"] = layout.Width;
            data["height"] = layout.Height;
            data["group_by"] = groupBy;
            data["vertex_count"] = layout.VertexCount;
            data["triangle_count"] = layout.TriangleCount;
            data["submesh_count"] = layout.SubmeshCount;
            data["coverage"] = layout.CoverageRatio;
            data["covered_pixels"] = layout.CoveredPixels;
            data["out_of_range"] = layout.OutOfRange;
            data["coordinate_system"] = new JsonData(L.T(
                "Pixel (x,y) maps from UV (u,v) as x=u*width, y=v*height; the first canvas row is v=0. Use rect_px directly in layer geometry — no V flip needed.",
                "像素 (x,y) 与 UV (u,v) 对应 x=u*width、y=v*height，画布第一行为 v=0。直接用 rect_px 写图层几何即可，无需翻转 V。"));

            var min = new JsonArray();
            min.Add(new JsonData(layout.UvMin.x));
            min.Add(new JsonData(layout.UvMin.y));
            data["uv_min"] = min;
            var max = new JsonArray();
            max.Add(new JsonData(layout.UvMax.x));
            max.Add(new JsonData(layout.UvMax.y));
            data["uv_max"] = max;

            var regions = new JsonArray();
            foreach (TgUvRegion region in layout.Regions)
            {
                var entry = new JsonClass();
                entry["id"] = region.Id;
                entry["name"] = region.Name;
                entry["triangles"] = region.Triangles;
                entry["covered_pixels"] = region.CoveredPixels;
                var rect = new JsonClass();
                rect["x"] = region.PxX;
                rect["y"] = region.PxY;
                rect["w"] = region.PxW;
                rect["h"] = region.PxH;
                entry["rect_px"] = rect;
                var rmin = new JsonArray();
                rmin.Add(new JsonData(region.UvMin.x));
                rmin.Add(new JsonData(region.UvMin.y));
                entry["uv_min"] = rmin;
                var rmax = new JsonArray();
                rmax.Add(new JsonData(region.UvMax.x));
                rmax.Add(new JsonData(region.UvMax.y));
                entry["uv_max"] = rmax;
                regions.Add(entry);
            }
            data["regions"] = regions;
            data["region_count"] = layout.Regions.Count;
            if (layout.Warnings.Count > 0) data["warnings"] = ToJsonArray(layout.Warnings);
            return data;
        }

        private static JsonNode ResolveLayers(JsonClass node)
        {
            JsonNode layers = TgJson.Get(node, "layers");
            if (layers != null) return layers;
            string json = TgJson.GetString(node, "layers_json");
            if (string.IsNullOrEmpty(json)) return null;
            return Json.Parse(json);
        }

        private static TgParseResult ParseSpecWithLayers(JsonClass args, JsonNode layers, int width, int height)
        {
            var root = new JsonClass();
            root["layers"] = layers;
            CopyIfPresent(args, root, "background", "origin", "supersample");
            // Pin the canvas to the UV mask size. TgParser defaults to 256 while
            // the UV layout defaults to 512; leaving them independent would make
            // the rendered Color32[] and the mask index different pixels.
            root["width"] = width;
            root["height"] = height;
            return TgParser.Parse(root);
        }

        /// <summary>Resolve either a project asset path or a scene hierarchy path to a Mesh.</summary>
        private static string TryLoadMesh(string path, out Mesh mesh)
        {
            mesh = null;
            if (string.IsNullOrEmpty(path))
                return L.T("'mesh_path' is required (an Assets/... mesh or a scene GameObject path).",
                    "'mesh_path' 必填（Assets/... 下的 Mesh 资源，或场景中 GameObject 层级路径）。");

            string p = path.Replace('\\', '/');
            if (p.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                mesh = AssetDatabase.LoadAssetAtPath<Mesh>(p);
                if (mesh == null)
                {
                    var assets = AssetDatabase.LoadAllAssetsAtPath(p);
                    for (int i = 0; i < assets.Length; i++)
                    {
                        mesh = assets[i] as Mesh;
                        if (mesh != null) break;
                    }
                }
                if (mesh == null)
                    return L.T($"No Mesh found in '{p}'.", $"'{p}' 中未找到 Mesh。");
                return null;
            }

            GameObject go = GameObject.Find(p);
            if (go == null)
            {
                var all = Resources.FindObjectsOfTypeAll<GameObject>();
                for (int i = 0; i < all.Length; i++)
                {
                    if (all[i].name == p || HierarchyPath(all[i].transform) == p)
                    {
                        go = all[i];
                        break;
                    }
                }
            }
            if (go == null)
                return L.T($"GameObject '{p}' not found. Pass an Assets/... mesh path instead.",
                    $"未找到 GameObject '{p}'。也可改传 Assets/... 下的 Mesh 路径。");

            var filter = go.GetComponent<MeshFilter>();
            if (filter != null) mesh = filter.sharedMesh;
            if (mesh == null)
            {
                var skinned = go.GetComponent<SkinnedMeshRenderer>();
                if (skinned != null) mesh = skinned.sharedMesh;
            }
            if (mesh == null)
            {
                var childFilters = go.GetComponentsInChildren<MeshFilter>(true);
                for (int i = 0; i < childFilters.Length; i++)
                {
                    if (childFilters[i].sharedMesh != null) { mesh = childFilters[i].sharedMesh; break; }
                }
            }
            if (mesh == null)
                return L.T($"GameObject '{p}' has no MeshFilter / SkinnedMeshRenderer with a mesh.",
                    $"GameObject '{p}' 上没有带 Mesh 的 MeshFilter / SkinnedMeshRenderer。");
            return null;
        }

        private static string HierarchyPath(Transform t)
        {
            string path = t.name;
            while (t.parent != null)
            {
                t = t.parent;
                path = t.name + "/" + path;
            }
            return path;
        }

        private static string AssignTextureToMaterial(string absolutePng, string materialPath, string property)
        {
            if (!IsUnderAssets(absolutePng))
                return L.T("PNG is outside Assets/, cannot assign to a material.", "PNG 位于 Assets/ 之外，无法挂到材质。");

            string textureAssetPath = ToAssetPath(absolutePng);
            Texture2D texture = AssetDatabase.LoadAssetAtPath<Texture2D>(textureAssetPath);
            if (texture == null)
                return L.T($"Texture not imported at '{textureAssetPath}'.", $"'{textureAssetPath}' 尚未导入为贴图。");

            Material material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (material == null)
                return L.T($"Material not found at '{materialPath}'.", $"未找到材质 '{materialPath}'。");
            if (string.IsNullOrEmpty(property)) property = "_MainTex";
            if (!material.HasProperty(property))
                return L.T($"Material '{materialPath}' has no property '{property}'.",
                    $"材质 '{materialPath}' 没有属性 '{property}'。");

            material.SetTexture(property, texture);
            EditorUtility.SetDirty(material);
            AssetDatabase.SaveAssets();
            return null;
        }

        /// <summary>
        /// Import only the PNG that was just written. A project-wide AssetDatabase.Refresh()
        /// scans every asset and may start a script recompile / domain reload on Unity's
        /// main thread — the same thread that writes the MCP HTTP response. That is the
        /// root cause of "invalid JSON-RPC response" on write calls whose side effect
        /// had in fact succeeded. Importing one file avoids the reload entirely.
        /// </summary>
        private static void ImportSingleAsset(string absolutePath)
        {
            if (!IsUnderAssets(absolutePath)) return;
            string assetPath = ToAssetPath(absolutePath);
            if (string.IsNullOrEmpty(assetPath)) return;
            AssetDatabase.ImportAsset(assetPath,
                ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
        }

        private static string ToAssetPath(string absolutePath)
        {
            string assetPath = absolutePath.Replace('\\', '/');
            if (assetPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)) return assetPath;
            string data = (Application.dataPath ?? "").Replace('\\', '/');
            if (data.Length == 0 || !assetPath.StartsWith(data, StringComparison.OrdinalIgnoreCase)) return "";
            return "Assets" + assetPath.Substring(data.Length).Replace('\\', '/');
        }

        private static string SafeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "mesh";
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
                sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
            return sb.ToString();
        }

        // ------------------------------------------------------------------
        // Rendering + output helpers
        // ------------------------------------------------------------------

        private object RenderAndWrite(TgSpec spec, string outputPath, JsonClass args, bool selectByDefault)
        {
            Texture2D tex;
            int ss;
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                tex = TgRasterizer.Render(spec, out ss);
            }
            catch (Exception e)
            {
                return Response.Error(L.T($"Render failed: {e.Message}", $"渲染失败：{e.Message}"));
            }

            if (TgJson.GetBool(args, "open_preview", false))
                TexGenPreviewWindow.Show(spec, tex);

            byte[] png = TgRasterizer.EncodePng(tex);
            stopwatch.Stop();

            var data = new JsonClass();
            data["width"] = tex.width;
            data["height"] = tex.height;
            data["supersample"] = ss;
            data["layers"] = spec.Layers.Count;
            data["normal_map"] = spec.NormalMap;
            data["bytes"] = png.Length;
            data["render_ms"] = stopwatch.ElapsedMilliseconds;

            UnityEngine.Object.DestroyImmediate(tex);

            if (string.IsNullOrEmpty(outputPath))
            {
                data["written"] = false;
                return Response.Success(
                    L.T($"Rendered {spec.Width}x{spec.Height} in memory; pass output_path to write a PNG.",
                        $"已在内存中渲染 {spec.Width}x{spec.Height}；传入 output_path 可写出 PNG。"), data);
            }

            string absolute = ResolvePath(outputPath, true);
            if (!TgJson.GetBool(args, "overwrite", true) && File.Exists(absolute))
                return Response.Error(L.T($"File already exists: {absolute}", $"文件已存在：{absolute}"));

            try
            {
                TgRasterizer.WritePng(absolute, png);
            }
            catch (Exception e)
            {
                return Response.Error(L.T($"Failed to write '{absolute}': {e.Message}",
                    $"写入 '{absolute}' 失败：{e.Message}"));
            }

            bool underAssets = IsUnderAssets(absolute);
            if (underAssets)
            {
                ImportSingleAsset(absolute);
                if (TgJson.GetBool(args, "select_asset", selectByDefault))
                    SelectAsset(absolute);
            }

            ApplyImporterSettings(absolute, args, spec.NormalMap);

            data["path"] = outputPath;
            data["written"] = true;

            string warningSuffix = "";
            if (!underAssets)
                warningSuffix = L.T(" (outside Assets/, not imported as an asset)",
                    "（位于 Assets/ 之外，未作为资源导入）");

            return Response.Success(
                L.T($"Rendered {spec.Width}x{spec.Height} with {spec.Layers.Count} layer(s) and wrote '{outputPath}'{warningSuffix}.",
                    $"已用 {spec.Layers.Count} 个图层渲染 {spec.Width}x{spec.Height} 并写出 '{outputPath}'{warningSuffix}。"), data);
        }

        private static void ApplyImporterSettings(string absolutePath, JsonClass args, bool normalMap)
        {
            if (!IsUnderAssets(absolutePath)) return;

            if (!absolutePath.StartsWith(Application.dataPath)) return;
            string assetPath = "Assets" + absolutePath.Substring(Application.dataPath.Length).Replace('\\', '/');

            // The caller imports the freshly written PNG with ImportSingleAsset;
            // do not Refresh() the whole project here (see ImportSingleAsset).
            var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer == null) return;

            string type = TgJson.GetString(args, "texture_type");
            if (string.IsNullOrEmpty(type))
                type = normalMap ? "NormalMap" : null;
            if (!string.IsNullOrEmpty(type) && Enum.TryParse(type, true, out TextureImporterType parsedType))
                importer.textureType = parsedType;

            string filter = TgJson.GetString(args, "filter_mode");
            if (!string.IsNullOrEmpty(filter) && Enum.TryParse(filter, true, out FilterMode fm))
                importer.filterMode = fm;

            string wrap = TgJson.GetString(args, "wrap_mode");
            if (!string.IsNullOrEmpty(wrap) && Enum.TryParse(wrap, true, out TextureWrapMode wm))
                importer.wrapMode = wm;

            int maxSize = TgJson.GetInt(args, "max_size", 0);
            if (maxSize > 0) importer.maxTextureSize = maxSize;

            if (importer.textureType == TextureImporterType.Sprite)
            {
                importer.spriteImportMode = SpriteImportMode.Single;
                importer.spritePixelsPerUnit = TgJson.GetInt(args, "pixels_per_unit", 100);
                if (TgJson.GetBool(args, "sprite_pivot_center", true))
                    importer.spritePivot = new Vector2(0.5f, 0.5f);
            }

            if (normalMap) importer.textureType = TextureImporterType.NormalMap;

            EditorUtility.SetDirty(importer);
            importer.SaveAndReimport();
        }

        private static void SelectAsset(string assetOrAbsolutePath)
        {
            if (!IsUnderAssets(assetOrAbsolutePath)) return;

            string assetPath = assetOrAbsolutePath.Replace('\\', '/');
            if (!assetPath.StartsWith("Assets/"))
            {
                if (!assetOrAbsolutePath.StartsWith(Application.dataPath)) return;
                assetPath = "Assets" + assetOrAbsolutePath.Substring(Application.dataPath.Length).Replace('\\', '/');
            }

            var obj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
            if (obj != null)
            {
                EditorGUIUtility.PingObject(obj);
                Selection.activeObject = obj;
            }
        }

        private static bool IsUnderAssets(string absolutePath)
        {
            if (string.IsNullOrEmpty(absolutePath)) return false;
            string normalized = absolutePath.Replace('\\', '/');
            string assets = Application.dataPath.Replace('\\', '/');
            return normalized.StartsWith(assets + "/") || normalized == assets;
        }

        /// <summary>Resolve a project-relative ("Assets/...") or absolute path.</summary>
        private static string ResolvePath(string path, bool ensureDirectory)
        {
            if (string.IsNullOrEmpty(path)) return path;

            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
            string resolved;

            if (Path.IsPathRooted(path))
            {
                resolved = Path.GetFullPath(path);
            }
            else if (path.Replace('\\', '/').StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                resolved = Path.GetFullPath(Path.Combine(projectRoot, path));
            }
            else
            {
                // Default relative paths to the project root, not the process CWD.
                resolved = Path.GetFullPath(Path.Combine(projectRoot, path));
            }

            if (ensureDirectory)
            {
                string dir = Path.GetDirectoryName(resolved);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            }

            return resolved;
        }

        // ------------------------------------------------------------------
        // Parsing helpers
        // ------------------------------------------------------------------

        private static TgParseResult ParseArguments(JsonClass args)
        {
            JsonNode layers = TgJson.Get(args, "layers");
            if (layers == null)
            {
                string json = TgJson.GetString(args, "layers_json");
                if (!string.IsNullOrEmpty(json))
                {
                    try { layers = Json.Parse(json); }
                    catch (Exception e)
                    {
                        var bad = new TgParseResult { Spec = new TgSpec() };
                        bad.Errors.Add("layers_json is not valid JSON: " + e.Message);
                        return bad;
                    }
                }
            }

            var root = new JsonClass();
            if (layers != null) root["layers"] = layers;

            CopyIfPresent(args, root, "width", "height", "background", "origin", "supersample",
                "normal_map", "normal_strength", "tiling", "tileable", "seamless");

            var parse = TgParser.Parse(root);

            if (layers == null)
                parse.Errors.Insert(0, L.T(
                    "No layers given. Pass \"layers\":[{...}] or a \"layers_json\" string. Call texgen.examples for ready-made lists.",
                    "未提供图层。请传 \"layers\":[{...}] 或 \"layers_json\" 字符串。可用 texgen.examples 取现成指令表。"));

            return parse;
        }

        private static void CopyIfPresent(JsonClass from, JsonClass to, params string[] keys)
        {
            foreach (string key in keys)
            {
                JsonNode v = TgJson.Get(from, key);
                if (v != null) to[key] = v;
            }
        }

        private static object ValidationError(TgParseResult parse)
        {
            var data = new JsonClass();
            data["errors"] = ToJsonArray(parse.Errors);
            if (parse.Warnings.Count > 0) data["warnings"] = ToJsonArray(parse.Warnings);
            return Response.Error(string.Join(" | ", parse.Errors.ToArray()), data);
        }

        private static JsonArray ToJsonArray(List<string> values)
        {
            var arr = new JsonArray();
            foreach (string v in values) arr.Add(new JsonData(v));
            return arr;
        }

        // ------------------------------------------------------------------
        // Grammar description
        // ------------------------------------------------------------------

        private static JsonClass CanvasGrammar()
        {
            var canvas = new JsonClass();
            canvas["width"] = new JsonData("integer 1-2048 (default 256)");
            canvas["height"] = new JsonData("integer 1-2048 (default 256)");
            canvas["background"] = new JsonData("#RRGGBB / #RRGGBBAA / #RGB / #RGBA / \"r,g,b[,a]\" / colour name");
            canvas["origin"] = EnumGrammar(typeof(TgOrigin), "topLeft");
            canvas["supersample"] = new JsonData("integer 1-4 (default 2)");
            canvas["normal_map"] = new JsonData("boolean (default false)");
            canvas["normal_strength"] = new JsonData("number >= 0 (default 4)");
            canvas["tiling"] = new JsonData(L.T(
                "boolean (default false). Makes noise/pattern fills wrap at the canvas border so the PNG tiles seamlessly. Aliases: tileable, seamless.",
                "布尔（默认 false）。让噪波/图案填充在画布边界闭合，使 PNG 可无缝平铺。别名：tileable、seamless。"));
            return canvas;
        }

        private static JsonClass LayerGrammar()
        {
            var layer = new JsonClass();

            var geometry = new JsonClass();
            geometry["shape"] = EnumGrammar(typeof(TgShape), "circle");
            geometry["center"] = new JsonData("array [x, y] in pixels (alias: at)");
            geometry["radius"] = new JsonData("number: circle/ring/star radius, or a fallback for ellipse");
            geometry["innerRadius"] = new JsonData("number: ring inner edge / star inner radius");
            geometry["thickness"] = new JsonData("number: ring band width (overrides innerRadius)");
            geometry["radiusX"] = new JsonData("number: ellipse horizontal radius");
            geometry["radiusY"] = new JsonData("number: ellipse vertical radius");
            geometry["size"] = new JsonData("array [w, h]: rect / roundRect bounds");
            geometry["cornerRadius"] = new JsonData("number: roundRect corner radius");
            geometry["from"] = new JsonData("array [x, y]: line start");
            geometry["to"] = new JsonData("array [x, y]: line end");
            geometry["width"] = new JsonData("number: line stroke width");
            geometry["roundCap"] = new JsonData("boolean: round line caps (default true)");
            geometry["points"] = new JsonData("array [[x,y],...]: polygon vertices, >= 3 (relative to center)");
            geometry["pointCount"] = new JsonData("integer 3-64: star spike count (alias: spikes)");
            geometry["rotation"] = new JsonData("number: degrees; rotates star/polygon seeds and the linear-gradient axis");
            layer["geometry"] = geometry;

            var paint = new JsonClass();
            paint["fill"] = EnumGrammar(typeof(TgFill), "solid");
            paint["color"] = new JsonData("colour: solid fill, or radial/linear start (aliases: centerColor)");
            paint["edgeColor"] = new JsonData("colour: radial/linear end (alias: color2 / toColor)");
            paint["midColor"] = new JsonData("optional colour: adds a middle stop to noise/ripple ramps");
            paint["gradient"] = new JsonData("array of {offset:0..1, color} or {\"0\":\"#000\",\"1\":\"#fff\"}");
            paint["noise"] = EnumGrammar(typeof(TgNoiseKind), "fbm");
            paint["seed"] = new JsonData("integer: deterministic noise seed");
            paint["octaves"] = new JsonData("integer 1-10: fbm/ridged detail");
            paint["scale"] = new JsonData("number: noise cell size in pixels (default 48)");
            paint["frequency"] = new JsonData("number: ripple cycles per 100 px");
            paint["phase"] = new JsonData("number: ripple phase in turns");
            paint["amplitude"] = new JsonData("number 0-1: ripple contrast");
            paint["cell"] = new JsonData("array [w, h]: grid cell size (also the pattern period)");
            paint["lineWidth"] = new JsonData("number: grid line thickness");
            paint["pattern"] = new JsonData(L.T(
                "pattern name when fill=\"pattern\": brick | plank | tile | checker | stripe | hex | scale | weave | dot | cobble",
                "fill=\"pattern\" 时的图案名：brick | plank | tile | checker | stripe | hex | scale | weave | dot | cobble"));
            paint["gap"] = new JsonData(L.T(
                "number: pattern seam width in pixels (mortar joint, thread gap). Aliases: mortar",
                "数字：图案缝宽（像素），砖缝/织线间隙。别名：mortar"));
            paint["gapColor"] = new JsonData(L.T(
                "colour inside pattern seams (alias: mortarColor). Use #00000000 to cut through",
                "图案缝隙内的颜色（别名 mortarColor）。用 #00000000 可镂空"));
            paint["stagger"] = new JsonData(L.T(
                "number 0-1: row offset (brick, plank, dot) or duty cycle (stripe, weave). Default 0.5",
                "数字 0-1：行错位（brick/plank/dot）或占空比（stripe/weave）。默认 0.5"));
            paint["variation"] = new JsonData(L.T(
                "number 0-1: per-cell brightness jitter so repeated cells do not look cloned (alias: jitter). Default 0.12",
                "数字 0-1：单格亮度抖动，避免重复格子雷同（别名 jitter）。默认 0.12"));
            layer["paint"] = paint;

            var warp = new JsonClass();
            warp["warp"] = new JsonData(L.T(
                "boolean or {enabled, noise, scale, amount, octaves}. Domain warp displaces the fill lookup: straight stripes become wood grain, cell noise becomes marble veining. Geometry is not affected.",
                "布尔或 {enabled, noise, scale, amount, octaves}。域扭曲偏移填充采样坐标：直线条纹变木纹，胞状噪波变大理石纹。不影响几何形状。"));
            warp["warp.scale"] = new JsonData("number: noise cell size of the warp field in px (default 64)");
            warp["warp.amount"] = new JsonData("number: peak displacement in px (default 12); 30+ gives heavy marble veining");
            warp["example"] = new JsonData(L.T(
                "\"warp\":{\"noise\":\"fbm\",\"scale\":70,\"amount\":26} on a stripe fill = wood grain",
                "在 stripe 填充上加 \"warp\":{\"noise\":\"fbm\",\"scale\":70,\"amount\":26} = 木纹"));
            layer["warp"] = warp;

            var compositing = new JsonClass();
            compositing["blend"] = EnumGrammar(typeof(TgBlend), "normal");
            compositing["opacity"] = new JsonData("number 0-1 (default 1)");
            compositing["feather"] = new JsonData("number: soft edge band width in pixels");
            compositing["blur"] = new JsonData("number: layer blur radius in pixels");
            compositing["glow"] = new JsonData("{enabled, radius, color, intensity}: outer glow added under the layer");
            layer["compositing"] = compositing;

            layer["name"] = new JsonData("string: label used in diagnostics");
            return layer;
        }

        private static JsonNode EnumGrammar(Type enumType, string def)
        {
            var obj = new JsonClass();
            var values = new JsonArray();
            foreach (string name in Enum.GetNames(enumType))
            {
                string camel = char.ToLowerInvariant(name[0]) + name.Substring(1);
                values.Add(new JsonData(camel));
            }
            obj["values"] = values;
            obj["default"] = new JsonData(def);
            return obj;
        }
    }
}
