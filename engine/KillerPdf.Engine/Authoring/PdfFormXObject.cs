using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;

namespace KillerPdf.Engine.Authoring;

/// <summary>
/// Reusable vector or composite page content stored once as a PDF Form XObject.
/// </summary>
public sealed class PdfFormXObject
{
    /// <summary>Creates reusable form content with optional transparency-group semantics.</summary>
    public PdfFormXObject(
        double width, double height, PdfContentStreamBuilder content,
        bool isolatedTransparencyGroup = false, bool knockoutTransparencyGroup = false,
        PdfTransparencyGroupColorSpace transparencyGroupColorSpace =
            PdfTransparencyGroupColorSpace.Rgb)
    {
        ArgumentNullException.ThrowIfNull(content);
        Width = Dimension(width, nameof(width));
        Height = Dimension(height, nameof(height));
        if (!Enum.IsDefined(transparencyGroupColorSpace))
            throw new ArgumentOutOfRangeException(nameof(transparencyGroupColorSpace));
        if (content.MarkedContentIds.Count > 0)
            throw new ArgumentException(
                "Tagged marked content belongs to a page structure tree and cannot be captured inside a reusable form.",
                nameof(content));

        Content = content.Build();
        Fonts = content.FontResources.ToDictionary(entry => entry.Key, entry => entry.Value);
        EmbeddedFonts = [.. content.EmbeddedFontResources];
        Images = content.ImageResources.ToDictionary(entry => entry.Key, entry => entry.Value);
        Assets = content.AssetResources.ToDictionary(entry => entry.Key, entry => entry.Value);
        OptionalContentGroups = content.OptionalContentResources
            .ToDictionary(entry => entry.Key, entry => entry.Value);
        GraphicsStates = content.GraphicsStateResources
            .ToDictionary(entry => entry.Key, entry => entry.Value);
        Shadings = content.ShadingResources.ToDictionary(entry => entry.Key, entry => entry.Value);
        Forms = content.FormResources.ToDictionary(entry => entry.Key, entry => entry.Value);
        Patterns = content.PatternResources.ToDictionary(entry => entry.Key, entry => entry.Value);
        IccColorSpaces = content.IccColorSpaceResources.ToDictionary(entry => entry.Key, entry => entry.Value);
        SpotColors = content.SpotColorResources.ToDictionary(entry => entry.Key, entry => entry.Value);
        LabColorSpaces = content.LabColorSpaceResources.ToDictionary(entry => entry.Key, entry => entry.Value);
        IndexedColorSpaces = content.IndexedColorSpaceResources.ToDictionary(entry => entry.Key, entry => entry.Value);
        CalibratedColorSpaces = content.CalibratedColorSpaceResources.ToDictionary(entry => entry.Key, entry => entry.Value);
        IsolatedTransparencyGroup = isolatedTransparencyGroup;
        KnockoutTransparencyGroup = knockoutTransparencyGroup;
        TransparencyGroupColorSpace = transparencyGroupColorSpace;
    }

    /// <summary>Gets the form bounding-box width.</summary>
    public double Width { get; }
    /// <summary>Gets the form bounding-box height.</summary>
    public double Height { get; }
    /// <summary>Gets whether compositing begins against a transparent backdrop.</summary>
    public bool IsolatedTransparencyGroup { get; }
    /// <summary>Gets whether group elements knock out earlier group elements.</summary>
    public bool KnockoutTransparencyGroup { get; }
    /// <summary>Gets the transparency-group blending color space.</summary>
    public PdfTransparencyGroupColorSpace TransparencyGroupColorSpace { get; }

    internal byte[] Content { get; }
    internal IReadOnlyDictionary<PdfStandardFont, PdfName> Fonts { get; }
    internal IReadOnlyCollection<EmbeddedFontUsage> EmbeddedFonts { get; }
    internal IReadOnlyDictionary<PdfImage, PdfName> Images { get; }
    internal IReadOnlyDictionary<PdfImageAsset, PdfName> Assets { get; }
    internal IReadOnlyDictionary<PdfOptionalContentGroup, PdfName> OptionalContentGroups { get; }
    internal IReadOnlyDictionary<PdfGraphicsState, PdfName> GraphicsStates { get; }
    internal IReadOnlyDictionary<PdfShading, PdfName> Shadings { get; }
    internal IReadOnlyDictionary<PdfFormXObject, PdfName> Forms { get; }
    internal IReadOnlyDictionary<PdfTilingPattern, PdfName> Patterns { get; }
    internal IReadOnlyDictionary<PdfIccProfile, PdfName> IccColorSpaces { get; }
    internal IReadOnlyDictionary<PdfSpotColor, PdfName> SpotColors { get; }
    internal IReadOnlyDictionary<PdfLabColorSpace, PdfName> LabColorSpaces { get; }
    internal IReadOnlyDictionary<PdfIndexedColorSpace, PdfName> IndexedColorSpaces { get; }
    internal IReadOnlyDictionary<PdfCalibratedColorSpace, PdfName> CalibratedColorSpaces { get; }

    private static double Dimension(double value, string name)
    {
        if (!double.IsFinite(value) || value <= 0)
            throw new ArgumentOutOfRangeException(name,
                "A form dimension must be finite and greater than zero.");
        return value;
    }
}

/// <summary>Device color spaces available for transparency-group compositing.</summary>
public enum PdfTransparencyGroupColorSpace
{
    /// <summary>Device grayscale.</summary>
    Gray,
    /// <summary>Device RGB.</summary>
    Rgb,
    /// <summary>Device CMYK.</summary>
    Cmyk
}
