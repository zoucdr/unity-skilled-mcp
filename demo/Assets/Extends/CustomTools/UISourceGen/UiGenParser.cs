using System;
using System.Collections.Generic;
using System.Globalization;
using UniMcp;

namespace UniMcp.UiGen
{
    /// <summary>Outcome of parsing a UI screen description.</summary>
    public sealed class UiParseResult
    {
        public UiScreen Screen;
        public readonly List<string> Errors = new List<string>();
        public readonly List<string> Warnings = new List<string>();
        public bool Ok => Screen != null && Errors.Count == 0;
    }

    /// <summary>
    /// Turns a JSON screen description into a <see cref="UiScreen"/>. Deterministic and
    /// total: unreadable fields fall back to defaults and are reported as warnings.
    /// </summary>
    public static class UiParser
    {
        public static UiParseResult Parse(JsonNode root)
        {
            var result = new UiParseResult();
            var screen = new UiScreen();

            if (root == null)
            {
                result.Errors.Add("Screen description is empty. Pass {\"width\", \"height\", \"controls\":[...]}.");
                result.Screen = screen;
                return result;
            }
            JsonClass obj = root as JsonClass;
            if (obj == null)
            {
                result.Errors.Add("Screen description must be a JSON object.");
                result.Screen = screen;
                return result;
            }

            screen.Name = GetString(obj, "name", "Screen");
            screen.Width = Clamp(GetInt(obj, "width", screen.Width), 16, 8192);
            screen.Height = Clamp(GetInt(obj, "height", screen.Height), 16, 8192);
            screen.Background = GetColor(obj, "background", screen.Background);
            screen.BackgroundEnd = GetColor(obj, "background_end", screen.BackgroundEnd);
            if (HasKey(obj, "background_gradient"))
                screen.BackgroundGradient = GetBool(obj, "background_gradient", screen.BackgroundGradient);
            screen.Supersample = Clamp(GetFloat(obj, "supersample", screen.Supersample), 1f, 4f);

            ParseTheme(obj, "theme", screen.Theme);

            JsonNode controlsNode = Get(obj, "controls");
            if (controlsNode is JsonArray controls)
            {
                for (int i = 0; i < controls.Count; i++)
                {
                    JsonClass node = controls[i] as JsonClass;
                    if (node == null) { result.Errors.Add($"Control {i} is not an object."); continue; }
                    UiControl ctrl = ReadControl(node, i, result);
                    if (ctrl != null) screen.Controls.Add(ctrl);
                }
            }
            else
            {
                result.Errors.Add("\"controls\" must be an array of control objects.");
            }

            result.Screen = screen;
            return result;
        }

        private static UiControl ReadControl(JsonClass node, int index, UiParseResult result)
        {
            var c = new UiControl();
            c.Id = GetString(node, "id", "ctrl" + index);
            c.Type = GetEnum(node, "type", UiControlType.Button);
            c.Anchor = GetEnum(node, "anchor", UiAnchor.Center);

            // rect [x, y, w, h] or individual x/y/w/h
            JsonNode rect = Get(node, "rect");
            if (rect is JsonArray r && r.Count >= 4)
            {
                c.X = r[0].AsFloatDefault(0f); c.Y = r[1].AsFloatDefault(0f);
                c.W = r[2].AsFloatDefault(64f); c.H = r[3].AsFloatDefault(32f);
            }
            else
            {
                c.X = GetFloat(node, "x", c.X);
                c.Y = GetFloat(node, "y", c.Y);
                c.W = GetFloat(node, "w", GetFloat(node, "width", c.W));
                c.H = GetFloat(node, "h", GetFloat(node, "height", c.H));
            }
            if (c.W <= 0 || c.H <= 0) result.Warnings.Add($"Control '{c.Id}' has non-positive size; clamped.");
            c.W = Math.Max(1f, c.W); c.H = Math.Max(1f, c.H);

            ParseStyle(node, "style", c.Style, result);
            c.Text = GetString(node, "text", "");
            c.Icon = GetString(node, "icon", "");
            c.Value = Clamp(GetFloat(node, "value", c.Value), 0f, 1f);
            if (HasKey(node, "checked")) c.Checked = GetBool(node, "checked", false);
            if (HasKey(node, "selected")) c.Selected = GetBool(node, "selected", false);
            JsonNode items = Get(node, "items");
            if (items is JsonArray ia)
            {
                var list = new List<string>();
                foreach (JsonNode it in ia) if (it != null) list.Add(it.Value);
                c.Items = list.ToArray();
            }

            JsonNode states = Get(node, "states");
            if (states is JsonClass sc)
            {
                foreach (var kv in sc.AsEnumerable())
                {
                    if (Enum.TryParse<UiState>(Normalize(kv.Key), true, out UiState st))
                    {
                        var stStyle = new UiStyle();
                        ParseStyle(kv.Value as JsonClass, null, stStyle, result);
                        c.StateStyles[st] = stStyle;
                    }
                }
            }
            else if (states is JsonArray sa)
            {
                foreach (JsonNode sn in sa)
                {
                    string raw = sn?.Value;
                    if (!string.IsNullOrEmpty(raw) && Enum.TryParse<UiState>(Normalize(raw), true, out UiState st))
                    {
                        // ask the renderer to also emit these explicit states
                        c.StateStyles[st] = new UiStyle();
                    }
                }
            }
            return c;
        }

