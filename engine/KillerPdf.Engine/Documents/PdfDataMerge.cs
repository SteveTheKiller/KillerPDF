using System.Text;
using System.Text.Json;
using KillerPdf.Engine.Editing;

namespace KillerPdf.Engine.Documents;

/// <summary>Expands named data fields for PDF mail merge and batch generation.</summary>
public static class PdfDataMerge
{
    /// <summary>Expands double-braced field names using one record.</summary>
    public static string Expand(string template, IReadOnlyDictionary<string, string?> record,
        PdfMissingMergeValueBehavior missingValueBehavior = PdfMissingMergeValueBehavior.Error)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(record);
        var output = new StringBuilder(template.Length);
        for (int index = 0; index < template.Length;)
        {
            int opening = template.IndexOf("{{", index, StringComparison.Ordinal);
            if (opening < 0) { output.Append(template, index, template.Length - index); break; }
            output.Append(template, index, opening - index);
            int closing = template.IndexOf("}}", opening + 2, StringComparison.Ordinal);
            if (closing < 0) throw new FormatException("A merge placeholder is not closed.");
            string name = template[(opening + 2)..closing].Trim();
            if (name.Length == 0) throw new FormatException("A merge placeholder has no field name.");
            if (record.TryGetValue(name, out string? value) && value is not null) output.Append(value);
            else if (missingValueBehavior == PdfMissingMergeValueBehavior.KeepPlaceholder)
                output.Append(template, opening, closing + 2 - opening);
            else if (missingValueBehavior == PdfMissingMergeValueBehavior.Error)
                throw new KeyNotFoundException($"The merge record has no value for '{name}'.");
            index = closing + 2;
        }
        return output.ToString();
    }

    /// <summary>Processes every record independently and reports failures without ending the batch.</summary>
    public static IReadOnlyList<PdfDataMergeResult> RunBatch(
        IEnumerable<IReadOnlyDictionary<string, string?>> records,
        Func<IReadOnlyDictionary<string, string?>, byte[]> generate)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(generate);
        var results = new List<PdfDataMergeResult>();
        int index = 0;
        foreach (IReadOnlyDictionary<string, string?> record in records)
        {
            try
            {
                byte[] data = generate(record) ?? throw new InvalidOperationException("The generator returned no PDF data.");
                results.Add(new PdfDataMergeResult(index, data, null));
            }
            catch (Exception exception) when (exception is not OutOfMemoryException
                and not StackOverflowException and not AccessViolationException)
            {
                results.Add(new PdfDataMergeResult(index, null, exception.Message));
            }
            index++;
        }
        return Array.AsReadOnly(results.ToArray());
    }

    /// <summary>Maps records into an AcroForm template and isolates each generated PDF.</summary>
    public static IReadOnlyList<PdfDataMergeDocumentResult> RunFormBatch(
        PdfDocument template, IEnumerable<IReadOnlyDictionary<string, string?>> records,
        PdfDataMergeProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(profile);
        var results = new List<PdfDataMergeDocumentResult>();
        var usedFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int index = 0;
        foreach (IReadOnlyDictionary<string, string?> record in records)
        {
            if (cancellationToken.IsCancellationRequested) break;
            string? outputFileName = null;
            try
            {
                PdfDataMergeMappedRecord mapped = profile.Map(record);
                outputFileName = mapped.OutputFileName;
                if (usedFileNames.Contains(outputFileName))
                    throw new InvalidOperationException(
                        $"The output filename '{outputFileName}' is already used by this batch.");
                IReadOnlyList<PdfFormDataMatch> preview =
                    PdfFormDataImporter.Preview(template, mapped.FormData);
                PdfFormDataMatch[] blocked = [.. preview.Where(match =>
                    match.Status != PdfFormDataMatchStatus.Matched)];
                if (blocked.Length > 0)
                    throw new InvalidOperationException("The record cannot be applied to: "
                        + string.Join(", ", blocked.Select(match => match.FieldName)) + ".");
                byte[] data = PdfFormDataImporter.Apply(template, mapped.FormData);
                usedFileNames.Add(outputFileName);
                results.Add(new PdfDataMergeDocumentResult(
                    index, outputFileName, data, null));
            }
            catch (Exception exception) when (exception is not OutOfMemoryException
                and not StackOverflowException and not AccessViolationException)
            {
                results.Add(new PdfDataMergeDocumentResult(
                    index, outputFileName, null, exception.Message));
            }
            index++;
        }
        return Array.AsReadOnly(results.ToArray());
    }

    /// <summary>Previews one mapped record without changing the template or retaining its values.</summary>
    public static PdfDataMergePreview PreviewFormRecord(PdfDocument template,
        IReadOnlyDictionary<string, string?> record, PdfDataMergeProfile profile)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(profile);
        try
        {
            PdfDataMergeMappedRecord mapped = profile.Map(record);
            IReadOnlyList<PdfFormDataMatch> matches =
                PdfFormDataImporter.Preview(template, mapped.FormData);
            PdfFormDataMatch[] blocked = [.. matches.Where(match =>
                match.Status != PdfFormDataMatchStatus.Matched)];
            string? error = blocked.Length == 0 ? null
                : "The record cannot be applied to: "
                    + string.Join(", ", blocked.Select(match => match.FieldName)) + ".";
            return new PdfDataMergePreview(mapped.OutputFileName, matches, error);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException
            and not StackOverflowException and not AccessViolationException)
        {
            return new PdfDataMergePreview(null, [], exception.Message);
        }
    }

    /// <summary>Combines successful generated records in batch order and skips failed records.</summary>
    public static PdfDataMergeCombinedResult CombineSuccessful(
        IEnumerable<PdfDataMergeDocumentResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        PdfDataMergeDocumentResult[] successful = [.. results
            .Where(result => result.Succeeded)
            .OrderBy(result => result.RecordIndex)];
        if (successful.Length == 0)
            throw new InvalidOperationException("The merge batch has no successful PDFs to combine.");
        PdfDocument first = PdfDocument.Open(successful[0].Data!.Value);
        byte[] document;
        if (successful.Length == 1)
            document = successful[0].Data!.Value.ToArray();
        else
        {
            var editor = new PdfIncrementalPageEditor(first);
            foreach (PdfDataMergeDocumentResult result in successful.Skip(1))
                editor.AddImportedDocument(PdfDocument.Open(result.Data!.Value));
            document = editor.Build();
        }
        return new PdfDataMergeCombinedResult(document,
            Array.AsReadOnly(successful.Select(result => result.RecordIndex).ToArray()));
    }
}

