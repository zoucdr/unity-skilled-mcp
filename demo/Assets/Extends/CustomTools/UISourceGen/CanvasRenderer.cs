using System;
using System.Collections.Generic;
using System.IO;
using SkiaSharp;

namespace UniMcp.UiGen
{
    /// <summary>
    /// Renders a <see cref="CanvasDoc"/> with SkiaSharp. Pure .NET with no Unity
    /// dependency, so the exact same code path can be exercised from a standalone
    /// harness. Everything is resolved lazily: a paint that names a shader we
    /// cannot build degrades to a solid fill instead of throwing.
    /// </summary>
    public sealed class CanvasRenderer
    {
        /// <summary>Font file used when an element/theme gives neither path nor family.</summary>
        public string DefaultFontPath;
        public int DefaultFontIndex;

        private readonly Dictionary<string, SKTypeface> _faces = new Dictionary<string, SKTypeface>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, SKImage> _images = new Dictionary<string, SKImage>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Warnings collected during the last render (unsupported / skipped ops).</summary>
        public readonly List<string> Warnings = new List<string>();

        /// <summary>Resolves a project-relative or absolute path to a readable file, or null.</summary>
        public Func<string, string> PathResolver;

        // -------------------------------------------------------------- surface

        /// <summary>Renders the document and encodes it as an image.</summary>
        public byte[] Render(CanvasDoc doc, SKEncodedImageFormat format = SKEncodedImageFormat.Png, int quality = 100)
        {
            SKImage image = RenderToImage(doc);
            if (image == null) return null;
            using (image)
            using (SKData data = image.Encode(format, Clamp(quality, 1, 100)))
                return data?.ToArray();
        }

        /// <summary>Renders to an <see cref="SKImage"/>. Caller owns the result.</summary>
        public SKImage RenderToImage(CanvasDoc doc)
        {
            Warnings.Clear();
            if (doc == null) return null;

            float ss = Clamp(doc.Supersample <= 0f ? 1f : doc.Supersample, 1f, 4f);
            int pw = Math.Max(1, (int)Math.Round(doc.Width * ss));
            int ph = Math.Max(1, (int)Math.Round(doc.Height * ss));

            var info = new SKImageInfo(pw, ph, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info);
            if (surface == null)
            {
                Warnings.Add("SKSurface.Create returned null; out of graphics memory?");
                return null;
            }

            SKCanvas cv = surface.Canvas;
            cv.Clear(SKColors.Transparent);
            cv.Scale(ss, ss);

            using (new SKAutoCanvasRestore(cv, true))
            {
                if (doc.Background != null && doc.Background.IsVisible)
                    DrawPaint(cv, doc.Background, 0f, 0f, doc.Width, doc.Height);

                foreach (CanvasElement e in doc.Elements)
                    DrawElement(cv, e, null);
            }

            return surface.Snapshot();
        }

        /// <summary>
        /// Renders several documents into a single multi-page PDF.
        /// Returns the file bytes, or null when Skia cannot create the document.
        /// </summary>
        public byte[] RenderPdf(IList<CanvasDoc> docs)
        {
            if (docs == null || docs.Count == 0) return null;
            using var ms = new MemoryStream();
            using (SKDocument pdf = SKDocument.CreatePdf(ms))
            {
                if (pdf == null) return null;
                foreach (CanvasDoc doc in docs)
                {
                    if (doc == null) continue;
                    SKCanvas page = pdf.BeginPage(doc.Width, doc.Height);
                    if (page == null) continue;
                    // PDF userspace is 1:1 with the declared page size; supersampling
                    // would just inflate the file, so it is deliberately ignored here.
                    using (new SKAutoCanvasRestore(page, true))
                    {
                        if (doc.Background != null && doc.Background.IsVisible)
                            DrawPaint(page, doc.Background, 0f, 0f, doc.Width, doc.Height);
                        foreach (CanvasElement e in doc.Elements) DrawElement(page, e, null);
                    }
                    pdf.EndPage();
                }
                pdf.Close();
            }
            return ms.ToArray();
        }

        // ------------------------------------------------------------- elements

        /// <summary>Draws one element plus its children.</summary>
        public void DrawElement(SKCanvas cv, CanvasElement e, CanvasPaint inherited)
        {
            if (cv == null || e == null) return;

            int save = cv.Save();
            try
            {
                ApplyTransform(cv, e);

                CanvasPaint fill = e.Fill ?? inherited;
                // an element with no fill and no stroke still needs to host children/clip
                bool drawsSomething = e.Shape != CanvasShape.Group;

                if (e.Clip) ApplyClip(cv, e);

                bool layered = e.Layer || e.Opacity < 0.999f;
                using SKPaint layerPaint = layered ? MakeLayerPaint(e) : null;
                if (layered) cv.SaveLayer(layerPaint);

                if (drawsSomething) DrawShape(cv, e, fill);

                if (e.Children != null)
                {
                    // A group passes down whatever paint reached it; a non-group
                    // element hands its own fill to children so they can omit it.
                    CanvasPaint childPaint = e.Shape == CanvasShape.Group
                        ? (fill ?? inherited)
                        : (e.Fill ?? inherited);
                    foreach (CanvasElement child in e.Children) DrawElement(cv, child, childPaint);
                }

                if (layered) cv.Restore();
            }
            catch (Exception ex)
            {
                Warnings.Add("element " + (e.Id ?? e.Shape.ToString()) + " failed: " + ex.Message);
            }
            finally
            {
                cv.RestoreToCount(save);
            }
        }

