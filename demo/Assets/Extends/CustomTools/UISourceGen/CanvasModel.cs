using System;
using System.Collections.Generic;

namespace UniMcp.UiGen
{
    /// <summary>
    /// The shape kinds the generic canvas can draw. This is a superset of the UI
    /// control vocabulary: everything SkiaSharp exposes as a primitive becomes a
    /// declarative element so MCP clients never need to write C#.
    /// </summary>
    public enum CanvasShape
    {
        Rect,
        RoundRect,
        Circle,
        Oval,
        Line,
        Arc,
        Path,          // svg path data or a command list
        Polygon,
        Polyline,
        Points,
        Text,
        TextOnPath,
        Icon,          // one of the built-in vector icons
        Image,         // project / absolute image path
        ImageNinePatch,
        Vertices,      // Gouraud-ish gradient mesh
        Group          // container: children inherit transform / clip / paint
    }

    /// <summary>Paint fill style.</summary>
    public enum CanvasFillKind
    {
        None,
        Solid,
        Linear,
        Radial,
        Sweep,
        TwoPointConical,
        Noise,
        Image
    }

    /// <summary>How a repeating shader behaves past its bounds.</summary>
    public enum CanvasTileMode { Clamp, Repeat, Mirror, Decal }

    /// <summary>Stroke cap / join.</summary>
    public enum CanvasCap { Butt, Round, Square }
    public enum CanvasJoin { Miter, Round, Bevel }

    /// <summary>Blend mode names mirrored from SkiaSharp.SKBlendMode.</summary>
    public enum CanvasBlend
    {
        Clear, Src, Dst, SrcOver, DstOver, SrcIn, DstIn, SrcOut, DstOut,
        SrcATop, DstATop, Xor, Plus, Modulate, Screen, Overlay, Darken, Lighten,
        ColorDodge, ColorBurn, HardLight, SoftLight, Difference, Exclusion,
        Multiply, Hue, Saturation, Color, Luminosity
    }

    /// <summary>Blur styles mirrored from SkiaSharp.SKBlurStyle.</summary>
    public enum CanvasBlurStyle { Normal, Solid, Outer, Inner }

    /// <summary>Path boolean operations.</summary>
    public enum CanvasPathOp { Difference, Intersect, Union, Xor, ReverseDifference }

    /// <summary>Dash / path effect kinds.</summary>
    public enum CanvasPathEffect
    {
        None,
        Dash,
        Corner,
        Discrete,
        Trim,
        Compose
    }

    /// <summary>Image filter kinds.</summary>
    public enum CanvasImageFilter
    {
        None,
        Blur,
        DropShadow,
        Offset,
        Dilate,
        Erode
    }

    // --------------------------------------------------------------------- paint

    /// <summary>
    /// A declarative paint. Every field is optional; the renderer fills in
    /// sensible defaults so a bare {"fill":"#f00"} already draws.
    /// </summary>
    public sealed class CanvasPaint
    {
        public CanvasFillKind FillKind = CanvasFillKind.Solid;

        /// <summary>Solid colour, or the first gradient stop.</summary>
        public UiColor Color = UiColor.White;
        /// <summary>Last gradient stop. Null means "single colour".</summary>
        public UiColor? Color2;

        /// <summary>Extra gradient stops (beyond the two main ones), ordered.</summary>
        public List<CanvasStop> Stops;

        // gradient geometry — interpreted per FillKind
        public float X0, Y0, X1, Y1;
        public float Radius0, Radius1 = 100f;
        public float StartAngle, EndAngle = 360f;

        /// <summary>Noise shader parameters.</summary>
        public float NoiseBaseFreq = 0.02f, NoiseNumOctaves = 3f, NoiseSeed = 0f;
        public float NoiseStitch = 0f;
        public bool NoiseTurbulence;

