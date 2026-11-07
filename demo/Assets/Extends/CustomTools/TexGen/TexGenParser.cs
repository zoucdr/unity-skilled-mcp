using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace UniMcp.TexGen
{
    /// <summary>Outcome of parsing a drawing instruction list.</summary>
    public sealed class TgParseResult
    {
        public TgSpec Spec;
        public readonly List<string> Errors = new List<string>();
        public readonly List<string> Warnings = new List<string>();
        public bool Ok => Spec != null && Errors.Count == 0;
    }

    /// <summary>
    /// Turns a JSON instruction list into a <see cref="TgSpec"/>.
    ///
    /// The input is deliberately a structured command list rather than free text:
    /// an LLM reads the schema once (via the `texgen.spec` action), reasons about
    /// the picture, and emits primitives. Parsing stays deterministic and total —
    /// unreadable fields fall back to defaults and are reported as warnings.
    /// </summary>
    public static class TgParser
    {
        public static TgParseResult Parse(JsonNode root)
        {
            var result = new TgParseResult();
            var spec = new TgSpec();

            if (root == null)
            {
                result.Errors.Add("Instruction list is empty. Pass {\"layers\":[...]}.");
                result.Spec = spec;
                return result;
            }

            JsonClass obj = root as JsonClass;
            if (obj == null)
            {
                result.Errors.Add("Instruction list must be a JSON object with a \"layers\" array.");
                result.Spec = spec;
                return result;
            }

            spec.Width = Mathf.Clamp(TgJson.GetInt(obj, "width", spec.Width), 1, TgSpec.MaxDimension);
            spec.Height = Mathf.Clamp(TgJson.GetInt(obj, "height", spec.Height), 1, TgSpec.MaxDimension);
            spec.Background = TgJson.GetColor(obj, "background", new Color(0f, 0f, 0f, 0f));
            spec.Origin = TgJson.GetEnum(obj, "origin", TgOrigin.TopLeft);
            spec.Supersample = Mathf.Clamp(TgJson.GetInt(obj, "supersample", spec.Supersample), 1, 4);
            spec.NormalMap = TgJson.GetBool(obj, "normalMap", false) || TgJson.GetBool(obj, "normal_map", false);
            spec.NormalStrength = TgJson.GetFloat(obj, "normalStrength",
                TgJson.GetFloat(obj, "normal_strength", spec.NormalStrength));
            spec.Tiling = TgJson.GetBool(obj, "tiling", false)
                || TgJson.GetBool(obj, "tileable", false)
                || TgJson.GetBool(obj, "seamless", false);

            JsonNode layersNode = TgJson.Get(obj, "layers");
            if (!(layersNode is JsonArray layers))
            {
                result.Errors.Add("\"layers\" must be an array of layer objects.");
                result.Spec = spec;
                return result;
            }

            if (layers.Count > TgSpec.MaxLayers)
            {
                result.Errors.Add($"Too many layers ({layers.Count}); the limit is {TgSpec.MaxLayers}.");
                result.Spec = spec;
                return result;
            }

            for (int i = 0; i < layers.Count; i++)
            {
                JsonClass node = layers[i] as JsonClass;
                if (node == null)
                {
                    result.Errors.Add($"Layer {i} is not an object.");
                    continue;
                }

                TgLayer layer = ParseLayer(node, i, result);
                if (layer != null) spec.Layers.Add(layer);
            }

            result.Spec = spec;
            return result;
        }

        private static TgLayer ParseLayer(JsonClass node, int index, TgParseResult result)
        {
            var layer = new TgLayer();
            layer.Name = TgJson.GetString(node, "name", "layer" + index);

            // A layer may instead be a stencil that clips or punches the backdrop.
            string blendRaw = TgJson.GetString(node, "blend", "normal");
            layer.Blend = TgJson.GetEnum(node, "blend", TgBlend.Normal);
            if (Matches(blendRaw, "mask", "clip", "stencil"))
            {
                layer.Blend = TgBlend.Mask;
            }
            else if (Matches(blendRaw, "erase", "maskout", "mask_out", "cutout", "cut_out", "hole"))
            {
                layer.Blend = TgBlend.Erase;
            }

            layer.Shape = TgJson.GetEnum(node, "shape", TgShape.Circle);

            if (!TgJson.TryGetVector2(node, "center", out layer.Center))
            {
                // Accept "at": [x, y] as an alias.
                TgJson.TryGetVector2(node, "at", out layer.Center);
            }

            layer.Radius = TgJson.GetFloat(node, "radius", layer.Radius);
            layer.InnerRadius = TgJson.GetFloat(node, "innerRadius",
                TgJson.GetFloat(node, "inner_radius", layer.InnerRadius));
            layer.Thickness = TgJson.GetFloat(node, "thickness", layer.Thickness);
            layer.RadiusX = TgJson.GetFloat(node, "radiusX", TgJson.GetFloat(node, "radius_x", 0f));
            layer.RadiusY = TgJson.GetFloat(node, "radiusY", TgJson.GetFloat(node, "radius_y", 0f));

            if (TgJson.TryGetVector2(node, "size", out Vector2 size)) layer.Size = size;
            layer.CornerRadius = TgJson.GetFloat(node, "cornerRadius",
                TgJson.GetFloat(node, "corner_radius", layer.CornerRadius));

            TgJson.TryGetVector2(node, "from", out layer.From);
            TgJson.TryGetVector2(node, "to", out layer.To);
            layer.Width = TgJson.GetFloat(node, "width", layer.Width);
            layer.RoundCap = TgJson.GetBool(node, "roundCap", TgJson.GetBool(node, "round_cap", true));

            var points = TgJson.GetPoints(node, "points");
            if (points != null) layer.Points = points.ToArray();
            layer.PointCount = Mathf.Clamp(TgJson.GetInt(node, "points_count",
                TgJson.GetInt(node, "spikes", layer.PointCount)), 3, 64);

            layer.Rotation = TgJson.GetFloat(node, "rotation", layer.Rotation);

            // Fill + paint.
            layer.Fill = TgJson.GetEnum(node, "fill", TgFill.Solid);
            layer.ColorA = ReadPaint(node, "color", "centerColor", "center_color", layer.ColorA);
            layer.ColorB = ReadPaint(node, "edgeColor", "edge_color", "color2",
                TgJson.GetColor(node, "toColor", layer.ColorB));

            JsonNode c3 = TgJson.Get(node, "midColor") ?? TgJson.Get(node, "mid_color");
            if (TgJson.TryParseColor(c3, out Color mid))
            {
                layer.ColorC = mid;
                layer.HasColorC = true;
            }

            layer.Gradient = ParseGradient(node);
            layer.Noise = TgJson.GetEnum(node, "noise", TgNoiseKind.Fbm);
            layer.Seed = TgJson.GetInt(node, "seed", layer.Seed);
            layer.Octaves = Mathf.Clamp(TgJson.GetInt(node, "octaves", layer.Octaves), 1, 10);
            layer.Scale = TgJson.GetFloat(node, "scale", layer.Scale);
            layer.Frequency = TgJson.GetFloat(node, "frequency", layer.Frequency);
            layer.Phase = TgJson.GetFloat(node, "phase", layer.Phase);
            layer.Amplitude = TgJson.GetFloat(node, "amplitude", layer.Amplitude);
            if (TgJson.TryGetVector2(node, "cell", out Vector2 cell)) layer.GridCell = cell;
            layer.GridLineWidth = TgJson.GetFloat(node, "lineWidth",
                TgJson.GetFloat(node, "gridLineWidth", layer.GridLineWidth));

            // Pattern fill. "cell" doubles as the pattern period, "lineWidth" as the
            // seam width when no explicit gap is given.
            layer.Pattern = ReadPattern(node);
            layer.PatternGap = TgJson.GetFloat(node, "gap",
                TgJson.GetFloat(node, "patternGap",
                TgJson.GetFloat(node, "pattern_gap",
                TgJson.GetFloat(node, "mortar", layer.GridLineWidth))));
            layer.PatternGapColor = ReadPaint(node, "gapColor", "gap_color", "mortarColor", layer.PatternGapColor);
            layer.PatternStagger = TgJson.GetFloat(node, "stagger",
                TgJson.GetFloat(node, "offset", layer.PatternStagger));
            layer.PatternVariation = TgJson.GetFloat(node, "variation",
                TgJson.GetFloat(node, "jitter", layer.PatternVariation));
            layer.PatternRoundness = TgJson.GetFloat(node, "roundness", layer.PatternRoundness);

            // Domain warp: object form, or "warp": true with sibling keys.
            JsonNode warpNode = TgJson.Get(node, "warp");
            if (warpNode is JsonClass warpObj)
            {
                layer.WarpEnabled = TgJson.GetBool(warpObj, "enabled", true);
                layer.WarpNoise = TgJson.GetEnum(warpObj, "noise",
                    TgJson.GetEnum(warpObj, "kind", layer.WarpNoise));
                layer.WarpScale = TgJson.GetFloat(warpObj, "scale", layer.WarpScale);
                layer.WarpAmount = TgJson.GetFloat(warpObj, "amount",
                    TgJson.GetFloat(warpObj, "strength", layer.WarpAmount));
                layer.WarpOctaves = Mathf.Clamp(TgJson.GetInt(warpObj, "octaves", layer.WarpOctaves), 1, 10);
            }
            else if (warpNode != null && TgJson.GetBool(node, "warp", false))
            {
                layer.WarpEnabled = true;
                layer.WarpScale = TgJson.GetFloat(node, "warpScale",
                    TgJson.GetFloat(node, "warp_scale", layer.WarpScale));
                layer.WarpAmount = TgJson.GetFloat(node, "warpAmount",
                    TgJson.GetFloat(node, "warp_amount", layer.WarpAmount));
                layer.WarpOctaves = Mathf.Clamp(TgJson.GetInt(node, "warpOctaves", layer.WarpOctaves), 1, 10);
            }

            // Compositing.
            layer.Opacity = Mathf.Clamp01(TgJson.GetFloat(node, "opacity", layer.Opacity));
            layer.Feather = Mathf.Max(TgJson.GetFloat(node, "feather", layer.Feather), 0f);
            layer.Blur = Mathf.Max(TgJson.GetFloat(node, "blur", layer.Blur), 0f);

            JsonClass glow = TgJson.Get(node, "glow") as JsonClass;
            if (glow != null)
            {
                layer.GlowEnabled = TgJson.GetBool(glow, "enabled", true);
                layer.GlowRadius = Mathf.Max(TgJson.GetFloat(glow, "radius", layer.GlowRadius), 0f);
                layer.GlowColor = TgJson.GetColor(glow, "color", layer.ColorA);
                layer.GlowIntensity = TgJson.GetFloat(glow, "intensity", layer.GlowIntensity);
            }

            // Guard against geometry that would silently render nothing.
            if (layer.Shape == TgShape.Line && layer.From == layer.To)
                result.Warnings.Add($"Layer '{layer.Name}': \"from\" and \"to\" are equal; the line will be a dot.");
            if (layer.Shape == TgShape.Polygon && (layer.Points == null || layer.Points.Length < 3))
                result.Errors.Add($"Layer '{layer.Name}': a polygon needs at least three \"points\".");
            if (layer.Shape == TgShape.Ring && layer.Thickness <= 0f && layer.InnerRadius >= layer.Radius)
                result.Errors.Add($"Layer '{layer.Name}': ring \"innerRadius\" must be smaller than \"radius\".");

            return layer;
        }

        /// <summary>
        /// Reads the pattern name, accepting the aliases an LLM is likely to emit
        /// ("wood_floor", "honeycomb", "fishscale", ...) so a near-miss still draws.
        /// </summary>
        private static TgPattern ReadPattern(JsonClass node)
        {
            string raw = TgJson.GetString(node, "pattern");
            if (string.IsNullOrEmpty(raw)) return TgJson.GetEnum(node, "pattern", TgPattern.Brick);

            string key = raw.Replace("_", "").Replace("-", "").Replace(" ", "").ToLowerInvariant();
            switch (key)
            {
                case "brick": case "bricks": case "masonry": case "runningbond": return TgPattern.Brick;
                case "plank": case "planks": case "board": case "boards": case "woodfloor":
                case "floorboard": return TgPattern.Plank;
                case "tile": case "tiles": case "ceramic": case "grid": return TgPattern.Tile;
                case "checker": case "chequer": case "chess": case "checkerboard": return TgPattern.Checker;
                case "stripe": case "stripes": case "band": case "bands": case "pinstripe": return TgPattern.Stripe;
                case "hex": case "hexagon": case "hexagons": case "honeycomb": return TgPattern.Hex;
                case "scale": case "scales": case "fishscale": case "shingle": case "shingles":
                case "roof": return TgPattern.Scale;
                case "weave": case "woven": case "cloth": case "fabric": case "linen":
                case "canvas": return TgPattern.Weave;
                case "dot": case "dots": case "polka": case "poke": case "spot": case "spots": return TgPattern.Dot;
                case "cobble": case "cobblestone": case "pebble": case "stone": case "gravel": return TgPattern.Cobble;
                default: return TgJson.GetEnum(node, "pattern", TgPattern.Brick);
            }
        }

        private static Color ReadPaint(JsonClass node, string primary, string aliasA, string aliasB, Color fallback)
        {
            JsonNode v = TgJson.Get(node, primary)
                ?? TgJson.Get(node, aliasA)
                ?? TgJson.Get(node, aliasB);
            return TgJson.TryParseColor(v, out Color c) ? c : fallback;
        }

        private static bool Matches(string value, params string[] candidates)
        {
            if (string.IsNullOrEmpty(value)) return false;
            foreach (string candidate in candidates)
            {
                if (string.Equals(value, candidate, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static TgGradient ParseGradient(JsonClass node)
        {
            JsonNode raw = TgJson.Get(node, "gradient");
            if (raw == null)
            {
                // A "stops" field is the same thing under another name.
                raw = TgJson.Get(node, "stops");
            }
            if (raw == null) return null;

            var grad = new TgGradient();

            if (raw is JsonArray arr)
            {
                foreach (JsonNode stop in arr)
                {
                    if (stop is JsonClass stopObj)
                    {
                        float offset = Mathf.Clamp01(TgJson.GetFloat(stopObj, "offset",
                            TgJson.GetFloat(stopObj, "t", 0f)));
                        Color col = TgJson.GetColor(stopObj, "color", Color.white);
                        grad.Add(offset, col);
                    }
                    else if (stop is JsonArray pair && pair.Count >= 2)
                    {
                        float offset = Mathf.Clamp01(pair[0].AsFloatDefault(0f));
                        grad.Add(offset, TgJson.TryParseColor(pair[1], out Color c) ? c : Color.white);
                    }
                }
            }
            else
            {
                // "gradient": {"0": "#000", "0.5": "#f00", "1": "#fff"}
                if (raw is JsonClass map)
                {
                    foreach (var kv in map.AsEnumerable())
                    {
                        if (float.TryParse(kv.Key, NumberStyles.Float, CultureInfo.InvariantCulture, out float offset))
                        {
                            grad.Add(Mathf.Clamp01(offset),
                                TgJson.TryParseColor(kv.Value, out Color c) ? c : Color.white);
                        }
                    }
                }
            }

            if (grad.Count == 0) return null;
            grad.Sort();
            return grad;
        }
    }
}