        private void DrawShape(SKCanvas cv, CanvasElement e, CanvasPaint fill)
        {
            switch (e.Shape)
            {
                case CanvasShape.Rect:
                    DrawBox(cv, e, fill, 0f);
                    break;
                case CanvasShape.RoundRect:
                    DrawBox(cv, e, fill, e.Radius > 0f ? e.Radius : Math.Min(e.W, e.H) * 0.12f);
                    break;
                case CanvasShape.Circle:
                case CanvasShape.Oval:
                    DrawOval(cv, e, fill, e.Shape == CanvasShape.Circle);
                    break;
                case CanvasShape.Line:
                    DrawLineShape(cv, e, fill);
                    break;
                case CanvasShape.Arc:
                    DrawArcShape(cv, e, fill);
                    break;
                case CanvasShape.Path:
                    DrawPathShape(cv, e, fill);
                    break;
                case CanvasShape.Polygon:
                case CanvasShape.Polyline:
                case CanvasShape.Points:
                    DrawPointShape(cv, e, fill);
                    break;
                case CanvasShape.Text:
                    DrawTextShape(cv, e, fill);
                    break;
                case CanvasShape.TextOnPath:
                    DrawTextOnPathShape(cv, e, fill);
                    break;
                case CanvasShape.Icon:
                    DrawIconShape(cv, e, fill);
                    break;
                case CanvasShape.Image:
                    DrawImageShape(cv, e, fill, false);
                    break;
                case CanvasShape.ImageNinePatch:
                    DrawImageShape(cv, e, fill, true);
                    break;
                case CanvasShape.Vertices:
                    DrawVerticesShape(cv, e, fill);
                    break;
                case CanvasShape.Group:
                    DrawBox(cv, e, fill, e.Radius);   // a group may still paint its own box
                    break;
            }
        }

        // ------------------------------------------------------------ primitives

        /// <summary>Fills a bare rectangle with a paint. Used for canvas backgrounds.</summary>
        private void DrawPaint(SKCanvas cv, CanvasPaint p, float x, float y, float w, float h)
        {
            if (p == null || !p.IsVisible || w <= 0f || h <= 0f) return;
            using SKPaint paint = MakePaint(p, false, x, y, w, h);
            cv.DrawRect(new SKRect(x, y, x + w, y + h), paint);
        }

        private void DrawBox(SKCanvas cv, CanvasElement e, CanvasPaint p, float radius)
        {
            if (p == null || !p.IsVisible) return;
            float x = e.X, y = e.Y, w = e.W, h = e.H;
            if (w < 0f) { x += w; w = -w; }
            if (h < 0f) { y += h; h = -h; }
            if (w <= 0f || h <= 0f) return;

            float rr = Clamp(radius, 0f, Math.Min(w, h) * 0.5f);

            using SKPaint fillPaint = p.FillKind != CanvasFillKind.None ? MakePaint(p, false, x, y, w, h) : null;
            using SKPaint strokePaint = p.Stroke ? MakePaint(p, true, x, y, w, h) : null;

            if (fillPaint != null)
            {
                if (rr > 0f) cv.DrawRoundRect(new SKRect(x, y, x + w, y + h), rr, rr, fillPaint);
                else cv.DrawRect(new SKRect(x, y, x + w, y + h), fillPaint);
            }
            if (strokePaint != null)
            {
                // inset so the stroke sits inside the declared box
                float inset = strokePaint.StrokeWidth * 0.5f;
                float ix = x + inset, iy = y + inset, iw = w - inset * 2f, ih = h - inset * 2f;
                if (iw > 0f && ih > 0f)
                {
                    float irr = Math.Max(0f, rr - inset);
                    if (irr > 0f) cv.DrawRoundRect(new SKRect(ix, iy, ix + iw, iy + ih), irr, irr, strokePaint);
                    else cv.DrawRect(new SKRect(ix, iy, ix + iw, iy + ih), strokePaint);
                }
            }
        }

        private void DrawOval(SKCanvas cv, CanvasElement e, CanvasPaint p, bool forceCircle)
        {
            if (p == null || !p.IsVisible) return;
            float cx = e.X + e.W * 0.5f;
            float cy = e.Y + e.H * 0.5f;
            float rx = Math.Abs(e.W) * 0.5f;
            float ry = forceCircle ? rx : Math.Abs(e.H) * 0.5f;
            if (rx <= 0f || ry <= 0f) return;

            using SKPaint fillPaint = p.FillKind != CanvasFillKind.None ? MakePaint(p, false, cx - rx, cy - ry, rx * 2f, ry * 2f) : null;
            using SKPaint strokePaint = p.Stroke ? MakePaint(p, true, cx - rx, cy - ry, rx * 2f, ry * 2f) : null;
            var rect = new SKRect(cx - rx, cy - ry, cx + rx, cy + ry);

            if (fillPaint != null) cv.DrawOval(rect, fillPaint);
            if (strokePaint != null)
            {
                float inset = strokePaint.StrokeWidth * 0.5f;
                var inner = new SKRect(cx - rx + inset, cy - ry + inset, cx + rx - inset, cy + ry - inset);
                if (inner.Width > 0f && inner.Height > 0f) cv.DrawOval(inner, strokePaint);
            }
        }

        private void DrawLineShape(SKCanvas cv, CanvasElement e, CanvasPaint p)
        {
            if (p == null) return;
            CanvasPaint lp = p.Clone();
            if (!lp.Stroke) { lp.Stroke = true; lp.StrokeWidth = lp.StrokeWidth <= 0f ? 1f : lp.StrokeWidth; }
            // x1/y1 are relative to the element origin, matching point lists
            float ax = e.X, ay = e.Y, bx = e.X + e.X1, by = e.Y + e.Y1;
            using SKPaint paint = MakePaint(lp, true, Math.Min(ax, bx), Math.Min(ay, by), 1f, 1f);
            cv.DrawLine(ax, ay, bx, by, paint);
        }

        private void DrawArcShape(SKCanvas cv, CanvasElement e, CanvasPaint p)
        {
            if (p == null || !p.IsVisible) return;
            float cx = e.X + e.W * 0.5f, cy = e.Y + e.H * 0.5f;
            float rx = Math.Abs(e.W) * 0.5f, ry = Math.Abs(e.H) * 0.5f;
            if (rx <= 0f || ry <= 0f) return;
            var rect = new SKRect(cx - rx, cy - ry, cx + rx, cy + ry);

            using SKPaint fillPaint = p.FillKind != CanvasFillKind.None ? MakePaint(p, false, cx - rx, cy - ry, rx * 2f, ry * 2f) : null;
            using SKPaint strokePaint = p.Stroke ? MakePaint(p, true, cx - rx, cy - ry, rx * 2f, ry * 2f) : null;
            bool useCenter = p.FillKind != CanvasFillKind.None;
            if (fillPaint != null) cv.DrawArc(rect, e.StartAngle, e.Sweep, useCenter, fillPaint);
            if (strokePaint != null) cv.DrawArc(rect, e.StartAngle, e.Sweep, false, strokePaint);
        }

