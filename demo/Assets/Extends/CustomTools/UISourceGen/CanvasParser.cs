using System;
using System.Collections.Generic;
using System.Globalization;
using UniMcp;

namespace UniMcp.UiGen
{
    /// <summary>
    /// Parses a free-form 2D canvas document into <see cref="CanvasDoc"/>.
    /// Total and forgiving: unknown shapes/values are reported as warnings, never
    /// thrown, so a partially wrong spec still produces a useful image.
    /// </summary>
    public static class CanvasParser
    {
        public static CanvasParseResult Parse(JsonNode root)
        {
            var result = new CanvasParseResult();
            var doc = new CanvasDoc();
            result.Doc = doc;

            JsonClass obj = root as JsonClass;
            if (obj == null)
            {
                result.Errors.Add("Canvas description must be a JSON object: {width,height,elements:[...]}.");
                return result;
            }

            doc.Name = UiParser.GetString(obj, "name", "Canvas");
            doc.Width = Clamp(UiParser.GetInt(obj, "width", doc.Width), 1, 16384);
            doc.Height = Clamp(UiParser.GetInt(obj, "height", doc.Height), 1, 16384);
            doc.Supersample = Clamp(UiParser.GetFloat(obj, "supersample", doc.Supersample), 1f, 4f);
            doc.ClearFirst = UiParser.GetBool(obj, "clear", doc.ClearFirst);
            doc.Note = UiParser.GetString(obj, "note", null);

            // background accepts either a paint object or a bare colour string
            JsonNode bg = UiParser.Get(obj, "background");
            if (bg is JsonClass bgObj) doc.Background = ReadPaint(bgObj, result, "background");
            else if (bg != null && !string.IsNullOrEmpty(bg.Value))
            {
                if (UiColor.TryParse(bg.Value, out UiColor c))
                    doc.Background = new CanvasPaint { FillKind = CanvasFillKind.Solid, Color = c };
                else result.Warnings.Add("background colour not understood: " + bg.Value);
            }

            JsonNode els = UiParser.Get(obj, "elements") ?? UiParser.Get(obj, "shapes");
            if (els is JsonArray arr)
            {
                for (int i = 0; i < arr.Count; i++)
                {
                    CanvasElement e = ReadElement(arr[i] as JsonClass, result, "elements[" + i + "]");
                    if (e != null) doc.Elements.Add(e);
                }
            }
            else if (els != null)
            {
                result.Errors.Add("elements must be an array.");
            }

            return result;
        }

        // ------------------------------------------------------------- elements

