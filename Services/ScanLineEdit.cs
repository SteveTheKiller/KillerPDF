using System.Windows;
using System.Windows.Media;
using KillerPdf.Engine.Documents;

namespace KillerPDF.Services;

/// <summary>What an edit of a scanned line changes: the words' area, the slightly larger area to
/// cover so no scanned edge shows, the text set over it, and the first word it replaces.</summary>
internal sealed record ScanLineEditPlan(Rect Covered, Rect Cover, string Text, int First);

/// <summary>How a scanned line looks on the page, in canvas units: its colors, the closest typeface
/// and size, the height of its ink, and that height word by word because a scan tilts.</summary>
internal sealed record ScanLineFit(Color Background, Color Ink, string Family, double FontSize, bool Bold,
    double Baseline, IReadOnlyList<double> WordBaselines);

/// <summary>A picture of the page a scanned line can be measured from, with how many of its pixels
/// fall on one canvas unit.</summary>
internal sealed record ScanMeasureSource(double PixelsPerUnit, Func<ScanLineFit?> Measure);

/// <summary>Limits an edit of a scanned line to the words it changes, so the rest stay as scanned.</summary>
internal static class ScanLineEdit
{
    /// <summary>
    /// Plans the edit of a line from its words and their boxes, or returns null when nothing changed.
    /// Replacement text too wide for the gap before the next word takes over the rest of the line,
    /// because setting it there would run over scanned words.
    /// </summary>
    internal static ScanLineEditPlan? Plan(IReadOnlyList<string> words, IReadOnlyList<Rect> boxes,
        string typed, Func<string, double> measure)
    {
        ArgumentNullException.ThrowIfNull(words);
        ArgumentNullException.ThrowIfNull(boxes);
        ArgumentNullException.ThrowIfNull(measure);
        if (typed.Contains('\n') || typed.Contains('\r'))
            return Planned(boxes, 0, boxes.Count - 1, typed);
        if (ScanEditSpan.Diff(words, typed) is not { } span) return null;
        int next = span.Last + 1;
        if (next < boxes.Count && measure(span.Replacement) > boxes[next].Left - boxes[span.First].Left)
        {
            string[] edited = typed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            return Planned(boxes, span.First, boxes.Count - 1, string.Join(' ', edited[span.First..]));
        }
        return Planned(boxes, span.First, span.Last, span.Replacement);
    }

    // The cover reaches past the words so no scanned edge shows, but never into a neighbor that stays.
    private const double CoverMargin = 3;

    private static ScanLineEditPlan Planned(IReadOnlyList<Rect> boxes, int first, int last, string text)
    {
        Rect covered = Span(boxes, first, last);
        Rect cover = covered;
        cover.Inflate(CoverMargin, CoverMargin);
        double left = first > 0 ? Math.Min(covered.Left, Math.Max(cover.Left, boxes[first - 1].Right)) : cover.Left;
        double right = last + 1 < boxes.Count ? Math.Max(covered.Right, Math.Min(cover.Right, boxes[last + 1].Left)) : cover.Right;
        cover = new Rect(left, cover.Top, right - left, cover.Height);
        return new ScanLineEditPlan(covered, cover, text, first);
    }

    // The long edge a page is drawn at to measure a line. Letters only a few pixels tall at the
    // screen's zoom round to the wrong height, which picks too small a size and the wrong typeface.
    private const int MeasureEdge = 2048;

    /// <summary>The pixels the measure render puts on one canvas unit of a page shown at this size.</summary>
    internal static double RenderPixelsPerUnit(double canvasWidth, double canvasHeight) =>
        MeasureEdge / Math.Max(canvasWidth, canvasHeight);

    /// <summary>Measures from the source with the most pixels per unit first, so a large sheet at a
    /// high zoom reads from the screen and is not drawn again at fewer pixels. A source that cannot be
    /// read, such as a page the file cannot draw completely, gives way to the next.</summary>
    internal static ScanLineFit? MeasureFromSharpest(IReadOnlyList<ScanMeasureSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        foreach (ScanMeasureSource source in sources.OrderByDescending(source => source.PixelsPerUnit))
        {
            try
            {
                if (source.Measure() is { } fit) return fit;
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException
                or NotSupportedException or FormatException or System.IO.IOException) { }
        }
        return null;
    }

    // The last page drawn for a measure, so the clicks that follow on that page do not draw it again.
    private static (string Key, byte[] Pixels, int Width, int Height)? _lastMeasureRender;
    private static readonly Lock MeasureRenderSync = new();

    private static (byte[] Pixels, int Width, int Height) MeasureRender(string path, int pageIndex)
    {
        var file = new System.IO.FileInfo(path);
        string key = string.Join('|', file.FullName, file.LastWriteTimeUtc.Ticks, file.Length, pageIndex);
        lock (MeasureRenderSync)
            if (_lastMeasureRender is { } last && last.Key == key)
                return ((byte[])last.Pixels.Clone(), last.Width, last.Height);
        using PdfPageRenderSession session = PdfPageRenderSession.OpenEngineFirst(path, MeasureEdge, MeasureEdge);
        PdfRenderedPage page = session.RenderPage(pageIndex);
        byte[] pixels = (byte[])page.Pixels.Clone();
        OverWhitePage(pixels);
        lock (MeasureRenderSync) _lastMeasureRender = (key, pixels, page.Width, page.Height);
        return ((byte[])pixels.Clone(), page.Width, page.Height);
    }

