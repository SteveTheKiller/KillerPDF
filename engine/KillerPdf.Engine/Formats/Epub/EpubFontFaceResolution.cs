using KillerPdf.Engine.Fonts;

namespace KillerPdf.Engine.Formats.Epub;

/// <summary>Requests a computed weight in a CSS family, or a variant of the explicit fallback when Family is null.</summary>
public sealed record EpubFontFaceRequest(EpubFontFamily? Family, double Weight, TrueTypeFont DefaultFont);

/// <summary>Describes the host's actual face selection rather than assuming the requested weight was found.</summary>
public enum EpubFontMatch
{
    /// <summary>The actual and requested weights match.</summary>
    Exact,
    /// <summary>The host selected a real available face with another weight.</summary>
    Nearest
}

/// <summary>Captures an actual font face, its declared weight and the host's matching result.</summary>
public sealed record EpubFontFaceResult(TrueTypeFont Font, double ActualWeight, EpubFontMatch Match);