        private static CanvasElement ReadElement(JsonClass node, CanvasParseResult r, string where)
        {
            if (node == null) { r.Errors.Add(where + " is not an object."); return null; }

            var e = new CanvasElement();
            e.Id = UiParser.GetString(node, "id", null);
            string rawShape = UiParser.GetString(node, "shape", null) ?? UiParser.GetString(node, "type", null) ?? "rect";
            if (!TryShape(rawShape, out e.Shape))
            {
                r.Warnings.Add(where + ": unknown shape \"" + rawShape + "\", drawn as rect.");
                e.Shape = CanvasShape.Rect;
            }

            ReadGeometry(node, e);

            e.Fill = ReadOptionalPaint(node, "fill", r, where);
            e.Stroke = ReadOptionalPaint(node, "stroke", r, where);

            // An author may fold the outline into the paint object
            // ("fill":{"stroke":true,"stroke_width":2,"stroke_color":"#0ff"}).
            // Split that out so shapes draw a real fill + outline pair instead of
            // silently rendering as a fat single-colour stroke.
            if (e.Fill != null && e.Fill.Stroke && e.Stroke == null)
            {
                e.Stroke = new CanvasPaint
                {
                    FillKind = CanvasFillKind.None,
                    Stroke = true,
                    StrokeWidth = e.Fill.StrokeWidth > 0f ? e.Fill.StrokeWidth : 1f,
                    Color = e.Fill.StrokeColor ?? e.Fill.Color,
                    Cap = e.Fill.Cap,
                    Join = e.Fill.Join,
                    Miter = e.Fill.Miter,
                    Blend = e.Fill.Blend,
                    AntiAlias = e.Fill.AntiAlias,
                    PathEffect = e.Fill.PathEffect,
                    Dashes = e.Fill.Dashes,
                    DashPhase = e.Fill.DashPhase
                };
                // the paint now describes the outline only
                e.Fill.Stroke = false;
                e.Fill.StrokeWidth = 0f;
            }
            // shorthand: "stroke": 2  means a 2px stroke in the current colour
            JsonNode strokeNode = UiParser.Get(node, "stroke");
            if (strokeNode != null && !(strokeNode is JsonClass))
            {
                float w = strokeNode.AsFloatDefault(1f);
                e.Stroke = CanvasPaint.StrokeOnly(w <= 0 ? 1f : w,
                    UiParser.GetColor(node, "stroke_color", e.Fill?.Color ?? UiColor.White));
            }
            else if (strokeNode is JsonClass)
            {
                // A stroke object may leave "color" out and rely on the sibling
                // "stroke_color", which is the spelling that also works for the
                // numeric shorthand above.
                if (!UiParser.HasKey((JsonClass)strokeNode, "color") && UiParser.HasKey(node, "stroke_color"))
                    e.Stroke.Color = UiParser.GetColor(node, "stroke_color", e.Stroke.Color);
                if (!e.Stroke.Stroke)
                {
                    e.Stroke.Stroke = true;
                    if (e.Stroke.StrokeWidth <= 0f) e.Stroke.StrokeWidth = 1f;
                }
            }

            e.Text = UiParser.GetString(node, "text", null);
            e.FontPath = UiParser.GetString(node, "font_path", null);
            e.FontFamily = UiParser.GetString(node, "font_family", null);
            e.FontIndex = UiParser.GetInt(node, "font_index", 0);
            e.FontSize = UiParser.GetFloat(node, "font_size", 24f);
            e.Bold = UiParser.GetBool(node, "bold", false);
            e.Italic = UiParser.GetBool(node, "italic", false);
            e.TextAlign = UiParser.GetEnum(node, "text_align", UiTextAlign.Left);
            e.LetterSpacing = UiParser.GetFloat(node, "letter_spacing", 0f);
            e.TextScaleX = UiParser.GetFloat(node, "text_scale_x", 1f);
            e.TextSkewX = UiParser.GetFloat(node, "text_skew_x", 0f);

            e.ImagePath = UiParser.GetString(node, "image", null) ?? UiParser.GetString(node, "image_path", null);
            e.Icon = UiParser.GetString(node, "icon", null);
            e.NearestNeighbour = UiParser.GetBool(node, "nearest", false);
            JsonNode nine = UiParser.Get(node, "nine_patch");
            if (nine is JsonArray na && na.Count >= 4)
            {
                e.NinePatch = new[] { na[0].AsIntDefault(0), na[1].AsIntDefault(0), na[2].AsIntDefault(0), na[3].AsIntDefault(0) };
            }

            ReadPoints(node, e, r, where);
            ReadVertices(node, e, r, where);
            ReadTransform(node, e);
            ReadClip(node, e);
            ReadLayer(node, e);

            e.Opacity = Clamp(UiParser.GetFloat(node, "opacity", 1f), 0f, 1f);

            JsonNode kids = UiParser.Get(node, "children");
            if (kids is JsonArray ka)
            {
                e.Children = new List<CanvasElement>();
                for (int i = 0; i < ka.Count; i++)
                {
                    CanvasElement k = ReadElement(ka[i] as JsonClass, r, where + ".children[" + i + "]");
                    if (k != null) e.Children.Add(k);
                }
            }

            // a path may declare a second path to boolean-combine with
            JsonNode with = UiParser.Get(node, "combine_with");
            if (with is JsonClass wc) e.CombineWith = ReadElement(wc, r, where + ".combine_with");

            return e;
        }

