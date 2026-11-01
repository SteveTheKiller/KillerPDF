using System;
using System.Windows;
using System.Windows.Media;

namespace KillerPDF
{
    public enum EditTool { Select, Text, FormField, Highlight, Strikethrough, Underline, Draw, Signature, Image, Crop, Line, Rotate, Shape, Measure, Redact }

    /// <summary>Sub-mode of the Shapes tool (#127 Phase 3): drag a rectangle or ellipse, or click
    /// out a free-form polygon vertex by vertex.</summary>
    public enum ShapeKind { Rectangle, Ellipse, Polygon }

    /// <summary>What the Measure tool measures: a straight distance, the length around a path,
    /// the area inside a polygon, or the angle at a corner.</summary>
    public enum MeasurementKind { Distance, Perimeter, Area, Angle }

    /// <summary>How a HighlightAnnotation paints over its bounds.</summary>
    public enum HighlightStyle { Fill, Strikethrough, Underline }

    public abstract class PageAnnotation
    {
        // History snapshots retain identity; independent copies receive a new identity.
        internal Guid HistoryId { get; set; } = Guid.NewGuid();
        internal virtual void CopyHistoryIdentityFrom(PageAnnotation source) =>
            HistoryId = source.HistoryId;
        public int PageIndex { get; set; }
        // Links a text-edit cover to its replacement text (same non-empty id on both). A cover with a
        // PairId renders dashed (it's "paired"); when the partner text is deleted the cover's PairId is
        // cleared and it renders as a solid box. Empty for everything else.
        public string PairId { get; set; } = "";

        // Groups arbitrary annotations so they select and move together (same non-empty id on every
        // member). Independent of PairId. Empty when the annotation isn't grouped.
        public string GroupId { get; set; } = "";
    }

    /// <summary>
    /// Base class for placed/resizable annotations (signature, image).
    /// Carries the shared position, scale, and source-dimension properties used by the resize handle.
    /// </summary>
    public abstract class PlacedAnnotation : PageAnnotation
    {
        public Point Position { get; set; }
        public double Scale { get; set; } = 0.5;
        public double SourceWidth { get; set; } = 400;
        public double SourceHeight { get; set; } = 150;

        // Runtime-only cache of the decoded image (for image signatures / placed images). Held in
        // memory so a resize-drag doesn't re-decode the Base64 on every mouse tick. Not serialized;
        // the immutable ImageData stays the source of truth.
        public System.Windows.Media.Imaging.BitmapSource? CachedBitmap;
    }

    public class TextAnnotation : PageAnnotation
    {
        public Point Position { get; set; }
        public string Content { get; set; } = "";
        public double FontSize { get; set; } = 14;
        // Additional distance between adjacent characters, in canvas units. Zero preserves text
        // created before the letter-spacing control was introduced.
        public double LetterSpacing { get; set; }
        // Typeface and style. FontName is a font-family name (any installed system font). Bold/Italic/Strike
        // apply to the whole box. Defaults keep text placed before these existed rendering as plain Segoe UI.
        public string FontName { get; set; } = "Segoe UI";
        public bool Bold { get; set; }
        public bool Italic { get; set; }
        public bool Strike { get; set; }
        public bool Underline { get; set; }
        public byte ColorR { get; set; } = 0;
        public byte ColorG { get; set; } = 0;
        public byte ColorB { get; set; } = 0;
        public byte ColorA { get; set; } = 255;

        // Box geometry. Width is fixed (text wraps to it); Height auto-grows to fit the wrapped text.
        public double Width { get; set; } = 200;
        public double Height { get; set; } = 28;

        // Optional background fill (the "whiteout"/highlight behind the text). BgA == 0 means no fill.
        public byte BgR { get; set; } = 255;
        public byte BgG { get; set; } = 255;
        public byte BgB { get; set; } = 255;
        public byte BgA { get; set; } = 0;

        public Color GetColor() => Color.FromArgb(ColorA, ColorR, ColorG, ColorB);
        public void SetColor(Color c) { ColorR = c.R; ColorG = c.G; ColorB = c.B; ColorA = c.A; }

        public Color GetFill() => Color.FromArgb(BgA, BgR, BgG, BgB);
        public void SetFill(Color c) { BgR = c.R; BgG = c.G; BgB = c.B; BgA = c.A; }
        public bool HasFill => BgA > 0;

        // Clockwise turn on screen (0, 90, 180 or 270) from left-to-right reading, so an edit of
        // sideways or upside-down text reads the same way as the text it replaces. Width runs along
        // the reading direction and Height across the lines. Text placed before turns existed has 0.
        public int Rotation { get; set; }

        // Position is the top-left of the box as it sits on the canvas; a quarter turn swaps the
        // box's width and height there.
        public Size FootprintSize => Rotation is 90 or 270 ? new Size(Height, Width) : new Size(Width, Height);
    }

    public class InkAnnotation : PageAnnotation
    {
        public List<Point> Points { get; set; } = [];
        public double StrokeWidth { get; set; } = 2;
        public byte ColorR { get; set; } = 255;
        public byte ColorG { get; set; } = 0;
        public byte ColorB { get; set; } = 0;
        public byte ColorA { get; set; } = 255;

