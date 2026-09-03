namespace KillerPdf.Engine.Documents;

/// <summary>Describes one native link annotation and its resolved target.</summary>
public sealed record PdfLinkInfo
{
    /// <summary>Gets the zero-based page containing the link.</summary>
    public required int PageIndex { get; init; }
    /// <summary>Gets the link's index in the page annotation array.</summary>
    public required int AnnotationIndex { get; init; }
    /// <summary>Gets the annotation object number when the link is indirect.</summary>
    public int? ObjectNumber { get; init; }
    /// <summary>Gets the annotation object generation when the link is indirect.</summary>
    public int? Generation { get; init; }
    /// <summary>Gets the normalized left coordinate in PDF points.</summary>
    public required double Left { get; init; }
    /// <summary>Gets the normalized bottom coordinate in PDF points.</summary>
    public required double Bottom { get; init; }
    /// <summary>Gets the normalized right coordinate in PDF points.</summary>
    public required double Right { get; init; }
    /// <summary>Gets the normalized top coordinate in PDF points.</summary>
    public required double Top { get; init; }
    /// <summary>Gets the resolved zero-based local destination page.</summary>
    public int? DestinationPageIndex { get; init; }
    /// <summary>Gets the decoded named destination when one was used.</summary>
    public string? NamedDestination { get; init; }
    /// <summary>Gets the decoded URI target.</summary>
    public string? Uri { get; init; }
}
