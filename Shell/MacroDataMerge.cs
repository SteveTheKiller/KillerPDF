using System.IO;
using System.Text;
using System.Windows;
using KillerPdf.Engine.Documents;
using KillerPDF.Services;

namespace KillerPDF.Shell;

public partial class MainWindow
{
    private async void RunDataMergeMacro(PdfMacro macro, bool batch)
    {
        string title = Loc("Str_DocumentTools_Macros");
        string? snapshot = null;
        Func<bool> sourceCurrent = CaptureDocumentOrigin();
        try
        {
            if (!batch && (_doc is null || string.IsNullOrWhiteSpace(_currentFile)))
            {
                KillerDialog.Show(this, Loc("Str_Msg_OpenFirst"));
                return;
            }
            string[]? inputs = batch ? SelectBatchInputs(title) : [_originalFile ?? _currentFile!];
            if (inputs is null || inputs.Length == 0) return;
            var dataPicker = new Controls.FileDialog(Controls.FileDialogMode.Open)
            {
                Title = Loc("Str_DataMerge_SelectData"),
                Filter = Loc("Str_DataMerge_DataFiles") + "|*.csv;*.json;*.xlsx"
            };
            if (dataPicker.ShowDialog(this) != true || (!batch && !sourceCurrent())) return;
            var records = PdfDataMergeWorkflow.LoadRecords(dataPicker.FileName);
            var picker = new Controls.FileDialog(Controls.FileDialogMode.Save)
            {
                Title = Loc("Str_DataMerge_SelectOutput"), Filter = Loc("Str_Filter_Pdf") + "|*.pdf", DefaultExt = "pdf",
                FileName = Path.GetFileNameWithoutExtension(inputs[0]) + "-" + SafeMacroFileName(macro.Name) + ".pdf"
            };
            if (picker.ShowDialog(this) != true || (!batch && !sourceCurrent())) return;
            string[] protectedPaths = [.. inputs, dataPicker.FileName, .. new[] { _currentFile, _originalFile }.OfType<string>()];
            string[] seeds = PdfEngineIntegration.PlanBatchOutputs(inputs, picker.FileName);
            PdfDataMergeMacroPlan[] plans = inputs.Select((input, index) =>
                PdfDataMergeWorkflow.PlanMacro(macro, records, input, seeds[index], index + 1, protectedPaths)).ToArray();
            string[] allOutputs = plans.SelectMany(plan => plan.OutputPaths).ToArray();
            if (allOutputs.Distinct(StringComparer.OrdinalIgnoreCase).Count() != allOutputs.Length)
                throw new ArgumentException("Data-merge output filenames must be unique across all input PDFs.");
            var overwrite = allOutputs.Where(File.Exists).ToHashSet(StringComparer.OrdinalIgnoreCase);
            string preview = string.Join("\n", macro.Steps.Select((step, index) =>
                (index + 1) + ". " + MacroOperationName(step.Operation))) + "\n\n"
                + string.Join("\n", inputs.Select((input, index) => input + "\n"
                    + string.Join("\n", plans[index].OutputPaths.Select(path => "  > " + path
                        + (overwrite.Contains(path) ? " (" + Loc("Str_Macro_Replace") + ")" : "")))));
            var review = new ImportReviewDialog(this, title, preview, null, Loc("Str_Btn_Run"));
            if (review.ShowDialog() != true || (!batch && !sourceCurrent())) return;
            if (macro.Steps.Any(step => step.Operation == PdfMacroOperation.Ocr)
                && !await EnsureOcrModelsReadyAsync()) return;
            if (!batch && !sourceCurrent()) return;
            _macroRedaction = new MacroRedaction();
            if (macro.Steps.Any(step => step.Operation == PdfMacroOperation.Redact))
            {
                if (!PrepareMacroRedaction()) return;
                if (batch) _macroRedaction.Marked.Clear();
                if (batch && _macroRedaction.Terms.Length == 0)
                {
                    KillerDialog.Show(this, Loc("Str_Macro_RedactNothing"));
                    return;
                }
            }
            if (!batch && !sourceCurrent()) return;
            if (!batch)
            {
                if (!EnsureEditable(continueAfterUnlock: false)) return;
                CommitActiveTextBox();
                snapshot = WriteSavedCopyToTemp();
            }
            var busy = ShowBusyOverlay(Loc("Str_Macro_Running"));
            CancellationToken token = BeginCancellableOp(title);
            var report = new StringBuilder();
            try
            {
                for (int index = 0; index < inputs.Length; index++)
                {
                    token.ThrowIfCancellationRequested();
                    report.AppendLine(inputs[index]);
                    byte[] source = await File.ReadAllBytesAsync(snapshot ?? inputs[index], token);
                    PdfMacroDispatcher dispatcher = CreateMacroDispatcherForData(plans[index].Merge.Records);
                    PdfMacroRunReport outcome = await Task.Run(() => dispatcher.RunReport(macro,
                        [new ReadOnlyMemory<byte>(source)], PdfRenameMacro.CreateContext(Path.GetFileName(inputs[index]), index + 1), token), token);
                    PdfMacroFileResult result = AssertSingleResult(outcome);
                    if (result.WasCanceled || _macroRedaction.Canceled) throw new OperationCanceledException(token);
                    report.AppendLine(outcome.ToText());
                    if (result.Outputs is not null)
                        WriteGeneratedMacroOutputs(result.Outputs, plans[index].OutputPaths, overwrite, report, token);
                }
            }
            catch (OperationCanceledException) { report.AppendLine(Loc("Str_Macro_Canceled")); }
            finally { HideBusyOverlay(busy); EndCancellableOp(token); }
            new DocumentReportDialog(this, title, report.ToString()).ShowDialog();
        }
        catch (Exception error) when (error is not OutOfMemoryException
            and not StackOverflowException and not AccessViolationException) { ShowExchangeError(error); }
        finally { if (snapshot is not null) File.Delete(snapshot); }
    }

    private static bool WriteGeneratedMacroOutputs(IReadOnlyList<PdfMacroOutput> generated,
        IReadOnlyList<string> planned, IReadOnlySet<string> overwrite, StringBuilder report, CancellationToken token)
    {
        var targets = planned.ToDictionary(path => Path.GetFileName(path), path => path, StringComparer.OrdinalIgnoreCase);
        PdfMacroOutput[] successful = [.. generated.Where(output => output.Succeeded).SelectMany(PdfMacroExportFiles.Expand)];
        if (successful.Any(output => output.OutputFileName is null || !targets.ContainsKey(output.OutputFileName))
            || successful.Select(output => output.OutputFileName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != successful.Length)
            throw new InvalidOperationException("The macro output names differ from their approved plan.");
        bool written = true;
        foreach (PdfMacroOutput output in successful)
        {
            token.ThrowIfCancellationRequested();
            string destination = targets[output.OutputFileName!];
            try
            {
                WriteMacroOutput(destination, output.Data!.Value, overwrite.Contains(destination), token);
                report.AppendLine(destination);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                report.AppendLine(destination + ": " + error.Message);
                written = false;
            }
        }
        return written;
    }
}
