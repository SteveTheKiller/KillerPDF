using System.IO;
using System.Windows;
using System.Windows.Controls;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Formats.Epub;
using KillerPDF.Services;

namespace KillerPDF.Shell;

public partial class MainWindow
{
    private async void ConvertNativeDocumentToPdf()
    {
        if (_busyCts is not null) return;
        var viewer = ActiveViewer;
        var source = viewer.NativeDocumentRef;
        string? sourcePath = viewer.CurrentFilePathExt;
        if (source is null || string.IsNullOrWhiteSpace(sourcePath)) return;
        EpubPdfConversionOptions? epubOptions = null;
        try
        {
            if (source.Format == PdfDocumentFormat.Epub)
            {
                var options = new NativePdfConversionDialog(this);
                if (options.ShowDialog() != true) return;
                var face = PdfEngineIntegration.LoadTextFont("Segoe UI", "A", bold: false, italic: false)
                    ?? throw new NotSupportedException("The EPUB conversion font is unavailable.");
                epubOptions = new EpubPdfConversionOptions(face.Font)
                {
                    PageWidthPoints = options.WidthPoints,
                    PageHeightPoints = options.HeightPoints,
                    MarginPoints = options.MarginPoints,
                    FontFaceResolver = PdfEngineIntegration.EpubFontFaceResolver(face.Family)
                };
            }
            var picker = new Controls.FileDialog(Controls.FileDialogMode.Save)
            {
                Title = Loc("Str_NativePdf_Convert"), Filter = Loc("Str_Filter_Pdf") + "|*.pdf",
                FileName = Path.GetFileNameWithoutExtension(sourcePath) + ".pdf",
                DefaultExt = "pdf", AddExtension = true, RequireFilterExtension = true,
                CheckFileExists = false, CheckPathExists = true, OverwritePrompt = false
            };
            string? directory = Path.GetDirectoryName(sourcePath);
            if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory)) picker.InitialDirectory = directory;
            if (picker.ShowDialog(this) != true) return;
            if (!ReferenceEquals(viewer.NativeDocumentRef, source)) return;
            string destination = picker.FileName;
            if (App.IsCleanupOwnedPath(destination))
                throw new InvalidOperationException("The converted PDF requires a permanent destination.");
            string label = Loc("Str_NativePdf_Convert");
            var token = BeginCancellableOp(label);
            var busy = ShowBusyOverlay(string.Format(Loc("Str_Convert_Working"), Path.GetFileName(sourcePath)));
            if (busy.Child is Panel panel)
            {
                var cancel = UiKit.Make(Loc("Str_Btn_Cancel"), accent: false);
                cancel.Name = "NativeConversionBusyCancel";
                cancel.Margin = new Thickness(0, 14, 0, 0);
                cancel.Click += (_, _) =>
                {
                    if (_busyCts?.Token == token) _busyCts.Cancel();
                    cancel.IsEnabled = false;
                };
                panel.Children.Add(cancel);
            }
            try
            {
                await Task.Run(() =>
                {
                    if (source.Format == PdfDocumentFormat.Epub)
                        NativePdfConversion.WriteNewEpub(source, sourcePath, destination, epubOptions!, token);
                    else
                        NativePdfConversion.WriteNewDjvu(source, sourcePath, destination, token);
                });
            }
            catch (OperationCanceledException)
            {
                SetStatus(Loc("Str_St_Canceled"));
                return;
            }
            finally
            {
                HideBusyOverlay(busy);
                EndCancellableOp(token);
            }
            if (!IsLoaded || Dispatcher.HasShutdownStarted) return;
            viewer.OpenInNewTabExt(destination);
            SetStatus(string.Format(Loc("Str_St_Saved"), Path.GetFileName(destination)));
        }
        catch (Exception error)
        {
            KillerDialog.Show(this, Loc("Str_Convert_Failed") + "\n" + error.Message, "KillerPDF",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