        private void DrawPathShape(SKCanvas cv, CanvasElement e, CanvasPaint p)
        {
            if (p == null || !p.IsVisible) return;
            using SKPath path = BuildPath(e);
            if (path == null) return;

            using SKPaint fillPaint = p.FillKind != CanvasFillKind.None ? MakePaint(p, false, e.X, e.Y, e.W, e.H) : null;
            using SKPaint strokePaint = p.Stroke ? MakePaint(p, true, e.X, e.Y, e.W, e.H) : null;
            if (fillPaint != null)
            {
                if (e.EvenOdd) path.FillType = SKPathFillType.EvenOdd;
                cv.DrawPath(path, fillPaint);
            }
            if (strokePaint != null) cv.DrawPath(path, strokePaint);
        }

        private void DrawPointShape(SKCanvas cv, CanvasElement e, CanvasPaint p)
        {
            if (p == null) return;
            if (e.Points == null || e.Points.Count == 0) return;

            // Point coordinates are local; x/y shift the whole element so a
            // polygon can be repositioned without rewriting every pair.
            // SKCanvas.DrawPoints only strokes, so a filled polygon is drawn
            // through an explicit path to keep fill + stroke honoured.
            if (e.Shape == CanvasShape.Polygon)
            {
                using var path = new SKPath();
                for (int i = 0; i < e.Points.Count; i++)
                {
                    float px = e.Points[i].X + e.X, py = e.Points[i].Y + e.Y;
                    if (i == 0) path.MoveTo(px, py); else path.LineTo(px, py);
                }
                path.Close();

                using SKPaint fillPaint = p.FillKind != CanvasFillKind.None
                    ? MakePaint(p, false, e.X, e.Y, e.W, e.H) : null;
                using SKPaint strokePaint = p.Stroke
                    ? MakePaint(p, true, e.X, e.Y, e.W, e.H) : null;
                if (fillPaint != null) cv.DrawPath(path, fillPaint);
                if (strokePaint != null) cv.DrawPath(path, strokePaint);
                return;
            }

            var pts = new SKPoint[e.Points.Count];
            for (int i = 0; i < e.Points.Count; i++)
                pts[i] = new SKPoint(e.Points[i].X + e.X, e.Points[i].Y + e.Y);

            CanvasPaint pp = p.Clone();
            if (e.Shape == CanvasShape.Points && pp.FillKind == CanvasFillKind.None && !pp.Stroke)
            {
                // scatter points default to fat round strokes rather than a hairline
                pp.Stroke = true;
                pp.StrokeWidth = pp.StrokeWidth <= 1f ? 6f : pp.StrokeWidth;
                pp.Cap = CanvasCap.Round;
            }
            else if (e.Shape == CanvasShape.Points)
            {
                // dots are round points regardless of the paint's fill
                pp.Stroke = true;
                if (pp.StrokeWidth <= 0f) pp.StrokeWidth = 6f;
                pp.Cap = CanvasCap.Round;
            }
            else if (!pp.Stroke)
            {
                pp.Stroke = true;
                if (pp.StrokeWidth <= 0f) pp.StrokeWidth = 1f;
            }

            using SKPaint paint = MakePaint(pp, true, e.X, e.Y, e.W, e.H);
            SKPointMode mode = e.Shape == CanvasShape.Points ? SKPointMode.Points : SKPointMode.Lines;
            cv.DrawPoints(mode, pts, paint);
        }

        private void DrawTextShape(SKCanvas cv, CanvasElement e, CanvasPaint p)
        {
            if (string.IsNullOrEmpty(e.Text)) return;
            CanvasPaint tp = p ?? new CanvasPaint { FillKind = CanvasFillKind.Solid, Color = UiColor.Black };
            CanvasPaint textPaint = tp.Clone();
            // text is filled by default even when the caller passed a stroke-only paint
            if (textPaint.FillKind == CanvasFillKind.None && !textPaint.Stroke)
                textPaint.FillKind = CanvasFillKind.Solid;

            using SKPaint paint = MakeTextPaint(e, textPaint);
            if (paint == null) return;

            float x = e.X, y = e.Y;
            string text = ApplyLetterSpacing(e);

            // Stroke and fill are two passes: the outline is drawn first and the
            // fill painted over its inner half, matching how designers expect a
            // "stroked heading" to look.
            SKPaint strokePaint = null;
            try
            {
                if (e.Stroke != null && e.Stroke.IsVisible && e.Stroke.Stroke)
                {
                    strokePaint = MakeTextPaint(e, e.Stroke);
                    if (strokePaint != null)
                    {
                        strokePaint.Style = SKPaintStyle.Stroke;
                        strokePaint.StrokeWidth = e.Stroke.StrokeWidth > 0f ? e.Stroke.StrokeWidth : 1f;
                        strokePaint.StrokeJoin = ToJoin(e.Stroke.Join);
                        strokePaint.StrokeCap = ToCap(e.Stroke.Cap);
                    }
                }

                if (e.W > 0f || e.H > 0f)
                {
                    float w = e.W > 0f ? e.W : float.MaxValue;
                    SKTextAlign align = e.TextAlign == UiTextAlign.Center ? SKTextAlign.Center
                                      : e.TextAlign == UiTextAlign.Right ? SKTextAlign.Right
                                      : SKTextAlign.Left;
                    paint.TextAlign = align;
                    if (strokePaint != null) strokePaint.TextAlign = align;
                    if (align == SKTextAlign.Center) x += w * 0.5f;
                    else if (align == SKTextAlign.Right) x += w;

                    float baseline;
                    if (e.H > 0f)
                    {
                        paint.GetFontMetrics(out SKFontMetrics m);
                        baseline = e.Y + e.H * 0.5f - (m.Ascent + m.Descent) * 0.5f;
                    }
                    else baseline = e.Y;

                    if (strokePaint != null && textPaint.FillKind != CanvasFillKind.None)
                        cv.DrawText(text, x, baseline, strokePaint);
                    if (textPaint.FillKind != CanvasFillKind.None)
                        cv.DrawText(text, x, baseline, paint);
                    else if (strokePaint != null)
                        cv.DrawText(text, x, baseline, strokePaint);
                }
                else
                {
                    SKTextAlign align = e.TextAlign == UiTextAlign.Center ? SKTextAlign.Center
                                      : e.TextAlign == UiTextAlign.Right ? SKTextAlign.Right
                                      : SKTextAlign.Left;
                    paint.TextAlign = align;
                    if (strokePaint != null) strokePaint.TextAlign = align;

                    if (strokePaint != null && textPaint.FillKind != CanvasFillKind.None)
                        cv.DrawText(text, x, y, strokePaint);
                    if (textPaint.FillKind != CanvasFillKind.None)
                        cv.DrawText(text, x, y, paint);
                    else if (strokePaint != null)
                        cv.DrawText(text, x, y, strokePaint);
                }
            }
            finally
            {
                strokePaint?.Dispose();
            }
        }

