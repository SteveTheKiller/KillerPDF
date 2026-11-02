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
using Microsoft.Win32;
using KillerPDF.Services;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Writing;
// CliParsePageRange moved out with the CLI runner but is used here too (Export Images'
// range box); its encoder sibling is now Services/BitmapHelpers.EncodeJpeg.
using static KillerPDF.Features.CliRunner;
using KillerFamily.Accessibility;

namespace KillerPDF.Shell
{
    public partial class MainWindow
    {
        // True while an open is finishing on a background thread (encryption strip / repair). The
        // synchronous open callers check this so they don't treat the not-yet-loaded _doc as a failure;
        // the background path finalizes the tab itself via FinalizeAsyncOpen.
        private bool _asyncOpenPending { get => ActiveViewer.AsyncOpenPendingRef; set => ActiveViewer.AsyncOpenPendingRef = value; }

        // True when the open document came from a password/encryption-protected source file (#149).
        // Drives the Save menu's Remove Password entry and the saved-without-protection status note.
        // Cleared once a save writes the unprotected file; carried per tab via DocumentSession.
        private bool _openedFromProtected { get => ActiveViewer.OpenedFromProtectedRef; set => ActiveViewer.OpenedFromProtectedRef = value; }
        private bool _restrictedByCertificate { get => ActiveViewer.RestrictedByCertificateRef; set => ActiveViewer.RestrictedByCertificateRef = value; }
        private bool _restrictedFormFillable { get => ActiveViewer.RestrictedFormFillableRef; set => ActiveViewer.RestrictedFormFillableRef = value; }
        private Services.OpenedProtection _protection { get => ActiveViewer.ProtectionRef; set => ActiveViewer.ProtectionRef = value; }

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

            // Certificate-recipient encryption has no password to ask for: it opens with a personal
            // certificate whose private key belongs to one of the recipients.
            PdfEncryptionState encryption = PdfRestrictions.ReadEncryptionState(srcPath);
            if (encryption == PdfEncryptionState.Encrypted && PdfEngineIntegration.IsCertificateEncrypted(srcPath))
            {
                OpenCertificateProtected(srcPath, path);
                return;
            }

