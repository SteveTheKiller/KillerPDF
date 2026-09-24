# RFC 3161 timestamp integration

KillerPDF.Engine supports two different timestamp roles:

- A signature timestamp token is an unsigned attribute inside a detached CMS
  approval or certification signature. The host's CMS callback obtains and
  embeds that token.
- A document timestamp is a PDF `DocTimeStamp` signature whose contents are an
  RFC 3161 token over the PDF byte ranges. The engine reads and verifies these
  signatures, but does not currently create a document-timestamp revision.

Neither role is the same as the PDF signature dictionary's `SigningTime`. That
field is descriptive document data and is not independent time evidence.

## Require a timestamped approval signature

A signature field can declare an optional or required timestamp authority URL:

```csharp
using KillerPdf.Engine.Authoring;

const string tsaUrl = "https://tsa.example.test/rfc3161";

byte[] sourceBytes = new PdfDocumentBuilder()
    .AddBlankPage()
    .AddSignatureField(
        pageIndex: 0,
        name: "Approval",
        x: 36,
        y: 36,
        width: 220,
        height: 72,
        seedValue: new PdfSignatureSeedValue
        {
            Timestamp = new PdfSignatureTimestamp(tsaUrl, Required: true)
        })
    .Build();
```

The URL must be an absolute ASCII HTTP or HTTPS URL. `Required: true` makes the
writer reject a detached CMS result that lacks the RFC 3161 signature-timestamp
attribute. It also requires `PdfSignatureOptions.TimestampServerUrl` to match
the seed URL exactly.

A seed value is a signing constraint, not a timestamp client. The engine does
not send an HTTP request to that URL.

## Supply timestamped CMS from the host

The detached-signing callback receives the exact PDF byte ranges to sign. The
host signs those bytes, requests a timestamp for the CMS signature value, embeds
the returned RFC 3161 token as the signature-timestamp unsigned attribute, and
returns the complete DER CMS value.

```csharp
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Signing;

static byte[] SignApprovalWithTimestamp(
    byte[] sourceBytes,
    string tsaUrl,
    Func<ReadOnlyMemory<byte>, byte[]> createTimestampedCms)
{
    PdfDocument document = PdfDocument.Open(sourceBytes);
    return PdfDetachedSignatureWriter.Sign(
        document,
        createTimestampedCms,
        new PdfSignatureOptions
        {
            FieldName = "Approval",
            TimestampServerUrl = tsaUrl,
            ReservedSignatureSize = 65_536
        });
}
```

The callback is responsible for private-key access, certificate selection,
digest consistency, TSA authentication, request nonce policy, HTTP timeouts,
response size limits, RFC 3161 status handling, message-imprint matching, and
the trust policy for the timestamp authority. Do not return a token merely
because the server responded successfully.

`ReservedSignatureSize` must hold the final CMS value after certificates,
revocation evidence, and the timestamp token are embedded. If the value is too
large, signing fails and the host must retry the whole operation with a larger
reservation. Do not reuse a partially written result.

The writer rejects null, empty, or oversized callback output. When the field
requires timestamp evidence, it checks that the returned CMS has the expected
timestamp-token attribute. That check establishes presence, not token validity
or TSA trust. Reopen and verify the completed PDF, and validate the TSA response
before embedding it.

## Inspect document timestamps

Read all signature fields and select the ones marked as document timestamps:

```csharp
PdfDocument document = PdfDocument.Open(timestampedBytes);
IReadOnlyList<PdfSignatureInfo> timestamps = PdfSignatureReader.Read(document)
    .Where(signature => signature.IsDocumentTimestamp)
    .ToArray();

foreach (PdfSignatureInfo timestamp in timestamps)
{
    PdfSignatureVerificationResult integrity =
        PdfSignatureVerifier.VerifyIntegrity(document, timestamp);
    Console.WriteLine($"{timestamp.FieldName}: {integrity.ValidationStatus}");
}
```

For a `DocTimeStamp` signature, integrity verification decodes the RFC 3161
token and verifies that its message imprint matches the PDF bytes selected by
the signature byte range. Invalid token encoding, a malformed byte range, or a
mismatched imprint produces an invalid result.

`PdfSignatureInspection.Inspect(document)` combines timestamp identification,
integrity, signed-revision coverage, later-change analysis, and PAdES evidence
in text or data-safe JSON. Pass `PdfSignatureTrustOptions` to its other overload
when the host also wants certificate-chain and revocation evaluation.

Integrity without trust is not proof that the timestamp authority is accepted.
A trust result can also be incomplete when revocation evidence or a chain is
unavailable. Preserve `Invalid`, `Incomplete`, and `Valid` as distinct states.
The host chooses trust roots, verification time, network-download policy, and
revocation mode.

## Creating document timestamps

The public API does not currently author a PDF `DocTimeStamp` dictionary or
reserve and patch an RFC 3161 token directly into a document-timestamp revision.
Do not create one by signing an approval field and changing `/Type` or
`/SubFilter`; the byte-range digest and token imprint would no longer describe a
properly constructed document timestamp.

Until a dedicated writer exists, use a standards-compliant external signing
component to create the document timestamp, then reopen the exact output bytes
and verify them with `PdfSignatureReader`, `PdfSignatureVerifier`, and
`PdfSignatureInspection`. Keep this limitation visible in product UI and API
claims.

## Failure and operational behavior

Timestamp servers are external security dependencies. Use explicit connection
and total-operation timeouts, TLS validation, bounded responses, retry limits,
and an auditable authority allowlist. A timeout or rejected response leaves the
document unsigned. It must not silently fall back to an un-timestamped signature
when the field requires one.

Detached signing is an incremental change and is subject to existing
certification permissions, field locks, seed constraints, password permissions,
and reserved-size limits. Later PDF revisions do not alter the bytes already
covered by a valid timestamp, but they can mean the timestamp does not cover the
latest document state. Check `CoversWholeDocument` and signed-revision analysis.

Reports omit CMS and signed document bytes, but they include certificate
identity, fingerprints, authority details, byte ranges, and errors. Treat them
as security records. See the [security guide](security.md) for trust policy,
offline verification, permissions, and long-term validation evidence.
