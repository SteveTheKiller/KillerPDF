using System.IO;
using System.Windows;

namespace KillerPDF
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
        public void OpenFromExternal(string? path)
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path)) ActiveViewer.OpenInNewTabExt(path!);
        }

        public void RestoreAndActivate()
        {
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            // Briefly toggle Topmost to pull the window in front without keeping it pinned.
            Topmost = true;
            Topmost = false;
            Focus();
        }
    }
}