            try
            {
                _doc?.Close();
                // No tab's key opens it here: a file that needs a password asks for it, each tab its own.
                _doc = null; _doc = PdfWorkingDocument.Open(srcPath, null);
                // A file that could not be read a moment ago has opened, so it can say what it is now.
                if (encryption == PdfEncryptionState.Unreadable) encryption = PdfRestrictions.ReadEncryptionState(srcPath);
                // The working document is a decrypted copy. A save puts the document back under the
                // file's own encryption, so what is edited here never has to be.
                if (encryption == PdfEncryptionState.Encrypted)
                {
                    // That strip is CPU-heavy, so it runs off-thread behind the busy overlay instead of
                    // freezing the window. The background path finalizes the tab itself, so the flag tells
                    // the synchronous caller not to treat the not-yet-set _doc as a failed open.
                    _asyncOpenPending = true;
                    StripEncryptionAndOpen(srcPath, path, busyMessage: Loc("Str_Busy_OpeningProtected"));
                    return;
                }
                _currentFile = srcPath;
                FinishOpenFile(path, srcPath);
            }
            catch (WorkingCopyUnavailableException ex)
            {
                ShowWorkingCopyProblem(ex);
            }
            catch (Exception ex) when (PdfImport.IsOwnerPasswordException(ex))
            {
                // An empty user password can open the file while its owner password still forbids
                // modification. Retrying through PdfSharp's ReadOnly parser is not safe for malformed
                // linearized files such as the Fritzbox manual: it reaches a broken hint table and throws
                // an array-index error. PDFium already has a tolerant, lossless rewrite path that removes
                // the restriction and repairs those tables, so use it just as we do for other encrypted PDFs.
                _asyncOpenPending = true;
                StripEncryptionAndOpen(srcPath, path, busyMessage: Loc("Str_Busy_OpeningProtected"));
            }
            catch (Exception ex) when (PdfImport.IsPasswordException(ex))
            {
                string? pw = PromptForPassword(path);
                if (pw is null) return;
                try
                {
                    _doc?.Close();
                    _doc = null; var tempDec = App.MakeTempFile("dec");
                    PdfEngineIntegration.RemoveEncryption(srcPath, tempDec, pw);
                    _doc = PdfWorkingDocument.Open(tempDec, null);
                    _currentFile = tempDec;
                    FinishOpenFile(path, tempDec);
                    _openedFromProtected = true;   // #149: unlocked with the user's password
                    _protection = OpenedProtection.Capture(srcPath, pw);
                    CompleteProtectedRecovery(path);
                }
                catch (WorkingCopyUnavailableException copy)
                {
                    ShowWorkingCopyProblem(copy);
                }
                catch (Exception) when (PdfEngineIntegration.OpensWhereItLies(srcPath, pw))
                {
                    // A strip would clear the signatures, so the file opens read-only under its key.
                    OpenSignedProtected(srcPath, path, OpenedProtection.Capture(srcPath, pw));
                }
                catch (Exception) when (PdfRestrictions.Read(srcPath, pw) is { Role: PdfAccessRole.User, MayModify: false })
                {
                    // The user password opens the file but its owner forbids editing, so it opens
                    // read-only with the notice that offers the owner password.
                    OpenViewingCopy(srcPath, path, OpenedProtection.Capture(srcPath, pw));
                }
                catch (Exception ex2)
                {
                    // The document was assigned before the finish step threw (a PDF with no pages, for one),
                    // so nothing may count as opened.
                    AbandonFailedOpen();
                    KillerDialog.Show(this, string.Format(Loc("Str_Dlg_FailedOpen"), ex2.Message), Loc("Str_Dlg_AppTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex) when (PdfImport.IsXRefException(ex))
            {
                // Some PDFs have malformed or non-standard XRef tables that PdfSharp can't
                // open in Modify mode. Fall back to ReadOnly; if that also fails, offer repair.
                try
                {
                    _doc?.Close();
                    _doc = null; _doc = PdfWorkingDocument.Open(srcPath, null, isReadOnly: true);
                    _currentFile = srcPath;
                    FinishOpenFile(path, srcPath);
                    RememberProtectionOfDamagedSource(srcPath);
                    SetStatus(string.Format(Loc("Str_OpenedReadOnlyXRef"), System.IO.Path.GetFileName(path), _doc.PageCount));
                    KillerDialog.Show(this,
                        string.Format(Loc("Str_Dlg_OpenedReadOnlyXRef"), System.IO.Path.GetFileName(path)),
                        Loc("Str_Dlg_AppTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                catch (WorkingCopyUnavailableException copy)
                {
                    ShowWorkingCopyProblem(copy);
                }
                catch
                {
                    // ReadOnly also failed - offer to repair.
                    var result = KillerDialog.Show(this,
                        string.Format(Loc("Str_Dlg_RepairAskOpen"), System.IO.Path.GetFileName(path)),
                        Loc("Str_Dlg_AppTitle"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (result == MessageBoxResult.Yes)
                        TryRepairAndOpen(srcPath);
                    else
                        AbandonFailedOpen();
                }
            }
            catch (Exception ex) when (PdfImport.IsEofParseException(ex))
            {
                // PdfSharpCore rejects some structurally-valid PDFs with "Unexpected EOF" even though
                // PDFium (and every common viewer) reads them fine. Re-save losslessly through PDFium on
                // a background thread (so the window doesn't freeze). The recovered copy is content-
                // equivalent, so it opens clean without nagging to save (markDirty: false).
                _asyncOpenPending = true;
                StripEncryptionAndOpen(srcPath, path, rebuildStartsDirty: false);
            }
            catch (Exception)
            {
                // Any other open failure (truncated file, malformed objects, an out-of-range parse, etc.):
                // we can't classify the damage, but the PDFium-based repair often recovers it anyway, so
                // offer the repair rather than just failing outright.
                var result = KillerDialog.Show(this,
                    string.Format(Loc("Str_Dlg_RepairAskOpen"), System.IO.Path.GetFileName(path)),
                    Loc("Str_Dlg_AppTitle"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (result == MessageBoxResult.Yes)
                    TryRepairAndOpen(srcPath);   // sets _asyncOpenPending and finalizes the tab itself
                else
                    AbandonFailedOpen();
            }
        }

        // A file that opened but could not be finished (for example one that recovers to no pages) leaves
        // a working document behind. When the user declines the repair, nothing may count as opened.
        private void AbandonFailedOpen()
        {
            _doc?.Close();
            _doc = null;
            CloseEngineDocumentSession();
        }

        // The open-failure classifiers (IsEofParseException, IsXRefException, IsNetworkPath,
        // IsOwnerPasswordException, IsPasswordException) live in Services/PdfImport.cs.

        // The PDF itself is fine; there was no room for KillerPDF's working copy, usually a full or
        // unavailable temp folder. Every open path reports that instead of offering a repair.
        private void ShowWorkingCopyProblem(WorkingCopyUnavailableException error)
        {
            _doc?.Close();
            _doc = null;
            KillerDialog.Show(this, Loc("Str_Err_WorkingCopy") + "\n" + error.Message, Loc("Str_Dlg_AppTitle"),
                MessageBoxButton.OK, MessageBoxImage.Error);
        }

        // A tab that reads its file where it lies, still encrypted, passes the protection that holds its
        // key, because finishing the open already reads and renders the file. Every other open is a
        // plain working copy, and the protected routes set what the file allowed after this returns.
        private void FinishOpenFile(string displayPath, string workingPath, OpenedProtection? readingProtection = null)
        {
            if (readingProtection is not null) _protection = readingProtection;
            try
            {
                _completeDocumentSource = CompleteDocumentSource.Capture(_doc
                    ?? throw new InvalidOperationException("No PDF is open."));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                throw new WorkingCopyUnavailableException(error);
            }
            workingPath = _completeDocumentSource.Path;
            List<PdfEngineIntegration.MeasurementData> openedMeasurements = TakeMeasurementsFromWorkingCopy(ref workingPath);
            _currentFile = workingPath;
            // A document with no pages is refused before any of the previous document's session state
            // (annotations, labels, undo history) is replaced, so a declined repair leaves nothing half-open.
            CloseEngineDocumentSession();
            PdfEngineDocumentSession engineSession = EnsureEngineDocumentSession();
            int pageCount = engineSession.Pages.Count;
            if (pageCount == 0)
                throw new InvalidOperationException("The PDF contains no pages.");
            _publishedFile = null;
            // A temp-backed import (a browser download) has no saved home: cleanup deletes the
            // temp file, so the first Save must ask where to keep it.
            string? suggested = App.TakeSuggestedName(displayPath);
            bool imported = App.IsCleanupOwnedPath(displayPath);
            _originalFile = imported ? null : displayPath;
            _loadedRevision = imported ? null : Services.FileRevision.Capture(displayPath);
            _suggestedName = imported ? suggested ?? System.IO.Path.GetFileName(displayPath) : null;
            FileNameLabel.Text = _suggestedName ?? System.IO.Path.GetFileName(displayPath);
            _docStampSpec = null;
            _annotations.Clear();
            _continuousLinks.Clear();   // drop the previous document's cached link rects
            _undoStack.Clear();
            _redoStack.Clear();
            // The map belongs to the previous document. Native rotations in a newly opened PDF
            // remain on each page until SaveTempAndReload moves them into this map.
            _pageRotations.Clear();
            _navBack.Clear();
            _navForward.Clear();
            _renderDims.Clear();
            _formTextValues.Clear();
            _formChoiceValues.Clear();
            _formCheckValues.Clear();
            _formFontSizes.Clear();
            _formRadioValues.Clear();
            LoadRedactionMarksFromDocument();
            ActiveViewer.LoadMeasurements(openedMeasurements);
            Search.ClearPageResults();
            _gridScrollToPage = -1;
            AcceptSavedCheckpoint();
            _openedFromProtected = false;   // #149: set true by the two protected-open paths after this returns
            _restrictedByCertificate = false;
            _restrictedFormFillable = false;   // read once by TryOpenProtectedReadOnly after this returns
            _protection = readingProtection ?? OpenedProtection.None;   // read once by the protected-open paths after this returns
            // Restore this file's last page and zoom if we've seen it before. View mode is global:
            // the last mode selected with the footer controls becomes the default for every PDF.
            _viewMode = Enum.TryParse<ViewMode>(App.GetSetting("ViewMode"), out var preferredView)
                ? preferredView : ViewMode.Continuous;
            FitMode? preferredFit = Enum.TryParse<FitMode>(App.GetSetting("DefaultFitMode"), out var savedFit)
                && savedFit != FitMode.None ? savedFit : null;
            if (TryGetDocState(displayPath, out var sfit, out var szoom, out var savedView, out var spage,
                out var scrollH, out var scrollV, out var trueZoom))
            {
                _fitMode   = sfit;
                _zoomLevel = szoom;
                int pg = Math.Max(0, Math.Min(spage, pageCount - 1));
                BootstrapDocumentView(pg, autoFit: false, restoreFitMode: true,
                    restoreHorizontalOffset: savedView == _viewMode ? scrollH : null,
                    restoreVerticalOffset: savedView == _viewMode ? scrollV : null,
                    restoreTrueZoom: trueZoom);
            }
            else
            {
                if (preferredFit.HasValue)
                {
                    _fitMode = preferredFit.Value;
                    BootstrapDocumentView(0, autoFit: false, restoreFitMode: true);
                }
                else BootstrapDocumentView(0, autoFit: true);
            }
            SetStatus(string.Format(Loc("Str_Opened"), System.IO.Path.GetFileName(displayPath), pageCount));
            SyncSidebarToDocState(hasDoc: true, startup: false);   // a document is up: open the rail, show page controls
        }

        // A damaged file reaches its open through a fallback or a repair instead of the protected
        // paths, so whether it is protected is decided here, from the engine and not from how it opened.
        private void RememberProtectionOfDamagedSource(string sourcePath)
        {
            _protection = OpenedProtection.Capture(sourcePath, null);
            if (_protection.Access.Role != PdfAccessRole.Unencrypted) _openedFromProtected = true;
        }

        // Measurements are edited on the page overlay and written back on save, so the working copy
        // sheds the ones the file carries and they return as overlay measurements. A read-only open
        // keeps them in the page, where the renderer still shows them.
        private List<PdfEngineIntegration.MeasurementData> TakeMeasurementsFromWorkingCopy(ref string workingPath)
        {
            if (_doc is null || _doc.IsReadOnly) return [];
            try
            {
                List<PdfEngineIntegration.MeasurementData> measurements =
                    PdfEngineIntegration.ReadMeasurements(PdfDocument.Open(File.ReadAllBytes(workingPath)));
                if (measurements.Count == 0) return measurements;
                string stripped = App.MakeTempFile("measure");
                PdfEngineIntegration.RemoveMeasurements(workingPath, stripped);
                // The stripped copy opens before the live document closes, so a failed reopen keeps
                // the document usable with its measurements still in the page.
                var reopened = PdfWorkingDocument.Open(stripped, null);
                _doc.Close();
                _doc = reopened;
                workingPath = stripped;
                return measurements;
            }
            catch (Exception ex) when (ex is FormatException or InvalidOperationException
                or NotSupportedException or ArgumentException or IOException)
            {
                System.Diagnostics.Debug.WriteLine($"TakeMeasurementsFromWorkingCopy: {ex}");
                return [];
            }
        }

        // Themed "Password Required" prompt (KillerDialog): family dialog chrome + themed PasswordBox.
        private string? PromptForPassword(string filename) => KillerDialog.PromptPassword(this, filename);

        // PDF parsing, repair, and rendering are owned by The KillerPDF.Engine.
        // TryImportRepairToPath lives in Services/PdfImport.cs.

        private async void TryRepairAndOpen(string path)
        {
            // Repair is CPU/IO heavy, so it runs on a background thread behind a spinner overlay -
            // otherwise the window froze (hourglass, no feedback) for the whole repair. Only the
            // file production runs off-thread; opening/rendering the result stays on the UI thread.
            _asyncOpenPending = true;   // the synchronous open caller defers tab finalization to here
            var ct = BeginCancellableOp(Loc("Str_Op_Repair"));
            var owner = AsyncOwner();
            var busy = ShowBusyOverlay(Loc("Str_Busy_Repairing"));
            try
            {
                // Release any open document before the worker reads the source file.
                _doc?.Close();
                _doc = null;
                string? repairedPath = null;
                bool raster = false;

                // Strategy 0 (#103): lossless engine re-save. Compatibility recovery handles
                // broken xref tables (including the dangling /Outlines entry older KillerPDF
                // builds wrote) and rewrites a clean file preserving EVERYTHING - forms,
                // bookmarks, text. The import-copy below drops the document-level AcroForm,
                // which is what used to turn a repaired fillable form into a flat one.
                repairedPath = await System.Threading.Tasks.Task.Run(() =>
                {
                    var p = App.MakeTempFile("repaired");
                    return PdfEngineIntegration.TryRemoveEncryptionWithoutPassword(path, p)
                        ? p : null;
                });
                if (!ReclaimAsyncOwner(owner)) { HideBusyOverlay(busy); return; }   // its tab was closed meanwhile
                if (ct.IsCancellationRequested) { HideBusyOverlay(busy); _asyncOpenPending = false; SetStatus(Loc("Str_St_RepairCanceled")); return; }   // canceled during strategy 0

                // Strategy 1: PdfSharpCore Import mode - page-copy, more lenient than Modify/ReadOnly.
                // Works when the XRef is partially corrupt but the object data is intact. (Returns
                // null on failure rather than throwing.)
                repairedPath ??= await System.Threading.Tasks.Task.Run(() => PdfImport.RepairViaImportToFile(path));
                if (!ReclaimAsyncOwner(owner)) { HideBusyOverlay(busy); return; }   // its tab was closed meanwhile
                if (ct.IsCancellationRequested) { HideBusyOverlay(busy); _asyncOpenPending = false; SetStatus(Loc("Str_St_RepairCanceled")); return; }   // canceled during strategy 1

                // Strategy 2: engine rasterize. Compatibility recovery handles damage
                // PdfSharpCore cannot; each page is rendered to a bitmap and rebuilt into a clean PDF.
                // Text won't be selectable in the result, but the file will open and print.
                if (repairedPath is null)
                {
                    repairedPath = await System.Threading.Tasks.Task.Run(
                        () => PdfImport.RepairViaRasterizeToFile(path));
                    if (!ReclaimAsyncOwner(owner)) { HideBusyOverlay(busy); return; }   // its tab was closed meanwhile
                    raster = repairedPath is not null;
                }
                if (ct.IsCancellationRequested) { HideBusyOverlay(busy); _asyncOpenPending = false; SetStatus(Loc("Str_St_RepairCanceled")); return; }   // canceled during strategy 2

                if (repairedPath is null)
                {
                    HideBusyOverlay(busy);
                    _asyncOpenPending = false;
                    KillerDialog.Show(this, Loc("Str_Repair_Unrecoverable"),
                        "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // Open and render the repaired copy on the UI thread.
                _doc = PdfWorkingDocument.Open(repairedPath, null);
                _currentFile = repairedPath;
                FinishOpenFile(path, repairedPath);
                RememberProtectionOfDamagedSource(path);
                MarkDirty(true); // repaired copy lives in temp - user must Save As
                SetStatus(string.Format(Loc(raster ? "Str_OpenedRasterRepair" : "Str_OpenedRepaired"),
                                        System.IO.Path.GetFileName(path), _doc.PageCount));
                HideBusyOverlay(busy);
                FinalizeAsyncOpen();
                KillerDialog.Show(this,
                    raster
                        ? string.Format(Loc("Str_Dlg_RepairedRaster"), System.IO.Path.GetFileName(path))
                        : string.Format(Loc("Str_Dlg_Repaired"), System.IO.Path.GetFileName(path)),
                    "KillerPDF", MessageBoxButton.OK, MessageBoxImage.None);
            }
            catch (WorkingCopyUnavailableException copy)
            {
                HideBusyOverlay(busy);
                if (ReclaimAsyncOwner(owner))
                {
                    _asyncOpenPending = false;
                    ShowWorkingCopyProblem(copy);
                }
            }
            catch (Exception ex)
            {
                HideBusyOverlay(busy);
                if (ReclaimAsyncOwner(owner))
                {
                    _asyncOpenPending = false;
                    AbandonFailedOpen();   // the finish step can throw after the repaired copy is assigned
                    KillerDialog.Show(this, Loc("Str_Err_RepairFailed") + "\n" + ex.Message, "KillerPDF",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            finally
            {
                EndCancellableOp(ct);
            }
        }

        // Opens a PDF encrypted for certificate recipients with the first personal certificate that
        // unlocks it. The decrypted working copy lives in temp, like a password-protected open.
        private void OpenCertificateProtected(string srcPath, string displayPath)
        {
            var before = _doc;
            try
            {
                IReadOnlyList<System.Security.Cryptography.X509Certificates.X509Certificate2> certificates;
                try { certificates = Services.Signing.WindowsCertificateStore.ListEncryptionCertificates(); }
                catch (System.Security.Cryptography.CryptographicException) { certificates = []; }
                var tempDec = App.MakeTempFile("dec");
                if (!PdfEngineIntegration.TryRemoveCertificateEncryption(srcPath, tempDec, certificates, out OpenedProtection recipient))
                {
                    KillerDialog.Show(this, Loc("Str_Dlg_NoRecipientCertificate"), Loc("Str_Dlg_AppTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                _doc?.Close();
                _doc = null;
                bool mayEdit = recipient.Access.MayModify;
                // A recipient its author did not let edit gets the file read-only, as in Acrobat. There is
                // no owner password for certificate encryption, so nothing unlocks it here.
                _doc = PdfWorkingDocument.Open(tempDec, null, isReadOnly: !mayEdit);
                _currentFile = tempDec;
                FinishOpenFile(displayPath, tempDec);
                _openedFromProtected = true;
                _restrictedByCertificate = !mayEdit;
                _protection = recipient;
                if (!mayEdit) ActiveViewer.UpdateRestrictionNotice();
                CompleteProtectedRecovery(displayPath);
            }
            catch (WorkingCopyUnavailableException copy)
            {
                ShowWorkingCopyProblem(copy);
            }
            catch (Exception ex)
            {
                // The finish step can throw after the document is assigned (a PDF with no pages, for one), but a
                // failure before the open replaced anything leaves the document that was already showing.
                if (!ReferenceEquals(_doc, before)) AbandonFailedOpen();
                KillerDialog.Show(this, string.Format(Loc("Str_Dlg_FailedOpen"), ex.Message), Loc("Str_Dlg_AppTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // Writes an encrypted copy of the saved document. Unsaved edits are saved first so the copy
        // matches the screen; the open file itself stays as it is.
        private void OpenProtectDialog()
        {
            if (_doc is null) { KillerDialog.Show(this, Loc("Str_Msg_OpenFirst")); return; }
            if (!EnsureMayRemoveProtection()) return;
            CommitActiveTextBox();
            if (_isDirty || string.IsNullOrEmpty(_originalFile) || !File.Exists(_originalFile))
            {
                var ask = KillerDialog.Show(this, Loc("Str_Dlg_SaveBeforeProtect"),
                    "KillerPDF", MessageBoxButton.OKCancel);
                if (ask != MessageBoxResult.OK) return;
                SaveInPlace();
                if (_isDirty || string.IsNullOrEmpty(_originalFile) || !File.Exists(_originalFile))
                    return;   // save was canceled or failed
            }
            // A document opened from a protected file still reads as the plain bytes of its last save.
            string? plainCopy = _completeDocumentSource?.Path ?? _currentFile;
            string source = _openedFromProtected && !string.IsNullOrEmpty(plainCopy) && File.Exists(plainCopy)
                ? plainCopy : _originalFile!;
            new ProtectDocumentDialog(this, source, _originalFile!).ShowDialog();
        }

        // Remove Password is the one save that writes a protected file with no encryption, so it has
        // its own path: a regular save keeps the encryption, and only a limit makes this the owner's call.
        private void RemovePassword()
        {
            if (_doc is null) { KillerDialog.Show(this, Loc("Str_Msg_OpenFirst")); return; }
            if (EnsureMayRemoveProtection()) SaveInPlace(otherPaneConfirmed: false, removeProtection: true);
        }

        // Strips a PDF's encryption on a background thread (so the window doesn't freeze), then opens the
        // clean copy. Mirrors TryRepairAndOpen; finalizes the tab via FinalizeAsyncOpen.
        private async void StripEncryptionAndOpen(string srcPath, string displayPath, bool rebuildStartsDirty = true, string? busyMessage = null)
        {
            _asyncOpenPending = true;
            var ct = BeginCancellableOp(Loc("Str_Op_Operation"));
            var owner = AsyncOwner();
            var busy = ShowBusyOverlay(busyMessage ?? Loc("Str_Busy_Opening"));
            try
            {
                _doc?.Close();
                _doc = null; var repairedPath = App.MakeTempFile("repaired");
                ProtectedOpen opened = await System.Threading.Tasks.Task.Run(() => ProtectedDocumentFiles.PrepareWorkingCopy(
                    srcPath, repairedPath, (source, working) => PdfImport.TryImportRepairToPath(source, working)));
                if (!ReclaimAsyncOwner(owner)) { HideBusyOverlay(busy); AbandonProtectedRecovery(displayPath); EndCancellableOp(ct); return; }   // its tab was closed meanwhile
                if (ct.IsCancellationRequested) { HideBusyOverlay(busy); _asyncOpenPending = false; AbandonProtectedRecovery(displayPath); SetStatus(Loc("Str_St_Canceled")); EndCancellableOp(ct); return; }
                if (!opened.Stripped)
                {
                    HideBusyOverlay(busy);
                    // A protected PDF the engine can read keeps its real text read-only.
                    // Rasterizing repair is only for files that cannot be read at all.
                    if (TryOpenProtectedReadOnly(srcPath, displayPath, opened.Protection))
                    {
                        FinalizeAsyncOpen();
                        CompleteProtectedRecovery(displayPath);
                        EndCancellableOp(ct);
                        return;
                    }
                    AbandonProtectedRecovery(displayPath);   // a rasterized repair is not the protected copy
                    TryRepairAndOpen(srcPath);   // re-registers the cancellable op; repair finalizes the tab
                    return;
                }
                _doc = PdfWorkingDocument.Open(repairedPath, null);
                _currentFile = repairedPath;
                FinishOpenFile(displayPath, repairedPath);
                _openedFromProtected = opened.Protection.Access.Role != PdfAccessRole.Unencrypted;   // #149
                _protection = opened.Protection;
                if (opened.StartsDirty && rebuildStartsDirty) MarkDirty(true);   // a rebuild is not the file, so it is unsaved work
                HideBusyOverlay(busy);
                FinalizeAsyncOpen();
                CompleteProtectedRecovery(displayPath);
                EndCancellableOp(ct);
            }
            catch (WorkingCopyUnavailableException copy)
            {
                HideBusyOverlay(busy);
                AbandonProtectedRecovery(displayPath);
                if (ReclaimAsyncOwner(owner))
                {
                    _asyncOpenPending = false;
                    ShowWorkingCopyProblem(copy);
                }
                EndCancellableOp(ct);
            }
            catch (Exception ex)
            {
                HideBusyOverlay(busy);
                AbandonProtectedRecovery(displayPath);
                if (ReclaimAsyncOwner(owner))
                {
                    _asyncOpenPending = false;
                    AbandonFailedOpen();
                    KillerDialog.Show(this, Loc("Str_Err_OpenProtected") + "\n" + ex.Message,
                        "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                EndCancellableOp(ct);
            }
        }

        // Opens an encrypted PDF whose restrictions block a rewrite as a read-only working document.
        private bool TryOpenProtectedReadOnly(string srcPath, string displayPath, OpenedProtection known) =>
            TryOpenReadOnly(srcPath, displayPath, () => ReadOnlyOriginal.Open(srcPath, known));

        private bool TryOpenReadOnly(string srcPath, string displayPath,
            Func<(PdfWorkingDocument Document, OpenedProtection Protection)> open)
        {
            try
            {
                (PdfWorkingDocument document, OpenedProtection protection) = open();
                _doc = document;
                _currentFile = srcPath;
                // The original is read where it lies, so the tab's protection reads it from the start.
                FinishOpenFile(displayPath, srcPath, protection);
                _openedFromProtected = true;
                // The fill permission belongs to the file as opened, so it is read once here rather
                // than parsed again on every save of a fill-allowed restricted form.
                _protection = protection;
                _restrictedFormFillable = _protection.AllowsRestrictedFill;
                SetStatus(string.Format(Loc("Str_OpenedReadOnly"),
                    System.IO.Path.GetFileName(displayPath), _doc.PageCount));
                return true;
            }
            // A full or unavailable temp folder is reported as such, not answered with a repair.
            catch (Exception error) when (error is not OutOfMemoryException and not WorkingCopyUnavailableException)
            {
                _doc?.Close();
                _doc = null;
                return false;
            }
        }

        // The encrypted original behind each viewing copy. A viewing copy has no encryption left to
        // check a password against, so unlocking must always go back to the original.
        private static readonly Dictionary<string, string> ViewingCopySources =
            new(StringComparer.OrdinalIgnoreCase);

        // Opens a PDF whose user password forbids editing as a read-only viewing copy. The tab holds the
        // file's encryption, so the copy comes back after a filled form is saved with no password asked.
        private void OpenViewingCopy(string srcPath, string displayPath, OpenedProtection protection)
        {
            var before = _doc;
            try
            {
                string tempDec = App.MakeTempFile("dec");
                protection.WriteViewingCopy(srcPath, tempDec);
                _doc?.Close();
                _doc = null;
                _doc = PdfWorkingDocument.Open(tempDec, null, isReadOnly: true);
                _currentFile = tempDec;
                FinishOpenFile(displayPath, tempDec);
                ViewingCopySources[_currentFile!] = srcPath;   // finishing can move the working path
                _openedFromProtected = true;   // #149: the source still carries its encryption
                _protection = protection;
                _restrictedFormFillable = protection.AllowsRestrictedFill;
                ActiveViewer.UpdateRestrictionNotice();
                SetStatus(string.Format(Loc("Str_OpenedReadOnly"),
                    System.IO.Path.GetFileName(displayPath), _doc.PageCount));
                CompleteProtectedRecovery(displayPath);
            }
            catch (WorkingCopyUnavailableException copy)
            {
                ShowWorkingCopyProblem(copy);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                if (!ReferenceEquals(_doc, before)) AbandonFailedOpen();
                KillerDialog.Show(this, string.Format(Loc("Str_Dlg_FailedOpen"), error.Message),
                    Loc("Str_Dlg_AppTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // Opens a signed PDF that needs a password read-only from the encrypted file itself. Writing a
        // decrypted copy would clear its signatures, so the tab holds the key its password produced and
        // hands it to every reader of the file. Nothing is rewritten, and an owner unlock fails with
        // the writer's own reason, since no rewrite of the file can keep its signatures. The protection
        // is the tab's before the file is finished, since finishing already reads the file.
        private void OpenSignedProtected(string srcPath, string displayPath, OpenedProtection protection)
        {
            var before = _doc;
            try
            {
                _doc?.Close();
                _doc = null;
                _doc = PdfWorkingDocument.Open(srcPath, protection, isReadOnly: true);
                _currentFile = srcPath;
                FinishOpenFile(displayPath, srcPath, protection);
                _openedFromProtected = true;
                _restrictedFormFillable = protection.AllowsRestrictedFill;
                ActiveViewer.UpdateRestrictionNotice();
                SetStatus(string.Format(Loc("Str_OpenedReadOnly"),
                    System.IO.Path.GetFileName(displayPath), _doc.PageCount));
                CompleteProtectedRecovery(displayPath);
            }
            catch (WorkingCopyUnavailableException copy)
            {
                ShowWorkingCopyProblem(copy);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                if (!ReferenceEquals(_doc, before)) AbandonFailedOpen();
                KillerDialog.Show(this, string.Format(Loc("Str_Dlg_FailedOpen"), error.Message),
                    Loc("Str_Dlg_AppTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // A PDF whose owner password forbids editing stays read-only, the way Acrobat treats it,
        // until the user supplies that owner password. The notice bar in the pane offers this.
        private bool IsOwnerRestricted => ActiveViewer.IsOwnerRestricted;

        // Gate for anything that edits or rewrites the document. Returns true when it may go
        // ahead, asking for the owner password first on a restricted PDF. Commands that act on the
        // page selection pass continueAfterUnlock: false, because the reopen resets the selection;
        // the user then repeats the command on the unlocked document.
        private bool EnsureEditable(bool continueAfterUnlock = true, UnlockPurpose purpose = UnlockPurpose.Edit)
            => !IsOwnerRestricted || (UnlockOwnerRestrictions(purpose) && continueAfterUnlock);

        // Gate for an output that cannot keep the file's encryption: a new PDF, page images, text or
        // data. An edit-forbidden tab unlocks first, and then the limits the file declares answer.
        // The prompt asks for the output, not for editing.
        private bool EnsureMayWrite(PdfOutput output, bool continueAfterUnlock = false)
            => EnsureEditable(continueAfterUnlock, UnlockPurpose.Output)
               && PassesProtectionGate(_protection.AskToWrite(output, AskForOwnerPassword));

        // Gate for taking the encryption off, which only a limit makes the owner's call.
        private bool EnsureMayRemoveProtection()
            => EnsureEditable(purpose: UnlockPurpose.Output) && PassesProtectionGate(_protection.AskToRemove(AskForOwnerPassword));

        private string? AskForOwnerPassword(bool lastWasWrong)
        {
            if (lastWasWrong) KillerDialog.Show(this, Loc("Str_Restricted_WrongPassword"), "KillerPDF",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return KillerDialog.PromptPassword(this, _originalFile ?? _currentFile ?? string.Empty,
                Loc("Str_Protection_Prompt"), Loc("Str_Restricted_Unlock"));
        }

        private bool PassesProtectionGate(ProtectionAnswer answer)
        {
            if (answer == ProtectionAnswer.Allowed) return true;
            if (answer == ProtectionAnswer.Canceled) { SetStatus(Loc("Str_Protection_Refused")); return false; }
            bool unreadable = !EnsureProtectionReadable();
            if (!unreadable) KillerDialog.Show(this, Loc(_protection.Access.Role == PdfAccessRole.Recipient
                    ? "Str_Protection_Refused_Certificate" : "Str_Protection_Refused"),
                "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        // A tab whose protection could not be read holds no key, so nothing can be saved under it. It
        // says so in its own words, and each attempt reads the file again.
        private bool EnsureProtectionReadable()
        {
            if (!_protection.IsUnreadable) return true;
            KillerDialog.Show(this, string.Format(Loc("Str_Protection_Unreadable"), _protection.Access.Refusal), "KillerPDF",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        // Asks for the owner password and reopens the restricted PDF from an unrestricted working
        // copy. A wrong password, or the empty user password, leaves the document read-only.
        private bool UnlockOwnerRestrictions(UnlockPurpose purpose = UnlockPurpose.Edit)
        {
            if (!IsOwnerRestricted) return true;
            // Certificate encryption has no owner password, so the recipient's permissions stand.
            if (_restrictedByCertificate)
            {
                KillerDialog.Show(this, Loc("Str_Restricted_CertificateNotice"), "KillerPDF",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }
            if (string.IsNullOrEmpty(_currentFile) || string.IsNullOrEmpty(_originalFile)) return false;
            string sourcePath = _currentFile;
            string displayPath = _originalFile;
            string encryptedPath = ViewingCopySources.GetValueOrDefault(sourcePath, sourcePath);
            string? password = KillerDialog.PromptPassword(this, displayPath,
                Loc(UnlockPrompt.KeyFor(purpose)), Loc("Str_Restricted_Unlock"));
            if (password is null) return false;

            string unlockedPath = App.MakeTempFile("dec");
            UnlockAnswer unlock = _protection.Unlock(encryptedPath, unlockedPath, password);
            if (unlock.Outcome != UnlockOutcome.Unlocked)
            {
                KillerDialog.Show(this, unlock.Outcome == UnlockOutcome.WrongPassword
                        ? Loc("Str_Restricted_WrongPassword")
                        : string.Format(Loc("Str_Restricted_UnlockFailed"), System.IO.Path.GetFileName(displayPath), unlock.Reason),
                    "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            // The unlocked copy has the same pages, so overlay work made while read-only (form
            // entries, pasted annotations) carries over instead of being lost to the reopen. A failed
            // unlock puts it back on the read-only original the same way.
            bool wasDirty = _isDirty;
            OpenedProtection protection = _protection;
            var annotations = _annotations.ToDictionary(pair => pair.Key, pair => pair.Value);
            var rotations = new Dictionary<int, int>(_pageRotations);
            var textValues = new Dictionary<string, string>(_formTextValues);
            var choiceValues = new Dictionary<string, string>(_formChoiceValues);
            var checkValues = new Dictionary<string, bool>(_formCheckValues);
            var radioValues = new Dictionary<string, string>(_formRadioValues);
            var fontSizes = new Dictionary<string, double>(_formFontSizes);
            try
            {
                _doc?.Close();
                _doc = null;
                _doc = PdfWorkingDocument.Open(unlockedPath, null);
                _currentFile = unlockedPath;
                FinishOpenFile(displayPath, unlockedPath);
                _openedFromProtected = true;   // #149: the source still carries its encryption
                _protection = protection.AfterUnlock();   // the owner password lifts every limit, printing too
                CarryOverlays();
                FinalizeAsyncOpen();
                SetStatus(string.Format(Loc("Str_Restricted_Unlocked"), System.IO.Path.GetFileName(displayPath)));
                return true;
            }
            catch (WorkingCopyUnavailableException copy)
            {
                ShowWorkingCopyProblem(copy);
                RestoreReadOnlyOriginal(sourcePath, displayPath, CarryOverlays, protection);
                return false;
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                _doc?.Close();
                _doc = null;
                KillerDialog.Show(this, string.Format(Loc("Str_Dlg_FailedOpen"), error.Message),
                    Loc("Str_Dlg_AppTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
                RestoreReadOnlyOriginal(sourcePath, displayPath, CarryOverlays, protection);
                return false;
            }

            void CarryOverlays()
            {
                // A failed unlock reopens the viewing copy; it still unlocks from the original, and
                // a copy with no encryption left cannot say what the original allowed.
                if (_doc?.IsReadOnly == true && _currentFile is not null)
                {
                    ViewingCopySources[_currentFile] = encryptedPath;
                    _protection = protection;
                    _restrictedFormFillable = protection.AllowsRestrictedFill;
                }
                foreach (var pair in annotations) _annotations[pair.Key] = pair.Value;
                foreach (var pair in rotations) _pageRotations[pair.Key] = pair.Value;
                foreach (var pair in textValues) _formTextValues[pair.Key] = pair.Value;
                foreach (var pair in choiceValues) _formChoiceValues[pair.Key] = pair.Value;
                foreach (var pair in checkValues) _formCheckValues[pair.Key] = pair.Value;
                foreach (var pair in radioValues) _formRadioValues[pair.Key] = pair.Value;
                foreach (var pair in fontSizes) _formFontSizes[pair.Key] = pair.Value;
                foreach (int page in annotations.Keys) RenderAllAnnotations(page);
                if (wasDirty) MarkDirty(true);
            }
        }

        // Puts the read-only original back after a failed unlock so the tab is not left empty. When
        // there is still no room for a working copy the tab stays empty; that was just reported.
        private void RestoreReadOnlyOriginal(string sourcePath, string displayPath, Action carryOverlays,
            OpenedProtection protection)
        {
            try
            {
                if (TryOpenReadOnly(sourcePath, displayPath, () => ReadOnlyOriginal.Restore(sourcePath, protection)))
                    carryOverlays();
            }
            catch (WorkingCopyUnavailableException)
            {
                _doc?.Close();
                _doc = null;
            }
            FinalizeAsyncOpen();
        }

        // Finalizes a background open (encryption strip / repair) once the document is loaded on the UI
        // thread: stores it into the active tab session and refreshes the tool + tab strip. Mirrors the
        // tail of OpenInNewTab, which is skipped while _asyncOpenPending is set.
        // A background open belongs to the tab it started in. The user can switch tabs or panes,
        // or close that tab, while it runs, and _doc/_currentFile follow whatever is active, so
        // before a result touches them, bring its own tab back. False when that tab is gone and
        // the result must be dropped.
        private (Controls.PdfViewer Viewer, Controls.PdfViewer.DocumentSession? Session) AsyncOwner() =>
            (ActiveViewer, ActiveViewer.ActiveSessionExt);

        private bool ReclaimAsyncOwner((Controls.PdfViewer Viewer, Controls.PdfViewer.DocumentSession? Session) owner)
        {
            if (owner.Session is null || !owner.Viewer.SessionsRef.Contains(owner.Session))
            {
                // Its tab is gone; stop the pane treating an open as in flight unless another is.
                if (!owner.Viewer.SessionsRef.Any(session => session.LoadPending))
                    owner.Viewer.AsyncOpenPendingRef = false;
                return false;
            }
            if (ReferenceEquals(owner.Viewer, ViewerB) && !_isSplit) OpenSplit();
            if (!ReferenceEquals(ActiveViewer, owner.Viewer)) FocusPane(owner.Viewer);
            if (!ReferenceEquals(owner.Viewer.ActiveSessionExt, owner.Session)) owner.Viewer.SwitchToTabExt(owner.Session);
            return ReferenceEquals(ActiveViewer, owner.Viewer)
                && ReferenceEquals(owner.Viewer.ActiveSessionExt, owner.Session);
        }

        private void FinalizeAsyncOpen()
        {
            _asyncOpenPending = false;
            if (_active != null) CaptureSessionState(_active);
            SetTool(_currentTool);
            RebuildTabStrip();
        }

        // The background-safe repair strategy workers (RepairViaImportToFile,
        // RepairViaRasterizeToFile) live in Services/PdfImport.cs.

        // ============================================================
        // Close file (Ctrl+W) closes the tab, which commits the active editor before its prompt
        // ============================================================

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
                var tempPath = App.MakeTempFile("new");
                File.WriteAllBytes(tempPath, PdfEngineIntegration.CreateBlankDocument());

                _doc = PdfWorkingDocument.Open(tempPath, null);
                // The blank document has no saved home. It opens as a temp-backed import, the way a
                // browser download does, so FinishOpenFile leaves it without an original file and
                // the first Save asks where to keep it instead of writing next to the process.
                App.RememberSuggestedName(tempPath, Loc("Str_Untitled") + ".pdf");
                FinishOpenFile(tempPath, tempPath);
                SetStatus(Loc("Str_KS_NewBlank"));
                CaptureSessionState(_active!);
                SetTool(_currentTool);   // sync the tool UI to this (new) tab's tool
                RebuildTabStrip();
            }
            catch (Exception ex)
            {
                AbortTabLoad(target, prev, createdNew);
                KillerDialog.Show(this, Loc("Str_Err_NewDocFailed") + "\n" + ex.Message,
                    "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Open_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Controls.FileDialog(Controls.FileDialogMode.Open)
                          { Filter = Loc("Str_Filter_Pdf") + "|*.pdf", Title = Loc("Str_Dlg_OpenPdf") };
            if (dlg.ShowDialog(this) == true) OpenInNewTab(dlg.FileName);
        }

        private sealed record OpenMenuCommand(string LabelKey, string Icon, bool RequiresDocument, Action Run);

        private OpenMenuCommand[] DocumentCreationCommands() =>
        [
            new("Str_Menu_Scan", "\uE8FE", false, ScanToPdf),
            new("Str_Menu_ConvertOffice", "\uE8A5", false, ConvertOfficeDocument),
        ];

        // Dropdown next to the Open button: the recent-files list.
        private void OpenRecent_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;   // also fired by right-click on the Open button; don't let it bubble
            var menu = MakeThemedMenu();

            menu.Items.Add(MakeMenuItem(Loc("Str_Menu_Import") + "...", (s2, e2) => ImportImages_Click(s2, e2), null, ""));
            foreach (OpenMenuCommand command in DocumentCreationCommands())
            {
                MenuItem item = MakeMenuItem(Loc(command.LabelKey), (_, _) => command.Run(), null, command.Icon);
                item.IsEnabled = !command.RequiresDocument || _doc is not null;
                menu.Items.Add(item);
            }
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
                        else KillerDialog.Show(this, Loc("Str_Err_FileNotFound") + "\n" + path, "KillerPDF",
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
                        ToolTip = Loc("Str_Menu_RemoveFromRecents")
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
                        Source              = ShellIcons.Large(path, isFolder: false),
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
                    (_, _) => { App.ClearRecentFiles(); PopulateRecentFilesList(); }, glyph: ""));   // keep the start screen in sync (#146)
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
                    ? Services.RecentFilesList.DateLabel(System.IO.File.GetLastWriteTime(path))
                    : Loc("Str_RecentMissing");

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
                var delIcon = new Glyph
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
                    Source              = ShellIcons.Large(path, isFolder: false),
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
                    else KillerDialog.Show(this, Loc("Str_Err_FileNotFound") + "\n" + path, "KillerPDF",
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
            var menu = MakeThemedMenu();

            if (_doc is null)
            {
                menu.Items.Add(new MenuItem { Header = Loc("Str_Menu_SaveNothing"), IsEnabled = false });
                menu.Items.Add(BuildDocumentToolsMenu());
            }
            else
            {
                menu.Items.Add(MakeMenuItem(Loc("Str_Menu_Save"), (_, _) => SaveInPlace(), GestureFor("Ctrl:S"), ""));
                menu.Items.Add(MakeMenuItem(Loc("Str_Menu_SaveAs"), (s2, e2) => SaveAs_Click(s2, e2), GestureFor("CtrlShift:S"), ""));
                menu.Items.Add(MakeMenuItem(Loc("Str_Menu_ReduceSize"), (s2, e2) => ReduceFileSize_Click(s2, e2)));   // #433
                menu.Items.Add(MakeMenuItem(Loc("Str_Menu_CompressZip"), (s2, e2) => CompressToZip_Click(s2, e2), null, ""));
                menu.Items.Add(MakeMenuItem(Loc("Str_Menu_ExportImages"), (s2, e2) => ExportImages_Click(s2, e2), null, ""));   // #132
                menu.Items.Add(MakeMenuItem(Loc("Str_Menu_ExportDocument"), (s2, e2) => ExportDocument_Click(s2, e2), GestureFor("CtrlShift:E"), ""));
                menu.Items.Add(MakeMenuItem(Loc("Str_TableExport_Title"), (_, _) => ExportTable(PageList.SelectedIndex)));
                menu.Items.Add(new Separator());
                // #149: visible always (discoverability, the PDF Viewer Plus way), enabled only when the
                // open file actually had a password/encryption. A regular save keeps the encryption,
                // so this is the one command that takes it off.
                var removePw = MakeMenuItem(Loc("Str_Menu_RemovePassword"), (_, _) => RemovePassword(), null, "");
                removePw.IsEnabled = _openedFromProtected && !_restrictedByCertificate;
                menu.Items.Add(removePw);
                menu.Items.Add(MakeMenuItem(Loc("Str_Menu_ProtectDocument"), (_, _) => OpenProtectDialog(), GestureFor("CtrlShift:P"), ""));
                menu.Items.Add(new Separator());
                menu.Items.Add(BuildDocumentToolsMenu());
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
            string? path = _originalFile ?? _currentFile;
            KillerPdf.Engine.Documents.PdfDocumentInformation info;
            try { info = KillerPdf.Engine.Documents.PdfDocumentInformation.Read(EnsureEngineDocumentSession().Document); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A bad date or title in an otherwise valid PDF is not worth a crash dialog.
                KillerDialog.Show(this, ex.Message, "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            KillerPdf.Engine.Authoring.PdfDocumentMetadata? editedMetadata = null;
            var dlg = new DocumentInfoDialog(this, info, metadata => editedMetadata = metadata, path);
            dlg.ShowDialog();   // fade-close dialogs don't reliably return true; rely on the Saved flag
            if (dlg.Saved && editedMetadata is not null)
            {
                if (!EnsureEditable()) return;
                var completeMetadata = PdfEngineIntegration.CompleteDocumentMetadata(info, editedMetadata);
                try
                {
                    SaveTempAndReload(
                        keepAnnotations: true,
                        finalizeSavedFile: target =>
                            PdfEngineIntegration.ApplyDocumentMetadata(target, completeMetadata));
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    KillerDialog.Show(this, Loc("Str_Err_SaveFailed") + "\n" + ex.Message,
                        "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                SetStatus(Loc("Str_St_DocInfoUpdated"));
            }
        }

        private async void Merge_Click(object sender, RoutedEventArgs e)
            => await MergeAtIndex(null);

        private async Task MergeAtIndex(int? insertionIndex)
        {
            if (_doc is null) { KillerDialog.Show(this, Loc("Str_Msg_OpenFirst")); return; }
            if (!EnsureEditable()) return;
            var dlg = new Controls.FileDialog(Controls.FileDialogMode.Open)
                          { Filter = Loc("Str_Filter_Pdf") + "|*.pdf", Title = Loc("Str_Dlg_SelectMerge"), Multiselect = true };
            if (dlg.ShowDialog(this) != true) return;
            // Checking a damaged file copies its pages, so it runs off the UI thread behind the busy
            // overlay, and the import goes only into the document the merge was started on.
            var isTargetCurrent = CaptureDocumentOrigin();
            try
            {
                var imports = new List<PdfEngineIntegration.ImportedDocument>();
                foreach (var file in dlg.FileNames)
                {
                    try
                    {
                        string importPath = file;
                        IReadOnlyList<KillerPdf.Engine.Documents.PdfPageInformation> pages;
                        try
                        {
                            pages = await RunBusyAsync(() =>
                            {
                                PdfEngineIntegration.ValidateImportable(file);
                                return PdfEngineIntegration.ReadPageInformation(file);
                            });
                        }
                        catch (Exception ex) when (PdfEngineIntegration.IsParserDamage(ex))
                        {
                            // Only damage a repair can mend is offered one; a file the merge refuses for
                            // another reason keeps its own message.
                            (string? repaired, bool declined) = await RepairPdfForImportAsync(file);
                            if (repaired is null)
                            {
                                SetStatus(Loc(declined ? "Str_Drop_MergeCanceled" : "Str_Drop_RepairFailed"));
                                return;
                            }
                            importPath = repaired;
                            pages = await RunBusyAsync(() =>
                            {
                                PdfEngineIntegration.ValidateDocument(importPath);
                                return PdfEngineIntegration.ReadPageInformation(importPath);
                            });
                        }
                        imports.Add(new PdfEngineIntegration.ImportedDocument(importPath,
                            [.. pages.Select(page => page.Rotation)], file));
                    }
                    // A file with no pages already says so in plain words and names itself.
                    catch (Exception ex) when (PdfEngineIntegration.IsImportFailure(ex)
                        && ex is not PdfEngineIntegration.ImportNoPagesException)
                    {
                        throw PdfEngineIntegration.WithImportSource(file, ex);
                    }
                }
                if (_doc is null || !isTargetCurrent())
                {
                    SetStatus(Loc("Str_Merge_TargetChanged"));
                    return;
                }
                int insertAt = insertionIndex ?? _doc.PageCount;
                int importedPages = imports.Sum(import => import.PageRotations.Count);
                ReloadWithRemappedAnnotations(
                    annotations => PageAnnotationInsertion.Shift(annotations, insertAt, importedPages),
                    documentUndo => SaveTempAndReload(
                        keepAnnotations: true,
                        finalizeSavedFile: path =>
                            PdfEngineIntegration.InsertDocuments(path, imports, insertAt),
                        remapRotations: rotations =>
                            PdfEngineIntegration.RemapRotationsAfterDocumentInsertion(
                                rotations, imports, insertAt),
                        selectedPageAfterReload: insertionIndex,
                        documentUndo: documentUndo));
                SetStatus(string.Format(Loc("Str_St_Merged"), dlg.FileNames.Length, _doc?.PageCount));
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, Loc("Str_Err_MergeFailed") + "\n" + ex.Message, "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ============================================================
        // Adobe page-size guard
        // ============================================================

        // Adobe Reader only displays pages whose sides are 3-14400 points; anything outside
        // that range shows "The dimensions of this page are out-of-range" and renders blank.
        // Such pages usually come from images with broken DPI metadata turned into a PDF (by
        // KillerPDF 1.6.1 and earlier, or by other tools). PDFium renders any size, so the
        // file looks normal in KillerPDF and only fails in Adobe.
        // Min/MaxAdobePageDim live in Services/PdfImport.cs (shared with the image importer).

        // Called at the top of every user-facing save. If any page is outside Adobe's supported
        // range, offers a proportional rescale (content, page boxes, and annotations all scale
        // by the same factor, so pages look identical at their new size). Declining saves as-is.
        private void OfferRescaleOutOfRangePages()
        {
            if (_doc is null) return;
            PdfEngineDocumentSession session = EnsureEngineDocumentSession();
            IReadOnlyList<int> pages = PdfPageDimensionNormalizer.FindPagesOutsideRange(
                session.Document, PdfImport.MinAdobePageDim, PdfImport.MaxAdobePageDim);
            if (pages.Count == 0) return;
            var res = KillerDialog.Show(this, string.Format(Loc("Str_Dlg_PageOutOfRange"), pages.Count),
                "KillerPDF", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (res != MessageBoxResult.Yes) return;
            // Every save calls this before its own try block, so a failed rescale must not escape it.
            try
            {
                SaveTempAndReload(keepAnnotations: true, preserveZoom: true,
                    finalizeSavedFile: path => PdfEngineIntegration.NormalizePageDimensions(
                        path, pages, PdfImport.MinAdobePageDim, PdfImport.MaxAdobePageDim));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                KillerDialog.Show(this, string.Format(Loc("Str_Pages_Failed"), ex.Message),
                    "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // A save rewrites the file, which invalidates any digital signature it carries, so the
        // save clears the dead signature values. Ask before that happens to a signed file on disk.
        private bool ConfirmSignatureRemoval(string destinationPath)
        {
            bool signed;
            try
            {
                signed = System.IO.File.Exists(destinationPath)
                    && PdfEngineIntegration.HasSignedSignatures(destinationPath);
            }
            catch
            {
                signed = false;   // an unreadable destination is reported by the save itself
            }
            if (!signed) return true;
            return KillerDialog.Show(this,
                string.Format(Loc("Str_Dlg_SaveRemovesSignature"), System.IO.Path.GetFileName(destinationPath)),
                Loc("Str_Dlg_AppTitle"), MessageBoxButton.OKCancel, MessageBoxImage.Warning,
                defaultResult: MessageBoxResult.Cancel) == MessageBoxResult.OK;
        }

        // A form whose author restricts editing but allows filling saves its field values as Acrobat
        // does: into the protected file itself, which keeps its encryption and permissions. Anything
        // beyond field values (markup, stamps, redaction marks) still needs the owner password.
        private bool CanSaveRestrictedFormFill() =>
            IsOwnerRestricted && !_restrictedByCertificate && !string.IsNullOrEmpty(_currentFile)
            && !_annotations.Values.Any(list => list.Count > 0) && !HasActiveStamps
            && ActiveViewer.RedactionMarkCountExt == 0
            && _restrictedFormFillable;

        private void SaveRestrictedFormFill(string destination)
        {
            CommitActiveTextBox();
            // A viewing copy has no encryption left, so the values go into the encrypted file behind it.
            string source = ViewingCopySources.GetValueOrDefault(_currentFile!, _currentFile!);
            bool viewingCopy = !string.Equals(source, _currentFile, StringComparison.OrdinalIgnoreCase);
            OpenedProtection protection = _protection;
            string filled = App.MakeTempFile("filled");
            // Reopening the original after a failed replace clears the form state, so the typed values
            // are kept aside and put back, as UnlockOwnerRestrictions carries them across its reopen.
            bool wasDirty = _isDirty;
            var textValues = new Dictionary<string, string>(_formTextValues);
            var choiceValues = new Dictionary<string, string>(_formChoiceValues);
            var checkValues = new Dictionary<string, bool>(_formCheckValues);
            var radioValues = new Dictionary<string, string>(_formRadioValues);
            var fontSizes = new Dictionary<string, double>(_formFontSizes);
            IReadOnlyCollection<string> kept;
            try
            {
                kept = PdfEngineIntegration.FillRestrictedForm(source, filled, ActiveViewer.SnapshotFormValuesForPrint(), protection);
                CloseEngineDocumentSession();
                _doc?.Close();
                _doc = null;
                PdfEngineIntegration.ReplaceFileAtomically(filled, destination);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                KillerDialog.Show(this, Loc("Str_Err_SaveFailed") + "\n" + ex.Message, "KillerPDF",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                // Still read-only on the unchanged original, with the typed values kept.
                if (_doc is null)
                {
                    if (viewingCopy)
                    {
                        OpenViewingCopy(source, _originalFile ?? source, protection);
                        CarryOverlays();
                        FinalizeAsyncOpen();
                    }
                    else RestoreReadOnlyOriginal(source, _originalFile ?? source, CarryOverlays, protection);
                }
                return;
            }
            // The saved values now live in the file, so it reopens read-only with nothing pending.
            if (viewingCopy)
            {
                OpenViewingCopy(destination, destination, protection);
                FinalizeAsyncOpen();
            }
            else RestoreReadOnlyOriginal(destination, destination, () => { }, protection);
            _loadedRevision = Services.FileRevision.Capture(destination);
            SetStatus(WithFieldsThatKeptTheirValue(string.Format(Loc("Str_St_Saved"), System.IO.Path.GetFileName(destination)), kept));

            void CarryOverlays()
            {
                foreach (var pair in textValues) _formTextValues[pair.Key] = pair.Value;
                foreach (var pair in choiceValues) _formChoiceValues[pair.Key] = pair.Value;
                foreach (var pair in checkValues) _formCheckValues[pair.Key] = pair.Value;
                foreach (var pair in radioValues) _formRadioValues[pair.Key] = pair.Value;
                foreach (var pair in fontSizes) _formFontSizes[pair.Key] = pair.Value;
                if (wasDirty) MarkDirty(true);
            }
        }

        private void SaveInPlace() => SaveInPlace(otherPaneConfirmed: false);

        // The same file can be open in both panes as two independent copies, so every save path
        // (Ctrl+S, the menu, tab close, quit and the tools that save first) warns before this copy
        // overwrites the other pane's unsaved edits. A canceled warning leaves this copy dirty,
        // which every caller already reads as a canceled save.
        private void SaveInPlace(bool otherPaneConfirmed, bool removeProtection = false)
        {
            if (_doc is null) { KillerDialog.Show(this, Loc("Str_Msg_OpenFirst")); return; }
            if (!otherPaneConfirmed && !string.IsNullOrEmpty(_originalFile) && OtherPaneHasDirtyCopyOf(_originalFile)
                && KillerDialog.Show(this,
                    string.Format(Loc("Str_Dlg_SaveOtherPaneDirty"), System.IO.Path.GetFileName(_originalFile)),
                    Loc("Str_Dlg_AppTitle"), MessageBoxButton.OKCancel, MessageBoxImage.Warning,
                    defaultResult: MessageBoxResult.Cancel) != MessageBoxResult.OK)
                return;
            if (!removeProtection && CanSaveRestrictedFormFill() && !string.IsNullOrEmpty(_originalFile))
            {
                // The same three answers as the regular save below: Save As opens the dialog.
                if (Services.FileRevision.ConflictsWith(_loadedRevision, _originalFile!))
                {
                    var choice = KillerDialog.AskToSave(this,
                        string.Format(Loc("Str_Dlg_SaveConflict"), System.IO.Path.GetFileName(_originalFile)),
                        Loc("Str_Btn_Replace"), Loc("Str_KS_SaveAs"), Loc("Str_Btn_CancelDlg"));
                    if (choice == MessageBoxResult.No) { SaveAs_Click(this, new RoutedEventArgs()); return; }
                    if (choice != MessageBoxResult.Yes) return;
                }
                SaveRestrictedFormFill(_originalFile!);
                return;
            }
            if (!EnsureEditable() || !EnsureProtectionReadable()) return;
            // A save keeps the file's encryption unless this is the command that takes it off.
            OpenedProtection publishUnder = removeProtection ? OpenedProtection.None : _protection;
            // Save back to the user's real file. After a page edit (crop/rotate) _currentFile is a
            // temp working copy, so the real path is kept in _originalFile. If there is no real path
            // (e.g. a repaired temp-backed open), fall back to Save As.
            if (string.IsNullOrEmpty(_originalFile)) { SaveAsWithName(null, removeProtection: removeProtection); return; }
            // Another tab, pane or program may have saved over the file since this copy read it
            // (Ctrl+S, the menu, tab close and quit all land here), so never replace that silently.
            if (Services.FileRevision.ConflictsWith(_loadedRevision, _originalFile!))
            {
                var conflict = KillerDialog.AskToSave(this,
                    string.Format(Loc("Str_Dlg_SaveConflict"), System.IO.Path.GetFileName(_originalFile)),
                    Loc("Str_Btn_Replace"), Loc("Str_KS_SaveAs"), Loc("Str_Btn_CancelDlg"));
                if (conflict == MessageBoxResult.No) { SaveAsWithName(null, removeProtection: removeProtection); return; }
                if (conflict != MessageBoxResult.Yes) return;
            }
            if (TrySaveCertifiedFormFill(_originalFile!)) return;
            if (!ConfirmSignatureRemoval(_originalFile!)) return;
            CommitActiveTextBox();
            OfferRescaleOutOfRangePages();   // Adobe page-size guard
            string saveTarget = _originalFile!;
            // Drop the immutable engine view before replacing the working file. It reopens lazily
            // on the next link or form render and therefore cannot expose stale document state.
            CloseEngineDocumentSession();
            bool keptProtection;
            try
            {
                bool hasAnnotations = _annotations.Values.Any(list => list.Count > 0);
                Dictionary<int, int> finalRotations = SnapshotPageRotations();
                if (hasAnnotations || HasActiveStamps)   // #147: stamps alone must still burn
                {
                    // Finish the edited copy before touching the destination. Keep a clean
                    // working copy so future saves do not burn the same markup twice.
                    var tempClean = App.MakeTempFile("clean");
                    _doc.Save(tempClean);
                    PdfEngineIntegration.RepairHarmlessSaveArtifacts(tempClean);
                    PdfEngineIntegration.StripLinkAppearances(tempClean);
                    var tempEdited = App.MakeTempFile("edited");
                    System.IO.File.Copy(tempClean, tempEdited, true);
                    PdfEngineIntegration.ClearInvalidatedSignatures(tempEdited);
                    PdfEngineBurn.Burn(tempEdited, _annotations, _renderDims,
                        _docStampSpec, null, _pageRotations, measurementsAsAnnotations: true);
                    PdfEngineIntegration.StripLinkAppearances(tempEdited);
                    WriteFormValuesToDocument(tempEdited);
                    WriteRedactionMarksToDocument(tempEdited);
                    WriteMeasurementsToDocument(tempEdited);
                    PdfEngineIntegration.ApplyPageRotations(tempEdited, finalRotations);
                    var completeSource = CompleteDocumentSource.Capture(tempEdited);
                    // The working copy reopens before the destination changes: a failed reopen then
                    // leaves the file on disk untouched and the tab on its current document.
                    var reopened = ReopenCleanWorkingCopy(ref tempClean);
                    keptProtection = ProtectedDocumentFiles.Publish(tempEdited, saveTarget, publishUnder);
                    _completeDocumentSource = completeSource;
                    // Signing reads these bytes, since the working copy lacks what the save burned in.
                    _publishedFile = Services.PublishedFileStamp.Capture(saveTarget);
                    _loadedRevision = Services.FileRevision.Capture(saveTarget);
                    _doc.Close();
                    _doc = reopened;
                    _currentFile = tempClean;
                }
                else
                {
                    // Finish the saved copy before touching the destination.
                    var tempSaved = App.MakeTempFile("saved");
                    _doc.Save(tempSaved);
                    PdfEngineIntegration.RepairHarmlessSaveArtifacts(tempSaved);
                    PdfEngineIntegration.ClearInvalidatedSignatures(tempSaved);
                    PdfEngineIntegration.StripLinkAppearances(tempSaved);
                    WriteFormValuesToDocument(tempSaved);
                    WriteRedactionMarksToDocument(tempSaved);
                    WriteMeasurementsToDocument(tempSaved);
                    PdfEngineIntegration.ApplyPageRotations(tempSaved, finalRotations);
                    var completeSource = CompleteDocumentSource.Capture(tempSaved);
                    keptProtection = ProtectedDocumentFiles.Publish(tempSaved, saveTarget, publishUnder);
                    _completeDocumentSource = completeSource;
                    // Signing reads these bytes, since the working copy lacks what the save burned in.
                    _publishedFile = Services.PublishedFileStamp.Capture(saveTarget);
                    _loadedRevision = Services.FileRevision.Capture(saveTarget);
                }

                AcceptSavedCheckpoint();
                ReportSaved(saveTarget, keptProtection, "Str_St_Saved");
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, Loc("Str_Err_SaveFailed") + "\n" + ex.Message, "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // The tail of every save. A file that lost its protection says so and stops counting as
        // protected (#149), and a field that could not take a value is named with the save.
        private void ReportSaved(string path, bool keptProtection, string plainStatusKey)
        {
            string name = System.IO.Path.GetFileName(path);
            string status = string.Format(Loc(keptProtection ? "Str_St_SavedProtected"
                : _openedFromProtected ? "Str_St_SavedNoPassword" : plainStatusKey), name);
            if (!keptProtection && _openedFromProtected)
            {
                _openedFromProtected = false;
                _protection = OpenedProtection.None;
            }
            SetStatus(WithFieldsThatKeptTheirValue(status, ActiveViewer.FieldsThatKeptTheirValueRef));
        }

        // A save names each field that shares its name with one that took the value and kept its own.
        private string WithFieldsThatKeptTheirValue(string status, IReadOnlyCollection<string> kept) =>
            kept.Count > 0 ? status + " " + string.Format(Loc("Str_Form_TwinsKept"), string.Join(", ", kept)) : status;

        // The clean working copy a save or export continues from, with the repair fallback the
        // open path uses. Callers open it before closing the current document, so a failed reopen
        // leaves the tab on a usable document.
        private static PdfWorkingDocument ReopenCleanWorkingCopy(ref string tempClean)
        {
            try
            {
                return PdfWorkingDocument.Open(tempClean, null);
            }
            catch (Exception saveOpenEx) when (PdfImport.IsXRefException(saveOpenEx))
            {
                var fixedPath = App.MakeTempFile("savefixed");
                if (!PdfImport.TryImportRepairToPath(tempClean, fixedPath)
                    && !PdfEngineIntegration.TryCreateZeroRotationCopy(tempClean, fixedPath))
                    throw;
                tempClean = fixedPath;
                return PdfWorkingDocument.Open(tempClean, null);
            }
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null) { KillerDialog.Show(this, Loc("Str_Msg_OpenFirst")); return; }
            // No real path yet (repaired temp-backed open) -> go straight to Save As.
            if (string.IsNullOrEmpty(_originalFile)) { SaveAs_Click(sender, e); return; }
            var name = System.IO.Path.GetFileName(_originalFile);
            // The same file can be open in BOTH panes as two independent copies (2026-08-01),
            // each with its own annotations and undo stack. Overwriting from one pane
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
            var choice = KillerDialog.Show(this, string.Format(Loc("Str_Dlg_OverwriteMsg"), name), Loc("Str_Lbl_Save"),
                                           MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (choice == MessageBoxResult.Yes)      SaveInPlace(otherPaneConfirmed: true);
            else if (choice == MessageBoxResult.No)  SaveAs_Click(sender, e);
            // Cancel or closed: do nothing.
        }

        private void SaveAs_Click(object sender, RoutedEventArgs e) => SaveAsWithName(null);

        // Save As, offering the given file name in place of the current one when there is one.
        internal void SaveAsWithName(string? suggestedName, bool openCopy = false, bool removeProtection = false)
        {
            if (_doc is null || _currentFile is null) { KillerDialog.Show(this, Loc("Str_Msg_OpenFirst")); return; }
            bool restrictedFill = !removeProtection && CanSaveRestrictedFormFill();
            if (!restrictedFill && (!EnsureEditable() || !EnsureProtectionReadable())) return;
            OpenedProtection publishUnder = removeProtection ? OpenedProtection.None : _protection;
            CommitActiveTextBox();
            var dlg = new Controls.FileDialog(Controls.FileDialogMode.Save)
                          { Filter = Loc("Str_Filter_Pdf") + "|*.pdf", Title = Loc("Str_Dlg_SavePdfAs"),
                            CheckFileExists = false, CheckPathExists = true, RequireFilterExtension = true };
            // Seed the dialog from the last real save location. Guard every path call: on .NET Framework
            // Path.GetDirectoryName("") throws ArgumentException ("path is not of a legal form"), so a merged
            // or imported doc (where _originalFile is null) would crash Save before the dialog opened (#112).
            string? seed = _originalFile ?? _currentFile;
            try
            {
                if (!string.IsNullOrWhiteSpace(suggestedName))
                    dlg.FileName = suggestedName;
                else if (!string.IsNullOrWhiteSpace(_suggestedName))
                    dlg.FileName = _suggestedName;
                else if (!string.IsNullOrWhiteSpace(seed))
                    dlg.FileName = System.IO.Path.GetFileName(seed);
                if (!string.IsNullOrWhiteSpace(_originalFile))
                {
                    var seedDir = System.IO.Path.GetDirectoryName(_originalFile);
                    if (!string.IsNullOrEmpty(seedDir) && System.IO.Directory.Exists(seedDir))
                        dlg.InitialDirectory = seedDir;
                }
                else if (_suggestedName is not null)
                {
                    // An import with no saved home (a browser download) starts in Downloads.
                    var downloads = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                    if (System.IO.Directory.Exists(downloads)) dlg.InitialDirectory = downloads;
                }
            }
            catch { /* malformed seed path - just open the dialog with its defaults */ }
            if (dlg.ShowDialog(this) != true) return;
            if (App.IsCleanupOwnedPath(dlg.FileName))
            {
                KillerDialog.Show(this, Loc("Str_Err_SaveFailed") + "\n" + dlg.FileName, "KillerPDF",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            if (restrictedFill)
            {
                SaveRestrictedFormFill(dlg.FileName);
                return;
            }
            if (TrySaveCertifiedFormFill(dlg.FileName, openCopy, confirmOtherSignedTarget: true)) return;
            // A signed document with no changes is copied byte for byte, so the copy keeps valid
            // signatures. Any rewrite clears them, so say so first even for a new file (BF-A005).
            // The working bytes of a protected file are decrypted, so they are never that copy.
            string? unchanged = publishUnder.KeepsEncryption ? null : UnchangedDocumentBytes();
            bool sourceSigned;
            try { sourceSigned = PdfEngineIntegration.HasSignedSignatures(unchanged ?? _currentFile); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { sourceSigned = false; }
            if (sourceSigned && unchanged is not null)
            {
                try { SaveExactCopy(unchanged, dlg.FileName, openCopy); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    KillerDialog.Show(this, Loc("Str_Err_SaveFailed") + "\n" + ex.Message, "KillerPDF",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
                return;
            }
            if (sourceSigned && KillerDialog.Show(this,
                    string.Format(Loc("Str_Dlg_SaveAsClearsSignature"), System.IO.Path.GetFileName(dlg.FileName)),
                    Loc("Str_Dlg_AppTitle"), MessageBoxButton.OKCancel, MessageBoxImage.Warning,
                    defaultResult: MessageBoxResult.Cancel) != MessageBoxResult.OK)
                return;
            if (!sourceSigned && !ConfirmSignatureRemoval(dlg.FileName)) return;
            if (openCopy)
            {
                try { SaveCopyAndOpen(dlg.FileName); }
                catch (Exception ex)
                {
                    KillerDialog.Show(this, Loc("Str_Err_SaveFailed") + "\n" + ex.Message,
                        "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                return;
            }
            CloseEngineDocumentSession();
            OfferRescaleOutOfRangePages();   // Adobe page-size guard
            bool keptProtection;
            try
            {
                bool hasAnnotations = _annotations.Values.Any(list => list.Count > 0);
                Dictionary<int, int> finalRotations = SnapshotPageRotations();
                if (hasAnnotations || HasActiveStamps)   // #147: stamps alone must still burn
                {
                    var tempClean = App.MakeTempFile("clean");
                    _doc.Save(tempClean);
                    PdfEngineIntegration.RepairHarmlessSaveArtifacts(tempClean);
                    PdfEngineIntegration.StripLinkAppearances(tempClean);
                    var tempEdited = App.MakeTempFile("edited");
                    System.IO.File.Copy(tempClean, tempEdited, true);
                    PdfEngineIntegration.ClearInvalidatedSignatures(tempEdited);
                    PdfEngineBurn.Burn(tempEdited, _annotations, _renderDims,
                        _docStampSpec, null, _pageRotations, measurementsAsAnnotations: true);
                    PdfEngineIntegration.StripLinkAppearances(tempEdited);
                    WriteFormValuesToDocument(tempEdited);
                    WriteRedactionMarksToDocument(tempEdited);
                    WriteMeasurementsToDocument(tempEdited);
                    PdfEngineIntegration.ApplyPageRotations(tempEdited, finalRotations);
                    var completeSource = CompleteDocumentSource.Capture(tempEdited);
                    // The working copy reopens before the destination changes: a failed reopen then
                    // leaves the file on disk untouched and the tab on its current document.
                    var reopened = ReopenCleanWorkingCopy(ref tempClean);
                    keptProtection = ProtectedDocumentFiles.Publish(tempEdited, dlg.FileName, publishUnder);
                    _completeDocumentSource = completeSource;
                    // Signing reads these bytes, since the working copy lacks what the save burned in.
                    _publishedFile = Services.PublishedFileStamp.Capture(dlg.FileName);
                    _loadedRevision = Services.FileRevision.Capture(dlg.FileName);
                    _doc.Close();
                    _doc = reopened;
                    _currentFile = tempClean;
                    _originalFile = dlg.FileName;
                    _suggestedName = null;
                    FileNameLabel.Text = System.IO.Path.GetFileName(dlg.FileName);
                    AcceptSavedCheckpoint();
                    ReportSaved(dlg.FileName, keptProtection, "Str_St_SavedWithAnnots");
                }
                else
                {
                    // Finish the saved copy before touching the destination.
                    var tempSaved = App.MakeTempFile("saved");
                    _doc.Save(tempSaved);
                    PdfEngineIntegration.RepairHarmlessSaveArtifacts(tempSaved);
                    PdfEngineIntegration.ClearInvalidatedSignatures(tempSaved);
                    PdfEngineIntegration.StripLinkAppearances(tempSaved);
                    WriteFormValuesToDocument(tempSaved);
                    WriteRedactionMarksToDocument(tempSaved);
                    WriteMeasurementsToDocument(tempSaved);
                    PdfEngineIntegration.ApplyPageRotations(tempSaved, finalRotations);
                    var completeSource = CompleteDocumentSource.Capture(tempSaved);
                    keptProtection = ProtectedDocumentFiles.Publish(tempSaved, dlg.FileName, publishUnder);
                    _completeDocumentSource = completeSource;
                    // Signing reads these bytes, since the working copy lacks what the save burned in.
                    _publishedFile = Services.PublishedFileStamp.Capture(dlg.FileName);
                    _loadedRevision = Services.FileRevision.Capture(dlg.FileName);
                    _originalFile = dlg.FileName;
                    _suggestedName = null;
                    FileNameLabel.Text = System.IO.Path.GetFileName(dlg.FileName);
                    AcceptSavedCheckpoint();
                    ReportSaved(dlg.FileName, keptProtection, "Str_St_SavedTo");
                }
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, Loc("Str_Err_SaveFailed") + "\n" + ex.Message, "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // Publishes the exact bytes of an unchanged document, then treats the copy as this tab's
        // file (or opens it, for Save Copy). Nothing is rewritten, so signatures stay valid.
        private void SaveExactCopy(string sourcePath, string outputPath, bool openCopy,
            IReadOnlyCollection<string>? keptTheirValue = null)
        {
            PdfEngineIntegration.ReplaceFileAtomically(sourcePath, outputPath);
            if (openCopy)
            {
                var source = ActiveViewer;
                var other = ReferenceEquals(source, Viewer) ? ViewerB : Viewer;
                bool otherEmpty = !other.SessionsRef.Any(session => session.Doc is not null
                    || session.DeferredPath is not null || session.OriginalFile is not null);
                var target = _isSplit && otherEmpty ? other : source;
                FocusPane(target);
                target.OpenInNewTabExt(outputPath);
                return;
            }
            _originalFile = outputPath;
            _suggestedName = null;
            _publishedFile = Services.PublishedFileStamp.Capture(outputPath);
            _loadedRevision = Services.FileRevision.Capture(outputPath);
            FileNameLabel.Text = System.IO.Path.GetFileName(outputPath);
            AcceptSavedCheckpoint();
            SetStatus(WithFieldsThatKeptTheirValue(string.Format(Loc("Str_St_SavedTo"), System.IO.Path.GetFileName(outputPath)),
                keptTheirValue ?? []));
        }

        private void SaveCopyAndOpen(string outputPath)
        {
            var source = ActiveViewer;
            string snapshot = WriteSavedCopyToTemp();
            ProtectedDocumentFiles.Publish(snapshot, outputPath, _protection);
            var other = ReferenceEquals(source, Viewer) ? ViewerB : Viewer;
            bool otherEmpty = !other.SessionsRef.Any(session => session.Doc is not null
                || session.DeferredPath is not null || session.OriginalFile is not null);
            var target = _isSplit && otherEmpty ? other : source;
            FocusPane(target);
            target.OpenInNewTabExt(outputPath);
        }

        private Dictionary<int, int> SnapshotPageRotations()
        {
            var rotations = new Dictionary<int, int>(_pageRotations);
            if (_doc is not null) EnsureEngineDocumentSession().CaptureRotations(rotations);
            return rotations;
        }

        private async void SaveFlattened_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null || _currentFile is null) { KillerDialog.Show(this, Loc("Str_Msg_OpenFirst")); return; }
            if (!EnsureMayWrite(PdfOutput.Printable, continueAfterUnlock: true)) return;
            var dlg = new Controls.FileDialog(Controls.FileDialogMode.Save)
                          { Filter = Loc("Str_Filter_Pdf") + "|*.pdf", Title = Loc("Str_Dlg_SaveFlattened"),
                            CheckFileExists = false, CheckPathExists = true, RequireFilterExtension = true };
            if (dlg.ShowDialog(this) != true) return;
            var optionsDialog = new FlattenOptionsDialog(this);
            optionsDialog.ShowDialog();
            if (!optionsDialog.Confirmed) return;
            PdfRasterize.FlattenOptions flattenOptions = optionsDialog.Options;
            Border? overlay = null;
            bool operationStarted = false;
            CancellationToken ct = default;
            try
            {
                CommitActiveTextBox();
                CloseEngineDocumentSession();
                OfferRescaleOutOfRangePages();   // Adobe page-size guard (pageDims below must be in range)
                string outputPath = dlg.FileName;

                // The overlay and its Cancel come first: writing the copy below blocks the window, on a
                // large file for seconds, and the flatten reports its pages from page 0 on.
                ct = BeginCancellableOp(Loc("Str_Op_Flatten"));
                operationStarted = true;
                // Capture forms, annotations, and complete page rotations before rendering.
                // Saving this temporary copy leaves the editor's dirty state intact.
                string sourcePath = await BusyCue.ThenRun(
                    () => overlay = ShowFlattenProgress(_doc.PageCount, Loc("Str_Busy_FlattenPage"), () => _busyCts?.Cancel()),
                    async () => await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background),
                    () => WriteSavedCopyToTemp(forRasterization: true));
                if (ct.IsCancellationRequested) { SetStatus(Loc("Str_St_FlattenCanceled")); return; }
                Border shown = overlay!;

                PdfEngineDocumentSession engineSession = EnsureEngineDocumentSession();
                int pageCount = engineSession.PageCount;

                // Snapshot the viewed page dimensions (CropBox-aware, rotation-aware)
                // before going off-thread so the rebuilt raster pages keep their
                // on-screen orientation.
                var pageDims = new (double widthPt, double heightPt)[pageCount];
                for (int i = 0; i < pageCount; i++)
                {
                    pageDims[i] = engineSession.PhysicalPageSize(i, _pageRotations);
                }
                // Rasterize on a background thread - keeps the UI responsive. The core lives in
                // Services/PdfRasterize.cs; progress marshals back to the overlay here.
                var incomplete = new IncompletePageLog();
                await Task.Run(() => PdfRasterize.FlattenToPdf(sourcePath, pageCount, pageDims, outputPath,
                    flattenOptions,
                    (n, total) => Dispatcher.BeginInvoke(new Action(() => UpdateFlattenProgress(shown, n, total))),
                    ct, incomplete));

                if (ct.IsCancellationRequested) { SetStatus(Loc("Str_St_FlattenCanceled")); return; }
                SetStatus(string.Format(Loc("Str_St_FlattenedSaved"), System.IO.Path.GetFileName(outputPath)));
                WarnIncompletePages(incomplete);
            }
            catch (Exception ex)
            {
                try { KillerDialog.Show(this, Loc("Str_Err_FlattenFailed") + "\n" + ex.GetType().Name + ": " + ex.Message, "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error); }
                catch { /* dialog failed; overlay still removed in finally */ }
            }
            finally
            {
                try { if (overlay is not null) HideFlattenProgress(overlay); } catch { /* ensure overlay never leaks */ }
                if (operationStarted) EndCancellableOp(ct);
            }
        }

        // ---- Export pages as images (#132) ----
        // The CLI --to-image pipeline behind a GUI entry (Save dropdown). Burns pending
        // annotations + stamps into a temp render source first (same clean-copy dance as Save
        // Flattened, so future saves don't double-burn), then renders each selected page at the
        // chosen DPI and writes <base>-page-NNN.<ext> beside the base name the user picked.
        private async void ExportImages_Click(object sender, RoutedEventArgs e)
            => await ExportImagesFlow("");

        private async void ExportDocument_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null) { KillerDialog.Show(this, Loc("Str_Msg_OpenFirst")); return; }
            if (!EnsureEditable(continueAfterUnlock: false)) return;
            CommitActiveTextBox();
            Func<bool> sourceCurrent = CaptureDocumentOrigin();
            string? currentSource = _currentFile;
            string? originalSource = _originalFile;
            var options = new ExportDocumentDialog(this);
            options.ShowDialog();
            if (!options.Confirmed || !sourceCurrent()) return;
            // Pictures of the pages could be printed freely; everything else is content copied out.
            bool pageImages = options.OfficeMode == PdfOfficeExportMode.PageImages
                && options.Format is PdfStructuredExportFormat.WordDocument or PdfStructuredExportFormat.Presentation;
            if (!EnsureMayWrite(pageImages ? PdfOutput.Printable : PdfOutput.Data, continueAfterUnlock: true)) return;

            int pageCount = EnsureEngineDocumentSession().PageCount;
            List<int>? pages = null;
            if (options.Range.Length > 0)
            {
                pages = CliParsePageRange(options.Range, pageCount, out string rangeError);
                if (pages is null)
                {
                    KillerDialog.Show(this, rangeError.Length > 0 ? rangeError : Loc("Str_InvalidRange"),
                        "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            string extension = PdfStructuredExportBatchRunner.OutputName("document.pdf", options.Format);
            extension = System.IO.Path.GetExtension(extension);
            string description = options.Format switch
            {
                PdfStructuredExportFormat.WordDocument => Loc("Str_Filter_Word"),
                PdfStructuredExportFormat.Spreadsheet => Loc("Str_Filter_Excel"),
                PdfStructuredExportFormat.Presentation => Loc("Str_Filter_PowerPoint"),
                PdfStructuredExportFormat.Html => Loc("Str_Filter_Html"),
                PdfStructuredExportFormat.Markdown => Loc("Str_Filter_Markdown"),
                PdfStructuredExportFormat.PlainText => Loc("Str_Filter_Text"),
                PdfStructuredExportFormat.Json => Loc("Str_Filter_Json"),
                _ => Loc("Str_Filter_AllFiles")
            };
            var save = new Controls.FileDialog(Controls.FileDialogMode.Save)
            {
                Filter = $"{description}|*{extension}",
                Title = Loc("Str_ExportDoc_Suffix"),
                CheckFileExists = false,
                CheckPathExists = true,
                RequireFilterExtension = true,
                FileName = System.IO.Path.GetFileNameWithoutExtension(_originalFile ?? _currentFile ?? "document") + extension
            };
            if (save.ShowDialog(this) != true || !sourceCurrent()) return;
            string destination = System.IO.Path.GetFullPath(save.FileName);
            if (new[] { currentSource, originalSource }.Any(source => source is not null
                && string.Equals(System.IO.Path.GetFullPath(source), destination, StringComparison.OrdinalIgnoreCase)))
            {
                KillerDialog.Show(this, Loc("Str_Merge_SourceOutput"));
                return;
            }

            bool operationStarted = false;
            CancellationToken cancellation = default;
            string? snapshot = null;
            try
            {
                snapshot = WriteSavedCopyToTemp(
                    forRasterization: options.OfficeMode == PdfOfficeExportMode.PageImages, throwaway: true);
                // The saved copy holds the tab's own bytes, so it reads under the tab's protection.
                var document = PdfEngineIntegration.OpenForReading(System.IO.File.ReadAllBytes(snapshot), _protection);
                PdfStructuredExportReport losses = PdfStructuredExport.InspectLosses(document, options.Format, pages, officeMode: options.OfficeMode);
                if (!losses.IsLossless)
                {
                    if (!ConfirmExportLosses(losses)) return;
                }
                if (!sourceCurrent()) return;

                cancellation = BeginCancellableOp(Loc("Str_Op_Export"));
                operationStarted = true;
                byte[] output = await Task.Run(() => PdfStructuredExport.Export(
                    document, options.Format, pages, cancellationToken: cancellation, officeMode: options.OfficeMode), cancellation);
                if (cancellation.IsCancellationRequested || !sourceCurrent()) return;
                await AtomicFileOutput.WriteAsync(destination, output, cancellation);
                if (sourceCurrent()) SetStatus(string.Format(Loc("Str_ExportDoc_Done"), System.IO.Path.GetFileName(destination)));
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                KillerDialog.Show(this, Loc("Str_Err_ExportFailed") + "\n" + ex.Message,
                    "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                if (operationStarted) EndCancellableOp(cancellation);
                if (snapshot is not null)
                    try { System.IO.File.Delete(snapshot); } catch (System.IO.IOException) { }
            }
        }

        /// <summary>The image-export flow; presetRange scopes the dialog to specific pages
        /// (the Pages panel's "Export page as image", #207) while staying fully editable.</summary>
        private async Task ExportImagesFlow(string presetRange)
        {
            if (_doc is null || _currentFile is null) { KillerDialog.Show(this, Loc("Str_Msg_OpenFirst")); return; }
            // The images carry the page out of a restricted PDF, so they wait for the owner password.
            if (!EnsureMayWrite(PdfOutput.Printable)) return;
            CommitActiveTextBox();
            PdfEngineDocumentSession engineSession = EnsureEngineDocumentSession();
            int pageCount = engineSession.PageCount;
            int previewPage = Math.Max(0, Math.Min(PageList.SelectedIndex, pageCount - 1));
            var (pageWidthPoints, pageHeightPoints) =
                engineSession.PhysicalPageSize(previewPage, _pageRotations);

            var opts = new ExportImagesDialog(
                this, presetRange, pageWidthPoints, pageHeightPoints,
                engineSession.PageImagePixels(previewPage, _pageRotations));
            opts.ShowDialog();   // fade-close dialogs don't reliably return true; rely on Confirmed
            if (!opts.Confirmed) return;

            List<int>? selected = null;
            if (opts.Range.Length > 0)
            {
                selected = CliParsePageRange(opts.Range, pageCount, out string rangeErr);
                if (selected is null)
                {
                    KillerDialog.Show(this, rangeErr.Length > 0 ? rangeErr : Loc("Str_InvalidRange"),
                                      "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }
            selected ??= [.. Enumerable.Range(0, pageCount)];

            string ext = opts.Jpeg ? "jpg" : "png";
            var dlg = new Controls.FileDialog(Controls.FileDialogMode.Save)
            {
                Filter = Loc("Str_Filter_Images") + (opts.Jpeg ? "|*.jpg" : "|*.png"),
                Title  = Loc("Str_ExportImg_Suffix"),
                OverwritePrompt = false,
                CheckFileExists = false, CheckPathExists = true,
                FileName = System.IO.Path.GetFileNameWithoutExtension(_originalFile ?? _currentFile) + "." + ext,
            };
            if (dlg.ShowDialog(this) != true) return;
            string outDir   = System.IO.Path.GetDirectoryName(dlg.FileName) is { Length: > 0 } d2 ? d2 : ".";
            string baseName = System.IO.Path.GetFileNameWithoutExtension(dlg.FileName);
            int digits = Math.Max(3, pageCount.ToString().Length);
            string[] outputPaths = PdfRasterize.PlanPageImagePaths(_originalFile ?? _currentFile,
                selected, outDir, baseName, digits, opts.Jpeg);
            var approvedOverwrites = outputPaths.Where(System.IO.File.Exists).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (approvedOverwrites.Count > 0 && KillerDialog.Show(this,
                    string.Format(Loc("Str_Dlg_OverwriteMsg"), string.Join("\n", approvedOverwrites.Select(System.IO.Path.GetFileName))),
                    Loc("Str_ExportImg_Suffix"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            // The immutable output copy already carries the displayed page rotations.
            var rotSnapshot = new int[pageCount];

            bool jpeg   = opts.Jpeg;
            double dpi  = opts.Dpi;
            var pages   = selected;
            bool keepResolution = opts.KeepResolution;
            var rotations = new Dictionary<int, int>(_pageRotations);
            CancellationToken ct = BeginCancellableOp(Loc("Str_Op_Export"));
            using var hold = new OperationHold();
            _busyHold = hold;
            Border? overlay = null;
            string? sourcePath = null;
            try
            {
                // Writing the copy blocks the window, on a large file for a long time, so the overlay
                // shows first.
                sourcePath = await BusyCue.ThenRun(
                    () => overlay = ShowFlattenProgress(selected.Count, Loc("Str_Busy_ExportPage"), () => _busyCts?.Cancel()),
                    async () => await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background),
                    () => WriteSavedCopyToTemp(forRasterization: true, throwaway: true));
                string rendered = sourcePath;
                Border shown = overlay!;
                // The per-page render/encode/write core lives in Services/PdfRasterize.cs;
                // progress marshals back to the overlay here.
                var incomplete = new IncompletePageLog();
                // Reading each page's own resolution parses its content, so it waits behind the overlay.
                int written = await Task.Run(() =>
                {
                    Dictionary<int, double>? nativeDpi = keepResolution
                        ? engineSession.NativeDpis(pages, rotations, dpi, ct,
                            (n, total) => Dispatcher.BeginInvoke(new Action(() => UpdateFlattenProgress(shown, n, total))), hold)
                        : null;
                    return PdfRasterize.ExportPageImages(rendered, pages,
                        rotSnapshot, dpi, jpeg, outDir, baseName, digits,
                        (n, total) => Dispatcher.BeginInvoke(new Action(() => UpdateFlattenProgress(shown, n, total))),
                        ct, incomplete, approvedOverwrites, nativeDpi is null ? null : page => nativeDpi[page], hold);
                });
                string statusKey = PdfRasterize.ExportStatusKey(written, pages.Count, ct.IsCancellationRequested);
                SetStatus(statusKey == "Str_ExportImg_Done"
                    ? string.Format(Loc(statusKey), written, outDir)
                    : string.Format(Loc(statusKey), written, pages.Count, outDir));
                WarnIncompletePages(incomplete);
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, Loc("Str_Err_ExportFailed") + "\n" + ex.Message, "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                try { if (overlay is not null) HideFlattenProgress(overlay); } catch { /* ensure overlay never leaks */ }
                _busyHold = null;
                EndCancellableOp(ct);
                try { if (sourcePath is not null) System.IO.File.Delete(sourcePath); } catch (System.IO.IOException) { }
            }
        }

        // A page whose content could not all be drawn is exported as it appears on screen; the
        // finished job names those pages instead of refusing the whole export.
        private void WarnIncompletePages(IncompletePageLog incomplete)
        {
            IReadOnlyList<int> pages = incomplete.Pages;
            if (pages.Count == 0) return;
            KillerDialog.Show(this, string.Format(Loc("Str_Export_IncompletePages"), IncompletePageLog.Describe(pages)),
                "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // ---- flatten progress overlay helpers ----

        private Border ShowFlattenProgress(int pageCount, string progressFormat, Action? cancel = null)
        {
            var panel = Controls.BusyProgressPanel.Build(string.Format(progressFormat, 0, pageCount), progressFormat,
                Loc("Str_Btn_Cancel"), cancel, TryFindResource("SurfaceButton") as Style);

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
                    if (child is TextBlock tb && tb.Tag is string progressFormat)
                        tb.Text = string.Format(progressFormat, current, total);
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
            if (!TryAllowPrinting(out double printDpi)) return;

            // The print prep (annotation burn + doc reopen) runs synchronously on the UI thread and
            // freezes it for a moment; yield one render cycle first to keep the click responsive.
            // (Deeper fix - backgrounding the burn - is tracked separately.)
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() => RunPrintFlow(printDpi)));
        }

        // The print permission was read when the file opened and stays with the tab, because a PDF
        // whose owner forbids only printing still opens for editing and never counts as restricted.
        // The owner password lifts the limit for this document, and a file that allows only low
        // quality printing prints at the lower resolution without it. A certificate has no owner
        // password, so a recipient is never asked for one.
        private bool TryAllowPrinting(out double printDpi)
        {
            OpenedProtection protection = _protection;
            PrintAnswer answer = protection.AskToPrint(allowance => KillerDialog.PromptPassword(this,
                _originalFile ?? _currentFile ?? string.Empty,
                Loc(allowance == PrintAllowance.Forbidden ? "Str_Print_Restricted_Prompt" : "Str_Print_LowQuality_Prompt"),
                Loc("Str_Restricted_Unlock")));
            printDpi = answer.Dpi;
            if (!answer.MayPrint && !EnsureProtectionReadable()) return false;
            PrintNotice notice = PrintPermission.NoticeFor(answer,
                protection.Access.Role == PdfAccessRole.Recipient, protection.CanLift);
            switch (notice)
            {
                case PrintNotice.WrongPassword:
                    KillerDialog.Show(this, Loc("Str_Restricted_WrongPassword"), "KillerPDF",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    break;
                case PrintNotice.RefusedQuietly:
                    SetStatus(Loc("Str_Print_Refused"));
                    break;
                case PrintNotice.Refused or PrintNotice.RefusedCertificate:
                    KillerDialog.Show(this, Loc(notice == PrintNotice.RefusedCertificate ? "Str_Print_Refused_Certificate" : "Str_Print_Refused"),
                        "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Warning);
                    break;
            }
            return answer.MayPrint;
        }

        private async void RunPrintFlow(double printDpi)
        {
            if (_doc is null || _currentFile is null) return;
            var sourceCurrent = CaptureDocumentOrigin();
            int sourcePage = _currentPage;
            string tempClean = App.MakeTempFile("printclean");
            string printPath = App.MakeTempFile("print");
            int pageCount;
            double[] pageDipW, pageDipH;
            try
            {
                CommitActiveTextBox();
                var annotsSnap = _annotations.ToDictionary(kv => kv.Key,
                    kv => kv.Value.Select(annotation => CloneAnnotation(annotation)
                        ?? throw new NotSupportedException("The annotation cannot be captured for printing.")).ToList());
                var dimsSnap = new Dictionary<int, (int w, int h)>(_renderDims);
                var stampSnap = _docStampSpec?.Clone();
                var visualRotations = new Dictionary<int, int>(_pageRotations);
                var finalRotations = SnapshotPageRotations();
                var formSnap = ActiveViewer.SnapshotFormValuesForPrint();
                PdfEngineDocumentSession engineSession = EnsureEngineDocumentSession();
                pageCount = engineSession.PageCount;
                pageDipW = new double[pageCount];
                pageDipH = new double[pageCount];
                for (int i = 0; i < pageCount; i++)
                {
                    var (pw, ph) = engineSession.PhysicalPageSize(i, visualRotations);
                    pageDipW[i] = pw * 96.0 / 72.0;
                    pageDipH[i] = ph * 96.0 / 72.0;
                }

                // Capture every live editor value before yielding. The worker finishes an owned
                // saved copy using the same persistence components as Save and Save Flattened.
                _doc.Save(tempClean);
                WriteRedactionMarksToDocument(tempClean);
                WriteMeasurementsToDocument(tempClean);
                await Task.Run(() =>
                {
                    System.IO.File.Copy(tempClean, printPath, true);
                    // The print copy is deleted afterward, so a damaged signed or usage-rights file is readied
                    // for it instead of refused. The copy still holds its rights until they are cleared next.
                    PdfEngineIntegration.PrepareThrowawayCopy(printPath);
                    PdfEngineIntegration.ClearInvalidatedSignatures(printPath);
                    PdfEngineIntegration.StripLinkAppearances(printPath);
                    if (annotsSnap.Values.Any(list => list.Count > 0) || stampSnap is not null)
                        PdfEngineBurn.Burn(printPath, annotsSnap, dimsSnap, stampSnap, null,
                            visualRotations, forRasterization: true, measurementsAsAnnotations: true);
                    PdfEngineIntegration.StripLinkAppearances(printPath);
                    PdfEngineIntegration.ApplyFormValues(printPath, formSnap);
                    PdfEngineIntegration.ApplyPageRotations(printPath, finalRotations);
                });
            }
            catch (Exception ex)
            {
                DeletePrintTemp(printPath);
                KillerDialog.Show(this, Loc("Str_Err_PrintFailed") + "\n" + ex.Message,
                    "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            finally { DeletePrintTemp(tempClean); }

            if (!sourceCurrent())
            {
                DeletePrintTemp(printPath);
                SetStatus(Loc("Str_St_Canceled"));
                return;
            }

            // Open the preview window immediately. Pages rasterize on a background thread and
            // stream in via SetRenderedPage, so the window appears at once and the app stays
            // responsive on large files. WPF's OS PrintDialog can't show a preview, so KillerPDF
            // renders it and drives printing itself.
            string  renderPath = printPath;
            string? cleanup    = printPath;
            // Preview rasters are display-only (shown fit-to-pane in a Viewbox), so render them at a
            // modest budget that scales DOWN as the document grows - this keeps the preview's resident
            // bitmaps from ballooning on large files. The Print button re-renders the chosen pages at a
            // true 300 DPI on demand (PrintPreviewWindow.DoPrint), so output stays crisp (issue #83).
            int previewBox = pageCount <= 80 ? 1536 : pageCount <= 250 ? 1100 : 800;
            var preview = new PrintPreviewWindow(
                this, pageCount, pageDipW, pageDipH, sourcePage, renderPath, cleanup, printDpi);

            _ = Task.Run(() =>
            {
                try
                {
                    using var renderSession = PdfPageRenderSession.OpenEngineFirst(
                        renderPath, previewBox, previewBox, null, forPrinting: true);   // the plain print copy
                    // A page that cannot be drawn exactly still prints as shown; the preview names it.
                    var incomplete = new IncompletePageLog();
                    for (int i = 0; i < pageCount; i++)
                    {
                        if (preview.Canceled) return;
                        PdfRenderedPage page = renderSession.RenderPage(i, requireComplete: false);
                        incomplete.Note(i, page);
                        int w = page.Width;
                        int h = page.Height;
                        byte[] raw = page.Pixels;
                        byte[] png = BitmapHelpers.RenderToPng(raw, w, h);   // #141
                        BitmapSource src;
                        using (var ms = new MemoryStream(png))
                            src = BitmapFrame.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                        src.Freeze();   // frozen so it can cross back to the UI thread
                        int ci = i;
                        try { preview.Dispatcher.Invoke(() => preview.SetRenderedPage(ci, src, w, h)); }
                        catch { return; }   // window closed mid-render
                    }
                    if (!preview.Canceled)
                        try { preview.Dispatcher.Invoke(() => preview.FinishLoading(incomplete.Pages)); } catch { }
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
                try { KillerDialog.Show(this, Loc("Str_Err_PrintFailed") + "\n" + ex.GetType().Name + ": " + ex.Message, "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error); }
                catch { }
            }

            static void DeletePrintTemp(string? path)
            {
                if (path is null) return;
                try { System.IO.File.Delete(path); } catch { }
            }
        }
    }
}
