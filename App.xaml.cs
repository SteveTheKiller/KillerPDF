using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Windows.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using TDPdf.Services;
using TDPdf.Diagnostics;

namespace TDPdf
{
    public partial class App : Application
    {
        // ============================================================
        // Paths
        // ============================================================

        private static readonly string AppName   = "TDPdf";
        private static readonly string ExeName   = "TDPdf.exe";

        private enum InstallScope { PerUser, PerMachine }

        // Possible install locations. Resolved lazily — never assume only one
        // exists. An EXE running as SYSTEM (Intune install behavior=System)
        // installs to PerMachine; an end user double-clicking the EXE falls
        // back to PerUser. Uninstall detects which one is actually installed
        // from the registry markers so the elevated UAC uninstall (which runs
        // as the user, not SYSTEM) still removes a PerMachine install.

        private static readonly string PerMachineInstallDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), AppName);
        private static readonly string PerMachineInstallExe = Path.Combine(PerMachineInstallDir, ExeName);
        private static readonly string PerMachineStartMenuDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), AppName);

        private static readonly string PerUserInstallDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", AppName);
        private static readonly string PerUserInstallExe = Path.Combine(PerUserInstallDir, ExeName);
        private static readonly string PerUserStartMenuDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName);

        private static readonly string DesktopLnk = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), $"{AppName}.lnk");

        // Install destination for a *new* install. SYSTEM (Intune) → PerMachine,
        // otherwise PerUser (matches the legacy interactive double-click flow).
        private static InstallScope NewInstallScope =>
            IsSystemContextSafe() ? InstallScope.PerMachine : InstallScope.PerUser;

        private static bool IsSystemContextSafe()
        {
            try { return WindowsIdentity.GetCurrent().IsSystem; }
            catch { return false; }
        }

        private static string InstallDirFor(InstallScope s) =>
            s == InstallScope.PerMachine ? PerMachineInstallDir : PerUserInstallDir;
        private static string InstallExeFor(InstallScope s) =>
            s == InstallScope.PerMachine ? PerMachineInstallExe : PerUserInstallExe;
        private static string StartMenuDirFor(InstallScope s) =>
            s == InstallScope.PerMachine ? PerMachineStartMenuDir : PerUserStartMenuDir;
        private static string StartMenuLnkFor(InstallScope s) =>
            Path.Combine(StartMenuDirFor(s), $"{AppName}.lnk");
        private static RegistryKey HiveFor(InstallScope s) =>
            s == InstallScope.PerMachine ? Registry.LocalMachine : Registry.CurrentUser;

        // Discover an existing install by checking registry markers. Used by
        // Uninstall and IsInstalled so they don't depend on the current
        // process's user context (PerMachine uninstall fires under UAC as the
        // user, not SYSTEM — `IsSystemContextSafe()` would lie there).
        private static InstallScope? DetectInstalledScope()
        {
            using (var k = Registry.LocalMachine.OpenSubKey(@"Software\TDPdf"))
            {
                if (k?.GetValue("Installed") is int hklm && hklm == 1)
                    return InstallScope.PerMachine;
            }
            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\TDPdf"))
            {
                if (k?.GetValue("Installed") is int hkcu && hkcu == 1)
                    return InstallScope.PerUser;
            }
            return null;
        }

        // ============================================================
        // Shell interop
        // ============================================================

        [DllImport("shell32.dll")]
        private static extern void SHChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);
        private const uint SHCNE_ASSOCCHANGED = 0x08000000;
        private const uint SHCNF_IDLIST       = 0x0000;

        // ============================================================
        // Startup
        // ============================================================

        protected override void OnStartup(StartupEventArgs e)
        {
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

            base.OnStartup(e);
            ThemeManager.Initialize(ParseThemeSetting(TDPdf.Properties.Settings.Default.Theme));

            // Handle install/uninstall flags (called by Intune, Add/Remove Programs, or shell).
            // `/install` and `/uninstall` accept an optional `/silent` second arg that
            // suppresses all dialogs — used by the Intune Win32 app install/uninstall commands
            // and by the QuietUninstallString in the Add/Remove Programs entry.
            if (e.Args.Length > 0)
            {
                bool silent = e.Args.Length > 1 &&
                              string.Equals(e.Args[1], "/silent", StringComparison.OrdinalIgnoreCase);

                if (string.Equals(e.Args[0], "/install", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        DoInstall(wantDesktop: false, silent: silent);
                        Shutdown();
                    }
                    catch
                    {
                        // In silent mode DoInstall rethrows; we surface failure to Intune
                        // via exit code rather than a UI dialog the user can't see.
                        Shutdown(1);
                    }
                    return;
                }

                if (string.Equals(e.Args[0], "/uninstall", StringComparison.OrdinalIgnoreCase))
                {
                    Uninstall(silent: silent);
                    Shutdown();
                    return;
                }
            }

            ShutdownMode = ShutdownMode.OnLastWindowClose;
            new MainWindow().Show();
        }


        private static Theme ParseThemeSetting(string? value)
        {
            return Enum.TryParse(value, ignoreCase: true, out Theme theme) ? theme : Theme.System;
        }


        protected override void OnExit(ExitEventArgs e)
        {
            ThemeManager.Cleanup();
            base.OnExit(e);
        }

        // ============================================================
        // Crash handling
        // ============================================================

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            var report = CrashReporter.Report(e.Exception, "UI thread");
            bool shouldContinue = CrashDialog.ShowCrash(report);
            e.Handled = report.Recoverable && shouldContinue;

            if (!e.Handled)
                Shutdown(1);
        }

        private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            var exception = e.ExceptionObject as Exception
                ?? new InvalidOperationException("A non-Exception object was thrown.");
            var report = CrashReporter.Report(exception, "AppDomain unhandled exception");
            CrashDialog.ShowCrash(report);
        }

        private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            var report = CrashReporter.Report(e.Exception, "Unobserved task exception");
            CrashDialog.ShowCrash(report);
            e.SetObserved();
        }

        // ============================================================
        // Public surface used by MainWindow (portable badge / install)
        // ============================================================

        /// <summary>
        /// True when running from outside any installed location (i.e. portable mode).
        /// </summary>
        internal static bool IsPortable()
        {
            string currentExe = Process.GetCurrentProcess().MainModule!.FileName;
            return !string.Equals(currentExe, PerMachineInstallExe, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(currentExe, PerUserInstallExe,    StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Installs TDPdf, offers to set as default PDF handler, then relaunches
        /// from the installed location. Returns false if installation failed or was
        /// already installed from this path.
        /// </summary>
        internal static void InstallAndRelaunch(string? fileToOpen, bool wantDesktop)
        {
            DoInstall(wantDesktop, silent: false);

            if (!IsDefaultPdfHandler())
            {
                var res = TdpDialog.Show(null,
                    "Would you like to set TDPdf as your default PDF viewer?\n\n" +
                    "Opens Windows Settings → Default Apps.",
                    AppName, MessageBoxButton.YesNo);
                if (res == MessageBoxResult.Yes)
                    Process.Start(new ProcessStartInfo("ms-settings:defaultapps")
                        { UseShellExecute = true });
            }

            var psi = new ProcessStartInfo(InstallExeFor(NewInstallScope));
            if (fileToOpen != null)
                psi.Arguments = $"\"{fileToOpen}\"";
            Process.Start(psi);
            Application.Current.Shutdown();
        }

        // ============================================================
        // Registry helpers
        // ============================================================

        private static bool IsInstalled() => DetectInstalledScope() is not null;

        private static bool IsDefaultPdfHandler()
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\Shell\Associations\FileAssociations\.pdf\UserChoice");
            return key?.GetValue("ProgId") is string progId &&
                   progId.Equals("TDPdf.pdf", StringComparison.OrdinalIgnoreCase);
        }

        // ============================================================
        // Launcher dialog
        // ============================================================

        /// <summary>
        /// Builds a button Style with a custom ControlTemplate so hover colours
        /// actually render (WPF's default template ignores Background changes on hover).
        /// </summary>
        private static Style MakeLauncherButtonStyle(
            SolidColorBrush normal, SolidColorBrush hover, SolidColorBrush fg)
        {
            var template = new ControlTemplate(typeof(Button));
            var border   = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
            border.SetBinding(Border.BackgroundProperty,
                new System.Windows.Data.Binding("Background")
                {
                    RelativeSource = new System.Windows.Data.RelativeSource(
                        System.Windows.Data.RelativeSourceMode.TemplatedParent)
                });
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty,   VerticalAlignment.Center);
            cp.SetValue(ContentPresenter.MarginProperty,              new Thickness(0, 6, 0, 6));
            border.AppendChild(cp);
            template.VisualTree = border;

            var style = new Style(typeof(Button));
            style.Setters.Add(new Setter(Button.BackgroundProperty,  normal));
            style.Setters.Add(new Setter(Button.ForegroundProperty,  fg));
            style.Setters.Add(new Setter(Button.BorderThicknessProperty, new Thickness(0)));
            style.Setters.Add(new Setter(Button.TemplateProperty,    template));
            style.Setters.Add(new Setter(Button.CursorProperty,      Cursors.Hand));

            var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hoverTrigger.Setters.Add(new Setter(Button.BackgroundProperty, hover));
            style.Triggers.Add(hoverTrigger);

            return style;
        }

        /// <summary>
        /// Shows the Install / Run dialog.
        /// Returns (cancelled, install, wantDesktopShortcut).
        /// </summary>
        private static (bool cancelled, bool install, bool desktop) ShowLauncher(bool alreadyInstalled)
        {
            bool cancelled = true;
            bool install   = false;
            bool desktop   = true;

            var bg       = new SolidColorBrush(Color.FromRgb(0x1a, 0x1a, 0x1a));
            var dimBg    = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x25));
            var accent   = new SolidColorBrush(Color.FromRgb(0x4a, 0xde, 0x80));
            var dimText  = new SolidColorBrush(Color.FromRgb(0x77, 0x77, 0x77));

            var win = new Window
            {
                Title                 = AppName,
                Width                 = 400,
                Height                = 280,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ResizeMode            = ResizeMode.NoResize,
                WindowStyle           = WindowStyle.None,
                Background            = bg
            };

            // ── Root grid: title bar row + content row ──────────────────
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(36) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // ── Title bar ───────────────────────────────────────────────
            var titleBar = new DockPanel { Background = dimBg };
            Grid.SetRow(titleBar, 0);

            // Drag anywhere on the title bar
            titleBar.MouseLeftButtonDown += (_, e) =>
            {
                if (e.ButtonState == MouseButtonState.Pressed) win.DragMove();
            };

            // Close button — custom template so Background trigger actually renders
            var closeBtnTemplate = new ControlTemplate(typeof(Button));
            var closeBorder = new FrameworkElementFactory(typeof(Border));
            closeBorder.SetBinding(Border.BackgroundProperty,
                new System.Windows.Data.Binding("Background")
                {
                    RelativeSource = new System.Windows.Data.RelativeSource(
                        System.Windows.Data.RelativeSourceMode.TemplatedParent)
                });
            var closeContent = new FrameworkElementFactory(typeof(ContentPresenter));
            closeContent.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            closeContent.SetValue(ContentPresenter.VerticalAlignmentProperty,   VerticalAlignment.Center);
            closeBorder.AppendChild(closeContent);
            closeBtnTemplate.VisualTree = closeBorder;

            var redHover = new SolidColorBrush(Color.FromRgb(0xc4, 0x2b, 0x1c));
            var closeBtnStyle = new Style(typeof(Button));
            closeBtnStyle.Setters.Add(new Setter(Button.BackgroundProperty,      Brushes.Transparent));
            closeBtnStyle.Setters.Add(new Setter(Button.ForegroundProperty,      dimText));
            closeBtnStyle.Setters.Add(new Setter(Button.TemplateProperty,        closeBtnTemplate));
            var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hoverTrigger.Setters.Add(new Setter(Button.BackgroundProperty, redHover));
            hoverTrigger.Setters.Add(new Setter(Button.ForegroundProperty, Brushes.White));
            closeBtnStyle.Triggers.Add(hoverTrigger);

            var closeBtn = new Button
            {
                Content                  = "\uE711",
                FontFamily               = new FontFamily("Segoe MDL2 Assets"),
                FontSize                 = 11,
                Width                    = 46,
                BorderThickness          = new Thickness(0),
                VerticalAlignment        = VerticalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Center,
                Cursor                   = Cursors.Arrow,
                Style                    = closeBtnStyle
            };
            closeBtn.Click += (_, _) => win.Close();
            DockPanel.SetDock(closeBtn, Dock.Right);
            titleBar.Children.Add(closeBtn);

            // App label in title bar
            titleBar.Children.Add(new TextBlock
            {
                Text              = AppName,
                Foreground        = dimText,
                FontSize          = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(12, 0, 0, 0)
            });

            root.Children.Add(titleBar);

            // ── Content ─────────────────────────────────────────────────
            var content = new StackPanel { Margin = new Thickness(36, 22, 36, 28) };
            Grid.SetRow(content, 1);

            content.Children.Add(new TextBlock
            {
                Text       = AppName,
                FontSize   = 26,
                FontWeight = FontWeights.Bold,
                Foreground = accent
            });

            var version = Assembly.GetExecutingAssembly().GetName().Version;
            content.Children.Add(new TextBlock
            {
                Text       = $"Version {version?.ToString(3)}",
                Foreground = dimText,
                FontSize   = 12,
                Margin     = new Thickness(0, 2, 0, 18)
            });

            content.Children.Add(new TextBlock
            {
                Text         = alreadyInstalled
                    ? "A newer version is available. Install it or run without updating."
                    : "Install TDPdf on this computer, or run it without installing.",
                Foreground   = Brushes.White,
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 0, 0, 16)
            });

            var desktopChk = new CheckBox
            {
                IsChecked = true,
                Margin    = new Thickness(0, 0, 0, 22),
                Content   = new TextBlock { Text = "Create desktop shortcut", Foreground = Brushes.White }
            };
            content.Children.Add(desktopChk);

            var btnRow = new StackPanel
            {
                Orientation         = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            var runBtn = new Button
            {
                Content = "Run",
                Width   = 88,
                Margin  = new Thickness(0, 0, 8, 0),
                Style   = MakeLauncherButtonStyle(
                    normal: new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x30)),
                    hover:  new SolidColorBrush(Color.FromRgb(0x16, 0x63, 0x34)),
                    fg:     Brushes.White)
            };
            var installBtn = new Button
            {
                Content    = alreadyInstalled ? "Update" : "Install",
                Width      = 110,
                Style      = MakeLauncherButtonStyle(
                    normal: accent,
                    hover:  new SolidColorBrush(Color.FromRgb(0x4a, 0xf0, 0x90)),
                    fg:     new SolidColorBrush(Color.FromRgb(0x0a, 0x0a, 0x0a))),
                FontWeight = FontWeights.SemiBold
            };

            runBtn.Click += (_, _) =>
            {
                cancelled = false; install = false;
                win.Close();
            };
            installBtn.Click += (_, _) =>
            {
                cancelled = false; install = true;
                desktop = desktopChk.IsChecked == true;
                win.Close();
            };

            btnRow.Children.Add(runBtn);
            btnRow.Children.Add(installBtn);
            content.Children.Add(btnRow);

            root.Children.Add(content);
            win.Content = root;
            win.ShowDialog();

            return (cancelled, install, desktop);
        }

        // ============================================================
        // Installation
        // ============================================================

        private static void DoInstall(bool wantDesktop, bool silent = false)
        {
            var scope = NewInstallScope;
            string installDir   = InstallDirFor(scope);
            string installExe   = InstallExeFor(scope);
            string startMenuDir = StartMenuDirFor(scope);
            string startMenuLnk = StartMenuLnkFor(scope);
            var hive = HiveFor(scope);

            try
            {
                // Copy EXE to install location.
                // If the destination file is currently locked (TDPdf.exe is running from
                // the install location), File.Copy throws IOException. In silent mode we
                // rethrow so Intune sees a non-zero exit and retries; in interactive mode
                // we surface a MessageBox below.
                Directory.CreateDirectory(installDir);
                string src = Process.GetCurrentProcess().MainModule!.FileName;
                File.Copy(src, installExe, overwrite: true);

                // Shortcuts
                Directory.CreateDirectory(startMenuDir);
                CreateShortcut(startMenuLnk, installExe);
                if (wantDesktop)
                    CreateShortcut(DesktopLnk, installExe);

                // Installed marker. Use the full 4-part version so revision-only
                // bumps (e.g. 1.0.0.2) are visible in Add/Remove Programs.
                string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "";
                using (var key = hive.CreateSubKey(@"Software\TDPdf"))
                {
                    key.SetValue("Installed",    1);
                    key.SetValue("InstallPath",  installExe);
                    key.SetValue("Version",      version);
                }

                // Add/Remove Programs entry
                using (var key = hive.CreateSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Uninstall\TDPdf"))
                {
                    key.SetValue("DisplayName",          AppName);
                    key.SetValue("DisplayVersion",       version);
                    key.SetValue("Publisher",            "The Doodle Project");
                    key.SetValue("InstallLocation",      installDir);
                    key.SetValue("DisplayIcon",          $"{installExe},0");
                    key.SetValue("UninstallString",      $"\"{installExe}\" /uninstall");
                    key.SetValue("QuietUninstallString", $"\"{installExe}\" /uninstall /silent");
                    key.SetValue("NoModify",             1);
                    key.SetValue("NoRepair",             1);
                }

                // Register as PDF file handler in the same hive
                RegisterFileHandler(scope);
            }
            catch (Exception ex)
            {
                if (silent) throw;
                MessageBox.Show($"Installation failed:\n{ex.Message}", AppName,
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static void RegisterFileHandler(InstallScope scope)
        {
            // HKLM\Software\Classes is visible to all users; HKCU\Software\Classes
            // is per-user. Stay consistent with the rest of the install.
            string installExe = InstallExeFor(scope);
            var hive = HiveFor(scope);

            using (var k = hive.CreateSubKey(@"Software\Classes\TDPdf.pdf"))
                k.SetValue("", "PDF Document");

            using (var k = hive.CreateSubKey(
                @"Software\Classes\TDPdf.pdf\DefaultIcon"))
                k.SetValue("", $"{installExe},0");

            using (var k = hive.CreateSubKey(
                @"Software\Classes\TDPdf.pdf\shell\open\command"))
                k.SetValue("", $"\"{installExe}\" \"%1\"");

            // Associate .pdf extension — adds TDPdf to the "Open with" list
            using (var k = hive.CreateSubKey(
                @"Software\Classes\.pdf\OpenWithProgids"))
                k.SetValue("TDPdf.pdf", new byte[0], RegistryValueKind.None);

            // RegisteredApplications capability (used by Default Programs UI)
            using (var k = hive.CreateSubKey(
                @"Software\TDPdf\Capabilities"))
            {
                k.SetValue("ApplicationName",        AppName);
                k.SetValue("ApplicationDescription", "Lightweight PDF viewer and editor");
            }
            using (var k = hive.CreateSubKey(
                @"Software\TDPdf\Capabilities\FileAssociations"))
                k.SetValue(".pdf", "TDPdf.pdf");

            using (var k = hive.CreateSubKey(@"Software\RegisteredApplications"))
                k.SetValue(AppName, @"Software\TDPdf\Capabilities");

            // Tell the shell file associations have changed
            SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
        }

        private static void CreateShortcut(string lnkPath, string targetPath)
        {
            try
            {
                var shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType is null) return;
                dynamic shell    = Activator.CreateInstance(shellType)!;
                dynamic shortcut = shell.CreateShortcut(lnkPath);
                shortcut.TargetPath       = targetPath;
                shortcut.WorkingDirectory = Path.GetDirectoryName(targetPath);
                shortcut.Save();
            }
            catch { /* best-effort */ }
        }

        // ============================================================
        // Uninstall
        // ============================================================

        private static void Uninstall(bool silent = false)
        {
            if (!silent)
            {
                var res = MessageBox.Show(
                    "Uninstall TDPdf from this computer?",
                    $"{AppName} Uninstall",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                if (res != MessageBoxResult.Yes) return;
            }

            // Discover which install actually exists so we delete the right
            // files. The interactive Add/Remove Programs uninstall fires the
            // UninstallString elevated as the user (not SYSTEM), so we can't
            // rely on `IsSystemContextSafe()` here for a per-machine install.
            var scope = DetectInstalledScope() ?? InstallScope.PerUser;
            string installDir   = InstallDirFor(scope);
            string startMenuDir = StartMenuDirFor(scope);
            string startMenuLnk = StartMenuLnkFor(scope);

            // Shortcuts
            try { File.Delete(startMenuLnk); } catch { }
            try { Directory.Delete(startMenuDir, recursive: false); } catch { }
            try { File.Delete(DesktopLnk); } catch { }

            // Registry cleanup — try both hives so we tear down whether the
            // install was machine-wide (HKLM) or per-user (HKCU).
            foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
            {
                try { root.DeleteSubKeyTree(@"Software\TDPdf"); } catch { }
                try { root.DeleteSubKeyTree(
                    @"Software\Microsoft\Windows\CurrentVersion\Uninstall\TDPdf"); } catch { }
                try { root.DeleteSubKeyTree(@"Software\Classes\TDPdf.pdf"); } catch { }

                try
                {
                    using var k = root.OpenSubKey(
                        @"Software\Classes\.pdf\OpenWithProgids", writable: true);
                    k?.DeleteValue("TDPdf.pdf", throwOnMissingValue: false);
                }
                catch { }

                try
                {
                    using var k = root.OpenSubKey(
                        @"Software\RegisteredApplications", writable: true);
                    k?.DeleteValue(AppName, throwOnMissingValue: false);
                }
                catch { }
            }

            SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);

            // Self-delete: deferred via cmd batch so the EXE can exit first
            string bat = Path.Combine(Path.GetTempPath(), "tdpdf_uninstall.bat");
            File.WriteAllText(bat,
                "@echo off\r\n" +
                "ping -n 3 127.0.0.1 >nul\r\n" +
                $"rmdir /s /q \"{installDir}\"\r\n" +
                "del \"%~f0\"\r\n");
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{bat}\"")
            {
                WindowStyle    = ProcessWindowStyle.Hidden,
                UseShellExecute = true
            });

            if (!silent)
            {
                MessageBox.Show("TDPdf has been uninstalled.", AppName,
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
    }
}