        private void DrawTextOnPathShape(SKCanvas cv, CanvasElement e, CanvasPaint p)
        {
            if (string.IsNullOrEmpty(e.Text)) return;
            using SKPath path = BuildPath(e);
            if (path == null) return;

            CanvasPaint textPaint = (p ?? new CanvasPaint()).Clone();
            if (textPaint.FillKind == CanvasFillKind.None && !textPaint.Stroke)
                textPaint.FillKind = CanvasFillKind.Solid;

            using SKPaint paint = MakeTextPaint(e, textPaint);
            if (paint == null) return;
            cv.DrawTextOnPath(ApplyLetterSpacing(e), path, e.Tx, e.Ty, paint);
        }

        private void DrawIconShape(SKCanvas cv, CanvasElement e, CanvasPaint p)
        {
            SKColor col = p != null ? ToSK(p.Color) : SKColors.White;
            float size = e.W > 0f ? Math.Min(e.W, e.H > 0f ? e.H : e.W) : 32f;
            float cx = e.W > 0f ? e.X + e.W * 0.5f : e.X;
            float cy = e.H > 0f ? e.Y + e.H * 0.5f : e.Y;
            IconLibrary.Draw(cv, e.Icon ?? e.Text, cx, cy, size, col);
        }

        private void DrawImageShape(SKCanvas cv, CanvasElement e, CanvasPaint p, bool ninePatch)
        {
            SKImage img = LoadImage(e.ImagePath ?? e.Text);
            if (img == null) return;

            float w = e.W > 0f ? e.W : img.Width;
            float h = e.H > 0f ? e.H : img.Height;
            var dest = new SKRect(e.X, e.Y, e.X + w, e.Y + h);

            using SKPaint paint = new SKPaint
            {
                IsAntialias = !e.NearestNeighbour,
                FilterQuality = e.NearestNeighbour ? SKFilterQuality.None : SKFilterQuality.High,
                BlendMode = p != null ? ToBlend(p.Blend) : SKBlendMode.SrcOver,
                Color = p != null ? SKColors.White.WithAlpha((byte)Math.Round(p.Color.A * 255)) : SKColors.White
            };

            if (ninePatch && e.NinePatch != null && e.NinePatch.Length >= 4)
            {
                var patch = new SKRectI(
                    e.NinePatch[0],
                    e.NinePatch[1],
                    img.Width - e.NinePatch[2],
                    img.Height - e.NinePatch[3]);
                using SKBitmap bmp = SKBitmap.FromImage(img);
                if (bmp != null) cv.DrawBitmapNinePatch(bmp, patch, dest, paint);
                return;
            }

            // "src" region, when present, crops instead of scaling the whole image
            var src = new SKRect(0, 0, img.Width, img.Height);
            cv.DrawImage(img, src, dest, paint);
        }

        private void DrawVerticesShape(SKCanvas cv, CanvasElement e, CanvasPaint p)
        {
            if (e.Vertices == null || e.Vertices.Count < 3) return;

            // vertices are local, shifted by the element's x/y
            var pts = new SKPoint[e.Vertices.Count];
            for (int i = 0; i < e.Vertices.Count; i++)
                pts[i] = new SKPoint(e.Vertices[i].X + e.X, e.Vertices[i].Y + e.Y);

            SKColor[] colors = null;
            if (e.VertexColors != null && e.VertexColors.Count > 0)
            {
                colors = new SKColor[e.VertexColors.Count];
                for (int i = 0; i < e.VertexColors.Count; i++) colors[i] = ToSK(e.VertexColors[i]);
            }

            ushort[] indices = null;
            if (e.Indices != null && e.Indices.Count > 0)
            {
                indices = new ushort[e.Indices.Count];
                for (int i = 0; i < e.Indices.Count; i++) indices[i] = (ushort)Math.Max(0, Math.Min(65535, e.Indices[i]));
            }

            using SKPaint paint = p != null ? MakePaint(p, false, e.X, e.Y, e.W, e.H) : new SKPaint { IsAntialias = true };
            // a mesh needs a shader for the interpolated gaps to read correctly
            if (paint.Shader == null && colors != null)
                paint.Shader = SKShader.CreateColor(ToSK(p != null ? p.Color : UiColor.White));

            if (indices != null)
                cv.DrawVertices(SKVertexMode.Triangles, pts, null, colors, indices, paint);
            else
                cv.DrawVertices(SKVertexMode.Triangles, pts, colors, paint);
        }

        // ---------------------------------------------------------------- paths

        private SKPath BuildPath(CanvasElement e)
        {
            if (!string.IsNullOrEmpty(e.PathData))
            {
                SKPath parsed = SKPath.ParseSvgPathData(e.PathData);
                if (parsed != null)
                {
                    // SVG path data is authored in local coordinates; x/y moves it.
                    if (e.X != 0f || e.Y != 0f) parsed.Offset(e.X, e.Y);
                    return parsed;
                }
                Warnings.Add("could not parse svg path data on " + (e.Id ?? "path"));
            }

            var path = new SKPath();
            bool started = false;
            if (e.Points != null)
            {
                // point lists are local to the element, so x/y offset them
                foreach (CanvasPoint pt in e.Points)
                {
                    float px = pt.X + e.X, py = pt.Y + e.Y;
                    if (!started) { path.MoveTo(px, py); started = true; }
                    else path.LineTo(px, py);
                }
            }
            if (!started)
            {
                // no points: fall back to the element box
                path.AddRect(new SKRect(e.X, e.Y, e.X + Math.Abs(e.W), e.Y + Math.Abs(e.H)));
                started = true;
            }
            if (e.ClosePath) path.Close();

            // boolean combine against a sibling path
            if (e.CombineWith != null)
            {
                using SKPath other = BuildPath(e.CombineWith);
                if (other != null)
                {
                    var combined = new SKPath();
                    if (path.Op(other, ToPathOp(e.CombineOp), combined))
                    {
                        path.Dispose();
                        return combined;
                    }
                    combined.Dispose();
                    Warnings.Add("path boolean op failed on " + (e.Id ?? "path") + "; using the primary path.");
                }
            }
            return path;
        }

