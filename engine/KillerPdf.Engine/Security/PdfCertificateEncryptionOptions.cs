using System.Security.Cryptography.X509Certificates;

namespace KillerPdf.Engine.Security;

/// <summary>Configures PDF 2.0 AES-256 encryption for certificate recipients.</summary>
public sealed class PdfCertificateEncryptionOptions
{
    /// <summary>Gets the certificates allowed to open the document.</summary>
    public required IReadOnlyList<X509Certificate2> Recipients { get; init; }
    /// <summary>Gets whether document metadata is encrypted.</summary>
    public bool EncryptMetadata { get; init; } = true;
    /// <summary>Gets whether low-resolution printing is permitted.</summary>
    public bool AllowLowQualityPrinting { get; init; } = true;
    /// <summary>Gets whether general document modification is permitted.</summary>
    public bool AllowDocumentModification { get; init; } = true;
    /// <summary>Gets whether copying or extracting content is permitted.</summary>
    public bool AllowContentCopying { get; init; } = true;
    /// <summary>Gets whether annotations may be added or changed.</summary>
    public bool AllowAnnotationModification { get; init; } = true;
    /// <summary>Gets whether form fields may be filled and signed.</summary>
    public bool AllowFormFilling { get; init; } = true;
    /// <summary>Gets whether content extraction for accessibility is permitted.</summary>
    public bool AllowAccessibilityExtraction { get; init; } = true;
    /// <summary>Gets whether page insertion, rotation, and assembly are permitted.</summary>
    public bool AllowDocumentAssembly { get; init; } = true;
    /// <summary>Gets whether high-resolution printing is permitted.</summary>
    public bool AllowHighQualityPrinting { get; init; } = true;
}
