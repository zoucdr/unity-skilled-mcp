using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace UniMcp.TexGen
{
    /// <summary>Geometry primitive of a layer.</summary>
    public enum TgShape
    {
        Circle,
        Ellipse,
        Ring,
        Rect,
        RoundRect,
        Line,
        Polygon,
        Star
    }

    /// <summary>How a layer's pixels are painted inside its geometry.</summary>
    public enum TgFill
    {
        Solid,
        Linear,
        Radial,
        Noise,
        Ripple,
        Grid,

        /// <summary>Tiled masonry / fabric / organic pattern; see <see cref="TgPattern"/>.</summary>
        Pattern
    }

    /// <summary>
    /// Periodic tiling pattern painted by <see cref="TgFill.Pattern"/>. Every pattern
    /// repeats with period <c>cell</c>, so a full-canvas rect plus <c>tiling</c>
    /// produces a seamlessly wrapping surface.
    /// </summary>
    public enum TgPattern
    {
        /// <summary>Running-bond masonry. Rows shift by <c>stagger</c> of a brick.</summary>
        Brick,

        /// <summary>Long staggered boards (wood floor / decking).</summary>
        Plank,

        /// <summary>Square tiles on an aligned grid.</summary>
        Tile,

        /// <summary>Alternating two-colour squares.</summary>
        Checker,

        /// <summary>Parallel stripes at <c>rotation</c> degrees.</summary>
        Stripe,

        /// <summary>Pointy-top honeycomb cells.</summary>
        Hex,

        /// <summary>Overlapping roof/fish scales.</summary>
        Scale,

        /// <summary>Plain weave: alternating over/under threads.</summary>
        Weave,

        /// <summary>Dot lattice, optionally staggered.</summary>
        Dot,

        /// <summary>Cobblestone: irregular rounded stones separated by mortar.</summary>
        Cobble
    }

    /// <summary>Layer compositing operator.</summary>
    public enum TgBlend
    {
        Normal,
        Add,
        Multiply,
        Screen,
        Overlay,
        Subtract,
        Difference,
        Darken,
        Lighten,

        /// <summary>Keep the backdrop only inside this shape (clip to a stencil).</summary>
        Mask,

        /// <summary>Remove the backdrop inside this shape (punch a hole).</summary>
        Erase
    }

    /// <summary>Procedural noise family used by the noise fill.</summary>
    public enum TgNoiseKind
    {
        Value,
        Perlin,
        Fbm,
        Worley,
        Ridged
    }

    /// <summary>Canvas coordinate origin. Y always grows downward.</summary>
    public enum TgOrigin
    {
        TopLeft,
        Center
    }

    /// <summary>Ordered colour ramp used by gradient fills.</summary>
    public sealed class TgGradient
    {
        public readonly List<float> Offsets = new List<float>();
        public readonly List<Color> Colors = new List<Color>();

        public int Count => Offsets.Count;

        public void Add(float offset, Color color)
        {
            Offsets.Add(Mathf.Clamp01(offset));
            Colors.Add(color);
        }

        /// <summary>Sort stops once so evaluation can binary-search linearly.</summary>
        public void Sort()
        {
            for (int i = 1; i < Offsets.Count; i++)
            {
                float o = Offsets[i];
                Color c = Colors[i];
                int j = i - 1;
                while (j >= 0 && Offsets[j] > o)
                {
                    Offsets[j + 1] = Offsets[j];
                    Colors[j + 1] = Colors[j];
                    j--;
                }
                Offsets[j + 1] = o;
                Colors[j + 1] = c;
            }
        }

        public Color Eval(float t)
        {
            if (Offsets.Count == 0) return Color.white;
            if (Offsets.Count == 1) return Colors[0];

            t = Mathf.Clamp01(t);
            if (t <= Offsets[0]) return Colors[0];
            if (t >= Offsets[Offsets.Count - 1]) return Colors[Colors.Count - 1];

            for (int i = 0; i < Offsets.Count - 1; i++)
            {
                float a = Offsets[i];
                float b = Offsets[i + 1];
                if (t >= a && t <= b)
                {
                    float span = b - a;
                    float k = span <= 1e-6f ? 0f : (t - a) / span;
                    return Color.Lerp(Colors[i], Colors[i + 1], k);
                }
            }
            return Colors[Colors.Count - 1];
        }
    }

    /// <summary>A single drawing layer: geometry + paint + compositing.</summary>
    public sealed class TgLayer
    {
        public string Name = "";
        public TgShape Shape = TgShape.Circle;

        // Geometry. Units are pixels in canvas space (see TgOrigin).
        public Vector2 Center = Vector2.zero;
        public float Radius = 64f;          // circle / ring mid radius / star outer
        public float InnerRadius = 0f;      // ring, star
        public float Thickness = 0f;        // ring band width; 0 -> derived from inner/outer
        public float RadiusX = 0f;          // ellipse (0 -> Radius)
        public float RadiusY = 0f;          // ellipse (0 -> Radius)
        public Vector2 Size = new Vector2(128f, 128f);   // rect / round rect
        public float CornerRadius = 0f;     // round rect
        public Vector2 From = Vector2.zero; // line
        public Vector2 To = Vector2.zero;   // line
        public float Width = 2f;            // line stroke width
        public bool RoundCap = true;        // line cap
        public Vector2[] Points;            // polygon
        public int PointCount = 5;          // star
        public float Rotation = 0f;         // degrees

        // Paint.
        public TgFill Fill = TgFill.Solid;
        public Color ColorA = Color.white;
        public Color ColorB = Color.white;
        public Color ColorC = Color.white;
        public bool HasColorC;
        public TgGradient Gradient;         // used by linear / radial
        public TgNoiseKind Noise = TgNoiseKind.Fbm;
        public int Seed = 1337;
        public int Octaves = 4;
        public float Scale = 48f;           // noise cell size (px)
        public float Frequency = 6f;        // ripple cycles per 100 px
        public float Phase = 0f;            // ripple phase (turns)
        public float Amplitude = 1f;        // ripple contrast
        public Vector2 GridCell = new Vector2(32f, 32f);
        public float GridLineWidth = 2f;

        // Pattern fill (TgFill.Pattern).
        public TgPattern Pattern = TgPattern.Brick;
        /// <summary>Gap between pattern cells in pixels (mortar / thread / seam width).</summary>
        public float PatternGap = 3f;
        /// <summary>Colour painted inside the gap bands.</summary>
        public Color PatternGapColor = new Color(0f, 0f, 0f, 0f);
        /// <summary>Row offset as a fraction of one cell (0 = aligned, 0.5 = running bond).</summary>
        public float PatternStagger = 0.5f;
        /// <summary>Per-cell brightness jitter, so repeated cells do not look cloned.</summary>
        public float PatternVariation = 0.12f;
        /// <summary>Corner rounding 0..1 for cell shapes that support it (cobble, dot, scale).</summary>
        public float PatternRoundness = 1f;

        // Domain warp: displaces the fill lookup, which is what turns a straight
        // stripe into wood grain or marble veining. Geometry is unaffected.
        public bool WarpEnabled;
        public TgNoiseKind WarpNoise = TgNoiseKind.Fbm;
        public float WarpScale = 64f;
        public float WarpAmount = 12f;
        public int WarpOctaves = 4;

        // Compositing.
        public TgBlend Blend = TgBlend.Normal;
        public float Opacity = 1f;
        public float Feather = 0f;          // soft edge band (px)
        public float Blur = 0f;             // layer blur radius (px)

        // Outer glow.
        public bool GlowEnabled;
        public float GlowRadius = 12f;
        public Color GlowColor = Color.white;
        public float GlowIntensity = 1f;
    }

    /// <summary>The full render description produced from one instruction list.</summary>
    public sealed class TgSpec
    {
        public int Width = 256;
        public int Height = 256;
        public Color Background = new Color(0f, 0f, 0f, 0f);
        public TgOrigin Origin = TgOrigin.TopLeft;
        public int Supersample = 2;         // 1..4, internal SSAA factor
        public bool NormalMap;              // render as a normal map
        public float NormalStrength = 1f;
        /// <summary>
        /// Make every periodic fill wrap at the canvas borders, so the PNG tiles
        /// seamlessly. Noise periods snap to whole lattice cells.
        /// </summary>
        public bool Tiling;
        public readonly List<TgLayer> Layers = new List<TgLayer>();

        public const int MaxDimension = 2048;
        public const int MaxLayers = 512;
    }

    /// <summary>Tolerant readers over the SimpleJson tree.</summary>
    internal static class TgJson
    {
        public static bool Has(JsonClass node, string key)
        {
            return node != null && node.TryGetValue(key, out JsonNode v) && v != null;
        }

        public static JsonNode Get(JsonClass node, string key)
        {
            if (node == null) return null;
            return node.TryGetValue(key, out JsonNode v) ? v : null;
        }

        public static string GetString(JsonClass node, string key, string fallback = null)
        {
            var v = Get(node, key);
            if (v == null) return fallback;
            string s = v.Value;
            return string.IsNullOrEmpty(s) ? fallback : s.Trim();
        }

        public static float GetFloat(JsonClass node, string key, float fallback)
        {
            var v = Get(node, key);
            if (v == null) return fallback;
            if (v.type == JsonNodeType.String)
            {
                return float.TryParse(v.Value, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out float parsed) ? parsed : fallback;
            }
            return v.AsFloatDefault(fallback);
        }

        public static int GetInt(JsonClass node, string key, int fallback)
        {
            var v = Get(node, key);
            if (v == null) return fallback;
            if (v.type == JsonNodeType.String)
            {
                return int.TryParse(v.Value, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out int parsed) ? parsed : fallback;
            }
            return v.AsIntDefault(fallback);
        }

        public static bool GetBool(JsonClass node, string key, bool fallback)
        {
            var v = Get(node, key);
            if (v == null) return fallback;
            if (v.type == JsonNodeType.String)
            {
                string s = v.Value;
                if (string.Equals(s, "true", StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(s, "false", StringComparison.OrdinalIgnoreCase)) return false;
                if (s == "1") return true;
                if (s == "0") return false;
                return fallback;
            }
            return v.AsBoolDefault(fallback);
        }

        /// <summary>Reads [x,y] arrays or "x,y" strings; returns false when absent.</summary>
        public static bool TryGetVector2(JsonClass node, string key, out Vector2 value)
        {
            value = Vector2.zero;
            var v = Get(node, key);
            if (v == null) return false;

            if (v is JsonArray arr && arr.Count >= 2)
            {
                value = new Vector2(arr[0].AsFloatDefault(0f), arr[1].AsFloatDefault(0f));
                return true;
            }

            return TryParseVector2(v.Value, out value);
        }

        public static bool TryParseVector2(string raw, out Vector2 value)
        {
            value = Vector2.zero;
            if (string.IsNullOrEmpty(raw)) return false;

            string s = raw.Replace('[', ' ').Replace(']', ' ').Replace(',', ' ');
            var parts = s.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 &&
                float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) &&
                float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y))
            {
                value = new Vector2(x, y);
                return true;
            }
            return false;
        }

        /// <summary>Reads a point list from [[x,y],...] or "x,y;x,y".</summary>
        public static List<Vector2> GetPoints(JsonClass node, string key)
        {
            var v = Get(node, key);
            if (v == null) return null;

            var result = new List<Vector2>();
            if (v is JsonArray arr)
            {
                foreach (JsonNode item in arr)
                {
                    if (item is JsonArray pair && pair.Count >= 2)
                        result.Add(new Vector2(pair[0].AsFloatDefault(0f), pair[1].AsFloatDefault(0f)));
                    else if (item != null && TryParseVector2(item.Value, out Vector2 p))
                        result.Add(p);
                }
                return result.Count > 0 ? result : null;
            }

            string raw = v.Value;
            if (string.IsNullOrEmpty(raw)) return null;
            foreach (string chunk in raw.Split(new[] { ';', '|' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (TryParseVector2(chunk, out Vector2 p)) result.Add(p);
            }
            return result.Count > 0 ? result : null;
        }

        /// <summary>Parses "#RGB/#RGBA/#RRGGBB/#RRGGBBAA", "r,g,b[,a]", a name or a JSON array.</summary>
        public static bool TryParseColor(JsonNode node, out Color color)
        {
            color = Color.white;
            if (node == null) return false;

            if (node is JsonArray arr && arr.Count >= 3)
            {
                float denom = 1f;
                for (int i = 0; i < Mathf.Min(arr.Count, 4); i++)
                {
                    if (arr[i].AsFloatDefault(0f) > 1.0001f) { denom = 255f; break; }
                }
                color = new Color(
                    arr[0].AsFloatDefault(0f) / denom,
                    arr[1].AsFloatDefault(0f) / denom,
                    arr[2].AsFloatDefault(0f) / denom,
                    arr.Count >= 4 ? arr[3].AsFloatDefault(1f) / denom : 1f);
                return true;
            }

            string raw = node.Value;
            if (string.IsNullOrEmpty(raw)) return false;

            raw = raw.Trim();
            if (raw.StartsWith("#"))
            {
                string hex = raw.Substring(1);
                if (hex.Length == 3 || hex.Length == 4)
                {
                    hex = string.Concat(hex[0], hex[0], hex[1], hex[1], hex[2], hex[2],
                        hex.Length == 4 ? hex[3].ToString() + hex[3] : "ff");
                }
                else if (hex.Length == 6)
                {
                    hex += "ff";
                }
                if (hex.Length == 8 && TryHex(hex, out color)) return true;
                return false;
            }

            var csv = raw.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (csv.Length >= 3 &&
                float.TryParse(csv[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float r) &&
                float.TryParse(csv[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float g) &&
                float.TryParse(csv[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float b))
            {
                float denom = (r > 1.0001f || g > 1.0001f || b > 1.0001f) ? 255f : 1f;
                float a = 1f;
                if (csv.Length >= 4 && float.TryParse(csv[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float av))
                    a = av > 1.0001f ? av / 255f : av;
                color = new Color(r / denom, g / denom, b / denom, Mathf.Clamp01(a));
                return true;
            }

            return ColorUtility.TryParseHtmlString(raw, out color);
        }

        private static bool TryHex(string hex, out Color color)
        {
            color = Color.white;
            if (!uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint value))
                return false;
            color = new Color(
                ((value >> 24) & 0xFF) / 255f,
                ((value >> 16) & 0xFF) / 255f,
                ((value >> 8) & 0xFF) / 255f,
                (value & 0xFF) / 255f);
            return true;
        }

        public static Color GetColor(JsonClass node, string key, Color fallback)
        {
            var v = Get(node, key);
            return TryParseColor(v, out Color c) ? c : fallback;
        }

        public static TEnum GetEnum<TEnum>(JsonClass node, string key, TEnum fallback) where TEnum : struct
        {
            string raw = GetString(node, key);
            if (string.IsNullOrEmpty(raw)) return fallback;
            raw = raw.Replace("_", "").Replace("-", "").Trim();
            foreach (var name in Enum.GetNames(typeof(TEnum)))
            {
                if (string.Equals(name, raw, StringComparison.OrdinalIgnoreCase))
                    return (TEnum)Enum.Parse(typeof(TEnum), name);
            }
            return fallback;
        }
    }
}
