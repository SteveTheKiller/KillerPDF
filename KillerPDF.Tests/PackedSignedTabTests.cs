using System.IO;
using KillerPDF.Services;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Security;
using KillerPdf.Engine.Signing;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPDF.Tests;

// A signed PDF that needs a password, written the way Acrobat writes one: its page tree sits in an
// encrypted object stream, so even the page sizes the form overlay, Rasterize Region and the page
// comparison read need the tab's key.
public sealed class PackedSignedTabTests : IDisposable
{
    private const string Owner = "owner-password";
    private const string Reader = "reader";
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "KillerPDF-packed-" + Guid.NewGuid().ToString("N"));

    public PackedSignedTabTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, true);

    private string PackedSigned()
    {
        byte[] encrypted = new PdfDocumentBuilder()
            .SetPasswordEncryption(new PdfPasswordEncryptionOptions
            {
                UserPassword = Reader,
                OwnerPassword = Owner,
                AllowDocumentModification = false,
            })
            .AddPage(300, 200, "q 0 0 50 50 re f Q"u8.ToArray())
            .Build();
        byte[] packed = PdfDocumentWriter.Write(PdfDocument.Open(encrypted, Owner), new PdfDocumentWriteOptions
        {
            CrossReferenceFormat = PdfCrossReferenceFormat.Stream,
            UseObjectStreams = true,
        });
        string path = Path.Combine(_folder, "packed-signed.pdf");
        File.WriteAllBytes(path, PdfDetachedSignatureWriter.Sign(PdfDocument.Open(packed, Owner),
            _ => new byte[64], new PdfSignatureOptions { FieldName = "Signature1" }));
        return path;
    }

    [Fact]
    public void TheTabReadsItsPageSizesUnderItsKey()
    {
        string path = PackedSigned();
        // The fixture is what it claims: encrypted, needing the user password, and signed.
        Assert.Equal(PdfAccessRole.Refused, PdfRestrictions.Read(path, null).Role);
        Assert.Equal(PdfAccessRole.Owner, PdfRestrictions.Read(path, Owner).Role);
        Assert.True(PdfEngineIntegration.HasSignedSignatures(path, Reader));
        OpenedProtection tab = OpenedProtection.Capture(path, Reader);
        Assert.Equal(PdfAccessRole.User, tab.Access.Role);

        IReadOnlyList<PdfPageInformation>? pages = null;
        Exception? failure = Record.Exception(() => pages = PdfEngineIntegration.ReadPageInformation(path, tab));

        Assert.Null(failure);
        PdfPageInformation page = Assert.Single(pages!);
        Assert.Equal(300, page.Width, 3);
        Assert.Equal(200, page.Height, 3);
        // The tab's own open, its render and its signature check read the packed file the same way.
        using (PdfWorkingDocument working = PdfWorkingDocument.Open(path, tab, isReadOnly: true))
            Assert.Equal(1, working.PageCount);
        Assert.True(PdfPageRenderSession.OpenDocument(path, tab).CanReadPageContent);
        Assert.True(PdfEngineIntegration.HasSignedSignatures(path, tab));
    }
}
