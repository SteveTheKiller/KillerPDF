namespace KillerPdf.Engine.Formats.Djvu;

/// <summary>The encoded semantic label of a text zone.</summary>
public enum DjvuTextZoneType
{
    /// <summary>Page.</summary>
    Page = 1,
    /// <summary>Column.</summary>
    Column = 2,
    /// <summary>Region.</summary>
    Region = 3,
    /// <summary>Paragraph.</summary>
    Paragraph = 4,
    /// <summary>Line.</summary>
    Line = 5,
    /// <summary>Word.</summary>
    Word = 6,
    /// <summary>Character.</summary>
    Character = 7
}

/// <summary>Original page text and its ordered, unrotated text zones.</summary>
public sealed class DjvuTextPage
{
    /// <summary>Original text, including embedded separators.</summary>
    public string Text { get; }
    /// <summary>Immutable zones in encoded order.</summary>
    public IReadOnlyList<DjvuTextZone> Zones { get; }
    /// <summary>Notices about text that could not be read exactly, each naming its page.</summary>
    public IReadOnlyList<DjvuDiagnostic> Diagnostics { get; }

    internal DjvuTextPage(string text, DjvuTextZone[] zones, DjvuDiagnostic[]? diagnostics = null)
    {
        Text = text;
        Zones = Array.AsReadOnly(zones);
        Diagnostics = Array.AsReadOnly(diagnostics ?? []);
    }
}

/// <summary>A UTF16 text range with a bottom-left pixel rectangle.</summary>
public sealed class DjvuTextZone
{
    /// <summary>Encoded semantic label.</summary>
    public DjvuTextZoneType Type { get; }
    /// <summary>Index of the structural parent, or -1 for a root.</summary>
    public int ParentIndex { get; }
    /// <summary>UTF16 offset into the page text.</summary>
    public int Start { get; }
    /// <summary>UTF16 length in the page text.</summary>
    public int Length { get; }
    /// <summary>Unrotated left coordinate in pixels.</summary>
    public long Left { get; }
    /// <summary>Unrotated bottom coordinate in pixels, measured from the bottom.</summary>
    public long Bottom { get; }
    /// <summary>Pixel width.</summary>
    public long Width { get; }
    /// <summary>Pixel height.</summary>
    public long Height { get; }

    internal DjvuTextZone(DjvuTextZoneType type, int parentIndex, int start, int length,
        long left, long bottom, long width, long height)
    {
        Type = type; ParentIndex = parentIndex; Start = start; Length = length;
        Left = left; Bottom = bottom; Width = width; Height = height;
    }
}