        private static void ParseTheme(JsonClass node, string key, UiTheme t)
        {
            JsonNode tn = Get(node, key);
            if (!(tn is JsonClass tc)) return;
            t.Primary = GetColor(tc, "primary", t.Primary);
            t.PrimaryEnd = GetColor(tc, "primary_end", t.PrimaryEnd);
            t.Surface = GetColor(tc, "surface", t.Surface);
            t.SurfaceEnd = GetColor(tc, "surface_end", t.SurfaceEnd);
            t.OnSurface = GetColor(tc, "on_surface", t.OnSurface);
            t.Border = GetColor(tc, "border", t.Border);
            t.Shadow = GetColor(tc, "shadow", t.Shadow);
            if (HasKey(tc, "radius")) t.Radius = GetFloat(tc, "radius", t.Radius);
            if (HasKey(tc, "border_width")) t.BorderWidth = GetFloat(tc, "border_width", t.BorderWidth);
            if (HasKey(tc, "shadow_blur")) t.ShadowBlur = GetFloat(tc, "shadow_blur", t.ShadowBlur);
            if (HasKey(tc, "shadow_y")) t.ShadowY = GetFloat(tc, "shadow_y", t.ShadowY);
            if (HasKey(tc, "font_size")) t.FontSize = GetFloat(tc, "font_size", t.FontSize);
            t.FontPath = GetString(tc, "font_path", t.FontPath);
            t.FontWeight = GetEnum(tc, "font_weight", t.FontWeight);
            t.TextAlign = GetEnum(tc, "text_align", t.TextAlign);
        }

