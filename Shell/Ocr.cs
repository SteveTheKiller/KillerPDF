using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KillerPDF.Features;
using KillerPDF.Services;

namespace KillerPDF
{
    public partial class MainWindow : IOcrHost
    {
        // ============================================================
        // OCR - extract text from a rendered page
        // ============================================================

        // Non-null only while a cancellable long-running operation (OCR, repair) is in flight. Esc (see
        // KeyboardShortcuts) offers to cancel it instead of closing the app; loops check the token so a long
        // run stops promptly. _busyOpLabel names the op in the cancel prompt.
        private CancellationTokenSource? _busyCts;
        private string _busyOpLabel = "operation";

        // Registers a cancellable long-running operation and returns its token to thread through the work.
        // Disposing any prior source first keeps the strip->repair handoff (fire-and-forget) clean.
        private CancellationToken BeginCancellableOp(string label)
        {
            _busyCts?.Dispose();
            _busyCts = new CancellationTokenSource();
            _busyOpLabel = label;
            return _busyCts.Token;
        }

        private void EndCancellableOp()
        {
            _busyCts?.Dispose();
            _busyCts = null;
        }

        // ============================================================
        // OCR languages (multi-select, on-demand download)
        // ============================================================

        // The catalog, install checks and traineddata downloads live in Services/OcrLanguages.cs.

        // The user's chosen OCR languages, persisted as a '+'-joined setting. Filtered to those actually
        // installed and never empty - English is the floor.
        private static List<string> GetSelectedOcrLanguages()
        {
            var stored = (App.GetSetting("OcrLanguages") ?? "eng")
                .Split(['+'], StringSplitOptions.RemoveEmptyEntries);
            var sel = new List<string>();
            foreach (var c in stored)
                if (OcrLanguages.IsLanguageInstalled(c) && !sel.Contains(c)) sel.Add(c);
            if (sel.Count == 0) sel.Add("eng");
            return sel;
        }

        private static void SetSelectedOcrLanguages(List<string> langs) =>
            App.SetSetting("OcrLanguages", string.Join("+", langs));

        // The language string handed to Tesseract, e.g. "eng" or "eng+spa".
        private static string CurrentOcrLanguageString() => string.Join("+", GetSelectedOcrLanguages());

        // High-quality (tessdata_best) vs standard model preference, persisted. When on, downloads pull the
        // larger, more accurate "best" models and new languages keep using them.
        private static bool OcrHighQuality => App.GetSetting("OcrHighQuality") == "1";
        private static void SetOcrHighQuality(bool on) => App.SetSetting("OcrHighQuality", on ? "1" : "0");
        private static bool OcrFormAware => App.GetSetting("OcrFormAware") == "1";

