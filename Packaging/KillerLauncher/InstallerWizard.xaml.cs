using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Threading.Tasks;

namespace KillerLauncher
{
    public partial class InstallerWizard : Window
    {
        private int _page;
        private bool _installed;
        private bool _closeAfterNotice;
        private string _installedDirectory = string.Empty;

        private InstallerWizard()
        {
            InitializeComponent();
            SetupVersionLabel.Text = LauncherStrings.Format("SetupVersion",
                typeof(InstallerWizard).Assembly.GetName().Version!.ToString(3));
            InstallFolder.Text = Program.DefaultInstallDirectory(false);
            ImageBrush grain = CreateGrain();
            GrainLayer.Background = grain;
            SidebarGrain.Background = grain;
            FrameGrain.Background = grain;
            RenderPage();
        }

        internal static int Run(string[] args)
        {
            var application = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var wizard = new InstallerWizard();
            bool? result = wizard.ShowDialog();
            application.Shutdown();
            return result == true ? 0 : 1;
        }

        internal static int ShowFailure(string message)
        {
            var application = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var wizard = new InstallerWizard { _closeAfterNotice = true };
            wizard.Loaded += (_, _) => wizard.ShowNotice(
                LauncherStrings.Get("CannotContinue"), message, NoticeKind.Error);
            wizard.ShowDialog();
            application.Shutdown();
            return 1;
        }

        private void RenderPage()
        {
            bool options = _page == 1;
            Options.Visibility = options ? Visibility.Visible : Visibility.Collapsed;
            RuntimeStatus.Visibility = options ? Visibility.Visible : Visibility.Collapsed;
            BackButton.IsEnabled = _page > 0 && !_installed;
            CancelButton.Visibility = _installed ? Visibility.Collapsed : Visibility.Visible;
            if (_page == 0)
            {
                Heading.Text = LauncherStrings.Get("Welcome");
                Copy.Text = LauncherStrings.Get("WelcomeCopy");
                NextButton.Content = LauncherStrings.Get("Next");
            }
            else if (options)
            {
                Heading.Text = LauncherStrings.Get("OptionsHeading");
                Copy.Text = LauncherStrings.Get("OptionsCopy");
                SetRuntimeStatus();
                NextButton.Content = LauncherStrings.Get("Next");
            }
            else
            {
                Heading.Text = _installed ? LauncherStrings.Get("CompleteHeading") : LauncherStrings.Get("ReadyHeading");
                Copy.Text = _installed ? LauncherStrings.Get("CompleteCopy") :
                    (AllUsers.IsChecked == true ? LauncherStrings.Get("AllUsers") : LauncherStrings.Get("CurrentUser")) +
                    (DesktopShortcut.IsChecked == true ? "  •  " + LauncherStrings.Get("WithShortcut") : "  •  " + LauncherStrings.Get("WithoutShortcut"));
                SetRuntimeStatus();
                NextButton.Content = _installed ? LauncherStrings.Get("Launch") : LauncherStrings.Get("Install");
            }
        }

        private void SetRuntimeStatus()
        {
            bool ready = Program.HasDesktopRuntime10();
            RuntimeStatus.Text = ready ? LauncherStrings.Get("RuntimeDetected") : LauncherStrings.Get("RuntimeRequired");
            RuntimeStatus.Foreground = new SolidColorBrush(ready
                ? Color.FromRgb(30, 165, 76) : Color.FromRgb(255, 190, 80));
        }

        private async void Next_Click(object sender, RoutedEventArgs e)
        {
            if (_installed)
            {
                Process.Start(new ProcessStartInfo(Program.InstalledExecutable(_installedDirectory)) { UseShellExecute = true });
                DialogResult = true;
                return;
            }
            if (_page < 2) { _page++; RenderPage(); return; }
            string installDirectory;
            try { installDirectory = Program.ValidateInstallDirectory(InstallFolder.Text); }
            catch (Exception ex)
            {
                _page = 1;
                RenderPage();
                InstallFolder.Focus();
                InstallFolder.SelectAll();
                ShowNotice(LauncherStrings.Get("CheckOptions"), ex.Message, NoticeKind.Warning);
                return;
            }
            if (!Program.HasDesktopRuntime10())
            {
                Process.Start(new ProcessStartInfo("https://dotnet.microsoft.com/en-us/download/dotnet/10.0") { UseShellExecute = true });
                ShowNotice(LauncherStrings.Get("RuntimeRequired"),
                    LauncherStrings.Get("InstallRuntime"), NoticeKind.Information);
                return;
            }
            try
            {
                NextButton.IsEnabled = BackButton.IsEnabled = CancelButton.IsEnabled = false;
                Heading.Text = LauncherStrings.Get("Installing");
                Copy.Text = LauncherStrings.Get("InstallingCopy");
                RuntimeStatus.Visibility = Visibility.Collapsed;
                InstallProgress.Visibility = Visibility.Visible;
                bool machine = AllUsers.IsChecked == true;
                bool desktop = DesktopShortcut.IsChecked == true;
                Task<int> install = Task.Run(() => machine ? InstallForEveryone(desktop, installDirectory)
                    : Program.Install(false, desktop, installDirectory));
                await Task.WhenAll(install, Task.Delay(2000));
                int result = install.Result;
                if (result != 0) throw new InvalidOperationException(LauncherStrings.Format("SetupReturned", result));
                _installedDirectory = installDirectory;
                _installed = true;
                NextButton.IsEnabled = true;
                InstallProgress.Visibility = Visibility.Collapsed;
                RenderPage();
            }
            catch (Exception ex)
            {
                NextButton.IsEnabled = BackButton.IsEnabled = CancelButton.IsEnabled = true;
                InstallProgress.Visibility = Visibility.Collapsed;
                RenderPage();
                ShowNotice(LauncherStrings.Get("InstallFailed"), ex.Message, NoticeKind.Error);
            }
        }

