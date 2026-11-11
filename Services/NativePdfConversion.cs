using System.IO;
using KillerPdf.Engine.Formats.Djvu;
using KillerPdf.Engine.Formats.Epub;

namespace KillerPDF.Services;

internal static class NativePdfConversion
{
    internal static bool IsDestinationOpen(string path, IEnumerable<string?> openPaths)
    {
        string full = Path.GetFullPath(path);
        return openPaths.Any(other =>
        {
            if (string.IsNullOrEmpty(other)) return false;
            try { return string.Equals(Path.GetFullPath(other), full, StringComparison.OrdinalIgnoreCase); }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
        });
    }

    private const double CssPixelsPerMillimeter = 96 / 25.4;
    private const double PointsPerMillimeter = 72 / 25.4;
    // EpubPdfConversionOptions.FontSizePoints (12 pt) in CSS pixels.
    private const double ConversionFontCssPixels = 12 * 96d / 72;

    /// <summary>
    /// True when the paper and margins in millimeters are a page the EPUB layout accepts and a PDF
    /// reader opens. Both rules are the engine's, so the dialog cannot accept a size refused later.
    /// </summary>
    internal static bool FitsLayout(double widthMm, double heightMm, double marginMm) =>
        EpubPdfConversionOptions.FitsPdfPageLimit(MillimetersToPoints(widthMm), MillimetersToPoints(heightMm))
        && EpubLayoutOptions.FitsPageLimits(widthMm * CssPixelsPerMillimeter, heightMm * CssPixelsPerMillimeter,
            marginMm * CssPixelsPerMillimeter, ConversionFontCssPixels);

    // Rounded to a millionth of a point so an exact size such as 5,080 mm (14,400 pt) is not pushed
    // over a limit by binary floating point (5080 * 72 / 25.4 is 14400.000000000002 unrounded).
    internal static double MillimetersToPoints(double millimeters) => Math.Round(millimeters * PointsPerMillimeter, 6);

    /// <summary>
    /// The app's EPUB conversion settings for a chosen paper size in points. A chapter the reader
    /// refuses converts as its labeled unavailable-chapter page instead of failing the conversion.
    /// </summary>
    internal static EpubPdfConversionOptions EpubOptions(KillerPdf.Engine.Fonts.TrueTypeFont font,
        Func<EpubFontFaceRequest, EpubFontFaceResult?>? faceResolver, double widthPoints, double heightPoints,
        double marginPoints, string unavailableChapterLabel) => new(font)
    {
        PageWidthPoints = widthPoints,
        PageHeightPoints = heightPoints,
        MarginPoints = marginPoints,
        FontFaceResolver = faceResolver,
        IsolateChapterFailures = true,
        UnavailableChapterLabel = unavailableChapterLabel
    };

    /// <summary>
    /// Converts a DjVu document. An existing destination is replaced only when the user confirmed it. Returns the
    /// notices for pages whose text the PDF kept only in part, for <see cref="DjvuConversionNote"/>.
    /// </summary>
    internal static IReadOnlyList<DjvuDiagnostic> WriteNewDjvu(NativeWorkingDocument source,
        string sourcePath, string destination, CancellationToken cancellationToken = default, bool replaceExisting = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(source);
        CheckDestination(sourcePath, destination, replaceExisting);
        var notices = new List<DjvuDiagnostic>();
        Publish(destination, source.ConvertDjvuToPdf(notices, cancellationToken), replaceExisting, cancellationToken);
        return notices;
    }

    /// <summary>
    /// The note shown after a DjVu conversion: the pages whose text was left out, then the pages whose text kept
    /// replacement characters, each as one-based numbers with consecutive pages joined. A page left out is named only
    /// there. Null when no page lost text.
    /// </summary>
    internal static string? DjvuConversionNote(IReadOnlyList<DjvuDiagnostic> notices, Func<string, string> loc)
    {
        ArgumentNullException.ThrowIfNull(notices);
        ArgumentNullException.ThrowIfNull(loc);
        var leftOut = new SortedSet<int>();
        var replaced = new SortedSet<int>();
        foreach (var notice in notices)
        {
            if (notice.PageIndex is not int page) continue;
            if (notice.Code == "TextNotConverted") leftOut.Add(page);
            else if (notice.Code is "TextEncodingInvalid" or "TextCharactersReplaced") replaced.Add(page);
        }
        replaced.ExceptWith(leftOut);
        var lines = new List<string>(2);
        if (leftOut.Count > 0) lines.Add(string.Format(loc("Str_NativePdf_TextLeftOut"), IncompletePageLog.Describe([.. leftOut])));
        if (replaced.Count > 0) lines.Add(string.Format(loc("Str_NativePdf_TextReplaced"), IncompletePageLog.Describe([.. replaced])));
        return lines.Count == 0 ? null : string.Join("\n", lines);
    }

    /// <summary>Converts an EPUB document. An existing destination is replaced only when the user confirmed it.</summary>
    internal static void WriteNewEpub(NativeWorkingDocument source, string sourcePath, string destination,
        EpubPdfConversionOptions options, CancellationToken cancellationToken = default, bool replaceExisting = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);
        CheckDestination(sourcePath, destination, replaceExisting);
        Publish(destination, source.ConvertEpubToPdf(options, cancellationToken), replaceExisting, cancellationToken);
    }

    // Probed before any conversion work, so an unconfirmed existing file fails at once rather than at the end.
    private static void CheckDestination(string sourcePath, string destination, bool replaceExisting)
    {
        // The conversion dialog shows these messages, so they come from the string table. A parameter
        // name would add untranslated text to the message.
        if (string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(Loc("Str_Convert_SameAsSource"));
        if (!Path.GetExtension(destination).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(Loc("Str_Convert_NeedsPdfName"));
        if (!replaceExisting && File.Exists(destination))
            throw new IOException(Loc("Str_Convert_DestinationExists"));
    }

    private static string Loc(string key) => System.Windows.Application.Current?.TryFindResource(key) as string ?? key;

    // Both paths write a temporary file beside the destination and publish it only once it is complete,
    // so a failed conversion never touches the file it would replace.
    private static void Publish(string destination, byte[] pdf, bool replaceExisting, CancellationToken cancellationToken)
    {
        if (replaceExisting) AtomicFileOutput.Write(destination, pdf, cancellationToken);
        else AtomicFileOutput.WriteNew(destination, pdf, cancellationToken);
    }
}