        // Builds the multi-select Language submenu. Installed languages are checkable and stay toggled in the
        // open menu; not-yet-installed ones offer a one-time download. At least one language stays selected.
        private MenuItem BuildLanguageMenu()
        {
            OcrNativeBootstrap.EnsureLanguageData();
            var selected = GetSelectedOcrLanguages();
            bool hqPref = OcrHighQuality;

            var root = new MenuItem
            {
                Header = Loc("Str_Ocr_Language"),
                Icon = new TextBlock
                {
                    Text = "",
                    FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            // Not-yet-installed language rows, so the HQ toggle can refresh their "(download)"
            // suffixes in place while the menu stays open.
            var downloadItems = new List<(MenuItem item, string code, string name)>();

            // Header with the Tesseract language code right-aligned, mirroring the Settings language list.
            FrameworkElement LangHeader(string name, string code, string? suffix = null)
            {
                var dp = new DockPanel { HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 170 };
                var codeTb = new TextBlock
                {
                    Text = code, FontFamily = UiKit.MonoFont, FontSize = 11,
                    Foreground = (Brush)FindResource("MutedTextBrush"),
                    Margin = new Thickness(20, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center
                };
                DockPanel.SetDock(codeTb, Dock.Right);
                dp.Children.Add(codeTb);
                dp.Children.Add(new TextBlock { Text = suffix is null ? name : $"{name}  {suffix}", VerticalAlignment = VerticalAlignment.Center });
                return dp;
            }

            foreach (var (code, name) in OcrLanguages.OcrLanguageCatalog)
            {
                bool installed = OcrLanguages.IsLanguageInstalled(code);
                if (installed)
                {
                    var item = new MenuItem
                    {
                        Header = LangHeader(name, code),
                        IsCheckable = true,
                        IsChecked = selected.Contains(code),
                        StaysOpenOnClick = true,
                    };
                    item.Click += (s, _) =>
                    {
                        var mi = (MenuItem)s!;
                        var sel = GetSelectedOcrLanguages();
                        if (mi.IsChecked) { if (!sel.Contains(code)) sel.Add(code); }
                        else
                        {
                            if (sel.Count <= 1) { mi.IsChecked = true; return; }   // keep at least one selected
                            sel.Remove(code);
                        }
                        SetSelectedOcrLanguages(sel);
                        SetStatus(string.Format(Loc("Str_St_OcrLanguage"), string.Join("+", sel)));
                    };
                    root.Items.Add(item);
                }
                else
                {
                    var item = new MenuItem { Header = LangHeader(name, code, hqPref ? Loc("Str_Ocr_SuffixDownloadHq") : Loc("Str_Ocr_SuffixDownload")) };
                    item.Click += (_, _) => DownloadOcrLanguage(code, name);
                    downloadItems.Add((item, code, name));
                    root.Items.Add(item);
                }
            }

            // High-quality toggle. Enabling it upgrades the languages already selected and makes future
            // downloads pull the "best" models too.
            root.Items.Add(new Separator());
            var hq = new MenuItem
            {
                Header = Loc("Str_Ocr_HighQuality"),
                IsChecked = hqPref,
                StaysOpenOnClick = true,   // stay open like the language checkboxes above
            };
            // Flips the persisted preference directly so the setting can't drift from the visual
            // state; the checkmark and the "(download)" suffixes refresh IN PLACE, so the menu can
            // stay open instead of closing just to rebuild those labels.
            hq.Click += (_, _) =>
            {
                bool now = !OcrHighQuality;
                SetOcrHighQuality(now);
                hq.IsChecked = now;
                foreach (var (item, code, name) in downloadItems)
                    item.Header = LangHeader(name, code, now ? Loc("Str_Ocr_SuffixDownloadHq") : Loc("Str_Ocr_SuffixDownload"));
                if (now) RedownloadSelectedHighQuality();
            };
            root.Items.Add(hq);
            return root;
        }

        // Downloads a single language's traineddata (standard or HQ, per the toggle) and selects it.
        private async void DownloadOcrLanguage(string code, string name)
        {
            var ct = BeginCancellableOp(Loc("Str_Op_LangDownload"));
            var busy = ShowBusyOverlay(string.Format(Loc("Str_Busy_LangData"), name));
            string tessDir = OcrNativeBootstrap.EnsureLanguageData();
            string dest = Path.Combine(tessDir, code + ".traineddata");
            try
            {
                using var http = OcrLanguages.MakeDownloadClient();
                await OcrLanguages.DownloadTrainedDataAsync(http, OcrLanguages.LanguageDataUrl(code, OcrHighQuality), dest,
                    string.Format(Loc("Str_Busy_Downloading"), name), Loc("Str_Busy_CancelHint"),
                    msg => SetBusyMessage(busy, msg), ct);
                OcrLanguages.MarkLanguageHq(code, OcrHighQuality);

                var sel = GetSelectedOcrLanguages();
                if (!sel.Contains(code)) { sel.Add(code); SetSelectedOcrLanguages(sel); }
                HideBusyOverlay(busy);
                SetStatus(string.Format(Loc("Str_St_LangInstalled"), name, string.Join("+", GetSelectedOcrLanguages())));
            }
            catch (OperationCanceledException)
            {
                HideBusyOverlay(busy);
                OcrLanguages.TryDeleteFile(dest + ".part");
                if (ct.IsCancellationRequested) SetStatus(string.Format(Loc("Str_St_LangCanceled"), name));
                else KillerDialog.Show(this, string.Format(Loc("Str_Dlg_DownloadTimeout"), name),
                    "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch (Exception ex)
            {
                HideBusyOverlay(busy);
                OcrLanguages.TryDeleteFile(dest + ".part");
                KillerDialog.Show(this, string.Format(Loc("Str_Err_LangDataFailed"), name) + "\n" + ex.Message, "KillerPDF",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                EndCancellableOp();
            }
        }

        // Re-downloads every currently-selected language in high quality (tessdata_best), replacing the
        // standard copies. Triggered when the user enables "Use High Quality Models". Cancellable; a single
        // language's failure is reported but doesn't abort the rest, and a failed file never replaces a
        // working one (temp+move).
        private async void RedownloadSelectedHighQuality()
        {
            // Only UPGRADE languages that are actually installed and not already HQ. A language the user has
            // selected but hasn't downloaded yet (e.g. the default English right after clearing data) must NOT
            // be auto-downloaded here - that would surprise the user with no prompt. It is fetched on the first
            // OCR instead, via EnsureOcrModelsReadyAsync, which shows the heads-up dialog and honors this HQ pref.
            var hq = OcrLanguages.GetHqLanguages();
            var toDownload = new List<string>();
            foreach (var c in GetSelectedOcrLanguages())
                if (OcrLanguages.IsTesseractLanguageInstalled(c) && !hq.Contains(c)) toDownload.Add(c);

            if (toDownload.Count == 0)
            {
                bool anyInstalled = false;
                foreach (var c in GetSelectedOcrLanguages()) if (OcrLanguages.IsTesseractLanguageInstalled(c)) { anyInstalled = true; break; }
                SetStatus(anyInstalled
                    ? Loc("Str_St_HqAlready")
                    : Loc("Str_St_HqNextTime"));
                return;
            }

            var ct = BeginCancellableOp(Loc("Str_Op_LangDownload"));
            var busy = ShowBusyOverlay(Loc("Str_Busy_HqModels"));
            string tessDir = OcrNativeBootstrap.EnsureLanguageData();
            var failed = new List<string>();
            try
            {
                using var http = OcrLanguages.MakeDownloadClient();
                int i = 0;
                foreach (var code in toDownload)
                {
                    if (ct.IsCancellationRequested) break;
                    i++;
                    string name = OcrLanguages.NameForCode(code);
                    string dest = Path.Combine(tessDir, code + ".traineddata");
                    string url = $"https://raw.githubusercontent.com/tesseract-ocr/tessdata_best/main/{code}.traineddata";
                    try
                    {
                        await OcrLanguages.DownloadTrainedDataAsync(http, url, dest,
                            string.Format(Loc("Str_Busy_DownloadingHq"), name, i, toDownload.Count), Loc("Str_Busy_CancelHint"),
                            msg => SetBusyMessage(busy, msg), ct);
                        OcrLanguages.MarkLanguageHq(code, true);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                    catch { failed.Add(name); OcrLanguages.TryDeleteFile(dest + ".part"); }
                }
                HideBusyOverlay(busy);
                if (ct.IsCancellationRequested) SetStatus(Loc("Str_St_HqDownloadCanceled"));
                else if (failed.Count > 0) SetStatus(string.Format(Loc("Str_St_HqFailed"), string.Join(", ", failed)));
                else SetStatus(string.Format(Loc("Str_St_HqInstalled"), string.Join("+", toDownload)));
            }
            catch (Exception ex)
            {
                HideBusyOverlay(busy);
                KillerDialog.Show(this, Loc("Str_Err_HqDownloadFailed") + "\n" + ex.Message, "KillerPDF",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                EndCancellableOp();
            }
        }

        // Ensures the language models OCR is about to use are present on disk. Nothing is bundled, so on the
        // first OCR (or after the user adds a new language) the model is downloaded here, behind a heads-up
        // dialog. Returns true only when every required model is installed and OCR may proceed.
        private async Task<bool> EnsureOcrModelsReadyAsync()
        {
            // Desired languages from the persisted setting (default English), regardless of install state.
            var desired = new List<string>(
                (App.GetSetting("OcrLanguages") ?? "eng").Split(['+'], StringSplitOptions.RemoveEmptyEntries));
            if (desired.Count == 0) desired.Add("eng");

            var missing = OcrLanguages.MissingLanguagesForOcr(desired).ToList();
            if (missing.Count == 0) return true;

            string names = string.Join(", ", missing.ConvertAll(OcrLanguages.NameForCode));
            var choice = KillerDialog.Show(this,
                string.Format(Loc("Str_Dlg_LangDownloadAsk"), names),
                "KillerPDF", MessageBoxButton.OKCancel, MessageBoxImage.Information);
            if (choice != MessageBoxResult.OK) return false;

            var ct = BeginCancellableOp(Loc("Str_Op_LangDownload"));
            var busy = ShowBusyOverlay(Loc("Str_Busy_Model"));
            try
            {
                string tessDir = OcrNativeBootstrap.EnsureLanguageData();
                using var http = OcrLanguages.MakeDownloadClient();
                for (int i = 0; i < missing.Count; i++)
                {
                    string code = missing[i];
                    string name = OcrLanguages.NameForCode(code);
                    string dest = Path.Combine(tessDir, code + ".traineddata");
                    await OcrLanguages.DownloadTrainedDataAsync(http, OcrLanguages.LanguageDataUrl(code, OcrHighQuality), dest,
                        missing.Count == 1 ? string.Format(Loc("Str_Busy_Downloading"), name)
                                           : string.Format(Loc("Str_Busy_DownloadingMany"), name, i + 1, missing.Count),
                        Loc("Str_Busy_CancelHint"),
                        msg => SetBusyMessage(busy, msg), ct);
                    OcrLanguages.MarkLanguageHq(code, OcrHighQuality);
                    if (ct.IsCancellationRequested) return false;
                }
                return OcrLanguages.MissingLanguagesForOcr(desired).Count == 0;
            }
            catch (OperationCanceledException)
            {
                SetStatus(Loc("Str_St_LangDownloadCanceled"));
                return false;
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, Loc("Str_Err_ModelDownloadFailed") + "\n" + ex.Message,
                    "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
            finally
            {
                HideBusyOverlay(busy);
                EndCancellableOp();
            }
        }

        // ============================================================
        // Document OCR operations - the logic lives in Features/Ocr/OcrController.cs. These
        // one-line forwarders keep every existing call site (toolbar, context menu, keyboard
        // shortcuts, Annotations' region-drag) unchanged, and the IOcrHost implementation below
        // is everything the controller needs from the window.
        // ============================================================

        private OcrController? _ocrController;
        private OcrController Ocr => _ocrController ??= new OcrController(this);

        private void OcrPageToClipboard(int pageIdx) => Ocr.OcrPageToClipboard(pageIdx);
        private void OcrSelectedPagesToClipboard()
        {
            int[] pages = [.. PageList.SelectedItems.Cast<PageThumbnailVm>()
                .Select(page => page.PageIndex).Distinct().OrderBy(page => page)];
            if (pages.Length == 0) pages = [PageList.SelectedIndex];
            Ocr.OcrPagesToClipboard(pages);
        }
        private void OcrRegion(int pageIdx, Rect canvasBounds) => Ocr.OcrRegion(pageIdx, canvasBounds);
        private void MakeSearchablePdf() => Ocr.MakeSearchablePdf();
        private void ExtractAllText() => Ocr.ExtractAllText();

        // ---- IOcrHost ------------------------------------------------------------------------
        // (IShellServices - Window, Loc, SetStatus - is implemented once for the class in
        // Shell/About.cs.)

        bool IOcrHost.HasDocument => _doc is not null && _currentFile is not null;
        int IOcrHost.PageCount => _doc?.PageCount ?? 0;
        string? IOcrHost.CurrentFile => _currentFile;
        string? IOcrHost.OriginalFile => _originalFile;
        int IOcrHost.RotationFor(int pageIdx) => _pageRotations.TryGetValue(pageIdx, out var r) ? r : 0;

        bool IOcrHost.TryGetRenderDims(int pageIdx, out int w, out int h)
        {
            if (_renderDims.TryGetValue(pageIdx, out var rd)) { w = rd.w; h = rd.h; return true; }
            w = 0; h = 0; return false;
        }

        string IOcrHost.OcrLanguageString => CurrentOcrLanguageString();
        bool IOcrHost.FormAwareOcr => OcrFormAware;
        Task<bool> IOcrHost.EnsureOcrModelsReadyAsync() => EnsureOcrModelsReadyAsync();
        void IOcrHost.CommitActiveTextBox() => CommitActiveTextBox();
        void IOcrHost.SaveDocumentTo(string path) => _doc!.Save(path);

        // The busy overlay Border stays a shell detail: the host tracks the one live overlay so
        // the controller can speak in intents (BeginOp / SetBusyMessage / HideBusy / EndOp). Only
        // one cancellable op runs at a time (single _busyCts), so a single field is faithful.
        private Border? _ocrBusy;

        CancellationToken IOcrHost.BeginOp(string label, string busyMessage)
        {
            var ct = BeginCancellableOp(label);
            _ocrBusy = ShowBusyOverlay(busyMessage);
            return ct;
        }

        void IOcrHost.SetBusyMessage(string message)
        {
            if (_ocrBusy is not null) SetBusyMessage(_ocrBusy, message);
        }

        void IOcrHost.HideBusy()
        {
            if (_ocrBusy is null) return;
            HideBusyOverlay(_ocrBusy);
            _ocrBusy = null;
        }

        void IOcrHost.EndOp() => EndCancellableOp();

        // OCR Region: armed by the menu item; the next box-drag (Select tool) crops that area of the page
        // bitmap and OCRs only it to the clipboard. Works on scans that have no text layer to extract from.
        private bool _ocrRegionMode { get => ActiveViewer.OcrRegionModeRef; set => ActiveViewer.OcrRegionModeRef = value; }

        private void BeginOcrRegion()
        {
            if (_doc is null || _currentFile is null) { KillerDialog.Show(this, Loc("Str_Msg_OpenFirst")); return; }
            SetTool(EditTool.Select);
            _ocrRegionMode = true;
            SetStatus(Loc("Str_St_OcrDragBox"));
        }

        // Primary OCR toolbar button: OCR every selected page and combine the text on the clipboard.
        private void Ocr_Click(object sender, RoutedEventArgs e) => OcrSelectedPagesToClipboard();

        // Caret dropdown next to the OCR button - same split-button pattern as Save/Open. Page OCR is live;
        // the remaining entries are stubs until their commands land (Region, Searchable PDF, Extract Text).
        private void OcrMenu_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;   // also fired by right-click on the OCR button; don't let it bubble
            var menu = MakeThemedMenu();
            if (_doc is null)
            {
                menu.Items.Add(new MenuItem { Header = Loc("Str_Ocr_NoDoc"), IsEnabled = false });
            }
            else
            {
                int pageIdx = PageList.SelectedIndex;
                menu.Items.Add(MakeMenuItem(Loc("Str_Ctx_OcrPage"), (_, _) => OcrPageToClipboard(pageIdx), "Ctrl+Shift+O", ""));
                menu.Items.Add(MakeMenuItem(Loc("Str_Ocr_Region"), (_, _) => BeginOcrRegion(), "Ctrl+Shift+I", ""));
                menu.Items.Add(new Separator());
                menu.Items.Add(MakeMenuItem(Loc("Str_Ocr_SearchablePdf"), (_, _) => MakeSearchablePdf(), null, ""));
                menu.Items.Add(MakeMenuItem(Loc("Str_Ocr_ExtractText"), (_, _) => ExtractAllText(), null, ""));
                menu.Items.Add(new Separator());
                menu.Items.Add(BuildLanguageMenu());
                var formAware = new MenuItem
                {
                    Header = Loc("Str_Ocr_FormAware"),
                    IsCheckable = true,
                    IsChecked = OcrFormAware,
                    StaysOpenOnClick = true
                };
                formAware.Click += (_, _) =>
                {
                    bool enabled = !OcrFormAware;
                    App.SetSetting("OcrFormAware", enabled ? "1" : "0");
                    formAware.IsChecked = enabled;
                };
                menu.Items.Add(formAware);
            }
            menu.PlacementTarget = (UIElement)sender;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }

        // Placeholder for OCR commands that are designed but not yet built; keeps the menu complete.
        private void OcrComingSoon(string name) => SetStatus(string.Format(Loc("Str_Ocr_ComingSoon"), name));

        // Updates the busy overlay's message line (its TextBlock) for per-page progress. UI thread only.
        private static void SetBusyMessage(Border overlay, string msg)
        {
            if (overlay.Child is StackPanel sp)
                foreach (var c in sp.Children)
                    if (c is TextBlock tb) { tb.Text = msg; return; }
        }
    }
}
