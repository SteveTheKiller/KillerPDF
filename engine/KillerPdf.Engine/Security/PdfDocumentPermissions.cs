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
    /// <summary>
    /// Whether the file's bits really forbid anything. ISO 32000-1 table 22 has bit 9 grant filling
    /// and bit 11 grant assembly "even if" bits 6 and 4 are clear, so with those set, clearing bit 9
    /// or bit 11 takes nothing away. Bit 10 is a right of its own.
    /// </summary>
    public bool DeclaresLimits => !(AllowLowQualityPrinting && AllowHighQualityPrinting
        && AllowContentCopying && AllowDocumentModification && AllowAnnotationModification
        && AllowAccessibilityExtraction);

    internal static PdfDocumentPermissions FromFlags(int flags, long revision)
    {
        bool Allowed(int bit) => (flags & (1 << (bit - 1))) != 0;
        bool extended = revision >= 3;
        // Below revision 3 bits 9 to 12 do not exist, and bits 6, 5 and 4 carry those rights.
        return new PdfDocumentPermissions(
            Allowed(3), Allowed(4), Allowed(5), Allowed(6),
            extended ? Allowed(9) : Allowed(6), extended ? Allowed(10) : Allowed(5),
            extended ? Allowed(11) : Allowed(4), Allowed(3) && (!extended || Allowed(12)));
    }
}
