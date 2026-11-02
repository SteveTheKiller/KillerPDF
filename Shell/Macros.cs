using System.IO;
using System.Text;
using System.Windows;
using KillerPdf.Engine.Documents;
using KillerPDF.Services;

namespace KillerPDF.Shell;

public partial class MainWindow
{
    private void OpenMacroDialog()
    {
        PdfMacroDispatcher dispatcher = CreateMacroDispatcher();
        PdfMacroOperation[] supported = [.. dispatcher.SupportedOperations];
        PdfMacroOperation[] addable =
        [
            PdfMacroOperation.Optimize,
            PdfMacroOperation.AuditNavigation,
            PdfMacroOperation.RemoveUnsafeLinks,
            PdfMacroOperation.AuditAttachments,
            PdfMacroOperation.RemoveAttachments,
            PdfMacroOperation.Flatten,
            PdfMacroOperation.FlattenLayers,
            PdfMacroOperation.EditLayers,
            PdfMacroOperation.EditPortfolio,
            PdfMacroOperation.SetPageLabels,
            PdfMacroOperation.EditAttachments,
            PdfMacroOperation.Validate,
            PdfMacroOperation.RemoveUnresolvedLinks,
            PdfMacroOperation.Ocr,
            PdfMacroOperation.Redact,
            PdfMacroOperation.Watermark,
            PdfMacroOperation.DataMerge,
            PdfMacroOperation.Resize,
            PdfMacroOperation.ConvertColor,
            PdfMacroOperation.Export,
            PdfMacroOperation.Rename,
            PdfMacroOperation.Save,
            PdfMacroOperation.NumberPages,
            PdfMacroOperation.GenerateBookmarks,
            PdfMacroOperation.GenerateTableOfContents,
            PdfMacroOperation.ImposeNUp,
            PdfMacroOperation.ImposeBooklet,
            PdfMacroOperation.ImposeStepAndRepeat,
            PdfMacroOperation.ImposeCutStack,
            PdfMacroOperation.ImposeManualSequence,
            PdfMacroOperation.ImposePoster
        ];
        var dialog = new MacroDialog(this, supported, addable, OpenWatchedFolders);
        if (dialog.ShowDialog() == true && dialog.MacroToRun is PdfMacro macro)
        {
            if (dialog.RunOnBatch) RunMacroBatch(macro, dispatcher);
            else RunMacro(macro, dispatcher);
        }
    }