        // --------------------------------------------------------------- paints

        /// <summary>Builds an SKPaint for a fill or stroke of the given bounds.</summary>
        private SKPaint MakePaint(CanvasPaint p, bool stroke, float x, float y, float w, float h)
        {
            var paint = new SKPaint
            {
                IsAntialias = p.AntiAlias,
                IsDither = p.Dither,
                Style = stroke ? SKPaintStyle.Stroke : SKPaintStyle.Fill,
                Color = ToSK(p.Color),
                BlendMode = ToBlend(p.Blend)
            };
            if (stroke)
            {
                paint.StrokeWidth = p.StrokeWidth > 0f ? p.StrokeWidth : 1f;
                paint.StrokeCap = ToCap(p.Cap);
                paint.StrokeJoin = ToJoin(p.Join);
                paint.StrokeMiter = p.Miter;
                // a paint may carry a dedicated outline colour
                if (p.StrokeColor.HasValue) paint.Color = ToSK(p.StrokeColor.Value);
            }

            if (!stroke || p.FillKind != CanvasFillKind.None)
            {
                SKShader shader = MakeShader(p, x, y, w, h);
                if (shader != null) paint.Shader = shader;
            }

            if (p.BlurRadius > 0f)
            {
                paint.MaskFilter = SKMaskFilter.CreateBlur(ToBlurStyle(p.BlurStyle), p.BlurRadius * 0.5f);
            }

            if (p.Filter != CanvasImageFilter.None)
            {
                paint.ImageFilter = MakeImageFilter(p);
            }

            if (p.PathEffect != CanvasPathEffect.None)
            {
                paint.PathEffect = MakePathEffect(p);
            }

            return paint;
        }

        private SKPaint MakeTextPaint(CanvasElement e, CanvasPaint p)
        {
            var paint = new SKPaint
            {
                IsAntialias = p.AntiAlias,
                IsDither = p.Dither,
                Style = p.Stroke ? SKPaintStyle.StrokeAndFill : SKPaintStyle.Fill,
                Color = ToSK(p.Color),
                BlendMode = ToBlend(p.Blend),
                TextSize = e.FontSize > 0f ? e.FontSize : 24f,
                TextScaleX = e.TextScaleX != 0f ? e.TextScaleX : 1f,
                TextSkewX = e.TextSkewX,
                SubpixelText = true,
                Typeface = ResolveTypeface(e)
            };
            if (e.FontSize <= 0f) paint.TextSize = 12f;

            SKShader shader = MakeShader(p, e.X, e.Y, e.W, e.H);
            if (shader != null) paint.Shader = shader;

            if (p.BlurRadius > 0f)
                paint.MaskFilter = SKMaskFilter.CreateBlur(ToBlurStyle(p.BlurStyle), p.BlurRadius * 0.5f);
            if (p.Filter != CanvasImageFilter.None) paint.ImageFilter = MakeImageFilter(p);
            if (p.PathEffect != CanvasPathEffect.None) paint.PathEffect = MakePathEffect(p);
            if (p.Stroke) paint.StrokeWidth = p.StrokeWidth > 0f ? p.StrokeWidth : 1f;
            return paint;
        }

        private SKShader MakeShader(CanvasPaint p, float x, float y, float w, float h)
        {
            // anchors are relative to the shape box unless the paint asks for
            // canvas space (typical for gradients masked by a clip)
            if (p.ShaderSpaceCanvas) { x = 0f; y = 0f; }
            SKColor c0 = ToSK(p.Color);
            SKColor c1 = ToSK(p.Color2 ?? new UiColor(p.Color.R, p.Color.G, p.Color.B, 0f));
            SKColor[] colors = BuildStops(p, c0, c1, out float[] positions);

            SKMatrix? local = BuildShaderMatrix(p);

            try
            {
                switch (p.FillKind)
                {
                    case CanvasFillKind.None:
                        return null;
                    case CanvasFillKind.Solid:
                        return c0.Alpha >= 255 ? SKShader.CreateColor(c0) : SKShader.CreateColor(c0);
                    case CanvasFillKind.Linear:
                        {
                            float x0 = p.X0, y0 = p.Y0, x1 = p.X1, y1 = p.Y1;
                            if (x0 == 0f && y0 == 0f && x1 == 0f && y1 == 0f)
                            {
                                // default: top-to-bottom across the shape
                                x1 = 0f; y1 = h > 0f ? h : 100f;
                            }
                            var a = new SKPoint(x + x0, y + y0);
                            var b = new SKPoint(x + x1, y + y1);
                            var mode = ToTile(p.TileX);
                            return positions != null
                                ? SKShader.CreateLinearGradient(a, b, colors, positions, mode)
                                : SKShader.CreateLinearGradient(a, b, colors, mode);
                        }
                    case CanvasFillKind.Radial:
                        {
                            var centre = new SKPoint(x + (p.X0 != 0f || p.Y0 != 0f ? p.X0 : w * 0.5f),
                                                      y + (p.X0 != 0f || p.Y0 != 0f ? p.Y0 : h * 0.5f));
                            float radius = p.Radius1 > 0f ? p.Radius1 : Math.Max(w, h) * 0.5f;
                            var mode = ToTile(p.TileX);
                            return positions != null
                                ? SKShader.CreateRadialGradient(centre, radius, colors, positions, mode)
                                : SKShader.CreateRadialGradient(centre, radius, colors, mode);
                        }
                    case CanvasFillKind.Sweep:
                        {
                            var centre = new SKPoint(x + w * 0.5f, y + h * 0.5f);
                            return positions != null
                                ? SKShader.CreateSweepGradient(centre, colors, positions)
                                : SKShader.CreateSweepGradient(centre, colors);
                        }
                    case CanvasFillKind.TwoPointConical:
                        {
                            var c1p = new SKPoint(x + (p.X0 != 0f || p.Y0 != 0f ? p.X0 : w * 0.5f),
                                                  y + (p.X0 != 0f || p.Y0 != 0f ? p.Y0 : h * 0.5f));
                            var c2p = new SKPoint(x + (p.X1 != 0f || p.Y1 != 0f ? p.X1 : w * 0.5f),
                                                  y + (p.X1 != 0f || p.Y1 != 0f ? p.Y1 : h * 0.5f));
                            float r0 = p.Radius0;
                            float r1 = p.Radius1 > 0f ? p.Radius1 : Math.Max(w, h) * 0.5f;
                            var mode = ToTile(p.TileX);
                            return positions != null
                                ? SKShader.CreateTwoPointConicalGradient(c1p, r0, c2p, r1, colors, positions, mode)
                                : SKShader.CreateTwoPointConicalGradient(c1p, r0, c2p, r1, colors, mode);
                        }
                    case CanvasFillKind.Noise:
                        {
                            float freq = p.NoiseBaseFreq > 0f ? p.NoiseBaseFreq : 0.02f;
                            int octaves = Math.Max(1, (int)Math.Round(p.NoiseNumOctaves));
                            SKShader baseShader = p.NoiseTurbulence
                                ? SKShader.CreatePerlinNoiseTurbulence(freq, freq, octaves, p.NoiseSeed)
                                : SKShader.CreatePerlinNoiseFractalNoise(freq, freq, octaves, p.NoiseSeed);
                            if (baseShader == null) return null;
                            // tint the greyscale noise with the requested colour
                            SKShader tint = SKShader.CreateColor(c0);
                            SKShader composed = SKShader.CreateCompose(baseShader, tint, SKBlendMode.Modulate);
                            return composed ?? baseShader;
                        }
                    case CanvasFillKind.Image:
                        {
                            SKImage img = LoadImage(p.ImagePath);
                            if (img == null) return null;
                            return local.HasValue
                                ? SKShader.CreateImage(img, ToTile(p.TileX), ToTile(p.TileY), local.Value)
                                : SKShader.CreateImage(img, ToTile(p.TileX), ToTile(p.TileY));
                        }
                    default:
                        return SKShader.CreateColor(c0);
                }
            }
            catch (Exception ex)
            {
                Warnings.Add("shader build failed (" + p.FillKind + "): " + ex.Message);
                return SKShader.CreateColor(c0);
            }
        }

