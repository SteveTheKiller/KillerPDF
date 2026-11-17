using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using KillerPDF.Services;

namespace KillerPDF.Shell
{
    /// <summary>
    /// Single-instance entry points: App forwards a second launch's file path here rather than
    /// starting another process.
    ///
    /// These stay on the window: RestoreAndActivate drives WindowState, Activate() and Topmost,
    /// which a UserControl does not have. The OpenInNewTab call routes through ActiveViewer so the
    /// forwarded file lands in whichever pane has focus.
    /// </summary>
    public partial class MainWindow
    {
        /// <summary>A forwarded path that arrived before the panes existed, replayed from Loaded.</summary>
        private string? _pendingExternalPath;
        private int _externalOpenInFlight;

        private enum BrowserOpenFailure
        {
            TooLarge,
            NotPdf,
            DownloadFailed,
            TimedOut,
            FileError,
            Unusable,
        }

        private sealed class BrowserOpenException(BrowserOpenFailure failure) : System.Exception
        {
            internal BrowserOpenFailure Failure { get; } = failure;
        }

        /// <summary>#480: images opened with KillerPDF, waiting to become one new PDF.</summary>
        private readonly System.Collections.Generic.List<string> _externalImages = [];
        private System.Windows.Threading.DispatcherTimer? _externalImageTimer;

        public async void OpenFromExternal(string? path)
        {
            App.InvalidateUpdateRestart();
            // Explorer starts one launch per selected image, and each is forwarded here on its own. They
            // gather until none has arrived for a moment, then open together as one unsaved PDF. Checked
            // before the pane check below, whose single held path would keep only the last image.
            if (!string.IsNullOrEmpty(path) && ImagesToPdfPlan.IsImage(path) && File.Exists(path))
            {
                QueueExternalImage(path);
                return;
            }
            // A second launch is forwarded here off the pipe thread, and Application.MainWindow is
            // already set while this window's constructor is still running - so ActiveViewer can
            // still be null (it is assigned by InitSplitPanes). Hold the path and let Loaded
            // replay it rather than dereferencing null and losing the file the user double-clicked.
            if (ActiveViewer == null)
            {
                _pendingExternalPath = path;
                return;
            }

            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                ActiveViewer.OpenInNewTabExt(path!);
                return;
            }
            if (ProtocolRegistrar.TryGetTargetUrl(path, out var target, out var rejection) && target != null)
            {
                _externalOpenInFlight++;
                try { await OpenProtocolUrlAsync(target); }
                finally { _externalOpenInFlight--; }
                return;
            }
            // #267 follow-up: say why a killerpdf: launch was refused. Without this the browser
            // hands off, the app comes up on an empty viewer and nothing explains it. NotAHandoff
            // stays silent because that is a plain path that simply is not on disk any more.
            if (rejection != ProtocolRegistrar.HandoffRejection.NotAHandoff)
                ShowRefusedHandoff(rejection);
        }

        private void QueueExternalImage(string path)
        {
            _externalImages.Add(path);
            if (_externalImageTimer is null)
            {
                _externalImageTimer = new System.Windows.Threading.DispatcherTimer(
                    System.Windows.Threading.DispatcherPriority.Background, Dispatcher)
                { Interval = ImagesToPdfPlan.OpenWithQuietPeriod };
                _externalImageTimer.Tick += (_, _) => FlushExternalImages();
            }
            _externalImageTimer.Stop();   // restart the quiet period
            _externalImageTimer.Start();
        }

        private void FlushExternalImages()
        {
            if (_windowResourcesReleased)
            {
                _externalImageTimer?.Stop();
                _externalImages.Clear();
                return;
            }
            if (ActiveViewer == null) return;   // panes not built yet: try again on the next tick
            _externalImageTimer?.Stop();
            var images = ImagesToPdfPlan.Order(_externalImages.Where(File.Exists));
            _externalImages.Clear();
            if (images.Count > 0) OpenImagesAsNewDocument(images);
        }

