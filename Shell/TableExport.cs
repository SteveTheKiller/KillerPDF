using System.IO;
using System.Windows;
using KillerPdf.Engine.Documents;
using KillerPDF.Services;

namespace KillerPDF.Shell;

public partial class MainWindow
{
    private async void ExportTable(int pageIndex)
    {
        if (_doc is null || _currentFile is null) { SetStatus(Loc("Str_Msg_OpenFirst")); return; }
        if (pageIndex < 0 || pageIndex >= _doc.PageCount) return;
        if (!EnsureMayWrite(PdfOutput.Data)) return;
        bool started = false;
        CancellationToken cancellation = default;
        try
        {
            CommitActiveTextBox();
            Func<bool> sourceCurrent = CaptureDocumentOrigin();
            string? currentSource = _currentFile;
            string? originalSource = _originalFile;
            PdfEngineDocumentSession session = EnsureEngineDocumentSession();
            string sourceName = Path.GetFileNameWithoutExtension(originalSource ?? currentSource ?? "document");
            PdfPageInformation page = session.Pages[pageIndex];
            int rotation = ((_pageRotations.TryGetValue(pageIndex, out int turned) ? turned : page.Rotation) % 360 + 360) % 360;
            var preview = RegionPreview(session.Path, pageIndex, page.Rotation, rotation)
                ?? throw new InvalidOperationException(Loc("Str_Err_ExportFailed"));
            var review = new TableExportDialog(this, session.Document, pageIndex, preview, rotation, _appScale);
            review.ShowDialog();
            if (review.Result is not { } table || !sourceCurrent()) return;
            PdfTableExportFormat format = review.Format;
            string extension = format == PdfTableExportFormat.Csv ? ".csv" : ".xlsx";
            var save = new Controls.FileDialog(Controls.FileDialogMode.Save)
            {
                Filter = (format == PdfTableExportFormat.Csv ? "CSV" : "XLSX") + "|*" + extension,
                Title = Loc("Str_TableExport_Title"), CheckFileExists = false, CheckPathExists = true,
                RequireFilterExtension = true,
                FileName = sourceName + "-table" + extension
            };
            if (save.ShowDialog(this) != true || !sourceCurrent()) return;
            string destination = Path.GetFullPath(save.FileName);
            if (new[] { currentSource, originalSource, _currentFile, _originalFile }
                .Any(source => source is not null && string.Equals(Path.GetFullPath(source), destination,
                    StringComparison.OrdinalIgnoreCase)))
            {
                KillerDialog.Show(this, Loc("Str_Merge_SourceOutput"));
                return;
            }
            cancellation = BeginCancellableOp(Loc("Str_Op_Export")); started = true;
            byte[] output = await Task.Run(() => PdfTableExport.Export(table, format, cancellation), cancellation);
            if (!sourceCurrent()) return;
            await AtomicFileOutput.WriteAsync(destination, output, cancellation);
            if (sourceCurrent()) SetStatus(string.Format(Loc("Str_ExportDoc_Done"), Path.GetFileName(destination)));
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            KillerDialog.Show(this, Loc("Str_Err_ExportFailed") + "\n" + error.Message,
                "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { if (started) EndCancellableOp(cancellation); }
    }
}
