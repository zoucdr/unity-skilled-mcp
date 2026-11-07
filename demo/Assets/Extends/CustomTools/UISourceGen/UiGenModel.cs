using System;
using System.Collections.Generic;

namespace UniMcp.UiGen
{
    /// <summary>Control kinds UISourceGen can draw.</summary>
    public enum UiControlType
    {
        Button,
        Panel,
        Card,
        Slider,
        Toggle,
        ProgressBar,
        Label,
        Input,
        Checkbox,
        Radio,
        Icon,
        Divider,
        Badge,
        Tab,
        Dropdown,
        Scrollbar
    }

    /// <summary>Visual state of a control; each can map to its own image.</summary>
    public enum UiState
    {
        Normal,
        Hover,
        Pressed,
        Disabled,
        Selected,
        Checked
    }

    /// <summary>How a background gradient flows.</summary>
    public enum UiGradientDir
    {
        Vertical,
        Horizontal,
        Diagonal
    }

    /// <summary>Font weight (maps to SKFontStyle).</summary>
    public enum UiFontWeight
    {
        Regular,
        Medium,
        Bold
    }

    /// <summary>Horizontal text alignment.</summary>
    public enum UiTextAlign
    {
        Left,
        Center,
        Right
    }

    /// <summary>Anchor of a control inside the screen canvas.</summary>
    public enum UiAnchor
    {
        TopLeft,
        TopCenter,
        TopRight,
        MiddleLeft,
        Center,
        MiddleRight,
        BottomLeft,
        BottomCenter,
        BottomRight
    }

    /// <summary>Colour with 0..1 components; mirrors Unity/texgen convention.</summary>
    public struct UiColor
    {
        public float R, G, B, A;

        public UiColor(float r, float g, float b, float a = 1f)
        {
            R = Clamp01(r); G = Clamp01(g); B = Clamp01(b); A = Clamp01(a);
        }

        public static readonly UiColor Transparent = new UiColor(0, 0, 0, 0);
        public static readonly UiColor White = new UiColor(1, 1, 1, 1);
        public static readonly UiColor Black = new UiColor(0, 0, 0, 1);

        private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);

