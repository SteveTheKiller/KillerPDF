using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Security;
using Xunit;

namespace KillerPdf.Engine.Tests.Security;

public sealed class PdfDeclaredPermissionsTests
{
    private static byte[] Protected(bool lowPrint, bool highPrint) => new PdfDocumentBuilder()
        .SetPasswordEncryption(new PdfPasswordEncryptionOptions
        {
            UserPassword = "user-password",
            OwnerPassword = "owner-password",
            AllowLowQualityPrinting = lowPrint,
            AllowHighQualityPrinting = highPrint
        })
        .AddPage(612, 792, "q 0 0 10 10 re f Q"u8.ToArray())
        .Build();

    // The permissions sit in the clear in the encryption dictionary, so a viewer that has not been
    // given a password can still tell whether the file may be printed.
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void PrintingIsReadableWithoutAnyPassword(bool lowPrint, bool highPrint)
    {
        PdfDocument document = PdfDocument.Open(Protected(lowPrint, highPrint));

        PdfDocumentPermissions? permissions = document.ReadDeclaredPermissionsWithoutPassword();

        Assert.NotNull(permissions);
        Assert.Equal(lowPrint, permissions.AllowLowQualityPrinting);
        Assert.Equal(highPrint, permissions.AllowHighQualityPrinting);
        Assert.Null(document.DeclaredPermissions);
    }

    [Fact]
    public void ADocumentWithoutEncryptionDeclaresNoPermissions()
    {
        byte[] source = new PdfDocumentBuilder().AddBlankPage().Build();

        Assert.Null(PdfDocument.Open(source).ReadDeclaredPermissionsWithoutPassword());
    }

    // Revision 2 has one print bit and no separate high quality bit.
    [Fact]
    public void ARevisionTwoDocumentThatMayPrintMayPrintAtFullQuality()
    {
        PdfDocumentPermissions permissions = PdfDocumentPermissions.FromFlags(~0 & ~(1 << 11), revision: 2);

        Assert.True(permissions.AllowLowQualityPrinting);
        Assert.True(permissions.AllowHighQualityPrinting);
    }

    // R3-006: below revision 3 bits 9 to 11 do not exist. ISO 32000-1 table 22 gives filling to bit 6,
    // extraction for accessibility to bit 5 and assembly to bit 4 on those files.
    [Fact]
    public void ARevisionTwoFileReadsTheThreeLaterRightsFromTheBitsItHas()
    {
        PdfDocumentPermissions all = PdfDocumentPermissions.FromFlags(-4, revision: 2);

        Assert.True(all.AllowFormFilling);
        Assert.True(all.AllowAccessibilityExtraction);
        Assert.True(all.AllowDocumentAssembly);
    }

    [Theory]
    [InlineData(-36, false, true, true)]    // bit 6 clear
    [InlineData(-20, true, false, true)]    // bit 5 clear
    [InlineData(-12, true, true, false)]    // bit 4 clear
    public void ARevisionTwoRightFollowsTheBitThatGrantsIt(int flags, bool fill, bool accessibility, bool assembly)
    {
        PdfDocumentPermissions permissions = PdfDocumentPermissions.FromFlags(flags, revision: 2);

        Assert.Equal(fill, permissions.AllowFormFilling);
        Assert.Equal(accessibility, permissions.AllowAccessibilityExtraction);
        Assert.Equal(assembly, permissions.AllowDocumentAssembly);
    }

    // Bits 9 and 11 add a right on top of bits 6 and 4, so clearing one alone forbids nothing. Bit 10
    // names extraction that bit 5 leaves out, so clearing it is a limit of its own.
    [Theory]
    [InlineData(-4, 2, false)]
    [InlineData(-4, 3, false)]
    [InlineData(-260, 3, false)]    // bit 9 clear, bit 6 still grants filling
    [InlineData(-1028, 3, false)]   // bit 11 clear, bit 4 still grants assembly
    [InlineData(-516, 3, true)]     // bit 10 clear: no extraction for accessibility
    [InlineData(-8, 3, true)]       // bit 3 clear: no printing
    [InlineData(-2052, 3, true)]    // bit 12 clear: low quality printing only
    [InlineData(-12, 3, true)]      // bit 4 clear: no editing
    [InlineData(-20, 3, true)]      // bit 5 clear: no copying
    [InlineData(-36, 3, true)]      // bit 6 clear: no comments
    [InlineData(-8, 2, true)]
    [InlineData(-12, 2, true)]
    public void ADeclaredLimitIsWhatTheBitsReallyForbid(int flags, long revision, bool limits) =>
        Assert.Equal(limits, PdfDocumentPermissions.FromFlags(flags, revision).DeclaresLimits);
}
