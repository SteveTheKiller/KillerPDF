using System.Security.Cryptography;
using KillerPdf.Engine.CrossReference;
using KillerPdf.Engine.Diagnostics;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Writing;

namespace KillerPdf.Engine.Validation;

/// <summary>The outcome and artifacts from deterministic rewrite verification.</summary>
/// <param name="Succeeded">Whether rewriting, reopening, and verification succeeded.</param>
/// <param name="IsDeterministic">Whether the first and second rewrite bytes are identical.</param>
/// <param name="RewrittenSha256">The lowercase SHA-256 digest of the first rewrite.</param>
/// <param name="RewrittenBytes">The first rewritten PDF bytes.</param>
/// <param name="SourceInspection">The structural inspection of the source.</param>
/// <param name="RewrittenInspection">The structural inspection of the first rewrite.</param>
/// <param name="FailureMessage">A failure explanation, or null after success.</param>
public sealed record PdfRoundTripResult(
    bool Succeeded,
    bool IsDeterministic,
    string? RewrittenSha256,
    byte[]? RewrittenBytes,
    PdfInspectionReport SourceInspection,
    PdfInspectionReport? RewrittenInspection,
    string? FailureMessage)
{
    /// <summary>Structured details for recognized failures, suitable for localized presentation.</summary>
    public PdfRoundTripFailure? Failure { get; init; }
}

/// <summary>Runs the preservation writer through reopen and second-write verification.</summary>
public static class PdfRoundTripValidator
{
    /// <summary>Validates an unencrypted PDF through structural inspection and two deterministic rewrites.</summary>
    public static PdfRoundTripResult Validate(
        ReadOnlyMemory<byte> source,
        PdfDocumentWriteOptions? options = null)
        => ValidateCore(source, password: null, options);

    /// <summary>Validates a password-encrypted PDF through two authenticated rewrites.</summary>
    public static PdfRoundTripResult ValidateAuthenticated(
        ReadOnlyMemory<byte> source,
        string password,
        PdfDocumentWriteOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(password);
        return ValidateCore(source, password, options);
    }

    private static PdfRoundTripResult ValidateCore(
        ReadOnlyMemory<byte> source,
        string? password,
        PdfDocumentWriteOptions? options)
    {
        PdfInspectionReport sourceInspection = password is null
            ? PdfDocumentInspector.Inspect(source)
            : PdfDocumentInspector.InspectAuthenticated(source, password);
        if (!sourceInspection.IsStructurallyValid || sourceInspection.RequiresAuthentication)
        {
            var failure = new PdfRoundTripFailure(password is null && sourceInspection.RequiresAuthentication
                ? PdfRoundTripFailureCode.AuthenticationRequired
                : sourceInspection.Diagnostics.Any(item => item.Code == PdfDiagnosticCode.AuthenticationFailed)
                    ? PdfRoundTripFailureCode.AuthenticationFailed
                    : PdfRoundTripFailureCode.SourceInspection);
            return new PdfRoundTripResult(
                false, false, null, null, sourceInspection, null,
                failure.Format()) { Failure = failure };
        }

        try
        {
            PdfDocument document = password is null
                ? PdfDocument.Open(source)
                : PdfDocument.Open(source, password);
            byte[] rewritten = PdfDocumentWriter.Write(document, options);
            PdfInspectionReport rewrittenInspection = password is null
                ? PdfDocumentInspector.Inspect(rewritten)
                : PdfDocumentInspector.InspectAuthenticated(rewritten, password);
            if (!rewrittenInspection.IsStructurallyValid
                || rewrittenInspection.RequiresAuthentication)
            {
                var failure = new PdfRoundTripFailure(PdfRoundTripFailureCode.RewrittenInspection);
                return new PdfRoundTripResult(
                    false, false, Hex(rewritten), rewritten, sourceInspection, rewrittenInspection,
                    failure.Format()) { Failure = failure };
            }

            PdfDocument reopened = password is null
                ? PdfDocument.Open(rewritten)
                : PdfDocument.Open(rewritten, password);
            byte[] secondPass = PdfDocumentWriter.Write(reopened, options);
            bool deterministic = rewritten.AsSpan().SequenceEqual(secondPass);
            if (password is not null)
            {
                PdfInspectionReport secondInspection =
                    PdfDocumentInspector.InspectAuthenticated(secondPass, password);
                if (!secondInspection.IsStructurallyValid
                    || secondInspection.RequiresAuthentication)
                {
                    var failure = new PdfRoundTripFailure(PdfRoundTripFailureCode.SecondAuthenticatedInspection);
                    return new PdfRoundTripResult(
                        false, false, Hex(rewritten), rewritten, sourceInspection,
                        rewrittenInspection,
                        failure.Format()) { Failure = failure };
                }
                PdfDocument secondDocument = PdfDocument.Open(secondPass, password);
                if (!EquivalentResolvedObjects(reopened, secondDocument))
                {
                    var failure = new PdfRoundTripFailure(PdfRoundTripFailureCode.AuthenticatedGraphMismatch);
                    return new PdfRoundTripResult(
                        false, false, Hex(rewritten), rewritten, sourceInspection,
                        rewrittenInspection,
                        failure.Format()) { Failure = failure };
                }
                return new PdfRoundTripResult(
                    true, deterministic, Hex(rewritten), rewritten, sourceInspection,
                    rewrittenInspection, null);
            }
            int firstDifference = deterministic ? -1 : FirstDifference(rewritten, secondPass);
            PdfRoundTripFailure? mismatch = deterministic ? null : new(
                PdfRoundTripFailureCode.RewriteMismatch, firstDifference, rewritten.Length, secondPass.Length);
            return new PdfRoundTripResult(
                deterministic,
                deterministic,
                Hex(rewritten),
                rewritten,
                sourceInspection,
                rewrittenInspection,
                mismatch?.Format()) { Failure = mismatch };
        }
        catch (Exception error)
        {
            return new PdfRoundTripResult(
                false, false, null, null, sourceInspection, null, error.Message);
        }
    }

