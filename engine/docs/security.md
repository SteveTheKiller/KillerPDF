# Encryption and signature integration

KillerPDF.Engine exposes authentication, encryption, permission, and signature
APIs independently of the desktop app. The host supplies passwords, certificates,
signing services, trust policy, and the user's choice of output file.
These examples describe the 1.9 source.

Password-authenticated compatibility recovery treats an AES-256-CBC stream
containing only its 16-byte initialization vector as empty. This accommodates
producers that omit the encrypted padding block for empty content. It applies
only to streams, not strings, AES-GCM, or other truncated ciphertext lengths.
Password and permission authentication still run first. Strict reads reject the
malformed stream, and writers continue emitting the required padding block.

## Author a password-encrypted document

Pass passwords from the host's credential flow. The user password opens the file
with the declared permissions; the owner password authenticates unrestricted
access. An empty user password permits opening without a password prompt in
viewers that try it automatically.

```csharp
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Security;

static byte[] CreateProtectedPage(string userPassword, string ownerPassword)
{
    return new PdfDocumentBuilder()
        .SetPasswordEncryption(new PdfPasswordEncryptionOptions
        {
            UserPassword = userPassword,
            OwnerPassword = ownerPassword,
            AllowDocumentModification = false,
            AllowContentCopying = false
        })
        .AddPage(612, 792, new PdfContentStreamBuilder()
            .BeginText().SetFont(PdfStandardFont.Helvetica, 12)
            .MoveText(36, 740).ShowLatin1Text("Protected document").EndText())
        .Build();
}
```

The default algorithm is `Aes256Cbc` with revision 6 password authentication.
`Aes256Gcm` selects revision 7 and requires PDF 2.0. Metadata encryption defaults
to true. All permission flags default to true, so specify each restriction that
the output should declare. Printing, annotation changes, form filling,
accessibility extraction, and page assembly have separate flags.

