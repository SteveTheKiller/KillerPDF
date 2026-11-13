using System.IO;
using System.Windows;
using System.Windows.Controls;
using KillerPDF.Services;
using KillerPdf.Engine.Formats.Epub;

namespace KillerPDF.Shell;

public partial class MainWindow
{
    // EPUB and DjVu read and lay out on a pool thread behind the busy overlay, so a long book never
    // leaves the window Not Responding. Like the repair path, the tab that started the open owns the
    // result: it is finalized here, or rolled back when the open fails, is canceled or the tab closes.
    private async void OpenNativeFile(string path)
    {
        EpubLayoutOptions? epubOptions = null;
        try
        {
            if (NativeOpen.IsEpubPath(path))
            {
                // Localized text and the fallback face are read here; layout itself runs off the UI thread.
                var face = PdfEngineIntegration.LoadTextFont("Segoe UI", "A", bold: false, italic: false)
                    ?? throw new NotSupportedException("Native EPUB fallback font is unavailable.");
                string unavailable = Loc("Str_Epub_ChapterUnavailable");
                epubOptions = new EpubLayoutOptions(800, 1000, face.Font)
                {
                    FontFaceResolver = PdfEngineIntegration.EpubFontFaceResolver(face.Family),
                    IsolateChapterFailures = true,
                    UnavailableChapterLabel = unavailable,
                    NoticeFallbackFamilies = NativePdfConversion.NoticeFallbackFamilies(face.Family, unavailable)
                };
            }
        }
        catch (Exception error)
        {
            KillerDialog.Show(this, string.Format(Loc("Str_Dlg_FailedOpen"), error.Message),
                Loc("Str_Dlg_AppTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        _asyncOpenPending = true;   // the synchronous open caller defers tab finalization to here
        var owner = AsyncOwner();
        var completion = owner.Session?.OpenCompletion;
        // Each open keeps its own cancellation, so a second open arriving meanwhile cannot strand this one.
        var cancel = new CancellationTokenSource();
        CancellationToken token = cancel.Token;
        string opening = string.Format(Loc("Str_Native_Opening"), Path.GetFileName(path));
        if (_busyCts is null)
        {
            _busyCts = cancel;
            _busyOpLabel = opening;
        }
        var busy = ShowBusyOverlay(opening);
        if (busy.Child is Panel panel)
        {
            var cancelButton = UiKit.Make(Loc("Str_Btn_Cancel"), accent: false);
            cancelButton.Name = "NativeOpenBusyCancel";
            cancelButton.Margin = new Thickness(0, 14, 0, 0);
            cancelButton.Click += (_, _) =>
            {
                try { cancel.Cancel(); } catch (ObjectDisposedException) { }
                cancelButton.IsEnabled = false;
            };
            panel.Children.Add(cancelButton);
        }
        // Each laid-out chapter updates the overlay, so a long book shows it is moving. The open returns at the first
        // chapter; reports from the background pagination after that are FollowNativePagination's to show.
        var busyText = (busy.Child as Panel)?.Children.OfType<TextBlock>().FirstOrDefault();
        bool overlayShown = true;
        var progress = new Progress<(int Done, int Total)>(report =>
        {
            if (busyText is not null && overlayShown && !token.IsCancellationRequested)
                busyText.Text = string.Format(Loc("Str_Native_OpeningChapter"), Path.GetFileName(path), report.Done, report.Total);
        });
        NativeWorkingDocument? opened = null;
        bool adopted = false;
        try
        {
            try
            {
                opened = await NativeOpen.OpenAsync(path, epubOptions, progress, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                HideBusyOverlay(busy);
                overlayShown = false;
                return;   // the rollback below reports the cancellation
            }
            HideBusyOverlay(busy);
            overlayShown = false;
            // Its tab was closed, or Cancel arrived after the last check inside the open.
            if (!ReclaimAsyncOwner(owner) || token.IsCancellationRequested
                || _windowResourcesReleased || Dispatcher.HasShutdownStarted)
                return;
            AdoptNativeDocument(path, opened);
            FinalizeAsyncOpen();
            adopted = true;
            if (opened.IsPaginating) FollowNativePagination(opened, Path.GetFileName(path));
        }
        catch (Exception error)
        {
            if (overlayShown) HideBusyOverlay(busy);
            if (ReclaimAsyncOwner(owner))
            {
                _asyncOpenPending = false;
                if (ReferenceEquals(ActiveViewer.NativeDocumentRef, opened))
                    ActiveViewer.NativeDocumentRef = null;
                KillerDialog.Show(this, string.Format(Loc("Str_Dlg_FailedOpen"), error.Message),
                    Loc("Str_Dlg_AppTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        finally
        {
            if (!adopted) opened?.Dispose();
            bool canceled = !adopted && token.IsCancellationRequested;
            if (ReferenceEquals(_busyCts, cancel)) _busyCts = null;
            cancel.Dispose();
            if (!adopted && ReclaimAsyncOwner(owner))
            {
                _asyncOpenPending = false;
                owner.Viewer.AbortPendingTabLoadExt(owner.Session!);
                // The rollback re-renders the previous tab and its status, so the cancellation is said last.
                if (canceled) SetStatus(Loc("Str_St_Canceled"));
            }
            completion?.TrySetResult(adopted);
        }
    }

    // The UI-thread half of a native open: the tab's previous document goes only once the new one is ready.
    private void AdoptNativeDocument(string path, NativeWorkingDocument opened)
    {
        _doc?.Close();
        _doc = null;
        ActiveViewer.NativeDocumentRef?.Close();
        ActiveViewer.NativeDocumentRef = opened;
        ActiveViewer.FollowNativeNoticesExt(opened);
        CloseEngineDocumentSession();
        _currentFile = path;
        _originalFile = path;
        _publishedFile = null;
        _completeDocumentSource = null;
        _loadedRevision = null;
        _suggestedName = null;
        FileNameLabel.Text = Path.GetFileName(path);
        _openedFromProtected = false;
        _restrictedByCertificate = false;
        _restrictedFormFillable = false;
        _protection = OpenedProtection.None;
        _pageRotations.Clear();
        _renderDims.Clear();
        _navBack.Clear();
        _navForward.Clear();
        _undoStack.Clear();
        _redoStack.Clear();
        _annotations.Clear();
        _continuousLinks.Clear();
        Search.ClearPageResults();
        _docStampSpec = null;
        ActiveViewer.RestoreProofPreferencesExt(path, opened.ContentIdentity);
        AcceptSavedCheckpoint();
        _viewMode = Enum.TryParse<ViewMode>(App.GetSetting("ViewMode"), out var preferredView)
            ? preferredView : ViewMode.Continuous;
        BootstrapDocumentView(0, autoFit: true);
        // A book still being paginated reports its pages so far as "340+"; FollowNativePagination corrects the count.
        string status = OpenedStatus(Path.GetFileName(path), opened);
        if (ActiveViewer.ProofPreferenceNoticeKeyExt is { } notice) status += " " + Loc(notice);
        SetStatus(status);
        _paginatingOpenStatus = opened.IsPaginating ? (opened, StatusText.Text) : default;
        SyncSidebarToDocState(hasDoc: true, startup: false);
        ActiveViewer.CompleteTabLoadExt();
    }
}