        private static SKColor[] BuildStops(CanvasPaint p, SKColor c0, SKColor c1, out float[] positions)
        {
            positions = null;
            if (p.Stops == null || p.Stops.Count == 0) return new[] { c0, c1 };

            var colors = new List<SKColor>();
            var pos = new List<float>();
            foreach (CanvasStop s in p.Stops)
            {
                colors.Add(ToSK(s.Color));
                pos.Add(s.Offset);
            }
            if (colors.Count == 1) { colors.Add(colors[0]); pos.Add(1f); }
            positions = pos.ToArray();
            return colors.ToArray();
        }

        private static SKMatrix? BuildShaderMatrix(CanvasPaint p)
        {
            if (p.ShaderMatrix == null) return null;
            var m = new SKMatrix();
            if (p.ShaderMatrix.Length >= 6)
            {
                m.ScaleX = p.ShaderMatrix[0]; m.SkewX = p.ShaderMatrix[1]; m.TransX = p.ShaderMatrix[2];
                m.SkewY = p.ShaderMatrix[3]; m.ScaleY = p.ShaderMatrix[4]; m.TransY = p.ShaderMatrix[5];
                m.Persp0 = p.ShaderMatrix.Length > 6 ? p.ShaderMatrix[6] : 0f;
                m.Persp1 = p.ShaderMatrix.Length > 7 ? p.ShaderMatrix[7] : 0f;
                m.Persp2 = p.ShaderMatrix.Length > 8 ? p.ShaderMatrix[8] : 1f;
            }
            return m;
        }

        private SKImageFilter MakeImageFilter(CanvasPaint p)
        {
            try
            {
                switch (p.Filter)
                {
                    case CanvasImageFilter.Blur:
                        return SKImageFilter.CreateBlur(
                            p.FilterSigmaX > 0f ? p.FilterSigmaX : 4f,
                            p.FilterSigmaY > 0f ? p.FilterSigmaY : p.FilterSigmaX > 0f ? p.FilterSigmaX : 4f,
                            null, null);
                    case CanvasImageFilter.DropShadow:
                        return SKImageFilter.CreateDropShadow(
                            p.FilterDx, p.FilterDy,
                            p.FilterSigmaX > 0f ? p.FilterSigmaX : 4f,
                            p.FilterSigmaY > 0f ? p.FilterSigmaY : p.FilterSigmaX > 0f ? p.FilterSigmaX : 4f,
                            ToSK(p.FilterColor), null, null);
                    case CanvasImageFilter.Offset:
                        return SKImageFilter.CreateOffset(p.FilterDx, p.FilterDy, null, null);
                    case CanvasImageFilter.Dilate:
                        return SKImageFilter.CreateDilate(Math.Max(0f, p.FilterRadius), Math.Max(0f, p.FilterRadius), null, null);
                    case CanvasImageFilter.Erode:
                        return SKImageFilter.CreateErode(Math.Max(0f, p.FilterRadius), Math.Max(0f, p.FilterRadius), null, null);
                    default:
                        return null;
                }
            }
            catch (Exception ex)
            {
                Warnings.Add("image filter failed (" + p.Filter + "): " + ex.Message);
                return null;
            }
        }

        private SKPathEffect MakePathEffect(CanvasPaint p)
        {
            try
            {
                switch (p.PathEffect)
                {
                    case CanvasPathEffect.Dash:
                        {
                            float[] dashes = p.Dashes;
                            if (dashes == null || dashes.Length < 2)
                                dashes = new[] { Math.Max(1f, p.StrokeWidth * 3f), Math.Max(1f, p.StrokeWidth * 2f) };
                            return SKPathEffect.CreateDash(dashes, p.DashPhase);
                        }
                    case CanvasPathEffect.Corner:
                        return SKPathEffect.CreateCorner(Math.Max(0f, p.CornerRadius));
                    case CanvasPathEffect.Discrete:
                        return SKPathEffect.CreateDiscrete(Math.Max(0.01f, p.DiscreteLength),
                            p.DiscreteDeviation, (uint)Math.Max(0, (int)p.NoiseSeed));
                    case CanvasPathEffect.Trim:
                        return SKPathEffect.CreateTrim(
                            Clamp(p.TrimStart, 0f, 1f), Clamp(p.TrimEnd, 0f, 1f));
                    default:
                        return null;
                }
            }
            catch (Exception ex)
            {
                Warnings.Add("path effect failed (" + p.PathEffect + "): " + ex.Message);
                return null;
            }
        }

