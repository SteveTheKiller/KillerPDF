using System.Windows;
using System.Windows.Input;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;

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
            InstallerWizard.ApplyLanguageFonts(this);
            InitializeComponent();
            VersionLabel.Text = System.Diagnostics.FileVersionInfo.GetVersionInfo(System.Reflection.Assembly.GetExecutingAssembly().Location).ProductVersion?.Split('+')[0] ?? "";
            TaskbarIdentity.Track(this);
            MessageText.Text = message;
            GrainLayer.Background = InstallerWizard.CreateGrain();
            PaneGrain.Background = GrainLayer.Background;
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
