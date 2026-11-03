namespace KillerPdf.Engine.Documents;

/// <summary>Owns a logical document identity with an optional native read snapshot.</summary>
public sealed class PdfLogicalDocument
{
    /// <summary>Creates an owner with a new logical identity and no native read snapshot.</summary>
    public PdfLogicalDocument() { }

    /// <summary>Creates an independent owner for the supplied read snapshot.</summary>
    public PdfLogicalDocument(PdfReadSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ReadSnapshot = snapshot;
    }
    /// <summary>Gets this owner's identity, independently of source or layout identity.</summary>
    public Guid Id { get; } = Guid.NewGuid();
    /// <summary>Gets the explicitly retained read snapshot, if one was supplied.</summary>
    public PdfReadSnapshot? ReadSnapshot { get; }
}
