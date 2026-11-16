using System.IO;
using System.Windows;
using System.Windows.Input;

namespace KillerPDF.Controls
{
    // New folder (#498): the button, or Ctrl+Shift+N, opens a name box under it with the first free "New folder" name.
    // Enter creates the folder in the current folder and opens it, so a Save As can go straight into it; Esc cancels.
    public partial class FileDialog
    {
        private void NewFolder_Click(object sender, RoutedEventArgs e) => OpenNewFolderBox();

        private void OpenNewFolderBox()
        {
            if (_currentDir.Length == 0 || !Directory.Exists(_currentDir)) return;
            NewFolderBox.Text = FolderNameRule.FirstFree(Loc("Str_FD_NewFolder"), FolderExists);
            NewFolderError.Visibility = Visibility.Collapsed;
            NewFolderPopup.IsOpen = true;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() =>
            {
                NewFolderBox.Focus();
                NewFolderBox.SelectAll();
            }));
        }

        private bool FolderExists(string name)
        {
            try { return Directory.Exists(Path.Combine(_currentDir, name)) || File.Exists(Path.Combine(_currentDir, name)); }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { return false; }
        }

        private void NewFolderBox_KeyDown(object sender, KeyEventArgs e)
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
            FolderNameProblem problem = FolderNameRule.Check(NewFolderBox.Text, FolderExists);
            if (problem != FolderNameProblem.None)
            {
                ShowNewFolderError(problem switch
                {
                    FolderNameProblem.Empty => "Str_FD_FolderEmpty",
                    FolderNameProblem.InvalidCharacters => "Str_FD_FolderInvalid",
                    FolderNameProblem.Reserved => "Str_FD_FolderReserved",
                    FolderNameProblem.TrailingDotOrSpace => "Str_FD_FolderTrailing",
                    FolderNameProblem.TooLong => "Str_FD_FolderTooLong",
                    _ => "Str_FD_FolderExists",
                });
                return;
            }
            string created = Path.Combine(_currentDir, FolderNameRule.Clean(NewFolderBox.Text));
            try { Directory.CreateDirectory(created); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                ShowNewFolderError("Str_FD_FolderFailed", ex.Message);
                return;
            }
            NewFolderPopup.IsOpen = false;
            NavigateTo(created);
        }

        private void ShowNewFolderError(string key, string? detail = null)
        {
            NewFolderError.Text = detail is null ? Loc(key) : Loc(key) + " " + detail;
            NewFolderError.Visibility = Visibility.Visible;
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
