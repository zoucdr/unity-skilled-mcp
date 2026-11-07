using System;
using SkiaSharp;

namespace UniMcp.UiGen
{
    /// <summary>
    /// Built-in vector icons drawn with plain SkiaSharp primitives.
    /// Shared by the UI control renderer and the generic canvas renderer so both
    /// spell icon names identically.
    /// </summary>
    public static class IconLibrary
    {
        /// <summary>Names accepted by <see cref="Draw"/>.</summary>
        public static readonly string[] Names =
        {
            "check", "cross", "arrow", "chevron-down", "star", "menu",
            "play", "plus", "minus", "search", "gear", "heart"
        };

        /// <summary>Draws an icon centred at (cx,cy) inside a box of <paramref name="size"/>.</summary>
        public static void Draw(SKCanvas cv, string name, float cx, float cy, float size, SKColor col)
        {
            if (cv == null || size <= 0f || col.Alpha == 0) return;
            using var paint = new SKPaint { IsAntialias = true, Color = col, Style = SKPaintStyle.Fill };
            using var stroke = new SKPaint
            {
                IsAntialias = true,
                Color = col,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = Math.Max(1f, size * 0.11f),
                StrokeCap = SKStrokeCap.Round,
                StrokeJoin = SKStrokeJoin.Round
            };
            float s = size;
            switch ((name ?? string.Empty).ToLowerInvariant())
            {
                case "check":
                    using (var p = new SKPath())
                    {
                        p.MoveTo(cx - s * 0.30f, cy + s * 0.02f);
                        p.LineTo(cx - s * 0.08f, cy + s * 0.26f);
                        p.LineTo(cx + s * 0.32f, cy - s * 0.26f);
                        cv.DrawPath(p, stroke);
                    }
                    break;
                case "cross":
                case "close":
                    cv.DrawLine(cx - s * 0.26f, cy - s * 0.26f, cx + s * 0.26f, cy + s * 0.26f, stroke);
                    cv.DrawLine(cx - s * 0.26f, cy + s * 0.26f, cx + s * 0.26f, cy - s * 0.26f, stroke);
                    break;
                case "arrow":
                case "arrow-right":
                    using (var p = new SKPath())
                    {
                        p.MoveTo(cx - s * 0.24f, cy - s * 0.24f);
                        p.LineTo(cx + s * 0.22f, cy);
                        p.LineTo(cx - s * 0.24f, cy + s * 0.24f);
                        cv.DrawPath(p, stroke);
                    }
                    break;
                case "chevron-down":
                    using (var p = new SKPath())
                    {
                        p.MoveTo(cx - s * 0.24f, cy - s * 0.09f);
                        p.LineTo(cx, cy + s * 0.17f);
                        p.LineTo(cx + s * 0.24f, cy - s * 0.09f);
                        cv.DrawPath(p, stroke);
                    }
                    break;
                case "star":
                    DrawStar(cv, cx, cy, s * 0.46f, s * 0.20f, col);
                    break;
                case "menu":
                case "hamburger":
                    for (int i = -1; i <= 1; i++)
                        cv.DrawLine(cx - s * 0.30f, cy + i * s * 0.20f, cx + s * 0.30f, cy + i * s * 0.20f, stroke);
                    break;
                case "play":
                    using (var p = new SKPath())
                    {
                        p.MoveTo(cx - s * 0.22f, cy - s * 0.28f);
                        p.LineTo(cx + s * 0.30f, cy);
                        p.LineTo(cx - s * 0.22f, cy + s * 0.28f);
                        p.Close();
                        cv.DrawPath(p, paint);
                    }
                    break;
                case "plus":
                    cv.DrawLine(cx, cy - s * 0.28f, cx, cy + s * 0.28f, stroke);
                    cv.DrawLine(cx - s * 0.28f, cy, cx + s * 0.28f, cy, stroke);
                    break;
                case "minus":
                    cv.DrawLine(cx - s * 0.28f, cy, cx + s * 0.28f, cy, stroke);
                    break;
                case "search":
                    cv.DrawCircle(cx - s * 0.08f, cy - s * 0.08f, s * 0.22f, stroke);
                    cv.DrawLine(cx + s * 0.08f, cy + s * 0.08f, cx + s * 0.28f, cy + s * 0.28f, stroke);
                    break;
                case "gear":
                case "settings":
                    using (var p = new SKPath())
                    {
                        for (int i = 0; i < 8; i++)
                        {
                            double a = i * Math.PI / 4;
                            p.MoveTo(cx + (float)Math.Cos(a) * s * 0.28f, cy + (float)Math.Sin(a) * s * 0.28f);
                            p.LineTo(cx + (float)Math.Cos(a) * s * 0.40f, cy + (float)Math.Sin(a) * s * 0.40f);
                        }
                        cv.DrawPath(p, stroke);
                        cv.DrawCircle(cx, cy, s * 0.15f, stroke);
                    }
                    break;
                case "heart":
                    using (var p = new SKPath())
                    {
                        p.MoveTo(cx, cy + s * 0.28f);
                        p.CubicTo(cx - s * 0.44f, cy - s * 0.04f, cx - s * 0.19f, cy - s * 0.36f, cx, cy - s * 0.10f);
                        p.CubicTo(cx + s * 0.19f, cy - s * 0.36f, cx + s * 0.44f, cy - s * 0.04f, cx, cy + s * 0.28f);
                        cv.DrawPath(p, paint);
                    }
                    break;
                default:
                    FillCircle(cv, cx, cy, s * 0.16f, col);
                    break;
            }
        }

        private static void FillCircle(SKCanvas cv, float cx, float cy, float rad, SKColor col)
        {
            if (rad <= 0f) return;
            using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = col };
            cv.DrawCircle(cx, cy, rad, paint);
        }

        private static void DrawStar(SKCanvas cv, float cx, float cy, float ro, float ri, SKColor col)
        {
            using var path = new SKPath();
            for (int i = 0; i < 10; i++)
            {
                double a = -Math.PI / 2 + i * Math.PI / 5;
                float rad = (i % 2 == 0) ? ro : ri;
                float x = cx + (float)Math.Cos(a) * rad;
                float y = cy + (float)Math.Sin(a) * rad;
                if (i == 0) path.MoveTo(x, y); else path.LineTo(x, y);
            }
            path.Close();
            using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = col };
            cv.DrawPath(path, paint);
        }
    }
}
