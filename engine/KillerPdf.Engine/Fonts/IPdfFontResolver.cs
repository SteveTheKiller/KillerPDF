namespace KillerPdf.Engine.Fonts;

/// <summary>Supplies optional host font bytes without coupling the engine to a UI platform.</summary>
public interface IPdfFontResolver
{
    /// <summary>Returns a standalone TrueType or OpenType font, or null when no match is available.</summary>
    byte[]? Resolve(PdfFontRequest request);
}

/// <summary>Describes an unembedded PDF font requested by the engine.</summary>
public readonly record struct PdfFontRequest(
    string PostScriptName, string Registry, string Ordering, bool IsVertical);
