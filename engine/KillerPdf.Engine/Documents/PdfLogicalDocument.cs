namespace KillerPdf.Engine.Documents;

/// <summary>Owns a logical document identity and an explicitly supplied read snapshot.</summary>
public sealed class PdfLogicalDocument
{
    /// <summary>Creates an independent owner for the supplied read snapshot.</summary>
    public PdfLogicalDocument(PdfReadSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ReadSnapshot = snapshot;
    }
    /// <summary>Gets this owner's identity, independently of source or layout identity.</summary>
    public Guid Id { get; } = Guid.NewGuid();
    /// <summary>Gets the explicitly retained read snapshot.</summary>
    public PdfReadSnapshot ReadSnapshot { get; }
}