        private static void ReadGeometry(JsonClass node, CanvasElement e)
        {
            // "rect":[x,y,w,h] or explicit x/y/w/h
            JsonNode rect = UiParser.Get(node, "rect");
            if (rect is JsonArray ra && ra.Count >= 4)
            {
                e.X = ra[0].AsFloatDefault(0f);
                e.Y = ra[1].AsFloatDefault(0f);
                e.W = ra[2].AsFloatDefault(0f);
                e.H = ra[3].AsFloatDefault(0f);
            }
            e.X = UiParser.GetFloat(node, "x", e.X);
            e.Y = UiParser.GetFloat(node, "y", e.Y);
            e.W = UiParser.GetFloat(node, "w", e.W);
            e.H = UiParser.GetFloat(node, "h", e.H);

            // "size":[w,h]
            JsonNode size = UiParser.Get(node, "size");
            if (size is JsonArray sa && sa.Count >= 2)
            {
                e.W = sa[0].AsFloatDefault(e.W);
                e.H = sa[1].AsFloatDefault(e.H);
            }

            // circle / oval take a radius
            e.Radius = UiParser.GetFloat(node, "radius", e.Radius);
            if (e.Shape == CanvasShape.Circle && e.Radius <= 0f)
            {
                float r0 = UiParser.GetFloat(node, "r", 0f);
                if (r0 > 0f) e.Radius = r0;
                else if (e.W > 0f && e.H > 0f) e.Radius = Math.Min(e.W, e.H) * 0.5f;
                e.W = e.H = e.Radius * 2f;
            }

            // line / arc endpoints
            e.X1 = UiParser.GetFloat(node, "x1", e.X1);
            e.Y1 = UiParser.GetFloat(node, "y1", e.Y1);
            JsonNode to = UiParser.Get(node, "to");
            if (to is JsonArray ta && ta.Count >= 2)
            {
                e.X1 = ta[0].AsFloatDefault(e.X1);
                e.Y1 = ta[1].AsFloatDefault(e.Y1);
            }

            e.StartAngle = UiParser.GetFloat(node, "start_angle", e.StartAngle);
            e.Sweep = UiParser.GetFloat(node, "sweep", e.Sweep);
            e.ClosePath = UiParser.GetBool(node, "close", e.ClosePath);
            e.EvenOdd = UiParser.GetBool(node, "even_odd", e.EvenOdd);
            e.PathData = UiParser.GetString(node, "path", e.PathData);
            e.CombineOp = UiParser.GetEnum(node, "path_op", e.CombineOp);
        }

        private static void ReadPoints(JsonClass node, CanvasElement e, CanvasParseResult r, string where)
        {
            JsonNode pts = UiParser.Get(node, "points");
            if (pts == null) return;
            var list = new List<CanvasPoint>();
            if (pts is JsonArray pa)
            {
                for (int i = 0; i < pa.Count; i++)
                {
                    JsonNode item = pa[i];
                    if (item is JsonArray pair && pair.Count >= 2)
                        list.Add(new CanvasPoint(pair[0].AsFloatDefault(0f), pair[1].AsFloatDefault(0f)));
                    else if (item is JsonClass po)
                        list.Add(new CanvasPoint(UiParser.GetFloat(po, "x", 0f), UiParser.GetFloat(po, "y", 0f)));
                }
            }
            if (list.Count == 0) r.Warnings.Add(where + ": points array had no usable pairs.");
            e.Points = list;
        }

