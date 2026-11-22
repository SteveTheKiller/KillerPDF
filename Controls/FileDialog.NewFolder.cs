using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Input;

namespace KillerPDF.Controls
{
    // New folder (#498): the button, or Ctrl+Shift+N, opens a name box under it with the first free "New folder" name.
    // Enter creates the folder in the current folder and opens it, so a Save As can go straight into it; Esc cancels.
    // Every disk touch runs on the dialog's STA worker with the probe timeout, so a folder on a share that stopped
    // answering costs at most that wait and never freezes the window.
    public partial class FileDialog
    {
        // Bumped whenever the box opens or closes, so an answer that arrives after either is dropped.
        private int _newFolderRequest;
        private bool _newFolderBusy;

        // What the box asks the disk: whether a name is taken in a folder. Tests stand in a share that hangs.
        internal Func<string, string, bool> NewFolderProbe = FolderExists;
        internal TimeSpan NewFolderTimeout = ProbeTimeout;

        private void NewFolder_Click(object sender, RoutedEventArgs e) => OpenNewFolderBox();

        private async void OpenNewFolderBox()
        {
            string dir = _currentDir;
            if (dir.Length == 0) return;
            int request = ++_newFolderRequest;
            string? name = await FirstFreeFolderNameAsync(dir);
            if (name == null || request != _newFolderRequest || dir != _currentDir || !IsVisible) return;
            _newFolderBusy = false;
            NewFolderBox.IsReadOnly = false;
            NewFolderBox.Text = name;
            NewFolderError.Visibility = Visibility.Collapsed;
            NewFolderPopup.IsOpen = true;
            _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() =>
            {
                NewFolderBox.Focus();
                NewFolderBox.SelectAll();
            }));
        }

        // The first free "New folder" name, or null when the folder is gone. A folder that does not answer
        // still gets the plain name, and Enter then says why nothing could be created.
        internal async Task<string?> FirstFreeFolderNameAsync(string dir)
        {
            string baseName = Loc("Str_FD_NewFolder");
            var probe = NewFolderProbe;
            try { return await OnStaWorker(() => Directory.Exists(dir) ? FolderNameRule.FirstFree(baseName, candidate => probe(dir, candidate)) : null, NewFolderTimeout); }
            catch (TimeoutException) { return baseName; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return baseName; }
        }

        private static bool FolderExists(string dir, string name)
        {
            try { return Directory.Exists(Path.Combine(dir, name)) || File.Exists(Path.Combine(dir, name)); }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { return false; }
        }

        private void NewFolderPopup_Closed(object sender, EventArgs e)
        {
            _newFolderRequest++;
            _newFolderBusy = false;
            NewFolderBox.IsReadOnly = false;
        }

        private async void NewFolderBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                NewFolderPopup.IsOpen = false;
                NewFolderButton.Focus();
                e.Handled = true;
                return;
            }
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            if (_newFolderBusy) return;
            string dir = _currentDir;
            int request = _newFolderRequest;
            _newFolderBusy = true;
            NewFolderBox.IsReadOnly = true;
            var outcome = await CreateNewFolderAsync(dir, NewFolderBox.Text);
            // The box closed or the dialog moved on while the folder was being made, so there is nothing left to show.
            if (request != _newFolderRequest || dir != _currentDir || !NewFolderPopup.IsOpen) return;
            _newFolderBusy = false;
            NewFolderBox.IsReadOnly = false;
            if (!ShowNewFolderOutcome(outcome)) return;
            NewFolderPopup.IsOpen = false;
            NavigateTo(outcome.Created!);
        }

        // Checks the name against what is on disk, then makes the folder, all on the worker.
        internal async Task<(FolderNameProblem Problem, string? Created, string? Failure)> CreateNewFolderAsync(string dir, string typed)
        {
            var probe = NewFolderProbe;
            try { return await OnStaWorker(() => CreateNewFolder(dir, typed, probe), NewFolderTimeout); }
            catch (TimeoutException) { return (FolderNameProblem.None, null, new System.ComponentModel.Win32Exception(1460).Message); }
        }

        private static (FolderNameProblem Problem, string? Created, string? Failure) CreateNewFolder(string dir, string typed, Func<string, string, bool> probe)
        {
            FolderNameProblem problem = FolderNameRule.Check(typed, candidate => probe(dir, candidate));
            if (problem != FolderNameProblem.None) return (problem, null, null);
            string created = Path.Combine(dir, FolderNameRule.Clean(typed));
            try { Directory.CreateDirectory(created); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return (FolderNameProblem.None, null, ex.Message);
            }
            return (FolderNameProblem.None, created, null);
        }

        // Shows why nothing was created and returns false, or returns true when the folder is there to open.
        internal bool ShowNewFolderOutcome((FolderNameProblem Problem, string? Created, string? Failure) outcome)
        {
            if (outcome.Failure != null) { ShowNewFolderError("Str_FD_FolderFailed", outcome.Failure); return false; }
            if (outcome.Problem == FolderNameProblem.None) return true;
            ShowNewFolderError(outcome.Problem switch
            {
                FolderNameProblem.Empty => "Str_FD_FolderEmpty",
                FolderNameProblem.InvalidCharacters => "Str_FD_FolderInvalid",
                FolderNameProblem.Reserved => "Str_FD_FolderReserved",
                FolderNameProblem.TrailingDotOrSpace => "Str_FD_FolderTrailing",
                FolderNameProblem.TooLong => "Str_FD_FolderTooLong",
                _ => "Str_FD_FolderExists",
            });
            return false;
        }

        // The reason is announced as well as shown, because a screen reader would otherwise hear nothing after Enter.
        private void ShowNewFolderError(string key, string? detail = null)
        {
            NewFolderError.Text = detail is null ? Loc(key) : Loc(key) + " " + detail;
            NewFolderError.Visibility = Visibility.Visible;
            AutomationProperties.SetLiveSetting(NewFolderError, AutomationLiveSetting.Assertive);
            UIElementAutomationPeer.CreatePeerForElement(NewFolderError)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
            NewFolderBox.Focus();
            NewFolderBox.SelectAll();
        }

        // Ctrl+Shift+N opens the name box, as it does in File Explorer.
        private bool HandleNewFolderKey(KeyEventArgs e)
        {
            if (e.Key != Key.N || Keyboard.Modifiers != (ModifierKeys.Control | ModifierKeys.Shift)) return false;
            OpenNewFolderBox();
            return true;
        }
    }
}
