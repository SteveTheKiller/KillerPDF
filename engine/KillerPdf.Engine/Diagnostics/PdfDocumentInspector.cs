using KillerPdf.Engine.CrossReference;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Filters;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Security;
using KillerPdf.Engine.Syntax;
using System.Security.Cryptography;

namespace KillerPdf.Engine.Diagnostics;

/// <summary>Performs a bounded, non-throwing structural inspection of a PDF file.</summary>
public static class PdfDocumentInspector
{
    /// <summary>Default maximum number of indirect objects inspected in one document.</summary>
    public const int DefaultMaximumInspectedObjects = 100_000;

    private static readonly PdfName RootName = new("Root"u8);
    private static readonly PdfName TypeName = new("Type"u8);
    private static readonly PdfName CatalogName = new("Catalog"u8);

    /// <summary>Inspects an unencrypted PDF for structural and conformance diagnostics.</summary>
    public static PdfInspectionReport Inspect(
        ReadOnlyMemory<byte> source,
        int maximumInspectedObjects = DefaultMaximumInspectedObjects)
        => InspectCore(source, () => PdfDocument.Open(source), maximumInspectedObjects);

    /// <summary>
    /// Inspects a document the caller has already opened, such as one authenticated under encryption
    /// held from an earlier open, where no password is at hand. The document's own bytes are inspected.
    /// </summary>
    public static PdfInspectionReport InspectAuthenticated(
        PdfDocument document,
        int maximumInspectedObjects = DefaultMaximumInspectedObjects)
    {
        ArgumentNullException.ThrowIfNull(document);
        return InspectCore(document.Source, () => document, maximumInspectedObjects);
    }

    /// <summary>Performs bounded structural inspection after password authentication.</summary>
    public static PdfInspectionReport InspectAuthenticated(
        ReadOnlyMemory<byte> source,
        string password,
        int maximumInspectedObjects = DefaultMaximumInspectedObjects)
    {
        ArgumentNullException.ThrowIfNull(password);
        return InspectCore(source, () => PdfDocument.Open(source, password), maximumInspectedObjects);
    }

