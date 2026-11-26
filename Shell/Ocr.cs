using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KillerPDF.Features;
using KillerPDF.Services;
using KillerFamily.Accessibility;

namespace KillerPDF.Shell
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
        private OperationHold? _busyHold;   // set by a page job that waits while the cancel prompt is open
        private Task? _busyCancelPrompt;    // completes when the open cancel prompt is answered
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

        private void EndCancellableOp(CancellationToken expectedToken)
        {
            if (_busyCts?.Token != expectedToken) return;
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

        // The interface locale whose flag a Tesseract language wears (eng is en-US, chi_tra is zh-TW),
        // or null for a language with no interface translation.
        private static Locale? OcrFlagLocale(string code)
        {
            foreach (var (tag, ocr) in OcrLanguages.LocaleToOcrCode)
                if (ocr == code) return LocaleManager.LocaleForCulture(new System.Globalization.CultureInfo(tag));
            return null;
        }

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
                Icon = new Glyph
                {
                    Text = "",
                    FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            // The list copies the kit's locale picker (Controls/LanguageMenu.cs, Steve 2026-11-26): two
            // columns of flag, name and code rows in one panel, so all 22 languages fit a short window.
            // A row that is not installed yet shows only its name: clicking it downloads the language,
            // which needs no "(download)" label, and the label made the columns far wider (Steve 2026-11-26).

            // Closes the whole OCR card, for a row that starts a download.
            void CloseCard()
            {
                root.IsSubmenuOpen = false;
                for (DependencyObject? d = root; d != null; d = LogicalTreeHelper.GetParent(d) ?? (d is Visual ? VisualTreeHelper.GetParent(d) : null))
                    if (d is ContextMenu card) { card.IsOpen = false; return; }
            }

            // Margins as in the locale picker: each hover row 8px from the card's outer edge. One grid holds
            // both columns and, under a rule, the two settings side by side, one under each column. The two
            // columns share one width (at least the locale picker's 226) and grow to the longest
            // row, so no name is trimmed (Steve 2026-11-25).
            var columns = new Grid();
            Grid.SetIsSharedSizeScope(columns, true);
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "OcrCol", MinWidth = 226 });
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "OcrCol", MinWidth = 226 });
            columns.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            columns.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            columns.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var left = new StackPanel();
            var right = new StackPanel();
            var divider = new Border
            {
                Width = 1,
                Margin = new Thickness(6, 8, 16, 8),
                Opacity = 0.18,
                IsHitTestVisible = false,
                OpacityMask = new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0), EndPoint = new Point(0, 1),
                    GradientStops =
                    {
                        new GradientStop(Colors.Transparent, 0),
                        new GradientStop(Colors.Black, 0.15),
                        new GradientStop(Colors.Black, 0.85),
                        new GradientStop(Colors.Transparent, 1),
                    },
                },
            };
            divider.SetResourceReference(Border.BackgroundProperty, "TextBrush");
            Grid.SetColumn(divider, 1);
            Grid.SetColumn(right, 2);
            columns.Children.Add(left);
            columns.Children.Add(divider);
            columns.Children.Add(right);

            var checkStyle = (Style)FindResource("ThemeCheck");
            var catalog = OcrLanguages.OcrLanguageCatalog;
            int half = (catalog.Length + 1) / 2, index = 0;
            foreach (var (code, name) in catalog)
            {
                bool installed = OcrLanguages.IsLanguageInstalled(code);
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                if (OcrFlagLocale(code) is Locale flagLocale)
                {
                    var flag = Controls.LanguageFlags.Badge(flagLocale);
                    flag.Margin = new Thickness(0, 0, 8, 0);
                    grid.Children.Add(flag);
                }
                var label = new TextBlock
                {
                    Text = name,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(label, 1);
                grid.Children.Add(label);
                var codeBlock = new TextBlock
                {
                    Text = code,
                    Margin = new Thickness(12, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                codeBlock.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
                Grid.SetColumn(codeBlock, 2);
                grid.Children.Add(codeBlock);

                var row = new CheckBox { Content = grid, Style = checkStyle, IsChecked = installed && selected.Contains(code) };
                if (installed)
                {
                    row.Click += (_, _) =>
                    {
                        var sel = GetSelectedOcrLanguages();
                        if (row.IsChecked == true) { if (!sel.Contains(code)) sel.Add(code); }
                        else
                        {
                            if (sel.Count <= 1) { row.IsChecked = true; return; }   // keep at least one selected
                            sel.Remove(code);
                        }
                        SetSelectedOcrLanguages(sel);
                        SetStatus(string.Format(Loc("Str_St_OcrLanguage"), string.Join("+", sel)));
                    };
                }
                else
                {
                    row.Click += (_, _) => { row.IsChecked = false; CloseCard(); DownloadOcrLanguage(code, name); };
                }
                (index++ < half ? left : right).Children.Add(row);
            }

            // The settings sit in one row under both columns, past a faded rule across the whole card.
            var rule = new Border
            {
                Height = 1,
                // From the left hover box's edge to where the right column's rows end, as the old rule did.
                Margin = new Thickness(-8, 8, 2, 8),
                Opacity = 0.18,
                IsHitTestVisible = false,
            };
            rule.SetResourceReference(Border.BackgroundProperty, "TextBrush");
            Grid.SetRow(rule, 1);
            Grid.SetColumnSpan(rule, 3);
            columns.Children.Add(rule);
            // High-quality toggle. Enabling it upgrades the languages already selected and makes future
            // downloads pull the "best" models too. It flips the persisted preference directly so the
            // setting can't drift from the visual state.
            var hq = new CheckBox { Content = Loc("Str_Ocr_HighQuality"), Style = checkStyle, IsChecked = hqPref };
            hq.Click += (_, _) =>
            {
                bool now = !OcrHighQuality;
                SetOcrHighQuality(now);
                hq.IsChecked = now;
                if (now) RedownloadSelectedHighQuality();
            };
            Grid.SetRow(hq, 2);
            columns.Children.Add(hq);
            // Form-aware OCR, a setting like High quality: it was a seventh tile on the OCR card, which left a half row.
            var formAware = new CheckBox { Content = Loc("Str_Ocr_FormAware"), Style = checkStyle, IsChecked = OcrFormAware };
            formAware.Click += (_, _) => App.SetSetting("OcrFormAware", formAware.IsChecked == true ? "1" : "0");
            Grid.SetRow(formAware, 2);
            Grid.SetColumn(formAware, 2);
            columns.Children.Add(formAware);

            // The rows' 8px gap is for between rows; under the last one it would double the bottom edge.
            foreach (var last in new FrameworkElement?[] { left.Children.Count > 0 ? left.Children[^1] as FrameworkElement : null,
                         right.Children.Count > 0 ? right.Children[^1] as FrameworkElement : null, hq, formAware })
                if (last is not null)
                    last.Margin = new Thickness(last.Margin.Left, last.Margin.Top, last.Margin.Right, 0);

            // PanelMenuItem is the kit's gutter-free container for a custom panel in a menu. The outer margin
            // is the locale picker's 8px edge (1px border, 15 left less the row's -8, 7 top and bottom), less
            // the 4px the submenu's own ItemsPresenter already adds on every side.
            root.Items.Add(new MenuItem
            {
                Header = new Border { Margin = new Thickness(11, 3, 1, 3), Child = columns },
                StaysOpenOnClick = true,
                Style = (Style)FindResource("PanelMenuItem"),
            });
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
                EndCancellableOp(ct);
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
                    string url = OcrLanguages.LanguageDataUrl(code, highQuality: true);
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
                EndCancellableOp(ct);
            }
        }

        // Ensures the language models OCR is about to use are present on disk. Nothing is bundled, so on the
        // first OCR (or after the user adds a new language) the model is downloaded here, behind a heads-up
        // dialog. Returns true only when every required model is installed and OCR may proceed.
        private async Task<bool> EnsureOcrModelsReadyAsync(Window? owner = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Desired languages from the persisted setting (default English), regardless of install state.
            var desired = new List<string>(
                (App.GetSetting("OcrLanguages") ?? "eng").Split(['+'], StringSplitOptions.RemoveEmptyEntries));
            if (desired.Count == 0) desired.Add("eng");

            var missing = OcrLanguages.MissingLanguagesForOcr(desired).ToList();
            if (missing.Count == 0) return true;

            string names = string.Join(", ", missing.ConvertAll(OcrLanguages.NameForCode));
            var choice = KillerDialog.Show(owner ?? this,
                string.Format(Loc("Str_Dlg_LangDownloadAsk"), names),
                "KillerPDF", MessageBoxButton.OKCancel, MessageBoxImage.Information);
            if (choice != MessageBoxResult.OK) return false;

            var ct = BeginCancellableOp(Loc("Str_Op_LangDownload"));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, cancellationToken);
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
                        msg => SetBusyMessage(busy, msg), linked.Token);
                    OcrLanguages.MarkLanguageHq(code, OcrHighQuality);
                    if (linked.IsCancellationRequested) return false;
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
                KillerDialog.Show(owner ?? this, Loc("Str_Err_ModelDownloadFailed") + "\n" + ex.Message,
                    "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
            finally
            {
                HideBusyOverlay(busy);
                EndCancellableOp(ct);
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
        private void ConvertScanToText() => Ocr.ConvertScanToText();
        private void ExtractAllText() => Ocr.ExtractAllText();

        // ---- IOcrHost ------------------------------------------------------------------------
        // (IShellServices - Window, Loc, SetStatus - is implemented once for the class in
        // Shell/About.cs.)

        bool IOcrHost.HasDocument => _doc is not null && _currentFile is not null;
        int IOcrHost.PageCount => _doc?.PageCount ?? 0;
        string? IOcrHost.CurrentFile => _currentFile;
        OpenedProtection IOcrHost.CurrentProtection => _protection;
        string? IOcrHost.OriginalFile => _originalFile;
        int IOcrHost.RotationFor(int pageIdx) => _pageRotations.TryGetValue(pageIdx, out var r) ? r : 0;

        bool IOcrHost.TryGetRenderDims(int pageIdx, out int w, out int h)
        {
            if (_renderDims.TryGetValue(pageIdx, out var rd)) { w = rd.w; h = rd.h; return true; }
            w = 0; h = 0; return false;
        }

        string IOcrHost.OcrLanguageString => CurrentOcrLanguageString();
        bool IOcrHost.FormAwareOcr => OcrFormAware;
        Func<bool> IOcrHost.CaptureDocumentOrigin() => CaptureDocumentOrigin();
        Task<bool> IOcrHost.EnsureOcrModelsReadyAsync() => EnsureOcrModelsReadyAsync();
        void IOcrHost.CommitActiveTextBox() => CommitActiveTextBox();
        bool IOcrHost.EnsureDocumentMayWrite(PdfOutput output) => EnsureMayWrite(output);

        int IOcrHost.SaveDocumentTo(string path)
        {
            // The snapshot feeds recognition, and the delivered file is built from it, so its skipped-picture count
            // goes back to the caller for the success status.
            SavedCopy copy = WriteSavedCopyToTemp(forRasterization: true, throwaway: true);
            try { System.IO.File.Copy(copy.Path, path, overwrite: true); }
            finally { System.IO.File.Delete(copy.Path); }
            return copy.SkippedPictures;
        }

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

        void IOcrHost.EndOp(CancellationToken token) => EndCancellableOp(token);

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
                string Short(string key) => Loc(key).TrimEnd('.', '\u2026');
                // Each command keeps the icon its row had. Make Searchable PDF had the Region magnifier, so it now
                // shows a document; its old glyph is kept in the comment below because it cannot be edited out.
                string regionGlyph = ((""));
                // Make Searchable PDF's old row glyph:""));
                string scanGlyph = ((""));
                string textGlyph = ((""));
                // The card frame and tiles of the Save dropdown: every command a tile, its shortcut on the tooltip. Two
                // full rows of three with Language last, its chevron on the right; Form-aware OCR is a setting in
                // Language's list beside High quality, so the grid has no half row and no separator.
                if (TryFindResource("CardMenu") is Style card) menu.Style = card;
                var tiles = QuickStrip(menu, 3,
                    new CardTile("", Loc("Str_Ctx_OcrPage"), "CtrlShift:O", () => OcrPageToClipboard(pageIdx), false, true),
                    new CardTile(regionGlyph, Short("Str_Ocr_Region"), "CtrlShift:I", BeginOcrRegion, false, true),
                    new CardTile(GlyphText(0xE8A5), Short("Str_Ocr_SearchablePdf"), null, MakeSearchablePdf, false, true),
                    new CardTile(scanGlyph, Short("Str_Ocr_ScanToText"), "CtrlShift:R", ConvertScanToText, false, true),
                    new CardTile(textGlyph, Short("Str_Ocr_ExtractText"), null, ExtractAllText, false, true),
                    FlyoutTile(GlyphText(0xF2B7), Loc("Str_Ocr_Language"), null, BuildLanguageMenu()));
                menu.Items.Add(CardSlot(tiles, new Thickness(0)));
                FocusFirstActionFromKeyboard(menu, tiles);
            }
            menu.PlacementTarget = (UIElement)sender;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }

        // Updates the busy overlay's message line (its TextBlock) for per-page progress. UI thread only.
        private static void SetBusyMessage(Border overlay, string msg)
        {
            if (overlay.Child is StackPanel sp)
                foreach (var c in sp.Children)
                    if (c is TextBlock tb) { tb.Text = msg; return; }
        }
    }
}
