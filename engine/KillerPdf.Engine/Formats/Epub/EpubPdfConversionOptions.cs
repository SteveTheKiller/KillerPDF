using KillerPdf.Engine.Fonts;

namespace KillerPdf.Engine.Formats.Epub;

/// <summary>Controls explicit EPUB conversion paper geometry and fallback typography in points.</summary>
public sealed record EpubPdfConversionOptions(TrueTypeFont DefaultFont)
{
    /// <summary>Gets the per-chapter table work budget, from 1 through 16,000,000 block and inline atom visits.</summary>
    public int TableFlowWorkLimit { get; init; } = EpubLayoutOptions.DefaultTableFlowWorkLimit;
    /// <summary>Gets the paper width in points.</summary>
    public double PageWidthPoints { get; init; } = 595.2755905511812;
    /// <summary>Gets the paper height in points.</summary>
    public double PageHeightPoints { get; init; } = 841.8897637795276;
    /// <summary>Gets the paper margin in points.</summary>
    public double MarginPoints { get; init; } = 42.51968503937008;
    /// <summary>Gets the fallback font size in points.</summary>
    public double FontSizePoints { get; init; } = 12;
    /// <summary>Gets an optional host font-family resolver for upright weight 400, used for layout and embedded PDF text.</summary>
    public Func<EpubFontFamily, TrueTypeFont?>? FontFamilyResolver { get; init; }
    /// <summary>Gets an optional resolver for real font weights and styles shared by native layout and embedded PDF text.</summary>
    public Func<EpubFontFaceRequest, EpubFontFaceResult?>? FontFaceResolver { get; init; }
    /// <summary>Gets an optional callback for omitted chapters, resources and navigation. Navigation uses spine index -1.</summary>
    public Action<EpubChapterDiagnostic>? DiagnosticReported { get; init; }
    /// <summary>
    /// Gets whether a chapter whose content is refused converts as a labeled diagnosis page instead of
    /// refusing the whole conversion. Package, safety and cancellation failures still end conversion.
    /// </summary>
    public bool IsolateChapterFailures { get; init; }
    /// <summary>Gets the caller's localized heading for an isolated chapter's diagnosis page.</summary>
    public string UnavailableChapterLabel { get; init; } = "This chapter could not be displayed.";

    /// <summary>Reports whether a PDF reader accepts this page size in points.</summary>
    /// <remarks>ISO 32000-1 annex C.2: without UserUnit each side is 3 to 14,400 default user units (200 in, 5,080 mm).</remarks>
    public static bool FitsPdfPageLimit(double widthPoints, double heightPoints)
        => widthPoints is >= MinimumPagePoints and <= MaximumPagePoints && heightPoints is >= MinimumPagePoints and <= MaximumPagePoints;

    internal const double MinimumPagePoints = 3, MaximumPagePoints = 14_400;
}
