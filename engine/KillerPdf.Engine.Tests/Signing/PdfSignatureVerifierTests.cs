using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Formats.Asn1;
using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
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
    public void VerifyIntegrityReportsCadesSignaturePolicyIdentifier()
    {
        using RSA key = RSA.Create(2048);
        var request = new CertificateRequest("CN=KillerPDF Policy Test", key,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using X509Certificate2 certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        const string policyOid = "1.2.3.4.5.6";
        byte[] signed = PdfDetachedSignatureWriter.Sign(
            PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage().Build()),
            content => Sign(content, certificate, policyOid),
            new PdfSignatureOptions { ReservedSignatureSize = 4_096 });

        PdfSignatureInspectionReport report = PdfSignatureInspection.Inspect(
            PdfDocument.Open(signed));

        Assert.Equal([policyOid], Assert.Single(report.Entries)
            .Verification.SignaturePolicyOids);
        Assert.Contains($"Signature policy: {policyOid}", report.ToText());
        Assert.Contains($"\"signaturePolicyOids\":[\"{policyOid}\"]", report.ToJson());
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
        DateTime verificationTime = DateTime.Now.AddMinutes(-5);

        PdfSignatureVerificationResult result = PdfSignatureVerifier.VerifyTrust(document,
            Assert.Single(PdfSignatureReader.Read(document)), new PdfSignatureTrustOptions
            {
                CustomTrustRoots = [certificate],
                RevocationMode = X509RevocationMode.NoCheck,
                VerificationTime = verificationTime,
                DisableCertificateDownloads = true
            });

        Assert.True(result.IsCryptographicallyValid);
        Assert.True(result.IsCertificateTrusted);
        Assert.Equal(PdfCertificateTrustStatus.Trusted, result.CertificateTrustStatus);
        Assert.True(result.IsCertificateTimeValid);
        Assert.Equal(PdfCertificateRevocationStatus.NotChecked, result.RevocationStatus);
        Assert.Equal(X509RevocationMode.NoCheck, result.RequestedRevocationMode);
        Assert.Equal(verificationTime, result.RequestedVerificationTime);
        Assert.True(result.CertificateDownloadsDisabled);
        Assert.Empty(result.CertificateChainErrors);
        PdfCertificateChainElement chainCertificate = Assert.Single(result.CertificateChain);
        Assert.Equal(certificate.Subject, chainCertificate.Subject);
        Assert.Equal(certificate.Issuer, chainCertificate.Issuer);
        Assert.Equal(certificate.SerialNumber, chainCertificate.SerialNumber);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(certificate.RawData)),
            chainCertificate.Sha256Fingerprint);
        Assert.Empty(chainCertificate.StatusMessages);
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
                RevocationMode = X509RevocationMode.NoCheck,
                DisableCertificateDownloads = true
            });
        PdfSignatureInspectionEntry entry = Assert.Single(report.Entries);
        string json = report.ToJson();

        Assert.Equal("Approval", entry.Signature.FieldName);
        Assert.True(entry.Verification.IsCryptographicallyValid);
        Assert.Equal(PdfCertificateTrustStatus.Trusted,
            entry.Verification.CertificateTrustStatus);
        Assert.NotNull(entry.Revision);
        Assert.False(entry.Revision.HasLaterChanges);
        Assert.Equal(PdfPadesProfile.BaselineB, entry.PadesProfile);
        Assert.Contains("\"fieldName\":\"Approval\"", json);
        Assert.Contains("\"padesProfile\":1", json);
        Assert.Contains("\"certificateChain\":[{", json);
        Assert.DoesNotContain("contents", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cms", json, StringComparison.OrdinalIgnoreCase);
        string text = report.ToText();
        Assert.Contains("Approval", text);
        Assert.Contains("Cryptographic integrity: Valid", text);
        Assert.Contains("Certificate trust: Trusted", text);
        Assert.Contains("Revocation: NotChecked", text);
        Assert.Contains("Requested revocation mode: NoCheck", text);
        Assert.Contains("Requested verification time: ", text);
        Assert.Contains("Network certificate retrieval: Disabled", text);
        Assert.Contains("PAdES evidence: BaselineB", text);
        Assert.Contains($"Signer subject: {certificate.Subject}", text);
        Assert.Contains($"Signer issuer: {certificate.Issuer}", text);
        Assert.Contains("Certificate SHA-256: ", text);
        Assert.Contains("Digest algorithm: 2.16.840.1.101.3.4.2.1", text);
        Assert.Contains("Signature algorithm: ", text);
        Assert.Contains("Certificate valid from: ", text);
        Assert.Contains("Certificate valid until: ", text);
        Assert.Contains("Certificate time validity: Valid", text);
        Assert.Contains($"Chain certificate 1: {certificate.Subject}", text);
        Assert.Contains("    SHA-256: ", text);
        Assert.Contains("Later changes: No", text);
    }

    [Fact]
    public void ValidationDataWriterAppendsDssWithoutChangingSignedBytes()
    {
        using RSA key = RSA.Create(2048);
        var request = new CertificateRequest("CN=KillerPDF LTV Test", key,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using X509Certificate2 certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        byte[] signed = PdfDetachedSignatureWriter.Sign(
            PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage().Build()),
            content => Sign(content, certificate),
            new PdfSignatureOptions { FieldName = "Approval", ReservedSignatureSize = 4_096 });
        PdfDocument document = PdfDocument.Open(signed);
        PdfSignatureInfo signature = Assert.Single(PdfSignatureReader.Read(document));

        byte[] embedded = PdfPadesValidationDataWriter.Embed(document, signature,
            new PdfPadesValidationData
            {
                Certificates = [certificate.RawData],
                CertificateRevocationLists = [new byte[] { 0x30, 0x00 }],
                ValidationTime = new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero)
            });
        PdfDocument reopened = PdfDocument.Open(embedded);
        PdfSignatureInfo reopenedSignature = Assert.Single(PdfSignatureReader.Read(reopened));
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(reopened.Resolve(
            Assert.IsType<PdfIndirectReference>(reopened.Trailer[new PdfName("Root"u8)])));
        PdfDictionary dss = Assert.IsType<PdfDictionary>(reopened.Resolve(
            Assert.IsType<PdfIndirectReference>(catalog[new PdfName("DSS"u8)])));
        PdfPadesValidationEvidence evidence = Assert.IsType<PdfPadesValidationEvidence>(
            PdfPadesValidationDataReader.Read(reopened, reopenedSignature));

        Assert.True(PdfSignatureVerifier.VerifyIntegrity(reopened, reopenedSignature)
            .IsCryptographicallyValid);
        Assert.Equal(signed, embedded.AsSpan(0, signed.Length).ToArray());
        Assert.Single(Assert.IsType<PdfArray>(dss[new PdfName("Certs"u8)]));
        Assert.Single(Assert.IsType<PdfArray>(dss[new PdfName("CRLs"u8)]));
        PdfDictionary vri = Assert.IsType<PdfDictionary>(dss[new PdfName("VRI"u8)]);
        Assert.Contains(new PdfName(Encoding.ASCII.GetBytes(
            Convert.ToHexString(SHA1.HashData(signature.Contents.Span)))), vri.Keys);
        Assert.Equal(certificate.RawData, Assert.Single(evidence.Certificates).ToArray());
        Assert.Empty(evidence.OcspResponses);
        Assert.Equal(new byte[] { 0x30, 0x00 },
            Assert.Single(evidence.CertificateRevocationLists).ToArray());
        Assert.Equal(new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero),
            evidence.ValidationTime);
        Assert.Throws<InvalidOperationException>(() =>
            PdfPadesValidationDataWriter.Embed(reopened, reopenedSignature,
                new PdfPadesValidationData
                {
                    Certificates = [certificate.RawData],
                    CertificateRevocationLists = [new byte[] { 0x30, 0x00 }]
                }));
    }

    private static byte[] Sign(ReadOnlyMemory<byte> content, X509Certificate2 certificate,
        string? policyOid = null)
    {
        var cms = new SignedCms(new ContentInfo(content.ToArray()), detached: true);
        var signer = new CmsSigner(certificate)
        {
            IncludeOption = X509IncludeOption.EndCertOnly,
            DigestAlgorithm = new Oid("2.16.840.1.101.3.4.2.1")
        };
        if (policyOid is not null)
        {
            var writer = new AsnWriter(AsnEncodingRules.DER);
            writer.PushSequence();
            writer.WriteObjectIdentifier(policyOid);
            writer.PushSequence();
            writer.PushSequence();
            writer.WriteObjectIdentifier("2.16.840.1.101.3.4.2.1");
            writer.WriteNull();
            writer.PopSequence();
            writer.WriteOctetString(SHA256.HashData("policy"u8));
            writer.PopSequence();
            writer.PopSequence();
            var attributeOid = new Oid("1.2.840.113549.1.9.16.2.15");
            signer.SignedAttributes.Add(new CryptographicAttributeObject(
                attributeOid, new AsnEncodedDataCollection(
                    new AsnEncodedData(attributeOid, writer.Encode()))));
        }
        cms.ComputeSignature(signer);
        return cms.Encode();
    }
}
