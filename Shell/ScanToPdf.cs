using System.IO;
using System.Windows;
using System.Windows.Interop;
using KillerPDF.Services;

namespace KillerPDF.Shell
{
    // Scan to PDF (#73): scan through TWAIN and open the pages as a new, unsaved document,
    // the same way Import opens images.
    public partial class MainWindow
    {
        private const string ScanSourceSetting = "ScanSource";
        private const string ScanSettingsWindowSetting = "ScanShowSettings";

        private async void ScanToPdf()
        {
            if (!TwainScan.ManagerInstalled)
            {
                KillerDialog.Show(this, Loc("Str_Scan_NoManager"), "KillerPDF",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            IntPtr window = new WindowInteropHelper(this).Handle;
            string folder = Path.Combine(Path.GetTempPath(), $"KillerPDF-scan-{DateTime.Now:yyyyMMdd-HHmmss-fff}");
            TwainScanResult result;
            try
            {
                using TwainScan scan = TwainScan.Open(window);
                IReadOnlyList<NTwain.DataSource> sources = scan.Sources;
                if (sources.Count == 0)
                {
                    KillerDialog.Show(this, Loc("Str_Scan_NoScanners"), "KillerPDF",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                var dialog = new ScanDialog(this, [.. sources.Select(source => source.Name)],
                    App.GetSetting(ScanSourceSetting), App.GetSetting(ScanSettingsWindowSetting) != "0");
                dialog.ShowDialog();
                if (!dialog.Confirmed || dialog.SelectedIndex < 0) return;
                NTwain.DataSource chosen = sources[dialog.SelectedIndex];
                App.SetSetting(ScanSourceSetting, chosen.Name);
                App.SetSetting(ScanSettingsWindowSetting, dialog.ShowSettings ? "1" : "0");
                SetStatus(Loc("Str_Scan_Scanning"));
                result = await scan.ScanAsync(chosen, dialog.ShowSettings, folder);
            }
            catch (Exception ex)
            {
                SetStatus("");
                KillerDialog.Show(this, Loc("Str_Scan_Failed") + "\n" + ex.Message + ScanRecoveryMessage(folder), "KillerPDF",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            IReadOnlyList<string> pages = result.Pages;
            string completion = result.Errors.Count > 0
                ? string.Format(Loc("Str_Scan_Partial"), pages.Count)
                : result.Canceled ? Loc("Str_St_Canceled") + ": " + string.Format(Loc("Str_Scan_Done"), pages.Count)
                : string.Format(Loc("Str_Scan_Done"), pages.Count);
            if (!result.Completed)
            {
                SetStatus(completion);
                KillerDialog.Show(this, completion + "\n"
                    + string.Join("\n", result.Errors.Select(error => error.Message))
                    + ScanRecoveryMessage(folder), "KillerPDF", MessageBoxButton.OK,
                    result.Errors.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
            }
            if (pages.Count == 0)
            {
                if (result.Completed) SetStatus(Loc("Str_Scan_NoPages"));
                DeleteScanFolder(folder);
                return;
            }

            var target = BeginTabLoad(out var prev, out bool createdNew);
            bool opened = false;
            try
            {
                string tempPath = Path.Combine(Path.GetTempPath(), $"Scanned-{DateTime.Now:yyyyMMdd-HHmmss-fff}.pdf");
                File.WriteAllBytes(tempPath, PdfEngineIntegration.MergeFiles(pages));
                _doc = PdfWorkingDocument.Open(tempPath, null);
                FinishOpenFile("Scanned.pdf", tempPath);
                _originalFile = null;   // no saved location yet, so Save becomes Save As
                MarkDirty(true);
                SetStatus(completion);
                CaptureSessionState(_active!);
                SetTool(_currentTool);
                RebuildTabStrip();
                opened = true;
            }
            catch (Exception ex)
            {
                AbortTabLoad(target, prev, createdNew);
                KillerDialog.Show(this, Loc("Str_Scan_Failed") + "\n" + ex.Message + ScanRecoveryMessage(folder), "KillerPDF",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                if (opened && result.Completed) DeleteScanFolder(folder);
            }
        }

        private string ScanRecoveryMessage(string folder) => Directory.Exists(folder)
            ? "\n\n" + string.Format(Loc("Str_Scan_RecoveryFiles"), folder) : string.Empty;

        private static void DeleteScanFolder(string folder)
        {
            try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