    private static PdfInspectionReport InspectCore(
        ReadOnlyMemory<byte> source,
        Func<PdfDocument> open,
        int maximumInspectedObjects)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumInspectedObjects, 1);

        var diagnostics = new List<PdfDiagnostic>();
        PdfVersion? version = ReadHeader(source, diagnostics);
        long? startXrefOffset = ReadStartXref(source, diagnostics);
        if (!version.HasValue || !startXrefOffset.HasValue)
            return Report(version, startXrefOffset, null, 0, diagnostics);

        PdfCrossReferenceTable table;
        try
        {
            table = PdfCrossReferenceTable.Read(source);
        }
        catch (PdfSyntaxException error)
        {
            diagnostics.Add(Diagnostic(
                PdfDiagnosticCode.InvalidCrossReference,
                error.Message,
                error.Offset));
            return Report(version, startXrefOffset, null, 0, diagnostics);
        }
        catch (PdfFilterException error)
        {
            diagnostics.Add(Diagnostic(PdfDiagnosticCode.InvalidCrossReference, error.Message));
            return Report(version, startXrefOffset, null, 0, diagnostics);
        }
        catch (Exception error) when (IsStructuralFailure(error))
        {
            diagnostics.Add(Diagnostic(PdfDiagnosticCode.InvalidCrossReference, error.Message));
            return Report(version, startXrefOffset, null, 0, diagnostics);
        }

        PdfDocument document;
        try
        {
            document = open();
        }
        catch (PdfPasswordAuthenticationException error)
        {
            diagnostics.Add(new PdfDiagnostic(
                PdfDiagnosticCode.AuthenticationFailed,
                PdfDiagnosticSeverity.Warning,
                error.Message));
            return Report(version, startXrefOffset, table.Count, 0, diagnostics);
        }
        catch (Exception error) when (IsStructuralFailure(error))
        {
            diagnostics.Add(Diagnostic(PdfDiagnosticCode.InvalidCrossReference, error.Message));
            return Report(version, startXrefOffset, table.Count, 0, diagnostics);
        }

        if (document.IsEncrypted && !document.IsDecrypted)
        {
            diagnostics.Add(new PdfDiagnostic(
                PdfDiagnosticCode.AuthenticationFailed,
                PdfDiagnosticSeverity.Warning,
                "Password authentication is required to inspect encrypted indirect objects."));
            return Report(version, startXrefOffset, table.Count, 0, diagnostics);
        }

        int inspected = InspectObjects(document, table, maximumInspectedObjects, diagnostics);
        InspectCatalog(document, table, diagnostics);
        return Report(version, startXrefOffset, table.Count, inspected, diagnostics);
    }

    private static PdfVersion? ReadHeader(ReadOnlyMemory<byte> source, List<PdfDiagnostic> diagnostics)
    {
        try
        {
            return PdfHeader.Parse(source.Span).Version;
        }
        catch (PdfSyntaxException error)
        {
            diagnostics.Add(Diagnostic(PdfDiagnosticCode.InvalidHeader, error.Message, error.Offset));
            return null;
        }
        catch (Exception error) when (error is FormatException or NotSupportedException)
        {
            diagnostics.Add(Diagnostic(PdfDiagnosticCode.InvalidHeader, error.Message));
            return null;
        }
    }

    private static long? ReadStartXref(ReadOnlyMemory<byte> source, List<PdfDiagnostic> diagnostics)
    {
        try
        {
            return PdfStartXref.Find(source.Span).Offset;
        }
        catch (PdfSyntaxException error)
        {
            diagnostics.Add(Diagnostic(PdfDiagnosticCode.InvalidStartXref, error.Message, error.Offset));
            return null;
        }
    }

    private static int InspectObjects(
        PdfDocument document,
        PdfCrossReferenceTable table,
        int maximumInspectedObjects,
        List<PdfDiagnostic> diagnostics)
    {
        int inspected = 0;
        foreach (PdfCrossReferenceEntry entry in table.Values
                     .Where(entry => entry.Type is PdfCrossReferenceEntryType.InUse or PdfCrossReferenceEntryType.Compressed)
                     .OrderBy(entry => entry.ObjectNumber))
        {
            if (inspected >= maximumInspectedObjects)
            {
                diagnostics.Add(new PdfDiagnostic(
                    PdfDiagnosticCode.InspectionLimitReached,
                    PdfDiagnosticSeverity.Warning,
                    $"Inspection stopped after {maximumInspectedObjects:N0} in-use objects."));
                break;
            }

            inspected++;
            try
            {
                document.Resolve(entry.ObjectNumber);
            }
            catch (Exception error) when (IsStructuralFailure(error))
            {
                int? offset = error is PdfSyntaxException syntax
                    ? syntax.Offset
                    : entry.Type == PdfCrossReferenceEntryType.InUse ? checked((int)entry.Field1) : null;
                diagnostics.Add(Diagnostic(
                    PdfDiagnosticCode.InvalidIndirectObject,
                    $"Object {entry.ObjectNumber} cannot be resolved: {error.Message}",
                    offset,
                    entry.ObjectNumber));
            }
        }
        return inspected;
    }

    private static void InspectCatalog(
        PdfDocument document,
        PdfCrossReferenceTable table,
        List<PdfDiagnostic> diagnostics)
    {
        if (!table.TryGetTrailerValue(RootName, out PdfObject root))
        {
            diagnostics.Add(Diagnostic(
                PdfDiagnosticCode.MissingCatalogRoot,
                "The cross-reference trailer chain does not contain a /Root catalog reference."));
            return;
        }
        if (root is not PdfIndirectReference reference)
        {
            diagnostics.Add(Diagnostic(
                PdfDiagnosticCode.InvalidCatalogRoot,
                "The trailer /Root value is not an indirect reference."));
            return;
        }

        try
        {
            PdfObject resolved = ResolveFully(reference, "The trailer /Root value");
            if (resolved is not PdfDictionary catalog)
            {
                diagnostics.Add(Diagnostic(
                    PdfDiagnosticCode.InvalidCatalogRoot,
                    $"The trailer /Root reference {reference.ObjectNumber} {reference.Generation} does not resolve to a dictionary.",
                    objectNumber: reference.ObjectNumber));
                return;
            }
            if (!catalog.TryGetValue(TypeName, out PdfObject? type)
                || ResolveFully(type, "The catalog /Type value") is not PdfName typeName
                || !typeName.Equals(CatalogName))
                diagnostics.Add(Diagnostic(
                    PdfDiagnosticCode.InvalidCatalogRoot,
                    "The trailer /Root dictionary does not declare /Type /Catalog.",
                    objectNumber: reference.ObjectNumber));

            PdfObject ResolveFully(PdfObject value, string description)
            {
                var visited = new HashSet<(int ObjectNumber, int Generation)>();
                for (int depth = 0; value is PdfIndirectReference current; depth++)
                {
                    if (depth >= 32)
                        throw new InvalidOperationException(
                            $"{description} is too deeply indirect.");
                    if (!visited.Add((current.ObjectNumber, current.Generation)))
                        throw new InvalidOperationException(
                            $"{description} contains an indirect-reference cycle.");
                    value = document.Resolve(current);
                }
                return value;
            }
        }
        catch (Exception error) when (IsStructuralFailure(error))
        {
            diagnostics.Add(Diagnostic(
                PdfDiagnosticCode.InvalidCatalogRoot,
                $"The trailer /Root reference cannot be resolved: {error.Message}",
                error is PdfSyntaxException syntax ? syntax.Offset : null,
                reference.ObjectNumber));
        }
    }

    private static bool IsStructuralFailure(Exception error) =>
        error is ArgumentException or InvalidOperationException or FormatException
            or NotSupportedException or PdfFilterException or OverflowException
            or CryptographicException;

    private static PdfDiagnostic Diagnostic(
        PdfDiagnosticCode code,
        string message,
        int? offset = null,
        int? objectNumber = null) =>
        new(code, PdfDiagnosticSeverity.Error, message, offset, objectNumber);

    private static PdfInspectionReport Report(
        PdfVersion? version,
        long? startXrefOffset,
        int? crossReferenceEntryCount,
        int inspectedObjectCount,
        List<PdfDiagnostic> diagnostics) =>
        new(version, startXrefOffset, crossReferenceEntryCount, inspectedObjectCount, diagnostics);
}