        private static void ReadVertices(JsonClass node, CanvasElement e, CanvasParseResult r, string where)
        {
            JsonNode vn = UiParser.Get(node, "vertices");
            if (vn is JsonArray va)
            {
                e.Vertices = new List<CanvasPoint>();
                for (int i = 0; i < va.Count; i++)
                {
                    JsonNode item = va[i];
                    if (item is JsonArray pair && pair.Count >= 2)
                        e.Vertices.Add(new CanvasPoint(pair[0].AsFloatDefault(0f), pair[1].AsFloatDefault(0f)));
                }
            }

            JsonNode cn = UiParser.Get(node, "colors");
            if (cn is JsonArray ca)
            {
                e.VertexColors = new List<UiColor>();
                for (int i = 0; i < ca.Count; i++)
                {
                    JsonNode item = ca[i];
                    if (item is JsonArray rgba && rgba.Count >= 3)
                    {
                        var f = new List<float>();
                        for (int j = 0; j < rgba.Count; j++) f.Add(rgba[j].AsFloatDefault(0f));
                        e.VertexColors.Add(UiColor.FromFloats(f));
                    }
                    else if (UiColor.TryParse(item?.Value, out UiColor c)) e.VertexColors.Add(c);
                }
            }

            JsonNode it = UiParser.Get(node, "indices");
            if (it is JsonArray ia)
            {
                e.Indices = new List<int>();
                for (int i = 0; i < ia.Count; i++) e.Indices.Add(ia[i].AsIntDefault(0));
            }

            if (e.Vertices != null && e.Vertices.Count > 0 && e.Vertices.Count % 3 != 0 && e.Indices == null)
                r.Warnings.Add(where + ": vertex count is not a multiple of 3 and no indices were given.");
        }

        private static void ReadTransform(JsonClass node, CanvasElement e)
        {
            JsonNode t = UiParser.Get(node, "translate");
            if (t is JsonArray ta && ta.Count >= 2)
            {
                e.Tx = ta[0].AsFloatDefault(0f);
                e.Ty = ta[1].AsFloatDefault(0f);
            }
            e.Tx = UiParser.GetFloat(node, "tx", e.Tx);
            e.Ty = UiParser.GetFloat(node, "ty", e.Ty);

            e.RotateDeg = UiParser.GetFloat(node, "rotate", UiParser.GetFloat(node, "rotation", e.RotateDeg));

            JsonNode s = UiParser.Get(node, "scale");
            if (s is JsonArray sa && sa.Count >= 1)
            {
                e.ScaleX = sa[0].AsFloatDefault(1f);
                e.ScaleY = sa.Count >= 2 ? sa[1].AsFloatDefault(e.ScaleX) : e.ScaleX;
            }
            else if (s != null)
            {
                float sv = s.AsFloatDefault(1f);
                e.ScaleX = e.ScaleY = sv;
            }
            e.ScaleX = UiParser.GetFloat(node, "scale_x", e.ScaleX);
            e.ScaleY = UiParser.GetFloat(node, "scale_y", e.ScaleY);

            e.SkewX = UiParser.GetFloat(node, "skew_x", e.SkewX);
            e.SkewY = UiParser.GetFloat(node, "skew_y", e.SkewY);

            JsonNode m = UiParser.Get(node, "matrix");
            if (m is JsonArray ma && (ma.Count == 6 || ma.Count == 9))
            {
                var vals = new float[ma.Count];
                for (int i = 0; i < ma.Count; i++) vals[i] = ma[i].AsFloatDefault(0f);
                if (ma.Count == 6)
                {
                    // 6 values = 2x3 affine; expand to Skia's 9-value layout
                    e.Matrix = new[] { vals[0], vals[2], vals[4], vals[1], vals[3], vals[5], 0f, 0f, 1f };
                }
                else e.Matrix = vals;
            }

            e.PivotX = UiParser.GetFloat(node, "pivot_x", e.PivotX);
            e.PivotY = UiParser.GetFloat(node, "pivot_y", e.PivotY);
        }