        // strokes
        public bool Stroke;
        public float StrokeWidth = 1f;
        /// <summary>Outline colour inside a single paint; falls back to <see cref="Color"/>.</summary>
        public UiColor? StrokeColor;
        public CanvasCap Cap = CanvasCap.Round;
        public CanvasJoin Join = CanvasJoin.Round;
        public float Miter = 4f;

        // effects
        public CanvasBlend Blend = CanvasBlend.SrcOver;
        public bool AntiAlias = true;
        public bool Dither;

        /// <summary>Mask blur radius (0 disables).</summary>
        public float BlurRadius;
        public CanvasBlurStyle BlurStyle = CanvasBlurStyle.Normal;

        /// <summary>Image filter (applies to the whole draw op).</summary>
        public CanvasImageFilter Filter = CanvasImageFilter.None;
        public float FilterSigmaX = 0f, FilterSigmaY = 0f;
        public float FilterDx = 0f, FilterDy = 0f;
        public float FilterRadius = 0f;
        public UiColor FilterColor = new UiColor(0, 0, 0, 0.45f);

        /// <summary>Path effect (dash etc.).</summary>
        public CanvasPathEffect PathEffect = CanvasPathEffect.None;
        public float[] Dashes;
        public float DashPhase;
        public float TrimStart, TrimEnd = 1f;
        public float CornerRadius;
        public float DiscreteLength = 4f, DiscreteDeviation = 2f;

        /// <summary>Image shader source (project- or absolute path).</summary>
        public string ImagePath;
        public CanvasTileMode TileX = CanvasTileMode.Repeat;
        public CanvasTileMode TileY = CanvasTileMode.Repeat;

        /// <summary>Optional local matrix applied to the shader, 6 floats.</summary>
        public float[] ShaderMatrix;

        /// <summary>
        /// Gradient anchor space. false (default) anchors are relative to the
        /// shape's own box, which keeps a gradient attached when geometry moves.
        /// true treats them as canvas coordinates, which is what a clip masks
        /// usually want.
        /// </summary>
        public bool ShaderSpaceCanvas;

        public bool IsVisible => FillKind != CanvasFillKind.None || Stroke;

        public CanvasPaint Clone()
        {
            var c = (CanvasPaint)MemberwiseClone();
            c.Stops = Stops == null ? null : new List<CanvasStop>(Stops);
            c.Dashes = Dashes == null ? null : (float[])Dashes.Clone();
            c.ShaderMatrix = ShaderMatrix == null ? null : (float[])ShaderMatrix.Clone();
            return c;
        }

        public static CanvasPaint StrokeOnly(float width, UiColor color)
        {
            return new CanvasPaint
            {
                FillKind = CanvasFillKind.None,
                Stroke = true,
                StrokeWidth = width,
                Color = color
            };
        }
    }

    /// <summary>One explicit gradient stop.</summary>
    public sealed class CanvasStop
    {
        public float Offset;
        public UiColor Color;
    }

    // -------------------------------------------------------------- element base

    /// <summary>
    /// One drawable element. Elements are recursive through <see cref="Children"/>
    /// so a group can carry transform / clip / paint for its subtree.
    /// </summary>
    public sealed class CanvasElement
    {
        public string Id;
        public CanvasShape Shape;

        // Common geometry. Meaning depends on Shape:
        //   Rect/RoundRect/Image/Text : X,Y,W,H is the box
        //   Circle/Oval/Arc           : X,Y is centre, W,H is radii*2
        //   Line                      : X,Y -> X1,Y1
        //   Path/Polygon/Polyline     : first point is X,Y
        public float X, Y, W, H;
        public float X1, Y1;
        public float Radius;                 // RoundRect corner radius

        /// <summary>Point list for path commands / polygons / points.</summary>
        public List<CanvasPoint> Points;

        /// <summary>SVG path data (overrides Points when present).</summary>
        public string PathData;

        /// <summary>Arc sweep, degrees, clockwise.</summary>
        public float Sweep = 360f, StartAngle = 0f;

