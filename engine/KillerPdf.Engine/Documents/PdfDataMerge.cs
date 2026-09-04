using System.Text;
using System.Text.Json;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Parsing;

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
        PdfDataMergeProfile profile, CancellationToken cancellationToken = default) =>
        RunFormBatch(template, records, profile,
            PdfDataMergeOutputMode.Editable, cancellationToken);

    /// <summary>Maps records into editable or flattened AcroForm output.</summary>
    public static IReadOnlyList<PdfDataMergeDocumentResult> RunFormBatch(
        PdfDocument template, IEnumerable<IReadOnlyDictionary<string, string?>> records,
        PdfDataMergeProfile profile, PdfDataMergeOutputMode outputMode,
        CancellationToken cancellationToken = default) =>
        RunFormBatchCore(template, records, profile, outputMode, null, cancellationToken);

    /// <summary>Maps records with caller-resolved images into editable or flattened output.</summary>
    public static IReadOnlyList<PdfDataMergeDocumentResult> RunFormBatch(
        PdfDocument template, IEnumerable<IReadOnlyDictionary<string, string?>> records,
        PdfDataMergeProfile profile, Func<string, PdfImage> imageResolver,
        PdfDataMergeOutputMode outputMode = PdfDataMergeOutputMode.Editable,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(imageResolver);
        return RunFormBatchCore(template, records, profile, outputMode,
            imageResolver, cancellationToken);
    }

    private static IReadOnlyList<PdfDataMergeDocumentResult> RunFormBatchCore(
        PdfDocument template, IEnumerable<IReadOnlyDictionary<string, string?>> records,
        PdfDataMergeProfile profile, PdfDataMergeOutputMode outputMode,
        Func<string, PdfImage>? imageResolver, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(profile);
        if (!Enum.IsDefined(outputMode))
            throw new ArgumentOutOfRangeException(nameof(outputMode));
        var results = new List<PdfDataMergeDocumentResult>();
        var usedFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int index = 0;
        foreach (IReadOnlyDictionary<string, string?> record in records)
        {
            if (cancellationToken.IsCancellationRequested) break;
            if (!profile.Includes(record))
            {
                results.Add(new PdfDataMergeDocumentResult(index, null, null, null)
                {
                    Skipped = true
                });
                index++;
                continue;
            }
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
                byte[] data = mapped.FormData.Fields.Count == 0
                    ? template.Source.ToArray()
                    : PdfFormDataImporter.Apply(template, mapped.FormData);
                if (mapped.TextReplacements.Count > 0)
                    data = ApplyTextReplacements(PdfDocument.Open(data),
                        mapped.TextReplacements, cancellationToken);
                if (mapped.Images.Count > 0)
                {
                    if (imageResolver is null)
                        throw new InvalidOperationException(
                            "The data-merge profile requires an image resolver.");
                    data = ApplyImages(PdfDocument.Open(data), mapped.Images,
                        imageResolver, cancellationToken);
                }
                if (outputMode == PdfDataMergeOutputMode.Flattened)
                    data = PdfFormFlattener.Flatten(PdfDocument.Open(data));
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

    private static byte[] ApplyImages(PdfDocument document,
        IReadOnlyList<PdfDataMergeMappedImage> images, Func<string, PdfImage> imageResolver,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<PdfPageInformation> pages = PdfPageInformation.Read(document);
        var editor = new PdfIncrementalPageEditor(document);
        foreach (PdfDataMergeMappedImage mapped in images)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PdfDataMergeImageMapping placement = mapped.Mapping;
            if (placement.PageIndex >= pages.Count)
                throw new InvalidOperationException(
                    $"Image mapping '{placement.SourceField}' targets a missing page.");
            PdfPageInformation page = pages[placement.PageIndex];
            if (placement.X + placement.Width > page.Width
                || placement.Y + placement.Height > page.Height)
                throw new InvalidOperationException(
                    $"Image mapping '{placement.SourceField}' extends outside its page.");
            PdfImage image;
            try
            {
                image = imageResolver(mapped.SourceValue)
                    ?? throw new InvalidOperationException("The image resolver returned no image.");
            }
            catch (Exception exception) when (exception is not OutOfMemoryException
                and not StackOverflowException and not AccessViolationException)
            {
                throw new InvalidOperationException(
                    $"Image mapping '{placement.SourceField}' could not be resolved.", exception);
            }
            editor.AppendPageContent(placement.PageIndex, page.Width, page.Height,
                new PdfContentStreamBuilder().DrawImage(image, placement.X, placement.Y,
                    placement.Width, placement.Height));
        }
        return editor.Build();
    }

    private static byte[] ApplyTextReplacements(PdfDocument document,
        IReadOnlyDictionary<string, string> replacements,
        CancellationToken cancellationToken)
    {
        var reader = new PdfPageContentReader(document);
        var editor = new PdfIncrementalPageEditor(document);
        var counts = replacements.Keys.ToDictionary(key => key, _ => 0,
            StringComparer.Ordinal);
        for (int pageIndex = 0; pageIndex < reader.PageCount; pageIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<PdfContentInstruction> instructions =
                reader.ReadInstructions(pageIndex, cancellationToken);
            bool changed = false;
            foreach ((string placeholder, string value) in replacements)
            {
                instructions = PdfContentTransformation.ReplaceLatin1Text(
                    instructions, new Dictionary<string, string>
                    {
                        [placeholder] = value
                    }, out int count);
                counts[placeholder] += count;
                changed |= count > 0;
            }
            if (changed) editor.SetPageContent(pageIndex, instructions);
        }
        string[] unmatched = [.. counts.Where(item => item.Value == 0)
            .Select(item => item.Key)];
        if (unmatched.Length > 0)
            throw new InvalidOperationException("The template does not contain text placeholder(s): "
                + string.Join(", ", unmatched) + ".");
        return editor.Build();
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
            IReadOnlyList<PdfDataMergeTextMatch> textMatches =
                PreviewTextReplacements(template, mapped.TextReplacements);
            IReadOnlyList<PdfDataMergeImageMatch> imageMatches =
                PreviewImages(template, mapped.Images);
            PdfFormDataMatch[] blocked = [.. matches.Where(match =>
                match.Status != PdfFormDataMatchStatus.Matched)];
            string[] missingText = [.. textMatches.Where(match => !match.Matched)
                .Select(match => match.Placeholder)];
            var blockedTargets = blocked.Select(match => match.FieldName)
                .Concat(missingText)
                .Concat(imageMatches.Where(match => !match.Matched)
                    .Select(match => match.SourceField)).ToArray();
            string? error = blockedTargets.Length == 0 ? null
                : "The record cannot be applied to: "
                    + string.Join(", ", blockedTargets) + ".";
            return new PdfDataMergePreview(mapped.OutputFileName, matches, error)
            {
                TextPlaceholders = textMatches,
                Images = imageMatches
            };
        }
        catch (Exception exception) when (exception is not OutOfMemoryException
            and not StackOverflowException and not AccessViolationException)
        {
            return new PdfDataMergePreview(null, [], exception.Message);
        }
    }

    private static IReadOnlyList<PdfDataMergeImageMatch> PreviewImages(
        PdfDocument document, IReadOnlyList<PdfDataMergeMappedImage> images)
    {
        IReadOnlyList<PdfPageInformation> pages = PdfPageInformation.Read(document);
        return Array.AsReadOnly(images.Select(mapped =>
        {
            PdfDataMergeImageMapping placement = mapped.Mapping;
            bool pageExists = placement.PageIndex < pages.Count;
            bool fits = pageExists
                && placement.X + placement.Width <= pages[placement.PageIndex].Width
                && placement.Y + placement.Height <= pages[placement.PageIndex].Height;
            return new PdfDataMergeImageMatch(placement.SourceField, placement.PageIndex,
                placement.X, placement.Y, placement.Width, placement.Height, fits);
        }).ToArray());
    }

    private static IReadOnlyList<PdfDataMergeTextMatch> PreviewTextReplacements(
        PdfDocument document, IReadOnlyDictionary<string, string> replacements)
    {
        var reader = new PdfPageContentReader(document);
        var counts = replacements.Keys.ToDictionary(key => key, _ => 0,
            StringComparer.Ordinal);
        for (int pageIndex = 0; pageIndex < reader.PageCount; pageIndex++)
        {
            IReadOnlyList<PdfContentInstruction> instructions =
                reader.ReadInstructions(pageIndex);
            foreach ((string placeholder, string value) in replacements)
            {
                _ = PdfContentTransformation.ReplaceLatin1Text(instructions,
                    new Dictionary<string, string> { [placeholder] = value },
                    out int count);
                counts[placeholder] += count;
            }
        }
        return Array.AsReadOnly(counts.Select(item =>
            new PdfDataMergeTextMatch(item.Key, item.Value)).ToArray());
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

/// <summary>Controls whether generated form fields remain editable.</summary>
public enum PdfDataMergeOutputMode
{
    /// <summary>Keep generated form fields editable.</summary>
    Editable,
    /// <summary>Paint widget appearances into vector page content and remove the fields.</summary>
    Flattened
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
    /// <summary>Gets whether the reusable profile excluded this record.</summary>
    public bool Skipped { get; init; }
    /// <summary>Gets whether PDF generation succeeded.</summary>
    public bool Succeeded => !Skipped && Data.HasValue && Error is null;
}

/// <summary>A combined PDF and the successful source records included in it.</summary>
public sealed record PdfDataMergeCombinedResult(
    byte[] Document, IReadOnlyList<int> IncludedRecordIndices);

/// <summary>A data-free preview of one mapped record.</summary>
public sealed record PdfDataMergePreview(string? OutputFileName,
    IReadOnlyList<PdfFormDataMatch> Fields, string? Error)
{
    /// <summary>Gets data-free page-text placeholder matches.</summary>
    public IReadOnlyList<PdfDataMergeTextMatch> TextPlaceholders { get; init; } = [];
    /// <summary>Gets data-free image placement matches.</summary>
    public IReadOnlyList<PdfDataMergeImageMatch> Images { get; init; } = [];
    /// <summary>Gets whether the record can be generated.</summary>
    public bool CanGenerate => Error is null
        && Fields.All(match => match.Status == PdfFormDataMatchStatus.Matched)
        && TextPlaceholders.All(match => match.Matched)
        && Images.All(match => match.Matched);
}

/// <summary>A data-free image placement match.</summary>
public sealed record PdfDataMergeImageMatch(string SourceField, int PageIndex,
    double X, double Y, double Width, double Height, bool Matched);

/// <summary>A data-free page-text placeholder match.</summary>
public sealed record PdfDataMergeTextMatch(string Placeholder, int OccurrenceCount)
{
    /// <summary>Gets whether the placeholder occurs in the template.</summary>
    public bool Matched => OccurrenceCount > 0;
}

/// <summary>A data-free summary of one form-generation batch.</summary>
public sealed record PdfDataMergeBatchReport(
    int TotalRecords, int SucceededRecords, int FailedRecords,
    IReadOnlyList<PdfDataMergeBatchReportItem> Results)
{
    /// <summary>Gets the count of records excluded by the reusable profile.</summary>
    public int SkippedRecords { get; init; }
    /// <summary>Creates a report without retaining generated PDF bytes.</summary>
    public static PdfDataMergeBatchReport Create(
        IEnumerable<PdfDataMergeDocumentResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        PdfDataMergeBatchReportItem[] items = [.. results.Select(result =>
            new PdfDataMergeBatchReportItem(result.RecordIndex, result.OutputFileName,
                result.Succeeded, result.Error) { Skipped = result.Skipped })];
        int succeeded = items.Count(item => item.Succeeded);
        int skipped = items.Count(item => item.Skipped);
        return new PdfDataMergeBatchReport(items.Length, succeeded,
            items.Length - succeeded - skipped, Array.AsReadOnly(items))
        {
            SkippedRecords = skipped
        };
    }

    /// <summary>Exports the batch summary as machine-readable JSON.</summary>
    public string ToJson(bool indented = false) => JsonSerializer.Serialize(
        new { Version = 1, TotalRecords, SucceededRecords, SkippedRecords, FailedRecords, Results },
        new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = indented
        });
}

/// <summary>One data-free result in a form-generation batch report.</summary>
public sealed record PdfDataMergeBatchReportItem(
    int RecordIndex, string? OutputFileName, bool Succeeded, string? Error)
{
    /// <summary>Gets whether the reusable profile excluded this record.</summary>
    public bool Skipped { get; init; }
}
