using System.Windows.Media;

namespace KillerPDF
{
    internal enum StampKind { PageNumber, Watermark }

    // The full configuration produced/edited by the Stamp window. One spec can drive page numbers,
    // a watermark, or both, each over its own page range. A spec is the unit that gets re-opened when
    // the user double-clicks a placed stamp, so it carries everything needed to recreate the stamps.
    internal sealed class StampSpec
    {
        // ---- Page numbers ----
        public bool   NumbersEnabled;
        public int    StartNumber = 1;
        public string Format      = "{n}";    // {n} = this page's number, {N} = total
        public int    NumPosH     = 1;         // 0 left, 1 center, 2 right
        public int    NumPosV     = 2;         // 0 top, 1 middle, 2 bottom
        public double NumFontPt   = 12;
        public Color  NumColor    = Colors.Black;
        public string NumRange    = "";        // "" = all pages; else "1-3,5"
        public bool   NumMirror;               // flip left/right each page so numbers sit on the outer edge
        public double NumCustomX  = 0.5;       // used when NumPosH == -1 (Custom): center as a fraction of page
        public double NumCustomY  = 0.92;

        // ---- Watermark ----
        public bool    WmEnabled;
        public bool    WmIsImage;              // false = text, true = image
        public string  WmText    = "DRAFT";
        public string  WmFont    = "Segoe UI";
        public double  WmFontPt  = 64;
        public Color   WmColor   = Color.FromRgb(0x88, 0x88, 0x88);
        public double  WmOpacity = 0.25;       // 0..1
        public double  WmAngle   = 45;         // degrees, counter-clockwise
        public int     WmPosH    = 1;          // 0 left, 1 center, 2 right
        public int     WmPosV    = 1;          // 0 top, 1 middle, 2 bottom
        public string? WmImagePath;            // source image when WmIsImage
        public double  WmScale   = 1.0;        // multiplier on the natural placement size
        public string  WmRange   = "";         // "" = all pages
        public double  WmCustomX = 0.5;        // used when WmPosH == -1 (Custom): center as a fraction of page
        public double  WmCustomY = 0.5;

        public StampSpec Clone() => (StampSpec)MemberwiseClone();
    }

    // A single placed stamp on one page. It points back at the spec that created it so a double-click
    // on the page can re-open the Stamp window with the original settings. The concrete text/position is
    // derived from Spec + page geometry at render/burn time, so nothing here needs the resolved layout.
    internal sealed class StampInstance
    {
        public int       PageIndex;
        public StampKind Kind;
        public StampSpec Spec = null!;
    }
}