        /// <summary>Path state.</summary>
        public bool ClosePath = true;
        public bool EvenOdd;
        public CanvasPathOp CombineOp = CanvasPathOp.Union;
        public CanvasElement CombineWith;

        // text
        public string Text;
        public string FontPath;
        public string FontFamily;
        public int FontIndex;
        public float FontSize = 24f;
        public bool Bold, Italic;
        public UiTextAlign TextAlign = UiTextAlign.Left;
        public float LetterSpacing;
        public float TextScaleX = 1f;
        public float TextSkewX;

        // image
        public string ImagePath;
        public int[] NinePatch;              // [l,t,r,b] in source pixels
        public bool NearestNeighbour;

        // icon
        public string Icon;

        // vertices / gradient mesh
        public List<CanvasPoint> Vertices;
        public List<UiColor> VertexColors;
        public List<int> Indices;

        // paint (may be inherited from a parent group when null)
        public CanvasPaint Fill;
        public CanvasPaint Stroke;

        // transform, applied in this order: translate -> rotate -> scale -> skew -> matrix
        public float Tx, Ty;
        public float RotateDeg;
        public float ScaleX = 1f, ScaleY = 1f;
        public float SkewX, SkewY;
        public float[] Matrix;               // 9 values, overrides the above when set
        public float PivotX, PivotY;         // rotation/scale origin, default element centre

        // clipping
        public bool Clip;
        public CanvasShape ClipShape = CanvasShape.Rect;
        public float ClipRadius;
        public bool ClipEvenOdd;
        // Clip geometry is kept separate so a clip on a real element never
        // overwrites the element's own box.
        public float ClipX, ClipY, ClipW, ClipH;
        public bool ClipHasRect;

        /// <summary>Composite the element (and children) through a saveLayer.</summary>
        public bool Layer;
        public float LayerOpacity = 1f;
        public CanvasBlend LayerBlend = CanvasBlend.SrcOver;

        public float Opacity = 1f;

        public List<CanvasElement> Children;

        public bool HasGeometry()
        {
            return Shape == CanvasShape.Line || Shape == CanvasShape.Circle
                || Shape == CanvasShape.Oval || Shape == CanvasShape.Arc
                || Shape == CanvasShape.Path || Shape == CanvasShape.Polygon
                || Shape == CanvasShape.Polyline || Shape == CanvasShape.Points
                || Shape == CanvasShape.Vertices
                || W != 0f || H != 0f;
        }

        /// <summary>Element centre used as the default transform pivot.</summary>
        public float CenterX => Points != null && Points.Count > 0 ? Points[0].X + W * 0.5f : X + W * 0.5f;
        public float CenterY => Points != null && Points.Count > 0 ? Points[0].Y + H * 0.5f : Y + H * 0.5f;
    }

    /// <summary>A point with optional per-vertex colours for gradient meshes.</summary>
    public struct CanvasPoint
    {
        public float X, Y;
        public CanvasPoint(float x, float y) { X = x; Y = y; }
    }

    // ------------------------------------------------------------------ document

    /// <summary>A standalone 2D canvas document, independent of any UI screen.</summary>
    public sealed class CanvasDoc
    {
        public string Name = "Canvas";
        public int Width = 512;
        public int Height = 512;
        public float Supersample = 1f;

        /// <summary>Background paint. Null or transparent leaves the canvas clear.</summary>
        public CanvasPaint Background;
        public bool ClearFirst = true;

        public List<CanvasElement> Elements = new List<CanvasElement>();

        /// <summary>Optional output metadata echoed back into layout JSON.</summary>
        public string Note;
    }

    /// <summary>Result of parsing a canvas document.</summary>
    public sealed class CanvasParseResult
    {
        public CanvasDoc Doc;
        public readonly List<string> Errors = new List<string>();
        public readonly List<string> Warnings = new List<string>();
        public bool Ok => Doc != null && Errors.Count == 0;
    }
}
