namespace KillerPdf.Engine.ColorManagement;

/// <summary>Immutable requested viewing preferences for one logical document.</summary>
public sealed record PdfDocumentProofSettings
{
    /// <summary>Creates requested proof preferences without altering document content.</summary>
    public PdfDocumentProofSettings(bool enabled = false, PdfColorProfile? printerProfile = null,
        PdfProofOptions? options = null, bool gamutWarnings = false)
    {
        Enabled = enabled;
        PrinterProfile = printerProfile;
        Options = options ?? new PdfProofOptions();
        GamutWarnings = gamutWarnings;
    }

    /// <summary>Gets whether proofing is requested, independently of transform availability.</summary>
    public bool Enabled { get; }
    /// <summary>Gets the selected owned profile, if one is available.</summary>
    public PdfColorProfile? PrinterProfile { get; }
    /// <summary>Gets the requested conversion and simulation selections.</summary>
    public PdfProofOptions Options { get; }
    /// <summary>Gets whether the view requests printer-gamut warnings.</summary>
    public bool GamutWarnings { get; }
}