        private static void ReadClip(JsonClass node, CanvasElement e)
        {
            JsonNode c = UiParser.Get(node, "clip");
            if (c == null) return;
            if (c is JsonClass co)
            {
                e.Clip = true;
                if (!TryShape(UiParser.GetString(co, "shape", "rect") ?? "rect", out e.ClipShape)) e.ClipShape = CanvasShape.Rect;
                e.ClipRadius = UiParser.GetFloat(co, "radius", 0f);
                e.ClipEvenOdd = UiParser.GetBool(co, "even_odd", false);

                // Clip geometry lives in its own fields; when omitted it falls back
                // to the element box at render time.
                bool hasGeom = false;
                JsonNode cr = UiParser.Get(co, "rect");
                if (cr is JsonArray ca && ca.Count >= 4)
                {
                    e.ClipX = ca[0].AsFloatDefault(0f);
                    e.ClipY = ca[1].AsFloatDefault(0f);
                    e.ClipW = ca[2].AsFloatDefault(0f);
                    e.ClipH = ca[3].AsFloatDefault(0f);
                    hasGeom = true;
                }
                if (UiParser.HasKey(co, "x")) { e.ClipX = UiParser.GetFloat(co, "x", e.ClipX); hasGeom = true; }
                if (UiParser.HasKey(co, "y")) { e.ClipY = UiParser.GetFloat(co, "y", e.ClipY); hasGeom = true; }
                if (UiParser.HasKey(co, "w")) { e.ClipW = UiParser.GetFloat(co, "w", e.ClipW); hasGeom = true; }
                if (UiParser.HasKey(co, "h")) { e.ClipH = UiParser.GetFloat(co, "h", e.ClipH); hasGeom = true; }
                e.ClipHasRect = hasGeom;

                // an explicit clip path reuses the element's path data when present
                string cp = UiParser.GetString(co, "path", null);
                if (!string.IsNullOrEmpty(cp)) { e.PathData = cp; e.ClipShape = CanvasShape.Path; }
            }
            else e.Clip = c.AsBoolDefault(false);
        }

        private static void ReadLayer(JsonClass node, CanvasElement e)
        {
            JsonNode l = UiParser.Get(node, "layer");
            if (l is JsonClass lo)
            {
                e.Layer = true;
                e.LayerOpacity = Clamp(UiParser.GetFloat(lo, "opacity", 1f), 0f, 1f);
                e.LayerBlend = UiParser.GetEnum(lo, "blend", CanvasBlend.SrcOver);
            }
            else if (l != null) e.Layer = l.AsBoolDefault(false);
        }

        // ---------------------------------------------------------------- paints

        private static CanvasPaint ReadOptionalPaint(JsonClass node, string key, CanvasParseResult r, string where)
        {
            JsonNode v = UiParser.Get(node, key);
            if (v == null) return null;
            if (v is JsonClass o) return ReadPaint(o, r, where + "." + key);
            // a bare string is a solid colour
            if (UiColor.TryParse(v.Value, out UiColor c))
                return new CanvasPaint { FillKind = CanvasFillKind.Solid, Color = c };
            r.Warnings.Add(where + ": could not read " + key + ".");
            return null;
        }