        /// <summary>Parse "#RGB/#RGBA/#RRGGBB/#RRGGBBAA", "r,g,b[,a]" or a JSON array.</summary>
        public static bool TryParse(string raw, out UiColor color)
        {
            color = White;
            if (string.IsNullOrWhiteSpace(raw)) return false;
            string s = raw.Trim();
            if (s.StartsWith("#"))
            {
                string hex = s.Substring(1);
                if (hex.Length == 3 || hex.Length == 4)
                    hex = string.Concat(hex[0], hex[0], hex[1], hex[1], hex[2], hex[2],
                        hex.Length == 4 ? hex[3].ToString() + hex[3] : "ff");
                else if (hex.Length == 6) hex += "ff";
                if (hex.Length == 8 &&
                    uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber,
                        System.Globalization.CultureInfo.InvariantCulture, out uint v))
                {
                    color = new UiColor(
                        ((v >> 24) & 0xFF) / 255f, ((v >> 16) & 0xFF) / 255f,
                        ((v >> 8) & 0xFF) / 255f, (v & 0xFF) / 255f);
                    return true;
                }
                return false;
            }
            var csv = s.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (csv.Length >= 3 &&
                float.TryParse(csv[0], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float r) &&
                float.TryParse(csv[1], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float g) &&
                float.TryParse(csv[2], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float b))
            {
                float denom = (r > 1.0001f || g > 1.0001f || b > 1.0001f) ? 255f : 1f;
                float a = 1f;
                if (csv.Length >= 4 && float.TryParse(csv[3], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float av))
                    a = av > 1.0001f ? av / 255f : av;
                color = new UiColor(r / denom, g / denom, b / denom, Clamp01(a));
                return true;
            }
            return false;
        }

        /// <summary>Parse an already-read array of 3-4 floats (0..1 or 0..255).</summary>
        public static UiColor FromFloats(IList<float> v)
        {
            if (v == null || v.Count < 3) return White;
            float denom = 1f;
            for (int i = 0; i < Math.Min(v.Count, 4); i++)
                if (v[i] > 1.0001f) { denom = 255f; break; }
            float a = v.Count >= 4 ? v[3] / denom : 1f;
            return new UiColor(v[0] / denom, v[1] / denom, v[2] / denom, a);
        }

        public UiColor Multiply(float k) => new UiColor(R * k, G * k, B * k, A);
        public UiColor WithAlpha(float a) => new UiColor(R, G, B, a);
    }

    /// <summary>Visual style for a control or a state override.</summary>
    public sealed class UiStyle
    {
        public UiColor? Background;
        public UiColor? BackgroundEnd;            // when set, background becomes a gradient
        public UiGradientDir GradientDir = UiGradientDir.Vertical;
        public UiColor? BorderColor;
        public float? BorderWidth;
        public float? CornerRadius;
        public UiColor? ShadowColor;
        public float? ShadowBlur;
        public float? ShadowX;
        public float? ShadowY;
        public UiColor? TextColor;
        public float? FontSize;
        public string FontPath;                   // null -> inherit from theme/renderer default
        public UiFontWeight? FontWeight;
        public UiTextAlign? TextAlign;
        public float? Padding;                    // inner padding for text/icon, px
        public UiColor? TrackColor;               // slider / progress / scrollbar groove
        public UiColor? FillColor;                // slider fill / progress fill / thumb
        public UiColor? FillColorEnd;             // gradient end for the fill

        /// <summary>Return a copy with every non-null field of <paramref name="o"/> applied.</summary>
        public UiStyle Merge(UiStyle o)
        {
            if (o == null) return Clone();
            var r = Clone();
            if (o.Background.HasValue) r.Background = o.Background;
            if (o.BackgroundEnd.HasValue) r.BackgroundEnd = o.BackgroundEnd;
            if (o.GradientDir != GradientDir) r.GradientDir = o.GradientDir;
            if (o.BorderColor.HasValue) r.BorderColor = o.BorderColor;
            if (o.BorderWidth.HasValue) r.BorderWidth = o.BorderWidth;
            if (o.CornerRadius.HasValue) r.CornerRadius = o.CornerRadius;
            if (o.ShadowColor.HasValue) r.ShadowColor = o.ShadowColor;
            if (o.ShadowBlur.HasValue) r.ShadowBlur = o.ShadowBlur;
            if (o.ShadowX.HasValue) r.ShadowX = o.ShadowX;
            if (o.ShadowY.HasValue) r.ShadowY = o.ShadowY;
            if (o.TextColor.HasValue) r.TextColor = o.TextColor;
            if (o.FontSize.HasValue) r.FontSize = o.FontSize;
            if (o.FontPath != null) r.FontPath = o.FontPath;
            if (o.FontWeight.HasValue) r.FontWeight = o.FontWeight;
            if (o.TextAlign.HasValue) r.TextAlign = o.TextAlign;
            if (o.Padding.HasValue) r.Padding = o.Padding;
            if (o.TrackColor.HasValue) r.TrackColor = o.TrackColor;
            if (o.FillColor.HasValue) r.FillColor = o.FillColor;
            if (o.FillColorEnd.HasValue) r.FillColorEnd = o.FillColorEnd;
            return r;
        }

        public UiStyle Clone()
        {
            return new UiStyle
            {
                Background = Background,
                BackgroundEnd = BackgroundEnd,
                GradientDir = GradientDir,
                BorderColor = BorderColor,
                BorderWidth = BorderWidth,
                CornerRadius = CornerRadius,
                ShadowColor = ShadowColor,
                ShadowBlur = ShadowBlur,
                ShadowX = ShadowX,
                ShadowY = ShadowY,
                TextColor = TextColor,
                FontSize = FontSize,
                FontPath = FontPath,
                FontWeight = FontWeight,
                TextAlign = TextAlign,
                Padding = Padding,
                TrackColor = TrackColor,
                FillColor = FillColor,
                FillColorEnd = FillColorEnd
            };
        }
    }

    /// <summary>Shared design tokens applied when a control does not override them.</summary>
    public sealed class UiTheme
    {
        public string Name = "Default";
        public UiColor Primary = new UiColor(0.20f, 0.52f, 0.96f, 1f);
        public UiColor PrimaryEnd = new UiColor(0.12f, 0.40f, 0.86f, 1f);
        public UiColor Surface = new UiColor(0.16f, 0.17f, 0.21f, 1f);
        public UiColor SurfaceEnd = new UiColor(0.11f, 0.12f, 0.15f, 1f);
        public UiColor OnSurface = new UiColor(0.95f, 0.96f, 0.98f, 1f);
        public UiColor Border = new UiColor(1f, 1f, 1f, 0.12f);
        public UiColor Shadow = new UiColor(0f, 0f, 0f, 0.35f);
        public float Radius = 10f;
        public float BorderWidth = 1.5f;
        public float ShadowBlur = 18f;
        public float ShadowY = 6f;
        public float FontSize = 22f;
        public string FontPath;                   // optional default font file path
        public UiFontWeight FontWeight = UiFontWeight.Regular;
        public UiTextAlign TextAlign = UiTextAlign.Center;
    }

    /// <summary>One control on the screen.</summary>
    public sealed class UiControl
    {
        public string Id = "";
        public UiControlType Type = UiControlType.Button;
        public UiAnchor Anchor = UiAnchor.Center;
        public float X, Y, W, H;                  // when anchor == Center this is the centre point
        public UiStyle Style;                      // base style (merged over theme)
        public string Text = "";                   // label / input placeholder
        public string Icon = "";                   // built-in icon name (check/cross/arrow/star/menu/play/gear...)
        public float Value = 0.5f;                 // slider / progress 0..1
        public bool Checked = false;               // checkbox / radio / toggle
        public bool Selected = false;              // tab
        public string[] Items;                     // dropdown / tab list (optional)
        public Dictionary<UiState, UiStyle> StateStyles; // explicit per-state overrides
        public int NineLeft, NineTop, NineRight, NineBottom; // 9-slice borders (px, in image space)

        public UiControl()
        {
            Style = new UiStyle();
            StateStyles = new Dictionary<UiState, UiStyle>();
        }

        /// <summary>Resolve the effective style for a state: theme -> base -> override/derive.</summary>
        public UiStyle ResolveStyle(UiState state, UiTheme theme)
        {
            var s = theme.ToStyle().Merge(Style);
            if (StateStyles != null && StateStyles.TryGetValue(state, out var over))
                return s.Merge(over);
            return s.Merge(DeriveState(state, s));
        }

        private static UiStyle DeriveState(UiState state, UiStyle baseStyle)
        {
            var d = new UiStyle();
            switch (state)
            {
                case UiState.Hover:
                    if (baseStyle.Background.HasValue) d.Background = baseStyle.Background.Value.Multiply(1.08f);
                    if (baseStyle.BackgroundEnd.HasValue) d.BackgroundEnd = baseStyle.BackgroundEnd.Value.Multiply(1.08f);
                    break;
                case UiState.Pressed:
                    if (baseStyle.Background.HasValue) d.Background = baseStyle.Background.Value.Multiply(0.82f);
                    if (baseStyle.BackgroundEnd.HasValue) d.BackgroundEnd = baseStyle.BackgroundEnd.Value.Multiply(0.82f);
                    break;
                case UiState.Disabled:
                    if (baseStyle.Background.HasValue) d.Background = baseStyle.Background.Value.WithAlpha(0.35f);
                    if (baseStyle.BackgroundEnd.HasValue) d.BackgroundEnd = baseStyle.BackgroundEnd.Value.WithAlpha(0.35f);
                    if (baseStyle.TextColor.HasValue) d.TextColor = baseStyle.TextColor.Value.WithAlpha(0.4f);
                    if (baseStyle.BorderColor.HasValue) d.BorderColor = baseStyle.BorderColor.Value.WithAlpha(0.25f);
                    break;
                case UiState.Selected:
                case UiState.Checked:
                    if (baseStyle.Background.HasValue) d.Background = baseStyle.Background.Value.Multiply(1.12f);
                    break;
            }
            return d;
        }

        /// <summary>
        /// Resolve the control's top-left corner for a given screen size.
        /// Anchor semantics:
        ///   *Left   -> X is the gap from the screen's left edge
        ///   *Center -> X is the horizontal centre
        ///   *Right  -> X is the gap from the screen's right edge
        /// Same rule vertically: Top = gap from top, Middle = centre, Bottom = gap from bottom.
        /// </summary>
        public float Left(int screenW)
        {
            switch (Anchor)
            {
                case UiAnchor.Center:
                case UiAnchor.TopCenter:
                case UiAnchor.BottomCenter:
                    return X - W / 2f;
                case UiAnchor.TopRight:
                case UiAnchor.MiddleRight:
                case UiAnchor.BottomRight:
                    return screenW - X - W;
                default:
                    return X;
            }
        }

        public float Top(int screenH)
        {
            switch (Anchor)
            {
                case UiAnchor.MiddleLeft:
                case UiAnchor.Center:
                case UiAnchor.MiddleRight:
                    return Y - H / 2f;
                case UiAnchor.BottomLeft:
                case UiAnchor.BottomCenter:
                case UiAnchor.BottomRight:
                    return screenH - Y - H;
                default:
                    return Y;
            }
        }

        /// <summary>Legacy shorthand: centre-anchored controls treat X/Y as the centre.</summary>
        public float Left() => Anchor == UiAnchor.Center ? X - W / 2f : X;
        public float Top() => Anchor == UiAnchor.Center ? Y - H / 2f : Y;
    }

    /// <summary>A full screen: canvas size, theme, and an ordered control tree.</summary>
    public sealed class UiScreen
    {
        public string Name = "Screen";
        public int Width = 1280;
        public int Height = 720;
        public UiColor Background = new UiColor(0.07f, 0.08f, 0.10f, 1f);
        public UiColor BackgroundEnd = new UiColor(0.03f, 0.04f, 0.06f, 1f);
        public bool BackgroundGradient = true;
        public UiTheme Theme = new UiTheme();
        public readonly List<UiControl> Controls = new List<UiControl>();
        public float Supersample = 2f;            // AA factor for control images
    }

    internal static class UiThemeExtensions
    {
        public static UiStyle ToStyle(this UiTheme t)
        {
            return new UiStyle
            {
                Background = t.Surface,
                BackgroundEnd = t.SurfaceEnd,
                BorderColor = t.Border,
                BorderWidth = t.BorderWidth,
                CornerRadius = t.Radius,
                ShadowColor = t.Shadow,
                ShadowBlur = t.ShadowBlur,
                ShadowY = t.ShadowY,
                TextColor = t.OnSurface,
                FontSize = t.FontSize,
                FontPath = t.FontPath,
                FontWeight = t.FontWeight,
                TextAlign = t.TextAlign
            };
        }
    }
}
