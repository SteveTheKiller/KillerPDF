using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Security;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class ProtectDocumentIntegrationTests
{
    [Fact]
    public void ProtectWithPasswords_WritesARestrictedCopyAndLeavesTheSourceUnchanged()
    {
        string source = TempPdf("protect-source");
        string output = TempPdf("protect-output");
        try
        {
            File.WriteAllBytes(source, Source("password copy"));
            byte[] before = File.ReadAllBytes(source);

            PdfEngineIntegration.ProtectWithPasswords(source, output, new PdfPasswordEncryptionOptions
            {
                UserPassword = "open",
                OwnerPassword = "owner",
                AllowContentCopying = false
            }, allowSignatureInvalidation: false);

            Assert.Equal(before, File.ReadAllBytes(source));
            byte[] protectedCopy = File.ReadAllBytes(output);
            Assert.ThrowsAny<CryptographicException>(() => PdfDocument.Open(protectedCopy, "wrong"));
            PdfDocument opened = PdfDocument.Open(protectedCopy, "open");
            Assert.False(opened.DeclaredPermissions!.AllowContentCopying);
            Assert.Equal("password copy", new PdfPageContentReader(opened).Read(0).Text);
            Assert.False(PdfEngineIntegration.IsCertificateEncrypted(output));
        }
        finally
        {
            File.Delete(source);
            File.Delete(output);
        }
    }

    [Fact]
    public void ProtectForRecipients_OpensOnlyWithARecipientsPrivateKey()
    {
        string source = TempPdf("recipient-source");
        string output = TempPdf("recipient-output");
        string opened = TempPdf("recipient-opened");
        using X509Certificate2 allowed = Certificate("CN=KillerPDF allowed recipient");
        using X509Certificate2 denied = Certificate("CN=KillerPDF denied recipient");
        try
        {
            File.WriteAllBytes(source, Source("recipient copy"));
            Assert.False(PdfEngineIntegration.IsCertificateEncrypted(source));

            PdfEngineIntegration.ProtectForRecipients(source, output,
                new PdfCertificateEncryptionOptions { Recipients = [allowed] },
                allowSignatureInvalidation: false);

            Assert.True(PdfEngineIntegration.IsCertificateEncrypted(output));
            Assert.False(PdfEngineIntegration.TryRemoveCertificateEncryption(output, opened, [denied], out _));
            Assert.False(File.Exists(opened) && new FileInfo(opened).Length > 0);
            Assert.True(PdfEngineIntegration.TryRemoveCertificateEncryption(output, opened, [denied, allowed], out PdfAccess access));
            Assert.True(access.MayModify);
            PdfDocument decrypted = PdfDocument.Open(File.ReadAllBytes(opened));
            Assert.False(decrypted.IsEncrypted);
            Assert.Equal("recipient copy", new PdfPageContentReader(decrypted).Read(0).Text);
        }
        finally
        {
            File.Delete(source);
            File.Delete(output);
            File.Delete(opened);
        }
    }

    // Each recipient carries the permissions its author sealed for it. The copy KillerPDF reads from
    // is decrypted either way; a recipient that may not edit makes the app open it read-only.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ARecipientsSealedPermissionsDecideWhetherItOpensEditable(bool allowModification)
    {
        string source = TempPdf("recipient-perm-source");
        string output = TempPdf("recipient-perm-output");
        string opened = TempPdf("recipient-perm-opened");
        using X509Certificate2 recipient = Certificate("CN=KillerPDF restricted recipient");
        try
        {
            File.WriteAllBytes(source, Source("restricted recipient copy"));
            PdfEngineIntegration.ProtectForRecipients(source, output,
                new PdfCertificateEncryptionOptions { Recipients = [recipient], AllowDocumentModification = allowModification },
                allowSignatureInvalidation: false);

            Assert.True(PdfEngineIntegration.TryRemoveCertificateEncryption(output, opened, [recipient], out PdfAccess access));

            Assert.Equal(allowModification, access.MayModify);
            Assert.Equal("restricted recipient copy",
                new PdfPageContentReader(PdfDocument.Open(File.ReadAllBytes(opened))).Read(0).Text);
        }
        finally
        {
            File.Delete(source);
            File.Delete(output);
            File.Delete(opened);
        }
    }

    [Fact]
    public void RecipientCertificatesNeedAnRsaKeyThatMayEncryptKeys()
    {
        using X509Certificate2 open = Certificate("CN=No key usage");
        using X509Certificate2 signingOnly = Certificate("CN=Signing only", X509KeyUsageFlags.DigitalSignature);
        using X509Certificate2 encipherment = Certificate("CN=Key encipherment", X509KeyUsageFlags.KeyEncipherment);
        using ECDsa curve = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using X509Certificate2 elliptic = new CertificateRequest("CN=Elliptic", curve, HashAlgorithmName.SHA256)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));

        Assert.True(PdfEngineIntegration.CanEncryptForRecipient(open));
        Assert.True(PdfEngineIntegration.CanEncryptForRecipient(encipherment));
        Assert.False(PdfEngineIntegration.CanEncryptForRecipient(signingOnly));
        Assert.False(PdfEngineIntegration.CanEncryptForRecipient(elliptic));
    }

    [Fact]
    public void ProtectDocumentIsReachableFromTheKeyboardAndTheSaveMenu()
    {
        string root = RepoRoot();
        string keyboard = File.ReadAllText(Path.Combine(root, "Shell", "KeyboardShortcuts.cs"));
        string files = File.ReadAllText(Path.Combine(root, "Shell", "FileOperations.cs"));

        Assert.Contains("if (!e.IsRepeat) OpenProtectDialog();", keyboard, StringComparison.Ordinal);
        Assert.Contains("(_, _) => OpenProtectDialog(), GestureFor(\"CtrlShift:P\")", files, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Strings", "en-US.xaml")))
            dir = dir.Parent;
        Assert.True(dir != null, "could not locate the repo root from the test output folder");
        return dir!.FullName;
    }

    private static string TempPdf(string name) =>
        Path.Combine(Path.GetTempPath(), $"killerpdf-{name}-{Guid.NewGuid():N}.pdf");

    private static byte[] Source(string text) =>
        new PdfDocumentBuilder().AddPage(300, 400, new PdfContentStreamBuilder().BeginText()
            .SetFont(PdfStandardFont.Helvetica, 12).MoveText(20, 30).ShowLatin1Text(text).EndText()).Build();

    private static X509Certificate2 Certificate(string subject, X509KeyUsageFlags? usage = null)
    {
        using RSA key = RSA.Create(2048);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        if (usage is { } flags) request.CertificateExtensions.Add(new X509KeyUsageExtension(flags, critical: true));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    }
}
