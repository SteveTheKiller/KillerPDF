namespace KillerPdf.Engine.Security;

/// <summary>The operations allowed by a Standard Security handler's declared permissions.</summary>
public sealed record PdfDocumentPermissions(
    bool AllowLowQualityPrinting,
    bool AllowDocumentModification,
    bool AllowContentCopying,
    bool AllowAnnotationModification,
    bool AllowFormFilling,
    bool AllowAccessibilityExtraction,
    bool AllowDocumentAssembly,
    bool AllowHighQualityPrinting)
{
    internal static PdfDocumentPermissions FromFlags(int flags, long revision)
    {
        bool Allowed(int bit) => (flags & (1 << (bit - 1))) != 0;
        bool extended = revision >= 3;
        return new PdfDocumentPermissions(
            Allowed(3), Allowed(4), Allowed(5), Allowed(6),
            extended && Allowed(9), extended && Allowed(10),
            extended && Allowed(11), Allowed(3) && (!extended || Allowed(12)));
    }
}
