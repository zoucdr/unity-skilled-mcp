using System;
using System.Collections.Generic;
using SkiaSharp;

namespace UniMcp.UiGen
{
    /// <summary>
    /// One rendered control image plus its placement metadata.
    /// All pixel fields are in the image's own (supersampled) space.
    /// </summary>
    public sealed class UiControlImage
    {
        public string Key;                 // "{id}_{state}"
        public byte[] Png;
        public int ImageW, ImageH;
        public int FrameX, FrameY, FrameW, FrameH;   // logical control rect inside the image
        public int NineL, NineT, NineR, NineB;       // 9-slice borders (image space)
        public string State;               // lowercase state name
        public string ControlId;
    }

    /// <summary>
    /// SkiaSharp-backed renderer. Pure .NET — no Unity dependency — so it can be
    /// compiled and executed standalone to verify the drawing pipeline.
    /// </summary>
    public sealed class UiGenRenderer
    {
        /// <summary>Default font file used when a style/theme does not override it.</summary>
        public string DefaultFontPath;
        public int DefaultFontIndex;

        private SKTypeface _cachedTf;
        private string _cachedPath;
        private int _cachedIndex;
        private readonly Dictionary<string, SKTypeface> _weightCache = new Dictionary<string, SKTypeface>();

        public UiGenRenderer() { }

        // ------------------------------------------------------------------ fonts

        private SKTypeface ResolveTypeface(string path)
        {
            string p = path ?? DefaultFontPath;
            int idx = (path == null) ? DefaultFontIndex : 0;
            if (_cachedTf != null && _cachedPath == p && _cachedIndex == idx) return _cachedTf;
            _cachedPath = p; _cachedIndex = idx;
            _cachedTf = (!string.IsNullOrEmpty(p) && System.IO.File.Exists(p))
                ? SKTypeface.FromFile(p, idx)
                : SKTypeface.Default;
            _weightCache.Clear();
            return _cachedTf;
        }

        /// <summary>Pick a weight variant of the base face without reloading the file.</summary>
        private static SKTypeface Weighted(SKTypeface baseFace, UiFontWeight weight)
        {
            if (baseFace == null) return null;
            SKFontStyle style;
            switch (weight)
            {
                case UiFontWeight.Bold: style = SKFontStyle.Bold; break;
                case UiFontWeight.Medium: style = SKFontStyle.Normal; break;
                default: return baseFace;
            }
            if (baseFace.FontStyle.Weight == style.Weight) return baseFace;
            var alt = SKTypeface.FromFamilyName(baseFace.FamilyName, style);
            return alt ?? baseFace;
        }

        // ------------------------------------------------------------------ colours

        private static SKColor C(UiColor c) => new SKColor(
            (byte)(c.R * 255f + 0.5f), (byte)(c.G * 255f + 0.5f),
            (byte)(c.B * 255f + 0.5f), (byte)(c.A * 255f + 0.5f));

        private static SKColor Alpha(SKColor c, float a)
            => new SKColor(c.Red, c.Green, c.Blue, (byte)(a * 255f + 0.5f));

        private static SKColor Mix(SKColor a, SKColor b, float t)
        {
            t = t < 0 ? 0 : (t > 1 ? 1 : t);
            return new SKColor(
                (byte)(a.Red + (b.Red - a.Red) * t),
                (byte)(a.Green + (b.Green - a.Green) * t),
                (byte)(a.Blue + (b.Blue - a.Blue) * t),
                (byte)(a.Alpha + (b.Alpha - a.Alpha) * t));
        }

        // ------------------------------------------------------------------ shapes

        private static SKRoundRect MakeRR(float x, float y, float w, float h, float r)
        {
            float m = Math.Min(w, h) / 2f;
            float rr = r > m ? m : (r < 0 ? 0 : r);
            return new SKRoundRect(new SKRect(x, y, x + w, y + h), rr, rr);
        }

        private static void FillCircle(SKCanvas cv, float cx, float cy, float rad, SKColor col)
        {
            if (rad <= 0) return;
            using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = col };
            cv.DrawCircle(cx, cy, rad, paint);
        }

