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
        private readonly Program.InstalledCopy? _existing;

        // Lacquer has no Cyrillic, CJK, Bengali or some accented Latin letters. Setting a word that
        // Lacquer cannot fully draw in the UI font keeps it in one face instead of mixing letters.
        private bool LacquerCovers(string text)
        {
            if (!new Typeface((FontFamily)FindResource("WordmarkAccent"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal)
                    .TryGetGlyphTypeface(out GlyphTypeface face)) return false;
            foreach (char c in text)
                if (!char.IsWhiteSpace(c) && !face.CharacterToGlyphMap.ContainsKey(c)) return false;
            return true;
        }

        // The window's text face and the UiFont and HeadingFont resources follow the language the
        // launcher's strings load for. This runs before InitializeComponent so the XAML's
        // StaticResource lookups find the application-level resources.
        internal static void ApplyLanguageFonts(Window window)
        {
            string locale = LauncherStrings.CurrentLocale();
            var baseUri = new Uri("pack://application:,,,/" + typeof(InstallerWizard).Assembly.GetName().Name + ";component/");
            window.FontFamily = new FontFamily(baseUri, LauncherFonts.Ui(locale));
            if (Application.Current == null) return;
            Application.Current.Resources["UiFont"] = new FontFamily(baseUri, LauncherFonts.Ui(locale));
            Application.Current.Resources["HeadingFont"] = new FontFamily(baseUri, LauncherFonts.Heading(locale));
        }

        private InstallerWizard()
        {
            ApplyLanguageFonts(this);
            InitializeComponent();
            new KillerFamily.Accessibility.OverlayFocus(this).Track(NoticeOverlay);
            Closing += (_, e) =>
            {
                if (NoticeOpen && !_closeAfterNotice) { e.Cancel = true; DismissNotice(); }
            };
            PreviewKeyDown += (_, e) =>
            {
                if (NoticeOpen && e.Key == Key.Escape) { e.Handled = true; NoticeOk_Click(this, new RoutedEventArgs()); }
            };
            TaskbarIdentity.Track(this);
            SetupVersionLabel.SetVersion(typeof(InstallerWizard).Assembly.GetName().Version!.ToString(3));
            if (!LacquerCovers(SetupLabel.Text)) SetupLabel.FontFamily = (FontFamily)FindResource("UiFont");
            _existing = Program.FindInstalledCopy();
            InstallFolder.Text = _existing?.Directory ?? Program.DefaultInstallDirectory(false);
            if (_existing != null)
            {
                CurrentUser.IsChecked = !_existing.Machine;
                AllUsers.IsChecked = _existing.Machine;
                DesktopShortcut.IsChecked = _existing.DesktopShortcut;
                PreviewHandler.IsChecked = _existing.PreviewHandler;
            }
            if (Program.IsStorePackage)
            {
                PreviewHandler.IsChecked = false;
                PreviewHandler.Visibility = Visibility.Collapsed;
            }
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
            return result == true ? KillerFamily.Deployment.InstallerExitCodes.Success : KillerFamily.Deployment.InstallerExitCodes.Cancelled;
        }

        internal static int ShowFailure(string message, int exitCode = KillerFamily.Deployment.InstallerExitCodes.Failure)
        {
            var application = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var wizard = new InstallerWizard { _closeAfterNotice = true };
            wizard.Loaded += (_, _) => wizard.ShowNotice(
                LauncherStrings.Get("CannotContinue"), message, NoticeKind.Error);
            wizard.ShowDialog();
            application.Shutdown();
            return exitCode;
        }

        private void RenderPage()
        {
            if (_existing != null && !_installed)
            {
                Options.Visibility = Visibility.Collapsed;
                RuntimeStatus.Visibility = Visibility.Visible;
                BackButton.Visibility = Visibility.Collapsed;
                CancelButton.Visibility = Visibility.Visible;
                UninstallButton.Visibility = Visibility.Visible;
                SetRuntimeStatus();
                bool current = string.Equals(_existing.Version, Program.SetupVersion, StringComparison.Ordinal);
                Heading.Text = current
                    ? LauncherStrings.Get("AlreadyInstalledHeading")
                    : LauncherStrings.Get("UpgradeReadyHeading");
                Copy.Text = LauncherStrings.Format("MaintenanceCopy", _existing.Version);
                NextButton.Content = current
                    ? LauncherStrings.Get("Reinstall")
                    : LauncherStrings.Get("Upgrade");
                return;
            }

            bool options = _page == 1;
            Options.Visibility = options ? Visibility.Visible : Visibility.Collapsed;
            RuntimeStatus.Visibility = options ? Visibility.Visible : Visibility.Collapsed;
            BackButton.Visibility = Visibility.Visible;
            UninstallButton.Visibility = Visibility.Collapsed;
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
                    (DesktopShortcut.IsChecked == true ? "  •  " + LauncherStrings.Get("WithShortcut") : "  •  " + LauncherStrings.Get("WithoutShortcut")) +
                    (PreviewHandler.IsChecked == true ? "  •  " + LauncherStrings.Get("WithPreview") : string.Empty);
                SetRuntimeStatus();
                NextButton.Content = _installed ? LauncherStrings.Get("Launch") : LauncherStrings.Get("Install");
            }
        }

        private void SetRuntimeStatus()
        {
            if (Program.IsStorePackage) { RuntimeStatus.Visibility = Visibility.Collapsed; return; }
            bool ready = Program.HasDesktopRuntime10();
            RuntimeStatus.Text = ready ? LauncherStrings.Get("RuntimeDetected") : LauncherStrings.Get("RuntimeRequired");
            RuntimeStatus.Foreground = new SolidColorBrush(ready
                ? Color.FromRgb(30, 165, 76) : Color.FromRgb(255, 190, 80));
        }

        private async void Next_Click(object sender, RoutedEventArgs e)
        {
            if (NoticeOpen) return;
            if (_installed)
            {
                Process.Start(new ProcessStartInfo(Program.InstalledExecutable(_installedDirectory)) { UseShellExecute = true });
                DialogResult = true;
                return;
            }
            if (_existing == null && _page < 2) { _page++; RenderPage(); return; }
            string installDirectory;
            try { installDirectory = Program.ValidateInstallDestination(InstallFolder.Text); }
            catch (Exception ex)
            {
                _page = 1;
                RenderPage();
                InstallFolder.Focus();
                InstallFolder.SelectAll();
                ShowNotice(LauncherStrings.Get("CheckOptions"), ex.Message, NoticeKind.Warning);
                return;
            }
            if (!Program.RuntimeReady())
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
                bool machine = _existing?.Machine ?? AllUsers.IsChecked == true;
                bool desktop = _existing?.DesktopShortcut ?? DesktopShortcut.IsChecked == true;
                bool preview = !Program.IsStorePackage && (_existing?.PreviewHandler ?? PreviewHandler.IsChecked == true);
                Task<int> install = Task.Run(() => machine ? InstallForEveryone(desktop, preview, installDirectory)
                    : Program.Install(false, desktop, installDirectory, preview));
                await Task.WhenAll(install, Task.Delay(2000));
                int result = install.Result;
                if (result != 0) throw new InvalidOperationException(LauncherStrings.Format("SetupReturned", result));
                _installedDirectory = installDirectory;
                _installed = true;
                NextButton.IsEnabled = true;
                InstallProgress.Visibility = Visibility.Collapsed;
                RenderPage();
                // Windows can refuse the desktop shortcut, for example under Controlled folder
                // access. That does not fail the install, so it is reported here instead; an older
                // link that opens some other copy does not hide the refusal.
                if (desktop && !Program.DesktopShortcutOpensInstalledCopy(machine, installDirectory))
                    ShowNotice(LauncherStrings.Get("ShortcutSkipped"), LauncherStrings.Get("ShortcutSkippedCopy"),
                        NoticeKind.Warning);
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
        private bool NoticeOpen => NoticeOverlay?.Visibility == Visibility.Visible;

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
            ContentCard.IsEnabled = SidebarCard.IsEnabled = false;
            NoticeOk.Focus();
        }

        private void NoticeOk_Click(object sender, RoutedEventArgs e)
        {
            if (_closeAfterNotice) { DialogResult = false; return; }
            DismissNotice();
        }

        private void DismissNotice()
        {
            NoticeOverlay.Visibility = Visibility.Collapsed;
            ContentCard.IsEnabled = SidebarCard.IsEnabled = true;
        }

        private static int InstallForEveryone(bool desktop, bool preview, string installDirectory)
        {
            string arguments = "/silent " + (desktop ? "/desktop " : string.Empty) + (preview ? "/preview " : "/no-preview ") +
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
            if (NoticeOpen) return;
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
            if (NoticeOpen) return;
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

        private void Back_Click(object sender, RoutedEventArgs e) { if (!NoticeOpen && _page > 0) { _page--; RenderPage(); } }
        private void Uninstall_Click(object sender, RoutedEventArgs e)
        {
            if (NoticeOpen || _existing == null) return;
            Process.Start(new ProcessStartInfo(_existing.Executable, "/uninstall") { UseShellExecute = true });
            DialogResult = true;
        }
        private void Cancel_Click(object sender, RoutedEventArgs e) { if (!NoticeOpen) DialogResult = false; }
        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) { if (e.LeftButton == MouseButtonState.Pressed) DragMove(); }
        private void Website_Click(object sender, RoutedEventArgs e)
        {
            if (!NoticeOpen) Process.Start(new ProcessStartInfo("https://thekiller.net") { UseShellExecute = true });
        }

        internal static ImageBrush CreateGrain()
        {
            // The family film grain from KillerUI GrainTexture: about a third of the pixels carry a speck,
            // half bright and half dark, so the surfaces keep their theme color instead of lifting toward gray.
            const int size = 256;
            var pixels = new byte[size * size * 4];
            var random = new Random(1337);
            for (int i = 0; i < pixels.Length; i += 4)
            {
                if (random.Next(3) != 0) continue;
                bool bright = random.Next(2) == 0;
                byte value = bright ? (byte)random.Next(190, 255) : (byte)random.Next(0, 50);
                pixels[i] = pixels[i + 1] = pixels[i + 2] = value;
                pixels[i + 3] = (byte)random.Next(35, 95);
            }
            var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, size * 4);
            bitmap.Freeze();
            return new ImageBrush(bitmap) { TileMode = TileMode.Tile, ViewportUnits = BrushMappingMode.Absolute,
                Viewport = new Rect(0, 0, size, size), Stretch = Stretch.None };
        }
    }
}
