using KillerPdf.Engine.Fonts;

namespace KillerPdf.Engine.Formats.Epub;

/// <summary>Requests a computed weight and real style in a CSS family, or a variant of the explicit fallback when Family is null.</summary>
public sealed record EpubFontFaceRequest(EpubFontFamily? Family, double Weight, TrueTypeFont DefaultFont)
{
    /// <summary>Gets the requested real face style.</summary>
    public EpubFontStyle Style { get; init; } = EpubFontStyle.Normal;
}

/// <summary>Identifies the supported styles of an actual EPUB font face.</summary>
public enum EpubFontStyle
{
    /// <summary>The upright face.</summary>
    Normal,
    /// <summary>The italic face.</summary>
    Italic
}

/// <summary>Describes the host's actual face selection rather than assuming the requested weight was found.</summary>
public enum EpubFontMatch
{
    /// <summary>The actual and requested weights match within the requested style.</summary>
    Exact,
    /// <summary>The host selected a real available face with another weight within the requested style.</summary>
    Nearest
}

/// <summary>Captures an actual font face, its declared weight and style, and the host's matching result.</summary>
public sealed record EpubFontFaceResult(TrueTypeFont Font, double ActualWeight, EpubFontMatch Match)
{
    /// <summary>Gets the style of the selected real face, which must match the request even for a nearest weight.</summary>
    public EpubFontStyle ActualStyle { get; init; } = EpubFontStyle.Normal;
}
