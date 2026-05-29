using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
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
            base.OnStartup(e);

            // Handle install/uninstall flags BEFORE wiring up UI / theme / crash plumbing.
            //
            // Intune (install behavior=System) runs us as LocalSystem in session 0 where
            // there is no display, no message pump owner, and no user profile in the usual
            // sense. Touching ThemeManager (pack-URI ResourceDictionary load, SystemEvents
            // hook) or installing a crash dialog handler in that context adds failure
            // surface for zero gain — the install/uninstall paths are headless by design.
            //
            // `/install` and `/uninstall` accept an optional `/silent` second arg used by
            // the Intune Win32 app install/uninstall commands and by the QuietUninstallString
            // in the Add/Remove Programs entry.
            //
            // `/set-telemetry` and `/clear-telemetry` are admin/SYSTEM-only provisioning
            // flags for opt-in Application Insights (see Diagnostics/TelemetryStore.cs).
            // Connection string is read from STDIN, never the command line, so it can't
            // leak into shell history, Intune script logs, or process inspection.
            if (e.Args.Length > 0)
            {
                bool silent = e.Args.Length > 1 &&
                              string.Equals(e.Args[1], "/silent", StringComparison.OrdinalIgnoreCase);

                if (string.Equals(e.Args[0], "/install", StringComparison.OrdinalIgnoreCase))
                {
                    InstallLog.WriteHeader("INSTALL", e.Args);
                    // SYSTEM-context install is the right moment to drop
                    // %ProgramData%\TDPdf\telemetry.dat from the embedded
                    // key (release builds only). No-op for dev / CI / user
                    // /install if Embedded key isn't present, and a no-op
                    // if telemetry.dat already exists or the user has
                    // explicitly disabled telemetry on this device.
                    TryAutoProvisionEmbeddedTelemetry();
                    Telemetry.Initialize(AppVersionString());
                    Telemetry.TrackEvent("Install.Start", InstallProps(silent));
                    try
                    {
                        DoInstall(wantDesktop: false, silent: silent);
                        InstallLog.Write("INSTALL OK");
                        Telemetry.TrackEvent("Install.Success", InstallProps(silent));
                        Telemetry.Flush();
                        Shutdown(0);
                    }
                    catch (Exception ex)
                    {
                        InstallLog.WriteError("INSTALL FAILED", ex);
                        Telemetry.TrackCrash(ex, "Install", recoverable: false);
                        Telemetry.Flush();
                        Shutdown(1);
                    }
                    return;
                }

                if (string.Equals(e.Args[0], "/uninstall", StringComparison.OrdinalIgnoreCase))
                {
                    InstallLog.WriteHeader("UNINSTALL", e.Args);
                    Telemetry.Initialize(AppVersionString());
                    Telemetry.TrackEvent("Uninstall.Start", InstallProps(silent));
                    try
                    {
                        Uninstall(silent: silent);
                        InstallLog.Write("UNINSTALL OK");
                        Telemetry.TrackEvent("Uninstall.Success", InstallProps(silent));
                        Telemetry.Flush();
                        Shutdown(0);
                    }
                    catch (Exception ex)
                    {
                        InstallLog.WriteError("UNINSTALL FAILED", ex);
                        Telemetry.TrackCrash(ex, "Uninstall", recoverable: false);
                        Telemetry.Flush();
                        Shutdown(1);
                    }
                    return;
                }

                if (string.Equals(e.Args[0], "/set-telemetry", StringComparison.OrdinalIgnoreCase))
                {
                    // Deliberately do NOT log e.Args here — even though we
                    // require stdin input, an accidental positional arg
                    // would be the leaked-secret path. WriteHeader logs
                    // args verbatim, so emit a hand-built header instead.
                    InstallLog.Write("=== SET-TELEMETRY === (stdin)");
                    try
                    {
                        SetTelemetryFromStdin();
                        InstallLog.Write("SET-TELEMETRY OK");
                        Shutdown(0);
                    }
                    catch (Exception ex)
                    {
                        InstallLog.WriteError("SET-TELEMETRY FAILED", ex);
                        Shutdown(1);
                    }
                    return;
                }

                if (string.Equals(e.Args[0], "/clear-telemetry", StringComparison.OrdinalIgnoreCase))
                {
                    InstallLog.WriteHeader("CLEAR-TELEMETRY", e.Args);
                    try
                    {
                        TelemetryStore.Clear();
                        // Write the disabled-sentinel so the next launch
                        // does not silently re-provision from the
                        // build-time-embedded key.
                        TelemetryStore.MarkDisabled();
                        InstallLog.Write("CLEAR-TELEMETRY OK");
                        Shutdown(0);
                    }
                    catch (Exception ex)
                    {
                        InstallLog.WriteError("CLEAR-TELEMETRY FAILED", ex);
                        Shutdown(1);
                    }
                    return;
                }
            }

            // Single-instance: when enabled (the default), a second launch — for
            // example double-clicking another PDF in Explorer — forwards its file
            // path to the already-running window (opened there as a new tab)
            // instead of spawning a second window. Best-effort: any failure falls
            // back to the normal one-window-per-process behavior.
            if (TDPdf.Properties.Settings.Default.SingleInstanceTabs)
            {
                try
                {
                    _singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out bool createdNew);
                    if (!createdNew)
                    {
                        string payload = (e.Args.Length > 0 && File.Exists(e.Args[0]))
                            ? e.Args[0]
                            : string.Empty;
                        if (TrySendToRunningInstance(payload))
                        {
                            Shutdown(0);
                            return;
                        }
                        // Couldn't reach the running instance — continue and open
                        // a normal window so the user is never left with nothing.
                    }
                    else
                    {
                        StartSingleInstanceServer();
                    }
                }
                catch
                {
                    // Ignore and degrade to multi-window behavior.
                }
            }

            // Interactive launch: now wire up theme + crash handling.
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

            ThemeManager.Initialize(ParseThemeSetting(TDPdf.Properties.Settings.Default.Theme));

            // Auto-provision from build-time-embedded key if present.
            // Release builds (TDPDF_APPINSIGHTS_CONN set at release time)
            // carry an encrypted connection string; dev / CI / source
            // builds do not. This is a best-effort no-op when there's no
            // embedded key, when telemetry.dat already exists, or when
            // the user has run /clear-telemetry on this device.
            TryAutoProvisionEmbeddedTelemetry();

            // Opt-in telemetry (see Diagnostics/Telemetry.cs). No-op
            // unless telemetry.dat is present.
            string appVersion = AppVersionString();
            string installScope = DetectInstalledScope() switch
            {
                InstallScope.PerMachine => "PerMachine",
                InstallScope.PerUser    => "PerUser",
                _                        => "Portable",
            };
            Telemetry.Initialize(appVersion);
            Telemetry.TrackEvent("App.Startup", new Dictionary<string, string>
            {
                ["AppVersion"]      = appVersion,
                ["InstallScope"]    = installScope,
                ["OSVersion"]       = Environment.OSVersion.Version.ToString(),
                ["Is64BitProcess"]  = Environment.Is64BitProcess ? "true" : "false",
            });

            ShutdownMode = ShutdownMode.OnLastWindowClose;
            var window = new MainWindow();
            MainWindow = window;
            window.Show();
        }

        // ============================================================
        // Single instance (open subsequent PDFs as tabs in one window)
        // ============================================================

        // Per-user names so concurrent logon sessions don't collide and so a
        // per-user install never clashes with another account on the same box.
        private static readonly string SingleInstanceMutexName =
            "Local\\TDPdf.SingleInstance.Mutex." + Environment.UserName;
        private static readonly string SingleInstancePipeName =
            "TDPdf.SingleInstance.Pipe." + Environment.UserName;
        private Mutex? _singleInstanceMutex;

        // Sends a file path (or empty string = "just focus") to the running
        // instance over a named pipe. Returns false if no instance answered.
        private static bool TrySendToRunningInstance(string payload)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", SingleInstancePipeName, PipeDirection.Out);
                client.Connect(3000);
                using var writer = new StreamWriter(client) { AutoFlush = true };
                writer.WriteLine(payload);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void StartSingleInstanceServer()
        {
            var thread = new Thread(SingleInstanceServerLoop)
            {
                IsBackground = true,
                Name = "TDPdf-SingleInstance"
            };
            thread.Start();
        }

        private void SingleInstanceServerLoop()
        {
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        SingleInstancePipeName,
                        PipeDirection.In,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.None);
                    server.WaitForConnection();
                    using var reader = new StreamReader(server);
                    string? line = reader.ReadLine();
                    Dispatcher.Invoke(() =>
                    {
                        if (Current?.MainWindow is MainWindow mw)
                            mw.OpenPathFromAnotherInstance(line);
                    });
                }
                catch
                {
                    // Pipe broke, or the app is shutting down. Pause briefly and
                    // retry; bail out if the application is gone.
                    if (Current is null) break;
                    try { Thread.Sleep(200); } catch { }
                }
            }
        }

        private static string AppVersionString() =>
            Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0.0";

        private static Dictionary<string, string> InstallProps(bool silent) => new()
        {
            ["Silent"]          = silent ? "true" : "false",
            ["IsSystemContext"] = IsSystemContextSafe() ? "true" : "false",
            ["OSVersion"]       = Environment.OSVersion.Version.ToString(),
            ["AppVersion"]      = AppVersionString(),
        };


        private static Theme ParseThemeSetting(string? value)
        {
            return Enum.TryParse(value, ignoreCase: true, out Theme theme) ? theme : Theme.System;
        }


        protected override void OnExit(ExitEventArgs e)
        {
            // Bounded best-effort telemetry flush. Capped at ~2s inside
            // Telemetry.Flush so shutdown can never hang on the network.
            Telemetry.Flush();
            ThemeManager.Cleanup();
            try { _singleInstanceMutex?.Dispose(); } catch { }
            base.OnExit(e);
        }

        /// <summary>
        /// Read the App Insights connection string from STDIN (one line
        /// followed by EOF), encrypt it with DPAPI LocalMachine via
        /// <see cref="TelemetryStore"/>, and write the hardened
        /// ciphertext to <c>%ProgramData%\TDPdf\telemetry.dat</c>.
        ///
        /// Requires elevation. Caller (admin or SYSTEM) typically pipes
        /// the connection string:
        ///   <c>type secret.txt | TDPdf.exe /set-telemetry</c>
        /// </summary>
        private static void SetTelemetryFromStdin()
        {
            string? connectionString;
            using (var reader = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8))
            {
                // ReadLine is sufficient — connection strings are a
                // single line by spec. ReadToEnd would also work but
                // could pull trailing CR/LF noise we'd have to trim.
                connectionString = reader.ReadLine();
            }

            if (string.IsNullOrWhiteSpace(connectionString))
                throw new InvalidOperationException(
                    "No connection string received on stdin. " +
                    "Usage: type secret.txt | TDPdf.exe /set-telemetry");

            TelemetryStore.Save(connectionString);
            // Clear the disabled-sentinel — if a previous /clear-telemetry
            // wrote it, an explicit /set-telemetry should re-enable.
            TelemetryStore.ClearDisabledMarker();
            InstallLog.Write($"Wrote telemetry.dat at {TelemetryStore.Path}");
        }

        /// <summary>
        /// Best-effort attempt to write <c>telemetry.dat</c> from the
        /// build-time-embedded connection string (release builds with
        /// <c>$env:TDPDF_APPINSIGHTS_CONN</c> set during
        /// <c>release.ps1</c>). No-op when the embedded key is empty
        /// (dev/CI builds), when <c>telemetry.dat</c> already exists,
        /// or when the user has run <c>/clear-telemetry</c> on this
        /// device. Never throws.
        /// </summary>
        private static void TryAutoProvisionEmbeddedTelemetry()
        {
            try
            {
                if (!EmbeddedTelemetry.HasKey) return;
                if (TelemetryStore.Exists()) return;
                if (TelemetryStore.IsDisabled()) return;

                string? conn = EmbeddedTelemetry.TryDecrypt();
                if (string.IsNullOrWhiteSpace(conn)) return;

                TelemetryStore.Save(conn);
            }
            catch { /* best-effort — never block startup */ }
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
            try
            {
                DoInstall(wantDesktop, silent: false);
            }
            catch (Exception ex)
            {
                InstallLog.WriteError("Interactive install failed", ex);
                MessageBox.Show($"Installation failed:\n{ex.Message}", AppName,
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

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
            string src = Process.GetCurrentProcess().MainModule!.FileName;

            InstallLog.Write($"DoInstall scope={scope} src={src} dest={installExe} hive={hive.Name}");

            // Copy EXE to install location. If the destination file is locked
            // (TDPdf is running from the install location), File.Copy throws
            // IOException; the caller maps any exception to a non-zero exit
            // code so Intune retries on the next sync.
            Directory.CreateDirectory(installDir);
            File.Copy(src, installExe, overwrite: true);

            // Post-copy verification. Without this, a partial / silently-failed
            // copy could leave us with no file on disk and the caller would
            // still see "success" — exactly the Intune "installed but not
            // detected" footgun we are trying to fix.
            var fi = new FileInfo(installExe);
            if (!fi.Exists || fi.Length == 0)
                throw new IOException($"Post-copy verification failed: {installExe} missing or empty.");

            // Extract the stock PDF icon next to the EXE. This is what the
            // .pdf file-type association (DefaultIcon) points at, so PDF files
            // show the generic PDF logo while the EXE / taskbar keeps the
            // company logo as the primary indicator. Best-effort: a failure
            // here just means the association falls back to the EXE icon.
            try
            {
                ExtractPdfFileIcon(installDir);
            }
            catch (Exception ex)
            {
                InstallLog.WriteError("PDF file-type icon extraction failed (non-fatal)", ex);
            }

            try
            {
                var fvi = System.Diagnostics.FileVersionInfo.GetVersionInfo(installExe);
                InstallLog.Write($"Copied {fi.Length} bytes; FileVersion={fvi.FileVersion} ProductVersion={fvi.ProductVersion}");
            }
            catch (Exception ex)
            {
                InstallLog.WriteError("FileVersionInfo lookup failed (non-fatal)", ex);
            }

            // Shortcuts — best-effort. A failed shortcut should not fail the
            // entire install (and CreateShortcut already swallows internally),
            // but we still log it.
            try
            {
                Directory.CreateDirectory(startMenuDir);
                CreateShortcut(startMenuLnk, installExe);
                if (wantDesktop)
                    CreateShortcut(DesktopLnk, installExe);
            }
            catch (Exception ex)
            {
                InstallLog.WriteError("Shortcut creation failed (non-fatal)", ex);
            }

            // Add/Remove Programs entry. Use the full 4-part version so
            // revision-only bumps (e.g. 1.0.0.3) are visible.
            string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "";
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
            InstallLog.Write("Wrote Add/Remove Programs entry");

            // Register as PDF file handler in the same hive. This creates
            // `Software\TDPdf\Capabilities` under the scope hive.
            RegisterFileHandler(scope);
            InstallLog.Write("Registered PDF file handler");

            // Installed marker — written LAST so the Intune registry detection
            // rule (`HKLM\Software\TDPdf` value `Version` >= release version)
            // only flips to "installed" once all required steps above have
            // succeeded. A failure earlier in DoInstall must not leave a
            // partial install that detects as complete.
            using (var key = hive.CreateSubKey(@"Software\TDPdf"))
            {
                key.SetValue("Installed",    1);
                key.SetValue("InstallPath",  installExe);
                key.SetValue("Version",      version);
            }
            InstallLog.Write($"Wrote install marker: HKLM-or-HKCU\\Software\\TDPdf Version={version}");
        }

        private static void RegisterFileHandler(InstallScope scope)
        {
            // HKLM\Software\Classes is visible to all users; HKCU\Software\Classes
            // is per-user. Stay consistent with the rest of the install.
            string installExe = InstallExeFor(scope);
            var hive = HiveFor(scope);

            using (var k = hive.CreateSubKey(@"Software\Classes\TDPdf.pdf"))
                k.SetValue("", "PDF Document");

            // File-type icon shown on .pdf files. Prefer the generic stock PDF
            // icon extracted alongside the EXE so PDF files don't show the
            // company logo; fall back to the EXE's own icon if the extracted
            // file is missing for any reason.
            string pdfIcon = PdfFileIconPathFor(scope);
            string defaultIcon = File.Exists(pdfIcon) ? $"{pdfIcon},0" : $"{installExe},0";
            using (var k = hive.CreateSubKey(
                @"Software\Classes\TDPdf.pdf\DefaultIcon"))
                k.SetValue("", defaultIcon);

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

        // Full path to the stock PDF file-type icon for a given install scope.
        private static string PdfFileIconPathFor(InstallScope scope) =>
            Path.Combine(InstallDirFor(scope), "pdf-file.ico");

        // Write the embedded stock PDF icon out to the install directory so the
        // .pdf file-type association can point at it. The icon ships inside the
        // single-file EXE as a WPF resource (Resources\pdf-file.ico).
        private static void ExtractPdfFileIcon(string installDir)
        {
            var uri = new Uri("pack://application:,,,/Resources/pdf-file.ico", UriKind.Absolute);
            var info = Application.GetResourceStream(uri)
                       ?? throw new FileNotFoundException("Embedded resource Resources/pdf-file.ico not found.");

            string dest = Path.Combine(installDir, "pdf-file.ico");
            using var src = info.Stream;
            using var dst = File.Create(dest);
            src.CopyTo(dst);
            InstallLog.Write($"Extracted PDF file-type icon to {dest}");
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
                if (res != MessageBoxResult.Yes)
                {
                    InstallLog.Write("User cancelled uninstall");
                    return;
                }
            }

            // Discover which install actually exists so we delete the right
            // files. The interactive Add/Remove Programs uninstall fires the
            // UninstallString elevated as the user (not SYSTEM), so we can't
            // rely on `IsSystemContextSafe()` here for a per-machine install.
            var scope = DetectInstalledScope() ?? InstallScope.PerUser;
            string installDir   = InstallDirFor(scope);
            string startMenuDir = StartMenuDirFor(scope);
            string startMenuLnk = StartMenuLnkFor(scope);

            InstallLog.Write($"Uninstall scope={scope} installDir={installDir} silent={silent}");

            // Shortcuts
            try { File.Delete(startMenuLnk); } catch (Exception ex) { InstallLog.WriteError("Delete start menu lnk", ex); }
            try { Directory.Delete(startMenuDir, recursive: false); } catch { /* non-empty or already gone */ }
            try { File.Delete(DesktopLnk); } catch (Exception ex) { InstallLog.WriteError("Delete desktop lnk", ex); }

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
            InstallLog.Write("Registry cleanup complete");

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
            InstallLog.Write($"Scheduled deferred delete via {bat}");

            if (!silent)
            {
                MessageBox.Show("TDPdf has been uninstalled.", AppName,
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
    }
}
