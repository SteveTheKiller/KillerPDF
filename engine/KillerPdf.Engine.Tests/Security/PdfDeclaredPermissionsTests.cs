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
}