        /// <summary>The same dialog a failed browser download gets, with the refusal as the reason.</summary>
        private void ShowRefusedHandoff(ProtocolRegistrar.HandoffRejection rejection)
        {
            string reason = rejection switch
            {
                ProtocolRegistrar.HandoffRejection.UnknownCommand => Loc("Str_Handoff_UnknownCommand"),
                ProtocolRegistrar.HandoffRejection.MissingUrl => Loc("Str_Handoff_MissingUrl"),
                ProtocolRegistrar.HandoffRejection.MalformedUrl => Loc("Str_Handoff_MalformedUrl"),
                ProtocolRegistrar.HandoffRejection.SchemeNotAllowed => Loc("Str_Handoff_SchemeNotAllowed"),
                _ => Loc("Str_Handoff_Unusable"),
            };
            KillerDialog.Show(this,
                Loc("Str_Handoff_Failed") + "\n\n" + reason,
                "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        /// <summary>Replay whatever arrived during startup. No-op in the normal case.</summary>
        internal void FlushPendingExternalOpen()
        {
            if (_pendingExternalPath == null) return;
            var path = _pendingExternalPath;
            _pendingExternalPath = null;
            OpenFromExternal(path);
        }

        // The download's own file name when the URL ends in one, so the first Save can offer it.
        private static string BrowserFileName(System.Uri target)
        {
            string name = System.Uri.UnescapeDataString(Path.GetFileName(target.AbsolutePath));
            foreach (char invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
            name = name.Trim().TrimEnd('.');
            if (name.Length == 0) name = "document";
            return name.EndsWith(".pdf", System.StringComparison.OrdinalIgnoreCase) ? name : name + ".pdf";
        }

        private async Task OpenProtocolUrlAsync(System.Uri target)
        {
            string temp = App.MakeTempFile("browser");
            try
            {
                using var http = new HttpClient(PublicAddressGuard.CreateHandler()) { Timeout = System.TimeSpan.FromSeconds(90) };
                const long MaxBytes = 256L * 1024 * 1024;
                using (var output = File.Create(temp))
                    await HttpDownload.CopyToAsync(http, target, output, System.TimeSpan.FromSeconds(90), MaxBytes);

                using (var check = File.OpenRead(temp))
                {
                    var magic = new byte[5];
                    if (check.Read(magic, 0, magic.Length) != magic.Length ||
                        System.Text.Encoding.ASCII.GetString(magic) != "%PDF-")
                        throw new BrowserOpenException(BrowserOpenFailure.NotPdf);
                }
                App.RememberSuggestedName(temp, BrowserFileName(target));
                ActiveViewer.OpenInNewTabExt(temp);
            }
            catch (System.Exception ex)
            {
                try { File.Delete(temp); } catch { }
                BrowserOpenFailure failure = ex switch
                {
                    BrowserOpenException known => known.Failure,
                    HttpDownloadSizeException => BrowserOpenFailure.TooLarge,
                    System.OperationCanceledException => BrowserOpenFailure.TimedOut,
                    HttpRequestException => BrowserOpenFailure.DownloadFailed,
                    IOException => BrowserOpenFailure.FileError,
                    _ => BrowserOpenFailure.Unusable,
                };
                ShowBrowserOpenFailure(failure);
            }
        }

        private void ShowBrowserOpenFailure(BrowserOpenFailure failure)
        {
            string reason = failure switch
            {
                BrowserOpenFailure.TooLarge => Loc("Str_Handoff_TooLarge"),
                BrowserOpenFailure.NotPdf => Loc("Str_Handoff_NotPdf"),
                BrowserOpenFailure.DownloadFailed => Loc("Str_Handoff_DownloadFailed"),
                BrowserOpenFailure.TimedOut => Loc("Str_Handoff_TimedOut"),
                BrowserOpenFailure.FileError => Loc("Str_Handoff_FileError"),
                _ => Loc("Str_Handoff_Unusable"),
            };
            KillerDialog.Show(this,
                Loc("Str_Handoff_Failed") + "\n\n" + reason,
                "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        public void RestoreAndActivate()
        {
            _tray.ShowWindow();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            IntPtr handle = new WindowInteropHelper(this).Handle;
            if (handle != IntPtr.Zero) SetForegroundWindow(handle);
            // Briefly toggle Topmost to pull the window in front without keeping it pinned.
            Topmost = true;
            Topmost = false;
            Focus();
        }

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetForegroundWindow(IntPtr hWnd);
    }
}
