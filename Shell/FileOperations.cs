using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Docnet.Core;
using Docnet.Core.Models;
using Microsoft.Win32;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using KillerPDF.Services;
// CliParsePageRange moved out with the CLI runner but is used here too (Export Images'
// range box); its encoder sibling is now Services/BitmapHelpers.EncodeJpeg.
using static KillerPDF.Features.CliRunner;
using PdfPigDoc = UglyToad.PdfPig.PdfDocument;

namespace KillerPDF
{
    public partial class MainWindow
    {
        // True while an open is finishing on a background thread (encryption strip / repair). The
        // synchronous open callers check this so they don't treat the not-yet-loaded _doc as a failure;
        // the background path finalizes the tab itself via FinalizeAsyncOpen.
        private bool _asyncOpenPending;

        // True when the open document came from a password/encryption-protected source file (#149).
        // Drives the Save menu's Remove Password entry and the saved-without-protection status note.
        // Cleared once a save writes the unprotected file; carried per tab via DocumentSession.
        private bool _openedFromProtected;

        private void OpenFile(string path)
        {
            // Record real user files in the recent list (skips blank/new docs, which don't open a path).
            if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path)) App.AddRecentFile(path);

            // Files on UNC / network shares - notably the WSL \\wsl$ 9P filesystem - can hand
            // back partial reads, making the PDF parser see a truncated file ("Unexpected EOF").
            // Copy such files to a local temp via File.ReadAllBytes (which reads to EOF) and open
            // from there. `path` stays the user's real path for display and Save.
            string srcPath = path;
            if (PdfImport.IsNetworkPath(path))
            {
                try
                {
                    var localCopy = App.MakeTempFile("netopen");
                    File.WriteAllBytes(localCopy, File.ReadAllBytes(path));
                    srcPath = localCopy;
                }
                catch { srcPath = path; }
            }