/// <summary>How missing data values are handled during template expansion.</summary>
public enum PdfMissingMergeValueBehavior
{
    /// <summary>Reject the record.</summary>
    Error,
    /// <summary>Insert an empty value.</summary>
    Empty,
    /// <summary>Retain the original placeholder.</summary>
    KeepPlaceholder
}

/// <summary>The isolated outcome of processing one merge record.</summary>
public sealed record PdfDataMergeResult(int RecordIndex, ReadOnlyMemory<byte>? Data, string? Error)
{
    /// <summary>Gets whether generation succeeded.</summary>
    public bool Succeeded => Data.HasValue && Error is null;
}

/// <summary>The isolated outcome of generating one mapped PDF document.</summary>
public sealed record PdfDataMergeDocumentResult(int RecordIndex, string? OutputFileName,
    ReadOnlyMemory<byte>? Data, string? Error)
{
    /// <summary>Gets whether PDF generation succeeded.</summary>
    public bool Succeeded => Data.HasValue && Error is null;
}

/// <summary>A combined PDF and the successful source records included in it.</summary>
public sealed record PdfDataMergeCombinedResult(
    byte[] Document, IReadOnlyList<int> IncludedRecordIndices);

/// <summary>A data-free preview of one mapped record.</summary>
public sealed record PdfDataMergePreview(string? OutputFileName,
    IReadOnlyList<PdfFormDataMatch> Fields, string? Error)
{
    /// <summary>Gets whether the record can be generated.</summary>
    public bool CanGenerate => Error is null
        && Fields.All(match => match.Status == PdfFormDataMatchStatus.Matched);
}

/// <summary>A data-free summary of one form-generation batch.</summary>
public sealed record PdfDataMergeBatchReport(
    int TotalRecords, int SucceededRecords, int FailedRecords,
    IReadOnlyList<PdfDataMergeBatchReportItem> Results)
{
    /// <summary>Creates a report without retaining generated PDF bytes.</summary>
    public static PdfDataMergeBatchReport Create(
        IEnumerable<PdfDataMergeDocumentResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        PdfDataMergeBatchReportItem[] items = [.. results.Select(result =>
            new PdfDataMergeBatchReportItem(result.RecordIndex, result.OutputFileName,
                result.Succeeded, result.Error))];
        int succeeded = items.Count(item => item.Succeeded);
        return new PdfDataMergeBatchReport(items.Length, succeeded,
            items.Length - succeeded, Array.AsReadOnly(items));
    }

    /// <summary>Exports the batch summary as machine-readable JSON.</summary>
    public string ToJson(bool indented = false) => JsonSerializer.Serialize(
        new { Version = 1, TotalRecords, SucceededRecords, FailedRecords, Results },
        new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = indented
        });
}

/// <summary>One data-free result in a form-generation batch report.</summary>
public sealed record PdfDataMergeBatchReportItem(
    int RecordIndex, string? OutputFileName, bool Succeeded, string? Error);
