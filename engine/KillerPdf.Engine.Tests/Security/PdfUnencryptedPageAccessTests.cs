using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Security;
using KillerPdf.Engine.Writing;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace KillerPdf.Engine.Tests.Security;

public sealed class PdfUnencryptedPageAccessTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AttachmentOnlyEncryption_AllowsPageRenderingWithoutAuthenticating(bool explicitIdentity)
    {
        (byte[] bytes, PdfIndirectReference protectedStream) = Fixture(explicitIdentity);
        PdfDocument document = PdfDocument.Open(bytes);
        Assert.True(document.CanReadPageContent);
        Assert.True(document.IsEncrypted);
        Assert.False(document.IsDecrypted);
        Assert.Equal(PdfPasswordAuthenticationRole.None, document.PasswordAuthenticationRole);
        Assert.Null(document.DeclaredPermissions);

        PdfRenderedPage page = new PdfPageRenderer(document).Render(0,
            new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, page.Pixels.Span.Slice(220, 4).ToArray());
        Assert.Empty(page.Diagnostics);
        Assert.ThrowsAny<CryptographicException>(() => document.Resolve(protectedStream));
        Assert.Throws<InvalidOperationException>(() => new PdfIncrementalUpdateBuilder(document));
        Assert.Throws<InvalidOperationException>(() => PdfDocumentWriter.Write(document));
        Assert.Throws<InvalidOperationException>(() => PdfAttachmentReader.Read(document));

        PdfDocument authenticated = PdfDocument.Open(bytes, "owner");
        Assert.True(authenticated.IsDecrypted);
        Assert.Equal("protected attachment", Encoding.ASCII.GetString(
            Assert.IsType<PdfStream>(authenticated.Resolve(protectedStream)).EncodedData.Span));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AttachmentOnlyEncryption_ProtectsPreviouslyResolvedStreams(bool explicitCrypt)
    {
        (byte[] bytes, PdfIndirectReference protectedStream) = Fixture(false, explicitCrypt);
        PdfDocument document = PdfDocument.Open(bytes);
        PdfStream cached = Assert.IsType<PdfStream>(document.Resolve(protectedStream));
        Assert.True(document.CanReadPageContent);
        Assert.ThrowsAny<CryptographicException>(() => document.Resolve(protectedStream));
        Assert.ThrowsAny<CryptographicException>(() => document.DecodeStream(cached));
        Assert.ThrowsAny<CryptographicException>(() => document.DecodeJpegImage(cached, 1024, 1));
    }

    [Fact]
    public void OrdinaryPasswordEncryption_StillRequiresAuthentication()
    {
        byte[] bytes = new PdfDocumentBuilder().AddBlankPage()
            .SetPasswordEncryption(new PdfPasswordEncryptionOptions
            { UserPassword = "user", OwnerPassword = "owner" }).Build();
        PdfDocument document = PdfDocument.Open(bytes);
        Assert.False(document.CanReadPageContent);
        Assert.Throws<InvalidOperationException>(() => new PdfPageRenderer(document));
        Assert.Throws<InvalidOperationException>(() => new PdfPageContentReader(document));
    }

    private static (byte[], PdfIndirectReference) Fixture(bool explicitIdentity,
        bool explicitProtectedCrypt = false)
    {
        byte[] bytes = new PdfDocumentBuilder().AddBlankPage(10, 10)
            .SetPasswordEncryption(new PdfPasswordEncryptionOptions
            { UserPassword = "user", OwnerPassword = "owner" }).Build();
        PdfDocument document = PdfDocument.Open(bytes, "owner");
        PdfIndirectReference encryptionReference = Assert.IsType<PdfIndirectReference>(
            document.Trailer[Name("Encrypt")]);
        PdfDictionary encryption = Assert.IsType<PdfDictionary>(document.Resolve(encryptionReference));
        PdfDictionary filters = Assert.IsType<PdfDictionary>(encryption[Name("CF")]);
        PdfDictionary standardFilter = Assert.IsType<PdfDictionary>(filters[Name("StdCF")]);
        var attachmentFilter = new PdfDictionary(standardFilter
            .Where(entry => !entry.Key.Equals(Name("AuthEvent")))
            .Append(new KeyValuePair<PdfName, PdfObject>(Name("AuthEvent"), Name("EFOpen"))));
        var attachmentFilters = new PdfDictionary(filters.Select(entry =>
            entry.Key.Equals(Name("StdCF"))
                ? new KeyValuePair<PdfName, PdfObject>(entry.Key, attachmentFilter) : entry));
        var identityDefaults = new PdfDictionary(encryption.Select(entry =>
            entry.Key.Equals(Name("StmF")) || entry.Key.Equals(Name("StrF"))
                ? new KeyValuePair<PdfName, PdfObject>(entry.Key, Name("Identity"))
                : entry.Key.Equals(Name("CF"))
                    ? new KeyValuePair<PdfName, PdfObject>(entry.Key, attachmentFilters) : entry));
        bytes = new PdfIncrementalUpdateBuilder(document)
            .ReplaceObject(encryptionReference.ObjectNumber, identityDefaults).Build();
        document = PdfDocument.Open(bytes, "owner");
        var update = new PdfIncrementalUpdateBuilder(document);
        PdfDictionary page = PdfPageTree.Read(document).Pages[0].Dictionary;
        PdfIndirectReference pageReference = PdfPageTree.Read(document).Pages[0].Reference;
        var contentDictionary = explicitIdentity ? Crypt("Identity") : new PdfDictionary([]);
        PdfIndirectReference content = update.AddObject(new PdfStream(contentDictionary,
            "1 0 0 rg 0 0 10 10 re f"u8));
        update.ReplaceObject(pageReference.ObjectNumber, new PdfDictionary(page
            .Where(entry => !entry.Key.Equals(Name("Contents")))
            .Append(new KeyValuePair<PdfName, PdfObject>(Name("Contents"), content))));
        PdfDictionary protectedDictionary = explicitProtectedCrypt ? Crypt("StdCF")
            : new PdfDictionary([new(Name("Type"), Name("EmbeddedFile"))]);
        PdfIndirectReference protectedStream = update.AddObject(new PdfStream(
            protectedDictionary, "protected attachment"u8));
        return (update.Build(), protectedStream);
    }

    private static PdfDictionary Crypt(string name) => new([
        new(Name("Filter"), Name("Crypt")),
        new(Name("DecodeParms"), new PdfDictionary([new(Name("Name"), Name(name))]))]);
    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
}
