using System.IO;
using KillerPdf.Engine.Formats.Epub;

namespace KillerPDF.Services;

internal static class NativePdfConversion
{
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

    /// <summary>Converts a DjVu document. An existing destination is replaced only when the user confirmed it.</summary>
    internal static void WriteNewDjvu(NativeWorkingDocument source, string sourcePath, string destination,
        CancellationToken cancellationToken = default, bool replaceExisting = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(source);
        CheckDestination(sourcePath, destination, replaceExisting);
        Publish(destination, source.ConvertDjvuToPdf(cancellationToken), replaceExisting, cancellationToken);
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
        if (string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The converted PDF must use a different path from its source.", nameof(destination));
        if (!Path.GetExtension(destination).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The converted document requires a PDF destination.", nameof(destination));
        if (!replaceExisting && File.Exists(destination))
            throw new IOException("The converted PDF destination already exists.");
    }

    // Both paths write a temporary file beside the destination and publish it only once it is complete,
    // so a failed conversion never touches the file it would replace.
    private static void Publish(string destination, byte[] pdf, bool replaceExisting, CancellationToken cancellationToken)
    {
        if (replaceExisting) AtomicFileOutput.Write(destination, pdf, cancellationToken);
        else AtomicFileOutput.WriteNew(destination, pdf, cancellationToken);
    }
}
