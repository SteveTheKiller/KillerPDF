using System.IO;
using System.Text;
using System.Windows;
using KillerPdf.Engine.Documents;

namespace KillerPDF;

public partial class MainWindow
{
    private void OpenMacroDialog()
    {
        PdfMacroDispatcher dispatcher = CreateMacroDispatcher();
        PdfMacroOperation[] supported = [.. dispatcher.SupportedOperations
            .Where(operation => operation != PdfMacroOperation.Export)];
        PdfMacroOperation[] addable =
        [
            PdfMacroOperation.Optimize,
            PdfMacroOperation.AuditNavigation,
            PdfMacroOperation.RemoveUnsafeLinks,
            PdfMacroOperation.AuditAttachments,
            PdfMacroOperation.RemoveAttachments,
            PdfMacroOperation.Flatten,
            PdfMacroOperation.Validate,
            PdfMacroOperation.RemoveUnresolvedLinks,
            PdfMacroOperation.Save
        ];
        var dialog = new MacroDialog(this, supported, addable);
        if (dialog.ShowDialog() == true && dialog.MacroToRun is PdfMacro macro)
            RunMacro(macro, dispatcher);
    }

    private async void RunMacro(PdfMacro macro, PdfMacroDispatcher dispatcher)
    {
        if (_doc is null || string.IsNullOrWhiteSpace(_currentFile))
        {
            KillerDialog.Show(this, Loc("Str_Msg_OpenFirst"));
            return;
        }

        string sourceName = Path.GetFileName(_originalFile ?? _currentFile);
        string suggested = Path.GetFileNameWithoutExtension(sourceName) + "-"
            + SafeMacroFileName(macro.Name) + ".pdf";
        var outputDialog = new Controls.FileDialog(Controls.FileDialogMode.Save)
        {
            Title = Loc("Str_Macro_SelectOutput"),
            Filter = "PDF|*.pdf",
            DefaultExt = "pdf",
            FileName = suggested
        };
        if (outputDialog.ShowDialog(this) != true) return;

        PdfMacroPreview preview = macro.Preview([
            new PdfMacroPreviewFile(sourceName, outputDialog.FileName,
                File.Exists(outputDialog.FileName))], PdfMacroOverwriteBehavior.Replace);
        string steps = string.Join(Environment.NewLine,
            preview.Steps.Select((step, index) => $"{index + 1}. {MacroOperationName(step.Operation)}"));
        string message = string.Format(Loc("Str_Macro_Preview"), preview.Name,
            sourceName, outputDialog.FileName, steps);
        if (KillerDialog.Show(this, message, Loc("Str_DocumentTools_Macros"),
                MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes)
            return;

        CommitActiveTextBox();
        var busy = ShowBusyOverlay(Loc("Str_Macro_Running"));
        CancellationToken token = BeginCancellableOp(Loc("Str_DocumentTools_Macros"));
        try
        {
            byte[] source = await File.ReadAllBytesAsync(_currentFile, token);
            PdfMacroRunReport report = await Task.Run(() => dispatcher.RunReport(
                macro, [new ReadOnlyMemory<byte>(source)], cancellationToken: token), token);
            PdfMacroFileResult result = AssertSingleResult(report);
            if (result.Succeeded && result.Data is ReadOnlyMemory<byte> data)
                WriteMacroOutput(outputDialog.FileName, data);

            HideBusyOverlay(busy);
            SetStatus(result.Succeeded
                ? string.Format(Loc("Str_Macro_Complete"), outputDialog.FileName)
                : Loc("Str_Macro_Failed"));
            new DocumentReportDialog(this, Loc("Str_DocumentTools_Macros"),
                report.ToText()).ShowDialog();
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
            EndCancellableOp();
        }
    }

    private static PdfMacroDispatcher CreateMacroDispatcher() => new([
        new PdfMacroOperationRegistration(PdfMacroOperation.Save,
            (_, source, _, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return new PdfMacroOperationResult(source);
            })]);

    private static PdfMacroFileResult AssertSingleResult(PdfMacroRunReport report) =>
        report.Results.Count == 1 ? report.Results[0]
            : throw new InvalidOperationException("The macro did not produce one file result.");

    private static void WriteMacroOutput(string destination, ReadOnlyMemory<byte> data)
    {
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, data.ToArray());
            File.Move(temporary, destination, overwrite: true);
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
        string safe = new(value.Select(character => invalid.Contains(character) ? '_' : character)
            .ToArray());
        return safe.Trim().Length == 0 ? "macro" : safe.Trim();
    }

    private static string MacroOperationName(PdfMacroOperation operation)
    {
        string text = operation.ToString();
        var result = new StringBuilder(text.Length + 8);
        for (int index = 0; index < text.Length; index++)
        {
            if (index > 0 && char.IsUpper(text[index]) && !char.IsUpper(text[index - 1]))
                result.Append(' ');
            result.Append(text[index]);
        }
        return result.ToString();
    }
}