        /// <summary>Reads a paint object. Also accepts a plain colour by caller.</summary>
        private static CanvasPaint ReadPaint(JsonClass o, CanvasParseResult r, string where)
        {
            var p = new CanvasPaint();
            string kind = UiParser.GetString(o, "paint", null) ?? UiParser.GetString(o, "kind", null);
            // "type" on a paint only applies when it names a paint kind
            string byType = UiParser.GetString(o, "type", null);
            if (string.IsNullOrEmpty(kind) && !string.IsNullOrEmpty(byType) && IsPaintKind(byType)) kind = byType;

            if (!string.IsNullOrEmpty(kind))
            {
                string norm = kind.Replace("-", "").Replace("_", "");
                // "conical" is the natural spelling for the two-point conical gradient
                if (string.Equals(norm, "conical", StringComparison.OrdinalIgnoreCase)) norm = "twopointconical";
                if (!Enum.TryParse(norm, true, out p.FillKind))
                {
                    r.Warnings.Add(where + ": unknown paint kind \"" + kind + "\", using solid.");
                    p.FillKind = CanvasFillKind.Solid;
                }
            }

            p.Color = UiParser.GetColor(o, "color", p.Color);
            if (UiParser.HasKey(o, "color2")) p.Color2 = UiParser.GetColor(o, "color2", p.Color);
            if (UiParser.HasKey(o, "color_end")) p.Color2 = UiParser.GetColor(o, "color_end", p.Color);

            JsonNode stops = UiParser.Get(o, "stops");
            if (stops is JsonArray sa)
            {
                p.Stops = new List<CanvasStop>();
                for (int i = 0; i < sa.Count; i++)
                {
                    JsonNode item = sa[i];
                    if (item is JsonClass so)
                    {
                        p.Stops.Add(new CanvasStop
                        {
                            Offset = Clamp(UiParser.GetFloat(so, "offset", 0f), 0f, 1f),
                            Color = UiParser.GetColor(so, "color", UiColor.White)
                        });
                    }
                    else if (item is JsonArray pair && pair.Count >= 2)
                    {
                        p.Stops.Add(new CanvasStop
                        {
                            Offset = Clamp(pair[0].AsFloatDefault(0f), 0f, 1f),
                            Color = UiColor.TryParse(pair[1].Value, out UiColor sc) ? sc : UiColor.White
                        });
                    }
                }
            }

            JsonNode from = UiParser.Get(o, "from");
            if (from is JsonArray fa && fa.Count >= 2)
            {
                p.X0 = fa[0].AsFloatDefault(0f);
                p.Y0 = fa[1].AsFloatDefault(0f);
            }
            JsonNode to = UiParser.Get(o, "to");
            if (to is JsonArray ta && ta.Count >= 2)
            {
                p.X1 = ta[0].AsFloatDefault(0f);
                p.Y1 = ta[1].AsFloatDefault(0f);
            }
            p.X0 = UiParser.GetFloat(o, "x0", p.X0);
            p.Y0 = UiParser.GetFloat(o, "y0", p.Y0);
            p.X1 = UiParser.GetFloat(o, "x1", p.X1);
            p.Y1 = UiParser.GetFloat(o, "y1", p.Y1);
            p.Radius0 = UiParser.GetFloat(o, "radius0", p.Radius0);
            p.Radius1 = UiParser.GetFloat(o, "radius", UiParser.GetFloat(o, "radius1", p.Radius1));
            p.StartAngle = UiParser.GetFloat(o, "start_angle", p.StartAngle);
            p.EndAngle = UiParser.GetFloat(o, "end_angle", p.EndAngle);

            p.NoiseBaseFreq = UiParser.GetFloat(o, "base_freq", p.NoiseBaseFreq);
            p.NoiseNumOctaves = UiParser.GetFloat(o, "octaves", p.NoiseNumOctaves);
            p.NoiseSeed = UiParser.GetFloat(o, "seed", p.NoiseSeed);
            p.NoiseStitch = UiParser.GetFloat(o, "stitch", p.NoiseStitch);
            p.NoiseTurbulence = UiParser.GetBool(o, "turbulence", p.NoiseTurbulence);

            p.Stroke = UiParser.GetBool(o, "stroke", p.Stroke);
            p.StrokeWidth = UiParser.GetFloat(o, "stroke_width", p.StrokeWidth);
            if (UiParser.HasKey(o, "stroke_color")) p.StrokeColor = UiParser.GetColor(o, "stroke_color", p.Color);
            p.Cap = UiParser.GetEnum(o, "cap", p.Cap);
            p.Join = UiParser.GetEnum(o, "join", p.Join);
            p.Miter = UiParser.GetFloat(o, "miter", p.Miter);

            p.Blend = UiParser.GetEnum(o, "blend", p.Blend);
            p.AntiAlias = UiParser.GetBool(o, "antialias", p.AntiAlias);
            p.Dither = UiParser.GetBool(o, "dither", p.Dither);

            p.BlurRadius = UiParser.GetFloat(o, "blur", p.BlurRadius);
            p.BlurStyle = UiParser.GetEnum(o, "blur_style", p.BlurStyle);

            p.Filter = UiParser.GetEnum(o, "filter", p.Filter);
            p.FilterSigmaX = UiParser.GetFloat(o, "sigma_x", p.FilterSigmaX);
            p.FilterSigmaY = UiParser.GetFloat(o, "sigma_y", p.FilterSigmaY);
            p.FilterDx = UiParser.GetFloat(o, "dx", p.FilterDx);
            p.FilterDy = UiParser.GetFloat(o, "dy", p.FilterDy);
            p.FilterRadius = UiParser.GetFloat(o, "filter_radius", p.FilterRadius);
            p.FilterColor = UiParser.GetColor(o, "shadow_color", p.FilterColor);

            p.PathEffect = UiParser.GetEnum(o, "path_effect", p.PathEffect);
            JsonNode dash = UiParser.Get(o, "dash");
            if (dash is JsonArray da)
            {
                p.Dashes = new float[da.Count];
                for (int i = 0; i < da.Count; i++) p.Dashes[i] = da[i].AsFloatDefault(0f);
                if (p.PathEffect == CanvasPathEffect.None) p.PathEffect = CanvasPathEffect.Dash;
            }
            p.DashPhase = UiParser.GetFloat(o, "dash_phase", p.DashPhase);
            p.TrimStart = UiParser.GetFloat(o, "trim_start", p.TrimStart);
            p.TrimEnd = UiParser.GetFloat(o, "trim_end", p.TrimEnd);
            p.CornerRadius = UiParser.GetFloat(o, "corner", p.CornerRadius);
            p.DiscreteLength = UiParser.GetFloat(o, "discrete_length", p.DiscreteLength);
            p.DiscreteDeviation = UiParser.GetFloat(o, "discrete_deviation", p.DiscreteDeviation);

            p.ImagePath = UiParser.GetString(o, "image", null);
            p.TileX = UiParser.GetEnum(o, "tile_x", p.TileX);
            p.TileY = UiParser.GetEnum(o, "tile_y", p.TileY);

            JsonNode sm = UiParser.Get(o, "shader_matrix");
            if (sm is JsonArray sma && sma.Count >= 6)
            {
                p.ShaderMatrix = new float[sma.Count];
                for (int i = 0; i < sma.Count; i++) p.ShaderMatrix[i] = sma[i].AsFloatDefault(0f);
            }

            string space = UiParser.GetString(o, "space", null);
            if (!string.IsNullOrEmpty(space))
                p.ShaderSpaceCanvas = string.Equals(space, "canvas", StringComparison.OrdinalIgnoreCase)
                                   || string.Equals(space, "absolute", StringComparison.OrdinalIgnoreCase);

            if (p.FillKind != CanvasFillKind.None && p.FillKind != CanvasFillKind.Solid && p.Color2 == null && p.Stops == null)
                r.Warnings.Add(where + ": gradient/shaders have no second colour; will fade to transparent.");

            return p;
        }