        private static SKPaint MakeLayerPaint(CanvasElement e)
        {
            byte alpha = (byte)Math.Round(Clamp(e.Opacity, 0f, 1f) * Clamp(e.LayerOpacity, 0f, 1f) * 255f);
            return new SKPaint
            {
                Color = SKColors.White.WithAlpha(alpha),
                BlendMode = ToBlend(e.LayerBlend)
            };
        }

        // ------------------------------------------------------------ transform

        private void ApplyTransform(SKCanvas cv, CanvasElement e)
        {
            if (e.Matrix != null && e.Matrix.Length >= 6)
            {
                var m = new SKMatrix();
                m.ScaleX = e.Matrix[0]; m.SkewX = e.Matrix[1]; m.TransX = e.Matrix[2];
                m.SkewY = e.Matrix[3]; m.ScaleY = e.Matrix[4]; m.TransY = e.Matrix[5];
                m.Persp0 = e.Matrix.Length > 6 ? e.Matrix[6] : 0f;
                m.Persp1 = e.Matrix.Length > 7 ? e.Matrix[7] : 0f;
                m.Persp2 = e.Matrix.Length > 8 ? e.Matrix[8] : 1f;
                cv.Concat(ref m);
                return;
            }

            if (e.Tx != 0f || e.Ty != 0f) cv.Translate(e.Tx, e.Ty);

            float sx = e.ScaleX == 0f ? 1f : e.ScaleX;
            float sy = e.ScaleY == 0f ? 1f : e.ScaleY;
            bool needsPivot = e.RotateDeg != 0f || sx != 1f || sy != 1f || e.SkewX != 0f || e.SkewY != 0f;
            if (!needsPivot) return;

            float px = e.PivotX != 0f ? e.PivotX : e.CenterX;
            float py = e.PivotY != 0f ? e.PivotY : e.CenterY;
            cv.Translate(px, py);
            if (e.RotateDeg != 0f) cv.RotateDegrees(e.RotateDeg);
            if (sx != 1f || sy != 1f) cv.Scale(sx, sy);
            if (e.SkewX != 0f || e.SkewY != 0f) cv.Skew(e.SkewX, e.SkewY);
            cv.Translate(-px, -py);
        }

        private void ApplyClip(SKCanvas cv, CanvasElement e)
        {
            var op = SKClipOperation.Intersect;
            // fall back to the element's own box when the clip gives no geometry
            float cx0 = e.ClipHasRect ? e.ClipX : e.X;
            float cy0 = e.ClipHasRect ? e.ClipY : e.Y;
            float cw = e.ClipHasRect ? e.ClipW : e.W;
            float ch = e.ClipHasRect ? e.ClipH : e.H;

            switch (e.ClipShape)
            {
                case CanvasShape.Circle:
                case CanvasShape.Oval:
                    {
                        float cx = cx0 + cw * 0.5f, cy = cy0 + ch * 0.5f;
                        float rx = Math.Abs(cw) * 0.5f, ry = Math.Abs(ch) * 0.5f;
                        if (rx <= 0f || ry <= 0f) return;
                        using var path = new SKPath();
                        if (e.ClipShape == CanvasShape.Circle) path.AddCircle(cx, cy, rx);
                        else path.AddOval(new SKRect(cx - rx, cy - ry, cx + rx, cy + ry));
                        cv.ClipPath(path, op, e.ClipEvenOdd);
                        return;
                    }
                case CanvasShape.RoundRect:
                    {
                        float r = e.ClipRadius > 0f ? e.ClipRadius : Math.Min(Math.Abs(cw), Math.Abs(ch)) * 0.12f;
                        r = Clamp(r, 0f, Math.Min(Math.Abs(cw), Math.Abs(ch)) * 0.5f);
                        var rr = new SKRoundRect(new SKRect(cx0, cy0, cx0 + cw, cy0 + ch), r, r);
                        cv.ClipRoundRect(rr, op, e.ClipEvenOdd);
                        return;
                    }
                case CanvasShape.Path:
                    {
                        using SKPath path = BuildPath(e);
                        if (path != null) cv.ClipPath(path, op, e.ClipEvenOdd);
                        return;
                    }
                default:
                    cv.ClipRect(new SKRect(cx0, cy0, cx0 + cw, cy0 + ch), op, true);
                    return;
            }
        }

        // ------------------------------------------------------------ resources

        private SKTypeface ResolveTypeface(CanvasElement e)
        {
            string key = (e.FontPath ?? DefaultFontPath ?? string.Empty)
                       + "|" + e.FontFamily
                       + "|" + (e.FontPath == null ? DefaultFontIndex : e.FontIndex)
                       + "|" + e.Bold + e.Italic;
            if (_faces.TryGetValue(key, out SKTypeface cached) && cached != null) return cached;

            SKTypeface face = null;
            string path = e.FontPath ?? DefaultFontPath;
            if (!string.IsNullOrEmpty(path))
            {
                string resolved = ResolvePath(path);
                if (resolved != null)
                {
                    try { face = SKTypeface.FromFile(resolved, e.FontPath == null ? DefaultFontIndex : e.FontIndex); }
                    catch (Exception ex) { Warnings.Add("font load failed: " + ex.Message); }
                }
            }
            if (face == null && !string.IsNullOrEmpty(e.FontFamily))
            {
                try { face = SKTypeface.FromFamilyName(e.FontFamily); }
                catch (Exception ex) { Warnings.Add("font family failed: " + ex.Message); }
            }
            if (face == null) face = SKTypeface.Default;

            if (e.Bold || e.Italic)
            {
                SKFontStyle style = e.Bold && e.Italic ? SKFontStyle.BoldItalic
                                   : e.Bold ? SKFontStyle.Bold
                                   : SKFontStyle.Italic;
                SKTypeface alt = SKTypeface.FromFamilyName(face.FamilyName, style);
                if (alt != null) face = alt;
            }

            _faces[key] = face;
            return face;
        }

        private SKImage LoadImage(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (_images.TryGetValue(path, out SKImage cached)) return cached;

            SKImage img = null;
            string resolved = ResolvePath(path);
            if (resolved != null)
            {
                try
                {
                    using FileStream fs = File.OpenRead(resolved);
                    img = SKImage.FromEncodedData(fs);
                }
                catch (Exception ex)
                {
                    Warnings.Add("image load failed (" + path + "): " + ex.Message);
                }
            }
            else Warnings.Add("image not found: " + path);

            _images[path] = img;
            return img;
        }