    private async void RunMacro(PdfMacro macro, PdfMacroDispatcher dispatcher)
    {
        if (macro.Steps.Any(step => step.Operation == PdfMacroOperation.DataMerge))
        {
            RunDataMergeMacro(macro, batch: false);
            return;
        }
        if (_doc is null || string.IsNullOrWhiteSpace(_currentFile))
        {
            KillerDialog.Show(this, Loc("Str_Msg_OpenFirst"));
            return;
        }

        var sourceViewer = ActiveViewer;
        var sourceSession = sourceViewer.ActiveSessionExt;
        var sourceDocument = _doc;
        bool IsSourceCurrent() => ReferenceEquals(ActiveViewer, sourceViewer)
            && ReferenceEquals(sourceViewer.ActiveSessionExt, sourceSession)
            && ReferenceEquals(_doc, sourceDocument);
        string sourceName = Path.GetFileName(_originalFile ?? _currentFile);
        string sourcePath = _originalFile ?? _currentFile;
        bool renames = macro.Steps.Any(step => step.Operation == PdfMacroOperation.Rename);
        string suggested = Path.GetFileNameWithoutExtension(sourceName) + "-"
            + SafeMacroFileName(macro.Name) + ".pdf";
        try { if (renames) suggested = PdfRenameMacro.PlanFileNames(macro, [sourceName]).Single(); }
        catch (ArgumentException error) { ShowExchangeError(error); return; }
        var outputDialog = new Controls.FileDialog(Controls.FileDialogMode.Save)
        {
            Title = Loc("Str_Macro_SelectOutput"),
            Filter = Loc("Str_Filter_Pdf") + "|*.pdf",
            DefaultExt = "pdf",
            FileName = suggested,
            InitialDirectory = renames ? Path.GetDirectoryName(sourcePath) ?? "" : "",
            IsFileNameReadOnly = renames
        };
        if (outputDialog.ShowDialog(this) != true) return;
        if (!IsSourceCurrent()) return;
        try { outputDialog.FileName = PdfEngineIntegration.PlanMacroOutputs(macro,
            [sourcePath], outputDialog.FileName, [_currentFile, _originalFile ?? _currentFile]).Single(); }
        catch (ArgumentException error) { ShowExchangeError(error); return; }

        string[] plannedFiles = PdfMacroExportFiles.Plan(macro, [outputDialog.FileName],
            [sourcePath, _currentFile, _originalFile ?? _currentFile]);
        var overwrite = plannedFiles.Where(File.Exists).ToHashSet(StringComparer.OrdinalIgnoreCase);
        PdfMacroPreview preview = macro.Preview([
            new PdfMacroPreviewFile(sourceName, outputDialog.FileName,
                File.Exists(outputDialog.FileName))], PdfMacroOverwriteBehavior.Replace);
        string steps = string.Join(Environment.NewLine,
            preview.Steps.Select((step, index) => $"{index + 1}. {MacroOperationName(step.Operation)}"));
        string message = string.Format(Loc("Str_Macro_Preview"), preview.Name,
            sourceName, string.Join(Environment.NewLine, plannedFiles.Select(path => path
                + (overwrite.Contains(path) ? " (" + Loc("Str_Macro_Replace") + ")" : ""))), steps);
        if (KillerDialog.Show(this, message, Loc("Str_DocumentTools_Macros"),
                MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes)
            return;
        if (!IsSourceCurrent()) return;
        if (macro.Steps.Any(step => step.Operation == PdfMacroOperation.Ocr)
            && !await EnsureOcrModelsReadyAsync())
            return;
        if (!IsSourceCurrent()) return;
        _macroRedaction = new MacroRedaction();
        if (macro.Steps.Any(step => step.Operation == PdfMacroOperation.Redact)
            && !PrepareMacroRedaction(sourceViewer))
            return;
        if (!IsSourceCurrent()) return;
        if (!EnsureMayWrite(PdfOutput.Printable)) return;

        CommitActiveTextBox();
        var busy = ShowBusyOverlay(Loc("Str_Macro_Running"));
        CancellationToken token = BeginCancellableOp(Loc("Str_DocumentTools_Macros"));
        string? snapshot = null;
        try
        {
            snapshot = WriteSavedCopyToTemp();
            byte[] source = await File.ReadAllBytesAsync(snapshot, token);
            PdfMacroRunReport report = await Task.Run(() => dispatcher.RunReport(
                macro, [new ReadOnlyMemory<byte>(source)],
                renames ? PdfRenameMacro.CreateContext(sourceName, 1) : null, token), token);
            PdfMacroFileResult result = AssertSingleResult(report);
            bool written = true;
            var writeReport = new StringBuilder();
            if (_macroRedaction.Canceled) throw new OperationCanceledException();
            if (result.Succeeded && result.Data is ReadOnlyMemory<byte> data)
            {
                token.ThrowIfCancellationRequested();
                if (renames && result.OutputFileName != Path.GetFileName(outputDialog.FileName))
                    throw new InvalidOperationException(Loc("Str_Macro_OutputNameDrift"));
                written = WriteGeneratedMacroOutputs([new(data, Path.GetFileName(outputDialog.FileName))
                    { Exports = result.Exports }], plannedFiles, overwrite, writeReport, token);
            }

            HideBusyOverlay(busy);
            SetStatus(result.Succeeded && written
                ? string.Format(Loc("Str_Macro_Complete"), outputDialog.FileName)
                : Loc("Str_Macro_Failed"));
            string reportText = report.ToText() + Environment.NewLine + writeReport;
            if (_macroRedaction.Areas > 0)
                reportText += Environment.NewLine + string.Format(Loc("Str_Macro_Redacted"),
                    _macroRedaction.Areas, _macroRedaction.Pages);
            new DocumentReportDialog(this, Loc("Str_DocumentTools_Macros"), reportText).ShowDialog();
        }
        catch (OperationCanceledException)
        {
            HideBusyOverlay(busy);
            SetStatus(Loc("Str_Macro_Canceled"));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException
            and not StackOverflowException and not AccessViolationException)
        {
            HideBusyOverlay(busy);
            KillerDialog.Show(this, Loc("Str_Macro_Failed") + "\n" + ex.Message,
                "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            if (snapshot is not null) File.Delete(snapshot);
            EndCancellableOp(token);
        }
    }

    private PdfMacroDispatcher CreateMacroDispatcher() => CreateMacroDispatcherForData(null);

    private PdfMacroDispatcher CreateMacroDispatcherForData(IReadOnlyList<IReadOnlyDictionary<string, string?>>? mergeRecords)
    {
        string language = CurrentOcrLanguageString();
        bool formAware = OcrFormAware;
        return new([
            new PdfMacroOperationRegistration(PdfMacroOperation.Save,
                (_, source, _, cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return new PdfMacroOperationResult(source);
                }),
            new PdfMacroOperationRegistration(PdfMacroOperation.Ocr,
                (_, source, _, cancellationToken) =>
                    RecognizeMacroText(source, language, formAware, cancellationToken)),
            new PdfMacroOperationRegistration(PdfMacroOperation.Redact,
                (_, source, _, cancellationToken) => RedactMacroContent(source, cancellationToken)),
            new PdfMacroOperationRegistration(PdfMacroOperation.Watermark,
                PdfEngineBurn.ExecuteWatermarkMacro),
            new PdfMacroOperationRegistration(PdfMacroOperation.Resize, PdfResizeMacro.Execute),
            new PdfMacroOperationRegistration(PdfMacroOperation.ConvertColor, PdfColorConversionMacro.Execute),
            new PdfMacroOperationRegistration(PdfMacroOperation.DataMerge,
                (step, source, _, token) => PdfDataMergeMacro.Execute(step, source,
                    mergeRecords ?? throw new InvalidOperationException("Merge data has not been selected."),
                    token, PdfDataMergeWorkflow.LoadImage)),
            new PdfMacroOperationRegistration(PdfMacroOperation.Rename, PdfRenameMacro.Execute)]);
    }

    // What the next macro run's Redact step removes: the words asked for at the start of the run
    // and the areas already marked with the Redact tool on this document.
    private sealed class MacroRedaction
    {
        internal Controls.PdfViewer? SourceViewer;
        internal Controls.PdfViewer.DocumentSession? SourceSession;
        internal PdfWorkingDocument? SourceDocument;
        internal string[] Terms = [];
        internal List<PdfRedactionRegion> Marked = [];
        internal int Areas;
        internal int Pages;
        internal bool Canceled;
    }

    private MacroRedaction _macroRedaction = new();

    private bool PrepareMacroRedaction(Controls.PdfViewer? sourceViewer = null)
    {
        sourceViewer ??= ActiveViewer;
        _macroRedaction = new MacroRedaction
        {
            SourceViewer = sourceViewer,
            SourceSession = sourceViewer.ActiveSessionExt,
            SourceDocument = sourceViewer.DocumentRef,
            Marked = sourceViewer.PendingRedactionRegions()
        };
        string? text = KillerDialog.PromptLines(this, Loc("Str_Lbl_Redact"),
            Loc("Str_Macro_RedactPrompt"), Loc("Str_Btn_OK"));
        if (text is null) return false;
        _macroRedaction.Terms = [.. text.Split('\n')
            .Select(line => line.Trim()).Where(line => line.Length > 0).Distinct(StringComparer.Ordinal)];
        if (_macroRedaction.Terms.Length > 0 || _macroRedaction.Marked.Count > 0) return true;
        KillerDialog.Show(this, Loc("Str_Macro_RedactNothing"), Loc("Str_DocumentTools_Macros"),
            MessageBoxButton.OK, MessageBoxImage.Information);
        return false;
    }

    // Removes the matches and marked areas with the Redact tool's own engine pass, which verifies
    // that nothing under them is left. A page only becomes an image after the user approves it.
    private PdfMacroOperationResult RedactMacroContent(ReadOnlyMemory<byte> source,
        CancellationToken cancellationToken)
    {
        string path = App.MakeTempFile("macroredact");
        try
        {
            File.WriteAllBytes(path, source.ToArray());
            List<PdfRedactionRegion> regions = [.. _macroRedaction.Marked];
            cancellationToken.ThrowIfCancellationRequested();
            // All terms in one pass, so the document is parsed once rather than twice per term.
            try
            {
                foreach (var (page, rects) in SearchService.FindRedactionTermAreas(path, _macroRedaction.Terms))
                    foreach (var (left, bottom, right, top) in rects)
                        regions.Add(new PdfRedactionRegion(page, new PdfContentBounds(left, bottom, right, top)));
            }
            catch (RedactionPagesSkippedException unsearched)
            {
                // Failing the step writes nothing, and the run report names the pages. This runs on
                // the macro's worker, and the translated text is the window's to read.
                throw new InvalidOperationException(Dispatcher.Invoke(() => unsearched.Localized(
                    Loc("Str_Macro_RedactSkippedPages"), Loc("Str_Search_PagesSkipped"))));
            }
            if (regions.Count == 0) return new PdfMacroOperationResult(source);

            PdfContentRedactionPlan plan = PdfEngineIntegration.AnalyzeRedaction(
                PdfDocument.Open(source.ToArray()), regions);
            var approved = new List<int>();
            var skipped = new HashSet<int>();
            if (plan.RasterPages.Count > 0
                && !Dispatcher.Invoke(() =>
                {
                    var viewer = _macroRedaction.SourceViewer;
                    if (viewer is null || !ReferenceEquals(viewer.ActiveSessionExt, _macroRedaction.SourceSession)
                        || !ReferenceEquals(viewer.DocumentRef, _macroRedaction.SourceDocument))
                        return false;
                    return viewer.ApproveRasterPages(plan.RasterPages, approved, skipped);
                }))
            {
                _macroRedaction.Canceled = true;
                throw new OperationCanceledException(cancellationToken);
            }
            regions.RemoveAll(region => skipped.Contains(region.PageIndex));
            if (regions.Count == 0) return new PdfMacroOperationResult(source);

            cancellationToken.ThrowIfCancellationRequested();
            PdfEngineIntegration.ApplyRedaction(path, regions, approved);
            _macroRedaction.Areas += regions.Count;
            _macroRedaction.Pages += regions.Select(region => region.PageIndex).Distinct().Count();
            return new PdfMacroOperationResult(File.ReadAllBytes(path));
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    // Adds a searchable text layer the same way Make Searchable PDF does, working on temp copies.
    private static PdfMacroOperationResult RecognizeMacroText(ReadOnlyMemory<byte> source,
        string language, bool formAware, CancellationToken cancellationToken)
    {
        string input = App.MakeTempFile("macroocrsrc");
        string output = App.MakeTempFile("macroocr");
        try
        {
            File.WriteAllBytes(input, source.ToArray());
            Features.OcrController.BuildSearchablePdf(input, output, (_, _) => { },
                language, formAware, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return new PdfMacroOperationResult(File.ReadAllBytes(output));
        }
        finally
        {
            foreach (string path in new[] { input, output })
            {
                try { if (File.Exists(path)) File.Delete(path); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static PdfMacroFileResult AssertSingleResult(PdfMacroRunReport report) =>
        report.Results.Count == 1 ? report.Results[0]
            : throw new InvalidOperationException("The macro did not produce one file result.");

    private static void WriteMacroOutput(string destination, ReadOnlyMemory<byte> data, bool overwrite = true,
        CancellationToken cancellationToken = default)
    {
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, data.ToArray());
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite);
        }
        catch
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch { }
            throw;
        }
    }

    private static string SafeMacroFileName(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string safe = new([.. value.Select(character => invalid.Contains(character) ? '_' : character)]);
        return safe.Trim().Length == 0 ? "macro" : safe.Trim();
    }

    private static string MacroOperationName(PdfMacroOperation operation) => Loc(MacroStepEditor.TitleKey(operation));
}