        private static bool IsPaintKind(string s)
        {
            switch ((s ?? "").ToLowerInvariant())
            {
                case "solid": case "linear": case "radial": case "sweep":
                case "conical": case "twopointconical": case "noise": case "image": case "none":
                    return true;
                default: return false;
            }
        }

        private static bool TryShape(string raw, out CanvasShape shape)
        {
            string s = (raw ?? "").Replace("-", "").Replace("_", "").Replace(" ", "").ToLowerInvariant();
            switch (s)
            {
                case "rect": case "rectangle": case "box": shape = CanvasShape.Rect; return true;
                case "roundrect": case "roundedrect": shape = CanvasShape.RoundRect; return true;
                case "circle": case "disc": case "dot": shape = CanvasShape.Circle; return true;
                case "oval": case "ellipse": shape = CanvasShape.Oval; return true;
                case "line": case "segment": shape = CanvasShape.Line; return true;
                case "arc": case "pie": shape = CanvasShape.Arc; return true;
                case "path": case "svg": shape = CanvasShape.Path; return true;
                case "polygon": case "triangle": shape = CanvasShape.Polygon; return true;
                case "polyline": shape = CanvasShape.Polyline; return true;
                case "points": case "scatter": case "dots": shape = CanvasShape.Points; return true;
                case "text": case "label": shape = CanvasShape.Text; return true;
                case "textonpath": case "textpath": case "textalongpath": shape = CanvasShape.TextOnPath; return true;
                case "icon": case "glyph": shape = CanvasShape.Icon; return true;
                case "image": case "bitmap": shape = CanvasShape.Image; return true;
                case "imageninepatch": case "ninepatch": shape = CanvasShape.ImageNinePatch; return true;
                case "vertices": case "mesh": case "gradientmesh": shape = CanvasShape.Vertices; return true;
                case "group": case "layer": shape = CanvasShape.Group; return true;
                default: shape = CanvasShape.Rect; return false;
            }
        }

        private static float Clamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);
        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);
    }
}
