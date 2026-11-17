using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Security;
using KillerPdf.Engine.Signing;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Writing;

public sealed class PdfDocumentEncryptionWriterTests
{
    [Fact]
    public void EncryptWithPasswords_OpensOnlyWithAPasswordAndKeepsTheText()
    {
        byte[] encrypted = PdfDocumentEncryptionWriter.EncryptWithPasswords(
            PdfDocument.Open(Source("password secret")), new PdfPasswordEncryptionOptions
            {
                UserPassword = "open",
                OwnerPassword = "owner",
                AllowContentCopying = false
            });

        Assert.Equal(-1, encrypted.AsSpan().IndexOf("password secret"u8));
        Assert.ThrowsAny<CryptographicException>(() => PdfDocument.Open(encrypted, "wrong"));
        PdfDocument user = PdfDocument.Open(encrypted, "open");
        Assert.True(user.IsDecrypted);
        Assert.False(user.DeclaredPermissions!.AllowContentCopying);
        Assert.Equal("password secret", new PdfPageContentReader(user).Read(0).Text);
        Assert.True(PdfDocument.Open(encrypted, "owner").IsDecrypted);
    }

    [Fact]
    public void EncryptForRecipients_OpensOnlyForAListedCertificate()
    {
        using X509Certificate2 allowed = Certificate("CN=Allowed rewrite recipient");
        using X509Certificate2 denied = Certificate("CN=Denied rewrite recipient");

        byte[] encrypted = PdfDocumentEncryptionWriter.EncryptForRecipients(
            PdfDocument.Open(Source("recipient secret")),
            new PdfCertificateEncryptionOptions { Recipients = [allowed] });

        Assert.Equal(-1, encrypted.AsSpan().IndexOf("recipient secret"u8));
        PdfDocument opened = PdfDocument.Open(encrypted, allowed);
        Assert.True(opened.IsDecrypted);
        Assert.Equal("recipient secret", new PdfPageContentReader(opened).Read(0).Text);
        Assert.Throws<CryptographicException>(() => PdfDocument.Open(encrypted, denied));
    }

    [Fact]
    public void Encrypt_ReplacesTheSourcePasswordWithTheNewProtection()
    {
        byte[] protectedSource = new PdfDocumentBuilder()
            .SetPasswordEncryption(new PdfPasswordEncryptionOptions { UserPassword = "old", OwnerPassword = "old-owner" })
            .AddPage(300, 400, Text("rotated keys"))
            .Build();

        byte[] encrypted = PdfDocumentEncryptionWriter.EncryptWithPasswords(
            PdfDocument.Open(protectedSource, "old-owner"),
            new PdfPasswordEncryptionOptions { UserPassword = "new", OwnerPassword = "new-owner" });

        Assert.Equal("rotated keys", new PdfPageContentReader(PdfDocument.Open(encrypted, "new")).Read(0).Text);
        Assert.ThrowsAny<CryptographicException>(() => PdfDocument.Open(encrypted, "old"));
    }

    [Fact]
    public void Encrypt_KeepsThePermanentIdentifierAndOpensWithAnEmptyUserPassword()
    {
        byte[] source = Source("identifiers");

        byte[] encrypted = PdfDocumentEncryptionWriter.EncryptWithPasswords(PdfDocument.Open(source),
            new PdfPasswordEncryptionOptions { UserPassword = "", OwnerPassword = "owner" });
        byte[] again = PdfDocumentEncryptionWriter.EncryptWithPasswords(PdfDocument.Open(source),
            new PdfPasswordEncryptionOptions { UserPassword = "", OwnerPassword = "owner" });

        PdfDocument opened = PdfDocument.Open(encrypted, "");
        Assert.True(opened.IsEncrypted);
        Assert.True(opened.IsDecrypted);
        Assert.Equal("identifiers", new PdfPageContentReader(opened).Read(0).Text);
        // The first identifier stays so links to the document resolve; the second is new each time.
        Assert.Equal(FirstIdentifier(source), FirstIdentifier(encrypted));
        Assert.NotEqual(Identifiers(encrypted), Identifiers(again));
    }

    [Fact]
    public void Encrypt_GivesADocumentWithoutAnIdentifierARandomOne()
    {
        // Renaming the trailer key in place keeps every offset, and leaves the file with no /ID.
        byte[] source = System.Text.Encoding.Latin1.GetBytes(
            System.Text.Encoding.Latin1.GetString(Source("no identifier")).Replace("/ID", "/XX", StringComparison.Ordinal));
        var options = new PdfPasswordEncryptionOptions { UserPassword = "", OwnerPassword = "owner" };
        byte[] first = PdfDocumentEncryptionWriter.EncryptWithPasswords(PdfDocument.Open(source), options);
        byte[] second = PdfDocumentEncryptionWriter.EncryptWithPasswords(PdfDocument.Open(source), options);
        Assert.NotEqual(FirstIdentifier(first), FirstIdentifier(second));
    }

    private static string FirstIdentifier(byte[] pdf)
    {
        string ids = Identifiers(pdf);
        return ids[..ids.IndexOf('>')];
    }

    [Fact]
    public void Encrypt_RefusesUnsafePasswordsAndMissingRecipients()
    {
        PdfDocument document = PdfDocument.Open(Source("refusals"));

        Assert.Throws<ArgumentException>(() => PdfDocumentEncryptionWriter.EncryptWithPasswords(document,
            new PdfPasswordEncryptionOptions { UserPassword = "", OwnerPassword = "" }));
        Assert.Throws<ArgumentException>(() => PdfDocumentEncryptionWriter.EncryptWithPasswords(document,
            new PdfPasswordEncryptionOptions { UserPassword = "open", OwnerPassword = "" }));
        Assert.Throws<ArgumentException>(() => PdfDocumentEncryptionWriter.EncryptForRecipients(document,
            new PdfCertificateEncryptionOptions { Recipients = [] }));
    }

    [Fact]
    public void Encrypt_RefusesASignedDocumentUnlessAllowed()
    {
        byte[] signed = PdfDetachedSignatureWriter.Sign(PdfDocument.Open(Source("signed")), _ => [1],
            new PdfSignatureOptions { ReservedSignatureSize = 16 });
        var options = new PdfPasswordEncryptionOptions { UserPassword = "a", OwnerPassword = "b" };

        Assert.Throws<InvalidOperationException>(() =>
            PdfDocumentEncryptionWriter.EncryptWithPasswords(PdfDocument.Open(signed), options));
        byte[] allowed = PdfDocumentEncryptionWriter.EncryptWithPasswords(
            PdfDocument.Open(signed), options, allowSignatureInvalidation: true);
        Assert.Equal("signed", new PdfPageContentReader(PdfDocument.Open(allowed, "a")).Read(0).Text);
    }

    private static string Identifiers(byte[] pdf)
    {
        string text = System.Text.Encoding.Latin1.GetString(pdf);
        int start = text.LastIndexOf("/ID", StringComparison.Ordinal);
        return text[start..text.IndexOf(']', start)];
    }

    private static byte[] Source(string text) =>
        new PdfDocumentBuilder().AddPage(300, 400, Text(text)).Build();

    private static PdfContentStreamBuilder Text(string text) =>
        new PdfContentStreamBuilder().BeginText().SetFont(PdfStandardFont.Helvetica, 12)
            .MoveText(20, 30).ShowLatin1Text(text).EndText();

    private static X509Certificate2 Certificate(string subject)
    {
        using RSA key = RSA.Create(2048);
        var request = new CertificateRequest(subject, key,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(30));
    }
}
