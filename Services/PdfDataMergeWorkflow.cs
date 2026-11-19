using System.IO;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Parsing;

namespace KillerPDF.Services;

internal sealed record PdfDataMergePlan(
    PdfDataMergeProfile Profile,
    IReadOnlyList<IReadOnlyDictionary<string, string?>> Records,
    IReadOnlyList<string> OutputFileNames,
    int MatchedFieldCount);

internal sealed record PdfDataMergeMacroPlan(PdfDataMergePlan Merge, IReadOnlyList<string> OutputPaths);

/// <summary>No data column matches a form field or mapping name. The merge dialog says so in the
/// interface language (Str_DataMerge_NoColumns); the English message serves the CLI and macros.</summary>
internal sealed class DataMergeNoColumnsException()
    : InvalidOperationException("No merge-data columns match the PDF form field names or mapping names.");

internal static class PdfDataMergeWorkflow
{
    private const string OutputNameField = "__KillerPDFOutputFileName";

    internal static PdfDataMergeMacroPlan PlanMacro(PdfMacro macro,
        IReadOnlyList<IReadOnlyDictionary<string, string?>> records, string sourcePath,
        string firstOutput, int oneBasedInputIndex = 1, IEnumerable<string>? protectedSources = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(oneBasedInputIndex);
        int[] mergeSteps = macro.Steps.Select((step, index) => (step, index))
            .Where(item => item.step.Operation == PdfMacroOperation.DataMerge).Select(item => item.index).ToArray();
        if (mergeSteps.Length != 1)
            throw new ArgumentException("A file-output macro requires exactly one Data Merge step.", nameof(macro));
        if (records.Count == 0) throw new ArgumentException("Merge data has no records.", nameof(records));
        PdfDataMergeProfile profile = PdfDataMergeProfile.FromMacroStep(macro.Steps[mergeSteps[0]]);
        string directory = Path.GetDirectoryName(Path.GetFullPath(firstOutput))!;
        string stem = Path.GetFileNameWithoutExtension(firstOutput);
        var prepared = new List<IReadOnlyDictionary<string, string?>>(records.Count);
        var generatedNames = new List<string>();
        var outputPaths = new List<string>();
        var protectedPaths = (protectedSources ?? []).Append(sourcePath)
            .Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach ((var record, int index) in records.Select((record, index) => (record, index)))
        {
            if (record.Keys.Contains(OutputNameField, StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException("Merge data uses a reserved field name.", nameof(records));
            var copy = new Dictionary<string, string?>(record, StringComparer.OrdinalIgnoreCase)
            {
                [OutputNameField] = stem + (index == 0 ? "" : "-" + (index + 1)) + ".pdf"
            };
            prepared.Add(new System.Collections.ObjectModel.ReadOnlyDictionary<string, string?>(copy));
            if (!profile.Includes(copy)) continue;
            string name = PdfDataMerge.Expand(profile.OutputFileNameTemplate, copy, profile.MissingValueBehavior);
            if (name != Path.GetFileName(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || !Path.GetExtension(name).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Every output name must be a PDF filename.", nameof(macro));
            generatedNames.Add(name);
            var context = PdfRenameMacro.CreateContext(name, oneBasedInputIndex);
            foreach (PdfMacroStep rename in macro.Steps.Skip(mergeSteps[0] + 1)
                .Where(step => step.Operation == PdfMacroOperation.Rename))
            {
                PdfMacroOperationResult renamed = PdfRenameMacro.Execute(rename, ReadOnlyMemory<byte>.Empty, context);
                name = renamed.OutputFileName!;
                context = PdfRenameMacro.CreateContext(name, oneBasedInputIndex);
            }
            string path = Path.GetFullPath(Path.Combine(directory, name));
            if (protectedPaths.Contains(path)) throw new ArgumentException("Macro output cannot replace a source PDF.", nameof(firstOutput));
            outputPaths.Add(path);
        }
        if (generatedNames.Distinct(StringComparer.OrdinalIgnoreCase).Count() != generatedNames.Count
            || outputPaths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != outputPaths.Count)
            throw new ArgumentException("Data-merge output filenames must be unique.", nameof(macro));
        return new(new(profile, prepared.AsReadOnly(), generatedNames.AsReadOnly(), profile.Mappings.Count),
            PdfMacroExportFiles.Plan(macro, outputPaths, protectedPaths));
    }

    internal static PdfImage LoadImage(string path)
    {
        using var stream = File.OpenRead(path);
        var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(stream,
            System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
            System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
        var image = new System.Windows.Media.Imaging.FormatConvertedBitmap(decoder.Frames[0],
            System.Windows.Media.PixelFormats.Bgra32, null, 0);
        int stride = checked(image.PixelWidth * 4);
        byte[] rgba = new byte[checked(stride * image.PixelHeight)];
        image.CopyPixels(rgba, stride, 0);
        for (int index = 0; index < rgba.Length; index += 4)
            (rgba[index], rgba[index + 2]) = (rgba[index + 2], rgba[index]);
        return PdfImage.FromRgba(image.PixelWidth, image.PixelHeight, rgba);
    }

    internal static IReadOnlyList<IReadOnlyDictionary<string, string?>> LoadRecords(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".csv" => PdfDataRecordReader.FromCsv(File.ReadAllText(path)),
            ".json" => PdfDataRecordReader.FromJson(File.ReadAllText(path)),
            ".xlsx" => PdfDataRecordReader.FromXlsx(File.ReadAllBytes(path)),
            _ => throw new NotSupportedException("Merge data must be CSV, JSON, or XLSX.")
        };

    internal static PdfDataMergePlan CreatePlan(
        PdfDocument template,
        IReadOnlyList<IReadOnlyDictionary<string, string?>> records,
        string firstOutputFileName, PdfDataMergeProfile? configuredProfile = null)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count == 0)
            throw new ArgumentException("Merge data has no records.", nameof(records));
        if (string.IsNullOrWhiteSpace(firstOutputFileName))
            throw new ArgumentException("An output filename is required.", nameof(firstOutputFileName));

        var sourceNames = new List<string>();
        var knownSourceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (IReadOnlyDictionary<string, string?> record in records)
            foreach (string sourceName in record.Keys)
                if (knownSourceNames.Add(sourceName)) sourceNames.Add(sourceName);
        if (knownSourceNames.Contains(OutputNameField))
            throw new ArgumentException("Merge data uses a reserved field name.", nameof(records));

        IReadOnlyList<PdfFormDataField> fields = PdfFormDataExporter.Export(
            template, includeAnnotations: false).Fields;
        var mappings = new List<PdfDataMergeFieldMapping>();
        foreach (PdfFormDataField field in fields)
        {
            string? sourceName = sourceNames.FirstOrDefault(name =>
                string.Equals(name, field.Name, StringComparison.OrdinalIgnoreCase));
            sourceName ??= sourceNames.FirstOrDefault(name =>
                !string.IsNullOrWhiteSpace(field.MappingName)
                && string.Equals(name, field.MappingName, StringComparison.OrdinalIgnoreCase));
            if (sourceName is not null)
                mappings.Add(new PdfDataMergeFieldMapping(sourceName, field.Name));
        }
        if (mappings.Count == 0 && configuredProfile is null)
            throw new DataMergeNoColumnsException();

        string extension = Path.GetExtension(firstOutputFileName);
        if (!extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase)) extension = ".pdf";
        string stem = Path.GetFileNameWithoutExtension(firstOutputFileName);
        if (string.IsNullOrWhiteSpace(stem)) stem = "merged";

        var outputNames = new string[records.Count];
        var preparedRecords = new IReadOnlyDictionary<string, string?>[records.Count];
        for (int index = 0; index < records.Count; index++)
        {
            outputNames[index] = index == 0
                ? stem + extension
                : $"{stem}-{index + 1}{extension}";
            var prepared = new Dictionary<string, string?>(
                records[index], StringComparer.OrdinalIgnoreCase)
            {
                [OutputNameField] = outputNames[index]
            };
            preparedRecords[index] = prepared;
        }

        var profile = configuredProfile ?? new PdfDataMergeProfile(
            "Desktop data merge", mappings, "{{" + OutputNameField + "}}");
        string[] plannedNames = preparedRecords.Where(profile.Includes).Select(record => profile.Map(record).OutputFileName).ToArray();
        if (plannedNames.Any(name => string.IsNullOrWhiteSpace(name) || name != Path.GetFileName(name)
            || !string.Equals(Path.GetExtension(name), ".pdf", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Every output name must be a PDF filename.", nameof(configuredProfile));
        if (plannedNames.Distinct(StringComparer.OrdinalIgnoreCase).Count() != plannedNames.Length)
            throw new ArgumentException("Data-merge output filenames must be unique.", nameof(configuredProfile));
        if (configuredProfile is not null)
            foreach (var record in preparedRecords.Where(profile.Includes))
            {
                var preview = PdfDataMerge.PreviewFormRecord(template, record, profile);
                if (preview.Error is not null) throw new ArgumentException(preview.Error, nameof(configuredProfile));
            }
        return new PdfDataMergePlan(profile, preparedRecords, plannedNames, profile.Mappings.Count);
    }

    /// <summary>
    /// Fills one copy of the template per record and writes each into the folder, replacing a
    /// file of the same name only once its new copy is complete. A copy that cannot be written
    /// is reported as failed rather than stopping the rest.
    /// </summary>
    internal static PdfDataMergeBatchReport RunToDirectory(byte[] templateBytes, PdfDataMergePlan plan,
        string outputDirectory, PdfDataMergeOutputMode mode, CancellationToken token,
        Func<string, PdfImage>? imageResolver = null, IReadOnlySet<string>? approvedOverwrites = null)
    {
        IReadOnlyList<PdfDataMergeDocumentResult> generated = imageResolver is null
            ? PdfDataMerge.RunFormBatch(PdfDocument.Open(templateBytes), plan.Records, plan.Profile, mode, token)
            : PdfDataMerge.RunFormBatch(PdfDocument.Open(templateBytes), plan.Records, plan.Profile, imageResolver, mode, token);
        var completed = new List<PdfDataMergeDocumentResult>(generated.Count);
        foreach (PdfDataMergeDocumentResult result in generated)
        {
            if (token.IsCancellationRequested) break;
            if (!result.Succeeded || result.Data is null || result.OutputFileName is null)
            {
                completed.Add(result);
                continue;
            }
            string destination = Path.Combine(outputDirectory, result.OutputFileName);
            string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temporary, result.Data.Value.ToArray());
                token.ThrowIfCancellationRequested();
                // Only outputs the caller approved by name are replaced; without a list, an
                // existing file stays and the record is reported as failed.
                File.Move(temporary, destination, overwrite: approvedOverwrites is not null
                    && (approvedOverwrites.Contains(destination) || approvedOverwrites.Contains(Path.GetFullPath(destination))));
                completed.Add(result);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException
                and not StackOverflowException and not AccessViolationException)
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch { }
                completed.Add(new PdfDataMergeDocumentResult(
                    result.RecordIndex, result.OutputFileName, null, ex.Message));
            }
        }
        return PdfDataMergeBatchReport.Create(completed);
    }
}
