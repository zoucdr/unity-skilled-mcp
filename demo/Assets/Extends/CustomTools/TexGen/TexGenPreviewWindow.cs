using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UniMcp;
using UniMcp.Models;
using UniMcp.TexGen;

namespace UniMcp.Tools.TexGen
{
    /// <summary>
    /// Interactive surface for the texture generator: paste or edit an instruction
    /// list, render, inspect, and export the result as PNG without leaving the
    /// editor. The window talks to the same engine the MCP tool drives.
    /// </summary>
    public class TexGenPreviewWindow : EditorWindow
    {
        private const string PrefsKey = "UniMcp.TexGen.LastInstructions";

        private string _instructions;
        private Texture2D _preview;
        private TgSpec _spec;
        private Vector2 _scroll;
        private Vector2 _previewScroll;

        private bool _normalMap;
        private float _normalStrength = 4f;
        private int _supersample = 2;
        private string _outputPath = "Assets/TexGen/preview.png";
        private string _status = "";
        private bool _statusError;

        private int _renderedWidth;
        private int _renderedHeight;
        private double _lastRenderMs;

        [MenuItem("Window/MCP/TexGen Preview")]
        public static void Open()
        {
            var window = GetWindow<TexGenPreviewWindow>("TexGen");
            window.minSize = new Vector2(520f, 520f);
            window.Show();
        }

        /// <summary>Show the window with an already rendered texture.</summary>
        public static void Show(TgSpec spec, Texture2D texture)
        {
            var window = GetWindow<TexGenPreviewWindow>("TexGen");
            window.minSize = new Vector2(520f, 520f);
            window.Adopt(spec, texture);
            window.Show();
            window.Repaint();
        }

        private void OnEnable()
        {
            if (string.IsNullOrEmpty(_instructions))
            {
                _instructions = EditorPrefs.GetString(PrefsKey, TexGenSamples.MagicOrb);
            }
        }

        private void OnDestroy()
        {
            ReleasePreview();
        }

        private void Adopt(TgSpec spec, Texture2D texture)
        {
            ReleasePreview();
            _spec = spec;
            _preview = texture;
            _normalMap = spec.NormalMap;
            _normalStrength = spec.NormalStrength;
            _supersample = spec.Supersample;
            _renderedWidth = texture.width;
            _renderedHeight = texture.height;
            _status = L.T("Received a render from the MCP tool.", "已接收来自 MCP 工具的渲染结果。");
            _statusError = false;
        }

