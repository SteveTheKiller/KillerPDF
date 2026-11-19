using System.IO;
using System.Text;
using System.Windows;
using KillerPdf.Engine.Documents;
using KillerPDF.Services;

namespace KillerPDF.Shell;

public partial class MainWindow
{
    private string[]? SelectBatchInputs(string title)
    {
        var picker = new Controls.FileDialog(Controls.FileDialogMode.Open)
        {
            Title = title, Filter = Loc("Str_Filter_Pdf") + "|*.pdf", Multiselect = true
        };
        if (picker.ShowDialog(this) != true || picker.FileNames.Length == 0) return null;
        var order = new DocumentListDialog(this, title, picker.FileNames, reorder: true);
        return order.ShowDialog() == true ? order.Order.Select(index => picker.FileNames[index]).ToArray() : null;
    }

    private string[]? SelectBatchOutputs(string title, string[] inputs, string suffix, PdfMacro? macro = null)
    {
        bool renames = macro?.Steps.Any(step => step.Operation == PdfMacroOperation.Rename) == true;
        string name = renames ? PdfRenameMacro.PlanFileNames(macro!, inputs)[0]
            : Path.GetFileNameWithoutExtension(inputs[0]) + "-" + suffix + ".pdf";
        var picker = new Controls.FileDialog(Controls.FileDialogMode.Save)
        {
            Title = title, Filter = Loc("Str_Filter_Pdf") + "|*.pdf", DefaultExt = "pdf",
            FileName = name,
            InitialDirectory = renames ? Path.GetDirectoryName(inputs[0]) ?? "" : "",
            IsFileNameReadOnly = renames
        };
        if (picker.ShowDialog(this) != true) return null;
        return macro is null ? PdfEngineIntegration.PlanBatchOutputs(inputs, picker.FileName)
            : PdfEngineIntegration.PlanMacroOutputs(macro, inputs, picker.FileName);
    }

    private async void RunMacroBatch(PdfMacro macro, PdfMacroDispatcher dispatcher)
    {
        if (macro.Steps.Any(step => step.Operation == PdfMacroOperation.DataMerge))
        {
            RunDataMergeMacro(macro, batch: true);
            return;
        }
        string title = Loc("Str_Macro_Batch");
        try
        {
            string[]? inputs = SelectBatchInputs(title);
            if (inputs is null) return;
            string[]? outputs = SelectBatchOutputs(title, inputs, SafeMacroFileName(macro.Name), macro);
            if (outputs is null) return;
            string[][] plannedFiles = outputs.Select(output => PdfMacroExportFiles.Plan(macro, [output], inputs)).ToArray();
            var overwrite = plannedFiles.SelectMany(paths => paths).Where(File.Exists).ToHashSet(StringComparer.OrdinalIgnoreCase);
            string preview = string.Join("\n", macro.Steps.Select((step, index) =>
                (index + 1) + ". " + MacroOperationName(step.Operation))) + "\n\n"
                + string.Join("\n", inputs.Select((input, index) => input + "\n"
                    + string.Join("\n", plannedFiles[index].Select(path => "  > " + path
                        + (overwrite.Contains(path) ? " (" + Loc("Str_Macro_Replace") + ")" : "")))));
            var review = new ImportReviewDialog(this, title, preview, null, Loc("Str_Btn_Run"));
            if (review.ShowDialog() != true) return;
            if (macro.Steps.Any(step => step.Operation == PdfMacroOperation.Ocr)
                && !await EnsureOcrModelsReadyAsync()) return;
            _macroRedaction = new MacroRedaction();
            if (macro.Steps.Any(step => step.Operation == PdfMacroOperation.Redact))
            {
                if (!PrepareMacroRedaction()) return;
                _macroRedaction.Marked.Clear();
                if (_macroRedaction.Terms.Length == 0) { KillerDialog.Show(this, Loc("Str_Macro_RedactNothing")); return; }
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
                    try
                    {
                        byte[] source = await File.ReadAllBytesAsync(inputs[index], token);
                        PdfMacroRunReport result = await Task.Run(() => dispatcher.RunReport(macro,
                            [new ReadOnlyMemory<byte>(source)],
                            macro.Steps.Any(step => step.Operation == PdfMacroOperation.Rename)
                                ? PdfRenameMacro.CreateContext(Path.GetFileName(inputs[index]), index + 1) : null,
                            token), token);
                        PdfMacroFileResult file = AssertSingleResult(result);
                        if (file.WasCanceled || _macroRedaction.Canceled) throw new OperationCanceledException(token);
                        if (file.Succeeded && file.Data is ReadOnlyMemory<byte> data)
                        {
                            token.ThrowIfCancellationRequested();
                            if (file.OutputFileName is not null && file.OutputFileName != Path.GetFileName(outputs[index]))
                                throw new InvalidOperationException(Loc("Str_Macro_OutputNameDrift"));
                            bool written = WriteGeneratedMacroOutputs([new(data, Path.GetFileName(outputs[index]))
                                { Exports = file.Exports }], plannedFiles[index], overwrite, report, token);
                            if (written) report.AppendLine(string.Format(Loc("Str_Macro_Complete"), outputs[index]));
                            if (written && MacroSurvivorNote(data) is { } survivors) report.AppendLine(survivors);
                        }
                        else report.AppendLine(Services.LocalizedReports.MacroRun(result, MacroOperationName));
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    {
                        report.AppendLine(Loc("Str_Macro_Failed") + " " + error.Message);
                    }
                }
            }
            catch (OperationCanceledException) { report.AppendLine(Loc("Str_Macro_Canceled")); }
            finally { HideBusyOverlay(busy); EndCancellableOp(token); }
            new DocumentReportDialog(this, title, report.ToString()).ShowDialog();
        }
        catch (Exception error) { ShowExchangeError(error); }
    }
}
