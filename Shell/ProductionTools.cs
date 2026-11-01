using System.IO;
using KillerPdf.Engine.Documents;
using KillerPDF.Services;

namespace KillerPDF.Shell;

public partial class MainWindow
{
    private void OpenProductionTools() => OpenExchangeCommands("Str_DocumentTools_ProductionLayouts",
        ("Str_Production_NUp", () => ExportProductionLayout(PdfMacroOperation.ImposeNUp)),
        ("Str_Production_Booklet", () => ExportProductionLayout(PdfMacroOperation.ImposeBooklet)),
        ("Str_Production_Repeat", () => ExportProductionLayout(PdfMacroOperation.ImposeStepAndRepeat)),
        ("Str_Production_CutStack", () => ExportProductionLayout(PdfMacroOperation.ImposeCutStack)),
        ("Str_Production_Manual", () => ExportProductionLayout(PdfMacroOperation.ImposeManualSequence)),
        ("Str_Production_Poster", () => ExportProductionLayout(PdfMacroOperation.ImposePoster)),
        ("Str_Production_Bates", RunBatesBatch));

    private async void ExportProductionLayout(PdfMacroOperation operation)
    {
        if (_doc is null) return;
        var sourceViewer = ActiveViewer;
        var sourceSession = sourceViewer.ActiveSessionExt;
        var sourceDocument = _doc;
        string title = Loc(ProductionLayoutDialog.TitleKey(operation));
        var settings = new ProductionLayoutDialog(this, operation);
        if (settings.ShowDialog() != true || settings.Step is null) return;
        if (!ReferenceEquals(ActiveViewer, sourceViewer)
            || !ReferenceEquals(sourceViewer.ActiveSessionExt, sourceSession)
            || !ReferenceEquals(_doc, sourceDocument)) return;
        if (!EnsureEditable(continueAfterUnlock: false)) return;
        string[] sourcePaths = new[] { _currentFile, _originalFile }.OfType<string>().ToArray();
        string? sourceName = Path.GetFileNameWithoutExtension(_originalFile ?? _currentFile);
        string? snapshot = null;
        var busy = ShowBusyOverlay(title);
        CancellationToken token = BeginCancellableOp(title);
        try
        {
            snapshot = CreateOutputSnapshot();
            byte[] source = File.ReadAllBytes(snapshot);
            byte[] result = await Task.Run(() => PdfImpositionMacro.Execute(settings.Step, source, token).ToArray(), token);
            HideBusyOverlay(busy); EndCancellableOp(token);
            if (new ProductionPreviewDialog(this, title, result).ShowDialog() != true) return;
            var picker = new Controls.FileDialog(Controls.FileDialogMode.Save)
            {
                Title = title, Filter = Loc("Str_Filter_Pdf") + "|*.pdf", DefaultExt = "pdf",
                FileName = sourceName + "-layout.pdf"
            };
            if (picker.ShowDialog(this) != true) return;
            PdfEngineIntegration.PlanBatchOutputs(sourcePaths.Concat(
                new[] { _currentFile, _originalFile }.OfType<string>()), picker.FileName);
            PdfEngineIntegration.WriteExchangeFile(picker.FileName, result);
            SetStatus(string.Format(Loc("Str_Macro_Complete"), picker.FileName));
        }
        catch (OperationCanceledException) { SetStatus(Loc("Str_Macro_Canceled")); }
        catch (Exception error) { ShowExchangeError(error); }
        finally { HideBusyOverlay(busy); EndCancellableOp(token); if (snapshot is not null) File.Delete(snapshot); }
    }

    private async void RunBatesBatch()
    {
        string title = Loc("Str_Production_Bates");
        try
        {
            string[]? inputs = SelectBatchInputs(title); if (inputs is null) return;
            var settings = new BatesBatchDialog(this);
            if (settings.ShowDialog() != true || settings.Step is null) return;
            string[]? outputs = SelectBatchOutputs(title, inputs, "Bates"); if (outputs is null) return;
            var overwrite = outputs.Where(File.Exists).ToHashSet(StringComparer.OrdinalIgnoreCase);
            string preview = string.Join("\n", inputs.Select((input, index) => input + "\n > " + outputs[index]
                + (overwrite.Contains(outputs[index]) ? " (" + Loc("Str_Macro_Replace") + ")" : "")));
            if (new ImportReviewDialog(this, title, preview, null, Loc("Str_Btn_Run")).ShowDialog() != true) return;
            var busy = ShowBusyOverlay(title); CancellationToken token = BeginCancellableOp(title);
            var report = new System.Text.StringBuilder();
            try
            {
                var sources = new List<ReadOnlyMemory<byte>>();
                foreach (string input in inputs) sources.Add(await File.ReadAllBytesAsync(input, token));
                var results = await Task.Run(() => PdfPageFurnitureMacro.ExecuteBatesBatch(settings.Step, sources, token), token);
                HideBusyOverlay(busy); EndCancellableOp(token);
                if (results.Count == 0) return;
                var options = settings.Options!;
                var numbering = PdfBatesNumbering.Plan(sources.Select(source => PdfDocumentInformation.Read(PdfDocument.Open(source)).PageCount),
                    new PdfBatesNumberingOptions { StartNumber = options.StartNumber, DigitCount = options.DigitCount,
                        Prefix = options.Prefix, Suffix = options.Suffix });
                var ranges = inputs.Select((input, index) =>
                {
                    var pages = numbering.Where(number => number.DocumentIndex == index).ToArray();
                    return input + "\n" + (pages.Length == 0 ? "" : pages[0].Text + " ... " + pages[^1].Text) + "\n > " + outputs[index];
                });
                if (new ImportReviewDialog(this, title, string.Join("\n\n", ranges), null, Loc("Str_Tf_Apply")).ShowDialog() != true) return;
                if (new ProductionPreviewDialog(this, title, results.Select((bytes, index) =>
                    (Path.GetFileName(inputs[index]), bytes)).ToArray()).ShowDialog() != true) return;
                for (int index = 0; index < results.Count; index++)
                {
                    try
                    {
                        WriteMacroOutput(outputs[index], results[index], overwrite.Contains(outputs[index]));
                        report.AppendLine(string.Format(Loc("Str_Macro_Complete"), outputs[index]));
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    { report.AppendLine(outputs[index] + ": " + error.Message); }
                }
            }
            catch (OperationCanceledException) { report.AppendLine(Loc("Str_Macro_Canceled")); }
            finally { HideBusyOverlay(busy); EndCancellableOp(token); }
            new DocumentReportDialog(this, title, report.ToString()).ShowDialog();
        }
        catch (Exception error) { ShowExchangeError(error); }
    }
}
