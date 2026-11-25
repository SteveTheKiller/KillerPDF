using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Signing;
using Xunit;

namespace KillerPdf.Engine.Tests.Signing;

public sealed class PdfSignerUsageTests
{
    [Theory]
    [InlineData(null, true)]                              // no EKU extension: any use
    [InlineData("1.3.6.1.5.5.7.3.4", true)]               // email protection
    [InlineData("1.3.6.1.5.5.7.3.36", true)]              // document signing
    [InlineData("1.3.6.1.4.1.311.10.3.12", true)]         // Microsoft document signing
    [InlineData("1.2.840.113583.1.1.5", true)]            // Adobe Authentic Documents
    [InlineData("1.3.6.1.5.5.7.3.3", true)]               // code signing
    [InlineData("2.5.29.37.0", true)]                     // any extended usage
    [InlineData("1.3.6.1.5.5.7.3.1", false)]              // TLS server only
    [InlineData("1.3.6.1.5.5.7.3.8", false)]              // timestamping only
    public void SignerCertificateMustAllowDocumentSigning(string? usage, bool expectedTrusted)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        using RSA rootKey = RSA.Create(2048);
        using RSA leafKey = RSA.Create(2048);
        var rootRequest = new CertificateRequest("CN=Usage Root", rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        using X509Certificate2 root = rootRequest.CreateSelfSigned(now.AddDays(-2), now.AddDays(2));
        var leafRequest = new CertificateRequest("CN=Usage Signer", leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        leafRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        leafRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        if (usage is not null)
            leafRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(usage)], false));
        using X509Certificate2 publicLeaf = leafRequest.Create(root, now.AddHours(-1), now.AddHours(1),
            RandomNumberGenerator.GetBytes(16));
        using X509Certificate2 leaf = publicLeaf.CopyWithPrivateKey(leafKey);

        byte[] signed = PdfDetachedSignatureWriter.Sign(
            PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage().Build()), content =>
            {
                var cms = new SignedCms(new ContentInfo(content.ToArray()), detached: true);
                cms.ComputeSignature(new CmsSigner(leaf) { IncludeOption = X509IncludeOption.EndCertOnly });
                return cms.Encode();
            }, new PdfSignatureOptions { ReservedSignatureSize = 8192 });
        PdfDocument document = PdfDocument.Open(signed);
        PdfSignatureVerificationResult result = PdfSignatureVerifier.VerifyTrust(document,
            Assert.Single(PdfSignatureReader.Read(document)), new PdfSignatureTrustOptions
            {
                CustomTrustRoots = [root],
                DisableCertificateDownloads = true,
                RevocationMode = X509RevocationMode.NoCheck,
                VerificationTime = now.UtcDateTime
            });

        Assert.True(result.IsCryptographicallyValid);
        Assert.Equal(expectedTrusted, result.IsCertificateTrusted);
        if (!expectedTrusted)
            Assert.Contains(result.CertificateChainErrors, error => error.Contains("key usage", StringComparison.Ordinal));
    }
}
