using System;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace KillerPDF.Controls
{
    public partial class FileDialog
    {
        private readonly PickerListingWork _listingWork = new PickerListingWork();
        private CancellationTokenSource? _listingRows;
        private bool _listingClosed;
        private string? _listingError;
        // Bumped on every navigation, so a slow tree reveal for an older folder stops instead of
        // selecting it over the folder now shown.
        private long _navigationVersion;
        // Where a recovery walk gave up inside a tree that is gone. _currentDir is still the folder
        // that vanished, so Up carries on from here; a listing that works, or a folder the user asks
        // for, clears it.
        private string? _walkStopped;
        public Func<string, bool, CancellationToken, PickerListingResult> DirectoryListingReader { get; set; } = PickerDirectoryListing.Read;
        public Task DirectoryListingTask { get; private set; } = Task.CompletedTask;
        public bool IsDirectoryListingBusy { get; private set; }
        public bool IsDirectoryListingAvailable { get; private set; }
        public bool IsDirectoryListingComplete { get; private set; }

        // How many times in a row a failed listing may send the dialog to another folder before it stops
        // and shows the error, so folders that keep failing cannot send it back and forth.
        private const int MaxRecoveries = 8;

        public Task NavigateDirectoryAsync(string directory) => NavigateCoreAsync(directory, 0);

        private Task NavigateCoreAsync(string directory, int attempt)
        {
            if (_listingClosed || string.IsNullOrWhiteSpace(directory)) return Task.CompletedTask;
            if (attempt == 0) _walkStopped = null;
            _listingRows?.Cancel();
            long version = ++_navigationVersion;
            UpdateImagePreview(null);
            IsDirectoryListingBusy = true;
            IsDirectoryListingAvailable = false;
            IsDirectoryListingComplete = false;
            AcceptButton.IsEnabled = false;
            UpButton.IsEnabled = false;
            _raw.Clear();
            MutateListingRows(Entries.Clear);
            SelMeta.Text = "\u2026";
            bool showHidden = _showHidden;
            var reader = DirectoryListingReader;
            return DirectoryListingTask = _listingWork.RunAsync(token => reader(directory, showHidden, token), async (result, token) =>
            {
                if (!result.Exists)
                {
                    IsDirectoryListingBusy = false;
                    SelMeta.Text = Loc("Str_Dlg_NoSuchFolder");
                    // A bad typed path goes back to the last folder that listed. _currentDir only
                    // changes after a listing succeeds, so it is always that folder. When that
                    // folder is the one that vanished, the nearest parent takes its place.
                    RecoverListing(directory, vanished: true, attempt);
                    return;
                }
                IsDirectoryListingComplete = result.Complete && result.Entries.Count <= PickerDirectoryListing.MaximumEntries;
                _listingError = result.Error ?? (IsDirectoryListingComplete ? null : new Win32Exception(234).Message);
                MutateListingRows(() =>
                {
                    _currentDir = directory;
                    _walkStopped = null;
                    PathBox.Text = directory;
                    _raw.AddRange(result.Entries.Take(PickerDirectoryListing.MaximumEntries));
                });
                await ApplySortAsync();
                token.ThrowIfCancellationRequested();
                IsDirectoryListingBusy = false;
                IsDirectoryListingAvailable = true;
                RefreshAcceptEnabled();
                UpButton.IsEnabled = System.IO.Directory.GetParent(directory) != null;
                UpdateInfoSummary();
                MutateListingRows(SyncPlacesSelection);
                RecordRecent(directory);
                _ = RevealInTree(directory, version);
            }, error =>
            {
                IsDirectoryListingBusy = false;
                _listingError = error.Message;
                SelMeta.Text = error.Message;
                RecoverListing(directory, vanished: false, attempt);
            });
        }

        // Puts the dialog back on a folder it can use after a listing failed, so Up and Accept do
        // not stay off behind a failed or vanished folder. A path the user asked for goes back to the
        // last folder that listed; from there, and from any folder that vanished, it walks up to the
        // nearest parent that lists. Each hop is counted, and past MaxRecoveries the error stays.
        private void RecoverListing(string failed, bool vanished, int attempt)
        {
            if (_listingClosed) return;
            if (_currentDir.Length == 0)
            {
                // Still starting up: the next start folder takes over.
                string? next = attempt < MaxRecoveries ? NextStartCandidate() : null;
                if (next != null) RecoverTo(next, attempt + 1);
                else SettleListingError(failed);
                return;
            }
            bool sameFolder = string.Equals(_currentDir, failed, StringComparison.OrdinalIgnoreCase);
            string? target = null;
            if (attempt < MaxRecoveries)
            {
                if (vanished && (sameFolder || attempt > 0)) target = ParentOf(failed);
                else if (attempt == 0 && !sameFolder) target = _currentDir;
            }
            if (target != null) RecoverTo(target, attempt + 1);
            else SettleListingError(failed);
        }

        private static string? ParentOf(string folder)
        {
            try { return System.IO.Directory.GetParent(folder)?.FullName; }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or System.IO.PathTooLongException) { return null; }
        }

        // The error stays on screen, the path box and Cancel work, and Up walks on from the folder the
        // walk stopped at, so each press carries it further. Accept follows the listing, which is not available.
        private void SettleListingError(string failed)
        {
            IsDirectoryListingBusy = false;
            RefreshAcceptEnabled();
            _walkStopped = failed;
            UpButton.IsEnabled = ParentOf(failed) != null;
        }

        private void CloseDirectoryListing()
        {
            if (_listingClosed) return;
            _listingClosed = true;
            _listingWork.Dispose();
            _listingRows?.Cancel();
            IsDirectoryListingBusy = false;
        }

        private async Task<bool> PublishListingRowsAsync(PickerEntry[] rows)
        {
            _listingRows?.Cancel();
            var request = _listingRows = new CancellationTokenSource();
            try
            {
                await PickerListingWork.PublishAsync(rows, () => MutateListingRows(Entries.Clear), row => MutateListingRows(() => Entries.Add(row)),
                    async () => { await Dispatcher.Yield(DispatcherPriority.Background); }, request.Token);
                return true;
            }
            catch (OperationCanceledException) { return false; }
            finally { if (ReferenceEquals(_listingRows, request)) _listingRows = null; request.Dispose(); }
        }

        private void MutateListingRows(Action change)
        {
            bool previous = _navigating;
            _navigating = true;
            try { change(); }
            finally { _navigating = previous; }
        }
    }
}