        private static void ParseStyle(JsonClass node, string key, UiStyle s, UiParseResult result)
        {
            JsonNode sn = (key == null) ? node : Get(node, key);
            if (!(sn is JsonClass sc)) return;
            if (HasKey(sc, "background")) s.Background = GetColor(sc, "background", s.Background ?? UiColor.White);
            if (HasKey(sc, "background_end")) s.BackgroundEnd = GetColor(sc, "background_end", s.BackgroundEnd ?? UiColor.White);
            if (HasKey(sc, "gradient_dir")) s.GradientDir = GetEnum(sc, "gradient_dir", s.GradientDir);
            if (HasKey(sc, "border_color")) s.BorderColor = GetColor(sc, "border_color", s.BorderColor ?? UiColor.White);
            if (HasKey(sc, "border_width")) s.BorderWidth = GetFloat(sc, "border_width", s.BorderWidth ?? 0f);
            if (HasKey(sc, "corner_radius")) s.CornerRadius = GetFloat(sc, "corner_radius", s.CornerRadius ?? 0f);
            if (HasKey(sc, "shadow_color")) s.ShadowColor = GetColor(sc, "shadow_color", s.ShadowColor ?? UiColor.Black);
            if (HasKey(sc, "shadow_blur")) s.ShadowBlur = GetFloat(sc, "shadow_blur", s.ShadowBlur ?? 0f);
            if (HasKey(sc, "shadow_x")) s.ShadowX = GetFloat(sc, "shadow_x", s.ShadowX ?? 0f);
            if (HasKey(sc, "shadow_y")) s.ShadowY = GetFloat(sc, "shadow_y", s.ShadowY ?? 0f);
            if (HasKey(sc, "text_color")) s.TextColor = GetColor(sc, "text_color", s.TextColor ?? UiColor.White);
            if (HasKey(sc, "font_size")) s.FontSize = GetFloat(sc, "font_size", s.FontSize ?? 0f);
            s.FontPath = GetString(sc, "font_path", s.FontPath);
            if (HasKey(sc, "font_weight")) s.FontWeight = GetEnum(sc, "font_weight", s.FontWeight ?? UiFontWeight.Regular);
            if (HasKey(sc, "text_align")) s.TextAlign = GetEnum(sc, "text_align", s.TextAlign ?? UiTextAlign.Center);
            if (HasKey(sc, "padding")) s.Padding = GetFloat(sc, "padding", s.Padding ?? 0f);
            if (HasKey(sc, "track_color")) s.TrackColor = GetColor(sc, "track_color", s.TrackColor ?? UiColor.White);
            if (HasKey(sc, "fill_color")) s.FillColor = GetColor(sc, "fill_color", s.FillColor ?? UiColor.White);
            if (HasKey(sc, "fill_color_end")) s.FillColorEnd = GetColor(sc, "fill_color_end", s.FillColorEnd ?? UiColor.White);
        }

        // ----- JSON primitives (mirrors TgJson conventions) -----
        internal static JsonNode Get(JsonClass node, string key)
        {
            if (node == null) return null;
            return node.TryGetValue(key, out JsonNode v) ? v : null;
        }
        internal static bool HasKey(JsonClass node, string key) => node != null && node.ContainsKey(key);
        internal static string GetString(JsonClass node, string key, string fallback)
        {
            var v = Get(node, key);
            return (v == null || string.IsNullOrEmpty(v.Value)) ? fallback : v.Value.Trim();
        }
        internal static float GetFloat(JsonClass node, string key, float fallback)
        {
            var v = Get(node, key);
            if (v == null) return fallback;
            if (v.type == JsonNodeType.String) return float.TryParse(v.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : fallback;
            return v.AsFloatDefault(fallback);
        }
        internal static int GetInt(JsonClass node, string key, int fallback)
        {
            var v = Get(node, key);
            if (v == null) return fallback;
            if (v.type == JsonNodeType.String) return int.TryParse(v.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) ? i : fallback;
            return v.AsIntDefault(fallback);
        }
        internal static bool GetBool(JsonClass node, string key, bool fallback)
        {
            var v = Get(node, key);
            if (v == null) return fallback;
            if (v.type == JsonNodeType.String)
            {
                string s = v.Value;
                if (string.Equals(s, "true", StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(s, "false", StringComparison.OrdinalIgnoreCase)) return false;
                return fallback;
            }
            return v.AsBoolDefault(fallback);
        }
        internal static TEnum GetEnum<TEnum>(JsonClass node, string key, TEnum fallback) where TEnum : struct
        {
            string raw = GetString(node, key, null);
            if (string.IsNullOrEmpty(raw)) return fallback;
            return Enum.TryParse<TEnum>(Normalize(raw), true, out TEnum v) ? v : fallback;
        }
        internal static UiColor GetColor(JsonClass node, string key, UiColor fallback)
        {
            var v = Get(node, key);
            if (v == null) return fallback;
            if (v is JsonArray arr && arr.Count >= 3)
            {
                var list = new List<float>();
                for (int i = 0; i < arr.Count; i++) list.Add(arr[i].AsFloatDefault(0f));
                return UiColor.FromFloats(list);
            }
            return UiColor.TryParse(v.Value, out UiColor c) ? c : fallback;
        }
        private static string Normalize(string raw)
            => raw.Replace("_", "").Replace("-", "").Replace(" ", "");
        private static float Clamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);
        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);
    }
}