        private static void StrokeRR(SKCanvas cv, float x, float y, float w, float h,
            float r, float bw, SKColor col)
        {
            if (bw <= 0 || col.Alpha == 0) return;
            using var paint = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                Color = col,
                StrokeWidth = bw,
                StrokeJoin = SKStrokeJoin.Round
            };
            cv.DrawRoundRect(MakeRR(x + bw / 2f, y + bw / 2f, w - bw, h - bw, Math.Max(0f, r - bw / 2f)), paint);
        }

        /// <summary>Shadow + gradient/solid fill + border. The single surface primitive.</summary>
        private void Surface(SKCanvas cv, float x, float y, float w, float h, float r,
            UiStyle s, float ss, bool withShadow = true)
        {
            SKColor bg = C(s.Background ?? UiColor.White);
            SKColor bgE = s.BackgroundEnd.HasValue ? C(s.BackgroundEnd.Value) : bg;

            if (withShadow)
            {
                float blur = (s.ShadowBlur ?? 0) * ss;
                if (blur > 0 && s.ShadowColor.HasValue && s.ShadowColor.Value.A > 0.001f)
                {
                    float ox = (s.ShadowX ?? 0f) * ss;
                    float oy = (s.ShadowY ?? 0f) * ss;
                    using var sh = new SKPaint
                    {
                        IsAntialias = true,
                        Style = SKPaintStyle.Fill,
                        Color = C(s.ShadowColor.Value),
                        MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, blur * 0.5f)
                    };
                    cv.DrawRoundRect(MakeRR(x + ox, y + oy, w, h, r), sh);
                }
            }

            using (var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill })
            {
                if (bg.Alpha == 0) { /* fully transparent fill: skip */ }
                else if (s.BackgroundEnd.HasValue && bgE != bg)
                {
                    SKPoint p0, p1;
                    switch (s.GradientDir)
                    {
                        case UiGradientDir.Horizontal:
                            p0 = new SKPoint(x, y + h / 2f); p1 = new SKPoint(x + w, y + h / 2f); break;
                        case UiGradientDir.Diagonal:
                            p0 = new SKPoint(x, y); p1 = new SKPoint(x + w, y + h); break;
                        default:
                            p0 = new SKPoint(x + w / 2f, y); p1 = new SKPoint(x + w / 2f, y + h); break;
                    }
                    using var shader = SKShader.CreateLinearGradient(p0, p1,
                        new[] { bg, bgE }, new[] { 0f, 1f }, SKShaderTileMode.Clamp);
                    paint.Shader = shader;
                    cv.DrawRoundRect(MakeRR(x, y, w, h, r), paint);
                }
                else
                {
                    paint.Color = bg;
                    cv.DrawRoundRect(MakeRR(x, y, w, h, r), paint);
                }
            }

            if (s.BorderColor.HasValue && (s.BorderWidth ?? 0) > 0)
                StrokeRR(cv, x, y, w, h, r, (s.BorderWidth ?? 0) * ss, C(s.BorderColor.Value));
        }

        // ------------------------------------------------------------------ text

        private SKPaint MakeTextPaint(UiStyle s, UiScreen screen, float ss, SKColor col, float boxH = 0f)
        {
            string fontPath = s.FontPath ?? DefaultFontPath;
            SKTypeface tf = ResolveTypeface(fontPath);
            if (s.FontWeight.HasValue) tf = Weighted(tf, s.FontWeight.Value);
            float size = (s.FontSize ?? screen.Theme.FontSize) * ss;
            // a control must never draw text taller than its own box
            if (boxH > 0f)
            {
                float maxSize = boxH * 0.58f;
                if (size > maxSize) size = maxSize;
            }
            return new SKPaint
            {
                IsAntialias = true,
                Color = col,
                Typeface = tf,
                TextSize = size < 1f ? 1f : size,
                SubpixelText = true,
                LcdRenderText = false
            };
        }

        /// <summary>Horizontal anchor for the given alignment inside a padded box.</summary>
        private static float AlignX(SKTextAlign al, float x, float w, float pad)
        {
            switch (al)
            {
                case SKTextAlign.Left: return x + pad;
                case SKTextAlign.Right: return x + w - pad;
                default: return x + w / 2f;
            }
        }

        private static SKTextAlign ToSkAlign(UiTextAlign a)
            => a == UiTextAlign.Left ? SKTextAlign.Left
             : a == UiTextAlign.Right ? SKTextAlign.Right
             : SKTextAlign.Center;

        /// <summary>Shrink + ellipsize text so it fits maxWidth.</summary>
        private static string FitText(SKPaint paint, string text, float maxWidth)
        {
            if (string.IsNullOrEmpty(text) || maxWidth <= 0) return text;
            if (paint.MeasureText(text) <= maxWidth) return text;
            const string ell = "…";
            float ellW = paint.MeasureText(ell);
            int lo = 0, hi = text.Length;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (paint.MeasureText(text.Substring(0, mid)) + ellW <= maxWidth) lo = mid;
                else hi = mid - 1;
            }
            return lo <= 0 ? "" : text.Substring(0, lo) + ell;
        }

        /// <summary>Draw one line of text vertically centred in [y, y+h].</summary>
        private void Text(SKCanvas cv, string text, float x, float y, float w, float h,
            UiStyle s, UiScreen screen, float ss, UiColor? overrideColor = null,
            UiTextAlign? overrideAlign = null, float? extraPad = null)
        {
            if (string.IsNullOrEmpty(text)) return;
            SKColor col = C(overrideColor ?? s.TextColor ?? screen.Theme.OnSurface);
            if (col.Alpha == 0) return;
            using var paint = MakeTextPaint(s, screen, ss, col, h);
            UiTextAlign align = overrideAlign ?? s.TextAlign ?? screen.Theme.TextAlign;
            paint.TextAlign = ToSkAlign(align);
            float pad = (extraPad ?? (s.Padding ?? 0f)) * ss;
            float avail = w - pad * 2f;
            // fit by shrinking first, ellipsizing only as a last resort
            if (avail > 1f && paint.MeasureText(text) > avail)
            {
                float k = Math.Max(0.60f, avail / Math.Max(1f, paint.MeasureText(text)));
                paint.TextSize = Math.Max(6f, paint.TextSize * k);
            }
            string shown = FitText(paint, text, avail);
            if (shown.Length == 0) return;
            paint.GetFontMetrics(out SKFontMetrics m);
            float baseline = y + h / 2f - (m.Ascent + m.Descent) / 2f;
            cv.DrawText(shown, AlignX(paint.TextAlign, x, w, pad), baseline, paint);
        }

        /// <summary>Draw a workbook-style text+icon row, used by buttons and badges.</summary>
        private void TextWithIcon(SKCanvas cv, UiControl ctrl, float x, float y, float w, float h,
            UiStyle s, UiScreen screen, float ss, SKColor col)
        {
            bool hasText = !string.IsNullOrEmpty(ctrl.Text);
            bool hasIcon = !string.IsNullOrEmpty(ctrl.Icon);
            if (!hasText && !hasIcon) return;

            float pad = (s.Padding ?? 0f) * ss;
            UiTextAlign align = s.TextAlign ?? screen.Theme.TextAlign;

            if (!hasIcon)
            {
                Text(cv, ctrl.Text, x, y, w, h, s, screen, ss, null, align);
                return;
            }

            float iconSize = Math.Min(h * 0.56f, w * 0.5f);
            if (!hasText)
            {
                DrawIcon(cv, ctrl.Icon, x + w / 2f, y + h / 2f, iconSize, col);
                return;
            }

            // icon + text laid out as one centred group
            using var paint = MakeTextPaint(s, screen, ss, col, h);
            float gap = iconSize * 0.34f;
            float avail = w - pad * 2f;
            // shrink the type until icon + gap + text fit, so nothing gets ellipsized
            for (int guard = 0; guard < 12; guard++)
            {
                float need = iconSize + gap + paint.MeasureText(ctrl.Text);
                if (need <= avail || paint.TextSize <= 6f) break;
                float k = Math.Max(0.72f, avail / need);
                paint.TextSize = Math.Max(6f, paint.TextSize * k);
                iconSize = Math.Min(iconSize, paint.TextSize * 1.05f);
                gap = iconSize * 0.34f;
            }
            float tw = paint.MeasureText(ctrl.Text);
            float group = Math.Min(iconSize + gap + tw, avail);
            float startX = align == UiTextAlign.Left ? x + pad
                         : align == UiTextAlign.Right ? x + w - pad - group
                         : x + w / 2f - group / 2f;
            DrawIcon(cv, ctrl.Icon, startX + iconSize / 2f, y + h / 2f, iconSize, col);
            paint.TextAlign = SKTextAlign.Left;
            paint.GetFontMetrics(out SKFontMetrics m);
            float baseline = y + h / 2f - (m.Ascent + m.Descent) / 2f;
            float textX = startX + iconSize + gap;
            cv.DrawText(ctrl.Text, textX, baseline, paint);
        }

        // ------------------------------------------------------------------ icons

        /// <summary>Draws a built-in vector icon. Implementation lives in <see cref="IconLibrary"/>.</summary>
        private static void DrawIcon(SKCanvas cv, string name, float cx, float cy, float size, SKColor col)
            => IconLibrary.Draw(cv, name, cx, cy, size, col);

        // ------------------------------------------------------------------ controls

        private void DrawControl(SKCanvas cv, UiControl ctrl, UiScreen screen, UiState state, float ss,
            bool bake = false)
        {
            UiStyle s = ctrl.ResolveStyle(state, screen.Theme);
            float x = ctrl.Left(screen.Width) * ss;
            float y = ctrl.Top(screen.Height) * ss;
            float w = ctrl.W * ss;
            float h = ctrl.H * ss;
            float r = (s.CornerRadius ?? 0) * ss;
            SKColor txt = C(s.TextColor ?? screen.Theme.OnSurface);

            // On/off geometry. When baking sprites the pair is split across states
            // (Normal = off, Checked = on) so one control yields both variants;
            // the composite preview instead shows the authored value.
            bool on, sel;
            if (bake)
            {
                on = state == UiState.Checked || (state != UiState.Normal && ctrl.Checked);
                sel = state == UiState.Selected || (state != UiState.Normal && ctrl.Selected);
            }
            else
            {
                on = ctrl.Checked;
                sel = ctrl.Selected;
            }

            switch (ctrl.Type)
            {
                case UiControlType.Panel:
                case UiControlType.Card:
                    Surface(cv, x, y, w, h, r, s, ss);
                    // cards may carry a caption
                    if (!string.IsNullOrEmpty(ctrl.Text))
                        Text(cv, ctrl.Text, x, y, w, h, s, screen, ss);
                    break;

                case UiControlType.Button:
                    Surface(cv, x, y, w, h, r, s, ss);
                    TextWithIcon(cv, ctrl, x, y, w, h, s, screen, ss, txt);
                    break;

                case UiControlType.Badge:
                    Surface(cv, x, y, w, h, Math.Min(h / 2f, w / 2f), s, ss);
                    {
                        // badges are compact: font auto-clamps to the pill height
                        UiStyle bs = s.Clone();
                        float defSize = Math.Min(h * 0.52f / Math.Max(0.01f, ss), s.FontSize ?? 18f);
                        bs.FontSize = defSize;
                        bs.Padding = s.Padding ?? Math.Max(4f, h * 0.20f / Math.Max(0.01f, ss));
                        TextWithIcon(cv, ctrl, x, y, w, h, bs, screen, ss, txt);
                    }
                    break;

                case UiControlType.Tab:
                    {
                        bool selected = sel;
                        if (selected || (s.Background.HasValue && s.Background.Value.A > 0.01f))
                            Surface(cv, x, y, w, h, r, s, ss, false);
                        TextWithIcon(cv, ctrl, x, y, w, h, s, screen, ss, txt);
                        if (selected)
                        {
                            float barH = Math.Max(2f, h * 0.07f) * (ss > 1 ? 1f : 1f);
                            float barW = Math.Min(w * 0.62f, w - 2f * ((s.Padding ?? 8f) * ss));
                            SKColor accent = C(s.FillColor ?? screen.Theme.Primary);
                            using var bar = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = accent };
                            cv.DrawRoundRect(MakeRR(x + (w - barW) / 2f, y + h - barH * 1.2f, barW, barH, barH / 2f), bar);
                        }
                    }
                    break;

                case UiControlType.Label:
                    TextWithIcon(cv, ctrl, x, y, w, h, s, screen, ss, txt);
                    break;

                case UiControlType.Icon:
                    DrawIcon(cv, string.IsNullOrEmpty(ctrl.Icon) ? ctrl.Text : ctrl.Icon,
                        x + w / 2f, y + h / 2f, Math.Min(w, h) * 0.72f, txt);
                    break;

                case UiControlType.Input:
                    {
                        Surface(cv, x, y, w, h, r, s, ss, false);
                        // placeholder text is dimmed and left aligned
                        float padIn = s.Padding ?? Math.Max(10f, h * 0.30f / Math.Max(0.01f, ss));
                        Text(cv, ctrl.Text, x, y, w, h, s, screen, ss,
                            (s.TextColor ?? screen.Theme.OnSurface).WithAlpha(0.55f),
                            UiTextAlign.Left, padIn);
                        // caret hint
                        using var caret = new SKPaint
                        {
                            IsAntialias = true,
                            Style = SKPaintStyle.Fill,
                            Color = Alpha(C(screen.Theme.Primary), 0.9f)
                        };
                        float cw = Math.Max(1.5f, 1.5f * ss);
                        float cpH = h * 0.46f;
                        float cpX = x + w - padIn * ss - cw;
                        cv.DrawRoundRect(MakeRR(cpX, y + (h - cpH) / 2f, cw, cpH, cw / 2f), caret);
                    }
                    break;

                case UiControlType.Dropdown:
                    {
                        Surface(cv, x, y, w, h, r, s, ss);
                        float padIn = s.Padding ?? Math.Max(10f, h * 0.34f / Math.Max(0.01f, ss));
                        Text(cv, ctrl.Text, x, y, w - h * 0.6f, h, s, screen, ss,
                            s.TextColor ?? screen.Theme.OnSurface, UiTextAlign.Left, padIn);
                        DrawIcon(cv, "chevron-down", x + w - h * 0.46f, y + h / 2f,
                            Math.Min(h * 0.42f, w * 0.2f), txt);
                    }
                    break;

                case UiControlType.Slider:
                    {
                        float v = Clamp01(ctrl.Value);
                        float trackH = Math.Max(3f * ss, h * 0.26f);
                        float ty = y + (h - trackH) / 2f;
                        float tr = trackH / 2f;
                        // the handle must never leave the frame: inset the track by its
                        // radius on both ends and travel the handle inside that span
                        float hr = Math.Min(h * 0.42f, w * 0.30f);
                        float travel = Math.Max(0f, w - hr * 2f);
                        SKColor trackBase = C(s.TrackColor ?? screen.Theme.Border);
                        SKColor trackCol = Alpha(trackBase, Math.Max(0.18f, (s.TrackColor ?? screen.Theme.Border).A));
                        FillRoundRectColored(cv, x + hr, ty, travel, trackH, tr, trackCol);

                        float fillW = travel * v;
                        SKColor fillA = C(s.FillColor ?? screen.Theme.Primary);
                        SKColor fillB = C(s.FillColorEnd ?? screen.Theme.PrimaryEnd);
                        if (fillW > 0.5f)
                            FillRoundRectGradient(cv, x + hr, ty, fillW, trackH, tr, fillA, fillB);

                        float hx = x + hr + fillW;
                        // thumb shadow then body
                        FillCircle(cv, hx, y + h / 2f + 1.5f * ss, hr, new SKColor(0, 0, 0, 70));
                        FillCircle(cv, hx, y + h / 2f, hr, new SKColor(255, 255, 255, 255));
                        FillCircle(cv, hx, y + h / 2f, hr * 0.52f, fillA);
                    }
                    break;

                case UiControlType.ProgressBar:
                    {
                        float v = Clamp01(ctrl.Value);
                        float rad = Math.Min(h / 2f, r > 0 ? r : h / 2f);
                        // groove is always solid; only the fill may carry a gradient
                        SKColor groove = C(s.TrackColor ?? s.Background ?? screen.Theme.Surface);
                        FillRoundRectColored(cv, x, y, w, h, rad, groove);
                        if (v > 0.0005f)
                        {
                            float fw = Math.Max(h * 0.9f, w * v);
                            if (fw > w) fw = w;
                            FillRoundRectGradient(cv, x, y, fw, h, rad,
                                C(s.FillColor ?? screen.Theme.Primary),
                                C(s.FillColorEnd ?? screen.Theme.PrimaryEnd));
                        }
                        if (s.BorderColor.HasValue && (s.BorderWidth ?? 0) > 0)
                            StrokeRR(cv, x, y, w, h, rad, (s.BorderWidth ?? 0) * ss, C(s.BorderColor.Value));
                    }
                    break;

                case UiControlType.Toggle:
                    {
                        // uses the state-aware `on` computed above
                        float rad = h / 2f;
                        // the "off" track must stay legible on a transparent sprite:
                        // theme.Border is a hairline alpha and would vanish, so fall
                        // back to a mid-grey solid when no track_color is authored
                        SKColor offTrack = s.TrackColor.HasValue
                            ? C(s.TrackColor.Value)
                            : new SKColor(0x5A, 0x60, 0x6B, 0xFF);
                        SKColor trackA = on ? C(s.FillColor ?? screen.Theme.Primary) : offTrack;
                        SKColor trackB = on ? C(s.FillColorEnd ?? screen.Theme.PrimaryEnd) : offTrack;
                        FillRoundRectGradient(cv, x, y, w, h, rad, trackA, trackB);
                        float knob = h * 0.80f;
                        float kx = on ? x + w - rad : x + rad;
                        FillCircle(cv, kx, y + h / 2f + 1f * ss, knob / 2f, new SKColor(0, 0, 0, 60));
                        FillCircle(cv, kx, y + h / 2f, knob / 2f, SKColors.White);
                    }
                    break;

                case UiControlType.Checkbox:
                    {
                        float box = Math.Min(w, h) * 0.82f;
                        float bx = x + (w - box) / 2f, by = y + (h - box) / 2f;
                        float br = Math.Max(2f * ss, box * 0.22f);
                        // state-aware `on`
                        UiStyle cs = s.Clone();
                        cs.Background = on ? (s.FillColor ?? screen.Theme.Primary) : (s.Background ?? screen.Theme.Surface);
                        cs.BackgroundEnd = on ? (s.FillColorEnd ?? screen.Theme.PrimaryEnd) : cs.Background;
                        cs.CornerRadius = br / ss;
                        Surface(cv, bx, by, box, box, br, cs, ss);
                        if (on)
                            DrawIcon(cv, "check", bx + box / 2f, by + box / 2f, box * 0.74f, SKColors.White);
                    }
                    break;

                case UiControlType.Radio:
                    {
                        float dia = Math.Min(w, h) * 0.84f;
                        float cx = x + w / 2f, cy = y + h / 2f;
                        // state-aware `on`
                        float bw = Math.Max(1.5f, (s.BorderWidth ?? 2f) * ss);
                        FillCircle(cv, cx, cy, dia / 2f,
                            on ? C(s.FillColor ?? screen.Theme.Primary) : C(s.Background ?? screen.Theme.Surface));
                        using (var stroke = new SKPaint
                        {
                            IsAntialias = true, Style = SKPaintStyle.Stroke,
                            Color = on ? C(s.FillColor ?? screen.Theme.Primary) : C(s.BorderColor ?? screen.Theme.Border),
                            StrokeWidth = bw
                        })
                        {
                            cv.DrawCircle(cx, cy, dia / 2f - bw / 2f, stroke);
                        }
                        if (on) FillCircle(cv, cx, cy, dia * 0.24f, SKColors.White);
                    }
                    break;

                case UiControlType.Divider:
                    {
                        SKColor col = C(s.Background ?? s.BorderColor ?? screen.Theme.Border);
                        float thick = Math.Max(1f, (s.BorderWidth ?? 1f) * ss);
                        using var paint = new SKPaint { IsAntialias = true, Color = col, Style = SKPaintStyle.Fill };
                        if (w >= h) cv.DrawRect(new SKRect(x, y + h / 2f - thick / 2f, x + w, y + h / 2f + thick / 2f), paint);
                        else cv.DrawRect(new SKRect(x + w / 2f - thick / 2f, y, x + w / 2f + thick / 2f, y + h), paint);
                    }
                    break;

                case UiControlType.Scrollbar:
                    {
                        float rad = Math.Min(w, h) / 2f;
                        FillRoundRectColored(cv, x, y, w, h, rad,
                            Alpha(C(s.TrackColor ?? screen.Theme.Border), 0.6f));
                        float thumbH = Math.Max(h * 0.24f, h * 0.35f);
                        float ty = y + (h - thumbH) * Clamp01(ctrl.Value);
                        SKColor thumb = C(s.FillColor ?? screen.Theme.OnSurface);
                        FillRoundRectColored(cv, x, ty, w, thumbH, rad, Alpha(thumb, 0.75f));
                    }
                    break;
            }
        }

        private static void FillRoundRectColored(SKCanvas cv, float x, float y, float w, float h,
            float r, SKColor col)
        {
            if (w <= 0 || h <= 0 || col.Alpha == 0) return;
            using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = col };
            cv.DrawRoundRect(MakeRR(x, y, w, h, r), paint);
        }

        private static void FillRoundRectGradient(SKCanvas cv, float x, float y, float w, float h,
            float r, SKColor a, SKColor b)
        {
            if (w <= 0 || h <= 0) return;
            using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
            if (a == b) { paint.Color = a; }
            else
            {
                using var shader = SKShader.CreateLinearGradient(
                    new SKPoint(x, y), new SKPoint(x, y + h),
                    new[] { a, b }, new float[] { 0f, 1f }, SKShaderTileMode.Clamp);
                paint.Shader = shader;
            }
            cv.DrawRoundRect(MakeRR(x, y, w, h, r), paint);
        }

        private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);

        // ------------------------------------------------------------------ states

        private static UiState[] DefaultStates(UiControl ctrl)
        {
            var list = new List<UiState> { UiState.Normal };
            bool interactive = ctrl.Type == UiControlType.Button || ctrl.Type == UiControlType.Toggle ||
                ctrl.Type == UiControlType.Checkbox || ctrl.Type == UiControlType.Radio ||
                ctrl.Type == UiControlType.Tab || ctrl.Type == UiControlType.Slider ||
                ctrl.Type == UiControlType.Dropdown || ctrl.Type == UiControlType.Input;
            if (interactive) { list.Add(UiState.Hover); list.Add(UiState.Pressed); list.Add(UiState.Disabled); }

            // a control that is already in its "on" state needs that sprite too,
            // otherwise the consumer has no art for the state it starts in
            if (ctrl.Selected && !list.Contains(UiState.Selected)) list.Add(UiState.Selected);
            if (ctrl.Checked && !list.Contains(UiState.Checked)) list.Add(UiState.Checked);

            if (ctrl.StateStyles != null)
                foreach (var k in ctrl.StateStyles.Keys)
                    if (!list.Contains(k)) list.Add(k);
            return list.ToArray();
        }

        private UiControlImage RenderOne(UiControl ctrl, UiScreen screen, UiState state)
        {
            float ss = screen.Supersample <= 0 ? 1f : screen.Supersample;
            UiStyle s = ctrl.ResolveStyle(state, screen.Theme);

            // margin must fit the shadow ellipse and the border stroke
            float shadowExtent = s.ShadowColor.HasValue
                ? (s.ShadowBlur ?? 0) * 0.75f + Math.Abs(s.ShadowX ?? 0f) + Math.Abs(s.ShadowY ?? 0f)
                : 0f;
            float margin = Math.Max(shadowExtent, (s.BorderWidth ?? 0) * 0.5f) + 2f;
            int mImg = (int)Math.Ceiling(margin * ss);
            int frameW = (int)Math.Round(ctrl.W * ss);
            int frameH = (int)Math.Round(ctrl.H * ss);
            if (frameW < 1) frameW = 1;
            if (frameH < 1) frameH = 1;
            int imgW = frameW + 2 * mImg;
            int imgH = frameH + 2 * mImg;

            var info = new SKImageInfo(imgW, imgH, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info);
            var cv = surface.Canvas;
            cv.Clear(SKColors.Transparent);

            float ox = ctrl.Left(screen.Width) * ss;
            float oy = ctrl.Top(screen.Height) * ss;
            cv.Save();
            cv.Translate(mImg - ox, mImg - oy);
            DrawControl(cv, ctrl, screen, state, ss, bake: true);
            cv.Restore();

            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            byte[] bytes = data.ToArray();

            // 9-slice: corner radius + stroke, capped at a third of each axis
            int border = (int)Math.Round((s.CornerRadius ?? 0) * ss) + (int)Math.Round((s.BorderWidth ?? 0) * ss) + 1;
            int nineL = Math.Min(border, frameW / 3);
            int nineT = Math.Min(border, frameH / 3);

            return new UiControlImage
            {
                Key = ctrl.Id + "_" + state.ToString().ToLowerInvariant(),
                State = state.ToString().ToLowerInvariant(),
                ControlId = ctrl.Id,
                Png = bytes,
                ImageW = imgW, ImageH = imgH,
                FrameX = mImg, FrameY = mImg, FrameW = frameW, FrameH = frameH,
                NineL = nineL, NineT = nineT, NineR = nineL, NineB = nineT
            };        }

        /// <summary>Render every control (in its default state set) to a list of images.</summary>
        public List<UiControlImage> RenderControls(UiScreen screen)
        {
            var result = new List<UiControlImage>();
            foreach (var ctrl in screen.Controls)
                foreach (var st in DefaultStates(ctrl))
                    result.Add(RenderOne(ctrl, screen, st));
            return result;
        }

        /// <summary>Render a single control image for a specific state.</summary>
        public UiControlImage RenderControl(UiControl ctrl, UiScreen screen, UiState state)
            => RenderOne(ctrl, screen, state);

        /// <summary>Render the full screen as one composite preview image.</summary>
        public byte[] RenderScreenPreview(UiScreen screen)
        {
            int w = Math.Max(1, screen.Width), h = Math.Max(1, screen.Height);
            var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info);
            var cv = surface.Canvas;
            cv.Clear(SKColors.Transparent);

            if (screen.BackgroundGradient || screen.Background.A > 0.001f)
            {
                using var paint = new SKPaint { IsAntialias = false };
                if (screen.BackgroundGradient)
                {
                    using var shader = SKShader.CreateLinearGradient(
                        new SKPoint(0, 0), new SKPoint(0, h),
                        new[] { C(screen.Background), C(screen.BackgroundEnd) },
                        new float[] { 0f, 1f }, SKShaderTileMode.Clamp);
                    paint.Shader = shader;
                    cv.DrawRect(new SKRect(0, 0, w, h), paint);
                }
                else
                {
                    paint.Color = C(screen.Background);
                    cv.DrawRect(new SKRect(0, 0, w, h), paint);
                }
            }

            foreach (var ctrl in screen.Controls)
                DrawControl(cv, ctrl, screen, UiState.Normal, 1f);

            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }
    }
}
