using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using KillerPdf.Engine.Security;
using Xunit;

namespace KillerPdf.Engine.Tests.Security;

public sealed class PdfCertificateRecipientEncryptionTests
{
    [Fact]
    public void EachAuthorizedRecipientRecoversTheSameFileKeyAndPermissions()
    {
        using X509Certificate2 first = Certificate("CN=First recipient");
        using X509Certificate2 second = Certificate("CN=Second recipient");

        PdfCertificateRecipientMaterial created =
            PdfCertificateRecipientEncryption.Create([first, second], -44);
        PdfCertificateRecipientMaterial openedFirst =
            PdfCertificateRecipientEncryption.Open(created.RecipientBlocks, first);
        PdfCertificateRecipientMaterial openedSecond =
            PdfCertificateRecipientEncryption.Open(created.RecipientBlocks, second);

        Assert.Equal(32, created.FileKey.Length);
        Assert.Equal(2, created.RecipientBlocks.Count);
        Assert.Equal(created.FileKey.ToArray(), openedFirst.FileKey.ToArray());
        Assert.Equal(created.FileKey.ToArray(), openedSecond.FileKey.ToArray());
        Assert.Equal(-44, openedFirst.PermissionFlags);
        Assert.Equal(-44, openedSecond.PermissionFlags);
    }

    [Fact]
    public void UnlistedRecipientCannotRecoverMaterial()
    {
        using X509Certificate2 allowed = Certificate("CN=Allowed recipient");
        using X509Certificate2 denied = Certificate("CN=Denied recipient");
        PdfCertificateRecipientMaterial material =
            PdfCertificateRecipientEncryption.Create([allowed], -4);

        Assert.Throws<CryptographicException>(() =>
            PdfCertificateRecipientEncryption.Open(material.RecipientBlocks, denied));
        Assert.Throws<ArgumentException>(() =>
            PdfCertificateRecipientEncryption.Create([], -4));
        Assert.Throws<ArgumentException>(() =>
            PdfCertificateRecipientEncryption.Create([allowed, allowed], -4));
    }

    private static X509Certificate2 Certificate(string subject)
    {
        using RSA key = RSA.Create(2048);
        var request = new CertificateRequest(subject, key,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(30));
    }
}
