# Signing and encryption

The engine supports password and certificate-recipient security, detached CMS signatures, RFC 3161 timestamps, certification policies, field locks, signature discovery, cryptographic verification, trust and revocation policy, and signed revision analysis.

## Create a password-protected document

```csharp
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Security;

var encryption = new PdfPasswordEncryptionOptions
{
    UserPassword = "reader password",
    OwnerPassword = "owner password",
    AllowContentCopying = false,
    AllowDocumentModification = false,
    AllowHighQualityPrinting = true
};

byte[] protectedPdf = new PdfDocumentBuilder()
    .SetPasswordEncryption(encryption)
    .AddBlankPage()
    .Build();
```

New documents use PDF 2.0 AES-256 protection, and `Aes256Gcm` selects revision 7. The engine can also read supported RC4 and AES-128 documents and preserve authenticated security during incremental updates and rewrites.

## Protect for certificate recipients

```csharp
using System.Security.Cryptography.X509Certificates;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Security;

byte[] protectedPdf = new PdfDocumentBuilder()
    .SetCertificateEncryption(new PdfCertificateEncryptionOptions
    {
        Recipients = [recipientCertificate]
    })
    .AddBlankPage()
    .Build();
```

A recipient opens the file with `PdfDocument.Open(bytes, certificateWithPrivateKey)`. To protect an existing document, use `PdfDocumentEncryptionWriter`, which rewrites it with AES-256 passwords or certificate encryption, replaces any source encryption, and refuses a signed document unless you allow signature invalidation.

## Open encrypted input

```csharp
using KillerPdf.Engine.Documents;

PdfDocument document = PdfDocument.Open(source, password);
Console.WriteLine(document.PasswordAuthenticationRole);
Console.WriteLine(document.DeclaredPermissions?.AllowContentCopying);
```

Honor the authenticated permission set in your application workflow. An owner password grants unrestricted access. A user password may limit printing, copying, assembly, forms, annotations, or general modification.

## Sign with detached CMS

`PdfDetachedSignatureWriter` prepares the PDF signature revision and calls your application back to produce the detached CMS value. Your application owns certificate selection, private-key access, and trust configuration. Set `RequestTimestamp` on `PdfSignatureOptions` to have the engine request an RFC 3161 timestamp from `TimestampServerUrl`, or from a caller-supplied `TimestampTransport`, and verify the token before embedding it. Signing keeps the source cross-reference format and can place a visible appearance on an existing signature widget.

```csharp
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Signing;

var options = new PdfSignatureOptions
{
    FieldName = "Signature1",
    SignerName = "Example signer",
    Reason = "Approved",
    DigestMethod = PdfSignatureDigestMethod.Sha256
};
```

Use the `Sign` overload that matches your certificate and CMS provider. Do not copy a private key into the PDF or into application logs.

## Read and verify signatures

```csharp
IReadOnlyList<PdfSignatureInfo> signatures = PdfSignatureReader.Read(document);

foreach (PdfSignatureInfo signature in signatures)
{
    PdfSignatureVerificationResult integrity =
        PdfSignatureVerifier.VerifyIntegrity(document, signature);
    Console.WriteLine(integrity.IsCryptographicallyValid);
}
```

Integrity verification answers whether the signed bytes match the signature. Trust verification additionally evaluates the certificate chain under the trust policy supplied by your application.

`PdfSignatureInspection.Inspect` combines these results in one report. It gives an explicit complete, incomplete, or invalid status, the signer name, signing time, reason, location, and contact, certificate identity and fingerprints, permissions, later-revision analysis, and PAdES validation evidence. Pass `PdfSignatureTrustOptions` to control trust anchors, revocation checking, verification time, and offline policy. Document timestamps are verified for integrity but not created by the engine.

## Analyze later revisions

`PdfSignedRevisionAnalyzer` identifies changes after a signature and evaluates them against certification permissions and field locks. This is different from a simple cryptographic pass or fail result. A signature can remain cryptographically intact while later revisions add changes that violate the declared policy.

## Security boundaries

- Treat PDF passwords as sensitive data and avoid retaining them longer than needed.
- Keep private keys in the operating system certificate store, hardware, or another protected signing provider.
- Make trust policy explicit. Do not treat any embedded certificate as trusted by default.
- Validate signed output with an independent CMS implementation before release.