    /// <summary>Measures a line from the page itself, at a size that shows its letters. Returns null
    /// when the line cannot be measured or the page is shown turned from the way the file draws it.
    /// Throws when the page cannot be drawn completely, so the caller can measure another way.</summary>
    internal static ScanLineFit? Measure(string path, int pageIndex, double canvasWidth, double canvasHeight,
        IReadOnlyList<string> words, IReadOnlyList<Rect> boxes, IReadOnlyList<PdfScanTextFace> faces)
    {
        (byte[] pixels, int width, int height) = MeasureRender(path, pageIndex);
        if (Math.Abs(width / (double)height - canvasWidth / canvasHeight) > 0.01) return null;
        return Measure(pixels, width, height, canvasWidth, canvasHeight, words, boxes, faces);
    }

    /// <summary>Measures a line from opaque BGRA pixels of the page shown on a canvas of the given size.</summary>
    internal static ScanLineFit? Measure(byte[] pixels, int width, int height, double canvasWidth, double canvasHeight,
        IReadOnlyList<string> words, IReadOnlyList<Rect> boxes, IReadOnlyList<PdfScanTextFace> faces)
    {
        ArgumentNullException.ThrowIfNull(faces);
        if (faces.Count == 0) return null;
        double pixelsX = width / canvasWidth, pixelsY = height / canvasHeight;
        PdfOcrPixelWord[] pixelWords = [.. words.Select((word, index) => ToPixelWord(word, boxes[index], pixelsX, pixelsY))];
        if (PdfScanTextAnalyzer.MeasureLine(pixels, width, height, pixelWords) is not { } look) return null;
        PdfScanTextFace face = PdfScanTextWriter.ChooseFace(
            new PdfScanTextPageAnalysis(width, height, false, look.Words), faces);
        double size = PdfScanTextWriter.FontSizeFor(face, look.XHeight, look.CapHeight, look.CapitalsOnly, 1 / pixelsY);
        if (size <= 0) return null;
        double baseline = look.Baseline / pixelsY;
        double[] wordBaselines = [.. look.Words.Select(word => word.HasClearInk ? word.Baseline / pixelsY : baseline)];
        return new ScanLineFit(ToColor(look.BackgroundColor), ToColor(look.InkColor), face.Family, size, look.Bold,
            baseline, wordBaselines);
    }

    /// <summary>Lays transparent pixels over the white page, as the screen shows them.</summary>
    internal static void OverWhitePage(byte[] bgra)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        for (int pixel = 0; pixel < bgra.Length; pixel += 4)
        {
            int alpha = bgra[pixel + 3];
            for (int channel = 0; channel < 3; channel++)
                bgra[pixel + channel] = (byte)((bgra[pixel + channel] * alpha + 255 * (255 - alpha)) / 255);
            bgra[pixel + 3] = 255;
        }
    }

    /// <summary>The top of the edit box whose text sits on the line's ink. The OCR layer's own
    /// baseline and size say where the recognizer drew its box, not where the letters are.</summary>
    internal static double EditorTop(ScanLineFit fit, double faceBaseline, double fallbackTop) =>
        PdfTextEditPlacement.TopFromBaseline(fit.Baseline, faceBaseline, fit.FontSize, 1, fallbackTop);

    // The edit box sets its text one unit below its top, past its border. A placed text annotation
    // draws and saves two units below its own, so the same position would land one unit low.
    internal const double AnnotationInsetGap = 1;

    /// <summary>How far the placed replacement moves from the edit box: to the height of the word it
    /// replaces, less the annotation's deeper inset.</summary>
    internal static double TopShift(ScanLineEditPlan plan, ScanLineFit fit) =>
        (plan.First < fit.WordBaselines.Count ? fit.WordBaselines[plan.First] - fit.Baseline : 0) - AnnotationInsetGap;

    private static Color ToColor((byte Red, byte Green, byte Blue) value) =>
        Color.FromRgb(value.Red, value.Green, value.Blue);

    /// <summary>A word's box on the canvas as a word on the page bitmap.</summary>
    internal static PdfOcrPixelWord ToPixelWord(string text, Rect box, double pixelsX, double pixelsY) =>
        new(text, 1, (int)Math.Floor(box.Left * pixelsX), (int)Math.Floor(box.Top * pixelsY),
            (int)Math.Ceiling(box.Right * pixelsX), (int)Math.Ceiling(box.Bottom * pixelsY));

    private static Rect Span(IReadOnlyList<Rect> boxes, int first, int last)
    {
        Rect covered = boxes[first];
        for (int index = first + 1; index <= last; index++) covered.Union(boxes[index]);
        return covered;
    }
}