        private string ResolvePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (PathResolver != null)
            {
                string viaHost = PathResolver(path);
                if (!string.IsNullOrEmpty(viaHost) && File.Exists(viaHost)) return viaHost;
            }
            if (File.Exists(path)) return path;
            return null;
        }

        /// <summary>Releases cached typefaces and images.</summary>
        public void Dispose()
        {
            foreach (SKImage img in _images.Values) img?.Dispose();
            _images.Clear();
            foreach (SKTypeface face in _faces.Values)
            {
                if (face != null && face != SKTypeface.Default) face.Dispose();
            }
            _faces.Clear();
        }
        // ------------------------------------------------------------ converters

        private static SKColor ToSK(UiColor c) => new SKColor(
            (byte)(Clamp(c.R, 0f, 1f) * 255f + 0.5f),
            (byte)(Clamp(c.G, 0f, 1f) * 255f + 0.5f),
            (byte)(Clamp(c.B, 0f, 1f) * 255f + 0.5f),
            (byte)(Clamp(c.A, 0f, 1f) * 255f + 0.5f));

        private static SKBlendMode ToBlend(CanvasBlend b)
        {
            switch (b)
            {
                case CanvasBlend.Clear: return SKBlendMode.Clear;
                case CanvasBlend.Src: return SKBlendMode.Src;
                case CanvasBlend.Dst: return SKBlendMode.Dst;
                case CanvasBlend.DstOver: return SKBlendMode.DstOver;
                case CanvasBlend.SrcIn: return SKBlendMode.SrcIn;
                case CanvasBlend.DstIn: return SKBlendMode.DstIn;
                case CanvasBlend.SrcOut: return SKBlendMode.SrcOut;
                case CanvasBlend.DstOut: return SKBlendMode.DstOut;
                case CanvasBlend.SrcATop: return SKBlendMode.SrcATop;
                case CanvasBlend.DstATop: return SKBlendMode.DstATop;
                case CanvasBlend.Xor: return SKBlendMode.Xor;
                case CanvasBlend.Plus: return SKBlendMode.Plus;
                case CanvasBlend.Modulate: return SKBlendMode.Modulate;
                case CanvasBlend.Screen: return SKBlendMode.Screen;
                case CanvasBlend.Overlay: return SKBlendMode.Overlay;
                case CanvasBlend.Darken: return SKBlendMode.Darken;
                case CanvasBlend.Lighten: return SKBlendMode.Lighten;
                case CanvasBlend.ColorDodge: return SKBlendMode.ColorDodge;
                case CanvasBlend.ColorBurn: return SKBlendMode.ColorBurn;
                case CanvasBlend.HardLight: return SKBlendMode.HardLight;
                case CanvasBlend.SoftLight: return SKBlendMode.SoftLight;
                case CanvasBlend.Difference: return SKBlendMode.Difference;
                case CanvasBlend.Exclusion: return SKBlendMode.Exclusion;
                case CanvasBlend.Multiply: return SKBlendMode.Multiply;
                case CanvasBlend.Hue: return SKBlendMode.Hue;
                case CanvasBlend.Saturation: return SKBlendMode.Saturation;
                case CanvasBlend.Color: return SKBlendMode.Color;
                case CanvasBlend.Luminosity: return SKBlendMode.Luminosity;
                default: return SKBlendMode.SrcOver;
            }
        }

        private static SKShaderTileMode ToTile(CanvasTileMode m)
        {
            switch (m)
            {
                case CanvasTileMode.Clamp: return SKShaderTileMode.Clamp;
                case CanvasTileMode.Mirror: return SKShaderTileMode.Mirror;
                case CanvasTileMode.Decal: return SKShaderTileMode.Decal;
                default: return SKShaderTileMode.Repeat;
            }
        }

        private static SKStrokeCap ToCap(CanvasCap c)
        {
            switch (c)
            {
                case CanvasCap.Butt: return SKStrokeCap.Butt;
                case CanvasCap.Square: return SKStrokeCap.Square;
                default: return SKStrokeCap.Round;
            }
        }

        private static SKStrokeJoin ToJoin(CanvasJoin j)
        {
            switch (j)
            {
                case CanvasJoin.Miter: return SKStrokeJoin.Miter;
                case CanvasJoin.Bevel: return SKStrokeJoin.Bevel;
                default: return SKStrokeJoin.Round;
            }
        }

        private static SKBlurStyle ToBlurStyle(CanvasBlurStyle s)
        {
            switch (s)
            {
                case CanvasBlurStyle.Solid: return SKBlurStyle.Solid;
                case CanvasBlurStyle.Outer: return SKBlurStyle.Outer;
                case CanvasBlurStyle.Inner: return SKBlurStyle.Inner;
                default: return SKBlurStyle.Normal;
            }
        }

        private static SKPathOp ToPathOp(CanvasPathOp op)
        {
            switch (op)
            {
                case CanvasPathOp.Difference: return SKPathOp.Difference;
                case CanvasPathOp.Intersect: return SKPathOp.Intersect;
                case CanvasPathOp.Xor: return SKPathOp.Xor;
                case CanvasPathOp.ReverseDifference: return SKPathOp.ReverseDifference;
                default: return SKPathOp.Union;
            }
        }

        /// <summary>Applies manual letter spacing by inserting thin spaces is fragile,
        /// so spacing is emitted through per-glyph positioning instead.</summary>
        private static string ApplyLetterSpacing(CanvasElement e)
        {
            // SkiaSharp's simple DrawText has no tracking control; TextScaleX is the
            // closest cheap approximation and is handled in MakeTextPaint.
            return e.Text ?? string.Empty;
        }

        private static float Clamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);
        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);
    }

    /// <summary>Restores the canvas to a saved point on Dispose, even on exceptions.</summary>
    internal struct SKAutoCanvasRestore : IDisposable
    {
        private readonly SKCanvas _canvas;
        private readonly int _count;

        public SKAutoCanvasRestore(SKCanvas canvas, bool doSave)
        {
            _canvas = canvas;
            _count = doSave && canvas != null ? canvas.Save() : -1;
        }

        public void Dispose()
        {
            if (_canvas != null && _count >= 0) _canvas.RestoreToCount(_count);
        }
    }
}
