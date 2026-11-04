using KillerPdf.Engine.ColorManagement;

namespace KillerPdf.Engine.Documents;

/// <summary>Owns a logical document identity with an optional native read snapshot.</summary>
public sealed class PdfLogicalDocument
{
    private readonly Lock _proofLock = new();
    private PdfDocumentProofSettings _proof = new();
    private long _proofGeneration;

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
    /// <summary>Creates an independent identity retaining the read snapshot and proof preferences.</summary>
    public PdfLogicalDocument CreateIndependentCopy()
    {
        var copy = ReadSnapshot is { } snapshot
            ? new PdfLogicalDocument(snapshot) : new PdfLogicalDocument();
        copy._proof = Proof;
        return copy;
    }

    /// <summary>Gets or sets the requested proof preferences owned by this logical document.</summary>
    public PdfDocumentProofSettings Proof
    {
        get { lock (_proofLock) return _proof; }
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_proofLock)
            {
                if (_proof.Enabled == value.Enabled && _proof.GamutWarnings == value.GamutWarnings
                    && _proof.Options == value.Options
                    && _proof.PrinterProfile?.ContentSha256 == value.PrinterProfile?.ContentSha256) return;
                long generation = checked(_proofGeneration + 1);
                _proof = value;
                _proofGeneration = generation;
            }
        }
    }
    /// <summary>Gets the version of the requested proof preferences.</summary>
    public long ProofGeneration { get { lock (_proofLock) return _proofGeneration; } }

    /// <summary>Captures matching immutable preferences and generation for one render request.</summary>
    public (PdfDocumentProofSettings Settings, long Generation) CaptureProof()
    {
        lock (_proofLock) return (_proof, _proofGeneration);
    }
}