            try
            {
                if (_doc is not null) { _doc.Close(); _doc = null; }
                _doc = PdfReader.Open(srcPath, PdfDocumentOpenMode.Modify);
                // PdfSharp cannot save modified encrypted PDFs - it copies unmodified encrypted
                // stream bytes verbatim but fails when it has to re-serialize a dirty object.
                // Strip encryption silently at open time via Import so all edits work correctly.
                if (PdfImport.PdfFileHasEncryption(srcPath))
                {
                    // PdfSharp can read encrypted PDFs but cannot re-save them once modified, so the
                    // encryption is stripped (PDFium, lossless; Import fallback). That strip is CPU-heavy,
                    // so it runs off-thread behind the busy overlay instead of freezing the window. The
                    // background path finalizes the tab itself, so the flag tells the synchronous caller
                    // not to treat the not-yet-set _doc as a failed open.
                    _asyncOpenPending = true;
                    StripEncryptionAndOpen(srcPath, path, busyMessage: "Opening protected PDF...");
                    return;
                }
                _currentFile = srcPath;
                FinishOpenFile(path, srcPath);
            }
            catch (Exception ex) when (PdfImport.IsOwnerPasswordException(ex))
            {
                // PDF has owner/permissions restrictions but no open password -
                // open read-only so the user can still view and print it.
                try
                {
                    if (_doc is not null) { _doc.Close(); _doc = null; }
                    _doc = PdfReader.Open(srcPath, PdfDocumentOpenMode.ReadOnly);
                    _currentFile = srcPath;
                    FinishOpenFile(path, srcPath);
                    SetStatus(string.Format(Loc("Str_OpenedReadOnly"), System.IO.Path.GetFileName(path), _doc.PageCount));
                }
                catch (Exception ex2)
                {
                    KillerDialog.Show(this, string.Format(Loc("Str_Dlg_FailedOpen"), ex2.Message), Loc("Str_Dlg_AppTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex) when (PdfImport.IsPasswordException(ex))
            {
                string? pw = PromptForPassword(path);
                if (pw is null) return;
                try
                {
                    if (_doc is not null) { _doc.Close(); _doc = null; }
                    _doc = PdfReader.Open(srcPath, pw, PdfDocumentOpenMode.Modify);
                    // Save a decrypted temp copy so Docnet can render without needing the password
                    var tempDec = App.MakeTempFile("dec");
                    _doc.Save(tempDec);
                    _doc.Close();
                    _doc = PdfReader.Open(tempDec, PdfDocumentOpenMode.Modify);
                    _currentFile = tempDec;
                    FinishOpenFile(path, tempDec);
                    _openedFromProtected = true;   // #149: unlocked with the user's password
                }
                catch (Exception ex2)
                {
                    KillerDialog.Show(this, string.Format(Loc("Str_Dlg_FailedOpen"), ex2.Message), Loc("Str_Dlg_AppTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex) when (PdfImport.IsXRefException(ex))
            {
                // Some PDFs have malformed or non-standard XRef tables that PdfSharp can't
                // open in Modify mode. Fall back to ReadOnly; if that also fails, offer repair.
                try
                {
                    if (_doc is not null) { _doc.Close(); _doc = null; }
                    _doc = PdfReader.Open(srcPath, PdfDocumentOpenMode.ReadOnly);
                    _currentFile = srcPath;
                    FinishOpenFile(path, srcPath);
                    SetStatus(string.Format(Loc("Str_OpenedReadOnlyXRef"), System.IO.Path.GetFileName(path), _doc.PageCount));
                    KillerDialog.Show(this,
                        $"\"{System.IO.Path.GetFileName(path)}\" has a non-standard structure and was opened read-only.\n\nEditing, saving, and some other features may not work correctly.",
                        "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                catch
                {
                    // ReadOnly also failed - offer to repair.
                    var result = KillerDialog.Show(this,
                        $"This PDF has a damaged structure and couldn't be opened.\n\nWould you like KillerPDF to attempt a repair? A repaired copy will be created - the original file will not be changed.\n\nNote: repaired files may be missing bookmarks, forms, and other interactive features.",
                        "KillerPDF", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (result == MessageBoxResult.Yes)
                        TryRepairAndOpen(srcPath);
                }
            }
            catch (Exception ex) when (PdfImport.IsEofParseException(ex))
            {
                // PdfSharpCore rejects some structurally-valid PDFs with "Unexpected EOF" even though
                // PDFium (and every common viewer) reads them fine. Re-save losslessly through PDFium on
                // a background thread (so the window doesn't freeze). The recovered copy is content-
                // equivalent, so it opens clean without nagging to save (markDirty: false).
                _asyncOpenPending = true;
                StripEncryptionAndOpen(srcPath, path, markDirty: false);
            }
            catch (Exception)
            {
                // Any other open failure (truncated file, malformed objects, an out-of-range parse, etc.):
                // we can't classify the damage, but the PDFium-based repair often recovers it anyway, so
                // offer the repair rather than just failing outright.
                var result = KillerDialog.Show(this,
                    "This PDF couldn't be opened - its structure may be damaged.\n\nWould you like KillerPDF to attempt a repair? A repaired copy will be created - the original file will not be changed.\n\nNote: repaired files may be missing bookmarks, forms, and other interactive features.",
                    "KillerPDF", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (result == MessageBoxResult.Yes)
                    TryRepairAndOpen(srcPath);   // sets _asyncOpenPending and finalizes the tab itself
            }
        }

        // The open-failure classifiers (IsEofParseException, IsXRefException, IsNetworkPath,
        // IsOwnerPasswordException, IsPasswordException) live in Services/PdfImport.cs.

        private void FinishOpenFile(string displayPath, string workingPath)
        {
            _currentFile = workingPath;
            _originalFile = displayPath;
            FileNameLabel.Text = System.IO.Path.GetFileName(displayPath);
            _annotations.Clear();
            _continuousLinks.Clear();   // drop the previous document's cached link rects
            CloseLinkPdfiumDoc();       // and release the cached PDFium link handle for the old file
            _undoStack.Clear();
            _redoStack.Clear();
            _navBack.Clear();
            _navForward.Clear();
            _renderDims.Clear();
            _formTextValues.Clear();
            _formCheckValues.Clear();
            _formFontSizes.Clear();
            _formRadioValues.Clear();
            Search.ClearPageResults();
            _gridScrollToPage = -1;
            MarkDirty(false);
            _openedFromProtected = false;   // #149: set true by the two protected-open paths after this returns
            // Restore this file's last fit/zoom/view/page if we've seen it before; otherwise open at the
            // per-view-mode default. Set the fields first, then let BootstrapDocumentView apply them.
            if (TryGetDocState(displayPath, out var sfit, out var szoom, out var sview, out var spage))
            {
                _viewMode  = sview;
                _fitMode   = sfit;
                _zoomLevel = szoom;
                int pg = Math.Max(0, Math.Min(spage, _doc!.PageCount - 1));
                BootstrapDocumentView(pg, autoFit: false, restoreFitMode: true);
            }
            else
            {
                BootstrapDocumentView(0, autoFit: true);
            }
            SetStatus(string.Format(Loc("Str_Opened"), System.IO.Path.GetFileName(displayPath), _doc!.PageCount));
            SyncSidebarToDocState(hasDoc: true, startup: false);   // a document is up: open the rail, show page controls
        }

        // Themed "Password Required" prompt (KillerDialog): family dialog chrome + themed PasswordBox.
        private string? PromptForPassword(string filename) => KillerDialog.PromptPassword(this, filename);

        // ALL direct PDFium P/Invoke lives in Services/PdfiumInterop.cs (KillerUI refactor) -
        // one class, one lock (Docnet's), so the thread-safety discipline stays auditable.
        // PdfFileHasEncryption and TryImportRepairToPath live in Services/PdfImport.cs.

        private async void TryRepairAndOpen(string path)
        {
            // Repair is CPU/IO heavy, so it runs on a background thread behind a spinner overlay -
            // otherwise the window froze (hourglass, no feedback) for the whole repair. Only the
            // file production runs off-thread; opening/rendering the result stays on the UI thread.
            _asyncOpenPending = true;   // the synchronous open caller defers tab finalization to here
            var ct = BeginCancellableOp("repair");
            var busy = ShowBusyOverlay("Repairing PDF...");
            try
            {
                // Release any open document before the worker reads the source file.
                if (_doc is not null) { _doc.Close(); _doc = null; }

                string? repairedPath = null;
                bool raster = false;

                // Strategy 0 (#103): lossless PDFium re-save. PDFium's tolerant parser recovers
                // broken xref tables (including the dangling /Outlines entry older KillerPDF
                // builds wrote) and rewrites a clean file preserving EVERYTHING - forms,
                // bookmarks, text. The import-copy below drops the document-level AcroForm,
                // which is what used to turn a repaired fillable form into a flat one.
                repairedPath = await System.Threading.Tasks.Task.Run(() =>
                {
                    var p = App.MakeTempFile("repaired");
                    return PdfiumInterop.TryPdfiumStripEncryption(path, p) ? p : null;
                });
                if (ct.IsCancellationRequested) { HideBusyOverlay(busy); _asyncOpenPending = false; SetStatus(Loc("Str_St_RepairCancelled")); return; }   // cancelled during strategy 0

                // Strategy 1: PdfSharpCore Import mode - page-copy, more lenient than Modify/ReadOnly.
                // Works when the XRef is partially corrupt but the object data is intact. (Returns
                // null on failure rather than throwing.)
                repairedPath ??= await System.Threading.Tasks.Task.Run(() => PdfImport.RepairViaImportToFile(path));
                if (ct.IsCancellationRequested) { HideBusyOverlay(busy); _asyncOpenPending = false; SetStatus(Loc("Str_St_RepairCancelled")); return; }   // cancelled during strategy 1

                // Strategy 2: PDFium rasterize. PDFium's internal XRef recovery handles damage
                // PdfSharpCore cannot; each page is rendered to a bitmap and rebuilt into a clean PDF.
                // Text won't be selectable in the result, but the file will open and print.
                if (repairedPath is null)
                {
                    repairedPath = await System.Threading.Tasks.Task.Run(() => PdfImport.RepairViaDocnetRasterizeToFile(path));
                    raster = repairedPath is not null;
                }
                if (ct.IsCancellationRequested) { HideBusyOverlay(busy); _asyncOpenPending = false; SetStatus(Loc("Str_St_RepairCancelled")); return; }   // cancelled during strategy 2

                if (repairedPath is null)
                {
                    HideBusyOverlay(busy);
                    _asyncOpenPending = false;
                    KillerDialog.Show(this,
                        "Repair failed - the file is too severely damaged to recover.\n\nTry opening the original in a different application (Adobe Acrobat, browsers) which may have additional recovery options.",
                        "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // Open and render the repaired copy on the UI thread.
                _doc = PdfReader.Open(repairedPath, PdfDocumentOpenMode.Modify);
                _currentFile = repairedPath;
                FinishOpenFile(path, repairedPath);
                MarkDirty(true); // repaired copy lives in temp - user must Save As
                SetStatus(string.Format(Loc(raster ? "Str_OpenedRasterRepair" : "Str_OpenedRepaired"),
                                        System.IO.Path.GetFileName(path), _doc.PageCount));
                HideBusyOverlay(busy);
                FinalizeAsyncOpen();
                KillerDialog.Show(this,
                    raster
                        ? $"\"{System.IO.Path.GetFileName(path)}\" was repaired by rasterizing through PDFium.\n\nText is not selectable in the repaired copy. Use Save As to write it to a new location."
                        : $"\"{System.IO.Path.GetFileName(path)}\" was repaired successfully.\n\nBookmarks, forms, and other interactive features may have been lost. Use Save As to write the repaired file to a new location.",
                    "KillerPDF", MessageBoxButton.OK, MessageBoxImage.None);
            }
            catch (Exception ex)
            {
                HideBusyOverlay(busy);
                _asyncOpenPending = false;
                KillerDialog.Show(this, $"Repair failed:\n{ex.Message}", "KillerPDF",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                EndCancellableOp();
            }
        }

        // Strips a PDF's encryption on a background thread (so the window doesn't freeze), then opens the
        // clean copy. Mirrors TryRepairAndOpen; finalizes the tab via FinalizeAsyncOpen.
        private async void StripEncryptionAndOpen(string srcPath, string displayPath, bool markDirty = true, string busyMessage = "Opening PDF...")
        {
            _asyncOpenPending = true;
            var ct = BeginCancellableOp("operation");
            var busy = ShowBusyOverlay(busyMessage);
            try
            {
                if (_doc is not null) { _doc.Close(); _doc = null; }
                var repairedPath = App.MakeTempFile("repaired");
                bool ok = await System.Threading.Tasks.Task.Run(() =>
                    PdfiumInterop.TryPdfiumStripEncryption(srcPath, repairedPath) || PdfImport.TryImportRepairToPath(srcPath, repairedPath));
                if (ct.IsCancellationRequested) { HideBusyOverlay(busy); _asyncOpenPending = false; SetStatus(Loc("Str_St_Cancelled")); EndCancellableOp(); return; }
                if (!ok)
                {
                    HideBusyOverlay(busy);
                    TryRepairAndOpen(srcPath);   // re-registers the cancellable op; repair finalizes the tab
                    return;
                }
                _doc = PdfReader.Open(repairedPath, PdfDocumentOpenMode.Modify);
                _currentFile = repairedPath;
                FinishOpenFile(displayPath, repairedPath);
                _openedFromProtected = true;   // #149: source carried encryption, silently stripped above
                if (markDirty) MarkDirty(true);   // stripped copy lives in temp - user must Save As to keep it
                HideBusyOverlay(busy);
                FinalizeAsyncOpen();
                EndCancellableOp();
            }
            catch (Exception ex)
            {
                HideBusyOverlay(busy);
                _asyncOpenPending = false;
                KillerDialog.Show(this, $"Could not open the protected PDF:\n{ex.Message}",
                    "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
                EndCancellableOp();
            }
        }

        // Finalizes a background open (encryption strip / repair) once the document is loaded on the UI
        // thread: stores it into the active tab session and refreshes the tool + tab strip. Mirrors the
        // tail of OpenInNewTab, which is skipped while _asyncOpenPending is set.
        private void FinalizeAsyncOpen()
        {
            _asyncOpenPending = false;
            if (_active != null) CaptureSessionState(_active);
            SetTool(_currentTool);
            RebuildTabStrip();
        }

        // The background-safe repair strategy workers (RepairViaImportToFile,
        // RepairViaDocnetRasterizeToFile) live in Services/PdfImport.cs.

        // ============================================================
        // Close file (Ctrl+W) - returns to drop-zone state
        // ============================================================

        private void CloseFile()
        {
            if (_doc is null) return;
            if (_isDirty)
            {
                var res = KillerDialog.Show(this,
                    Loc("Str_Dlg_UnsavedClose"),
                    "KillerPDF", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (res != MessageBoxResult.Yes) return;
            }
            _doc.Close();
            _doc = null;
            _currentFile = null;
            CloseLinkPdfiumDoc();   // release the cached PDFium link handle for the closed file
            App.RemoveSetting("LastFile");   // don't reopen a manually-closed file on next launch (Issue #75)
            _activeTextBox = null;   // cancel any in-progress typewriter edit before canvas clear
            RemoveTextEditHandles();
            _annotations.Clear();
            _undoStack.Clear();
            _redoStack.Clear();
            _navBack.Clear();
            _navForward.Clear();
            _renderDims.Clear();
            _formTextValues.Clear();
            _formCheckValues.Clear();
            _formFontSizes.Clear();
            _formRadioValues.Clear();
            Search.ClearPageResults();
            _thumbCts?.Cancel();
            PageList.ItemsSource = null;
            PageImage.Source = null;
            _annotationCanvas.Children.Clear();
            MarqueeLayer.Children.Clear();   // the layer is window-level; drop any orphaned marquee boxes (#121)
            FileNameLabel.Text = "";
            DropZone.Visibility = Visibility.Visible;
            PopulateRecentFilesList();   // refresh the empty-state recent list
            PagePreviewPanel.Visibility = Visibility.Collapsed;
            CloseSearchBar();
            HideDrawSettings();
            HideTextSettings();
            HideSignaturePopup();
            SetTool(EditTool.Select);
            if (_closeFileBtnRef != null) _closeFileBtnRef.IsEnabled = false;
            _pageJumpBox.IsEnabled = false;
            _continuousRenderCts?.Cancel();
            _continuousPanel.Children.Clear();
            _continuousTops.Clear();
            _pageJumpBox.Text = "";
            _pageTotalLabel.Text = "/ -";
            OutlineTree.Items.Clear();
            SidebarOutlinesTab.IsEnabled = false;
            if (_sidebarShowingOutlines) SwitchSidebarToPagesTab();
            MarkDirty(false);
            SetStatus(Loc("Str_Ready"));
        }

        private void CloseFile_Click(object sender, RoutedEventArgs e) => CloseTab(_active);

        // ============================================================
        // File toolbar handlers
        // ============================================================

        private void New_Click(object sender, RoutedEventArgs e) => NewDocument();

        private void NewDocument()
        {
            // A new blank document opens in its own tab; other open tabs keep their state, so
            // there's no need to prompt about unsaved changes here.
            var target = BeginTabLoad(out var prev, out bool createdNew);
            try
            {
                var newDoc = new PdfDocument();
                newDoc.AddPage(); // one blank A4 page

                var tempPath = App.MakeTempFile("new");
                newDoc.Save(tempPath);
                newDoc.Close();

                _doc = PdfReader.Open(tempPath, PdfDocumentOpenMode.Modify);
                FinishOpenFile("Untitled.pdf", tempPath);
                SetStatus(Loc("Str_KS_NewBlank"));
                CaptureSessionState(_active!);
                SetTool(_currentTool);   // sync the tool UI to this (new) tab's tool
                RebuildTabStrip();
            }
            catch (Exception ex)
            {
                AbortTabLoad(target, prev, createdNew);
                KillerDialog.Show(this, $"Could not create new document:\n{ex.Message}",
                    "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Open_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Controls.FileDialog(Controls.FileDialogMode.Open)
                          { Filter = "PDF files|*.pdf", Title = "Open PDF" };
            if (dlg.ShowDialog(this) == true) OpenInNewTab(dlg.FileName);
        }

        // Dropdown next to the Open button: the recent-files list.
        private void OpenRecent_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;   // also fired by right-click on the Open button; don't let it bubble
            var menu = new ContextMenu();
            TextOptions.SetTextFormattingMode(menu, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(menu, TextRenderingMode.Grayscale);

            menu.Items.Add(MakeMenuItem(Loc("Str_Menu_Import") + "...", (s2, e2) => ImportImages_Click(s2, e2), null, ""));
            menu.Items.Add(new Separator());

            var recents = App.GetRecentFiles();
            if (recents.Count == 0)
            {
                menu.Items.Add(new MenuItem { Header = Loc("Str_Menu_RecentNone"), IsEnabled = false });
            }
            else
            {
                foreach (var p in recents)
                {
                    string path = p;   // capture
                    var item = MakeMenuItem(System.IO.Path.GetFileName(path), (_, _) =>
                    {
                        if (System.IO.File.Exists(path)) OpenInNewTab(path);
                        else KillerDialog.Show(this, $"File not found:\n{path}", "KillerPDF",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                    });
                    item.ToolTip = path;

                    // Header = filename then a small X right after it (kept tight - no right whitespace).
                    var rmBtn = new Button
                    {
                        Content = "",
                        FontFamily = UiKit.IconFont,
                        FontSize = 11,
                        Width = 18, Height = 18,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(0),
                        // No local Foreground - it would override the DangerCloseButton hover trigger.
                        Background = Brushes.Transparent,
                        BorderThickness = new Thickness(0),
                        Cursor = Cursors.Hand,
                        Padding = new Thickness(0),
                        Style = (Style)FindResource("DangerCloseButton"),
                        ToolTip = "Remove from list"
                    };
                    rmBtn.Click += (_, ev) =>
                    {
                        ev.Handled = true;
                        App.RemoveRecentFile(path);
                        menu.Items.Remove(item);   // drop just this row in place - no rebuild, no blink
                        if (!menu.Items.OfType<MenuItem>().Any(mi => mi.Header is Grid))
                            menu.IsOpen = false;   // nothing left to show
                    };
                    // Filename (fills) + X right-aligned. Trim the MenuItem's default 40px right padding
                    // so the X sits near the edge instead of floating in whitespace.
                    // Negative right margin overlaps the template's empty InputGestureText column
                    // (it reserves ~24px), so the X lands near the real right edge instead of floating.
                    // Real file-type icon (left), filename (fills), X (right).
                    var fileIcon = new Image
                    {
                        Source              = ShellIcons.GetShellIcon(path),
                        Width               = 18,
                        Height              = 18,
                        VerticalAlignment   = VerticalAlignment.Center,
                        Margin              = new Thickness(0, 0, 8, 0),
                        Stretch             = Stretch.Uniform,
                        SnapsToDevicePixels = true
                    };
                    RenderOptions.SetBitmapScalingMode(fileIcon, BitmapScalingMode.HighQuality);
                    fileIcon.Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = System.Windows.Media.Colors.Black, BlurRadius = 4, ShadowDepth = 2, Direction = 270, Opacity = TryFindResource("IconShadowOpacity") is double so2 ? so2 : 0.5 };
                    var hdr = new Grid { Width = 348, Margin = new Thickness(0, 0, 0, 0) };   // no negative right margin - it pushed the remove X past the menu's right edge, clipping it out of frame
                    hdr.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                    // icon
                    hdr.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // name
                    hdr.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                    // remove X
                    var nameText = new TextBlock { Text = System.IO.Path.GetFileName(path), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
                    Grid.SetColumn(fileIcon, 0);
                    Grid.SetColumn(nameText, 1);
                    Grid.SetColumn(rmBtn, 2);
                    hdr.Children.Add(fileIcon);
                    hdr.Children.Add(nameText);
                    hdr.Children.Add(rmBtn);
                    item.Header = hdr;
                    item.Padding = new Thickness(20, 6, 8, 6);

                    menu.Items.Add(item);
                }
                menu.Items.Add(new Separator());
                menu.Items.Add(MakeMenuItem(Loc("Str_Menu_ClearList"),
                    (_, _) => { App.ClearRecentFiles(); PopulateRecentFilesList(); }));   // keep the start screen in sync (#146)
            }

            menu.PlacementTarget = (UIElement)sender;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }

        // Start-screen "Clear list" link (#146): one click, then the box hides itself (empty list).
        // Handled = true, or the click bubbles into the surrounding DropZone and opens the file dialog.
        // internal: PdfViewer's XAML binds this and forwards to it.
        internal void RecentClearAll_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            e.Handled = true;
            App.ClearRecentFiles();
            PopulateRecentFilesList();
        }

        // The file-type icon lookup (per-extension cache + SHGetFileInfo) lives in
        // Services/ShellIcons.cs - the same shape the KillerUI file picker uses.

        // Fills the empty-state "Recent" list with clickable filenames (hidden when there are none).
        // The start screen belongs to a PANE, so a pane filling its own list has to name itself: the
        // bare RecentFilesBox / RecentFilesList members resolve through ActiveViewer, which meant an
        // unfocused pane's empty state stayed blank while the focused pane's list was rebuilt.
        private void PopulateRecentFilesList(Controls.PdfViewer? pane = null)
        {
            if (pane == null)
            {
                // No pane named means a window-level change to the recents themselves, which both
                // start screens show - refresh both, or the unfocused pane keeps a stale list.
                PopulateRecentFilesList(Viewer);
                PopulateRecentFilesList(ViewerB);
                return;
            }
            var box  = pane.RecentBox;
            var list = pane.RecentList;
            if (list is null || box is null) return;
            list.Items.Clear();
            var recents = App.GetRecentFiles();
            if (recents.Count == 0) { box.Visibility = Visibility.Collapsed; return; }
            // Visibility and width belong to SyncRecentBoxWidth, called once the rows are in - it
            // also has to drop the panel on a narrow pane, which this has no way of knowing.
            var fam = UiKit.UiFont;
            foreach (var p in recents)
            {
                string path = p;   // capture
                bool exists = System.IO.File.Exists(path);
                string dir = System.IO.Path.GetDirectoryName(path) ?? "";
                string dateStr = exists
                    ? $"{System.IO.File.GetLastWriteTime(path):MMM d, yyyy}"
                    : "missing";

                var name = new TextBlock
                {
                    Text         = System.IO.Path.GetFileName(path),
                    FontFamily   = fam,
                    FontSize     = 13,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                // DynamicResource so the color tracks theme switches (FindResource would freeze
                // whatever theme was active when the list was built).
                name.SetResourceReference(TextBlock.ForegroundProperty, exists ? "TextBrush" : "DimTextBrush");

                // File path line (slightly brighter) sits above the date line (slightly dimmer).
                var pathTb = new TextBlock
                {
                    Text         = dir,
                    FontFamily   = fam,
                    FontSize     = 11,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin       = new Thickness(0, 2, 0, 0)
                };
                pathTb.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");

                var dateTb = new TextBlock
                {
                    Text         = dateStr,
                    FontFamily   = fam,
                    FontSize     = 11,
                    Opacity      = 0.6,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin       = new Thickness(0, 1, 0, 0)
                };
                dateTb.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");

                var stack = new StackPanel();
                stack.Children.Add(name);
                stack.Children.Add(pathTb);
                stack.Children.Add(dateTb);

                // Per-row remove button: a small X that fades in on hover and drops just this
                // entry from the recents list (it does not touch the file on disk).
                var delIcon = new TextBlock
                {
                    Text              = "",   // close (X) glyph below set via code
                    FontFamily        = UiKit.IconFont,
                    FontSize          = 11,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                delIcon.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
                delIcon.Text = "";   // Segoe MDL2 ChromeClose (X)
                var del = new Border
                {
                    Width             = 22,
                    Height            = 22,
                    Background        = System.Windows.Media.Brushes.Transparent,
                    CornerRadius      = new CornerRadius(4),
                    Cursor            = Cursors.Hand,
                    VerticalAlignment = VerticalAlignment.Center,
                    Opacity           = 0,   // hidden until the row is hovered
                    Child             = delIcon,
                    ToolTip           = Loc("Str_Menu_RemoveFromRecents")
                };
                del.MouseEnter += (_, _) => { delIcon.SetResourceReference(TextBlock.ForegroundProperty, "DangerRed"); delIcon.Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = System.Windows.Media.Colors.Black, BlurRadius = 4, ShadowDepth = 1, Direction = 270, Opacity = 0.5 }; };
                del.MouseLeave += (_, _) => { delIcon.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush"); delIcon.Effect = null; };
                del.MouseLeftButtonDown += (_, ev) =>
                {
                    ev.Handled = true;   // don't open the file
                    App.RemoveRecentFile(path);
                    PopulateRecentFilesList();
                };

                // Real Windows file-type icon for this extension (left of the text).
                var icon = new Image
                {
                    Source              = ShellIcons.GetShellIcon(path),
                    Width               = 32,
                    Height              = 32,
                    VerticalAlignment   = VerticalAlignment.Center,
                    Margin              = new Thickness(0, 0, 10, 0),
                    Stretch             = Stretch.Uniform,
                    Opacity             = exists ? 1.0 : 0.45,   // dim missing files' icons, matching the text
                    SnapsToDevicePixels = true
                };
                RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
                icon.Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = System.Windows.Media.Colors.Black, BlurRadius = 4, ShadowDepth = 2, Direction = 270, Opacity = TryFindResource("IconShadowOpacity") is double so ? so : 0.5 };
                stack.VerticalAlignment = VerticalAlignment.Center;

                var rowGrid = new Grid();
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                    // icon
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // text
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                    // remove X
                Grid.SetColumn(icon, 0);
                Grid.SetColumn(stack, 1);
                Grid.SetColumn(del, 2);
                rowGrid.Children.Add(icon);
                rowGrid.Children.Add(stack);
                rowGrid.Children.Add(del);

                var row = new Border
                {
                    Background    = System.Windows.Media.Brushes.Transparent,
                    CornerRadius  = new CornerRadius(4),
                    Padding       = new Thickness(8, 6, 8, 6),
                    Margin        = new Thickness(0, 1, 0, 1),
                    Cursor        = Cursors.Hand,
                    Child         = rowGrid,
                    ToolTip       = path
                };
                row.MouseEnter += (_, _) => { row.Background = (SolidColorBrush)FindResource("RowHoverBrush"); del.Opacity = 1; };
                row.MouseLeave += (_, _) => { row.Background = System.Windows.Media.Brushes.Transparent; del.Opacity = 0; };
                row.MouseLeftButtonDown += (_, ev) =>
                {
                    ev.Handled = true;   // don't bubble to the DropZone "click to browse" handler
                    // Name the pane rather than trusting focus to have followed the click: these
                    // rows belong to a specific pane's start screen, and OpenInNewTab routes
                    // through ActiveViewer. Clicking pane B's recents opened the file in pane A.
                    FocusPane(pane);
                    if (System.IO.File.Exists(path)) OpenInNewTab(path);
                    else KillerDialog.Show(this, $"File not found:\n{path}", "KillerPDF",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                };
                list.Items.Add(row);
            }
            pane.SyncRecentBoxWidth();
        }

        // Dropdown next to the Save button: explicit Save / Save As.
        private void SaveMenu_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;   // also fired by right-click on the Save button; don't let it bubble
            var menu = new ContextMenu();
            TextOptions.SetTextFormattingMode(menu, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(menu, TextRenderingMode.Grayscale);

            if (_doc is null)
            {
                menu.Items.Add(new MenuItem { Header = Loc("Str_Menu_SaveNothing"), IsEnabled = false });
            }
            else
            {
                menu.Items.Add(MakeMenuItem(Loc("Str_Menu_Save"), (_, _) => SaveInPlace(), "Ctrl+S", ""));
                menu.Items.Add(MakeMenuItem(Loc("Str_Menu_SaveAs"), (s2, e2) => SaveAs_Click(s2, e2), "Ctrl+Shift+S", ""));
                menu.Items.Add(MakeMenuItem(Loc("Str_Menu_CompressZip"), (s2, e2) => CompressToZip_Click(s2, e2), null, ""));
                menu.Items.Add(MakeMenuItem(Loc("Str_Menu_ExportImages"), (s2, e2) => ExportImages_Click(s2, e2), null, ""));   // #132
                menu.Items.Add(new Separator());
                menu.Items.Add(MakeMenuItem(Loc("Str_Lbl_DigitalSig"), (_, _) => OpenSignDialog(), null, ""));
                // #149: visible always (discoverability, the PDF Viewer Plus way), enabled only when the
                // open file actually had a password/encryption. Saving in place IS the removal - the
                // working doc is already decrypted - and the SaveInPlace tail reports it and drops the flag.
                var removePw = MakeMenuItem(Loc("Str_Menu_RemovePassword"), (_, _) => SaveInPlace(), null, "");
                removePw.IsEnabled = _openedFromProtected;
                menu.Items.Add(removePw);
            }

            menu.PlacementTarget = (UIElement)sender;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }

        private void DocInfo_Click(object sender, RoutedEventArgs e) => OpenDocumentInfo();

        // Opens the Document Info dialog; edits are applied to the live doc and persist on the next save.
        private void OpenDocumentInfo()
        {
            if (_doc is null) { KillerDialog.Show(this, Loc("Str_Msg_OpenFirst")); return; }
            CommitActiveTextBox();
            var dlg = new DocumentInfoDialog(this, _doc, _originalFile ?? _currentFile);
            dlg.ShowDialog();   // fade-close dialogs don't reliably return true; rely on the Saved flag
            if (dlg.Saved)
            {
                MarkDirty();
                SetStatus(Loc("Str_St_DocInfoUpdated"));
            }
        }

        private void Merge_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null) { KillerDialog.Show(this, Loc("Str_Msg_OpenFirst")); return; }
            var doc = _doc;
            var dlg = new Controls.FileDialog(Controls.FileDialogMode.Open)
                          { Filter = "PDF files|*.pdf", Title = "Select PDF to merge", Multiselect = true };
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                foreach (var file in dlg.FileNames)
                {
                    int pageOffset = doc.PageCount;

                    // Open twice: Import mode for AddPage, ReadOnly for catalog access.
                    using var srcRead = PdfReader.Open(file, PdfDocumentOpenMode.ReadOnly);
                    var namedDestMap = PdfImport.BuildNamedDestMap(srcRead);

                    using var src = PdfReader.Open(file, PdfDocumentOpenMode.Import);
                    for (int i = 0; i < src.PageCount; i++)
                        doc.AddPage(src.Pages[i]);

                    // Rewrite named-destination links in the newly added pages so they
                    // resolve correctly after the catalog is not imported.
                    if (namedDestMap.Count > 0)
                        PdfImport.RewriteNamedDestLinks(doc, pageOffset, namedDestMap);
                }
                SaveTempAndReload();
                SetStatus($"Merged {dlg.FileNames.Length} file(s) - {_doc?.PageCount} total pages");
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, $"Merge failed:\n{ex.Message}", "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // The named-destination helpers (BuildNamedDestMap, RewriteNamedDestLinks and their
        // private walkers) live in Services/PdfImport.cs (KillerUI refactor).

        // DerefItemStatic, RectNum and the pre-save scrubs live in Services/PdfScrub.cs
        // (KillerUI refactor) - pure functions shared by the GUI saves, TempReload and the CLI.

        // ============================================================
        // Adobe page-size guard
        // ============================================================

        // Adobe Reader only displays pages whose sides are 3-14400 points; anything outside
        // that range shows "The dimensions of this page are out-of-range" and renders blank.
        // Such pages usually come from images with broken DPI metadata turned into a PDF (by
        // KillerPDF 1.6.1 and earlier, or by other tools). PDFium renders any size, so the
        // file looks normal in KillerPDF and only fails in Adobe.
        // Min/MaxAdobePageDim live in Services/PdfImport.cs (shared with the image importer).

        private static bool PageOutOfAdobeRange(PdfPage p)
        {
            double w = p.Width.Point, h = p.Height.Point;
            return w < PdfImport.MinAdobePageDim || w > PdfImport.MaxAdobePageDim || h < PdfImport.MinAdobePageDim || h > PdfImport.MaxAdobePageDim;
        }

        // Called at the top of every user-facing save. If any page is outside Adobe's supported
        // range, offers a proportional rescale (content, page boxes, and annotations all scale
        // by the same factor, so pages look identical at their new size). Declining saves as-is.
        private void OfferRescaleOutOfRangePages()
        {
            if (_doc is null) return;
            int bad = 0;
            for (int i = 0; i < _doc.PageCount; i++)
                if (PageOutOfAdobeRange(_doc.Pages[i])) bad++;
            if (bad == 0) return;
            var res = KillerDialog.Show(this, string.Format(Loc("Str_Dlg_PageOutOfRange"), bad),
                "KillerPDF", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (res != MessageBoxResult.Yes) return;
            for (int i = 0; i < _doc.PageCount; i++)
                if (PageOutOfAdobeRange(_doc.Pages[i]))
                    RescalePageToAdobeRange(_doc.Pages[i]);
        }

        // Rescales one page into Adobe's supported range: wraps the existing content in a
        // "q <s> 0 0 <s> 0 0 cm ... Q" transform and scales the page boxes and annotation
        // rectangles by the same factor.
        private static void RescalePageToAdobeRange(PdfPage page)
        {
            double w = page.Width.Point, h = page.Height.Point;
            double s = 1.0;
            if (w > PdfImport.MaxAdobePageDim || h > PdfImport.MaxAdobePageDim)
                s = Math.Min(PdfImport.MaxAdobePageDim / w, PdfImport.MaxAdobePageDim / h);
            else if (w < PdfImport.MinAdobePageDim || h < PdfImport.MinAdobePageDim)
                s = Math.Max(PdfImport.MinAdobePageDim / w, PdfImport.MinAdobePageDim / h);
            if (s == 1.0) return;

            string inv = s.ToString("0.########", System.Globalization.CultureInfo.InvariantCulture);
            page.Contents.PrependContent().CreateStream(
                System.Text.Encoding.ASCII.GetBytes($"q {inv} 0 0 {inv} 0 0 cm\n"));
            page.Contents.AppendContent().CreateStream(
                System.Text.Encoding.ASCII.GetBytes("\nQ\n"));

            ScaleRectValue(page.Elements, "/MediaBox", s);
            ScaleRectValue(page.Elements, "/CropBox",  s);
            ScaleRectValue(page.Elements, "/BleedBox", s);
            ScaleRectValue(page.Elements, "/TrimBox",  s);
            ScaleRectValue(page.Elements, "/ArtBox",   s);

            // Annotation rectangles (and link quad points) must follow so they stay on target.
            var annotsItem = page.Elements["/Annots"];
            if (annotsItem != null && PdfScrub.DerefItemStatic(annotsItem) is PdfArray annots)
            {
                foreach (var item in annots.Elements)
                {
                    if (PdfScrub.DerefItemStatic(item) is not PdfDictionary annot) continue;
                    ScaleRectValue(annot.Elements, "/Rect", s);
                    if (annot.Elements["/QuadPoints"] is PdfArray quads)
                        for (int i = 0; i < quads.Elements.Count; i++)
                            quads.Elements[i] = new PdfReal(PdfScrub.RectNum(quads.Elements[i]) * s);
                }
            }
        }

        // Multiplies a rectangle-valued dictionary entry by s in place; no-op when absent.
        // PdfSharpCore holds these as PdfRectangle (parsed) or PdfArray, so handle both.
        private static void ScaleRectValue(PdfDictionary.DictionaryElements elements, string key, double s)
        {
            var item = elements[key];
            if (item == null) return;
            item = PdfScrub.DerefItemStatic(item);
            if (item is PdfRectangle rect)
                elements.SetRectangle(key, new PdfRectangle(
                    new XPoint(rect.X1 * s, rect.Y1 * s),
                    new XPoint(rect.X2 * s, rect.Y2 * s)));
            else if (item is PdfArray arr && arr.Elements.Count == 4)
                for (int i = 0; i < 4; i++)
                    arr.Elements[i] = new PdfReal(PdfScrub.RectNum(arr.Elements[i]) * s);
        }

        private void SaveInPlace()
        {
            if (_doc is null) { KillerDialog.Show(this, Loc("Str_Msg_OpenFirst")); return; }
            // Save back to the user's real file. After a page edit (crop/rotate) _currentFile is a
            // temp working copy, so the real path is kept in _originalFile. If there is no real path
            // (e.g. a repaired temp-backed open), fall back to Save As.
            if (string.IsNullOrEmpty(_originalFile)) { SaveAs_Click(this, new RoutedEventArgs()); return; }
            CommitActiveTextBox();
            OfferRescaleOutOfRangePages();   // Adobe page-size guard
            PdfScrub.ScrubEmptyOutlines(_doc);        // #103: never write a dangling /Outlines reference
            PdfScrub.ScrubDegenerateCropBoxes(_doc);  // never write a zero-size /CropBox (Adobe out-of-range)
            PdfScrub.ScrubDeadSignatures(_doc);       // a rewrite voids signatures; never ship a dead one (PDF/A 6.4.3)
            string saveTarget = _originalFile!;
            // #129: the cached PDFium link handle (EnsureLinkPdfiumDoc) can hold _currentFile open,
            // and on a plain open _currentFile IS the user's real file - PdfSharp then can't overwrite
            // it (sharing violation, "being used by another process"). Release it before saving; it
            // reopens lazily on the next render sweep, re-parsing the freshly saved file.
            CloseLinkPdfiumDoc();
            try
            {
                bool hasAnnotations = _annotations.Values.Any(list => list.Count > 0);
                WriteFormValuesToDocument();
                // Always strip link annotation borders regardless of user annotation count
                // so mailto/URI links don't appear as strikethrough lines in other viewers.
                PdfScrub.StripLinkAnnotationBorders(_doc);

                if (hasAnnotations || HasActiveStamps)   // #147: stamps alone must still burn
                {
                    // Save a clean copy of the doc (without burned annotations), burn
                    // annotations into the real file, then restore the in-memory doc
                    // from the clean copy so future saves don't double-burn.
                    var tempClean = App.MakeTempFile("clean");
                    _doc.Save(tempClean);
                    DrawStampsOnDocument();
                    DrawAnnotationsOnDocument();
                    _doc.Save(saveTarget);
                    _doc.Close();
                    try
                    {
                        _doc = PdfReader.Open(tempClean, PdfDocumentOpenMode.Modify);
                    }
                    catch (Exception saveOpenEx) when (PdfImport.IsXRefException(saveOpenEx))
                    {
                        var fixedPath = App.MakeTempFile("savefixed");
                        if (!PdfImport.TryImportRepairToPath(tempClean, fixedPath)
                            && !PdfiumInterop.TryPdfiumSaveWithZeroRotations(tempClean, fixedPath))
                            throw;
                        tempClean = fixedPath;
                        _doc = PdfReader.Open(tempClean, PdfDocumentOpenMode.Modify);
                    }
                    _currentFile = tempClean;
                }
                else
                {
                    _doc.Save(saveTarget);
                }

                MarkDirty(false);
                if (_openedFromProtected)
                {
                    // #149: the file on disk no longer carries its password - say so instead of the
                    // plain saved message, and drop the flag (the source is unprotected from here on).
                    _openedFromProtected = false;
                    SetStatus(string.Format(Loc("Str_St_SavedNoPassword"), System.IO.Path.GetFileName(saveTarget)));
                }
                else
                    SetStatus($"Saved - {System.IO.Path.GetFileName(saveTarget)}");
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, $"Save failed:\n{ex.Message}", "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null) { KillerDialog.Show(this, Loc("Str_Msg_OpenFirst")); return; }
            // No real path yet (repaired temp-backed open) -> go straight to Save As.
            if (string.IsNullOrEmpty(_originalFile)) { SaveAs_Click(sender, e); return; }
            var name = System.IO.Path.GetFileName(_originalFile);
            // The same file can be open in BOTH panes as two independent copies (Steve's call,
            // 2026-08-01), each with its own annotations and undo stack. Overwriting from one pane
            // therefore discards whatever the other pane has done to it, with nothing on screen to
            // suggest that - so say so before it happens rather than after.
            if (OtherPaneHasDirtyCopyOf(_originalFile))
            {
                var warn = KillerDialog.Show(this,
                    string.Format(Loc("Str_Dlg_SaveOtherPaneDirty"), name),
                    Loc("Str_Dlg_AppTitle"), MessageBoxButton.OKCancel, MessageBoxImage.Warning,
                    defaultResult: MessageBoxResult.Cancel);
                if (warn != MessageBoxResult.OK) return;
            }
            var choice = KillerDialog.Show(this, $"Overwrite {name}?", "Save",
                                           MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (choice == MessageBoxResult.Yes)      SaveInPlace();
            else if (choice == MessageBoxResult.No)  SaveAs_Click(sender, e);
            // Cancel or closed: do nothing.
        }

        private void SaveAs_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null || _currentFile is null) { KillerDialog.Show(this, Loc("Str_Msg_OpenFirst")); return; }
            CommitActiveTextBox();
            var dlg = new Controls.FileDialog(Controls.FileDialogMode.Save)
                          { Filter = "PDF files|*.pdf", Title = "Save PDF as",
                            CheckFileExists = false, CheckPathExists = true };
            // Seed the dialog from the last real save location. Guard every path call: on .NET Framework
            // Path.GetDirectoryName("") throws ArgumentException ("path is not of a legal form"), so a merged
            // or imported doc (where _originalFile is null) would crash Save before the dialog opened (#112).
            string? seed = _originalFile ?? _currentFile;
            try
            {
                if (!string.IsNullOrWhiteSpace(seed))
                    dlg.FileName = System.IO.Path.GetFileName(seed);
                if (!string.IsNullOrWhiteSpace(_originalFile))
                {
                    var seedDir = System.IO.Path.GetDirectoryName(_originalFile);
                    if (!string.IsNullOrEmpty(seedDir) && System.IO.Directory.Exists(seedDir))
                        dlg.InitialDirectory = seedDir;
                }
            }
            catch { /* malformed seed path - just open the dialog with its defaults */ }
            if (dlg.ShowDialog(this) != true) return;
            CloseLinkPdfiumDoc();            // #129: the target may be the open file itself - release the cached PDFium handle
            OfferRescaleOutOfRangePages();   // Adobe page-size guard
            PdfScrub.ScrubEmptyOutlines(_doc);        // #103: never write a dangling /Outlines reference
            PdfScrub.ScrubDegenerateCropBoxes(_doc);  // never write a zero-size /CropBox (Adobe out-of-range)
            PdfScrub.ScrubDeadSignatures(_doc);       // a rewrite voids signatures; never ship a dead one (PDF/A 6.4.3)
            try
            {
                bool hasAnnotations = _annotations.Values.Any(list => list.Count > 0);
                WriteFormValuesToDocument();
                // Always strip link annotation borders regardless of user annotation count.
                PdfScrub.StripLinkAnnotationBorders(_doc);

                if (hasAnnotations || HasActiveStamps)   // #147: stamps alone must still burn
                {
                    var tempClean = App.MakeTempFile("clean");
                    _doc.Save(tempClean);
                    DrawStampsOnDocument();
                    DrawAnnotationsOnDocument();
                    _doc.Save(dlg.FileName);
                    _doc.Close();
                    try
                    {
                        _doc = PdfReader.Open(tempClean, PdfDocumentOpenMode.Modify);
                    }
                    catch (Exception saveOpenEx) when (PdfImport.IsXRefException(saveOpenEx))
                    {
                        var fixedPath = App.MakeTempFile("savefixed");
                        if (!PdfImport.TryImportRepairToPath(tempClean, fixedPath)
                            && !PdfiumInterop.TryPdfiumSaveWithZeroRotations(tempClean, fixedPath))
                            throw;
                        tempClean = fixedPath;
                        _doc = PdfReader.Open(tempClean, PdfDocumentOpenMode.Modify);
                    }
                    _currentFile = tempClean;
                    _originalFile = dlg.FileName;
                    FileNameLabel.Text = System.IO.Path.GetFileName(dlg.FileName);
                    MarkDirty(false);
                    if (_openedFromProtected)
                    {
                        // #149: the saved copy is now this tab's file, and it has no password.
                        _openedFromProtected = false;
                        SetStatus(string.Format(Loc("Str_St_SavedNoPassword"), System.IO.Path.GetFileName(dlg.FileName)));
                    }
                    else
                        SetStatus($"Saved with annotations to {System.IO.Path.GetFileName(dlg.FileName)}");
                }
                else
                {
                    _doc.Save(dlg.FileName);
                    _originalFile = dlg.FileName;
                    FileNameLabel.Text = System.IO.Path.GetFileName(dlg.FileName);
                    MarkDirty(false);
                    if (_openedFromProtected)
                    {
                        // #149: the saved copy is now this tab's file, and it has no password.
                        _openedFromProtected = false;
                        SetStatus(string.Format(Loc("Str_St_SavedNoPassword"), System.IO.Path.GetFileName(dlg.FileName)));
                    }
                    else
                        SetStatus($"Saved to {System.IO.Path.GetFileName(dlg.FileName)}");
                }
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, $"Save failed:\n{ex.Message}", "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void SaveFlattened_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null || _currentFile is null) { KillerDialog.Show(this, Loc("Str_Msg_OpenFirst")); return; }
            CommitActiveTextBox();
            var dlg = new Controls.FileDialog(Controls.FileDialogMode.Save)
                          { Filter = "PDF files|*.pdf", Title = "Save Flattened PDF",
                            CheckFileExists = false, CheckPathExists = true };
            if (dlg.ShowDialog(this) != true) return;
            CloseLinkPdfiumDoc();            // #129: the target may be the open file itself - release the cached PDFium handle
            OfferRescaleOutOfRangePages();   // Adobe page-size guard (pageDims below must be in range)
            PdfScrub.ScrubEmptyOutlines(_doc);        // #103: never write a dangling /Outlines reference
            PdfScrub.ScrubDegenerateCropBoxes(_doc);  // never write a zero-size /CropBox (Adobe out-of-range)
            PdfScrub.ScrubDeadSignatures(_doc);       // a rewrite voids signatures; never ship a dead one (PDF/A 6.4.3)

            // Burn any pending annotations into a temp source for rasterization
            // (must happen on UI thread before we go async)
            string sourcePath;
            bool hasAnnotations = _annotations.Values.Any(list => list.Count > 0);
            if (hasAnnotations || HasActiveStamps)   // #147: stamps alone must still burn
            {
                var tempClean  = App.MakeTempFile("clean");
                var tempBurned = App.MakeTempFile("burned");
                _doc.Save(tempClean);
                DrawStampsOnDocument();
                DrawAnnotationsOnDocument();
                _doc.Save(tempBurned);
                _doc.Close();
                try
                {
                    _doc = PdfReader.Open(tempClean, PdfDocumentOpenMode.Modify);
                }
                catch (Exception saveOpenEx) when (PdfImport.IsXRefException(saveOpenEx))
                {
                    var fixedPath = App.MakeTempFile("savefixed");
                    if (!PdfImport.TryImportRepairToPath(tempClean, fixedPath)
                        && !PdfiumInterop.TryPdfiumSaveWithZeroRotations(tempClean, fixedPath))
                        throw;
                    tempClean = fixedPath;
                    _doc = PdfReader.Open(tempClean, PdfDocumentOpenMode.Modify);
                }
                _currentFile = tempClean;
                sourcePath = tempBurned;
            }
            else
            {
                var temp = App.MakeTempFile("src");
                _doc.Save(temp);
                sourcePath = temp;
            }

            int pageCount = _doc.PageCount;

            // Snapshot per-page dimensions (CropBox-aware) before going off-thread
            var pageDims = new (double widthPt, double heightPt)[pageCount];
            for (int i = 0; i < pageCount; i++)
            {
                var p = _doc.Pages[i];
                pageDims[i] = (p.Width.Point, p.Height.Point);
            }

            // Show a progress overlay so the user knows we're working
            var overlay = ShowFlattenProgress(pageCount);
            string outputPath = dlg.FileName;

            try
            {
                var ct = BeginCancellableOp("flatten");
                // Rasterize on a background thread - keeps the UI responsive. The core lives in
                // Services/PdfRasterize.cs; progress marshals back to the overlay here.
                await Task.Run(() => PdfRasterize.FlattenToPdf(sourcePath, pageCount, pageDims, outputPath,
                    (n, total) => Dispatcher.BeginInvoke(new Action(() => UpdateFlattenProgress(overlay, n, total))),
                    ct));

                if (ct.IsCancellationRequested) { SetStatus(Loc("Str_St_FlattenCancelled")); return; }
                MarkDirty(false);
                SetStatus($"Flattened PDF saved to {System.IO.Path.GetFileName(outputPath)}");
            }
            catch (Exception ex)
            {
                try { KillerDialog.Show(this, $"Flatten failed:\n{ex.GetType().Name}: {ex.Message}", "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error); }
                catch { /* dialog failed; overlay still removed in finally */ }
            }
            finally
            {
                try { HideFlattenProgress(overlay); } catch { /* ensure overlay never leaks */ }
                EndCancellableOp();
            }
        }

        // ---- Export pages as images (#132) ----
        // The CLI --to-image pipeline behind a GUI entry (Save dropdown). Burns pending
        // annotations + stamps into a temp render source first (same clean-copy dance as Save
        // Flattened, so future saves don't double-burn), then renders each selected page at the
        // chosen DPI and writes <base>-page-NNN.<ext> beside the base name the user picked.
        private async void ExportImages_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null || _currentFile is null) { KillerDialog.Show(this, Loc("Str_Msg_OpenFirst")); return; }
            CommitActiveTextBox();

            var opts = new ExportImagesDialog(this);
            opts.ShowDialog();   // fade-close dialogs don't reliably return true; rely on Confirmed
            if (!opts.Confirmed) return;

            List<int>? selected = null;
            if (opts.Range.Length > 0)
            {
                selected = CliParsePageRange(opts.Range, _doc.PageCount, out string rangeErr);
                if (selected is null)
                {
                    KillerDialog.Show(this, rangeErr.Length > 0 ? rangeErr : Loc("Str_InvalidRange"),
                                      "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }
            selected ??= [.. Enumerable.Range(0, _doc.PageCount)];

            string ext = opts.Jpeg ? "jpg" : "png";
            var dlg = new Controls.FileDialog(Controls.FileDialogMode.Save)
            {
                Filter = opts.Jpeg ? "JPEG image|*.jpg" : "PNG image|*.png",
                Title  = Loc("Str_ExportImg_Suffix"),
                CheckFileExists = false, CheckPathExists = true,
                FileName = System.IO.Path.GetFileNameWithoutExtension(_originalFile ?? _currentFile) + "." + ext,
            };
            if (dlg.ShowDialog(this) != true) return;
            string outDir   = System.IO.Path.GetDirectoryName(dlg.FileName) is { Length: > 0 } d2 ? d2 : ".";
            string baseName = System.IO.Path.GetFileNameWithoutExtension(dlg.FileName);

            // Burn pending annotations + stamps into a temp render source (UI thread).
            string sourcePath;
            bool hasAnnotations = _annotations.Values.Any(list => list.Count > 0);
            if (hasAnnotations || HasActiveStamps)
            {
                var tempClean  = App.MakeTempFile("clean");
                var tempBurned = App.MakeTempFile("burned");
                _doc.Save(tempClean);
                DrawStampsOnDocument();
                DrawAnnotationsOnDocument();
                _doc.Save(tempBurned);
                _doc.Close();
                try
                {
                    _doc = PdfReader.Open(tempClean, PdfDocumentOpenMode.Modify);
                }
                catch (Exception saveOpenEx) when (PdfImport.IsXRefException(saveOpenEx))
                {
                    var fixedPath = App.MakeTempFile("savefixed");
                    if (!PdfImport.TryImportRepairToPath(tempClean, fixedPath)
                        && !PdfiumInterop.TryPdfiumSaveWithZeroRotations(tempClean, fixedPath))
                        throw;
                    tempClean = fixedPath;
                    _doc = PdfReader.Open(tempClean, PdfDocumentOpenMode.Modify);
                }
                _currentFile = tempClean;
                sourcePath = tempBurned;
            }
            else
            {
                var temp = App.MakeTempFile("src");
                _doc.Save(temp);
                sourcePath = temp;
            }

            // In-app rotations live outside the file (the working copy has /Rotate stripped -
            // BitmapHelpers), so snapshot them and rotate the pixels like the render path does.
            var rotSnapshot = new int[_doc.PageCount];
            for (int i = 0; i < rotSnapshot.Length; i++)
                if (_pageRotations.TryGetValue(i, out int r)) rotSnapshot[i] = r;

            var overlay = ShowFlattenProgress(selected.Count, "Exporting");
            int digits  = Math.Max(3, _doc.PageCount.ToString().Length);
            bool jpeg   = opts.Jpeg;
            double dpi  = opts.Dpi;
            var pages   = selected;
            try
            {
                var ct = BeginCancellableOp("export");
                // The per-page render/encode/write core lives in Services/PdfRasterize.cs;
                // progress marshals back to the overlay here.
                int written = await Task.Run(() => PdfRasterize.ExportPageImages(sourcePath, pages,
                    rotSnapshot, dpi, jpeg, outDir, baseName, digits,
                    (n, total) => Dispatcher.BeginInvoke(new Action(() => UpdateFlattenProgress(overlay, n, total))),
                    ct));
                SetStatus(string.Format(Loc("Str_ExportImg_Done"), written, outDir));
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, $"Export failed:\n{ex.Message}", "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                try { HideFlattenProgress(overlay); } catch { /* ensure overlay never leaks */ }
                EndCancellableOp();
            }
        }

        // ---- flatten progress overlay helpers ----

        private Border ShowFlattenProgress(int pageCount, string verb = "Flattening")
        {
            var progressText = new TextBlock
            {
                Text       = $"{verb} page 0 of {pageCount}...",
                Foreground = Brushes.White,
                FontSize   = 14,
                Tag        = verb   // stored so UpdateFlattenProgress can read it
            };
            var panel = new StackPanel
            {
                Orientation         = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center
            };
            panel.Children.Add(progressText);

            var overlay = new Border
            {
                Background        = new SolidColorBrush(Color.FromArgb(200, 0x1a, 0x1a, 0x1a)),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment   = VerticalAlignment.Stretch,
                Child             = panel,
                Tag               = "FlattenOverlay"
            };
            Panel.SetZIndex(overlay, 999);

            // Attach to the root grid
            if (Content is Grid rootGrid)
                rootGrid.Children.Add(overlay);

            return overlay;
        }

        private static void UpdateFlattenProgress(Border overlay, int current, int total)
        {
            if (overlay.Child is StackPanel panel)
                foreach (var child in panel.Children)
                    if (child is TextBlock tb && tb.Tag is string verb)
                        tb.Text = $"{verb} page {current} of {total}...";
        }

        private void HideFlattenProgress(Border overlay)
        {
            if (Content is Grid rootGrid)
                rootGrid.Children.Remove(overlay);
        }

        private void Print_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null || _currentFile is null) { KillerDialog.Show(this, Loc("Str_Msg_OpenFirst")); return; }
            CommitActiveTextBox();

            // The print prep (annotation burn + doc reopen) runs synchronously on the UI thread and
            // freezes it for a moment; yield one render cycle first to keep the click responsive.
            // (Deeper fix - backgrounding the burn - is tracked separately.)
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(RunPrintFlow));
        }

        private async void RunPrintFlow()
        {
            if (_doc is null || _currentFile is null) return;
            string srcFile = _currentFile;

            bool hasAnnotations = _annotations.Values.Any(list => list.Count > 0);
            string printPath;
            string? tempFlattened = null;
            if (hasAnnotations || _docStampSpec is not null)
            {
                var tempClean = App.MakeTempFile("clean");
                _doc.Save(tempClean);   // UI-thread snapshot of the current doc (just serialization)
                // Snapshot the data the burn needs so the background thread reads no live UI state.
                var annotsSnap = _annotations.ToDictionary(kv => kv.Key, kv => new List<PageAnnotation>(kv.Value));
                var dimsSnap   = new Dictionary<int, (int w, int h)>(_renderDims);
                var stampSnap  = _docStampSpec?.Clone();
                var rotSnap    = new Dictionary<int, int>(_pageRotations);   // #169: the burn needs the visual frame
                var burnPath   = App.MakeTempFile("print");

                // Flatten the annotations onto a throwaway COPY on a background thread. The live _doc is never
                // touched (no close/reopen), so the UI stays responsive and the editing session keeps its
                // overlay annotations. DrawAnnotationsIntoDoc is static, so it can't reach UI state.
                bool burned = await Task.Run(() =>
                {
                    try
                    {
                        PdfDocument burnDoc;
                        try { burnDoc = PdfReader.Open(tempClean, PdfDocumentOpenMode.Modify); }
                        catch (Exception ex) when (PdfImport.IsXRefException(ex))
                        {
                            // PdfSharpCore can write a snapshot its own reader then chokes on; repair via
                            // Import then PDFium, same as the save/undo paths.
                            var fixedPath = App.MakeTempFile("printfixed");
                            if (!PdfImport.TryImportRepairToPath(tempClean, fixedPath) && !PdfiumInterop.TryPdfiumSaveWithZeroRotations(tempClean, fixedPath))
                                return false;
                            burnDoc = PdfReader.Open(fixedPath, PdfDocumentOpenMode.Modify);
                        }
                        using (burnDoc)
                        {
                            PdfBurn.DrawStampsIntoDoc(burnDoc, stampSnap, null, rotSnap);   // stamps sit beneath annotations
                            PdfBurn.DrawAnnotationsIntoDoc(burnDoc, annotsSnap, dimsSnap, null, rotSnap);
                            burnDoc.Save(burnPath);
                        }
                        return true;
                    }
                    catch { return false; }
                });

                if (!burned) SetStatus(Loc("Str_St_FlattenPrintFailed"));
                printPath     = burned ? burnPath : srcFile;
                tempFlattened = burned ? burnPath : null;
            }
            else
            {
                printPath = srcFile;
            }

            if (_doc is null) return;   // re-check after the await (the doc was untouched, this satisfies flow analysis)
            int pageCount = _doc.PageCount;

            // Each page's true physical size in DIPs (96/inch) so the dialog can offer an exact
            // "actual size" / custom scale. Computed on the UI thread (PdfSharp isn't thread-safe).
            var pageDipW = new double[pageCount];
            var pageDipH = new double[pageCount];
            for (int i = 0; i < pageCount; i++)
            {
                double pw = _doc.Pages[i].Width.Point;
                double ph = _doc.Pages[i].Height.Point;
                if (_pageRotations.TryGetValue(i, out int rot) && (rot == 90 || rot == 270))
                    (pw, ph) = (ph, pw);
                pageDipW[i] = pw * 96.0 / 72.0;
                pageDipH[i] = ph * 96.0 / 72.0;
            }

            // Open the preview window immediately. Pages rasterize on a background thread and
            // stream in via SetRenderedPage, so the window appears at once and the app stays
            // responsive on large files. WPF's OS PrintDialog can't show a preview, so KillerPDF
            // renders it and drives printing itself.
            string  renderPath = printPath;
            string? cleanup    = tempFlattened;
            // Preview rasters are display-only (shown fit-to-pane in a Viewbox), so render them at a
            // modest budget that scales DOWN as the document grows - this keeps the preview's resident
            // bitmaps from ballooning on large files. The Print button re-renders the chosen pages at a
            // true 300 DPI on demand (PrintPreviewWindow.DoPrint), so output stays crisp (issue #83).
            int previewBox = pageCount <= 80 ? 1536 : pageCount <= 250 ? 1100 : 800;
            var preview = new PrintPreviewWindow(this, pageCount, pageDipW, pageDipH, renderPath, cleanup);

            _ = Task.Run(() =>
            {
                try
                {
                    using var docReader = DocLib.Instance.GetDocReader(renderPath, new PageDimensions(previewBox, previewBox));
                    for (int i = 0; i < pageCount; i++)
                    {
                        if (preview.Cancelled) return;
                        using var pr = docReader.GetPageReader(i);
                        int w = pr.GetPageWidth();
                        int h = pr.GetPageHeight();
                        byte[] png = BitmapHelpers.RenderToPng(pr.GetImage(PdfRender.WithAnnotations), w, h);   // #141
                        BitmapSource src;
                        using (var ms = new MemoryStream(png))
                            src = BitmapFrame.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                        src.Freeze();   // frozen so it can cross back to the UI thread
                        int ci = i;
                        try { preview.Dispatcher.Invoke(() => preview.SetRenderedPage(ci, src, w, h)); }
                        catch { return; }   // window closed mid-render
                    }
                    if (!preview.Cancelled)
                        try { preview.Dispatcher.Invoke(preview.FinishLoading); } catch { }
                }
                catch (Exception ex)
                {
                    try { preview.Dispatcher.Invoke(() => preview.LoadFailed(ex.Message)); } catch { }
                }
                // The flattened temp (cleanup) is NOT deleted here anymore: the Print button re-reads
                // renderPath to rasterize at 300 DPI, so the window owns the temp and deletes it on close.
            });

            try
            {
                if (preview.ShowDialog() == true)
                    SetStatus(string.Format(Loc("Str_Printed"), preview.PrintedPageCount));
            }
            catch (Exception ex)
            {
                try { KillerDialog.Show(this, $"Print failed:\n{ex.GetType().Name}: {ex.Message}", "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error); }
                catch { }
            }
        }
    }
}
