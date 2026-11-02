using System.IO;
using System.Windows;
using KillerPDF.Services;

namespace KillerPDF.Shell
{
    // Document to PDF (#74): a Word or Excel file opens as a new, unsaved PDF, the same way
    // Import opens images.
    public partial class MainWindow
    {
        private async void ConvertOfficeDocument()
        {
            var dlg = new Controls.FileDialog(Controls.FileDialogMode.Open)
            {
                Title = Loc("Str_Convert_Title"),
                Filter = $"{Loc("Str_Filter_Office")}|*.docx;*.doc;*.xlsx;*.xls|{Loc("Str_Filter_AllFiles")}|*.*"
            };
            if (dlg.ShowDialog(this) != true) return;
            string source = dlg.FileName;
            string name = Path.GetFileName(source);

            SetStatus(string.Format(Loc("Str_Convert_Working"), name));
            byte[] pdf;
            try
            {
                pdf = await Task.Run(() => OfficeConversion.ToPdf(source));
            }
            catch (OfficeNotInstalledException ex)
            {
                SetStatus("");
                KillerDialog.Show(this, string.Format(Loc("Str_Convert_NeedsOffice"), ex.Application), "KillerPDF",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            catch (Exception ex)
            {
                SetStatus("");
                KillerDialog.Show(this, Loc("Str_Convert_Failed") + "\n" + ex.Message, "KillerPDF",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var target = BeginTabLoad(out var prev, out bool createdNew);
            try
            {
                string tempPath = App.MakeTempFile("converted");
                File.WriteAllBytes(tempPath, pdf);
                _doc = PdfWorkingDocument.Open(tempPath, null);
                FinishOpenFile(Path.GetFileNameWithoutExtension(source) + ".pdf", tempPath);
                _originalFile = null;   // no saved location yet, so Save becomes Save As
                MarkDirty(true);
                SetStatus(string.Format(Loc("Str_Convert_Done"), name));
                CaptureSessionState(_active!);
                SetTool(_currentTool);
                RebuildTabStrip();
            }
            catch (Exception ex)
            {
                AbortTabLoad(target, prev, createdNew);
                KillerDialog.Show(this, Loc("Str_Convert_Failed") + "\n" + ex.Message, "KillerPDF",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
