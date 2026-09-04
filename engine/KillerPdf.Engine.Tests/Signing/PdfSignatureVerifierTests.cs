using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Signing;
using KillerPdf.Engine.Tests.Security;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Signing;

public sealed class PdfSignatureVerifierTests
{
    [Fact]
    public void VerifyIntegrity_AcceptsSignatureAddedToEncryptedDocument()
    {
        using RSA key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=KillerPDF Encrypted Signing Test", key, HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        using X509Certificate2 certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        PdfDocument source = PdfDocument.Open(
            PdfEncryptionTests.Revision6Fixture(), "owner-password");

        byte[] signed = PdfDetachedSignatureWriter.Sign(
            source, content => Sign(content, certificate),
            new PdfSignatureOptions
            {
                ReservedSignatureSize = 4_096,
                IncrementalWriteOptions = new PdfIncrementalUpdateWriteOptions
                {
                    CrossReferenceFormat = PdfCrossReferenceFormat.Stream,
                    CompressCrossReferenceStream = true,
                    UseObjectStreams = true,
                    CompressObjectStreams = true
                }
            });
        PdfDocument reopened = PdfDocument.Open(signed, "user-password");
        PdfSignatureVerificationResult result = PdfSignatureVerifier.VerifyIntegrity(
            reopened, Assert.Single(PdfSignatureReader.Read(reopened)));

