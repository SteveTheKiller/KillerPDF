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
            InitializeComponent();
            var titleFrames = BitmapDecoder.Create(new System.Uri("pack://application:,,,/Resources/kp-icon.ico"),
                BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames;
            void RefreshTitleIcon() => TitleIcon.Source = titleFrames.OrderBy(frame =>
                System.Math.Abs(frame.PixelWidth - TitleIcon.ActualWidth * VisualTreeHelper.GetDpi(TitleIcon).DpiScaleX)).First();
            TitleIcon.Loaded += (_, _) => RefreshTitleIcon();
            TitleIcon.SizeChanged += (_, _) => RefreshTitleIcon();
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