    private static string Hex(ReadOnlySpan<byte> value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    private static int FirstDifference(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
    {
        int sharedLength = Math.Min(first.Length, second.Length);
        for (int index = 0; index < sharedLength; index++)
            if (first[index] != second[index]) return index;
        return sharedLength;
    }

    private static bool EquivalentResolvedObjects(PdfDocument first, PdfDocument second)
    {
        Dictionary<(int ObjectNumber, int Generation), byte[]> firstObjects = CanonicalObjects(first);
        Dictionary<(int ObjectNumber, int Generation), byte[]> secondObjects = CanonicalObjects(second);
        return firstObjects.Count == secondObjects.Count
            && firstObjects.All(entry => secondObjects.TryGetValue(entry.Key, out byte[]? value)
                && entry.Value.AsSpan().SequenceEqual(value))
            && CanonicalTrailer(first).AsSpan().SequenceEqual(CanonicalTrailer(second));

        static Dictionary<(int ObjectNumber, int Generation), byte[]> CanonicalObjects(
            PdfDocument document)
        {
            var result = new Dictionary<(int ObjectNumber, int Generation), byte[]>();
            foreach (PdfCrossReferenceEntry entry in document.CrossReferences.Values.Where(entry =>
                         entry.Type is PdfCrossReferenceEntryType.InUse
                             or PdfCrossReferenceEntryType.Compressed))
            {
                PdfObject value = document.Resolve(entry.ObjectNumber);
                if (value is PdfStream stream
                    && stream.Dictionary.TryGetValue(new PdfName("Type"u8), out PdfObject? type)
                    && type is PdfName name
                    && name.ValueAsLatin1() is "XRef" or "ObjStm")
                    continue;
                int generation = entry.Type == PdfCrossReferenceEntryType.InUse
                    ? checked((int)entry.Field2) : 0;
                result[(entry.ObjectNumber, generation)] = PdfObjectWriter.Write(
                    new PdfIndirectObject(
                        entry.ObjectNumber, generation, value, offset: 0));
            }
            return result;
        }

        static byte[] CanonicalTrailer(PdfDocument document)
        {
            string[] structuralNames =
            ["Type", "Length", "Filter", "DecodeParms", "W", "Index", "Size", "Prev", "XRefStm"];
            var structural = structuralNames.Select(name => new PdfName(
                System.Text.Encoding.ASCII.GetBytes(name))).ToHashSet();
            return PdfObjectWriter.Write(new PdfDictionary(
                document.Trailer.Where(entry => !structural.Contains(entry.Key))));
        }
    }
}
