using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace UniMcp.TexGen
{
    /// <summary>
    /// Rasterises a <see cref="TgSpec"/> into pixels and exports PNG.
    ///
    /// The renderer is intentionally self-contained: coverage comes from analytic
    /// signed-distance functions, antialiasing from supersampling plus a coverage
    /// ramp, and every fill is a pure function of the canvas coordinate. No
    /// shaders, no GPU, no third-party packages — only Texture2D and the
    /// filesystem.
    /// </summary>
    public static class TgRasterizer
    {
        /// <summary>Safety ceiling so a huge canvas cannot allocate gigabytes.</summary>
        private const long MaxSamplePixels = 16_000_000L;

        /// <summary>Render the spec to a Texture2D. The caller owns the texture.</summary>
        public static Texture2D Render(TgSpec spec, out int usedSupersample)
        {
            int w = Mathf.Clamp(spec.Width, 1, TgSpec.MaxDimension);
            int h = Mathf.Clamp(spec.Height, 1, TgSpec.MaxDimension);
            spec.Width = w;
            spec.Height = h;

            int ss = Mathf.Clamp(spec.Supersample, 1, 4);
            while (ss > 1 && (long)w * h * ss * ss > MaxSamplePixels) ss--;
            usedSupersample = ss;

            int sw = w * ss;
            int sh = h * ss;

            var canvas = new float[sw * sh * 4];
            Color bg = spec.Background;
            for (int i = 0; i < sw * sh; i++)
            {
                // Store premultiplied so compositing stays associative.
                canvas[i * 4 + 0] = bg.r * bg.a;
                canvas[i * 4 + 1] = bg.g * bg.a;
                canvas[i * 4 + 2] = bg.b * bg.a;
                canvas[i * 4 + 3] = bg.a;
            }

            for (int i = 0; i < spec.Layers.Count; i++)
            {
                DrawLayer(canvas, sw, sh, ss, spec, spec.Layers[i]);
            }

            float[] down = Downsample(canvas, sw, sh, ss);
            float[] final = spec.NormalMap ? ToNormalMap(down, w, h, spec.NormalStrength) : down;

            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false, false);
            tex.LoadRawTextureData(ToBytes(final, w, h));
            tex.Apply(false, false);
            tex.name = "TexGen";
            return tex;
        }

        /// <summary>Encode a texture to PNG bytes.</summary>
        public static byte[] EncodePng(Texture2D tex)
        {
            return tex.EncodeToPNG();
        }

        /// <summary>Write PNG bytes to disk, creating the directory when needed.</summary>
        public static void WritePng(string absolutePath, byte[] png)
        {
            string dir = Path.GetDirectoryName(absolutePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllBytes(absolutePath, png);
        }

        /// <summary>Wrap a finished pixel buffer in a Texture2D (caller owns it).</summary>
        public static Texture2D TextureFromColors(Color[] pixels, int w, int h)
        {
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false, false);
            tex.SetPixels(pixels);
            tex.Apply(false, false);
            return tex;
        }

        /// <summary>Wrap a float RGBA buffer (0..1, straight alpha) in a Texture2D.</summary>
        public static Texture2D TextureFromColors(float[] rgba, int w, int h)
        {
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false, false);
            tex.LoadRawTextureData(ToBytes(rgba, w, h));
            tex.Apply(false, false);
            return tex;
        }

        // ------------------------------------------------------------------
        // Layer pipeline
        // ------------------------------------------------------------------

        private static void DrawLayer(float[] canvas, int sw, int sh, int ss, TgSpec spec, TgLayer layer)
        {
            var src = new float[sw * sh * 4];
            RasterizeGeometry(src, sw, sh, ss, spec, layer);

            if (layer.Blur > 0.01f) Blur(src, sw, sh, layer.Blur * ss);

            if (layer.GlowEnabled && layer.GlowRadius > 0.01f)
            {
                var glow = new float[sw * sh * 4];
                for (int i = 0; i < sw * sh; i++)
                {
                    float a = src[i * 4 + 3] * layer.GlowIntensity;
                    glow[i * 4 + 0] = layer.GlowColor.r * a;
                    glow[i * 4 + 1] = layer.GlowColor.g * a;
                    glow[i * 4 + 2] = layer.GlowColor.b * a;
                    glow[i * 4 + 3] = a;
                }
                Blur(glow, sw, sh, layer.GlowRadius * ss);
                Composite(canvas, glow, sw * sh, TgBlend.Add, 1f);
            }

            Composite(canvas, src, sw * sh, layer.Blend, Mathf.Clamp01(layer.Opacity));
        }

        /// <summary>
        /// W3C compositing over premultiplied buffers: apply the blend operator
        /// against the backdrop, then source-over with the blended colour. Reduces
        /// to plain source-over for <see cref="TgBlend.Normal"/>.
        /// </summary>
        private static void Composite(float[] dst, float[] src, int pixels, TgBlend blend, float opacity)
        {
            bool stencil = blend == TgBlend.Mask || blend == TgBlend.Erase;

            for (int i = 0; i < pixels; i++)
            {
                int o = i * 4;

                // Unpremultiply the source; opacity scales alpha only.
                float sa = Mathf.Clamp01(src[o + 3]);
                float csr = sa > 1e-6f ? src[o + 0] / sa : 0f;
                float csg = sa > 1e-6f ? src[o + 1] / sa : 0f;
                float csb = sa > 1e-6f ? src[o + 2] / sa : 0f;

                float ab = Mathf.Clamp01(dst[o + 3]);
                float cbr = ab > 1e-6f ? dst[o + 0] / ab : 0f;
                float cbg = ab > 1e-6f ? dst[o + 1] / ab : 0f;
                float cbb = ab > 1e-6f ? dst[o + 2] / ab : 0f;

                if (stencil)
                {
                    // Mask keeps the backdrop only inside the shape; Erase removes
                    // the backdrop inside the shape. Colour is untouched either way.
                    float coverage = Mathf.Clamp01(sa * opacity);
                    float keep = blend == TgBlend.Mask ? coverage : 1f - coverage;
                    float na = Mathf.Clamp01(ab * keep);
                    dst[o + 3] = na;
                    dst[o + 0] = cbr * na;
                    dst[o + 1] = cbg * na;
                    dst[o + 2] = cbb * na;
                    continue;
                }

                float asr = Mathf.Clamp01(sa * opacity);
                if (asr <= 1e-6f) continue;

                float ao = asr + ab * (1f - asr);
                if (ao <= 1e-6f)
                {
                    dst[o] = dst[o + 1] = dst[o + 2] = dst[o + 3] = 0f;
                    continue;
                }

                // Where the backdrop exists the blend operator mixes, otherwise the
                // source colour stands alone; then normal source-over weighting.
                float mixR = ab > 0f ? Blend(blend, cbr, csr) : csr;
                float mixG = ab > 0f ? Blend(blend, cbg, csg) : csg;
                float mixB = ab > 0f ? Blend(blend, cbb, csb) : csb;

                float backR = ab > 0f ? cbr : 0f;
                float backG = ab > 0f ? cbg : 0f;
                float backB = ab > 0f ? cbb : 0f;

                float or = ((1f - asr) * ab * backR + asr * ((1f - ab) * csr + ab * mixR)) / ao;
                float og = ((1f - asr) * ab * backG + asr * ((1f - ab) * csg + ab * mixG)) / ao;
                float ob = ((1f - asr) * ab * backB + asr * ((1f - ab) * csb + ab * mixB)) / ao;

                dst[o + 3] = ao;
                dst[o + 0] = Mathf.Clamp01(or) * ao;
                dst[o + 1] = Mathf.Clamp01(og) * ao;
                dst[o + 2] = Mathf.Clamp01(ob) * ao;
            }
        }

        private static float Blend(TgBlend mode, float b, float s)
        {
            switch (mode)
            {
                case TgBlend.Add: return Mathf.Clamp01(b + s);
                case TgBlend.Multiply: return b * s;
                case TgBlend.Screen: return 1f - (1f - b) * (1f - s);
                case TgBlend.Overlay:
                    return b < 0.5f ? 2f * b * s : 1f - 2f * (1f - b) * (1f - s);
                case TgBlend.Subtract: return Mathf.Clamp01(b - s);
                case TgBlend.Difference: return Mathf.Abs(b - s);
                case TgBlend.Darken: return Mathf.Min(b, s);
                case TgBlend.Lighten: return Mathf.Max(b, s);
                default: return s;
            }
        }

        // ------------------------------------------------------------------
        // Geometry + fill
        // ------------------------------------------------------------------

        private static void RasterizeGeometry(float[] src, int sw, int sh, int ss, TgSpec spec, TgLayer layer)
        {
            Vector2[] poly = layer.Shape == TgShape.Star ? BuildStar(layer)
                : layer.Shape == TgShape.Polygon ? (layer.Points ?? new Vector2[0])
                : null;
            int pointCount = poly == null ? 0 : poly.Length;

            bool maskBlend = layer.Blend == TgBlend.Mask;
            float originX = spec.Origin == TgOrigin.Center ? spec.Width * 0.5f : 0f;
            float originY = spec.Origin == TgOrigin.Center ? spec.Height * 0.5f : 0f;
            var origin = new Vector2(originX, originY);

            // Edge transition width in canvas pixels: the soft band when
            // requested, otherwise one sample pixel (plain antialiasing).
            float band = layer.Feather > 0f ? layer.Feather : 1f / ss;
            bool smooth = layer.Feather > 0f;
            float invSs = 1f / ss;

            for (int y = 0; y < sh; y++)
            {
                float cy = (y + 0.5f) * invSs;
                for (int x = 0; x < sw; x++)
                {
                    float cx = (x + 0.5f) * invSs;
                    Vector2 p = new Vector2(cx, cy) - origin;

                    float sdf = SignedDistance(layer, poly, pointCount, p);
                    float cov = Mathf.Clamp01(0.5f - sdf / band);
                    if (cov <= 0f) continue;
                    if (smooth) cov = cov * cov * (3f - 2f * cov);

                    int idx = (y * sw + x) * 4;

                    if (maskBlend)
                    {
                        src[idx + 3] = Mathf.Max(src[idx + 3], cov);
                        continue;
                    }

                    Color c = EvaluateFill(layer, layer.WarpEnabled ? WarpPoint(layer, p, spec) : p, spec);
                    float a = cov * Mathf.Clamp01(c.a);
                    src[idx + 0] = c.r * a;
                    src[idx + 1] = c.g * a;
                    src[idx + 2] = c.b * a;
                    src[idx + 3] = a;
                }
            }
        }

        private static Vector2[] BuildStar(TgLayer layer)
        {
            int spikes = Mathf.Clamp(layer.PointCount, 3, 64);
            float outer = layer.Radius;
            float inner = layer.InnerRadius > 0f ? layer.InnerRadius : outer * 0.45f;
            var pts = new Vector2[spikes * 2];
            float rot = layer.Rotation * Mathf.Deg2Rad;
            for (int i = 0; i < spikes * 2; i++)
            {
                float r = (i % 2 == 0) ? outer : inner;
                float a = rot - Mathf.PI * 0.5f + i * Mathf.PI / spikes;
                pts[i] = new Vector2(Mathf.Cos(a) * r, Mathf.Sin(a) * r);
            }
            return pts;
        }

        private static float SignedDistance(TgLayer layer, Vector2[] poly, int pointCount, Vector2 p)
        {
            Vector2 c = layer.Center;
            switch (layer.Shape)
            {
                case TgShape.Circle:
                    return Vector2.Distance(p, c) - layer.Radius;

                case TgShape.Ellipse:
                {
                    float rx = layer.RadiusX > 0f ? layer.RadiusX : layer.Radius;
                    float ry = layer.RadiusY > 0f ? layer.RadiusY : layer.Radius;
                    Vector2 d = new Vector2((p.x - c.x) / Mathf.Max(rx, 1e-4f),
                                            (p.y - c.y) / Mathf.Max(ry, 1e-4f));
                    return (d.magnitude - 1f) * Mathf.Min(rx, ry);
                }

                case TgShape.Ring:
                {
                    float half = layer.Thickness > 0f
                        ? layer.Thickness * 0.5f
                        : Mathf.Max((layer.Radius - layer.InnerRadius) * 0.5f, 0.5f);
                    float mid = layer.Thickness > 0f
                        ? layer.Radius
                        : (layer.Radius + layer.InnerRadius) * 0.5f;
                    return Mathf.Abs(Vector2.Distance(p, c) - mid) - half;
                }

                case TgShape.Rect:
                    return Box(p - c, layer.Size * 0.5f, 0f);

                case TgShape.RoundRect:
                    return Box(p - c, layer.Size * 0.5f, Mathf.Max(layer.CornerRadius, 0f));

                case TgShape.Line:
                    return Segment(p, layer.From, layer.To, Mathf.Max(layer.Width * 0.5f, 0.05f), layer.RoundCap);

                case TgShape.Polygon:
                case TgShape.Star:
                    return PolygonDistance(p - c, poly, pointCount);

                default:
                    return 1f;
            }
        }

        private static float Box(Vector2 d, Vector2 half, float radius)
        {
            float hx = Mathf.Max(half.x - radius, 0f);
            float hy = Mathf.Max(half.y - radius, 0f);
            Vector2 q = new Vector2(Mathf.Abs(d.x) - hx, Mathf.Abs(d.y) - hy);
            float outside = new Vector2(Mathf.Max(q.x, 0f), Mathf.Max(q.y, 0f)).magnitude;
            float inside = Mathf.Min(Mathf.Max(q.x, q.y), 0f);
            return outside + inside - radius;
        }

        /// <summary>Round- or flat-capped thick segment.</summary>
        private static float Segment(Vector2 p, Vector2 a, Vector2 b, float half, bool roundCap)
        {
            Vector2 ba = b - a;
            float len = ba.magnitude;
            if (len < 1e-6f) return Vector2.Distance(p, a) - half;

            Vector2 dir = ba / len;
            Vector2 pa = p - a;
            float t = Vector2.Dot(pa, dir);
            Vector2 lateral = pa - dir * t;
            float lat = lateral.magnitude;

            if (roundCap)
            {
                float h = Mathf.Clamp(t, 0f, len);
                return (pa - dir * h).magnitude - half;
            }

            // Flat cap: SDF of a box in the segment's local frame.
            float ax = Mathf.Abs(t - len * 0.5f) - len * 0.5f;
            float ay = lat - half;
            float outside = new Vector2(Mathf.Max(ax, 0f), Mathf.Max(ay, 0f)).magnitude;
            float inside = Mathf.Min(Mathf.Max(ax, ay), 0f);
            return outside + inside;
        }

        private static float PolygonDistance(Vector2 p, Vector2[] pts, int n)
        {
            if (pts == null || n < 3) return 1e6f;

            float minDist = float.MaxValue;
            bool inside = false;

            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                Vector2 a = pts[i];
                Vector2 b = pts[j];

                Vector2 pa = p - a;
                Vector2 ba = b - a;
                float denom = Vector2.Dot(ba, ba);
                float h = denom > 1e-9f ? Mathf.Clamp01(Vector2.Dot(pa, ba) / denom) : 0f;
                float d = (pa - ba * h).magnitude;
                if (d < minDist) minDist = d;

                if (((a.y > p.y) != (b.y > p.y)) &&
                    (p.x < (b.x - a.x) * (p.y - a.y) / (b.y - a.y + 1e-12f) + a.x))
                {
                    inside = !inside;
                }
            }

            return inside ? -minDist : minDist;
        }

        private static Color EvaluateFill(TgLayer layer, Vector2 p, TgSpec spec)
        {
            switch (layer.Fill)
            {
                case TgFill.Solid:
                    return layer.ColorA;

                case TgFill.Linear:
                {
                    float ang = layer.Rotation * Mathf.Deg2Rad;
                    Vector2 dir = new Vector2(Mathf.Cos(ang), Mathf.Sin(ang));
                    float len = layer.Size.x > 0f ? layer.Size.x : Mathf.Max(spec.Width, spec.Height);
                    float t = Vector2.Dot(p - layer.Center, dir) / len + 0.5f;
                    return GradientColor(layer, t);
                }

                case TgFill.Radial:
                {
                    float r = layer.Radius > 0f ? layer.Radius : 64f;
                    return GradientColor(layer, Vector2.Distance(p, layer.Center) / r);
                }

                case TgFill.Noise:
                    return Ramp(layer, SampleNoise(layer, p, Mathf.Max(layer.Scale, 0.5f), spec));

                case TgFill.Ripple:
                {
                    float dist = Vector2.Distance(p, layer.Center);
                    float wavelength = 100f / Mathf.Max(layer.Frequency, 0.01f);
                    float t = 0.5f + 0.5f * Mathf.Sin(2f * Mathf.PI * (dist / wavelength + layer.Phase));
                    t = 0.5f + (t - 0.5f) * Mathf.Clamp01(layer.Amplitude);
                    return Ramp(layer, t);
                }

                case TgFill.Grid:
                {
                    float cw = Mathf.Max(layer.GridCell.x, 0.5f);
                    float ch = Mathf.Max(layer.GridCell.y, 0.5f);
                    float lw = Mathf.Max(layer.GridLineWidth, 0.1f);
                    Vector2 rel = p - layer.Center;
                    float mx = Mathf.Abs(Mathf.Repeat(rel.x + cw * 0.5f, cw) - cw * 0.5f);
                    float my = Mathf.Abs(Mathf.Repeat(rel.y + ch * 0.5f, ch) - ch * 0.5f);
                    return (mx <= lw * 0.5f || my <= lw * 0.5f) ? layer.ColorA : layer.ColorB;
                }

                case TgFill.Pattern:
                    return EvaluatePattern(layer, p, spec);

                default:
                    return layer.ColorA;
            }
        }

        // ------------------------------------------------------------------
        // Pattern fill
        // ------------------------------------------------------------------

        /// <summary>
        /// Periodic masonry / fabric / organic patterns. Every branch works in a
        /// local rotated frame, so <c>rotation</c> spins the whole pattern.
        /// </summary>
        private static Color EvaluatePattern(TgLayer layer, Vector2 p, TgSpec spec)
        {
            float cw = Mathf.Max(layer.GridCell.x, 2f);
            float ch = Mathf.Max(layer.GridCell.y, 2f);
            if (spec.Tiling)
            {
                // Only a whole number of cells per canvas can wrap cleanly, so snap
                // the period to the nearest divisor instead of leaving a seam.
                cw = SnapCell(cw, spec.Width);
                ch = SnapCell(ch, spec.Height);
            }

            float ang = -layer.Rotation * Mathf.Deg2Rad;
            // When tiling, the pattern grid is anchored to the canvas origin: the
            // canvas then holds a whole number of cells and the borders line up.
            // Otherwise it is centred on the layer, which is what looks right for
            // an isolated patch.
            Vector2 anchor = spec.Tiling ? Vector2.zero : layer.Center;
            Vector2 rel = Rotate(p - anchor, ang);

            // A cell that straddles the border is visited from both sides, so its
            // jitter index has to wrap too — otherwise the same brick gets two
            // different tints and the seam shows up as a colour step.
            int cols = spec.Tiling ? Mathf.Max(1, Mathf.RoundToInt(spec.Width / cw)) : 0;
            int rows = spec.Tiling ? Mathf.Max(1, Mathf.RoundToInt(spec.Height / ch)) : 0;

            float gap = Mathf.Max(layer.PatternGap, 0f);
            Color gapColor = layer.PatternGapColor;
            Color baseColor = layer.ColorA;
            Color accent = layer.ColorB;

            switch (layer.Pattern)
            {
                case TgPattern.Checker:
                {
                    int cx = Mathf.FloorToInt((rel.x + cw * 0.5f) / cw);
                    int cy = Mathf.FloorToInt((rel.y + ch * 0.5f) / ch);
                    return ((cx + cy) & 1) == 0 ? baseColor : accent;
                }

                case TgPattern.Stripe:
                {
                    float duty = Mathf.Clamp(layer.PatternStagger, 0.02f, 0.98f);
                    float t = Mathf.Repeat(rel.x + cw * 0.5f, cw) / cw;
                    return t < duty ? baseColor : accent;
                }

                case TgPattern.Tile:
                {
                    float x = Mathf.Repeat(rel.x + cw * 0.5f, cw);
                    float y = Mathf.Repeat(rel.y + ch * 0.5f, ch);
                    float hx = Mathf.Min(x, cw - x);
                    float hy = Mathf.Min(y, ch - y);
                    if (hx < gap * 0.5f || hy < gap * 0.5f) return gapColor;

                    int cx = Mathf.FloorToInt((rel.x + cw * 0.5f) / cw);
                    int cy = Mathf.FloorToInt((rel.y + ch * 0.5f) / ch);
                    return JitterWrapped(baseColor, layer, cx, cy, cols, rows);
                }

                case TgPattern.Brick:
                case TgPattern.Plank:
                {
                    float rowH = ch;
                    int rowCount = rows;
                    if (spec.Tiling && layer.Pattern == TgPattern.Brick && (rowCount & 1) != 0)
                    {
                        // Running bond only repeats vertically with an even number of
                        // rows, otherwise the stagger flips across the border.
                        rowCount++;
                        rowH = spec.Height / (float)rowCount;
                    }
                    int row = Mathf.FloorToInt(rel.y / rowH);
                    float offset = layer.Pattern == TgPattern.Plank
                        ? TexNoise.Hash01(row, 0, layer.Seed) * cw
                        : (((row & 1) == 0) ? 0f : Mathf.Clamp01(layer.PatternStagger)) * cw;

                    float sx = rel.x + offset;
                    float x = Mathf.Repeat(sx, cw);
                    float y = Mathf.Repeat(rel.y, rowH);
                    if (x < gap || y < gap) return gapColor;

                    int col = Mathf.FloorToInt(sx / cw);
                    Color c = JitterWrapped(baseColor, layer, col, row, cols, rowCount);
                    if (layer.Pattern == TgPattern.Plank)
                    {
                        // A soft lengthwise sheen keeps long boards from reading flat.
                        float sheen = Mathf.Sin(y / rowH * Mathf.PI);
                        c = Scale(c, 0.88f + 0.24f * sheen);
                    }
                    return c;
                }

                case TgPattern.Hex:
                {
                    // Flat-top honeycomb: columns sit 1.5R apart, rows sqrt(3)R apart,
                    // and every other column is shifted half a row.
                    float R = cw * 0.5f;
                    float hw = 1.5f * R;
                    float vh = Mathf.Sqrt(3f) * R;
                    if (spec.Tiling)
                    {
                        // The stagger alternates per column, so a wrapping honeycomb
                        // needs an even column count; whole rows close the other axis.
                        // Snapping both axes stretches the cells a few percent, which
                        // is invisible next to a torn seam.
                        int nc = Mathf.Max(2, Mathf.RoundToInt(spec.Width / hw));
                        if ((nc & 1) != 0) nc++;
                        int nr = Mathf.Max(1, Mathf.RoundToInt(spec.Height / vh));
                        hw = spec.Width / (float)nc;
                        vh = spec.Height / (float)nr;
                        R = hw / 1.5f;
                    }

                    // Everything below works in normalised units: 0 at a cell centre,
                    // 1 on its edge, so an anisotropic cell still tests correctly.
                    float rx = hw / 1.5f;
                    float ryR = vh / Mathf.Sqrt(3f);

                    int col0 = Mathf.FloorToInt(rel.x / hw);
                    float best = float.MaxValue;
                    int bestCol = 0, bestRow = 0;

                    for (int dc = -1; dc <= 1; dc++)
                    {
                        int col = col0 + dc;
                        float offY = (col & 1) != 0 ? vh * 0.5f : 0f;
                        int row0 = Mathf.FloorToInt((rel.y - offY) / vh);
                        for (int dr = -1; dr <= 1; dr++)
                        {
                            int row = row0 + dr;
                            Vector2 centre = new Vector2(col * hw, row * vh + offY);
                            float d = HexDistanceNorm(rel - centre, rx, ryR);
                            if (d < best) { best = d; bestCol = col; bestRow = row; }
                        }
                    }

                    if (best > 1f - Mathf.Clamp01(gap / rx)) return gapColor;
                    int hexCols = spec.Tiling ? Mathf.Max(1, Mathf.RoundToInt(spec.Width / hw)) : 0;
                    int hexRows = spec.Tiling ? Mathf.Max(1, Mathf.RoundToInt(spec.Height / vh)) : 0;
                    return JitterWrapped(baseColor, layer, bestCol, bestRow, hexCols, hexRows);
                }

                case TgPattern.Scale:
                {
                    float rowH = Mathf.Max(ch * 0.55f, 2f);
                    if (spec.Tiling)
                        rowH = spec.Height / (float)Mathf.Max(1, Mathf.RoundToInt(spec.Height / rowH));

                    float reach = 1f - Mathf.Clamp01(gap / Mathf.Max(cw * 0.5f, 1f));
                    int baseRow = Mathf.FloorToInt(rel.y / rowH);

                    // Rows overlap: a scale from the row above can reach down into
                    // this one, which is what makes the tiles read as shingles
                    // instead of half-discs floating on a dark background.
                    float bestDist = float.MaxValue;
                    int bestCol = 0, bestRow = 0;
                    for (int dr = 0; dr <= 1; dr++)
                    {
                        int row = baseRow - dr;
                        float offX = (row & 1) != 0 ? cw * 0.5f : 0f;
                        float sx = rel.x + offX + cw * 0.5f;
                        float u = Mathf.Repeat(sx, cw) / cw - 0.5f;
                        float v = rel.y / rowH - row;
                        if (v > 1.7f) continue;

                        float rx = u * 1.7f;
                        float ry = v * 0.75f;
                        float dist = Mathf.Sqrt(rx * rx + ry * ry);
                        if (dist < bestDist)
                        {
                            bestDist = dist;
                            bestCol = Mathf.FloorToInt(sx / cw);
                            bestRow = row;
                        }
                    }

                    if (bestDist > reach) return gapColor;
                    int scaleRows = spec.Tiling ? Mathf.Max(1, Mathf.RoundToInt(spec.Height / rowH)) : 0;
                    return Scale(JitterWrapped(baseColor, layer, bestCol, bestRow, cols, scaleRows),
                        0.88f + 0.22f * (1f - Mathf.Clamp01(bestDist)));
                }

                case TgPattern.Weave:
                {
                    int cx = Mathf.FloorToInt((rel.x + cw * 0.5f) / cw);
                    int cy = Mathf.FloorToInt((rel.y + ch * 0.5f) / ch);
                    bool horiz = ((cx + cy) & 1) == 0;
                    float duty = Mathf.Clamp(0.5f + Mathf.Clamp01(layer.PatternStagger) * 0.5f, 0.2f, 0.98f);

                    float across = horiz
                        ? Mathf.Repeat(rel.y + ch * 0.5f, ch) / ch
                        : Mathf.Repeat(rel.x + cw * 0.5f, cw) / cw;
                    if (across > duty) return gapColor;

                    // Cylindrical shading across the thread.
                    float sheen = Mathf.Sin(Mathf.Clamp01(across / duty) * Mathf.PI);
                    Color thread = horiz ? baseColor : accent;
                    return Scale(thread, 0.72f + 0.42f * sheen);
                }

                case TgPattern.Dot:
                {
                    int row = Mathf.FloorToInt(rel.y / ch);
                    float offX = (row & 1) != 0 ? Mathf.Clamp01(layer.PatternStagger) * cw : 0f;
                    float x = Mathf.Repeat(rel.x + offX + cw * 0.5f, cw) / cw - 0.5f;
                    float y = Mathf.Repeat(rel.y + ch * 0.5f, ch) / ch - 0.5f;

                    float radius = Mathf.Clamp(layer.PatternStagger, 0.02f, 1f) * 0.5f;
                    float gapRatio = Mathf.Clamp01(gap / Mathf.Max(cw, 1f));
                    float d = Mathf.Sqrt(x * x + y * y);
                    if (d > radius) return gapColor;
                    return d > radius - gapRatio ? gapColor : baseColor;
                }

                case TgPattern.Cobble:
                {
                    float s = Mathf.Max(cw, 4f);
                    float u = rel.x / s;
                    float v = rel.y / s;
                    int nx = 0, ny = 0;
                    if (spec.Tiling)
                    {
                        // One whole number of cells per axis, so the stone lattice
                        // closes at the border instead of being squeezed.
                        nx = Mathf.Max(1, Mathf.RoundToInt(spec.Width / s));
                        ny = Mathf.Max(1, Mathf.RoundToInt(spec.Height / s));
                        u = rel.x / spec.Width * nx;
                        v = rel.y / spec.Height * ny;
                    }

                    // F2-F1 is small on the seams between neighbouring cells, which
                    // gives polygonal stones that pack the plane instead of islands.
                    float edge = TexNoise.WorleyEdgeT(u, v, layer.Seed, nx, ny);
                    // F2-F1 averages ~0.25 across a unit cell, so scale the pixel
                    // value into that range or every stone shrinks to an island.
                    float gapRatio = Mathf.Clamp01(gap / s) * 0.4f;
                    if (edge < gapRatio) return gapColor;

                    // F1 drives the domed shading: centres lit, rims in shadow.
                    float n1 = TexNoise.WorleyT(u, v, layer.Seed, nx, ny);
                    float k = 0.80f + 0.34f * (1f - Mathf.Clamp01(n1 * 1.5f));

                    // Coarse per-stone tint so neighbours differ.
                    int tx = Mathf.FloorToInt(u * 0.5f);
                    int ty = Mathf.FloorToInt(v * 0.5f);
                    if (nx > 0) tx = TexNoise.WrapIndex(tx, nx);
                    if (ny > 0) ty = TexNoise.WrapIndex(ty, ny);
                    float tint = TexNoise.Hash01(tx, ty, layer.Seed + 5);
                    k *= 1f + (tint - 0.5f) * 2f * Mathf.Clamp01(layer.PatternVariation) * 0.6f;

                    return Scale(baseColor, k);
                }

                default:
                    return baseColor;
            }
        }

        /// <summary>Rotate a vector by an angle in radians.</summary>
        private static Vector2 Rotate(Vector2 v, float radians)
        {
            if (Mathf.Abs(radians) < 1e-6f) return v;
            float c = Mathf.Cos(radians);
            float s = Mathf.Sin(radians);
            return new Vector2(v.x * c - v.y * s, v.x * s + v.y * c);
        }

        /// <summary>Snap a cell size so the canvas holds a whole number of cells.</summary>
        private static float SnapCell(float cell, int canvas)
        {
            if (canvas <= 0) return Mathf.Max(cell, 1f);
            int count = Mathf.Max(1, Mathf.RoundToInt(canvas / Mathf.Max(cell, 1f)));
            return canvas / (float)count;
        }

        /// <summary>Multiply RGB, leave alpha alone.</summary>
        private static Color Scale(Color c, float k)
        {
            return new Color(Mathf.Clamp01(c.r * k), Mathf.Clamp01(c.g * k), Mathf.Clamp01(c.b * k), c.a);
        }

        /// <summary>Jitter with the cell index wrapped, so a cell split by the border keeps one tint.</summary>
        private static Color JitterWrapped(Color c, TgLayer layer, int col, int row, int cols, int rows)
        {
            if (cols > 0) col = TexNoise.WrapIndex(col, cols);
            if (rows > 0) row = TexNoise.WrapIndex(row, rows);
            return Jitter(c, layer, col, row);
        }

        /// <summary>Per-cell brightness jitter so repeated cells do not look cloned.</summary>
        private static Color Jitter(Color c, TgLayer layer, int col, int row)
        {
            float amount = Mathf.Clamp(layer.PatternVariation, 0f, 1f);
            if (amount <= 0f) return c;
            float h = TexNoise.Hash01(col, row, layer.Seed);
            return Scale(c, 1f + (h - 0.5f) * 2f * amount);
        }

        /// <summary>
        /// Normalised distance to a flat-top hexagon edge: 0 at the centre, 1 on the
        /// border. <paramref name="rx"/> / <paramref name="ry"/> let the cell be
        /// stretched per axis while the test stays correct.
        /// </summary>
        private static float HexDistanceNorm(Vector2 d, float rx, float ry)
        {
            float px = Mathf.Abs(d.x) / Mathf.Max(rx, 1e-4f);
            float py = Mathf.Abs(d.y) / Mathf.Max(ry, 1e-4f);
            float k = Mathf.Max(px * 0.8660254f + py * 0.5f, py);
            return k / 0.8660254f;
        }

        // ------------------------------------------------------------------
        // Domain warp
        // ------------------------------------------------------------------

        /// <summary>
        /// Push the fill lookup around with two noise fields. Straight stripes become
        /// wood grain, cell noise becomes marble veining.
        /// </summary>
        private static Vector2 WarpPoint(TgLayer layer, Vector2 p, TgSpec spec)
        {
            float scale = Mathf.Max(layer.WarpScale, 1f);
            float amount = layer.WarpAmount;
            if (Mathf.Abs(amount) < 0.01f) return p;

            int px = 0, py = 0;
            float u = p.x / scale;
            float v = p.y / scale;
            if (spec.Tiling)
            {
                px = Mathf.Max(1, Mathf.RoundToInt(spec.Width / scale));
                py = Mathf.Max(1, Mathf.RoundToInt(spec.Height / scale));
                u = p.x / spec.Width * px;
                v = p.y / spec.Height * py;
            }

            float w1 = WarpSample(layer, u, v, px, py, 0);
            float w2 = WarpSample(layer, u, v, px, py, 1);
            return p + new Vector2(w1 - 0.5f, w2 - 0.5f) * (2f * amount);
        }

        private static float WarpSample(TgLayer layer, float u, float v, int px, int py, int channel)
        {
            int seed = unchecked(layer.Seed + 911 + channel * 3571);
            switch (layer.WarpNoise)
            {
                case TgNoiseKind.Value: return TexNoise.ValueT(u, v, seed, px, py);
                case TgNoiseKind.Perlin: return TexNoise.PerlinT(u, v, seed, px, py);
                case TgNoiseKind.Worley: return TexNoise.WorleyT(u, v, seed, px, py);
                case TgNoiseKind.Ridged: return TexNoise.RidgedT(u, v, seed, layer.WarpOctaves, px, py);
                default: return TexNoise.FbmT(u, v, seed, layer.WarpOctaves, px, py);
            }
        }

        private static float SampleNoise(TgLayer layer, Vector2 p, float scale, TgSpec spec)
        {
            float u = p.x / scale;
            float v = p.y / scale;
            int px = 0, py = 0;

            if (spec.Tiling)
            {
                // Map the canvas onto a whole number of lattice cells, so the border
                // repeats exactly rather than merely looking similar.
                px = Mathf.Max(1, Mathf.RoundToInt(spec.Width / Mathf.Max(scale, 0.5f)));
                py = Mathf.Max(1, Mathf.RoundToInt(spec.Height / Mathf.Max(scale, 0.5f)));
                u = p.x / spec.Width * px;
                v = p.y / spec.Height * py;
            }

            switch (layer.Noise)
            {
                case TgNoiseKind.Value: return TexNoise.ValueT(u, v, layer.Seed, px, py);
                case TgNoiseKind.Perlin: return TexNoise.PerlinT(u, v, layer.Seed, px, py);
                case TgNoiseKind.Worley: return TexNoise.WorleyT(u, v, layer.Seed, px, py);
                case TgNoiseKind.Ridged: return TexNoise.RidgedT(u, v, layer.Seed, layer.Octaves, px, py);
                default: return TexNoise.FbmT(u, v, layer.Seed, layer.Octaves, px, py);
            }
        }

        private static Color GradientColor(TgLayer layer, float t)
        {
            if (layer.Gradient != null && layer.Gradient.Count > 0)
                return layer.Gradient.Eval(t);
            return Color.Lerp(layer.ColorA, layer.ColorB, Mathf.Clamp01(t));
        }

        private static Color Ramp(TgLayer layer, float t)
        {
            t = Mathf.Clamp01(t);
            if (!layer.HasColorC) return Color.Lerp(layer.ColorA, layer.ColorB, t);
            return t < 0.5f
                ? Color.Lerp(layer.ColorA, layer.ColorB, t * 2f)
                : Color.Lerp(layer.ColorB, layer.ColorC, (t - 0.5f) * 2f);
        }

        // ------------------------------------------------------------------
        // Post-processing
        // ------------------------------------------------------------------

        /// <summary>Three box passes per axis, a close Gaussian approximation.</summary>
        private static void Blur(float[] buf, int w, int h, float radius)
        {
            int r = Mathf.RoundToInt(radius);
            if (r < 1) return;
            r = Mathf.Min(r, Mathf.Max(w, h));

            var tmp = new float[buf.Length];
            for (int pass = 0; pass < 3; pass++)
            {
                BoxBlurH(buf, tmp, w, h, r);
                BoxBlurV(tmp, buf, w, h, r);
            }
        }

        private static void BoxBlurH(float[] src, float[] dst, int w, int h, int r)
        {
            float inv = 1f / (2 * r + 1);
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    float a0 = 0f, a1 = 0f, a2 = 0f, a3 = 0f;
                    for (int k = x - r; k <= x + r; k++)
                    {
                        int kk = k < 0 ? 0 : (k >= w ? w - 1 : k);
                        int o = (row + kk) * 4;
                        a0 += src[o]; a1 += src[o + 1]; a2 += src[o + 2]; a3 += src[o + 3];
                    }
                    int d = (row + x) * 4;
                    dst[d] = a0 * inv; dst[d + 1] = a1 * inv; dst[d + 2] = a2 * inv; dst[d + 3] = a3 * inv;
                }
            }
        }

        private static void BoxBlurV(float[] src, float[] dst, int w, int h, int r)
        {
            float inv = 1f / (2 * r + 1);
            for (int x = 0; x < w; x++)
            {
                for (int y = 0; y < h; y++)
                {
                    float a0 = 0f, a1 = 0f, a2 = 0f, a3 = 0f;
                    for (int k = y - r; k <= y + r; k++)
                    {
                        int kk = k < 0 ? 0 : (k >= h ? h - 1 : k);
                        int o = (kk * w + x) * 4;
                        a0 += src[o]; a1 += src[o + 1]; a2 += src[o + 2]; a3 += src[o + 3];
                    }
                    int d = (y * w + x) * 4;
                    dst[d] = a0 * inv; dst[d + 1] = a1 * inv; dst[d + 2] = a2 * inv; dst[d + 3] = a3 * inv;
                }
            }
        }

        private static float[] Downsample(float[] src, int sw, int sh, int ss)
        {
            if (ss == 1) return src;

            int w = sw / ss;
            int h = sh / ss;
            var dst = new float[w * h * 4];
            float inv = 1f / (ss * ss);

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float a0 = 0f, a1 = 0f, a2 = 0f, a3 = 0f;
                    for (int oy = 0; oy < ss; oy++)
                    {
                        int row = (y * ss + oy) * sw;
                        for (int ox = 0; ox < ss; ox++)
                        {
                            int o = (row + x * ss + ox) * 4;
                            a0 += src[o]; a1 += src[o + 1]; a2 += src[o + 2]; a3 += src[o + 3];
                        }
                    }
                    int d = (y * w + x) * 4;
                    dst[d] = a0 * inv; dst[d + 1] = a1 * inv; dst[d + 2] = a2 * inv; dst[d + 3] = a3 * inv;
                }
            }
            return dst;
        }

        /// <summary>Derive a tangent-space normal map from the rendered height field.</summary>
        private static float[] ToNormalMap(float[] rgba, int w, int h, float strength)
        {
            return NormalMapInternal(rgba, w, h, strength, true);
        }

        /// <summary>
        /// Build a tangent-space normal map from straight-alpha RGBA floats and
        /// return it as a Texture2D. Used to normalise an existing PNG.
        /// </summary>
        public static Texture2D NormalMapFromRgba(float[] rgba, int w, int h, float strength)
        {
            float[] normal = NormalMapInternal(rgba, w, h, strength, false);
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false, false);
            tex.LoadRawTextureData(ToBytes(normal, w, h));
            tex.Apply(false, false);
            tex.name = "TexGenNormal";
            return tex;
        }

        /// <summary>
        /// Shared height-to-normal conversion. Pass premultiplied=true when the
        /// input comes from the renderer's working buffer.
        /// </summary>
        private static float[] NormalMapInternal(float[] rgba, int w, int h, float strength, bool premultiplied)
        {
            var height = new float[w * h];
            for (int i = 0; i < w * h; i++)
            {
                int o = i * 4;
                float a = Mathf.Clamp01(rgba[o + 3]);
                float r, g, b;
                if (premultiplied)
                {
                    float inv = a > 1e-6f ? 1f / a : 0f;
                    r = rgba[o] * inv; g = rgba[o + 1] * inv; b = rgba[o + 2] * inv;
                }
                else
                {
                    r = rgba[o]; g = rgba[o + 1]; b = rgba[o + 2];
                }

                float lum = 0.299f * r + 0.587f * g + 0.114f * b;
                // Silhouette (alpha) carries the bulk of the relief, luminance adds detail.
                height[i] = Mathf.Clamp01(a * (0.35f + 0.65f * lum));
            }

            var outRgba = new float[w * h * 4];
            float s = Mathf.Max(strength, 0f);

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float hl = height[y * w + Mathf.Max(x - 1, 0)];
                    float hr = height[y * w + Mathf.Min(x + 1, w - 1)];
                    float hu = height[Mathf.Max(y - 1, 0) * w + x];
                    float hd = height[Mathf.Min(y + 1, h - 1) * w + x];

                    var n = new Vector3((hl - hr) * s, (hu - hd) * s, 1f).normalized;

                    int o = (y * w + x) * 4;
                    // Straight alpha, opacity 1.
                    outRgba[o] = n.x * 0.5f + 0.5f;
                    outRgba[o + 1] = n.y * 0.5f + 0.5f;
                    outRgba[o + 2] = n.z * 0.5f + 0.5f;
                    outRgba[o + 3] = 1f;
                }
            }
            return outRgba;
        }

        /// <summary>
        /// Convert premultiplied RGBA floats to straight-alpha 8-bit BGRA-order
        /// bytes. Unity's RGBA32 memory layout is R,G,B,A so a straight copy is
        /// correct once the alpha has been unpremultiplied.
        /// </summary>
        private static byte[] ToBytes(float[] rgba, int w, int h)
        {
            var bytes = new byte[w * h * 4];
            for (int i = 0; i < w * h; i++)
            {
                int o = i * 4;
                float a = Mathf.Clamp01(rgba[o + 3]);
                float inv = a > 1e-6f ? 1f / a : 0f;

                bytes[o] = ToByte(rgba[o] * inv);
                bytes[o + 1] = ToByte(rgba[o + 1] * inv);
                bytes[o + 2] = ToByte(rgba[o + 2] * inv);
                bytes[o + 3] = ToByte(a);
            }
            return bytes;
        }

        private static byte ToByte(float v)
        {
            return (byte)Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01(v) * 255f), 0, 255);
        }
    }
}