        public Color GetColor() => Color.FromArgb(ColorA, ColorR, ColorG, ColorB);
        public void SetColor(Color c) { ColorR = c.R; ColorG = c.G; ColorB = c.B; ColorA = c.A; }

        // Shapes (#127 Phase 3): a non-zero alpha fills the region enclosed by the stroke (the
        // Shapes tool commits closed outlines - the last point repeats the first). Plain ink
        // strokes and lines leave FillA = 0 and render exactly as before.
        public byte FillR { get; set; }
        public byte FillG { get; set; }
        public byte FillB { get; set; }
        public byte FillA { get; set; }

        public bool HasFill => FillA > 0;
        public Color GetFillColor() => Color.FromArgb(FillA, FillR, FillG, FillB);
        public void SetFillColor(Color c) { FillR = c.R; FillG = c.G; FillB = c.B; FillA = c.A; }
    }

    // One brush-eraser pass over a highlight: a stroke (canvas-space points) of the given radius. The
    // highlight renders as its rectangle MINUS the union of these widened strokes - one anti-aliased
    // geometry, so the erased edges are smooth curves, not blocky steps or seamed strips.
    public sealed class HighlightErase
    {
        public System.Collections.Generic.List<Point> Points { get; set; } = [];
        public double Radius { get; set; }
    }

    public class HighlightAnnotation : PageAnnotation
    {
        public Rect Bounds { get; set; }
        // Brush-eraser passes carved out of this highlight (null = untouched solid rect). Only Fill-style
        // highlights are ever carved.
        public System.Collections.Generic.List<HighlightErase>? Erases { get; set; }
        public HighlightStyle Style { get; set; } = HighlightStyle.Fill;
        public byte ColorR { get; set; } = 255;
        public byte ColorG { get; set; } = 255;
        public byte ColorB { get; set; } = 0;
        public byte ColorA { get; set; } = 80;

        public Color GetColor() => Color.FromArgb(ColorA, ColorR, ColorG, ColorB);
        public virtual void SetColor(Color c) { ColorR = c.R; ColorG = c.G; ColorB = c.B; ColorA = c.A; }

        /// <summary>
        /// The actual rectangle painted for this annotation. Fill uses the whole bounds;
        /// strikethrough is a thin band at the vertical center; underline sits at the bottom.
        /// </summary>
        public Rect DrawRect()
        {
            double t = Math.Max(2.0, Bounds.Height * 0.10);
            return Style switch
            {
                HighlightStyle.Strikethrough => new Rect(Bounds.X, Bounds.Y + Bounds.Height / 2 - t / 2, Bounds.Width, t),
                HighlightStyle.Underline => new Rect(Bounds.X, Bounds.Y + Bounds.Height - t, Bounds.Width, t),
                _ => Bounds,
            };
        }
    }

    /// <summary>
    /// An opaque filled rectangle that covers ("erases") existing PDF content - the background half
    /// of a text edit. Subclasses HighlightAnnotation so it inherits all rect plumbing (render, drag,
    /// corner-resize, hit-test, export) for free; the only differences are an opaque default fill and
    /// a SetColor that can never go translucent (a see-through cover would let the old text ghost
    /// through, the exact bug this feature exists to avoid). The paired replacement text is a normal
    /// TextAnnotation placed on top, so it is independently editable, movable, and recolorable.
    /// </summary>
    public class CoverAnnotation : HighlightAnnotation
    {
        public CoverAnnotation()
        {
            ColorR = 255; ColorG = 255; ColorB = 255; ColorA = 255;   // opaque white by default
            Style = HighlightStyle.Fill;
        }

        /// <summary>Recolor the cover but keep it fully opaque - drop any alpha the caller passed.</summary>
        public override void SetColor(Color c) => base.SetColor(Color.FromArgb(255, c.R, c.G, c.B));
    }

    /// <summary>
    /// A measurement drawn with the Measure tool. Unlike the other overlay annotations it is not
    /// burned into the page on save: it is written as a real PDF measurement annotation (a
    /// dimension line, polyline or polygon with its scale), so Acrobat and other viewers can read
    /// it and KillerPDF can open it for editing again. Geometry is in canvas units like every
    /// other annotation. Each measurement keeps the scale it was made with, as the PDF does.
    /// </summary>
    public class MeasurementAnnotation : PageAnnotation
    {
        public long AnnotationFlags { get; set; } = 4;
        [System.Text.Json.Serialization.JsonIgnore]
        public bool IsVisibleOnScreen => (AnnotationFlags & 34) == 0;
        public MeasurementKind Kind { get; set; }
        /// <summary>The measured points. An angle is first arm end, corner, second arm end. A closed
        /// perimeter does not repeat its first point.</summary>
        public List<Point> Points { get; set; } = [];
        /// <summary>True when a perimeter returns to its first point.</summary>
        public bool Closed { get; set; }
        /// <summary>Center of the value label, or null for the automatic spot.</summary>
        public Point? LabelCenter { get; set; }
        /// <summary>Label text size in canvas units.</summary>
        public double LabelSize { get; set; } = 26;
        public double StrokeWidth { get; set; } = 2;
        public byte ColorR { get; set; } = 255;
        public byte ColorG { get; set; } = 0;
        public byte ColorB { get; set; } = 0;
        public byte ColorA { get; set; } = 255;
        /// <summary>The scale ratio text saved with the measurement, such as "1 in = 10 ft".</summary>
        public string ScaleName { get; set; } = "1 in = 1 in";
        /// <summary>Drawing units per PDF point.</summary>
        public double UnitsPerPoint { get; set; } = 1.0 / 72.0;
        /// <summary>Unit id from MeasurementUnits (in, ft, ft-in, yd, mi, mm, cm, m, km).</summary>
        public string Unit { get; set; } = "in";
        public int Precision { get; set; } = 2;

