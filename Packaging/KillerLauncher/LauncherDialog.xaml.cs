using System.Windows;
using System.Windows.Input;

namespace KillerLauncher
{
    /// <summary>
    /// Themed message box for the portable launcher, so a start failure looks like the rest of
    /// KillerPDF instead of a plain Windows message box.
    /// </summary>
    public partial class LauncherDialog : Window
    {
        private LauncherDialog(string message)
        {
            InitializeComponent();
            TaskbarIdentity.Track(this);
            MessageText.Text = message;
            GrainLayer.Background = InstallerWizard.CreateGrain();
        }

        /// <summary>Shows an error with a single OK button and waits for it to close.</summary>
        internal static void ShowError(string message)
        {
            bool ownsApplication = Application.Current == null;
            if (ownsApplication) new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            new LauncherDialog(message).ShowDialog();
            if (ownsApplication) Application.Current?.Shutdown();
        }

        private void Ok_Click(object sender, RoutedEventArgs e) => Close();

        private void Frame_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed) DragMove();
        }
    }
}
