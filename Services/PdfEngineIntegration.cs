using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Diagnostics;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Filters;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Parsing;
using KillerPdf.Engine.Signing;
using KillerPdf.Engine.Syntax;
using KillerPdf.Engine.Writing;
using DrawingBitmap = System.Drawing.Bitmap;
using DrawingGraphics = System.Drawing.Graphics;
using DrawingImage = System.Drawing.Image;

namespace KillerPDF.Services;

/// <summary>Bridges completed application state into The KillerPDF.Engine during migration.</summary>
internal static partial class PdfEngineIntegration
{
    internal static string[] PlanMacroOutputs(PdfMacro macro, IReadOnlyList<string> sources,
        string firstOutput, IEnumerable<string>? additionalSources = null)
    {
        string[] outputs;
        if (macro.Steps.Any(step => step.Operation == PdfMacroOperation.Rename))
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(firstOutput))!;
            outputs = PdfRenameMacro.PlanFileNames(macro, sources)
                .Select(name => Path.Combine(directory, name)).ToArray();
        }
        else outputs = PlanBatchOutputs(sources, firstOutput);
        var protectedPaths = sources.Concat(additionalSources ?? [])
            .Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _ = PdfMacroExportFiles.Plan(macro, outputs, protectedPaths);
        return outputs;
    }

    internal static string[] PlanBatchOutputs(IEnumerable<string> sources, string firstOutput)
    {
        string[] inputs = sources.Select(Path.GetFullPath).ToArray();
        string full = Path.GetFullPath(firstOutput);
        string directory = Path.GetDirectoryName(full)!;
        string stem = Path.GetFileNameWithoutExtension(full);
        string[] outputs = Enumerable.Range(0, inputs.Length).Select(index => Path.Combine(directory,
            stem + (index == 0 ? "" : "-" + (index + 1)) + ".pdf")).ToArray();
        if (outputs.Any(output => inputs.Contains(output, StringComparer.OrdinalIgnoreCase)))
            throw new ArgumentException("Batch output cannot replace a source PDF.", nameof(firstOutput));
        return outputs;
    }

    internal static void ApplyDocumentRepair(string path, Func<PdfDocument, byte[]> build) =>
        ReplaceWithBuiltResult(path, build(PdfDocument.Open(File.ReadAllBytes(path))));

    internal static void ApplyRecognizedFields(string path, PdfFormRecognitionReview review) =>
        ReplaceWithBuiltResult(path, review.ApplyAccepted(PdfDocument.Open(File.ReadAllBytes(path))));

    internal static void WriteExchangeFile(string path, byte[] bytes) => ReplaceWithBuiltResult(path, bytes);

    internal static void ApplyBookmarkData(string path, string json, bool replaceExisting) =>
        ReplaceWithBuiltResult(path, PdfBookmarkExchange.Import(
            PdfDocument.Open(File.ReadAllBytes(path)), json, replaceExisting));

    internal static byte[] ExportFormData(PdfDocument document, string extension,
        string? sourceName = null, bool includeComments = true)
    {
        PdfFormDataSet data = PdfFormDataExporter.Export(document, sourceName,
            includeAnnotations: includeComments);
        return extension.ToLowerInvariant() switch
        {
            ".fdf" => PdfFdfFormData.Write(data),
            ".xfdf" => PdfXfdfFormData.Write(data),
            ".json" => Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(data,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true })),
            _ => throw new ArgumentException("Expected FDF, XFDF or JSON.", nameof(extension))
        };
    }

    internal static PdfFormDataSet ReadFormData(byte[] bytes, string extension) =>
        extension.ToLowerInvariant() switch
        {
            ".fdf" => PdfFdfFormData.Read(bytes),
            ".xfdf" => PdfXfdfFormData.Read(bytes),
            _ => throw new ArgumentException("Expected FDF or XFDF.", nameof(extension))
        };

    internal static void ApplyFormData(string path, PdfFormDataSet data, bool flatten = false) =>
        ReplaceWithBuiltResult(path, PdfFormDataImporter.Apply(
            PdfDocument.Open(File.ReadAllBytes(path)), data, flatten
                ? PdfFormDataImportOutputMode.Flattened : PdfFormDataImportOutputMode.Editable));

    internal sealed record LayerEdit(
        int ObjectNumber, string Name, bool IsVisible, bool IsLocked);

    internal sealed record PortfolioEditorState(
        PdfCollectionInfo? Collection, IReadOnlyList<PortfolioAttachmentItem> Attachments);

    internal sealed record PortfolioAttachmentItem(
        string FileName, IReadOnlyList<PdfCollectionItemValue> Values);

    internal sealed record AttachmentEditorItem(
        string OriginalFileName, string FileName, string? Description,
        string MimeType, PdfAssociatedFileRelationship Relationship, int ByteCount,
        bool Remove = false, string? Key = null);

    internal sealed record FormEdits(
        IReadOnlyDictionary<string, string> TextValues,
        IReadOnlyDictionary<string, string> ChoiceValues,
        IReadOnlyDictionary<string, IReadOnlyList<string>> MultiChoiceValues,
        IReadOnlyDictionary<string, bool> CheckBoxValues,
        IReadOnlyDictionary<string, string> RadioValues,
        IReadOnlyDictionary<string, double> TextFontSizes);

    /// <summary>
    /// Opens a user's PDF for reading. An owner-restricted file is decrypted with the empty
    /// user password, so its strings and streams read as text instead of ciphertext.
    /// </summary>
    internal static PdfDocument OpenForReading(ReadOnlyMemory<byte> source)
    {
        PdfDocument document = PdfDocument.OpenWithCompatibilityRecovery(source);
        if (!document.IsEncrypted) return document;
        try { return PdfDocument.OpenWithCompatibilityRecovery(source, string.Empty); }
        catch (Exception error) when (error is not OutOfMemoryException) { return document; }
    }

    /// <summary>
    /// Opens a PDF whose pages are copied into another document. The viewer reads files through
    /// compatibility recovery, so a page source that only the strict parser refuses is retried the
    /// same way. No password is supplied, so an encrypted source keeps its strict-open permissions.
    /// The source bytes are never written back. A protected file whose trailer is gone is refused,
    /// because its pages read as ciphertext and would be copied as pages nobody can read.
    /// </summary>
    internal static PdfDocument OpenPageSource(ReadOnlyMemory<byte> source)
    {
        try { return PdfDocument.Open(source); }
        catch (Exception strict) when (IsImportFailure(strict))
        {
            PdfDocument recovered;
            try { recovered = PdfDocument.OpenWithCompatibilityRecovery(source); }
            catch (Exception recovery) when (IsImportFailure(recovery))
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(strict).Throw();
                throw;
            }
            if (HidesEncryption(source)) throw new DamagedSaveRefusedException(DamagedSaveRefusal.Encrypted);
            return recovered;
        }
    }

    // An incremental merge appends to its first document's bytes, so that document must open
    // strictly. When it cannot, the merge starts from an empty document and copies it in instead.
    private static (PdfIncrementalPageEditor Editor, PdfDocument? CopiedFirst) OpenMergeTarget(
        byte[] first, bool includeBookmarks = true)
    {
        try { return (new PdfIncrementalPageEditor(PdfDocument.Open(first)), null); }
        catch (Exception strict) when (IsImportFailure(strict))
        {
            PdfDocument copied = OpenPageSource(first);
            var editor = new PdfIncrementalPageEditor(PdfDocument.Open(new PdfDocumentBuilder().Build()))
                .AddImportedDocument(copied, includeBookmarks);
            return (editor, copied);
        }
    }

    /// <summary>True when the engine can read the file without asking for a password.</summary>
    internal static bool CanReadWithoutPassword(string path)
    {
        try { return OpenForReading(File.ReadAllBytes(path)).IsDecrypted; }
        catch (Exception error) when (error is not OutOfMemoryException) { return false; }
    }

    /// <summary>Reads the complete bookmark hierarchy for the desktop sidebar.</summary>
    internal static IReadOnlyList<PdfBookmarkInfo> ReadBookmarks(byte[] source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return PdfBookmarkReader.Read(OpenForReading(source));
    }

    /// <summary>Reads native links from one page for viewer hit testing.</summary>
    internal static IReadOnlyList<PdfLinkInfo> ReadPageLinks(string path, int pageIndex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return PdfLinkReader.ReadPage(OpenForReading(File.ReadAllBytes(path)), pageIndex);
    }

    /// <summary>Reads native links from an already parsed engine document.</summary>
    internal static IReadOnlyList<PdfLinkInfo> ReadPageLinks(PdfDocument document, int pageIndex)
    {
        ArgumentNullException.ThrowIfNull(document);
        return PdfLinkReader.ReadPage(document, pageIndex);
    }

    /// <summary>Reads interactive form widgets from an already parsed engine document.</summary>
    internal static IReadOnlyList<PdfFormWidgetInfo> ReadPageFormWidgets(
        PdfDocument document, int pageIndex, bool forDisplay = false)
    {
        ArgumentNullException.ThrowIfNull(document);
        IReadOnlyList<PdfFormWidgetInfo> widgets = PdfFormWidgetReader.ReadPage(document, pageIndex);
        // Hidden and NoView apply to interactive controls as well as saved appearances.
        return forDisplay ? [.. widgets.Where(widget => (widget.AnnotationFlags & 34) == 0)] : widgets;
    }

    internal static IReadOnlyList<PdfFormWidgetInfo> ReadPageFormWidgets(
        string path, int pageIndex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return ReadPageFormWidgets(OpenForReading(File.ReadAllBytes(path)), pageIndex);
    }

    internal static IReadOnlyList<IReadOnlyList<PdfFormWidgetInfo>> ReadAllPageFormWidgets(
        string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        PdfDocument document = OpenForReading(File.ReadAllBytes(path));
        int pageCount = PdfPageInformation.Read(document).Count;
        return
        [
            .. Enumerable.Range(0, pageCount).Select(index =>
                (IReadOnlyList<PdfFormWidgetInfo>)PdfFormWidgetReader.ReadPage(document, index))
        ];
    }

    /// <summary>Inspects page boxes, colorants, and output intents for print workflows.</summary>
    internal static PdfPrintProductionReport InspectPrintProduction(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return PdfPrintProductionReport.Inspect(OpenForReading(File.ReadAllBytes(path)));
    }

    /// <summary>Runs the complete built-in print-production preflight profile.</summary>
    internal static PdfPreflightReport InspectPreflight(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return PdfPreflightRunner.Run(File.ReadAllBytes(path), PdfPreflightProfile.PrintProduction);
    }

    /// <summary>Runs a caller-supplied preflight profile, such as one loaded from a profile file.</summary>
    internal static PdfPreflightReport InspectPreflight(string path, PdfPreflightProfile profile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(profile);
        return PdfPreflightRunner.Run(File.ReadAllBytes(path), profile);
    }

    /// <summary>Inspects implemented accessibility requirements and document structure.</summary>
    internal static PdfAccessibilityReport InspectAccessibility(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return PdfAccessibilityInspector.Inspect(OpenForReading(File.ReadAllBytes(path)));
    }

    /// <summary>Checks signature integrity, Windows and application certificate trust,
    /// and later document changes without downloading certificates.</summary>
    internal static PdfSignatureInspectionReport InspectSignatures(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new Signing.CertificateCatalog().Inspect(OpenForReading(File.ReadAllBytes(path)));
    }

    /// <summary>Reads the document's optional-content layer definitions and state.</summary>
    internal static PdfOptionalContentInfo InspectLayers(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return PdfOptionalContentReader.Read(OpenForReading(File.ReadAllBytes(path)));
    }

    /// <summary>Applies reviewed layer names, saved visibility, and lock state.</summary>
    internal static void ApplyLayerEdits(string path, IReadOnlyList<LayerEdit> edits)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(edits);
        byte[] result = File.ReadAllBytes(path);
        PdfDocument document = PdfDocument.Open(result);
        PdfOptionalContentInfo original = PdfOptionalContentReader.Read(document);
        var groups = original.Groups.ToDictionary(group => group.ObjectNumber);

        foreach (LayerEdit edit in edits)
        {
            if (!groups.TryGetValue(edit.ObjectNumber, out PdfOptionalContentGroupInfo? group))
                throw new InvalidOperationException("A selected layer no longer exists.");
            if (!string.Equals(edit.Name, group.Name, StringComparison.Ordinal))
            {
                result = PdfOptionalContentEditor.RenameGroup(document, edit.ObjectNumber, edit.Name);
                document = PdfDocument.Open(result);
            }
            if (edit.IsVisible != original.ViewVisibleGroupObjectNumbers.Contains(edit.ObjectNumber))
            {
                result = PdfOptionalContentEditor.SetInitialVisibility(
                    document, edit.ObjectNumber, edit.IsVisible);
                document = PdfDocument.Open(result);
            }
            if (edit.IsLocked != group.IsLocked)
            {
                result = PdfOptionalContentEditor.SetLocked(
                    document, edit.ObjectNumber, edit.IsLocked);
                document = PdfDocument.Open(result);
            }
        }

        ReplaceWithBuiltResult(path, result);
    }

    /// <summary>Formats portfolio metadata without loading embedded-file payloads.</summary>
    internal static string InspectPortfolio(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return PdfCollectionReader.ToText(OpenForReading(File.ReadAllBytes(path)));
    }

    internal static PortfolioEditorState ReadPortfolioEditorState(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        PdfDocument document = OpenForReading(File.ReadAllBytes(path));
        return new PortfolioEditorState(
            PdfCollectionReader.Read(document),
            [.. PdfAttachmentReader.Read(document)
                .Select(attachment => new PortfolioAttachmentItem(
                    attachment.FileName, attachment.CollectionValues))]);
    }

    internal static void ApplyPortfolioPresentation(
        string path, PdfCollectionView view, string? initialDocument)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ReadOnlyMemory<byte> result = PdfCollectionMacro.Execute(
            PdfCollectionMacro.PresentationStep(view, initialDocument),
            File.ReadAllBytes(path));
        ReplaceWithBuiltResult(path, result.ToArray());
    }

    internal static void ClearPortfolioMetadata(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ReadOnlyMemory<byte> result = PdfCollectionMacro.Execute(
            PdfCollectionMacro.ClearStep(), File.ReadAllBytes(path));
        ReplaceWithBuiltResult(path, result.ToArray());
    }

    internal static void ApplyPortfolioStructure(
        string path,
        IReadOnlyList<PdfCollectionFieldInfo> fields,
        IReadOnlyList<PdfCollectionSortInfo> sort,
        IReadOnlyList<PdfCollectionFolder> folders,
        IReadOnlyDictionary<string, IReadOnlyList<PdfCollectionItemValue>> itemValues)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(sort);
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(itemValues);
        byte[] result = File.ReadAllBytes(path);
        result = PdfCollectionEditor.SetSchema(PdfDocument.Open(result), fields, sort);
        result = PdfCollectionEditor.SetFolders(PdfDocument.Open(result), folders);
        foreach ((string fileName, IReadOnlyList<PdfCollectionItemValue> values) in itemValues)
            result = PdfCollectionEditor.SetItemValues(
                PdfDocument.Open(result), fileName, values);
        ReplaceWithBuiltResult(path, result);
    }

    internal static IReadOnlyList<AttachmentEditorItem> ReadAttachmentEditorItems(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return [.. PdfAttachmentReader.Read(PdfDocument.Open(File.ReadAllBytes(path)))
            .Select(attachment => new AttachmentEditorItem(
                attachment.FileName, attachment.FileName, attachment.Description,
                attachment.MimeType, attachment.Relationship, attachment.Data.Length,
                Key: attachment.NameTreeKey))];
    }

    // Picks the row a caller means by a name: the listed name first, exactly and then ignoring case, and
    // only when no listed name matches the name-tree key. Several matches are ambiguous, not a guess.
    internal static AttachmentEditorItem? FindAttachmentEditorItem(
        IReadOnlyList<AttachmentEditorItem> items, string name, out bool ambiguous)
    {
        ambiguous = false;
        foreach (Func<AttachmentEditorItem, string?> selector in new Func<AttachmentEditorItem, string?>[]
                     { item => item.FileName, item => item.Key })
            foreach (StringComparison comparison in new[] { StringComparison.Ordinal, StringComparison.OrdinalIgnoreCase })
            {
                AttachmentEditorItem[] matches = [.. items.Where(item =>
                    string.Equals(selector(item), name, comparison))];
                if (matches.Length > 1) { ambiguous = true; return null; }
                if (matches.Length == 1) return matches[0];
            }
        return null;
    }

    internal static void AddAttachment(string path, string fileName, byte[] data)
    {
        byte[] result = new PdfIncrementalPageEditor(PdfDocument.Open(File.ReadAllBytes(path)))
            .AddAttachment(fileName, data, "application/octet-stream").Build();
        ReplaceWithBuiltResult(path, result);
    }

    // Each row names its file by name-tree key, since two files can list the same name and a file's key
    // can be another file's listed name. A row built without a key is matched by its original listed
    // name, which must pick exactly one file.
    internal static void ApplyAttachmentEdits(
        string path, IReadOnlyList<AttachmentEditorItem> edits)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(edits);
        byte[] result = File.ReadAllBytes(path);
        PdfAttachmentInfo[] current = [.. PdfAttachmentReader.Read(PdfDocument.Open(result))];
        string[] keys = [.. edits.Select(edit => edit.Key ?? KeyOfListedName(current, edit.OriginalFileName))];
        if (!current.Select(item => item.NameTreeKey!).OrderBy(key => key, StringComparer.Ordinal)
            .SequenceEqual(keys.OrderBy(key => key, StringComparer.Ordinal), StringComparer.Ordinal))
            throw new InvalidOperationException("The attachment list changed before it was saved.");

        // A name must be valid and unique only where the user changed it, so a file that shares a name
        // with another can still have its description or type edited.
        var rows = edits.Select((edit, index) => (Edit: edit, Key: keys[index])).ToArray();
        var remaining = rows.Where(row => !row.Edit.Remove).ToArray();
        bool Renamed(AttachmentEditorItem edit) =>
            !string.Equals(edit.OriginalFileName, edit.FileName, StringComparison.Ordinal);
        if (remaining.Any(row => string.IsNullOrWhiteSpace(row.Edit.FileName)
                || string.IsNullOrWhiteSpace(row.Edit.MimeType))
            || remaining.Any(row => Renamed(row.Edit) && remaining.Any(other =>
                other.Key != row.Key
                && string.Equals(other.Edit.FileName, row.Edit.FileName, StringComparison.OrdinalIgnoreCase))))
            throw new InvalidOperationException("Attachment names and MIME types must be valid and unique.");

        if (rows.Any(row => row.Edit.Remove))
        {
            var removal = new PdfIncrementalPageEditor(PdfDocument.Open(result));
            foreach (var row in rows.Where(row => row.Edit.Remove)) removal.RemoveAttachmentByKey(row.Key);
            result = removal.Build();
        }

        // A rename makes the new name the key, and each rewrite of a file copies its whole payload into
        // the file again, so a row is rewritten once, with its new name, description and type together.
        // Only when a file takes the name another still has, as two files swapping names do, does every
        // renamed file move to a temporary name first, and each row's key is followed through the move.
        var temporaryKeys = new Dictionary<string, string>(StringComparer.Ordinal);
        bool needsTemporaryNames = remaining.Any(row => Renamed(row.Edit) && remaining.Any(other =>
            other.Key != row.Key
            && string.Equals(other.Edit.OriginalFileName, row.Edit.FileName, StringComparison.OrdinalIgnoreCase)));
        if (needsTemporaryNames)
            foreach (var row in remaining.Where(row => Renamed(row.Edit)))
            {
                string temporary = $"killerpdf-{Guid.NewGuid():N}{Path.GetExtension(row.Edit.FileName)}";
                result = new PdfIncrementalPageEditor(PdfDocument.Open(result))
                    .RenameAttachmentByKey(row.Key, temporary).Build();
                temporaryKeys[row.Key] = temporary;
            }
        var originals = current.ToDictionary(item => item.NameTreeKey!, StringComparer.Ordinal);
        foreach (var row in remaining)
        {
            PdfAttachmentInfo original = originals[row.Key];
            AttachmentEditorItem edit = row.Edit;
            bool renamed = Renamed(edit);
            bool described = !string.Equals(edit.Description, original.Description, StringComparison.Ordinal);
            bool retyped = !string.Equals(edit.MimeType, original.MimeType, StringComparison.Ordinal)
                || edit.Relationship != original.Relationship;
            if (!renamed && !described && !retyped) continue;
            if (described && !string.IsNullOrEmpty(edit.Description) && string.IsNullOrWhiteSpace(edit.Description))
                throw new ArgumentException("An attachment description cannot be whitespace.");
            result = new PdfIncrementalPageEditor(PdfDocument.Open(result))
                .EditAttachmentByKey(needsTemporaryNames && renamed ? temporaryKeys[row.Key] : row.Key,
                    renamed ? edit.FileName : null,
                    retyped ? edit.MimeType : null, retyped ? edit.Relationship : null,
                    described, string.IsNullOrEmpty(edit.Description) ? null : edit.Description).Build();
        }
        ReplaceWithBuiltResult(path, result);
    }

    private static string KeyOfListedName(PdfAttachmentInfo[] current, string listedName)
    {
        PdfAttachmentInfo[] matches = [.. current.Where(item =>
            string.Equals(item.FileName, listedName, StringComparison.Ordinal))];
        return matches.Length == 1
            ? matches[0].NameTreeKey!
            : throw new InvalidOperationException("The attachment list changed before it was saved.");
    }

    internal static IReadOnlyList<PdfNavigationFinding> InspectNavigation(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return PdfNavigationAudit.Inspect(PdfDocument.Open(File.ReadAllBytes(path)));
    }

    internal static string FormatNavigationAudit(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return PdfNavigationAudit.ExportText(PdfDocument.Open(File.ReadAllBytes(path)));
    }

    internal static void RepairNavigationLinks(
        string path, bool removeUnsafeLinks, bool removeUnresolvedLinks)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!removeUnsafeLinks && !removeUnresolvedLinks)
            throw new ArgumentException("At least one navigation repair must be selected.");
        byte[] result = File.ReadAllBytes(path);
        if (removeUnsafeLinks)
        {
            result = PdfNavigationMacro.Execute(
                PdfNavigationMacro.RemoveUnsafeLinksStep(), result).ToArray();
        }
        if (removeUnresolvedLinks)
        {
            result = PdfNavigationMacro.Execute(
                PdfNavigationMacro.RemoveUnresolvedLinksStep(), result).ToArray();
        }
        ReplaceWithBuiltResult(path, result);
    }

    internal static IReadOnlyList<PdfBookmarkProposal> DetectBookmarkHeadings(
        string path, PdfBookmarkDetectionOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(options);
        return PdfBookmarkGeneration.DetectHeadings(
            PdfDocument.Open(File.ReadAllBytes(path)), options);
    }

    /// <summary>Whether any page has extractable text; a scan without OCR has none.</summary>
    internal static bool HasTextLayer(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var reader = new PdfPageContentReader(PdfDocument.Open(File.ReadAllBytes(path)));
        for (int page = 0; page < reader.PageCount; page++)
            if (reader.Read(page).Letters.Count > 0) return true;
        return false;
    }

    internal static void ApplyBookmarkProposals(
        string path, IReadOnlyList<PdfBookmarkProposal> proposals)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(proposals);
        byte[] result = PdfBookmarkGeneration.Apply(
            PdfDocument.Open(File.ReadAllBytes(path)), proposals);
        ReplaceWithBuiltResult(path, result);
    }

    internal static void ApplyPageLabels(
        string path, IReadOnlyList<PdfPageLabelMacroRange> ranges)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(ranges);
        ReadOnlyMemory<byte> result = PdfNavigationMacro.Execute(
            PdfNavigationMacro.PageLabelsStep(ranges), File.ReadAllBytes(path));
        ReplaceWithBuiltResult(path, result.ToArray());
    }

    /// <summary>Compares interpreted content, resources, and page geometry.</summary>
    internal static PdfStructuralComparison CompareStructure(string originalPath, string changedPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(originalPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(changedPath);
        return PdfStructuralComparison.Compare(
            PdfDocument.Open(File.ReadAllBytes(originalPath)),
            PdfDocument.Open(File.ReadAllBytes(changedPath)));
    }

    /// <summary>Plans a clickable table of contents from the current bookmark hierarchy.</summary>
    internal static PdfTableOfContentsPlan PlanTableOfContents(string path, int maximumDepth)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return PdfTableOfContentsPlanner.Plan(
            PdfDocument.Open(File.ReadAllBytes(path)), maximumDepth);
    }

    /// <summary>Inserts clickable table-of-contents pages at the start of a document.</summary>
    internal static PdfTableOfContentsWriteResult InsertTableOfContents(
        string path, string title, int maximumDepth)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        PdfTableOfContentsWriteResult result = PdfTableOfContentsWriter.Write(
            PdfDocument.Open(File.ReadAllBytes(path)), new PdfTableOfContentsWriteOptions
            {
                Title = title,
                MaximumDepth = maximumDepth
            });
        ReplaceWithBuiltResult(path, result.Document);
        return result;
    }

    /// <summary>Builds a validated separation selection for the requested pages.</summary>
    internal static PdfSeparationPreview CreateSeparationPreview(
        string path, IEnumerable<string> plateNames, IEnumerable<int>? pageIndexes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return PdfSeparationPreview.Create(
            PdfDocument.Open(File.ReadAllBytes(path)), plateNames, pageIndexes);
    }

    /// <summary>Reads review comments and their reply relationships for the desktop.</summary>
    internal static IReadOnlyList<PdfCommentThread> ReadCommentThreads(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return PdfCommentReader.ReadThreads(PdfDocument.Open(File.ReadAllBytes(path)));
    }

    /// <summary>Changes the text of a comment selected from the current document.</summary>
    internal static void SetCommentContents(string path, PdfCommentInfo comment, string contents)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(comment);
        PdfDocument document = PdfDocument.Open(File.ReadAllBytes(path));
        ReplaceWithBuiltResult(path, PdfCommentEditor.SetContents(document, comment, contents));
    }

    /// <summary>
    /// Applies the Comments window's changes (#288) as three revisions: new text, then removals,
    /// then new notes and replies. Each step reads the comments again and finds them by object
    /// number, so a removal that shifts annotation positions cannot send an edit to the wrong one.
    /// </summary>
    internal static void ApplyCommentEdits(string path, IReadOnlyList<CommentEdit> edits, string? author)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(edits);
        static (int, int, int) Key(PdfCommentInfo comment) =>
            comment.ObjectNumber is int number ? (-1, -1, number) : (comment.PageIndex, comment.AnnotationIndex, 0);
        static PdfCommentInfo Find(IReadOnlyList<PdfCommentInfo> current, PdfCommentInfo wanted) =>
            current.FirstOrDefault(comment => Key(comment) == Key(wanted))
            ?? throw new InvalidOperationException("A comment being changed is no longer in the document.");

        var removed = edits.OfType<CommentRemoval>().Select(edit => Key(edit.Comment)).ToHashSet();
        CommentTextEdit[] texts = [.. edits.OfType<CommentTextEdit>().Where(edit => !removed.Contains(Key(edit.Comment)))];
        if (texts.Length > 0)
        {
            PdfDocument document = PdfDocument.Open(File.ReadAllBytes(path));
            IReadOnlyList<PdfCommentInfo> current = PdfCommentReader.Read(document);
            var editor = new PdfIncrementalAnnotationEditor(document);
            foreach (CommentTextEdit edit in texts)
            {
                PdfCommentInfo comment = Find(current, edit.Comment);
                editor.SetAnnotationContentsAt(comment.PageIndex, comment.AnnotationIndex,
                    string.IsNullOrWhiteSpace(edit.Contents) ? null : edit.Contents);
            }
            ReplaceWithBuiltResult(path, editor.Build());
        }

        CommentRemoval[] removals = [.. edits.OfType<CommentRemoval>()];
        if (removals.Length > 0)
        {
            PdfDocument document = PdfDocument.Open(File.ReadAllBytes(path));
            IReadOnlyList<PdfCommentInfo> current = PdfCommentReader.Read(document);
            var editor = new PdfIncrementalAnnotationEditor(document);
            foreach (CommentRemoval removal in removals)
            {
                PdfCommentInfo comment = Find(current, removal.Comment);
                editor.RemoveAnnotationAt(comment.PageIndex, comment.AnnotationIndex);
            }
            ReplaceWithBuiltResult(path, editor.Build());
        }

        CommentAddition[] additions = [.. edits.OfType<CommentAddition>()];
        if (additions.Length > 0)
        {
            PdfDocument document = PdfDocument.Open(File.ReadAllBytes(path));
            IReadOnlyList<PdfPageInformation> pages = PdfPageInformation.Read(document);
            var editor = new PdfIncrementalAnnotationEditor(document);
            DateTimeOffset now = DateTimeOffset.Now;
            var metadata = new PdfAnnotationMetadata
            {
                Author = string.IsNullOrWhiteSpace(author) ? null : author,
                CreationDate = now,
                ModificationDate = now
            };
            foreach (CommentAddition addition in additions)
            {
                PdfPageInformation page = pages[addition.PageIndex];
                double x = addition.X ?? 24;
                double y = addition.Y ?? Math.Max(0, page.Height - 48);
                editor.AddTextNote(addition.PageIndex, x, y, addition.Contents,
                    annotationMetadata: metadata, name: addition.Name, inReplyTo: addition.ReplyTo);
            }
            ReplaceWithBuiltResult(path, editor.Build());
        }
    }

    /// <summary>Removes a comment selected from the current document.</summary>
    internal static void RemoveComment(string path, PdfCommentInfo comment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(comment);
        PdfDocument document = PdfDocument.Open(File.ReadAllBytes(path));
        ReplaceWithBuiltResult(path, PdfCommentEditor.Remove(document, comment));
    }

    /// <summary>Divides selected spreads and returns their final page mappings.</summary>
    internal static IReadOnlyList<PdfSpreadDivisionMapping> DivideSpreads(
        string path, IEnumerable<PdfSpreadDivisionRequest> requests,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(requests);
        PdfSpreadDivisionResult result = PdfSpreadDivider.Divide(
            PdfDocument.Open(File.ReadAllBytes(path)), requests, cancellationToken);
        ReplaceWithBuiltResult(path, result.Document.ToArray());
        return result.Mappings;
    }

    /// <summary>Plans a selected partial-rasterization region for desktop preview.</summary>
    internal static PdfPartialRasterizationPlan PlanPartialRasterization(
        string path, int pageIndex, PdfContentBounds region, double dpi,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        PdfDocument document = PdfDocument.Open(File.ReadAllBytes(path));
        PdfPageContent page = new PdfPageContentReader(document).Read(pageIndex, cancellationToken);
        return PdfPartialRasterization.Plan(page, region, dpi);
    }

    /// <summary>
    /// Turns one box on a page into an image at <paramref name="dpi"/> (#369). The page is drawn
    /// without its annotations and form fields, which stay live above the new image, and only
    /// the box is replaced; everything outside it keeps its text and vectors.
    /// </summary>
    internal static void RasterizeRegion(string path, int pageIndex, PdfContentBounds region, double dpi,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        PdfPartialRasterizationPlan plan = PlanPartialRasterization(path, pageIndex, region, dpi, cancellationToken);
        PdfPageInformation page = PdfPageInformation.Read(PdfDocument.Open(File.ReadAllBytes(path)))[pageIndex];
        byte[] pixels;
        int width, height;
        using (PdfPageRenderSession session = PdfPageRenderSession.OpenEngineFirst(path, dpi / 72.0))
        {
            PdfRenderedPage rendered = session.RenderBasePage(pageIndex, cancellationToken);
            (pixels, width, height) = (rendered.Pixels, rendered.Width, rendered.Height);
        }
        // The renderer turns the page by its /Rotate; the region is in the unturned page.
        int rotation = ((page.Rotation % 360) + 360) % 360;
        if (rotation != 0) (pixels, width, height) = BitmapHelpers.RotateBitmap(pixels, width, height, 360 - rotation);

        int outWidth = plan.PixelWidth, outHeight = plan.PixelHeight;
        var rgb = new byte[checked(outWidth * outHeight * 3)];
        for (int y = 0; y < outHeight; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double pageY = region.Top - (y + 0.5) / outHeight * region.Height;
            int sourceY = Math.Clamp((int)((page.Height - pageY) / page.Height * height), 0, height - 1);
            for (int x = 0; x < outWidth; x++)
            {
                double pageX = region.Left + (x + 0.5) / outWidth * region.Width;
                int sourceX = Math.Clamp((int)(pageX / page.Width * width), 0, width - 1);
                int source = (sourceY * width + sourceX) * 4, target = (y * outWidth + x) * 3;
                rgb[target] = pixels[source + 2];
                rgb[target + 1] = pixels[source + 1];
                rgb[target + 2] = pixels[source];
            }
        }
        ApplyPartialRasterization(path,
            [new PdfPartialRasterizationReplacement(pageIndex, plan, PdfImage.FromRgb(outWidth, outHeight, rgb))],
            cancellationToken);
    }

    /// <summary>Applies reviewed partial-rasterization replacements in one revision.</summary>
    internal static void ApplyPartialRasterization(string path,
        IEnumerable<PdfPartialRasterizationReplacement> replacements,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(replacements);
        PdfDocument document = PdfDocument.Open(File.ReadAllBytes(path));
        ReplaceWithBuiltResult(path,
            PdfPartialRasterization.Apply(document, replacements, cancellationToken));
    }

    /// <summary>Adds one editable AcroForm text field to an existing page, styled by a saved
    /// preset (#340) when one is given.</summary>
    internal static string AddTextField(
        string path, int pageIndex, double x, double y, double width, double height,
        PdfTextFieldPreset? preset = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        PdfDocument document = PdfDocument.Open(File.ReadAllBytes(path));
        var existingNames = new HashSet<string>(StringComparer.Ordinal);
        int pageCount = PdfPageInformation.Read(document).Count;
        for (int index = 0; index < pageCount; index++)
            foreach (PdfFormWidgetInfo widget in PdfFormWidgetReader.ReadPage(document, index))
                if (!string.IsNullOrWhiteSpace(widget.FieldName))
                    existingNames.Add(widget.FieldName);

        int suffix = 1;
        string name;
        do name = $"answer_{suffix++:000}";
        while (existingNames.Contains(name));

        PdfFormFieldAppearanceStyle appearance = preset?.AppearanceStyle ?? new PdfFormFieldAppearanceStyle
        {
            BackgroundColor = new PdfRgbColor(1, 1, 1),
            BorderColor = new PdfRgbColor(1, 0, 0),
            TextColor = new PdfRgbColor(0, 0, 0),
            BorderWidth = 1
        };
        var options = new PdfTextFieldOptions { Multiline = height >= 32 };
        double initialFontSize = preset?.FontSize ?? Math.Clamp(height * 0.5, 12, 24);
        byte[] result = new PdfIncrementalPageEditor(document).AddTextField(
            pageIndex, name, x, y, width, height, fontSize: initialFontSize,
            options: options,
            fieldMetadata: new PdfFormFieldMetadata
            {
                Tooltip = $"Answer {suffix - 1}"
            }, appearanceStyle: appearance).Build();
        ReplaceWithBuiltResult(path, result);
        return name;
    }

    /// <summary>
    /// A preset (#340) from a text field already in the document: its size as it shows on the
    /// page, font size, text, background and border colors, and border width and style. A
    /// <paramref name="fontSize"/> overrides the saved one when the user has just changed it.
    /// </summary>
    internal static PdfTextFieldPreset ReadTextFieldPreset(
        string path, string fieldName, string presetName, double? fontSize = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldName);
        PdfDocument document = PdfDocument.Open(File.ReadAllBytes(path));
        int pageCount = PdfPageInformation.Read(document).Count;
        PdfFormWidgetInfo? widget = null;
        for (int index = 0; index < pageCount && widget is null; index++)
            widget = PdfFormWidgetReader.ReadPage(document, index).FirstOrDefault(candidate =>
                candidate.FieldKind == PdfFormFieldKind.Text
                && string.Equals(candidate.FieldName, fieldName, StringComparison.Ordinal));
        if (widget is null)
            throw new KeyNotFoundException($"Text field '{fieldName}' was not found.");

        double width = Math.Abs(widget.Right - widget.Left), height = Math.Abs(widget.Top - widget.Bottom);
        if (widget.PageRotation is 90 or 270) (width, height) = (height, width);
        (double savedSize, PdfRgbColor textColor) =
            TextFieldPresetCapture.ParseDefaultAppearance(widget.DefaultAppearance);
        double size = fontSize is > 0 ? fontSize.Value : savedSize > 0 ? savedSize : 12;

        double borderWidth = 1;
        PdfFormFieldBorderStyle borderStyle = PdfFormFieldBorderStyle.Solid;
        IReadOnlyList<double>? dash = null;
        PdfObject Resolve(PdfObject value) =>
            value is PdfIndirectReference reference ? document.Resolve(reference) : value;
        static double? Number(PdfObject value) => value switch
        {
            PdfInteger integer => integer.Value,
            PdfReal real => real.Value,
            _ => null
        };
        if (widget.ObjectNumber > 0
            && document.Resolve(new PdfIndirectReference(widget.ObjectNumber, widget.Generation)) is PdfDictionary dictionary)
        {
            if (dictionary.TryGetValue(new PdfName("BS"u8), out PdfObject bsValue)
                && Resolve(bsValue) is PdfDictionary bs)
            {
                if (bs.TryGetValue(new PdfName("W"u8), out PdfObject w) && Number(Resolve(w)) is double bw && bw >= 0)
                    borderWidth = bw;
                if (bs.TryGetValue(new PdfName("S"u8), out PdfObject s) && Resolve(s) is PdfName styleName)
                    borderStyle = TextFieldPresetCapture.BorderStyleFromName(styleName.ValueAsLatin1());
                if (bs.TryGetValue(new PdfName("D"u8), out PdfObject d) && Resolve(d) is PdfArray dashArray)
                {
                    double[] lengths = [.. dashArray.Select(item => Number(Resolve(item)) ?? 0)];
                    if (lengths.Length > 0 && lengths.All(length => double.IsFinite(length) && length > 0))
                        dash = lengths;
                }
            }
            else if (dictionary.TryGetValue(new PdfName("Border"u8), out PdfObject borderValue)
                && Resolve(borderValue) is PdfArray border && border.Count >= 3
                && Number(Resolve(border[2])) is double legacyWidth && legacyWidth >= 0)
                borderWidth = legacyWidth;
        }
        if (borderStyle == PdfFormFieldBorderStyle.Dashed && dash is null) dash = [3];

        return new PdfTextFieldPreset(presetName, width, height, size, new PdfFormFieldAppearanceStyle
        {
            BackgroundColor = widget.BackgroundColor,
            BorderColor = widget.BorderColor,
            TextColor = textColor,
            BorderWidth = borderWidth,
            BorderStyle = borderStyle,
            DashPattern = dash
        });
    }

    /// <summary>Moves one native form widget while preserving its field and appearance.</summary>
    internal static void MoveFormWidget(
        string path, int objectNumber, int generation,
        double left, double bottom, double right, double top)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        PdfDocument document = PdfDocument.Open(File.ReadAllBytes(path));
        byte[] result = new PdfIncrementalPageEditor(document)
            .SetFormWidgetRectangle(
                objectNumber, generation, left, bottom, right, top)
            .Build();
        ReplaceWithBuiltResult(path, result);
    }

    /// <summary>Removes one native form field and all of its widgets.</summary>
    internal static void RemoveFormField(string path, string fieldName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldName);
        PdfDocument document = PdfDocument.Open(File.ReadAllBytes(path));
        document = AttachUnlistedFormFields(document,
        [
            .. Enumerable.Range(0, PdfPageInformation.Read(document).Count)
                .SelectMany(pageIndex => PdfFormWidgetReader.ReadPage(document, pageIndex))
        ]);
        byte[] result = new PdfIncrementalPageEditor(document)
            .RemoveFormField(fieldName)
            .Build();
        ReplaceWithBuiltResult(path, result);
    }

    /// <summary>Changes one text field's background color while preserving its value.</summary>
    internal static void SetTextFieldBackground(
        string path, string fieldName, System.Windows.Media.Color color,
        double? fontSize)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldName);
        PdfDocument document = PdfDocument.Open(File.ReadAllBytes(path));
        var background = new PdfRgbColor(
            color.R / 255d, color.G / 255d, color.B / 255d);
        var fonts = new Dictionary<string, TrueTypeFont>(StringComparer.OrdinalIgnoreCase);
        byte[] result = new PdfIncrementalPageEditor(document)
            .SetTextFieldBackground(fieldName, background, fontSize,
                value => EmbeddedFormFont(value, fonts))
            .Build();
        ReplaceWithBuiltResult(path, result);
    }

    /// <summary>Replaces the document bookmark hierarchy as one engine revision.</summary>
    internal static void ReplaceBookmarks(string path, IReadOnlyList<PdfBookmarkInfo> bookmarks)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(bookmarks);
        PdfDocument document = PdfDocument.Open(File.ReadAllBytes(path));
        var editor = new PdfIncrementalPageEditor(document).ClearBookmarks();
        Add(bookmarks, 0);
        ReplaceWithBuiltResult(path, editor.Build());

        void Add(IReadOnlyList<PdfBookmarkInfo> items, int level)
        {
            foreach (PdfBookmarkInfo item in items)
            {
                var options = new PdfBookmarkOptions
                {
                    Style = item.Style,
                    Color = item.Color,
                    IsOpen = item.IsOpen,
                    Destination = item.Destination ?? PdfDestination.FitPage()
                };
                if (item.NamedDestination is not null)
                    editor.AddNamedDestinationBookmark(item.Title, item.NamedDestination, level, options);
                else if (item.DestinationPageIndex.HasValue)
                    editor.AddBookmark(item.Title, item.DestinationPageIndex.Value, level, options);
                else
                    throw new NotSupportedException(
                        $"Bookmark '{item.Title}' has no local page or named destination.");
                Add(item.Children, level + 1);
            }
        }
    }

    /// <summary>Applies a complete pending form-edit batch as one incremental revision.</summary>
    internal static void ApplyFormValues(string path, FormEdits edits)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(edits);
        if (!HasFormValues(edits)) return;
        ReplaceWithBuiltResult(path, BuildFormValues(PdfDocument.Open(File.ReadAllBytes(path)), edits));
    }

    private static bool HasFormValues(FormEdits edits) =>
        edits.TextValues.Count != 0 || edits.ChoiceValues.Count != 0 || edits.MultiChoiceValues.Count != 0
        || edits.CheckBoxValues.Count != 0 || edits.RadioValues.Count != 0 || edits.TextFontSizes.Count != 0;

    /// <summary>
    /// True for a PDF that opens without a password but whose author restricts editing while allowing
    /// form filling, either directly or through the annotation permission, as Acrobat reads it.
    /// </summary>
    internal static bool CanFillRestrictedForm(string path)
    {
        try
        {
            PdfDocument document = OpenForReading(File.ReadAllBytes(path));
            return document.IsDecrypted
                && document.PasswordAuthenticationRole == KillerPdf.Engine.Security.PdfPasswordAuthenticationRole.User
                && document.DeclaredPermissions is KillerPdf.Engine.Security.PdfDocumentPermissions permissions
                && !permissions.AllowDocumentModification
                && (permissions.AllowFormFilling || permissions.AllowAnnotationModification);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return false;
        }
    }

    /// <summary>
    /// Saves filled-in values into a restricted form as an incremental update of the encrypted file, so the
    /// result keeps its encryption and permissions. The engine refuses when the author forbids filling.
    /// </summary>
    internal static void FillRestrictedForm(string sourcePath, string destinationPath, FormEdits edits)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(edits);
        byte[] source = File.ReadAllBytes(sourcePath);
        byte[] filled = HasFormValues(edits) ? BuildFormValues(OpenForReading(source), edits) : source;
        AtomicFileOutput.Write(destinationPath, filled);
    }

    private static byte[] BuildFormValues(PdfDocument document, FormEdits edits)
    {
        var fonts = new Dictionary<string, TrueTypeFont>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<PdfFormWidgetInfo> widgets =
        [
            .. Enumerable.Range(
                0, PdfPageInformation.Read(document).Count)
                .SelectMany(pageIndex => PdfFormWidgetReader.ReadPage(document, pageIndex))
        ];
        document = AttachUnlistedFormFields(document, widgets);
        var editor = new PdfIncrementalPageEditor(document);
        foreach ((string name, string value) in edits.TextValues.OrderBy(item => item.Key))
            editor.SetTextFieldValue(name, value, EmbeddedFormFont(value, fonts), fontSize:
                edits.TextFontSizes.TryGetValue(name, out double size) ? size : null);
        // A size alone sends no value: a name the file repeats holds a value per field, and each keeps its own.
        foreach ((string name, double size) in edits.TextFontSizes.OrderBy(item => item.Key))
            if (!edits.TextValues.ContainsKey(name))
                editor.SetTextFieldFontSize(name, size, value => EmbeddedFormFont(value, fonts));
        foreach ((string name, string value) in edits.ChoiceValues.OrderBy(item => item.Key))
        {
            string appearanceText = widgets.FirstOrDefault(widget =>
                    widget.FieldKind == PdfFormFieldKind.Choice
                    && string.Equals(widget.FieldName, name, StringComparison.Ordinal))?
                .Options.FirstOrDefault(option =>
                    string.Equals(option.ExportValue, value, StringComparison.Ordinal))?
                .DisplayValue ?? value;
            editor.SetChoiceFieldValue(name, value, EmbeddedFormFont(appearanceText, fonts));
        }
        foreach ((string name, IReadOnlyList<string> values) in
                 edits.MultiChoiceValues.OrderBy(item => item.Key))
        {
            string appearanceText = string.Join(' ', values.Select(value => widgets
                .FirstOrDefault(widget => widget.FieldKind == PdfFormFieldKind.Choice
                    && string.Equals(widget.FieldName, name, StringComparison.Ordinal))?
                .Options.FirstOrDefault(option =>
                    string.Equals(option.ExportValue, value, StringComparison.Ordinal))?
                .DisplayValue ?? value));
            editor.SetChoiceFieldValues(name, values, EmbeddedFormFont(appearanceText, fonts));
        }
        foreach ((string name, bool value) in edits.CheckBoxValues.OrderBy(item => item.Key))
            editor.SetCheckBoxValue(name, value);
        foreach ((string name, string value) in edits.RadioValues.OrderBy(item => item.Key))
            editor.SetRadioButtonValue(name, value.TrimStart('/'));
        return editor.Build();
    }

    private static PdfDocument AttachUnlistedFormFields(
        PdfDocument document, IReadOnlyList<PdfFormWidgetInfo> widgets)
    {
        var rootName = new PdfName("Root"u8);
        var acroFormName = new PdfName("AcroForm"u8);
        if (!document.CrossReferences.TryGetTrailerValue(rootName, out PdfObject rootValue)
            || rootValue is not PdfIndirectReference catalogReference
            || document.Resolve(catalogReference) is not PdfDictionary catalog)
            throw new InvalidOperationException("The PDF has no valid document catalog.");
        if (catalog.ContainsKey(acroFormName) || widgets.Count == 0) return document;

        var names = new HashSet<string>(StringComparer.Ordinal);
        var fields = new List<PdfObject>(widgets.Count);
        foreach (PdfFormWidgetInfo widget in widgets)
        {
            if (widget.ObjectNumber == 0 || !names.Add(widget.FieldName))
                throw new NotSupportedException(
                    "This PDF has unlisted form fields that cannot be saved safely.");
            var reference = new PdfIndirectReference(widget.ObjectNumber, widget.Generation);
            if (document.Resolve(reference) is not PdfDictionary field
                || field.ContainsKey(new PdfName("Parent"u8)))
                throw new NotSupportedException(
                    "This PDF has unlisted form fields that cannot be saved safely.");
            fields.Add(reference);
        }

        var update = new PdfIncrementalUpdateBuilder(document);
        PdfIndirectReference formReference = update.AddObject(new PdfDictionary(
        [
            new(new PdfName("Fields"u8), new PdfArray(fields))
        ]));
        update.ReplaceObject(catalogReference.ObjectNumber, new PdfDictionary(
            catalog.Append(new KeyValuePair<PdfName, PdfObject>(acroFormName, formReference))));
        return PdfDocument.Open(update.Build());
    }

    private static TrueTypeFont? EmbeddedFormFont(
        string value, Dictionary<string, TrueTypeFont> cache)
    {
        if (!value.Any(character => character > byte.MaxValue)) return null;
        string family = FontCoverage.PickFamily("Segoe UI", value);
        if (cache.TryGetValue(family, out TrueTypeFont? font)) return font;
        byte[]? bytes = InstalledFontCatalog.RegularFaceBytes(family) ?? throw new InvalidOperationException(
                $"No installed font can preserve the Unicode form value using {family}.");
        try { font = TrueTypeFont.Load(bytes); }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"The installed font {family} could not be embedded for a Unicode form value.", ex);
        }
        cache.Add(family, font);
        return font;
    }

    /// <summary>Writes a copy of an unencrypted PDF protected with Standard Security passwords.</summary>
    internal static IReadOnlyList<PdfReferenceRepair> ProtectWithPasswords(string sourcePath, string destinationPath,
        KillerPdf.Engine.Security.PdfPasswordEncryptionOptions options, bool allowSignatureInvalidation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ThrowIfDamagedProtected(sourcePath);
        PdfDocument document = PdfDocument.OpenWithCompatibilityRecovery(File.ReadAllBytes(sourcePath));
        PdfDocumentWriteResult result = PdfDocumentEncryptionWriter.EncryptWithPasswordsWithReport(
            document, options, allowSignatureInvalidation, replaceDanglingReferences: true);
        AtomicFileOutput.Write(destinationPath, result.Data);
        return result.ReferenceRepairs;
    }

    /// <summary>Writes a copy of an unencrypted PDF that only the listed certificate holders can open.</summary>
    internal static IReadOnlyList<PdfReferenceRepair> ProtectForRecipients(string sourcePath, string destinationPath,
        KillerPdf.Engine.Security.PdfCertificateEncryptionOptions options, bool allowSignatureInvalidation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ThrowIfDamagedProtected(sourcePath);
        PdfDocument document = PdfDocument.OpenWithCompatibilityRecovery(File.ReadAllBytes(sourcePath));
        PdfDocumentWriteResult result = PdfDocumentEncryptionWriter.EncryptForRecipientsWithReport(
            document, options, allowSignatureInvalidation, replaceDanglingReferences: true);
        AtomicFileOutput.Write(destinationPath, result.Data);
        return result.ReferenceRepairs;
    }

    /// <summary>
    /// Accepts a recipient certificate with an RSA key whose Key Usage, if any, permits key
    /// encipherment, which is what certificate-recipient encryption needs.
    /// </summary>
    internal static bool CanEncryptForRecipient(
        System.Security.Cryptography.X509Certificates.X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        using (var key = System.Security.Cryptography.X509Certificates.RSACertificateExtensions.GetRSAPublicKey(certificate))
            if (key is null) return false;
        foreach (var extension in certificate.Extensions)
            if (extension is System.Security.Cryptography.X509Certificates.X509KeyUsageExtension usage)
                return (usage.KeyUsages
                    & System.Security.Cryptography.X509Certificates.X509KeyUsageFlags.KeyEncipherment) != 0;
        return true;
    }

    /// <summary>Reports whether a PDF is encrypted for certificate recipients rather than passwords.</summary>
    internal static bool IsCertificateEncrypted(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            PdfDocument document = PdfDocument.OpenWithCompatibilityRecovery(File.ReadAllBytes(path));
            if (!document.CrossReferences.TryGetTrailerValue(new PdfName("Encrypt"u8), out PdfObject value))
                return false;
            if (value is PdfIndirectReference reference) value = document.Resolve(reference);
            return value is PdfDictionary encryption
                && encryption.TryGetValue(new PdfName("Filter"u8), out PdfObject filter)
                && filter is PdfName name && name.Equals(new PdfName("Adobe.PubSec"u8));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return false;
        }
    }

    /// <summary>
    /// Opens a certificate-encrypted PDF with the first certificate whose private key unlocks it and
    /// writes a decrypted working copy. Returns false when none of the certificates can open it.
    /// </summary>
    internal static bool TryRemoveCertificateEncryption(string sourcePath, string destinationPath,
        IEnumerable<System.Security.Cryptography.X509Certificates.X509Certificate2> certificates,
        out PdfAccess recipient)
    {
        recipient = PdfAccess.Refused(PdfAccess.UnreadablePermissions);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(certificates);
        byte[] source = File.ReadAllBytes(sourcePath);
        foreach (var certificate in certificates)
        {
            if (!certificate.HasPrivateKey) continue;
            PdfDocument document;
            try { document = PdfDocument.OpenWithCompatibilityRecovery(source, certificate); }
            catch (Exception exception) when (exception is System.Security.Cryptography.CryptographicException
                or ArgumentException or InvalidOperationException or NotSupportedException)
            {
                continue;   // not a recipient, or a key that cannot decrypt; try the next one
            }
            // The working copy is decrypted so it can be shown; the recipient's sealed permissions decide
            // what the app then lets it do, and they stay with the tab because no file read gives them back.
            recipient = PdfAccess.Declared(PdfAccessRole.Recipient, document.DeclaredPermissions);
            ReplaceWithBuiltResult(destinationPath, PdfDocumentWriter.Write(document,
                new PdfDocumentWriteOptions { RemoveEncryption = true }));
            return true;
        }
        return false;
    }

    /// <summary>Attempts a zero-rotation copy through the engine.</summary>
    internal static bool TryCreateZeroRotationCopy(
        string sourcePath, string destinationPath)
    {
        try
        {
            CreateZeroRotationCopy(sourcePath, destinationPath);
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return false;
        }
    }

    /// <summary>Merges complete PDF documents while preserving the first document byte prefix.</summary>
    internal static byte[] MergeDocuments(IReadOnlyList<byte[]> sources) =>
        MergeDocuments(sources, out _);

    /// <summary>
    /// Merges complete PDF documents while preserving the first document byte prefix. A source whose
    /// bookmarks are damaged merges without them, and the warnings name each such source.
    /// </summary>
    internal static byte[] MergeDocuments(
        IReadOnlyList<byte[]> sources, out IReadOnlyList<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Count == 0)
            throw new ArgumentException("At least one PDF document is required.", nameof(sources));
        warnings = [];

        if (sources.Count == 1)
        {
            OpenPageSource(sources[0]);
            return sources[0].ToArray();
        }
        var fallback = new MergeBookmarkFallback(index => $"document {index + 1}");
        while (true)
        {
            try
            {
                byte[] merged = MergeDocuments(sources, fallback);
                warnings = fallback.Warnings;
                return merged;
            }
            catch (Exception ex) when (IsBookmarkGraphFailure(ex) && fallback.Advance())
            {
                // The pages are fine; a damaged outline must cost only that source its bookmarks.
            }
        }
    }

    private static byte[] MergeDocuments(IReadOnlyList<byte[]> sources, MergeBookmarkFallback fallback)
    {
        (PdfIncrementalPageEditor editor, PdfDocument? copiedFirst) =
            OpenMergeTarget(sources[0], !fallback.Omitted.Contains(0));
        var indexes = new Dictionary<PdfDocument, int>();
        if (copiedFirst is not null) indexes.Add(copiedFirst, 0);
        for (int index = 1; index < sources.Count; index++)
        {
            PdfDocument source = OpenPageSource(sources[index]);
            indexes.Add(source, index);
            editor.AddImportedDocument(source, !fallback.Omitted.Contains(index));
        }
        fallback.Apply(editor);
        try
        {
            return editor.Build();
        }
        catch (Exception ex) when (IsBookmarkGraphFailure(ex))
        {
            fallback.Note(editor, indexes);
            throw;
        }
    }

    // A damaged outline costs only the document it belongs to. The build says which document failed,
    // and the next attempt leaves that one's bookmarks out and keeps everyone else's. Only a failure
    // that names no document clears every outline.
    private sealed class MergeBookmarkFallback(Func<int, string> describe)
    {
        private int? _failedIndex;
        private bool _failedTarget;
        private bool _dropTarget;
        private bool _clearAll;
        private readonly List<string> _warnings = [];

        internal HashSet<int> Omitted { get; } = [];
        internal IReadOnlyList<string> Warnings => _warnings;

        internal void Apply(PdfIncrementalPageEditor editor)
        {
            if (_dropTarget) editor.DropTargetBookmarks();
            if (_clearAll) editor.ClearBookmarks();
        }

        internal void Note(PdfIncrementalPageEditor editor, IReadOnlyDictionary<PdfDocument, int> indexes)
        {
            _failedIndex = editor.FailedImportSource is PdfDocument failed
                && indexes.TryGetValue(failed, out int index) ? index : null;
            _failedTarget = editor.FailedBookmarksInTarget;
        }

        // False when another attempt would change nothing, so the original failure is raised.
        internal bool Advance()
        {
            if (_clearAll) return false;
            if (_failedIndex is int index && Omitted.Add(index))
                _warnings.Add(Describe(index));
            else if (_failedTarget && !_dropTarget)
            {
                _dropTarget = true;
                _warnings.Add(Describe(0));
            }
            else
            {
                _clearAll = true;
                _warnings.Clear();
                _warnings.Add("Some bookmarks were damaged, so the merged file has none.");
            }
            _failedIndex = null;
            _failedTarget = false;
            return true;
        }

        private string Describe(int index) =>
            $"The bookmarks in {describe(index)} were damaged, so they were left out of the merged file.";
    }

    /// <summary>Rebuilds a complete document graph into a clean engine-authored page tree.</summary>
    internal static void RebuildDocument(
        string sourcePath, string destinationPath, bool stripRotations = false,
        bool preserveBookmarks = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        PdfDocument source = OpenPageSource(File.ReadAllBytes(sourcePath));
        PdfDocument empty = PdfDocument.Open(new PdfDocumentBuilder().Build());
        var editor = new PdfIncrementalPageEditor(empty).AddImportedDocument(source);
        if (!preserveBookmarks)
            editor.ClearBookmarks();
        if (stripRotations)
            for (int pageIndex = 0; pageIndex < editor.PageCount; pageIndex++)
                editor.SetRotation(pageIndex, 0);
        ReplaceWithBuiltResult(destinationPath, editor.Build());
    }

    /// <summary>Performs a deterministic full-document resave through the engine writer.</summary>
    internal static IReadOnlyList<PdfReferenceRepair> ResaveDocument(
        string sourcePath,
        string destinationPath,
        bool allowSignatureInvalidation = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        return ResaveDocument(PdfDocument.Open(File.ReadAllBytes(sourcePath)), destinationPath,
            allowSignatureInvalidation);
    }

    /// <summary>Rewrites a document the caller has already opened, so a batch that opens each
    /// file to classify it does not read and parse the same file twice.</summary>
    internal static IReadOnlyList<PdfReferenceRepair> ResaveDocument(
        PdfDocument source,
        string destinationPath,
        bool allowSignatureInvalidation = false,
        bool bulkOutput = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        PdfDocumentWriteResult result = PdfDocumentWriter.WriteWithReport(source,
            new PdfDocumentWriteOptions
            {
                AllowSignatureInvalidation = allowSignatureInvalidation,
                ReplaceDanglingReferences = true
            });
        if (bulkOutput) AtomicFileOutput.WriteBulk(destinationPath, result.Data);
        else AtomicFileOutput.Write(destinationPath, result.Data);
        return result.ReferenceRepairs;
    }

    /// <summary>Removes one native PDF annotation by its page-array index.</summary>
    internal static void RemoveAnnotation(string path, int pageIndex, int annotationIndex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        byte[] original = File.ReadAllBytes(path);
        PdfDocument source = PdfDocument.Open(original);
        byte[] result = new PdfIncrementalAnnotationEditor(source)
            .RemoveAnnotationAt(pageIndex, annotationIndex)
            .Build();
        ReplaceWithBuiltResult(path, result);
    }

    /// <summary>
    /// One unapplied redaction mark in unrotated, crop-relative page points, with the overlay text
    /// and fill color drawn into the area when it is applied.
    /// </summary>
    internal sealed record RedactionMarkData(int PageIndex, double Left, double Bottom, double Right, double Top,
        string? OverlayText = null, PdfRgbColor? FillColor = null, long AnnotationFlags = 4);

    /// <summary>Reads the redaction marks saved in a document as crop-relative page areas.</summary>
    internal static List<RedactionMarkData> ReadRedactionMarks(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        IReadOnlyList<PdfPageInformation> pages = PdfPageInformation.Read(document);
        var marks = new List<RedactionMarkData>();
        foreach (PdfRedactionMarkInfo mark in PdfRedactionMarkReader.Read(document))
        {
            if (mark.PageIndex < 0 || mark.PageIndex >= pages.Count) continue;
            PdfPageInformation page = pages[mark.PageIndex];
            foreach (PdfContentBounds area in mark.Areas)
                marks.Add(new RedactionMarkData(mark.PageIndex, area.Left - page.Left,
                    area.Bottom - page.Bottom, area.Right - page.Left, area.Top - page.Bottom,
                    string.IsNullOrWhiteSpace(mark.OverlayText) ? null : mark.OverlayText, mark.FillColor,
                    mark.AnnotationFlags));
        }
        return marks;
    }

    /// <summary>
    /// Replaces the redaction marks saved in a file with the given marks, so unapplied marks
    /// travel with the document the way Acrobat stores them.
    /// </summary>
    internal static void WriteRedactionMarks(string path, IReadOnlyList<RedactionMarkData> marks)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(marks);
        PdfDocument source = PdfDocument.Open(File.ReadAllBytes(path));
        IReadOnlyList<PdfRedactionMarkInfo> existing = PdfRedactionMarkReader.Read(source);
        if (existing.Count == 0 && marks.Count == 0) return;
        IReadOnlyList<PdfPageInformation> pages = PdfPageInformation.Read(source);
        var editor = new PdfIncrementalAnnotationEditor(source);
        foreach (PdfRedactionMarkInfo mark in existing)
            editor.RemoveAnnotationAt(mark.PageIndex, mark.AnnotationIndex);
        foreach (RedactionMarkData mark in marks)
        {
            if (mark.PageIndex < 0 || mark.PageIndex >= pages.Count
                || mark.Right - mark.Left <= 0 || mark.Top - mark.Bottom <= 0) continue;
            PdfPageInformation page = pages[mark.PageIndex];
            double left = page.Left + mark.Left, right = page.Left + mark.Right;
            double bottom = page.Bottom + mark.Bottom, top = page.Bottom + mark.Top;
            string? overlay = string.IsNullOrWhiteSpace(mark.OverlayText)
                || !PdfContentRedaction.SupportsOverlayText(mark.OverlayText) ? null : mark.OverlayText.Trim();
            editor.AddRedactionMark(mark.PageIndex,
            [
                new PdfTextQuad(new PdfPoint(left, top), new PdfPoint(right, top),
                    new PdfPoint(left, bottom), new PdfPoint(right, bottom))
            ], fillColor: mark.FillColor, overlayText: overlay,
                annotationMetadata: new PdfAnnotationMetadata { Flags = checked((PdfAnnotationFlags)mark.AnnotationFlags) });
        }
        ReplaceWithBuiltResult(path, editor.Build());
    }

    /// <summary>
    /// One measurement in unrotated, crop-relative page points, with its scale and style. The
    /// label center and size are in page points too.
    /// </summary>
    internal sealed record MeasurementData(
        int PageIndex, PdfMeasurementKind Kind, IReadOnlyList<PdfPoint> Points, bool Closed,
        PdfMeasurementProfile? Profile, string? Contents, PdfRgbColor Color, double LineWidth,
        double Opacity, PdfPoint? LabelCenter, double? LabelSize, double? ArcRadius = null,
        long AnnotationFlags = 4);

    /// <summary>The scale written when a measurement carries none of its own.</summary>
    internal static readonly PdfMeasurementProfile ActualSizeProfile = new("1 in = 1 in", 1.0 / 72.0, "in", 2);

    /// <summary>Reads the measurement annotations saved in a document, crop-relative.</summary>
    internal static List<MeasurementData> ReadMeasurements(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        IReadOnlyList<PdfPageInformation> pages = PdfPageInformation.Read(document);
        var measurements = new List<MeasurementData>();
        foreach (PdfMeasurementAnnotationInfo info in PdfMeasurementAnnotationReader.Read(document))
        {
            if (info.PageIndex < 0 || info.PageIndex >= pages.Count) continue;
            PdfPageInformation page = pages[info.PageIndex];
            PdfPoint Local(PdfMeasurementPoint point) => new(point.X - page.Left, point.Y - page.Bottom);
            measurements.Add(new MeasurementData(info.PageIndex, info.Kind,
                [.. info.Points.Select(Local)], info.Closed, info.Profile, info.Contents,
                info.Color ?? new PdfRgbColor(1, 0, 0), info.LineWidth > 0 ? info.LineWidth : 1,
                info.Opacity, info.CaptionCenter is { } center ? Local(center) : null,
                info.CaptionFontSize, AnnotationFlags: info.AnnotationFlags));
        }
        return measurements;
    }

    /// <summary>
    /// Replaces the measurement annotations saved in a file with the given ones, written as the
    /// dimension lines, polylines and polygons Acrobat reads, each with its own scale.
    /// </summary>
    internal static void WriteMeasurements(string path, IReadOnlyList<MeasurementData> measurements)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(measurements);
        PdfDocument source = PdfDocument.Open(File.ReadAllBytes(path));
        IReadOnlyList<PdfMeasurementAnnotationInfo> existing = PdfMeasurementAnnotationReader.Read(source);
        if (existing.Count == 0 && measurements.Count == 0) return;
        IReadOnlyList<PdfPageInformation> pages = PdfPageInformation.Read(source);
        var editor = new PdfIncrementalAnnotationEditor(source);
        foreach (PdfMeasurementAnnotationInfo measurement in existing)
            editor.RemoveAnnotationAt(measurement.PageIndex, measurement.AnnotationIndex);
        foreach (MeasurementData measurement in measurements)
        {
            if (measurement.PageIndex < 0 || measurement.PageIndex >= pages.Count) continue;
            PdfPageInformation page = pages[measurement.PageIndex];
            PdfPoint Absolute(PdfPoint point) => new(page.Left + point.X, page.Bottom + point.Y);
            PdfPoint[] points = [.. measurement.Points.Select(Absolute)];
            string? contents = string.IsNullOrWhiteSpace(measurement.Contents) ? null : measurement.Contents;
            PdfMeasurementCaption? caption = contents is not null && measurement.LabelCenter is { } center
                ? new PdfMeasurementCaption(Absolute(center), Math.Clamp(measurement.LabelSize ?? 10, 1, 144))
                : null;
            PdfMeasurementProfile profile = measurement.Profile ?? ActualSizeProfile;
            double width = double.IsFinite(measurement.LineWidth) && measurement.LineWidth > 0
                ? measurement.LineWidth : 1;
            double opacity = Math.Clamp(double.IsFinite(measurement.Opacity) ? measurement.Opacity : 1, 0, 1);
            var metadata = new PdfAnnotationMetadata { Flags = checked((PdfAnnotationFlags)measurement.AnnotationFlags) };
            try
            {
                switch (measurement.Kind)
                {
                    case PdfMeasurementKind.Distance when points.Length >= 2:
                        editor.AddDistanceMeasurement(measurement.PageIndex, points[0], points[1], profile,
                            measurement.Color, width, opacity, contents, annotationMetadata: metadata, caption: caption);
                        break;
                    case PdfMeasurementKind.Perimeter when points.Length >= 2:
                        PdfPoint[] vertices = measurement.Closed && points.Length > 2 ? [.. points, points[0]] : points;
                        editor.AddPolyline(measurement.PageIndex, vertices, measurement.Color, width, opacity,
                            contents, annotationMetadata: metadata, intent: PdfVertexAnnotationIntent.Dimension, measurement: profile,
                            caption: caption);
                        break;
                    case PdfMeasurementKind.Area when points.Length >= 3:
                        editor.AddPolygon(measurement.PageIndex, points, measurement.Color, null, width,
                            opacity, contents, annotationMetadata: metadata, intent: PdfVertexAnnotationIntent.Dimension,
                            measurement: profile, caption: caption);
                        break;
                    case PdfMeasurementKind.Angle when points.Length >= 3:
                        editor.AddAngleMeasurement(measurement.PageIndex, points[0], points[1], points[2],
                            measurement.Color, width, opacity, 1, contents, annotationMetadata: metadata, caption: caption,
                            arcRadius: measurement.ArcRadius is > 0 and var arc && double.IsFinite(arc) ? arc : null);
                        break;
                }
            }
            catch (ArgumentException)
            {
                // A measurement whose points collapsed onto each other has nothing to save.
            }
        }
        if (editor.HasChanges) ReplaceWithBuiltResult(path, editor.Build());
    }

    /// <summary>
    /// Writes a copy of <paramref name="sourcePath"/> without its measurement annotations, so the
    /// viewer can edit them on its overlay and write them back on save. Returns how many it removed.
    /// </summary>
    internal static int RemoveMeasurements(string sourcePath, string destinationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        PdfDocument source = PdfDocument.Open(File.ReadAllBytes(sourcePath));
        IReadOnlyList<PdfMeasurementAnnotationInfo> existing = PdfMeasurementAnnotationReader.Read(source);
        File.Copy(sourcePath, destinationPath, true);
        if (existing.Count == 0) return 0;
        var editor = new PdfIncrementalAnnotationEditor(source);
        foreach (PdfMeasurementAnnotationInfo measurement in existing)
            editor.RemoveAnnotationAt(measurement.PageIndex, measurement.AnnotationIndex);
        ReplaceWithBuiltResult(destinationPath, editor.Build());
        return existing.Count;
    }

    /// <summary>
    /// Sets the drawing scale of the listed pages, saved as each page's /VP viewport, or removes
    /// it when <paramref name="profile"/> is null.
    /// </summary>
    internal static void SetPageScales(string path, IReadOnlyList<int> pageIndexes, PdfMeasurementProfile? profile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(pageIndexes);
        PdfDocument source = PdfDocument.Open(File.ReadAllBytes(path));
        var editor = new PdfIncrementalAnnotationEditor(source);
        foreach (int page in pageIndexes.Distinct())
            if (page >= 0 && page < editor.PageCount) editor.SetPageMeasurementScale(page, profile);
        if (editor.HasChanges) ReplaceWithBuiltResult(path, editor.Build());
    }

    /// <summary>
    /// The drawing scales saved in a document's page viewports, with regional scales moved into
    /// crop-relative page points to match the viewer's other page geometry.
    /// </summary>
    internal static PdfMeasurementProfileMap ReadPageScales(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        IReadOnlyList<PdfPageInformation> pages = PdfPageInformation.Read(document);
        var assignments = new List<PdfMeasurementProfileAssignment>();
        foreach (PdfMeasurementProfileAssignment assignment in PdfMeasurementAnnotationReader.ReadPageScales(document))
        {
            if (assignment.PageIndex is not int page || page < 0 || page >= pages.Count) continue;
            PdfContentBounds? region = assignment.Region is { } bounds
                ? new PdfContentBounds(bounds.Left - pages[page].Left, bounds.Bottom - pages[page].Bottom,
                    bounds.Right - pages[page].Left, bounds.Top - pages[page].Bottom)
                : null;
            assignments.Add(new PdfMeasurementProfileAssignment(assignment.Profile, page, region));
        }
        return new PdfMeasurementProfileMap(assignments);
    }

    /// <summary>Normalizes native links to invisible clickable regions through one revision.</summary>
    internal static void StripLinkAppearances(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        PdfDocument source = PdfDocument.Open(File.ReadAllBytes(path));
        var editor = new PdfIncrementalAnnotationEditor(source).StripLinkAppearances();
        if (!editor.HasChanges) return;
        ReplaceWithBuiltResult(path, editor.Build());
    }

    /// <summary>Clears signature state invalidated by the application's rewritten output.</summary>
    internal static void ClearInvalidatedSignatures(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        byte[] original = File.ReadAllBytes(path);
        PdfDocument source = PdfDocument.Open(original);
        byte[] result = PdfSignatureInvalidationWriter.ClearSignatureValues(source);
        if (result.AsSpan().SequenceEqual(original)) return;
        ReplaceWithBuiltResult(path, result);
    }

    /// <summary>Proportionally normalizes selected pages into a compatible dimension range.</summary>
    internal static void NormalizePageDimensions(string path,
        IReadOnlyCollection<int> pageIndexes, double minimum, double maximum)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(pageIndexes);
        byte[] original = File.ReadAllBytes(path);
        PdfDocument source = PdfDocument.Open(original);
        byte[] result = PdfPageDimensionNormalizer.NormalizePages(
            source, pageIndexes, minimum, maximum);
        if (result.AsSpan().SequenceEqual(original)) return;
        ReplaceWithBuiltResult(path, result);
    }

    /// <summary>Merges PDF documents and image frames through one engine page tree.</summary>
    internal static byte[] MergeFiles(IReadOnlyList<string> paths) => MergeFiles(paths, out _);

    /// <summary>
    /// Merges PDF documents and image frames through one engine page tree. A PDF whose bookmarks are
    /// damaged merges without them, and the warnings name each such file.
    /// </summary>
    internal static byte[] MergeFiles(IReadOnlyList<string> paths, out IReadOnlyList<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count == 0)
            throw new ArgumentException("At least one input file is required.", nameof(paths));
        warnings = [];

        var fallback = new MergeBookmarkFallback(index => $"\"{paths[index]}\"");
        while (true)
        {
            try
            {
                byte[] merged = MergeFiles(paths, fallback);
                warnings = fallback.Warnings;
                return merged;
            }
            catch (Exception ex) when (IsBookmarkGraphFailure(ex) && fallback.Advance())
            {
                // The pages are fine; a damaged outline must cost only that file its bookmarks.
            }
        }
    }

    private static byte[] MergeFiles(IReadOnlyList<string> paths, MergeBookmarkFallback fallback)
    {
        bool firstIsPdf = string.Equals(Path.GetExtension(paths[0]), ".pdf",
            StringComparison.OrdinalIgnoreCase);
        PdfIncrementalPageEditor editor;
        var sourcePaths = new Dictionary<PdfDocument, string>();
        var indexes = new Dictionary<PdfDocument, int>();
        try
        {
            if (firstIsPdf)
            {
                (editor, PdfDocument? copiedFirst) = OpenMergeTarget(
                    File.ReadAllBytes(paths[0]), !fallback.Omitted.Contains(0));
                if (copiedFirst is not null)
                {
                    sourcePaths.Add(copiedFirst, paths[0]);
                    indexes.Add(copiedFirst, 0);
                }
            }
            else editor = new PdfIncrementalPageEditor(PdfDocument.Open(new PdfDocumentBuilder().Build()));
        }
        catch (Exception ex) when (firstIsPdf && IsImportFailure(ex))
        {
            throw WithImportSource(paths[0], ex);
        }
        foreach ((string path, int index) in paths.Select((path, index) => (path, index)).Skip(firstIsPdf ? 1 : 0))
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            try
            {
                if (string.Equals(Path.GetExtension(path), ".pdf",
                        StringComparison.OrdinalIgnoreCase))
                {
                    PdfDocument source = OpenPageSource(File.ReadAllBytes(path));
                    sourcePaths.Add(source, path);
                    indexes.Add(source, index);
                    editor.AddImportedDocument(source, !fallback.Omitted.Contains(index));
                    continue;
                }
                AppendImageFrames(editor, path);
            }
            catch (Exception ex) when (IsImportFailure(ex))
            {
                throw WithImportSource(path, ex);
            }
        }
        fallback.Apply(editor);
        try
        {
            return BuildImportedDocuments(editor, sourcePaths);
        }
        catch (Exception ex) when (IsBookmarkGraphFailure(ex))
        {
            fallback.Note(editor, indexes);
            throw;
        }
    }

    internal static bool IsImportFailure(Exception exception) =>
        exception is not OperationCanceledException and not OutOfMemoryException;

    internal static InvalidOperationException WithImportSource(string path, Exception exception)
    {
        var named = new InvalidOperationException($"\"{path}\": {exception.Message}", exception);
        named.Data[ImportSourceMarker] = path;
        return named;
    }

    // Marks the exception that names the file that failed. Its message holds a path, which says nothing
    // about why the import failed, and its inner exception is the failure itself.
    private const string ImportSourceMarker = "KillerPDF.ImportSource";

    /// <summary>A PDF with no pages, which there is nothing to merge from.</summary>
    internal sealed class ImportNoPagesException : InvalidOperationException
    {
        internal ImportNoPagesException(string path) : base(TextFor(path)) { }

        private static string TextFor(string path)
        {
            string format = System.Windows.Application.Current?.TryFindResource("Str_Import_NoPages") as string
                ?? "{0} has no pages, so there is nothing to merge.";
            return string.Format(format, Path.GetFileName(path));
        }
    }

    /// <summary>
    /// True when a file failed because its bytes are damaged, which a repair can mend. A validator
    /// refusing content the file holds is not damage, and rebuilding the file does not change it.
    /// </summary>
    internal static bool IsParserDamage(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (current is FormatException or InvalidDataException or PdfFilterException)
                return true;
        return false;
    }

    private static byte[] BuildImportedDocuments(
        PdfIncrementalPageEditor editor, IReadOnlyDictionary<PdfDocument, string> sourcePaths)
    {
        try
        {
            return editor.Build();
        }
        catch (Exception ex) when (IsImportFailure(ex)
            && editor.FailedImportSource is PdfDocument source && sourcePaths.ContainsKey(source))
        {
            throw WithImportSource(sourcePaths[editor.FailedImportSource!], ex);
        }
    }

    /// <summary>Merges every readable PDF or image input and skips invalid entries.</summary>
    internal static byte[] MergeReadableFiles(IReadOnlyList<string> paths) => MergeReadableFiles(paths, out _);

    /// <summary>
    /// Merges every readable PDF or image input and skips invalid entries. The files it left out, in
    /// the order given, come back so the caller can say how many were not taken.
    /// </summary>
    internal static byte[] MergeReadableFiles(IReadOnlyList<string> paths, out IReadOnlyList<string> leftOut)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var rejected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Each PDF is read and parsed once; a retry only rebuilds the editor without the rejected one.
        var opened = new Dictionary<string, PdfDocument>(StringComparer.OrdinalIgnoreCase);
        var unreadable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var withoutBookmarks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            PdfDocument empty = PdfDocument.Open(new PdfDocumentBuilder().Build());
            var editor = new PdfIncrementalPageEditor(empty);
            var sourcePaths = new Dictionary<PdfDocument, string>();
            foreach (string path in paths)
            {
                if (rejected.Contains(path) || unreadable.Contains(path)) continue;
                try
                {
                    if (string.Equals(Path.GetExtension(path), ".pdf",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        if (!opened.TryGetValue(path, out PdfDocument? document))
                            opened[path] = document = OpenPageSource(File.ReadAllBytes(path));
                        int pagesBefore = editor.PageCount;
                        editor.AddImportedDocument(document, !withoutBookmarks.Contains(path));
                        sourcePaths[document] = path;
                        // A PDF with no pages gives the combine nothing, so it counts as left out.
                        if (editor.PageCount == pagesBefore) unreadable.Add(path);
                    }
                    else
                        AppendImageFrames(editor, path);
                }
                catch
                {
                    // Folder and archive imports deliberately retain every readable entry.
                    unreadable.Add(path);
                }
            }
            if (editor.PageCount == 0)
                throw new InvalidOperationException("No readable PDF or image pages were found.");
            try
            {
                byte[] merged = editor.Build();
                leftOut = [.. paths.Where(path => rejected.Contains(path) || unreadable.Contains(path))];
                return merged;
            }
            // A damaged outline leaves the pages readable, so that file loses only its bookmarks.
            catch (Exception ex) when (IsBookmarkGraphFailure(ex) && editor.FailedImportSource is PdfDocument damaged
                && sourcePaths.TryGetValue(damaged, out string? damagedPath) && withoutBookmarks.Add(damagedPath))
            {
            }
            // A PDF can open and import yet be rejected while its pages are built (#455). It is
            // skipped like any other unreadable entry and the rest is built again; each pass
            // rejects one more file, so this ends.
            catch (Exception ex) when (IsImportFailure(ex) && editor.FailedImportSource is PdfDocument failed
                && sourcePaths.TryGetValue(failed, out string? failedPath) && rejected.Add(failedPath))
            {
            }
        }
    }

    private static void AppendImageFrames(PdfIncrementalPageEditor editor, string path)
    {
        using DrawingImage source = DrawingImage.FromFile(path);
        var dimension = new System.Drawing.Imaging.FrameDimension(source.FrameDimensionsList[0]);
        int frameCount = Math.Max(1, source.GetFrameCount(dimension));
        bool useOriginalJpeg = frameCount == 1
            && Path.GetExtension(path) is string extension
            && (extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase));
        byte[]? originalJpeg = useOriginalJpeg ? File.ReadAllBytes(path) : null;
        for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
        {
            source.SelectActiveFrame(dimension, frameIndex);
            int width = source.Width;
            int height = source.Height;
            double dpiX = source.HorizontalResolution;
            double dpiY = source.VerticalResolution;
            if (dpiX is < 24 or > 4800) dpiX = 96;
            if (dpiY is < 24 or > 4800) dpiY = 96;
            double pageWidth = width * 72.0 / dpiX;
            double pageHeight = height * 72.0 / dpiY;
            double shrink = Math.Min(1, 14400.0 / Math.Max(pageWidth, pageHeight));
            pageWidth *= shrink;
            pageHeight *= shrink;
            double grow = Math.Max(1, 3.0 / Math.Min(pageWidth, pageHeight));
            pageWidth *= grow;
            pageHeight *= grow;

            PdfImage image;
            if (originalJpeg is not null)
            {
                image = PdfImage.FromJpeg(originalJpeg);
            }
            else
            {
                using var bitmap = new DrawingBitmap(width, height,
                    System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (DrawingGraphics graphics = DrawingGraphics.FromImage(bitmap))
                    graphics.DrawImage(source, 0, 0, width, height);
                byte[] rgba = CopyRgba(bitmap);
                image = PdfImage.FromRgba(width, height, rgba);
            }
            editor.AddPage(pageWidth, pageHeight,
                new PdfContentStreamBuilder().DrawImage(
                    image, 0, 0, pageWidth, pageHeight));
        }
    }

    private static byte[] CopyRgba(DrawingBitmap bitmap)
    {
        var rectangle = new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height);
        System.Drawing.Imaging.BitmapData data = bitmap.LockBits(rectangle,
            System.Drawing.Imaging.ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            byte[] rgba = new byte[checked(bitmap.Width * bitmap.Height * 4)];
            byte[] row = new byte[Math.Abs(data.Stride)];
            for (int y = 0; y < bitmap.Height; y++)
            {
                IntPtr rowAddress = IntPtr.Add(data.Scan0, y * data.Stride);
                System.Runtime.InteropServices.Marshal.Copy(rowAddress, row, 0, row.Length);
                for (int x = 0; x < bitmap.Width; x++)
                {
                    int source = x * 4;
                    int target = (y * bitmap.Width + x) * 4;
                    rgba[target] = row[source + 2];
                    rgba[target + 1] = row[source + 1];
                    rgba[target + 2] = row[source];
                    rgba[target + 3] = row[source + 3];
                }
            }
            return rgba;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    internal sealed record RasterPage(
        int PixelWidth, int PixelHeight, double PageWidth, double PageHeight,
        ReadOnlyMemory<byte> BgraPixels,
        ReadOnlyMemory<byte> JpegData = default,
        bool Bitonal = false,
        bool Grayscale = false);

    /// <summary>Appends invisible, Unicode-mapped OCR text to every supplied page.</summary>
    internal static int AddSearchableTextLayers(
        string sourcePath, string destinationPath, IReadOnlyList<PdfOcrPixelPage> pages)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(pages);
        PdfDocument document = PdfDocument.Open(File.ReadAllBytes(sourcePath));
        PdfOcrSearchableTextResult result = PdfOcrSearchableTextWriter.Write(
            document, pages, SearchableFontResolver());
        ReplaceWithBuiltResult(destinationPath, result.Document);
        return result.WrittenWords;
    }

    // Picks an installed face that covers a recognized word, for invisible searchable text.
    private static Func<string, TrueTypeFont?> SearchableFontResolver()
    {
        var fonts = new Dictionary<string, TrueTypeFont>(StringComparer.OrdinalIgnoreCase);
        return text =>
        {
            string family = FontCoverage.PickFamily("Segoe UI", text);
            if (!fonts.TryGetValue(family, out TrueTypeFont? font))
            {
                byte[]? bytes = InstalledFontCatalog.RegularFaceBytes(family);
                if (bytes is null) return null;
                try { font = TrueTypeFont.Load(bytes); }
                catch { return null; }
                fonts.Add(family, font);
            }
            return CanMap(font, text) ? font : null;
        };
    }

    /// <summary>Which pages are image scans that can be converted to text.</summary>
    internal static IReadOnlyList<bool> ReadScannedPages(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return PdfScanTextAnalyzer.ReadScannedPages(PdfDocument.Open(File.ReadAllBytes(path)));
    }

    /// <summary>Each page's size in pixels at the resolution it was scanned at, where it has an image.</summary>
    internal static IReadOnlyList<(int Width, int Height)?> ReadScanSizes(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return PdfPageRasterInformation.ReadScanSizes(PdfDocument.Open(File.ReadAllBytes(path)));
    }

    /// <summary>Which pages are drawn only from one-bit black-and-white images.</summary>
    internal static IReadOnlyList<bool> ReadBitonalPages(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return PdfPageRasterInformation.ReadBitonalImagePageHints(PdfDocument.Open(File.ReadAllBytes(path)));
    }

    // Families tried when matching a scan's printed typeface, serif, sans serif and typewriter.
    private static readonly string[] ScanTextFamilies =
    [
        "Times New Roman", "Georgia", "Palatino Linotype", "Garamond", "Bookman Old Style",
        "Century Schoolbook", "Cambria", "Arial", "Calibri", "Verdana", "Tahoma", "Segoe UI",
        "Courier New", "Consolas", "Lucida Console"
    ];

    /// <summary>
    /// The installed faces a scan's text can be set in, keeping only those that allow
    /// embedding, followed by the engine's bundled faces.
    /// </summary>
    internal static IReadOnlyList<PdfScanTextFace> ScanTextFaces()
    {
        static TrueTypeFont? Load(byte[]? bytes)
        {
            if (bytes is null) return null;
            try
            {
                TrueTypeFont font = TrueTypeFont.Load(bytes);
                return font.EmbeddingAllowed ? font : null;
            }
            catch { return null; }
        }
        var faces = new List<PdfScanTextFace>();
        foreach (string family in ScanTextFamilies)
        {
            byte[]? regularBytes = InstalledFontCatalog.FaceBytes(family, false, false);
            if (regularBytes is null || Load(regularBytes) is not TrueTypeFont regular) continue;
            byte[]? boldBytes = InstalledFontCatalog.FaceBytes(family, true, false);
            // A family without a bold face falls back to its regular face; that is no bold.
            TrueTypeFont? bold = boldBytes is not null && !boldBytes.AsSpan().SequenceEqual(regularBytes)
                ? Load(boldBytes) : null;
            faces.Add(new PdfScanTextFace(family, regular, bold));
        }
        faces.AddRange(PdfScanTextWriter.BundledFaces());
        return faces;
    }

    /// <summary>Rewrites the scanned pages as text over their erased images.</summary>
    internal static PdfScanTextResult WriteScanText(
        string sourcePath, string destinationPath, IReadOnlyList<PdfScanTextPage> pages)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(pages);
        PdfDocument document = PdfDocument.Open(File.ReadAllBytes(sourcePath));
        PdfScanTextResult result = PdfScanTextWriter.Write(document, pages, SearchableFontResolver());
        ReplaceWithBuiltResult(destinationPath, result.Document);
        return result;
    }

    private static bool CanMap(TrueTypeFont font, string text) =>
        text.EnumerateRunes().All(rune => font.GetGlyphId(rune.Value) != 0);

    /// <summary>Authors a flattened PDF from opaque or alpha-bearing PDFium BGRA pages.</summary>
    internal static byte[] CreateRasterDocument(IReadOnlyList<RasterPage> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);
        if (pages.Count == 0)
            throw new ArgumentException("At least one raster page is required.", nameof(pages));
        var builder = new PdfDocumentBuilder();
        foreach (RasterPage page in pages)
        {
            if (page.PixelWidth <= 0 || page.PixelHeight <= 0)
                throw new ArgumentOutOfRangeException(nameof(pages),
                    "Raster page dimensions must be positive.");
            PdfImage image;
            if (page.Bitonal || page.Grayscale && page.JpegData.IsEmpty)
            {
                int required = checked(page.PixelWidth * page.PixelHeight * 4);
                if (page.BgraPixels.Length != required)
                    throw new ArgumentException(
                        "A raster page does not contain the required BGRA pixel count.", nameof(pages));
                byte[] gray = new byte[checked(page.PixelWidth * page.PixelHeight)];
                ReadOnlySpan<byte> bgra = page.BgraPixels.Span;
                for (int pixel = 0; pixel < gray.Length; pixel++)
                    gray[pixel] = bgra[pixel * 4];
                image = page.Bitonal
                    ? PdfImage.FromBitonal(page.PixelWidth, page.PixelHeight, gray)
                    : PdfImage.FromGray(page.PixelWidth, page.PixelHeight, gray);
            }
            else if (!page.JpegData.IsEmpty)
            {
                image = PdfImage.FromJpeg(page.JpegData);
                if (image.Width != page.PixelWidth || image.Height != page.PixelHeight)
                    throw new ArgumentException(
                        "A raster page's JPEG dimensions do not match its declared dimensions.", nameof(pages));
            }
            else
            {
                int required = checked(page.PixelWidth * page.PixelHeight * 4);
                if (page.BgraPixels.Length != required)
                    throw new ArgumentException(
                        "A raster page does not contain the required BGRA pixel count.", nameof(pages));
                byte[] rgba = page.BgraPixels.ToArray();
                for (int pixel = 0; pixel < rgba.Length; pixel += 4)
                    (rgba[pixel], rgba[pixel + 2]) = (rgba[pixel + 2], rgba[pixel]);
                image = PdfImage.FromRgba(page.PixelWidth, page.PixelHeight, rgba);
            }
            builder.AddPage(page.PageWidth, page.PageHeight,
                new PdfContentStreamBuilder().DrawImage(
                    image, 0, 0, page.PageWidth, page.PageHeight));
        }
        return builder.Build();
    }

    /// <summary>Reads crop-aware page dimensions and native rotations.</summary>
    internal static IReadOnlyList<PdfPageInformation> ReadPageInformation(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        // The viewer shows the file through compatibility recovery, so its page geometry reads that way too.
        return PdfPageInformation.Read(OpenForReading(File.ReadAllBytes(path)));
    }

    /// <summary>Authors a new one-page blank document.</summary>
    internal static byte[] CreateBlankDocument(double width = 595, double height = 842) =>
        new PdfDocumentBuilder().AddPage(width, height, ReadOnlyMemory<byte>.Empty).Build();

    /// <summary>Extracts selected pages into a new PDF in the supplied order.</summary>
    internal static byte[] ExtractPages(byte[] source, IReadOnlyList<int> pageIndices)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(pageIndices);
        if (pageIndices.Count == 0)
            throw new ArgumentException("At least one page is required.", nameof(pageIndices));

        PdfDocument sourceDocument = OpenPageSource(source);
        PdfDocument empty = PdfDocument.Open(new PdfDocumentBuilder().Build());
        var editor = new PdfIncrementalPageEditor(empty);
        foreach (int pageIndex in pageIndices)
        {
            editor.AddImportedPage(sourceDocument, pageIndex);
            editor.ClearPageThumbnail(editor.PageCount - 1);
        }
        return editor.Build();
    }

    /// <summary>Splits a PDF into one independently valid PDF per source page.</summary>
    internal static IReadOnlyList<byte[]> SplitPages(byte[] source)
    {
        ArgumentNullException.ThrowIfNull(source);
        PdfDocument sourceDocument = OpenPageSource(source);
        int pageCount = new PdfIncrementalPageEditor(sourceDocument).PageCount;
        var results = new byte[pageCount][];
        for (int pageIndex = 0; pageIndex < pageCount; pageIndex++)
        {
            PdfDocument empty = PdfDocument.Open(new PdfDocumentBuilder().Build());
            var editor = new PdfIncrementalPageEditor(empty)
                .AddImportedPage(sourceDocument, pageIndex);
            editor.ClearPageThumbnail(0);
            results[pageIndex] = editor.Build();
        }
        return results;
    }

    internal readonly record struct PageRectangle(
        double X, double Y, double Width, double Height);

    internal sealed record ImportedDocument(
        string Path, IReadOnlyList<int> PageRotations, string? OriginalPath = null);

    /// <summary>
    /// Validates that a document can be merged, not only opened. Strict open is lazy, so damage in an
    /// object only a page copy reads passes it, and the page copy applies its own strict checks to what
    /// recovery rebuilt. Every file therefore goes through a trial copy of all its pages, made the way
    /// <see cref="InsertDocuments"/> makes it, so the check and the merge fail on the same things. A file
    /// with no pages fails with <see cref="ImportNoPagesException"/>. <see cref="IsParserDamage"/> tells
    /// a failure a repair can mend from one it cannot.
    /// </summary>
    internal static void ValidateImportable(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        PdfDocument source = OpenPageSource(File.ReadAllBytes(path));
        int count = new PdfIncrementalPageEditor(source).PageCount;
        if (count == 0) throw new ImportNoPagesException(path);
        // Page by page, as the merge copies them into an open document. A whole-document import reads the
        // trailer /Info first, and a validator failing there would hide the parser damage the merge meets
        // in the pages. An empty destination also adopts the source catalog, which an open document does
        // not, so the trial copy goes behind a page.
        new PdfIncrementalPageEditor(PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage().Build()))
            .InsertImportedPages(1, source, [.. Enumerable.Range(0, count)])
            .Build();
    }

    /// <summary>Validates that the engine can open a document for page-copy operations.</summary>
    internal static void ValidateDocument(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        // Page-copy operations read their source through OpenPageSource, so this checks the same way.
        _ = new PdfIncrementalPageEditor(OpenPageSource(File.ReadAllBytes(path))).PageCount;
    }

    /// <summary>
    /// Writes the application's effective page rotations as the final incremental revision.
    /// The source file is replaced only after the engine has built the complete result.
    /// </summary>
    internal static void ApplyPageRotations(
        string path, IReadOnlyDictionary<int, int> rotations)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(rotations);
        if (rotations.Count == 0) return;

        byte[] source = File.ReadAllBytes(path);
        PdfDocument document = PdfDocument.Open(source);
        var editor = new PdfIncrementalPageEditor(document);
        foreach ((int pageIndex, int rotation) in rotations.OrderBy(item => item.Key))
            editor.SetRotation(pageIndex, rotation);

        ReplaceWithBuiltResult(path, editor.Build());
    }

    /// <summary>Turns selected pages clockwise by a relative multiple of 90 degrees.</summary>
    internal static void RotatePages(
        string path, IReadOnlyCollection<int> pageIndices, int degreesClockwise, string? password = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(pageIndices);
        int turns = ((degreesClockwise % 360) + 360) % 360 / 90;
        if (turns == 0 || pageIndices.Count == 0) return;

        PdfDocument document = password is null
            ? PdfDocument.Open(File.ReadAllBytes(path))
            : PdfDocument.Open(File.ReadAllBytes(path), password);
        var editor = new PdfIncrementalPageEditor(document);
        foreach (int pageIndex in pageIndices.Distinct().OrderBy(index => index))
            for (int turn = 0; turn < turns; turn++) editor.RotateClockwise(pageIndex);
        ReplaceWithBuiltResult(path, editor.Build());
    }

    /// <summary>Creates a rendering copy with every native page rotation set to zero.</summary>
    internal static void CreateZeroRotationCopy(string sourcePath, string destinationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        PdfDocument document = PdfDocument.OpenWithCompatibilityRecovery(
            File.ReadAllBytes(sourcePath));
        var editor = new PdfIncrementalPageEditor(document);
        for (int pageIndex = 0; pageIndex < editor.PageCount; pageIndex++)
            editor.SetRotation(pageIndex, 0);
        ReplaceWithBuiltResult(destinationPath, editor.Build());
    }

    // The Document Info dialog edits the five text fields and carries the rest through from what was read.
    // A value the reader could not interpret shows as blank there, so it is kept as it is unless the user
    // typed a replacement into its field, and saving never deletes what the user did not touch.
    internal static PdfDocumentMetadata CompleteDocumentMetadata(
        PdfDocumentInformation info, PdfDocumentMetadata edited)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(edited);
        bool Typed(string key) => key switch
        {
            "Title" => !string.IsNullOrEmpty(edited.Title),
            "Author" => !string.IsNullOrEmpty(edited.Author),
            "Subject" => !string.IsNullOrEmpty(edited.Subject),
            "Keywords" => !string.IsNullOrEmpty(edited.Keywords),
            "Creator" => !string.IsNullOrEmpty(edited.Creator),
            _ => false
        };
        return edited with
        {
            Language = info.Language,
            CreationDate = info.CreationDate,
            ModificationDate = info.ModificationDate,
            Trapped = info.Trapped,
            PreservedInfoKeys = [.. info.UnreadableKeys.Where(key => !Typed(key))]
        };
    }

    /// <summary>Writes complete descriptive document metadata incrementally.</summary>
    internal static void ApplyDocumentMetadata(string path, PdfDocumentMetadata metadata)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(metadata);
        PdfDocument document = PdfDocument.Open(File.ReadAllBytes(path));
        var editor = new PdfIncrementalPageEditor(document);
        try
        {
            editor.SetMetadata(metadata);
        }
        catch (ArgumentException exception) when (
            metadata.Language is not null && exception.ParamName == nameof(metadata))
        {
            // The writer refuses a language tag that is not valid BCP 47, so the entry already in the
            // file stays as it is rather than being dropped.
            editor.SetMetadata(metadata with
            {
                Language = null,
                PreservedInfoKeys = [.. metadata.PreservedInfoKeys.Append("Lang").Distinct(StringComparer.Ordinal)]
            });
        }
        byte[] result = editor.Build();
        ReplaceWithBuiltResult(path, result);
    }

    /// <summary>Writes the document's initial viewer state as an incremental revision.</summary>
    internal static void ApplyInitialView(string path, PdfInitialView initialView)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(initialView);
        PdfDocument document = PdfDocument.Open(File.ReadAllBytes(path));
        ReplaceWithBuiltResult(path, initialView.Apply(document));
    }

    /// <summary>
    /// Writes visible crop and matching trim boundaries as the final incremental revision.
    /// A null rectangle removes both boundaries so the page falls back to its media box.
    /// </summary>
    internal static void ApplyCropBoxes(
        string path, IReadOnlyDictionary<int, PageRectangle?> crops)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(crops);
        if (crops.Count == 0) return;

        byte[] source = File.ReadAllBytes(path);
        PdfDocument document = PdfDocument.Open(source);
        var editor = new PdfIncrementalPageEditor(document);
        foreach ((int pageIndex, PageRectangle? crop) in crops.OrderBy(item => item.Key))
        {
            if (crop is PageRectangle box)
            {
                editor.SetCropBox(pageIndex, box.X, box.Y, box.Width, box.Height);
                editor.SetPageBox(pageIndex, PdfPageBox.Trim,
                    box.X, box.Y, box.Width, box.Height);
            }
            else
            {
                editor.ClearPageBox(pageIndex, PdfPageBox.Crop);
                editor.ClearPageBox(pageIndex, PdfPageBox.Trim);
            }
        }

        ReplaceWithBuiltResult(path, editor.Build());
    }

    /// <summary>Resizes pages by setting their media box.</summary>
    internal static void ApplyMediaBoxes(string path, IReadOnlyList<int> pages, PageRectangle box)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(pages);
        var editor = new PdfIncrementalPageEditor(PdfDocument.Open(File.ReadAllBytes(path)));
        foreach (int pageIndex in pages)
        {
            editor.SetMediaBox(pageIndex, box.X, box.Y, box.Width, box.Height);
            // Older boxes would keep the page at its old visible size, or leave nothing visible.
            foreach (PdfPageBox stale in new[] { PdfPageBox.Crop, PdfPageBox.Trim, PdfPageBox.Bleed, PdfPageBox.Art })
                editor.ClearPageBox(pageIndex, stale);
        }
        ReplaceWithBuiltResult(path, editor.Build());
    }

    /// <summary>Decides, without changing anything, which marked pages can be redacted in place.</summary>
    internal static PdfContentRedactionPlan AnalyzeRedaction(
        PdfDocument document, IReadOnlyList<PdfRedactionRegion> regions)
        => PdfContentRedaction.Analyze(document, regions);

    /// <summary>
    /// Permanently removes everything under the marked regions, replacing only the approved pages
    /// with sanitized images, and swaps in the verified single-revision result.
    /// </summary>
    internal static PdfContentRedactionResult ApplyRedaction(string path,
        IReadOnlyList<PdfRedactionRegion> regions, IReadOnlyCollection<int> approvedRasterPages)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(regions);
        PdfDocument document = PdfDocument.Open(File.ReadAllBytes(path));
        PdfContentRedactionResult result = PdfContentRedaction.Apply(document, regions,
            new PdfContentRedactionOptions
            {
                ApprovedRasterPages = approvedRasterPages,
                AllowSignatureInvalidation = true,
                FontResolver = InstalledPdfFontResolver.Instance
            });
        ReplaceWithBuiltResult(path, result.Document.ToArray());
        return result;
    }

    /// <summary>Reports whether a line in the file can be rewritten in the page content, or null
    /// when the file cannot be read for it.</summary>
    internal static PdfTextLineInspection? InspectTextLine(string path, int pageIndex, PdfContentBounds region)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try { return PdfTextLineEditor.Inspect(PdfDocument.Open(File.ReadAllBytes(path)), pageIndex, region); }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or NotSupportedException
            or ArgumentException or IOException)
        {
            return null;
        }
    }

    /// <summary>Rewrites a line of the page's own text in place and swaps in the result.</summary>
    internal static PdfTextLineEditResult ReplaceTextLine(string path, PdfTextLineEdit edit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(edit);
        PdfTextLineEditResult result = PdfTextLineEditor.Replace(PdfDocument.Open(File.ReadAllBytes(path)), edit);
        ReplaceWithBuiltResult(path, result.Document);
        return result;
    }

    /// <summary>Rewrites several lines in place, one after another, and swaps in the result.
    /// Returns the indexes of the edits the page content could not take.</summary>
    internal static IReadOnlyList<int> ReplaceTextLines(string path, IReadOnlyList<PdfTextLineEdit> edits)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(edits);
        byte[] current = File.ReadAllBytes(path);
        var failed = new List<int>();
        for (int index = 0; index < edits.Count; index++)
        {
            try { current = PdfTextLineEditor.Replace(PdfDocument.Open(current), edits[index]).Document; }
            catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException
                or FormatException or ArgumentException)
            {
                failed.Add(index);
            }
        }
        if (failed.Count < edits.Count) ReplaceWithBuiltResult(path, current);
        return failed;
    }

    /// <summary>The installed face that can show the text, starting from the wanted family, for
    /// embedding when a line's own font cannot show it. Null when no face loads.</summary>
    internal static (TrueTypeFont Font, string Family)? LoadTextFont(string family, string text, bool bold, bool italic)
    {
        string chosen = FontCoverage.PickFamily(string.IsNullOrWhiteSpace(family) ? "Segoe UI" : family, text);
        byte[]? bytes = InstalledFontCatalog.FaceBytes(chosen, bold, italic) ?? InstalledFontCatalog.RegularFaceBytes(chosen);
        if (bytes is null) return null;
        try { return (TrueTypeFont.Load(bytes), chosen); }
        catch (Exception ex) when (ex is FormatException or NotSupportedException or ArgumentException
            or InvalidDataException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The face for a paragraph edit: its own font when that is installed, otherwise the
    /// font of the same kind that sets closest to the width the page has it at, so the text keeps
    /// its rows. Null when no face loads.</summary>
    internal static (TrueTypeFont Font, string Family)? LoadParagraphFont(PdfParagraphCandidate candidate, string text)
    {
        DetectedPdfFontStyle style = PdfFontStyle.FromPdfName(candidate.FontName);
        if (FontCoverage.Covers(style.Family, text)) return LoadTextFont(style.Family, text, style.Bold, style.Italic);
        (TrueTypeFont Font, string Family)? closest = null;
        double gap = double.MaxValue;
        foreach (string family in SourceFontKind.Families(candidate.FontName))
        {
            if (!FontCoverage.Covers(family, text) || LoadTextFont(family, text, style.Bold, style.Italic) is not { } face) continue;
            // Wider is the costly direction, since each row it spills is a row the paragraph may not
            // have, and a little narrower only leaves a little more room at the end of a line.
            double ratio = PdfParagraphEditor.WidthRatio(candidate, face.Font);
            double off = ratio > 1 ? (ratio - 1) * 3 : (1 - ratio) * 0.5;
            if (off < gap) { gap = off; closest = face; }
        }
        return closest ?? LoadTextFont(style.Family, text, style.Bold, style.Italic);
    }

    /// <summary>The kinds of hidden information removed after a redaction.</summary>
    internal sealed record SanitizeChoices(bool Comments, bool Attachments, bool FormFields,
        bool HiddenLayers, bool Scripts, bool Links, bool Bookmarks);

    /// <summary>
    /// Removes the hidden information a redacted document can still carry, always including its
    /// metadata and page thumbnails, and swaps in the single-revision result.
    /// </summary>
    internal static PdfOptimizationResult SanitizeFile(string path, SanitizeChoices choices)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(choices);
        byte[] original = File.ReadAllBytes(path);
        PdfDocument document = PdfDocument.Open(original);
        if (choices.Links)
        {
            var reader = new PdfLinkReader(document);
            var editor = new PdfIncrementalAnnotationEditor(document);
            bool removed = false;
            int pageCount = PdfPageInformation.Read(document).Count;
            // Every link goes, so the list checks only the subtype and a link the reader cannot read is removed too.
            for (int pageIndex = 0; pageIndex < pageCount; pageIndex++)
                foreach (int annotationIndex in reader.ReadLinkAnnotationIndexes(pageIndex).OrderByDescending(index => index))
                {
                    editor.RemoveAnnotationAt(pageIndex, annotationIndex);
                    removed = true;
                }
            if (removed) document = PdfDocument.Open(editor.Build());
        }
        PdfOptimizationOptions settings = PdfOptimizationOptions.ForDocument(document) with
        {
            RepairHarmlessArtifacts = true,
            ReplaceDanglingReferences = true,
            RemoveMetadata = true,
            RemovePageThumbnails = true,
            RemoveComments = choices.Comments,
            RemoveAttachments = choices.Attachments,
            RemoveFormFields = choices.FormFields,
            RemoveXfaData = choices.FormFields,
            FlattenOptionalContent = choices.HiddenLayers,
            RemoveDocumentJavaScript = choices.Scripts,
            RemoveOpenAction = choices.Scripts,
            RemoveBookmarks = choices.Bookmarks,
            PruneUnusedPageResources = true,
            PruneUnreachableObjects = true,
            AllowSignatureInvalidation = true
        };
        PdfOptimizationResult result = PdfOptimizer.CreatePlan(document, settings).Apply()
            with { OriginalSize = original.Length };
        ReplaceWithBuiltResult(path, result.Data.ToArray());
        return result;
    }

    /// <summary>Removes pages as one byte-preserving incremental revision.</summary>
    internal static void RemovePages(string path, IReadOnlyCollection<int> pageIndices)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(pageIndices);
        int[] removed = [.. pageIndices.Distinct().OrderByDescending(index => index)];
        if (removed.Length == 0) return;

        byte[] source = File.ReadAllBytes(path);
        PdfDocument document = PdfDocument.Open(source);
        var editor = new PdfIncrementalPageEditor(document);
        foreach (int pageIndex in removed) editor.RemovePage(pageIndex);
        ReplaceWithBuiltResult(path, editor.Build());
    }

    /// <summary>Renumbers application rotation state after pages are removed.</summary>
    internal static void RemapRotationsAfterPageRemoval(
        Dictionary<int, int> rotations, IReadOnlyCollection<int> pageIndices)
    {
        ArgumentNullException.ThrowIfNull(rotations);
        ArgumentNullException.ThrowIfNull(pageIndices);
        int[] removed = [.. pageIndices.Distinct().OrderBy(index => index)];
        if (removed.Length == 0) return;

        var remapped = new Dictionary<int, int>();
        foreach ((int oldIndex, int rotation) in rotations.OrderBy(item => item.Key))
        {
            if (Array.BinarySearch(removed, oldIndex) >= 0) continue;
            int shift = removed.Count(index => index < oldIndex);
            remapped[oldIndex - shift] = rotation;
        }
        rotations.Clear();
        foreach ((int pageIndex, int rotation) in remapped)
            rotations[pageIndex] = rotation;
    }

    /// <summary>Moves one page to its final position in a byte-preserving revision.</summary>
    internal static void MovePage(string path, int sourceIndex, int destinationIndex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        byte[] source = File.ReadAllBytes(path);
        PdfDocument document = PdfDocument.Open(source);
        byte[] result = new PdfIncrementalPageEditor(document)
            .MovePage(sourceIndex, destinationIndex)
            .Build();
        ReplaceWithBuiltResult(path, result);
    }

    /// <summary>Moves selected pages as one ordered block into an original-order insertion slot.</summary>
    internal static IReadOnlyList<int> MovePages(
        string path, IReadOnlyList<int> sourceIndices, int insertionIndex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(sourceIndices);
        PdfDocument document = PdfDocument.Open(File.ReadAllBytes(path));
        var editor = new PdfIncrementalPageEditor(document);
        IReadOnlyList<int> target = PageOrderAfterMove(editor.PageCount, sourceIndices, insertionIndex);
        var current = Enumerable.Range(0, editor.PageCount).ToList();
        for (int destination = 0; destination < target.Count; destination++)
        {
            int source = current.IndexOf(target[destination]);
            if (source == destination) continue;
            editor.MovePage(source, destination);
            int page = current[source];
            current.RemoveAt(source);
            current.Insert(destination, page);
        }
        ReplaceWithBuiltResult(path, editor.Build());
        var selected = sourceIndices.Distinct().ToHashSet();
        return [.. target.Select((original, index) => (original, index))
            .Where(item => selected.Contains(item.original)).Select(item => item.index)];
    }

    internal static IReadOnlyList<int> PageOrderAfterMove(
        int pageCount, IReadOnlyList<int> sourceIndices, int insertionIndex)
    {
        ArgumentNullException.ThrowIfNull(sourceIndices);
        if (insertionIndex < 0 || insertionIndex > pageCount)
            throw new ArgumentOutOfRangeException(nameof(insertionIndex));
        int[] selected = [.. sourceIndices.Distinct().OrderBy(index => index)];
        if (selected.Length == 0 || selected.Any(index => index < 0 || index >= pageCount))
            throw new ArgumentOutOfRangeException(nameof(sourceIndices));
        var selectedSet = selected.ToHashSet();
        var remaining = Enumerable.Range(0, pageCount).Where(index => !selectedSet.Contains(index)).ToList();
        int adjustedSlot = insertionIndex - selected.Count(index => index < insertionIndex);
        remaining.InsertRange(adjustedSlot, selected);
        return remaining;
    }

    internal static void RemapRotationsAfterPageMoves(
        Dictionary<int, int> rotations, IReadOnlyList<int> sourceIndices, int insertionIndex)
    {
        ArgumentNullException.ThrowIfNull(rotations);
        IReadOnlyList<int> order = PageOrderAfterMove(rotations.Count, sourceIndices, insertionIndex);
        int[] values = [.. Enumerable.Range(0, rotations.Count).Select(index => rotations[index])];
        rotations.Clear();
        for (int index = 0; index < order.Count; index++) rotations[index] = values[order[index]];
    }

    /// <summary>Puts every page in a new order. <paramref name="order"/> lists, for each new
    /// position, the page that moves there.</summary>
    internal static void ReorderPages(string path, IReadOnlyList<int> order)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(order);
        PdfDocument document = PdfDocument.Open(File.ReadAllBytes(path));
        var editor = new PdfIncrementalPageEditor(document);
        ValidatePageOrder(order, editor.PageCount);
        var current = Enumerable.Range(0, editor.PageCount).ToList();
        for (int destination = 0; destination < order.Count; destination++)
        {
            int source = current.IndexOf(order[destination]);
            if (source == destination) continue;
            editor.MovePage(source, destination);
            int page = current[source];
            current.RemoveAt(source);
            current.Insert(destination, page);
        }
        ReplaceWithBuiltResult(path, editor.Build());
    }

    /// <summary>The order that reverses <paramref name="pages"/> in place: they trade positions
    /// with each other and every other page stays where it is.</summary>
    internal static IReadOnlyList<int> PageOrderAfterReverse(int pageCount, IReadOnlyList<int> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);
        int[] slots = [.. pages.Distinct().OrderBy(index => index)];
        if (slots.Any(index => index < 0 || index >= pageCount))
            throw new ArgumentOutOfRangeException(nameof(pages));
        int[] order = [.. Enumerable.Range(0, pageCount)];
        for (int index = 0; index < slots.Length; index++)
            order[slots[index]] = slots[slots.Length - 1 - index];
        return order;
    }

    /// <summary>Moves rotation state with the pages of a reorder.</summary>
    internal static void RemapRotationsAfterReorder(Dictionary<int, int> rotations, IReadOnlyList<int> order)
    {
        ArgumentNullException.ThrowIfNull(rotations);
        ArgumentNullException.ThrowIfNull(order);
        ValidatePageOrder(order, rotations.Count);
        int[] values = [.. Enumerable.Range(0, rotations.Count).Select(index => rotations[index])];
        rotations.Clear();
        for (int index = 0; index < order.Count; index++) rotations[index] = values[order[index]];
    }

    private static void ValidatePageOrder(IReadOnlyList<int> order, int pageCount)
    {
        if (order.Count != pageCount || order.Distinct().Count() != pageCount
            || order.Any(index => index < 0 || index >= pageCount))
            throw new ArgumentException("The page order must list every page exactly once.", nameof(order));
    }

    /// <summary>Moves rotation state with a reordered page.</summary>
    internal static void RemapRotationsAfterPageMove(
        Dictionary<int, int> rotations, int sourceIndex, int destinationIndex)
    {
        ArgumentNullException.ThrowIfNull(rotations);
        if (sourceIndex == destinationIndex) return;
        var ordered = Enumerable.Range(0, rotations.Count)
            .Select(index => rotations[index])
            .ToList();
        int moved = ordered[sourceIndex];
        ordered.RemoveAt(sourceIndex);
        ordered.Insert(destinationIndex, moved);
        rotations.Clear();
        for (int index = 0; index < ordered.Count; index++)
            rotations[index] = ordered[index];
    }

    /// <summary>Inserts a blank page at its final zero-based position.</summary>
    internal static void InsertBlankPage(
        string path, int pageIndex, double width, double height)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        byte[] source = File.ReadAllBytes(path);
        PdfDocument document = PdfDocument.Open(source);
        byte[] result = new PdfIncrementalPageEditor(document)
            .InsertBlankPage(pageIndex, width, height)
            .Build();
        ReplaceWithBuiltResult(path, result);
    }

    /// <summary>Creates a zero-rotation entry and shifts later page rotation state.</summary>
    internal static void RemapRotationsAfterPageInsertion(
        Dictionary<int, int> rotations, int pageIndex, int pageCount = 1)
    {
        ArgumentNullException.ThrowIfNull(rotations);
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageCount, 1);
        var ordered = Enumerable.Range(0, rotations.Count)
            .Select(index => rotations[index])
            .ToList();
        ordered.InsertRange(pageIndex, Enumerable.Repeat(0, pageCount));
        rotations.Clear();
        for (int index = 0; index < ordered.Count; index++)
            rotations[index] = ordered[index];
    }

    /// <summary>Deep-copies one page directly after its source page.</summary>
    internal static void DuplicatePage(string path, int pageIndex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        byte[] sourceBytes = File.ReadAllBytes(path);
        PdfDocument target = PdfDocument.Open(sourceBytes);
        PdfDocument source = PdfDocument.Open(sourceBytes);
        byte[] result = new PdfIncrementalPageEditor(target)
            .InsertImportedPage(pageIndex + 1, source, pageIndex)
            .SetRotation(pageIndex + 1, 0)
            .Build();
        ReplaceWithBuiltResult(path, result);
    }

    /// <summary>Duplicates the source page's effective application rotation.</summary>
    internal static void RemapRotationsAfterPageDuplication(
        Dictionary<int, int> rotations, int pageIndex)
    {
        ArgumentNullException.ThrowIfNull(rotations);
        var ordered = Enumerable.Range(0, rotations.Count)
            .Select(index => rotations[index])
            .ToList();
        ordered.Insert(pageIndex + 1, ordered[pageIndex]);
        rotations.Clear();
        for (int index = 0; index < ordered.Count; index++)
            rotations[index] = ordered[index];
    }

    /// <summary>Replaces one page with the first page of an authored PDF.</summary>
    internal static void ReplacePage(string path, int pageIndex, string replacementPath)
        => ReplacePages(path, new Dictionary<int, string> { [pageIndex] = replacementPath });

    /// <summary>Replaces selected pages with the first pages of authored PDFs in one revision.</summary>
    internal static void ReplacePages(string path, IReadOnlyDictionary<int, string> replacements,
        bool allowUntaggedImports = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(replacements);
        if (replacements.Count == 0) return;
        try
        {
            ReplaceWithBuiltResult(path, Build(clearBookmarks: false));
        }
        catch (Exception ex) when (IsBookmarkGraphFailure(ex))
        {
            ReplaceWithBuiltResult(path, Build(clearBookmarks: true));
        }

        byte[] Build(bool clearBookmarks)
        {
            PdfDocument target = PdfDocument.Open(File.ReadAllBytes(path));
            var editor = new PdfIncrementalPageEditor(target);
            if (allowUntaggedImports)
                editor.AllowUntaggedPageImports();
            if (clearBookmarks)
                editor.ClearBookmarks();
            foreach (var pair in replacements.OrderBy(pair => pair.Key))
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(pair.Value);
                if (pair.Key < 0 || pair.Key >= editor.PageCount)
                    throw new ArgumentOutOfRangeException(nameof(replacements));
                PdfDocument replacement = OpenPageSource(File.ReadAllBytes(pair.Value));
                if (new PdfIncrementalPageEditor(replacement).PageCount < 1)
                    throw new ArgumentException("A replacement document must contain a page.",
                        nameof(replacements));
                editor.RemovePage(pair.Key).InsertImportedPage(pair.Key, replacement, 0)
                    .SetRotation(pair.Key, 0);
            }
            return editor.Build();
        }
    }

    /// <summary>Replaces pages and removes the superseded page data from the saved file.</summary>
    internal static void ReplacePagesAndCompact(
        string path, IReadOnlyDictionary<int, string> replacements)
    {
        ReplacePages(path, replacements, allowUntaggedImports: true);
        try
        {
            RebuildDocument(path, path);
        }
        catch (Exception ex) when (IsBookmarkGraphFailure(ex))
        {
            try
            {
                RebuildDocument(path, path, preserveBookmarks: false);
            }
            catch (Exception retryException) when (IsImportedFontWidthFailure(retryException))
            {
                // The replacement is complete. Compaction is optional for this malformed source.
            }
        }
        catch (Exception ex) when (IsImportedFontWidthFailure(ex))
        {
            // The replacement is complete. Compaction is optional for this malformed source.
        }
    }

    /// <summary>Replaces every page from raster-only documents without retaining superseded resources.</summary>
    internal static void ReplaceAllPagesAndCompact(
        string path, IReadOnlyList<string> replacementPaths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(replacementPaths);
        if (replacementPaths.Count == 0) return;

        PdfDocument source = PdfDocument.Open(File.ReadAllBytes(path));
        PdfDocumentInformation information = PdfDocumentInformation.Read(source);
        IReadOnlyList<PdfBookmarkInfo> bookmarks;
        try
        {
            bookmarks = PdfBookmarkReader.Read(source);
        }
        catch (Exception ex) when (IsBookmarkGraphFailure(ex))
        {
            bookmarks = [];
        }
        bookmarks = SanitizeRasterizedBookmarks(bookmarks, replacementPaths.Count);

        byte[] result = MergeDocuments([.. replacementPaths.Select(File.ReadAllBytes)]);
        ReplaceWithBuiltResult(path, result);
        ApplyDocumentMetadata(path, new PdfDocumentMetadata
        {
            Title = information.Title,
            Author = information.Author,
            Subject = information.Subject,
            Keywords = information.Keywords,
            Creator = information.Creator,
            Producer = information.Producer,
            Language = information.Language,
            CreationDate = information.CreationDate,
            ModificationDate = information.ModificationDate,
            Trapped = information.Trapped
        });
        if (bookmarks.Count > 0)
        {
            try
            {
                ReplaceBookmarks(path, bookmarks);
            }
            catch (Exception ex) when (IsBookmarkGraphFailure(ex))
            {
                // The rasterized pages are complete. A broken source outline must not discard them.
            }
        }
    }

    internal static List<PdfBookmarkInfo> SanitizeRasterizedBookmarks(
        IReadOnlyList<PdfBookmarkInfo> bookmarks, int pageCount)
    {
        var result = new List<PdfBookmarkInfo>();
        foreach (PdfBookmarkInfo bookmark in bookmarks)
        {
            List<PdfBookmarkInfo> children =
                SanitizeRasterizedBookmarks(bookmark.Children, pageCount);
            if (bookmark.DestinationPageIndex is int pageIndex
                && pageIndex >= 0 && pageIndex < pageCount)
            {
                result.Add(bookmark with
                {
                    NamedDestination = null,
                    Children = children
                });
            }
            else
            {
                result.AddRange(children);
            }
        }
        return result;
    }

    // The editor names the outline in its failures but has no exception type of its own for them, so
    // the wording decides. The wrapper that names the failed file and the file errors that quote a path
    // are skipped, because a folder called "bookmarks" is not a damaged outline.
    internal static bool IsBookmarkGraphFailure(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current.Data.Contains(ImportSourceMarker)
                || current is IOException or UnauthorizedAccessException or ImportNoPagesException)
                continue;
            if (current.Message.Contains("bookmark", StringComparison.OrdinalIgnoreCase)
                || current.Message.Contains("/Outlines", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static bool IsImportedFontWidthFailure(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (current is InvalidOperationException
                && current.Message.Contains(
                    "has inconsistent /FirstChar, /LastChar, or /Widths values.",
                    StringComparison.Ordinal))
                return true;
        return false;
    }

    /// <summary>Resets the replaced page's application rotation.</summary>
    internal static void RemapRotationsAfterPageReplacement(
        Dictionary<int, int> rotations, int pageIndex)
        => RemapRotationsAfterPageReplacements(rotations, [pageIndex]);

    internal static void RemapRotationsAfterPageReplacements(
        Dictionary<int, int> rotations, IReadOnlyList<int> pageIndices)
    {
        ArgumentNullException.ThrowIfNull(rotations);
        ArgumentNullException.ThrowIfNull(pageIndices);
        foreach (int pageIndex in pageIndices.Distinct())
        {
            if (!rotations.ContainsKey(pageIndex))
                throw new ArgumentOutOfRangeException(nameof(pageIndices));
            rotations[pageIndex] = 0;
        }
    }

    /// <summary>Creates a new document from selected working-document pages.</summary>
    internal static void ExtractPages(
        string sourcePath, string destinationPath, IReadOnlyList<int> pageIndices,
        IReadOnlyDictionary<int, int> rotations)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(pageIndices);
        ArgumentNullException.ThrowIfNull(rotations);
        if (pageIndices.Count == 0)
            throw new ArgumentException("At least one page must be extracted.", nameof(pageIndices));

        PdfDocument source = PdfDocument.Open(File.ReadAllBytes(sourcePath));
        PdfDocument empty = PdfDocument.Open(new KillerPdf.Engine.Authoring.PdfDocumentBuilder().Build());
        var editor = new PdfIncrementalPageEditor(empty)
            .InsertImportedPages(0, source, pageIndices);
        for (int outputIndex = 0; outputIndex < pageIndices.Count; outputIndex++)
        {
            editor.ClearPageThumbnail(outputIndex);
            editor.SetRotation(outputIndex,
                rotations.TryGetValue(pageIndices[outputIndex], out int rotation) ? rotation : 0);
        }
        ReplaceWithBuiltResult(destinationPath, editor.Build());
    }

    /// <summary>Appends complete PDF documents and normalizes their rotations for the viewer.</summary>
    internal static void AppendDocuments(
        string path, IReadOnlyList<ImportedDocument> sources)
        => InsertDocuments(path, sources, int.MaxValue);

    /// <summary>Inserts complete PDF documents at a zero-based page position.</summary>
    internal static void InsertDocuments(
        string path, IReadOnlyList<ImportedDocument> sources, int insertionIndex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Count == 0) return;

        PdfDocument target = PdfDocument.Open(File.ReadAllBytes(path));
        var editor = new PdfIncrementalPageEditor(target);
        int offset = insertionIndex == int.MaxValue ? editor.PageCount : insertionIndex;
        if (offset < 0 || offset > editor.PageCount)
            throw new ArgumentOutOfRangeException(nameof(insertionIndex));
        var sourcePaths = new Dictionary<PdfDocument, string>();
        foreach (ImportedDocument import in sources)
        {
            string sourcePath = import.OriginalPath ?? import.Path;
            try
            {
                PdfDocument source = OpenPageSource(File.ReadAllBytes(import.Path));
                sourcePaths.Add(source, sourcePath);
                int count = new PdfIncrementalPageEditor(source).PageCount;
                if (import.PageRotations.Count != count)
                    throw new ArgumentException(
                        "The imported rotation count must match the source page count.", nameof(sources));
                editor.InsertImportedPages(offset, source, [.. Enumerable.Range(0, count)]);
                for (int index = 0; index < count; index++)
                    editor.SetRotation(offset + index, 0);
                offset += count;
            }
            catch (Exception ex) when (IsImportFailure(ex))
            {
                throw WithImportSource(sourcePath, ex);
            }
        }
        ReplaceWithBuiltResult(path, BuildImportedDocuments(editor, sourcePaths));
    }

    /// <summary>Appends imported page rotations to the application rotation map.</summary>
    internal static void RemapRotationsAfterDocumentAppend(
        Dictionary<int, int> rotations, IReadOnlyList<ImportedDocument> sources)
        => RemapRotationsAfterDocumentInsertion(rotations, sources, rotations.Count);

    internal static void RemapRotationsAfterDocumentInsertion(
        Dictionary<int, int> rotations, IReadOnlyList<ImportedDocument> sources, int insertionIndex)
    {
        ArgumentNullException.ThrowIfNull(rotations);
        ArgumentNullException.ThrowIfNull(sources);
        if (insertionIndex < 0 || insertionIndex > rotations.Count)
            throw new ArgumentOutOfRangeException(nameof(insertionIndex));
        var ordered = Enumerable.Range(0, rotations.Count).Select(index => rotations[index]).ToList();
        var inserted = new List<int>();
        foreach (ImportedDocument source in sources)
            foreach (int rotation in source.PageRotations)
                inserted.Add(((rotation % 360) + 360) % 360);
        ordered.InsertRange(insertionIndex, inserted);
        rotations.Clear();
        for (int index = 0; index < ordered.Count; index++) rotations[index] = ordered[index];
    }

    /// <summary>Turns selected application-managed pages without mutating the live PDF model.</summary>
    internal static void RemapRotationsAfterPageTurns(
        Dictionary<int, int> rotations, IReadOnlyList<int> pageIndices, int delta)
    {
        ArgumentNullException.ThrowIfNull(rotations);
        ArgumentNullException.ThrowIfNull(pageIndices);
        if (delta % 90 != 0)
            throw new ArgumentOutOfRangeException(nameof(delta),
                "Page rotation changes must be multiples of 90 degrees.");
        foreach (int pageIndex in pageIndices.Distinct())
        {
            if (!rotations.TryGetValue(pageIndex, out int current))
                throw new ArgumentOutOfRangeException(nameof(pageIndices),
                    $"Page index {pageIndex} has no rotation state.");
            rotations[pageIndex] = ((current + delta) % 360 + 360) % 360;
        }
    }

    /// <summary>
    /// Reduce File Size (#433): optionally resamples oversized images, then runs the lossless
    /// optimizer, and writes the result to <paramref name="outputPath"/>. Returns the size before
    /// and after and how many images were resampled.
    /// </summary>
    internal static (long Before, long After, int ImagesResampled,
        IReadOnlyList<PdfReferenceRepair> ReferenceRepairs) ReduceFileSize(string sourcePath,
        string outputPath, PdfImageDownsampleOptions? images, System.Threading.CancellationToken cancellationToken)
    {
        byte[] original = File.ReadAllBytes(sourcePath);
        PdfDocument document = OpenForReading(original);
        int resampled = 0;
        if (images is not null)
        {
            PdfImageDownsampleResult downsampled = PdfImageDownsampler.Downsample(document, images, cancellationToken);
            if (downsampled.ImagesResampled > 0)
            {
                document = OpenForReading(downsampled.Data);
                resampled = downsampled.ImagesResampled;
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        PdfOptimizationOptions settings = PdfOptimizationOptions.ForDocument(document) with
        {
            RepairHarmlessArtifacts = true,
            ReplaceDanglingReferences = true
        };
        PdfOptimizationResult optimized = PdfOptimizer.CreatePlan(document, settings).Apply();
        cancellationToken.ThrowIfCancellationRequested();
        string finished = Path.Combine(Path.GetTempPath(), $"killerpdf-reduced-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(finished, optimized.Data.ToArray());
            ReplaceFileAtomically(finished, outputPath);
        }
        finally
        {
            if (File.Exists(finished)) File.Delete(finished);
        }
        return (original.Length, optimized.OutputSize, resampled, optimized.ReferenceRepairs);
    }

    /// <summary>Replaces a destination with a finished file in one step. The finished bytes are
    /// first copied beside the destination so the final swap stays on one volume, which leaves
    /// the previous destination intact when the write fails partway.</summary>
    internal static void ReplaceFileAtomically(string finishedPath, string destinationPath)
        => ReplaceFileAtomically(finishedPath, destinationPath, new AtomicFileOutput.FileOperations());

    internal static void ReplaceFileAtomically(string finishedPath, string destinationPath, AtomicFileOutput.FileOperations files)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(finishedPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        AtomicFileOutput.Write(destinationPath, output =>
        {
            using var input = new FileStream(finishedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            input.CopyTo(output);
        }, default, files);
    }

    private static void ReplaceWithBuiltResult(string path, byte[] result)
        => AtomicFileOutput.Write(path, result);
}