        // Runtime-only: where the label was last drawn, for hit testing and the selection outline.
        public Rect LabelBounds;

        public Color GetColor() => Color.FromArgb(ColorA, ColorR, ColorG, ColorB);
        public void SetColor(Color c) { ColorR = c.R; ColorG = c.G; ColorB = c.B; ColorA = c.A; }
    }

    /// <summary>
    /// A signature placed on a PDF page: either ink strokes or an imported image.
    /// </summary>
    public class SignatureAnnotation : PlacedAnnotation
    {
        /// <summary>Object number of the signature form field this fills, if any. Kept on the
        /// annotation so it follows history snapshots and stays with its document.</summary>
        public int? FieldObjectNumber { get; set; }

        internal override void CopyHistoryIdentityFrom(PageAnnotation source)
        {
            base.CopyHistoryIdentityFrom(source);
            if (source is SignatureAnnotation signature)
                FieldObjectNumber = signature.FieldObjectNumber;
        }

        /// <summary>The signature filling the given field among one page's annotations.</summary>
        internal static SignatureAnnotation? ForField(IEnumerable<PageAnnotation>? page, int objNum) =>
            page?.OfType<SignatureAnnotation>().FirstOrDefault(s => s.FieldObjectNumber == objNum);

        public List<List<Point>> Strokes { get; set; } = [];
        /// <summary>Pen thickness (DIPs at source scale); multiplied by Scale when rendered.</summary>
        public double StrokeWidth { get; set; } = 2.5;
        /// <summary>Base-64 encoded PNG. Non-null = image sig; null = drawn strokes.</summary>
        public string? ImageData { get; set; }
        /// <summary>Outlines of an imported SVG signature, in source units. Null for drawn and
        /// image signatures.</summary>
        public List<SignatureVector>? Vectors { get; set; }
    }

    /// <summary>
    /// An image placed on a PDF page as a resizable annotation.
    /// </summary>
    public class ImageAnnotation : PlacedAnnotation
    {
        /// <summary>Base-64 encoded image bytes (PNG, JPG, BMP, etc.).</summary>
        public string ImageData { get; set; } = "";
    }

    /// <summary>
    /// A point that can be serialized to JSON (WPF Point doesn't serialize well).
    /// </summary>
    public class SerializablePoint
    {
        public double X { get; set; }
        public double Y { get; set; }
    }

    /// <summary>
    /// A saved signature stored in the user's AppData for reuse.
    /// </summary>
    /// <summary>Distinguishes a full signature from a (smaller) initials stamp. Default is Signature
    /// so signatures saved before this field existed still deserialize correctly.</summary>
    public enum SignatureKind { Signature, Initials }

    public class SavedSignature
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "Signature";
        /// <summary>Whether this is a full signature or an initials stamp. Drives which popup section
        /// it appears in and the default placement scale.</summary>
        public SignatureKind Kind { get; set; } = SignatureKind.Signature;
        /// <summary>Pen thickness the signature was drawn with (DIPs at CanvasWidth/Height scale).</summary>
        public double StrokeWidth { get; set; } = 2.5;
        public List<List<SerializablePoint>> Strokes { get; set; } = [];
        public double CanvasWidth { get; set; } = 400;
        public double CanvasHeight { get; set; } = 150;
        /// <summary>Base-64 encoded PNG for imported image signatures. Null = drawn strokes.</summary>
        public string? ImageData { get; set; }
        /// <summary>Outlines of a signature imported from SVG (#398), in CanvasWidth/Height units.
        /// Null for drawn and image signatures.</summary>
        public List<SignatureVector>? Vectors { get; set; }
    }

    /// <summary>
    /// One outline of a vector signature (#398). Data is absolute SVG path data using only move,
    /// line, cubic and close commands. Colors are ARGB; a null color is not painted. LineCap and
    /// LineJoin use the PDF numbering: butt, round, square and miter, round, bevel.
    /// </summary>
    public class SignatureVector
    {
        public string Data { get; set; } = "";
        public uint? Fill { get; set; }
        public uint? Stroke { get; set; }
        public double StrokeWidth { get; set; } = 1;
        public bool EvenOdd { get; set; }
        public int LineCap { get; set; }
        public int LineJoin { get; set; }
    }
}