        private enum NoticeKind { Information, Warning, Error }

        private void ShowNotice(string heading, string message, NoticeKind kind)
        {
            NoticeHeading.Text = heading;
            NoticeMessage.Text = message;
            NoticeGlyph.Text = kind == NoticeKind.Error ? "×" : kind == NoticeKind.Warning ? "!" : "i";
            NoticeGlyph.Foreground = new SolidColorBrush(kind == NoticeKind.Error
                ? Color.FromRgb(227, 93, 106)
                : kind == NoticeKind.Warning ? Color.FromRgb(255, 190, 80) : Color.FromRgb(30, 165, 76));
            NoticeRing.BorderBrush = NoticeGlyph.Foreground;
            NoticeOverlay.Visibility = Visibility.Visible;
            NoticeOk.Focus();
        }

        private void NoticeOk_Click(object sender, RoutedEventArgs e)
        {
            if (_closeAfterNotice) { DialogResult = false; return; }
            NoticeOverlay.Visibility = Visibility.Collapsed;
        }

        private static int InstallForEveryone(bool desktop, string installDirectory)
        {
            string arguments = "/silent " + (desktop ? "/desktop " : string.Empty) +
                Program.EncodeInstallDirectoryArgument(installDirectory);
            var start = new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName,
                arguments) { UseShellExecute = true, Verb = "runas" };
            using (Process elevated = Process.Start(start))
            {
                if (elevated == null) return 1;
                elevated.WaitForExit();
                return elevated.ExitCode;
            }
        }

        private void Scope_Checked(object sender, RoutedEventArgs e)
        {
            if (InstallFolder == null) return;
            string user = Program.DefaultInstallDirectory(false);
            string machine = Program.DefaultInstallDirectory(true);
            if (string.IsNullOrWhiteSpace(InstallFolder.Text) ||
                string.Equals(InstallFolder.Text, user, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(InstallFolder.Text, machine, StringComparison.OrdinalIgnoreCase))
                InstallFolder.Text = Program.DefaultInstallDirectory(AllUsers.IsChecked == true);
        }

        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            using var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = LauncherStrings.Get("ChooseFolder"),
                ShowNewFolderButton = true,
                SelectedPath = Directory.Exists(InstallFolder.Text) ? InstallFolder.Text :
                    (Directory.Exists(Path.GetDirectoryName(InstallFolder.Text)) ? Path.GetDirectoryName(InstallFolder.Text) : string.Empty)
            };
            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                InstallFolder.Text = dialog.SelectedPath;
        }

        private void Back_Click(object sender, RoutedEventArgs e) { if (_page > 0) { _page--; RenderPage(); } }
        private void Cancel_Click(object sender, RoutedEventArgs e) { DialogResult = false; }
        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) { if (e.LeftButton == MouseButtonState.Pressed) DragMove(); }
        private void Website_Click(object sender, RoutedEventArgs e) =>
            Process.Start(new ProcessStartInfo("https://thekiller.net") { UseShellExecute = true });

        private static ImageBrush CreateGrain()
        {
            const int size = 128;
            var pixels = new byte[size * size * 4];
            var random = new Random(1979);
            for (int i = 0; i < pixels.Length; i += 4)
            {
                byte value = (byte)random.Next(82, 174);
                pixels[i] = pixels[i + 1] = pixels[i + 2] = value;
                pixels[i + 3] = (byte)random.Next(34, 92);
            }
            var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, size * 4);
            bitmap.Freeze();
            return new ImageBrush(bitmap) { TileMode = TileMode.Tile, ViewportUnits = BrushMappingMode.Absolute,
                Viewport = new Rect(0, 0, size, size), Stretch = Stretch.None };
        }
    }
}