        Assert.True(reopened.IsEncrypted);
        Assert.True(reopened.CrossReferences.Sections[0].IsStream);
        Assert.Contains(reopened.CrossReferences.Sections[0].Values,
            entry => entry.Type == KillerPdf.Engine.CrossReference.PdfCrossReferenceEntryType.Compressed);
        Assert.True(result.IsStructurallyValid);
        Assert.True(result.IsCryptographicallyValid);
        Assert.Null(result.Error);
    }

    [Fact]
    public void VerifyIntegrity_AcceptsValidDetachedCmsAndRejectsChangedSignedBytes()
    {
        using RSA key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=KillerPDF Verification Test", key, HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature, true));
        using X509Certificate2 certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        byte[] source = new PdfDocumentBuilder().AddBlankPage().Build();
        byte[] signed = PdfDetachedSignatureWriter.Sign(
            PdfDocument.Open(source), content => Sign(content, certificate),
            new PdfSignatureOptions { ReservedSignatureSize = 4_096 });
        PdfDocument document = PdfDocument.Open(signed);
        PdfSignatureInfo signature = Assert.Single(PdfSignatureReader.Read(document));

        PdfSignatureVerificationResult valid =
            PdfSignatureVerifier.VerifyIntegrity(document, signature);

        Assert.True(valid.IsStructurallyValid);
        Assert.True(valid.IsCryptographicallyValid);
        Assert.False(valid.CertificateTrustWasChecked);
        Assert.False(valid.IsCertificateTrusted);
        Assert.Equal("2.16.840.1.101.3.4.2.1", valid.DigestAlgorithmOid);
        Assert.NotNull(valid.SignatureAlgorithmOid);
        Assert.Equal(certificate.Subject, valid.SignerSubject);
        Assert.Equal(certificate.Issuer, valid.SignerIssuer);
        Assert.Equal(certificate.SerialNumber, valid.SignerSerialNumber);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(certificate.RawData)),
            valid.SignerCertificateSha256);
        Assert.Equal(certificate.NotBefore, valid.CertificateNotBefore);
        Assert.Equal(certificate.NotAfter, valid.CertificateNotAfter);
        Assert.Null(valid.Error);

        byte[] marker = Encoding.ASCII.GetBytes("/MediaBox [0 0 612 792]");
        int markerOffset = signed.AsSpan().IndexOf(marker);
        Assert.True(markerOffset >= 0);
        signed[markerOffset + marker.AsSpan().IndexOf("612"u8) + 2] = (byte)'3';
        PdfDocument changedDocument = PdfDocument.Open(signed);
        PdfSignatureInfo changedSignature = Assert.Single(
            PdfSignatureReader.Read(changedDocument));

        PdfSignatureVerificationResult changed =
            PdfSignatureVerifier.VerifyIntegrity(changedDocument, changedSignature);

        Assert.True(changed.IsStructurallyValid);
        Assert.False(changed.IsCryptographicallyValid);
        Assert.NotNull(changed.Error);
    }

    [Fact]
    public void VerifyTrust_SeparatesValidSignatureFromUntrustedCertificate()
    {
        using RSA key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=Untrusted KillerPDF Test", key, HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        using X509Certificate2 certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        byte[] signed = PdfDetachedSignatureWriter.Sign(
            PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage().Build()),
            content => Sign(content, certificate),
            new PdfSignatureOptions { ReservedSignatureSize = 4_096 });
        PdfDocument document = PdfDocument.Open(signed);

        PdfSignatureVerificationResult result = PdfSignatureVerifier.VerifyTrust(
            document, Assert.Single(PdfSignatureReader.Read(document)));

        Assert.True(result.IsStructurallyValid);
        Assert.True(result.IsCryptographicallyValid);
        Assert.True(result.CertificateTrustWasChecked);
        Assert.False(result.IsCertificateTrusted);
        Assert.Equal(PdfCertificateTrustStatus.Untrusted, result.CertificateTrustStatus);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void VerifyTrustAcceptsExplicitTrustRootAndSeparatesRevocationPolicy()
    {
        using RSA key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Explicit KillerPDF Trust", key,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using X509Certificate2 certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        byte[] signed = PdfDetachedSignatureWriter.Sign(
            PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage().Build()),
            content => Sign(content, certificate),
            new PdfSignatureOptions { ReservedSignatureSize = 4_096 });
        PdfDocument document = PdfDocument.Open(signed);

        PdfSignatureVerificationResult result = PdfSignatureVerifier.VerifyTrust(document,
            Assert.Single(PdfSignatureReader.Read(document)), new PdfSignatureTrustOptions
            {
                CustomTrustRoots = [certificate],
                RevocationMode = X509RevocationMode.NoCheck
            });

        Assert.True(result.IsCryptographicallyValid);
        Assert.True(result.IsCertificateTrusted);
        Assert.Equal(PdfCertificateTrustStatus.Trusted, result.CertificateTrustStatus);
        Assert.True(result.IsCertificateTimeValid);
        Assert.Equal(PdfCertificateRevocationStatus.NotChecked, result.RevocationStatus);
        Assert.Empty(result.CertificateChainErrors);
    }

    [Fact]
    public void InspectionReportCombinesSignatureTrustAndRevisionDetailsSafely()
    {
        using RSA key = RSA.Create(2048);
        var request = new CertificateRequest("CN=KillerPDF Inspection Test", key,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using X509Certificate2 certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        byte[] signed = PdfDetachedSignatureWriter.Sign(
            PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage().Build()),
            content => Sign(content, certificate),
            new PdfSignatureOptions
            {
                FieldName = "Approval",
                SignerName = "Ada",
                ReservedSignatureSize = 4_096
            });

        PdfSignatureInspectionReport report = PdfSignatureInspection.Inspect(
            PdfDocument.Open(signed), new PdfSignatureTrustOptions
            {
                CustomTrustRoots = [certificate],
                RevocationMode = X509RevocationMode.NoCheck
            });
        PdfSignatureInspectionEntry entry = Assert.Single(report.Entries);
        string json = report.ToJson();

        Assert.Equal("Approval", entry.Signature.FieldName);
        Assert.True(entry.Verification.IsCryptographicallyValid);
        Assert.Equal(PdfCertificateTrustStatus.Trusted,
            entry.Verification.CertificateTrustStatus);
        Assert.NotNull(entry.Revision);
        Assert.False(entry.Revision.HasLaterChanges);
        Assert.Contains("\"fieldName\":\"Approval\"", json);
        Assert.DoesNotContain("contents", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cms", json, StringComparison.OrdinalIgnoreCase);
    }

    private static byte[] Sign(ReadOnlyMemory<byte> content, X509Certificate2 certificate)
    {
        var cms = new SignedCms(new ContentInfo(content.ToArray()), detached: true);
        cms.ComputeSignature(new CmsSigner(certificate)
        {
            IncludeOption = X509IncludeOption.EndCertOnly,
            DigestAlgorithm = new Oid("2.16.840.1.101.3.4.2.1")
        });
        return cms.Encode();
    }
}