For opening and inspecting authenticated roles, see the
[reading guide](reading.md#passwords-certificates-and-permissions).
Page-content access alone does not establish permission to edit or extract.
The host should apply its operation policy using authenticated permissions.

## Author for certificate recipients

Supply recipient certificates containing the public keys used to protect the
document. A recipient needs the corresponding private key when opening it.
The host owns certificate loading and disposal.

```csharp
using System.Security.Cryptography.X509Certificates;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Security;

static byte[] CreateRecipientDocument(X509Certificate2 recipient)
{
    return new PdfDocumentBuilder()
        .SetCertificateEncryption(new PdfCertificateEncryptionOptions
        {
            Recipients = [recipient],
            AllowDocumentModification = false
        })
        .AddBlankPage(612, 792)
        .Build();
}
```

The authoring options create AES-256 certificate-recipient encryption. Permission
and metadata options are independent of the password-encryption options.
One builder cannot configure both password and certificate encryption.
Open the resulting bytes with `PdfDocument.Open(bytes, recipientWithPrivateKey)`.
Opening also accepts certificate-protected files that use AES-128, as well as
AES-256.

## Encrypt an existing document

`PdfDocumentEncryptionWriter` fully rewrites an existing document under new
protection. `EncryptWithPasswords` applies Standard Security passwords, including
AES-256 and revision 7 AES-GCM with the same `PdfPasswordEncryptionOptions` as
authoring. `EncryptForRecipients` applies AES-256 certificate-recipient
encryption. Any source encryption is replaced, so the output carries only the new
protection. The first, permanent trailer `/ID` string is kept so links to the
document still resolve, or is a fresh random value when the source has none. The
second string, which marks this version, is always a new random value. Neither
is derived from the content, because a hash of the unencrypted bytes stored in
the clear would let anyone confirm a guess at what the document says. The `WithReport`
variants return a `PdfDocumentWriteResult` that lists explicit dangling-reference
repairs.

```csharp
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Security;
using KillerPdf.Engine.Writing;

static byte[] Protect(byte[] source, string userPassword, string ownerPassword)
{
    PdfDocument document = PdfDocument.Open(source);
    return PdfDocumentEncryptionWriter.EncryptWithPasswords(
        document,
        new PdfPasswordEncryptionOptions
        {
            UserPassword = userPassword,
            OwnerPassword = ownerPassword,
            AllowContentCopying = false
        });
}
```

The writer refuses a signed document unless `allowSignatureInvalidation` is true,
because a rewrite clears signature values. It also refuses a user password
without an owner password. Authenticate an encrypted source first with
credentials authorized for the change.

## Rewrite an authenticated document

A full rewrite requires decrypted content. For a Standard Security user-password
session, the writer also requires document-modification permission. Authenticate
with credentials authorized for the intended operation before calling it.

```csharp
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Writing;

static byte[] RewriteProtectedDocument(byte[] source, string password,
    bool removeEncryption)
{
    PdfDocument document = PdfDocument.Open(source, password);
    return PdfDocumentWriter.Write(document, new PdfDocumentWriteOptions
    {
        RemoveEncryption = removeEncryption
    });
}
```

`RemoveEncryption` defaults to false, preserving encryption for an authenticated
encrypted source. Selecting true produces an unencrypted full rewrite.
An encrypted file with readable, unencrypted page streams still requires
authentication before rewriting its encrypted objects.

The writer rejects signed documents by default because a full rewrite changes
their signed bytes. Explicit `AllowSignatureInvalidation = true` clears existing
signature values before rewriting. It does not preserve signature validity.
Incremental updates retain the original byte prefix, but later edits can still
violate certification permissions or field locks. Assess the permitted change
and the resulting signed revisions rather than treating byte preservation as
approval for any edit.

## Inspect signature integrity and trust

`PdfSignatureInspection.Inspect(document)` checks integrity and revision coverage
without evaluating certificate trust. Supply `PdfSignatureTrustOptions` to request
chain evaluation. This example uses the operating-system trust store and prohibits
certificate downloads; it deliberately does not check revocation.

```csharp
using System.Security.Cryptography.X509Certificates;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Signing;

static PdfSignatureInspectionReport InspectOffline(PdfDocument document)
{
    return PdfSignatureInspection.Inspect(document, new PdfSignatureTrustOptions
    {
        DisableCertificateDownloads = true,
        RevocationMode = X509RevocationMode.NoCheck
    });
}
```

Keep the returned validation, trust, revocation, and later-revision results
distinct. Cryptographic integrity alone does not establish certificate trust.
Disabled or unavailable revocation checking must not be presented as a successful
revocation check. Reports give an explicit complete, incomplete, or invalid
status. They list the signer name, signing time, reason, location, contact, certificate identity and
fingerprints, permissions, and later-revision analysis, and expose incomplete and
invalid outcomes as well as successful checks. `ToText()` and
`ToJson(indented: true)` export the report.

`ExtraCertificates` supplies intermediates; nonempty `CustomTrustRoots` selects
explicit trust anchors instead of the system store. `VerificationTime` defaults
to the current local time. `RevocationMode` defaults to `NoCheck`, and certificate
downloads are allowed unless disabled. Select these settings explicitly when a
host requires reproducible or offline validation.

## Signing and retained validation evidence

`PdfDetachedSignatureWriter.Sign` creates an incremental signature revision and
calls the host's `Func<ReadOnlyMemory<byte>, byte[]>` to produce detached CMS for
the supplied signed bytes. The host owns private-key access and CMS generation.
`PdfSignatureOptions` configures the field, digest commitment, reserved signature
size, descriptive values, optional appearance, and certification permission.
The CMS must fit the reserved space; the default reservation is 32,768 bytes and
the maximum is 1,048,576 bytes.

Signing preserves the cross-reference format of the source file. A visible
appearance can be placed on an existing signature widget, which keeps its page
rectangle. When the engine names a new signature field automatically, it avoids
names already in the document and keeps an unsigned default field. Set
`RequestTimestamp` to have the engine add an RFC 3161 signature timestamp. See
the [timestamp guide](timestamping.md).

An appearance is visual content, not evidence of a valid signature. Inspect the
saved output after signing. `PdfSignatureReader` exposes fields and signed byte
ranges, `PdfSignedRevisionAnalyzer` describes later changes, and
`PdfPadesProfileInspector` classifies retained PAdES evidence.
`PdfPadesValidationDataWriter` writes supplied DSS/VRI validation data; the host
must obtain and select that evidence. Storing evidence alone does not establish
its validity or complete long-term validation.

## Failure handling

Incorrect passwords can raise `CryptographicException`. Unsupported encryption,
malformed objects, denied writes, and invalid signature data have distinct failure
paths. Do not turn every failure into a password retry. Recovery mode tolerates
selected malformed structures while retaining authentication and writing guards.
Preserve the source bytes and save returned output to a separate destination
until the host's validation and overwrite policy allow replacement.