        private void ReleasePreview()
        {
            if (_preview != null)
            {
                DestroyImmediate(_preview);
                _preview = null;
            }
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(4f);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(L.T("Load magic orb", "载入魔法球"), GUILayout.Height(22f)))
                    LoadSample(TexGenSamples.MagicOrb);
                if (GUILayout.Button(L.T("Skill icon", "技能图标"), GUILayout.Height(22f)))
                    LoadSample(TexGenSamples.SkillIcon);
                if (GUILayout.Button(L.T("Noise", "噪波"), GUILayout.Height(22f)))
                    LoadSample(TexGenSamples.Noise);
                if (GUILayout.Button(L.T("Star", "星爆"), GUILayout.Height(22f)))
                    LoadSample(TexGenSamples.Star);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(L.T("Save PNG", "导出 PNG"), GUILayout.Height(22f))) ExportPng();
            }

            EditorGUILayout.Space(2f);
            EditorGUILayout.LabelField(L.T("Instruction list (JSON)", "指令表（JSON）"), EditorStyles.boldLabel);

            _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.MinHeight(140f), GUILayout.MaxHeight(240f));
            _instructions = EditorGUILayout.TextArea(_instructions ?? "", GUILayout.ExpandHeight(true));
            EditorGUILayout.EndScrollView();

            EditorGUILayout.Space(2f);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(L.T("Render", "渲染"), GUILayout.Height(26f), GUILayout.Width(120f)))
                    Render();

                _supersample = EditorGUILayout.IntPopup(
                    L.T("Supersample", "超采样"),
                    _supersample,
                    new[] { "1", "2", "3", "4" },
                    new[] { 1, 2, 3, 4 },
                    GUILayout.Width(180f));

                _normalMap = EditorGUILayout.ToggleLeft(L.T("Normal map", "法线贴图"), _normalMap, GUILayout.Width(110f));

                using (new EditorGUI.DisabledScope(!_normalMap))
                {
                    _normalStrength = EditorGUILayout.Slider(_normalStrength, 0.5f, 24f, GUILayout.Width(180f));
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(L.T("Output", "输出路径"), GUILayout.Width(64f));
                _outputPath = EditorGUILayout.TextField(_outputPath);
            }

            EditorGUILayout.Space(2f);

            if (_preview != null)
            {
                EditorGUILayout.LabelField(
                    L.T($"Preview: {_renderedWidth}x{_renderedHeight}  |  {_lastRenderMs:F0} ms",
                        $"预览：{_renderedWidth}x{_renderedHeight}  |  {_lastRenderMs:F0} ms"),
                    EditorStyles.miniLabel);
            }

            var area = GUILayoutUtility.GetRect(position.width, Mathf.Max(160f, position.height - 300f));
            DrawCheckerboard(area);

            if (_preview != null)
            {
                float aspect = (float)_preview.width / Mathf.Max(_preview.height, 1);
                float w = Mathf.Min(area.width, area.height * aspect);
                float h = w / Mathf.Max(aspect, 1e-4f);
                var fit = new Rect(
                    area.x + (area.width - w) * 0.5f,
                    area.y + (area.height - h) * 0.5f,
                    w, h);

                EditorGUI.DrawPreviewTexture(fit, _preview, null, ScaleMode.StretchToFill);
                EditorGUI.DrawRect(new Rect(fit.x, fit.y, fit.width, 1f), new Color(0f, 0f, 0f, 0.35f));
                EditorGUI.DrawRect(new Rect(fit.x, fit.yMax - 1f, fit.width, 1f), new Color(0f, 0f, 0f, 0.35f));
            }
            else
            {
                EditorGUI.HelpBox(area, L.T(
                    "Press Render to preview the instruction list.",
                    "点击「渲染」以预览指令表。"), MessageType.Info);
            }

            if (!string.IsNullOrEmpty(_status))
            {
                EditorGUILayout.Space(2f);
                EditorGUILayout.HelpBox(_status, _statusError ? MessageType.Error : MessageType.Info);
            }
        }

        private void DrawCheckerboard(Rect rect)
        {
            const float cell = 12f;
            var light = new Color(0.62f, 0.62f, 0.62f, 1f);
            var dark = new Color(0.48f, 0.48f, 0.48f, 1f);
            EditorGUI.DrawRect(rect, light);
            int cols = Mathf.CeilToInt(rect.width / cell);
            int rows = Mathf.CeilToInt(rect.height / cell);
            for (int y = 0; y < rows; y++)
            {
                for (int x = 0; x < cols; x++)
                {
                    if (((x + y) & 1) == 0) continue;
                    EditorGUI.DrawRect(new Rect(rect.x + x * cell, rect.y + y * cell, cell, cell), dark);
                }
            }
        }

        private void LoadSample(string json)
        {
            _instructions = json;
            SavePrefs();
            Render();
        }

        private void SavePrefs()
        {
            EditorPrefs.SetString(PrefsKey, _instructions ?? "");
        }

        private void Render()
        {
            SavePrefs();

            JsonNode root;
            try
            {
                root = Json.Parse(_instructions);
            }
            catch (Exception e)
            {
                Fail(L.T("Invalid JSON: ", "JSON 无效：") + e.Message);
                return;
            }

            if (root is JsonClass obj)
            {
                obj["supersample"] = new JsonData(Mathf.Clamp(_supersample, 1, 4));
                obj["normal_map"] = new JsonData(_normalMap);
                obj["normal_strength"] = new JsonData(_normalStrength);
            }

            var parse = TgParser.Parse(root);
            if (!parse.Ok)
            {
                Fail(string.Join("\n", parse.Errors.ToArray()));
                return;
            }

            try
            {
                double start = EditorApplication.timeSinceStartup;
                ReleasePreview();
                _spec = parse.Spec;
                _preview = TgRasterizer.Render(parse.Spec, out int used);
                _lastRenderMs = (EditorApplication.timeSinceStartup - start) * 1000.0;
                _renderedWidth = _preview.width;
                _renderedHeight = _preview.height;
                _status = L.T(
                    $"Rendered {parse.Spec.Layers.Count} layer(s) at {_preview.width}x{_preview.height} (SSAA x{used}).",
                    $"已渲染 {parse.Spec.Layers.Count} 个图层，{_preview.width}x{_preview.height}（超采样 x{used}）。");
                _statusError = false;

                if (parse.Warnings.Count > 0)
                    _status += "\n" + string.Join("\n", parse.Warnings.ToArray());
            }
            catch (Exception e)
            {
                Fail(L.T("Render failed: ", "渲染失败：") + e.Message);
            }

            Repaint();
        }

        private void ExportPng()
        {
            if (_preview == null)
            {
                Fail(L.T("Render something first.", "请先渲染。"));
                return;
            }

            string path = string.IsNullOrEmpty(_outputPath) ? "Assets/TexGen/preview.png" : _outputPath;
            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
            string absolute = Path.IsPathRooted(path)
                ? Path.GetFullPath(path)
                : Path.GetFullPath(Path.Combine(projectRoot, path));

            try
            {
                TgRasterizer.WritePng(absolute, TgRasterizer.EncodePng(_preview));
                AssetDatabase.Refresh();

                if (absolute.Replace('\\', '/').StartsWith(Application.dataPath.Replace('\\', '/') + "/"))
                {
                    string assetPath = "Assets" + absolute.Substring(Application.dataPath.Length).Replace('\\', '/');
                    var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
                    if (asset != null)
                    {
                        EditorGUIUtility.PingObject(asset);
                        Selection.activeObject = asset;
                    }
                }

                _status = L.T("Wrote ", "已写出 ") + absolute;
                _statusError = false;
            }
            catch (Exception e)
            {
                Fail(L.T("Write failed: ", "写入失败：") + e.Message);
            }
        }

        private void Fail(string message)
        {
            _status = message;
            _statusError = true;
            Repaint();
        }
    }

    /// <summary>Instruction lists shared by the preview window and the MCP tool.</summary>
    public static class TexGenSamples
    {
        public const string MagicOrb = @"{
  ""width"": 256, ""height"": 256, ""origin"": ""topLeft"", ""supersample"": 3,
  ""layers"": [
    { ""name"": ""outer_glow"", ""shape"": ""circle"", ""center"": [128,128], ""radius"": 92,
      ""fill"": ""solid"", ""color"": ""#44aaff"", ""opacity"": 0.55, ""blur"": 18 },
    { ""name"": ""orb"", ""shape"": ""circle"", ""center"": [128,128], ""radius"": 90,
      ""fill"": ""radial"", ""gradient"": [ {""offset"":0,""color"":""#ffffff""}, {""offset"":0.45,""color"":""#88ccff""}, {""offset"":1,""color"":""#1a5fd0""} ] },
    { ""name"": ""rim_ring"", ""shape"": ""ring"", ""center"": [128,128], ""radius"": 95, ""innerRadius"": 90,
      ""fill"": ""solid"", ""color"": ""#2288ff"", ""blend"": ""screen"", ""opacity"": 0.9 },
    { ""name"": ""cross_h"", ""shape"": ""line"", ""from"": [40,128], ""to"": [216,128],
      ""width"": 3, ""fill"": ""solid"", ""color"": ""#ffffff"", ""opacity"": 0.55, ""blur"": 2 },
    { ""name"": ""cross_v"", ""shape"": ""line"", ""from"": [128,40], ""to"": [128,216],
      ""width"": 3, ""fill"": ""solid"", ""color"": ""#ffffff"", ""opacity"": 0.55, ""blur"": 2 },
    { ""name"": ""core"", ""shape"": ""circle"", ""center"": [118,116], ""radius"": 26,
      ""fill"": ""radial"", ""color"": ""#ffffff"", ""edgeColor"": ""#00000000"", ""blend"": ""add"", ""opacity"": 0.85 }
  ]
}";

        public const string SkillIcon = @"{
  ""width"": 128, ""height"": 128, ""background"": ""#00000000"", ""supersample"": 3,
  ""layers"": [
    { ""name"": ""plate"", ""shape"": ""roundRect"", ""center"": [64,64], ""size"": [112,112], ""cornerRadius"": 24,
      ""fill"": ""linear"", ""rotation"": 90, ""gradient"": [ {""offset"":0,""color"":""#2b3a67""}, {""offset"":1,""color"":""#141c33""} ] },
    { ""name"": ""inner_trim"", ""shape"": ""roundRect"", ""center"": [64,64], ""size"": [96,96], ""cornerRadius"": 18,
      ""blend"": ""erase"" },
    { ""name"": ""frame_ring"", ""shape"": ""ring"", ""center"": [64,64], ""radius"": 50, ""thickness"": 4,
      ""fill"": ""solid"", ""color"": ""#5ad1ff"", ""opacity"": 0.95 },
    { ""name"": ""chevron"", ""shape"": ""polygon"", ""center"": [64,64],
      ""points"": [[-22,10],[-2,-14],[18,10],[18,22],[-2,-2],[-22,22]],
      ""fill"": ""solid"", ""color"": ""#eaf7ff"" }
  ]
}";

        public const string Noise = @"{
  ""width"": 256, ""height"": 256, ""background"": ""#000000ff"", ""supersample"": 1,
  ""layers"": [
    { ""name"": ""height"", ""shape"": ""rect"", ""center"": [128,128], ""size"": [256,256],
      ""fill"": ""noise"", ""noise"": ""fbm"", ""octaves"": 5, ""scale"": 42, ""seed"": 7,
      ""color"": ""#000000"", ""edgeColor"": ""#ffffff"" }
  ]
}";

        public const string Star = @"{
  ""width"": 200, ""height"": 200, ""origin"": ""topLeft"", ""supersample"": 3,
  ""layers"": [
    { ""name"": ""halo"", ""shape"": ""circle"", ""center"": [100,100], ""radius"": 70,
      ""fill"": ""radial"", ""color"": ""#ffd35c"", ""edgeColor"": ""#00000000"", ""opacity"": 0.5, ""blur"": 10 },
    { ""name"": ""star"", ""shape"": ""star"", ""center"": [100,100], ""radius"": 78, ""innerRadius"": 30,
      ""pointCount"": 6, ""fill"": ""linear"", ""rotation"": 90,
      ""gradient"": [ {""offset"":0,""color"":""#fff6c2""}, {""offset"":1,""color"": ""#ffab2e""} ] },
    { ""name"": ""spark"", ""shape"": ""circle"", ""center"": [86,84], ""radius"": 10,
      ""fill"": ""solid"", ""color"": ""#ffffff"", ""blend"": ""add"", ""opacity"": 0.8 }
  ]
}";
    }
}
