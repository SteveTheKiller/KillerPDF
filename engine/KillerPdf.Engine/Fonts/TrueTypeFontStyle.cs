namespace KillerPdf.Engine.Fonts;

/// <summary>The style declared by an OpenType font's metadata.</summary>
public enum TrueTypeFontStyle
{
    /// <summary>The metadata does not establish a consistent supported style.</summary>
    Unknown = 0,
    /// <summary>The font declares an upright style.</summary>
    Normal = 1,
    /// <summary>The font declares an italic style.</summary>
    Italic = 2,
    /// <summary>The font declares an oblique style.</summary>
    Oblique = 3
}
