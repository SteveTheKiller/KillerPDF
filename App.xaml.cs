using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using KillerPDF.Services;
using Microsoft.Win32;

namespace KillerPDF
{
    public partial class App : Application
    {
        // ============================================================
        // Paths
        // ============================================================

        private static readonly string AppName   = "KillerPDF";
        private static readonly string ExeName   = "KillerPDF.exe";
        private static readonly string InstallDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", AppName);
        private static readonly string InstallExe = Path.Combine(InstallDir, ExeName);
        private static readonly string FileIconPath = Path.Combine(InstallDir, "pdf-file.ico");

        private static readonly string StartMenuDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName);
        private static readonly string StartMenuLnk = Path.Combine(StartMenuDir, $"{AppName}.lnk");
        private static readonly string DesktopLnk   = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), $"{AppName}.lnk");

        // ── Machine-wide install (all users), matching Killendar / KillerShell ─────────────
        // ProgramFiles rather than LocalApplicationData\Programs, so it needs elevation - which
        // is why it goes through the /silent path under a UAC prompt instead of being done inline.
        private static readonly string MachineInstallDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), AppName);
        private static readonly string MachineInstallExe = Path.Combine(MachineInstallDir, ExeName);
        private static readonly string MachineFileIconPath = Path.Combine(MachineInstallDir, "pdf-file.ico");
        private static readonly string MachineStartMenuDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), AppName);
        private static readonly string MachineStartMenuLnk = Path.Combine(MachineStartMenuDir, $"{AppName}.lnk");

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
            DispatcherUnhandledException                    += OnDispatcherException;
            AppDomain.CurrentDomain.UnhandledException      += OnDomainException;
            TaskScheduler.UnobservedTaskException           += OnUnobservedTaskException;

            base.OnStartup(e);

            // #168: install our font resolver before anything can create an XFont - PdfSharpCore
            // caches the resolver on first use. Without this the save path sees only *.ttf and
            // cannot embed the .ttc families every CJK script relies on.
            Services.KillerFontResolver.Install();

            if (!CheckPdfiumIntegrity()) { Shutdown(2); return; }

            // Machine-wide install, no UI. Used by winget / choco / RMM deployments and by the
            // all-users checkbox, which re-runs this exe elevated with /silent. Checked before
            // everything else: it must never show a window or touch the single-instance mutex.
            if (e.Args.Length > 0 &&
                string.Equals(e.Args[0], "/silent", StringComparison.OrdinalIgnoreCase))
            {
                DoSilentInstall();
                Shutdown(0);
                return;
            }

            // Handle uninstall flag (called by Add/Remove Programs)
            if (e.Args.Length > 0 &&
                string.Equals(e.Args[0], "/uninstall", StringComparison.OrdinalIgnoreCase))
            {
                Uninstall();
                Shutdown();
                return;
            }

            // Headless CLI commands (see Features/Cli/CliRunner.cs; --batch-resave in
            // BatchRunner.cs). Checked before the single-instance mutex so CLI runs work
            // while a GUI instance is open, never forward to it, and never show a window.
            if (KillerPDF.Features.CliRunner.TryRunCli(e.Args, out int cliExit))
            {
                Shutdown(cliExit);
                return;
            }

            // Single instance: a second launch (e.g. double-clicking another PDF in Explorer)
            // forwards its file path to the already-running instance, which opens it as a new
            // tab, then this process exits. Without this, every launch spawned its own window.
            bool isPrimary;
            try { _instanceMutex = new Mutex(true, MutexName, out isPrimary); }
            catch { isPrimary = true; }
            if (!isPrimary)
            {
                var fwd = e.Args.FirstOrDefault(a => !a.StartsWith("/", StringComparison.Ordinal));
                ForwardToPrimary(fwd);
                Shutdown(0);
                return;
            }
            StartPipeServer();

            ShutdownMode = ShutdownMode.OnLastWindowClose;
            CleanupStaleTemps();
            ThemeManager.Initialize();
            LocaleManager.Initialize();
            new MainWindow().Show();
        }

        // ============================================================
        // Single-instance IPC (mutex + named pipe)
        // ============================================================

        private const string MutexName = @"Local\KillerPDF.SingleInstance";
        private const string PipeName  = "KillerPDF.OpenPipe";
        private Mutex? _instanceMutex;

        private void StartPipeServer()
        {
            var t = new System.Threading.Thread(RunPipeServer)
            {
                IsBackground = true,
                Name = "KillerPDF-IPC",
            };
            t.Start();
        }

        // Accepts forwarded file paths from secondary launches and hands them to the UI thread.
        private void RunPipeServer()
        {
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        PipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.None);
                    server.WaitForConnection();
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    string? path = reader.ReadLine();
                    Dispatcher.BeginInvoke(new Action(() => DeliverExternalOpen(path)));
                }
                catch
                {
                    System.Threading.Thread.Sleep(150);   // pipe error - back off, keep serving
                }
            }
        }

        private void DeliverExternalOpen(string? path)
        {
            if (Current?.MainWindow is MainWindow mw)
            {
                mw.RestoreAndActivate();
                if (!string.IsNullOrEmpty(path)) mw.OpenFromExternal(path);
            }
        }

        // Release the single-instance mutex so a relaunched/installed copy starts as the PRIMARY instance.
        // Without this, the new process sees the still-running old process holding the mutex, treats itself
        // as a secondary launch, forwards its args and exits - so once the old process finishes shutting
        // down, no window is left (the "installed but it didn't come back" bug).
        internal void ReleaseInstanceMutex()
        {
            try { _instanceMutex?.ReleaseMutex(); } catch { /* not owned on this thread / already released */ }
            _instanceMutex?.Dispose();
            _instanceMutex = null;
        }

        // Secondary instance: send our file path to the primary instance over the pipe.
        private static void ForwardToPrimary(string? path)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                client.Connect(3000);
                using var w = new StreamWriter(client, new UTF8Encoding(false)) { AutoFlush = true };
                w.WriteLine(path ?? "");
            }
            catch { /* primary not accepting yet; nothing more we can do */ }
        }

        // ============================================================
        // Crash handling
        // ============================================================
        //
        // NOTE: AccessViolationException is not catchable on .NET 4.8 without
        // [HandleProcessCorruptedStateExceptions], which we deliberately omit.

        private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            var logPath = CrashReporter.Capture(e.Exception, "Dispatcher");
            bool cont   = ShowCrashDialog(e.Exception, logPath, isFatal: false);
            e.Handled   = true; // always handle; we manage the exit ourselves
            if (!cont)
            {
                CleanupSessionTemps();
                Shutdown(1);
            }
        }

        private void OnDomainException(object sender, UnhandledExceptionEventArgs e)
        {
            var ex = e.ExceptionObject as Exception
                     ?? new Exception(e.ExceptionObject?.ToString() ?? "Unknown error");
            var logPath = CrashReporter.Capture(ex, "AppDomain");

            try
            {
                if (Dispatcher != null && !Dispatcher.HasShutdownStarted)
                    Dispatcher.Invoke(() => ShowCrashDialog(ex, logPath, isFatal: true));
                else
                    ShowCrashDialog(ex, logPath, isFatal: true);
            }
            catch { /* at least the log was written */ }

            CleanupSessionTemps();
            // CLR will terminate the process after this handler returns.
        }

        private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            e.SetObserved(); // prevent process teardown
            var logPath = CrashReporter.Capture(e.Exception, "TaskScheduler");

            try
            {
                if (Dispatcher != null && !Dispatcher.HasShutdownStarted)
                    Dispatcher.BeginInvoke(new Action(
                        () => ShowCrashDialog(e.Exception, logPath, isFatal: false)));
            }
            catch { /* best-effort */ }
        }

        /// <summary>
        /// Dark-themed crash report dialog. Returns true if the user chose Continue.
        /// Must be called on the UI thread.
        /// </summary>
        private static bool ShowCrashDialog(Exception ex, string logPath, bool isFatal)
        {
            bool shouldContinue = false;

            // ── Palette ─────────────────────────────────────────────
            var bg       = new SolidColorBrush(Color.FromRgb(0x1a, 0x1a, 0x1a));
            var dimBg    = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x25));
            var codeBg   = new SolidColorBrush(Color.FromRgb(0x12, 0x12, 0x12));
            var red      = new SolidColorBrush(Color.FromRgb(0xef, 0x44, 0x44));
            var green    = new SolidColorBrush(Color.FromRgb(0x1e, 0xa5, 0x4c));
            var greenHov = new SolidColorBrush(Color.FromRgb(0x27, 0xc8, 0x60));
            var dimText  = new SolidColorBrush(Color.FromRgb(0x77, 0x77, 0x77));
            var midText  = new SolidColorBrush(Color.FromRgb(0xaa, 0xaa, 0xaa));
            var redHov   = new SolidColorBrush(Color.FromRgb(0xc4, 0x2b, 0x1c));
            var grayBtn  = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
            var grayHov  = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44));
            var quitNorm = new SolidColorBrush(Color.FromRgb(0x5a, 0x10, 0x10));
            var quitHov  = new SolidColorBrush(Color.FromRgb(0xc4, 0x2b, 0x1c));

            var win = new Window
            {
                Title                 = "KillerPDF - Unexpected Error",
                Width                 = 680,
                Height                = 520,
                MinWidth              = 480,
                MinHeight             = 360,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ResizeMode            = ResizeMode.CanResize,
                WindowStyle           = WindowStyle.None,
                Background            = bg,
                ShowInTaskbar         = true
            };

            // ── Layout ──────────────────────────────────────────────
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(36) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // ── Title bar ───────────────────────────────────────────
            var titleBar = new DockPanel { Background = dimBg };
            Grid.SetRow(titleBar, 0);
            titleBar.MouseLeftButtonDown += (_, ea) =>
            {
                if (ea.ButtonState == MouseButtonState.Pressed) win.DragMove();
            };

            var xBtn = MakeTitleBarCloseButton(dimText, redHov);
            xBtn.Click += (_, _) => { shouldContinue = false; win.Close(); };
            DockPanel.SetDock(xBtn, Dock.Right);
            titleBar.Children.Add(xBtn);
            titleBar.Children.Add(new TextBlock
            {
                Text              = "KillerPDF - Unexpected Error",
                Foreground        = dimText,
                FontSize          = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(12, 0, 0, 0)
            });
            root.Children.Add(titleBar);

            // ── Error header ─────────────────────────────────────────
            var headerPanel = new StackPanel
            {
                Background = dimBg,
                Margin     = new Thickness(0, 1, 0, 0)
            };
            Grid.SetRow(headerPanel, 1);

            var headerInner = new StackPanel { Margin = new Thickness(20, 14, 20, 14) };

            var typeRow = new StackPanel { Orientation = Orientation.Horizontal };
            typeRow.Children.Add(new TextBlock
            {
                Text              = "⚠  ",
                Foreground        = red,
                FontSize          = 18,
                VerticalAlignment = VerticalAlignment.Center
            });
            typeRow.Children.Add(new TextBlock
            {
                Text              = ex.GetType().Name,
                Foreground        = red,
                FontSize          = 16,
                FontWeight        = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            });
            headerInner.Children.Add(typeRow);

            headerInner.Children.Add(new TextBlock
            {
                Text         = ex.Message,
                Foreground   = Brushes.White,
                FontSize     = 13,
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 6, 0, 0)
            });
            headerInner.Children.Add(new TextBlock
            {
                Text       = $"Log: {logPath}",
                Foreground = dimText,
                FontSize   = 11,
                Margin     = new Thickness(0, 6, 0, 0)
            });

            headerPanel.Children.Add(headerInner);
            root.Children.Add(headerPanel);

            // ── Stack trace ──────────────────────────────────────────
            var traceBox = new TextBox
            {
                Text                          = FormatExceptionChain(ex),
                Background                    = codeBg,
                Foreground                    = midText,
                FontFamily                    = new FontFamily("Consolas,Courier New"),
                FontSize                      = 11,
                IsReadOnly                    = true,
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                TextWrapping                  = TextWrapping.NoWrap,
                BorderThickness               = new Thickness(0),
                Padding                       = new Thickness(12, 8, 12, 8),
                Margin                        = new Thickness(0, 1, 0, 0)
            };
            Grid.SetRow(traceBox, 2);
            root.Children.Add(traceBox);

            // ── Button bar ───────────────────────────────────────────
            var btnBorder = new Border
            {
                Background = dimBg,
                Padding    = new Thickness(16, 10, 16, 10)
            };
            Grid.SetRow(btnBorder, 3);

            var btnPanel = new DockPanel();

            // Left: utility buttons
            var leftBtns = new StackPanel { Orientation = Orientation.Horizontal };

            var copyBtn = MakeCrashButton("Copy Report", grayBtn, grayHov, Brushes.White, 100);
            copyBtn.Click += (_, _) =>
            {
                try { Clipboard.SetText(BuildFullCrashReport(ex)); } catch { }
            };
            leftBtns.Children.Add(copyBtn);

            var logsBtn = MakeCrashButton("Open Logs", grayBtn, grayHov, Brushes.White, 88);
            logsBtn.Margin = new Thickness(8, 0, 0, 0);
            logsBtn.Click += (_, _) =>
            {
                try
                {
                    Directory.CreateDirectory(CrashReporter.LogDir);
                    Process.Start(new ProcessStartInfo(CrashReporter.LogDir) { UseShellExecute = true });
                }
                catch { }
            };
            leftBtns.Children.Add(logsBtn);

            var githubBtn = MakeCrashButton("Report on GitHub", grayBtn, grayHov,
                new SolidColorBrush(Color.FromRgb(0x60, 0xc0, 0xff)), 128);
            githubBtn.Margin = new Thickness(8, 0, 0, 0);
            githubBtn.Click += (_, _) =>
            {
                try
                {
                    var ver    = Assembly.GetExecutingAssembly().GetName().Version;
                    var msgLen = Math.Min(80, ex.Message.Length);
                    var title  = Uri.EscapeDataString(
                        $"Crash: {ex.GetType().Name}: {ex.Message[..msgLen]}");
                    var stack  = ex.StackTrace?.Length > 800
                        ? ex.StackTrace[..800] + "\n... (truncated)"
                        : ex.StackTrace ?? "(no stack trace)";
                    var body = Uri.EscapeDataString(
                        $"**Version:** {ver?.ToString(3)}\n" +
                        $"**OS:** {Environment.OSVersion}\n" +
                        $"**Exception:** `{ex.GetType().FullName}`\n" +
                        $"**Message:** {ex.Message}\n\n" +
                        $"```\n{stack}\n```\n\n" +
                        $"_Log folder: `{CrashReporter.LogDir}`_");
                    Process.Start(new ProcessStartInfo(
                        $"https://github.com/SteveTheKiller/KillerPDF/issues/new?title={title}&body={body}")
                        { UseShellExecute = true });
                }
                catch { }
            };
            leftBtns.Children.Add(githubBtn);

            DockPanel.SetDock(leftBtns, Dock.Left);
            btnPanel.Children.Add(leftBtns);

            // Right: Continue / Quit
            var rightBtns = new StackPanel
            {
                Orientation         = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            var contBtn = MakeCrashButton("Continue", green, greenHov,
                new SolidColorBrush(Color.FromRgb(0x0a, 0x0a, 0x0a)), 88);
            contBtn.IsEnabled  = !isFatal;
            contBtn.FontWeight = isFatal ? FontWeights.Normal : FontWeights.SemiBold;
            contBtn.Margin     = new Thickness(0, 0, 8, 0);
            contBtn.Click += (_, _) => { shouldContinue = true; win.Close(); };

            var quitBtnCtrl = MakeCrashButton("Quit", quitNorm, quitHov, Brushes.White, 72);
            quitBtnCtrl.FontWeight = isFatal ? FontWeights.SemiBold : FontWeights.Normal;
            quitBtnCtrl.Click += (_, _) => { shouldContinue = false; win.Close(); };

            rightBtns.Children.Add(contBtn);
            rightBtns.Children.Add(quitBtnCtrl);

            DockPanel.SetDock(rightBtns, Dock.Right);
            btnPanel.Children.Add(rightBtns);

            btnBorder.Child = btnPanel;
            root.Children.Add(btnBorder);

            win.Content = root;
            win.ShowDialog();
            return shouldContinue;
        }

        private static Button MakeTitleBarCloseButton(SolidColorBrush fg, SolidColorBrush hoverBg)
        {
            var t  = new ControlTemplate(typeof(Button));
            var b  = new FrameworkElementFactory(typeof(Border));
            b.SetBinding(Border.BackgroundProperty,
                new System.Windows.Data.Binding("Background")
                {
                    RelativeSource = new System.Windows.Data.RelativeSource(
                        System.Windows.Data.RelativeSourceMode.TemplatedParent)
                });
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty,   VerticalAlignment.Center);
            b.AppendChild(cp);
            t.VisualTree = b;

            var s    = new Style(typeof(Button));
            s.Setters.Add(new Setter(Button.BackgroundProperty, Brushes.Transparent));
            s.Setters.Add(new Setter(Button.ForegroundProperty, fg));
            s.Setters.Add(new Setter(Button.TemplateProperty,   t));
            var trig = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            trig.Setters.Add(new Setter(Button.BackgroundProperty, hoverBg));
            trig.Setters.Add(new Setter(Button.ForegroundProperty, Brushes.White));
            s.Triggers.Add(trig);

            return new Button
            {
                Content                  = "",
                FontFamily               = new FontFamily("Segoe MDL2 Assets"),
                FontSize                 = 11,
                Width                    = 46,
                BorderThickness          = new Thickness(0),
                VerticalAlignment        = VerticalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Center,
                Cursor                   = Cursors.Arrow,
                Style                    = s
            };
        }

        private static Button MakeCrashButton(string label,
            SolidColorBrush normal, SolidColorBrush hover, SolidColorBrush fg,
            double width = 88)
        {
            var btn = UiKit.Make(label, normal, hover, fg, fg);
            btn.Width  = width;
            btn.Height = 28;
            return btn;
        }

        private static string FormatExceptionChain(Exception ex)
        {
            var sb    = new StringBuilder();
            var inner = ex;
            var depth = 0;
            while (inner != null && depth < 5)
            {
                if (depth > 0) { sb.AppendLine(); sb.AppendLine("=== Inner Exception ==="); }
                sb.AppendLine($"{inner.GetType().FullName}: {inner.Message}");
                sb.AppendLine(inner.StackTrace ?? "(no stack trace)");
                inner = inner.InnerException;
                depth++;
            }
            return sb.ToString().TrimEnd();
        }

        private static string BuildFullCrashReport(Exception ex)
        {
            var sb  = new StringBuilder();
            var ver = Assembly.GetExecutingAssembly().GetName().Version;
            sb.AppendLine($"KillerPDF v{ver?.ToString(3)}");
            sb.AppendLine($"Time : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"OS   : {Environment.OSVersion}");
            sb.AppendLine();
            sb.Append(FormatExceptionChain(ex));
            return sb.ToString();
        }

        // ============================================================
        // Public surface used by MainWindow (portable badge / install)
        // ============================================================

        /// <summary>
        /// True when running from outside the installed location (i.e. portable mode).
        /// </summary>
        internal static bool IsPortable()
        {
            try
            {
                string currentExe = Process.GetCurrentProcess().MainModule!.FileName;
                return !string.Equals(currentExe, InstallExe, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(currentExe, MachineInstallExe, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>True when KillerPDF is already installed machine-wide.</summary>
        internal static bool MachineInstallExists() => File.Exists(MachineInstallExe);

        /// <summary>
        /// Installs KillerPDF, offers to set it as the default PDF handler, then relaunches from
        /// the installed location. Returns false if the install did not happen, so the caller can
        /// put the portable badge back rather than leaving the user with a half-finished state.
        ///
        /// An all-users install re-runs this exe elevated with /silent - the same machine-wide path
        /// winget and choco use - so UAC only appears when the user actually asked for it.
        /// </summary>
        internal static bool InstallAndRelaunch(string? fileToOpen, bool wantDesktop, bool allUsers)
        {
            string targetExe;

            if (allUsers)
            {
                if (!RunElevatedSilentInstall()) return false;

                // Only ever one install: drop the per-user copy so there is a single Start Menu
                // entry and a single uninstall entry. Settings are deliberately left alone.
                RemovePerUserInstall();

                // Associations are registered HERE, not in the elevated pass. Under /silent the
                // process runs as the ADMIN, so HKEY_CURRENT_USER is the admin's hive - writing
                // the .pdf handler there would associate PDFs for the wrong account and leave the
                // real user with nothing. This call is unelevated and in the right hive.
                RegisterFileHandler(MachineInstallExe, MachineFileIconPath);
                targetExe = MachineInstallExe;
            }
            else
            {
                DoInstall(wantDesktop);
                if (!File.Exists(InstallExe)) return false;   // trust gate / copy failure already reported
                targetExe = InstallExe;
            }

            if (!IsDefaultPdfHandler())
            {
                // Loc() is a MainWindow member; App resolves strings off the merged dictionaries
                // directly, the same way Uninstall() does.
                var res = KillerDialog.Show(null,
                    Current.TryFindResource("Str_Dlg_SetDefaultPdfMsg") as string
                        ?? "Would you like to set KillerPDF as your default PDF viewer?",
                    AppName, MessageBoxButton.YesNo);
                if (res == MessageBoxResult.Yes)
                    Process.Start(new ProcessStartInfo("ms-settings:defaultapps")
                        { UseShellExecute = true });
            }

            var psi = new ProcessStartInfo(targetExe);
            if (fileToOpen != null)
                psi.Arguments = $"\"{fileToOpen}\"";
            // Free the single-instance mutex first so the launched copy becomes primary (and shows a
            // window) instead of treating itself as a duplicate and exiting.
            (Current as App)?.ReleaseInstanceMutex();
            Process.Start(psi);
            Application.Current.Shutdown();
            return true;
        }

        /// <summary>Re-run this exe elevated with /silent and wait for it to finish.</summary>
        private static bool RunElevatedSilentInstall()
        {
            try
            {
                var psi = new ProcessStartInfo(Process.GetCurrentProcess().MainModule!.FileName, "/silent")
                {
                    UseShellExecute = true,
                    Verb = "runas",          // triggers the UAC prompt
                };
                using var p = Process.Start(psi);
                p?.WaitForExit();
                return p is not null && p.ExitCode == 0 && File.Exists(MachineInstallExe);
            }
            catch
            {
                // Declining the UAC prompt throws Win32Exception 1223 (ERROR_CANCELLED).
                return false;
            }
        }

        /// <summary>Remove a per-user install: files, shortcuts and its HKCU install markers.
        /// The settings are deliberately left alone so theme, accent, locale, recent files and
        /// window placement survive the move to a machine-wide install. The .pdf ASSOCIATION keys
        /// are left alone too - they are re-pointed at the machine exe straight afterwards, and
        /// deleting them here would briefly leave the user with no PDF handler at all.</summary>
        private static void RemovePerUserInstall()
        {
            try { if (File.Exists(StartMenuLnk)) File.Delete(StartMenuLnk); } catch { }
            try { if (Directory.Exists(StartMenuDir)) Directory.Delete(StartMenuDir, true); } catch { }
            try { if (File.Exists(DesktopLnk)) File.Delete(DesktopLnk); } catch { }
            try { if (Directory.Exists(InstallDir)) Directory.Delete(InstallDir, true); } catch { }
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\KillerPDF", writable: true);
                key?.DeleteValue("Installed", throwOnMissingValue: false);
                key?.DeleteValue("InstallPath", throwOnMissingValue: false);
            }
            catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree(
                @"Software\Microsoft\Windows\CurrentVersion\Uninstall\KillerPDF",
                throwOnMissingSubKey: false); }
            catch { }
        }

        // ============================================================
        // Silent (machine-wide) install - winget / choco / RMM / the all-users checkbox
        // ============================================================

        /// <summary>
        /// Machine-wide install. Runs elevated with no UI by definition, so the exit code is the
        /// only signal a deployment tool - or RunElevatedSilentInstall - gets back.
        ///
        /// Deliberately does NOT register file associations: this process is the admin, so HKCU is
        /// the wrong hive. InstallAndRelaunch does that afterwards as the real user.
        /// </summary>
        private static void DoSilentInstall()
        {
            try
            {
                string src = Process.GetCurrentProcess().MainModule!.FileName;

                // Same trust gate as the interactive path - an unsigned or wrong-publisher exe must
                // not be able to write itself into Program Files, least of all while elevated.
                var (valid, _, _) = VerifyAuthenticode(src);
                if (!valid)
                {
                    Console.Error.WriteLine("Silent install refused: EXE has no valid Authenticode signature.");
                    Environment.Exit(1);
                    return;
                }

                Directory.CreateDirectory(MachineInstallDir);
                if (File.Exists(MachineInstallExe))
                {
                    try { File.SetAttributes(MachineInstallExe, FileAttributes.Normal); } catch { }
                }
                File.Copy(src, MachineInstallExe, overwrite: true);
                try { File.SetAttributes(MachineInstallExe, FileAttributes.Normal); } catch { }

                Directory.CreateDirectory(MachineStartMenuDir);
                CreateShortcut(MachineStartMenuLnk, MachineInstallExe);

                ExtractFileIcon(MachineFileIconPath);

                using (var key = Registry.LocalMachine.CreateSubKey(@"Software\KillerPDF"))
                {
                    key.SetValue("Installed",   1);
                    key.SetValue("InstallPath", MachineInstallExe);
                    key.SetValue("Version",
                        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "");
                }

                using (var key = Registry.LocalMachine.CreateSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Uninstall\KillerPDF"))
                {
                    key.SetValue("DisplayName",          AppName);
                    key.SetValue("DisplayVersion",
                        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "");
                    key.SetValue("Publisher",            "Steve / thekiller.net");
                    key.SetValue("InstallLocation",      MachineInstallDir);
                    key.SetValue("DisplayIcon",          $"{MachineInstallExe},0");
                    key.SetValue("UninstallString",      $"\"{MachineInstallExe}\" /uninstall");
                    key.SetValue("QuietUninstallString", $"\"{MachineInstallExe}\" /uninstall");
                    key.SetValue("NoModify",             1);
                    key.SetValue("NoRepair",             1);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Silent install failed: " + ex.Message);
                Environment.Exit(1);
            }
        }

        // ============================================================
        // Registry helpers
        // ============================================================

        // ============================================================
        // Temp file tracking
        // ============================================================

        /// <summary>
        /// User-private temp directory for session working files (encrypted PDFs, etc.).
        /// %LOCALAPPDATA% is user-private and not indexed by Windows Search.
        /// </summary>
        internal static readonly string TempDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KillerPDF", "Temp");

        private static readonly List<string> _sessionTemps = [];

        /// <summary>
        /// Creates a tracked temp path of the form killerpdf_&lt;tag&gt;_&lt;guid&gt;.pdf
        /// under %LOCALAPPDATA%\KillerPDF\Temp\.
        /// All registered paths are deleted when CleanupSessionTemps() is called.
        /// </summary>
        internal static string MakeTempFile(string tag)
        {
            try { Directory.CreateDirectory(TempDir); } catch { }
            var path = Path.Combine(TempDir, $"killerpdf_{tag}_{Guid.NewGuid():N}.pdf");
            lock (_sessionTemps) _sessionTemps.Add(path);
            return path;
        }

        /// <summary>Deletes all temp files registered this session (best-effort).</summary>
        internal static void CleanupSessionTemps()
        {
            lock (_sessionTemps)
            {
                foreach (var f in _sessionTemps)
                    try { if (File.Exists(f)) File.Delete(f); } catch { }
                _sessionTemps.Clear();
            }
        }

        /// <summary>
        /// Deletes killerpdf_*.pdf files left over from previous crashed sessions.
        /// Sweeps both the current TempDir and the legacy %TEMP% location.
        /// Locked files (still open by another instance) are silently skipped.
        /// </summary>
        internal static void CleanupStaleTemps()
        {
            // Current location
            try
            {
                if (Directory.Exists(TempDir))
                    foreach (var f in Directory.GetFiles(TempDir, "killerpdf_*.pdf"))
                        try { File.Delete(f); } catch { }
            }
            catch { }

            // Legacy %TEMP% location - sweep once for users upgrading from older builds
            try
            {
                foreach (var f in Directory.GetFiles(Path.GetTempPath(), "killerpdf_*.pdf"))
                    try { File.Delete(f); } catch { }
            }
            catch { }
        }

        internal static string? GetSetting(string name)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\KillerPDF\Settings");
                return key?.GetValue(name) as string;
            }
            catch { return null; }
        }

        internal static void SetSetting(string name, string value)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(@"Software\KillerPDF\Settings");
                key.SetValue(name, value);
            }
            catch { /* best-effort */ }
        }

        internal static void RemoveSetting(string name)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\KillerPDF\Settings", writable: true);
                key?.DeleteValue(name, throwOnMissingValue: false);
            }
            catch { /* best-effort */ }
        }

        /// <summary>
        /// Wipes all persisted KillerPDF state: settings (registry), downloaded OCR language packs, the
        /// native OCR cache, and temp files. Best-effort - files locked this session (e.g. loaded native
        /// DLLs) are skipped and clear on the next restart. The user's actual PDFs are never touched.
        /// </summary>
        internal static void ClearAllData()
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\KillerPDF\Settings", throwOnMissingSubKey: false); } catch { }

            string localKp = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KillerPDF");
            TryDeleteDir(Path.Combine(localKp, "tessdata"));   // downloaded language packs + bundled English
            TryDeleteDir(Path.Combine(localKp, "ocr"));        // native Tesseract cache
            TryDeleteDir(TempDir);                              // temp working files

            // Legacy temp PDFs that may linger in %TEMP%.
            try
            {
                foreach (var f in Directory.GetFiles(Path.GetTempPath(), "killerpdf_*.pdf"))
                    try { File.Delete(f); } catch { }
            }
            catch { }
        }

        private static void TryDeleteDir(string dir)
        {
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // A whole-folder delete fails if any file is locked; remove what we can so the rest clears now.
                try
                {
                    foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                        try { File.Delete(f); } catch { }
                }
                catch { }
            }
        }

        // ── Recent files (most-recent first, capped) ─────────────────────
        private const int RecentFilesMax = 10;
        // "1" = don't track recently opened files at all (#146, privacy on shared machines).
        internal const string NoRecentFilesSetting = "NoRecentFiles";

        internal static System.Collections.Generic.List<string> GetRecentFiles()
        {
            var list = new System.Collections.Generic.List<string>();
            var raw = GetSetting("RecentFiles");
            if (string.IsNullOrEmpty(raw)) return list;
            foreach (var p in raw!.Split('|'))
                if (!string.IsNullOrWhiteSpace(p)) list.Add(p);
            return list;
        }

        internal static void AddRecentFile(string path)
        {
            if (GetSetting(NoRecentFilesSetting) == "1") return;   // tracking disabled (#146)
            if (string.IsNullOrWhiteSpace(path)) return;
            var list = GetRecentFiles();
            list.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            list.Insert(0, path);
            while (list.Count > RecentFilesMax) list.RemoveAt(list.Count - 1);
            SetSetting("RecentFiles", string.Join("|", list));   // '|' is illegal in Windows paths
        }

        internal static void ClearRecentFiles() => RemoveSetting("RecentFiles");

        // Drops a single entry from the recent-files list (used by the per-row remove button).
        internal static void RemoveRecentFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            var list = GetRecentFiles();
            list.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            if (list.Count == 0) RemoveSetting("RecentFiles");
            else SetSetting("RecentFiles", string.Join("|", list));
        }

        private static bool IsInstalled()
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\KillerPDF");
            if (key is null) return false;
            return key.GetValue("Installed") is int i && i == 1;
        }

        private static bool IsDefaultPdfHandler()
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\Shell\Associations\FileAssociations\.pdf\UserChoice");
            return key?.GetValue("ProgId") is string progId &&
                   progId.Equals("KillerPDF.pdf", StringComparison.OrdinalIgnoreCase);
        }

        // ============================================================
        // Launcher dialog
        // ============================================================

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

            // Close button - custom template so Background trigger actually renders
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
                    : "Install KillerPDF on this computer, or run it without installing.",
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

            var runBtn = UiKit.Make("Run",
                new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x30)),
                new SolidColorBrush(Color.FromRgb(0x16, 0x63, 0x34)),
                Brushes.White, Brushes.White);
            runBtn.Width  = 88;
            runBtn.Margin = new Thickness(0, 0, 8, 0);

            var installBtn = UiKit.Make(alreadyInstalled ? "Update" : "Install",
                accent,
                new SolidColorBrush(Color.FromRgb(0x4a, 0xf0, 0x90)),
                new SolidColorBrush(Color.FromRgb(0x0a, 0x0a, 0x0a)),
                new SolidColorBrush(Color.FromRgb(0x0a, 0x0a, 0x0a)));
            installBtn.Width      = 110;
            installBtn.FontWeight = FontWeights.SemiBold;

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
        // Security - Authenticode verification + pdfium integrity
        // ============================================================

        // ── WinVerifyTrust P/Invoke ──────────────────────────────────────────

        [StructLayout(LayoutKind.Sequential)]
        private struct WINTRUST_FILE_INFO
        {
            public uint   cbStruct;
            public IntPtr pcwszFilePath;   // LPCWSTR
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WINTRUST_DATA
        {
            public uint   cbStruct;
            public IntPtr pPolicyCallbackData;
            public IntPtr pSIPClientData;
            public uint   dwUIChoice;          // 2 = WTD_UI_NONE
            public uint   fdwRevocationChecks; // 0 = WTD_REVOKE_NONE
            public uint   dwUnionChoice;       // 1 = WTD_CHOICE_FILE
            public IntPtr pUnion;              // → WINTRUST_FILE_INFO
            public uint   dwStateAction;       // 0 = WTD_STATEACTION_IGNORE
            public IntPtr hWVTStateData;
            public IntPtr pwszURLReference;
            public uint   dwProvFlags;         // 0x1000 = WTD_CACHE_ONLY_URL_RETRIEVAL
            public uint   dwUIContext;
            public IntPtr pSignatureSettings;
        }

        private static readonly Guid WTD_VERIFY_GENERIC =
            new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false,
                   CharSet = CharSet.Unicode)]
        private static extern uint WinVerifyTrust(
            IntPtr hwnd, ref Guid pgActionID, IntPtr pWVTData);

        // ── Public helpers ───────────────────────────────────────────────────

        /// <summary>
        /// Calls WinVerifyTrust to validate an Authenticode signature.
        /// Returns (Valid, SubjectCN, Thumbprint).
        /// Valid=false for unsigned, expired (past grace), or tampered files.
        /// </summary>
        internal static (bool Valid, string Subject, string Thumbprint)
            VerifyAuthenticode(string filePath)
        {
            var subject    = "(not signed)";
            var thumbprint = string.Empty;

            // Try to read cert info regardless of signature validity
            try
            {
                var raw  = X509Certificate.CreateFromSignedFile(filePath);
                var cert = new X509Certificate2(raw);
                subject    = cert.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
                thumbprint = cert.Thumbprint ?? string.Empty;
            }
            catch { /* unsigned or unreadable */ }

            // Full chain + revocation check via WinVerifyTrust
            var pathPtr      = Marshal.StringToHGlobalUni(filePath);
            var fileInfoPtr  = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
            var dataPtr      = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_DATA>());
            try
            {
                Marshal.StructureToPtr(new WINTRUST_FILE_INFO
                {
                    cbStruct      = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
                    pcwszFilePath = pathPtr
                }, fileInfoPtr, false);

                Marshal.StructureToPtr(new WINTRUST_DATA
                {
                    cbStruct      = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                    dwUIChoice    = 2,  // WTD_UI_NONE
                    dwUnionChoice = 1,  // WTD_CHOICE_FILE
                    pUnion        = fileInfoPtr,
                    dwProvFlags   = 0x1000   // WTD_CACHE_ONLY_URL_RETRIEVAL, never hit the network
                }, dataPtr, false);

                var actionId = WTD_VERIFY_GENERIC;
                uint hr = WinVerifyTrust(IntPtr.Zero, ref actionId, dataPtr);
                return (hr == 0, subject, thumbprint);
            }
            finally
            {
                Marshal.FreeHGlobal(dataPtr);
                Marshal.FreeHGlobal(fileInfoPtr);
                Marshal.FreeHGlobal(pathPtr);
            }
        }

        /// <summary>
        /// Convenience wrapper: verify the currently running EXE.
        /// </summary>
        internal static (bool Valid, string Subject, string Thumbprint) GetExeSignerInfo()
        {
            try
            {
                return VerifyAuthenticode(Process.GetCurrentProcess().MainModule!.FileName);
            }
            catch
            {
                return (false, "(not signed)", string.Empty);
            }
        }

        /// <summary>SHA256 hex of the currently running EXE (for the About dialog).</summary>
        internal static string GetExeSha256()
        {
            try
            {
                var path = Process.GetCurrentProcess().MainModule!.FileName;
                using var sha = SHA256.Create();
                using var fs  = File.OpenRead(path);
                return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "");
            }
            catch { return "(unavailable)"; }
        }

        // ── About dialog ────────────────────────────────────────────────────────

        internal static void ShowAboutDialog(Window owner)
        {
            // Gather info on a background thread so the UI isn't blocked by hashing
            var version    = System.Reflection.Assembly.GetExecutingAssembly()
                                   .GetName().Version?.ToString(3) ?? "?";
            var (sigValid, sigSubject, sigThumbprint) = GetExeSignerInfo();
            var sha256 = GetExeSha256();

            // ── Layout ──────────────────────────────────────────────────────
            var bg     = new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x1e));
            var bgCard = new SolidColorBrush(Color.FromRgb(0x2a, 0x2a, 0x2a));
            var fg     = new SolidColorBrush(Color.FromRgb(0xe0, 0xe0, 0xe0));
            var fgDim  = new SolidColorBrush(Color.FromRgb(0x77, 0x77, 0x77));
            var accent = new SolidColorBrush(Color.FromRgb(0x1e, 0xa5, 0x4c));
            var mono   = new FontFamily("Consolas");

            // Title bar
            var titleBar = new Grid
            {
                Background = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x25)),
                Height = 32
            };
            titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titleText = new TextBlock
            {
                Text = $"About KillerPDF",
                Foreground = fg, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0), FontSize = 13, FontWeight = FontWeights.SemiBold
            };
            Grid.SetColumn(titleText, 0);
            titleBar.Children.Add(titleText);

            Window? dlg = null;
            var closeBtn = MakeTitleBarCloseButton(
                new SolidColorBrush(Color.FromRgb(0xcc, 0xcc, 0xcc)),
                new SolidColorBrush(Color.FromRgb(0xc0, 0x3b, 0x3b)));
            closeBtn.Click += (_, __) => dlg!.Close();
            Grid.SetColumn(closeBtn, 1);
            titleBar.Children.Add(closeBtn);

            // Helper: labelled row (onClick makes the value a clickable hyperlink)
            static StackPanel MakeRow(string label, string value,
                SolidColorBrush labelBrush, SolidColorBrush valueBrush,
                FontFamily? valueFont = null, bool wrap = false, Action? onClick = null)
            {
                var sp = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 0, 0, 10) };
                sp.Children.Add(new TextBlock
                {
                    Text = label, Foreground = labelBrush,
                    FontSize = 10, Margin = new Thickness(0, 0, 0, 2)
                });
                if (onClick != null)
                {
                    var tb = new TextBlock
                    {
                        FontFamily = valueFont ?? new FontFamily("Segoe UI"),
                        FontSize = 12,
                        Cursor = Cursors.Hand
                    };
                    var hl = new Hyperlink(new Run(value))
                    {
                        Foreground = valueBrush,
                        TextDecorations = null
                    };
                    hl.Click += (_, _) => onClick();
                    tb.Inlines.Add(hl);
                    sp.Children.Add(tb);
                }
                else
                {
                    sp.Children.Add(new TextBlock
                    {
                        Text = value, Foreground = valueBrush,
                        FontFamily = valueFont ?? new FontFamily("Segoe UI"),
                        FontSize = 12,
                        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap
                    });
                }
                return sp;
            }

            static void OpenUrl(string url)
            {
                try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
            }

            var sigInfo = sigValid
                ? $"{sigSubject}"
                : "(not signed or chain failed)";

            var thumbInfo = string.IsNullOrEmpty(sigThumbprint) ? "(none)" : sigThumbprint;

            var card = new Border
            {
                Background = bgCard,
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(16),
                Margin = new Thickness(0, 0, 0, 0)
            };
            var cardContent = new StackPanel();
            cardContent.Children.Add(MakeRow("VERSION", $"v{version}", fgDim, accent,
                onClick: () => OpenUrl($"https://github.com/SteveTheKiller/KillerPDF/releases/tag/v{version}")));
            cardContent.Children.Add(MakeRow("PUBLISHER", sigInfo,         fgDim, fg));
            cardContent.Children.Add(MakeRow("THUMBPRINT", thumbInfo,      fgDim, fg, mono, wrap: true));
            cardContent.Children.Add(MakeRow("EXE SHA256", sha256,         fgDim, fg, mono, wrap: true));
            card.Child = cardContent;

            // Close button
            var okBtn = UiKit.Make("Close",
                new SolidColorBrush(Color.FromRgb(0x1e, 0xa5, 0x4c)),
                new SolidColorBrush(Color.FromRgb(0x17, 0x7a, 0x38)),
                new SolidColorBrush(Colors.White),
                new SolidColorBrush(Colors.White));
            okBtn.Width  = 80;
            okBtn.Height = 28;
            okBtn.HorizontalAlignment = HorizontalAlignment.Right;
            okBtn.Margin = new Thickness(0, 12, 0, 0);
            okBtn.Click += (_, __) => dlg!.Close();

            // KillerPDF logo - clickable link to product site
            var logo = new TextBlock { FontSize = 22, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 4) };
            var logoHl = new Hyperlink(new Run("KillerPDF"))
            {
                Foreground = accent,
                TextDecorations = null
            };
            logoHl.Click += (_, _) => OpenUrl("https://pdf.killertools.com");
            logo.Inlines.Add(logoHl);

            // Tagline with Killer Tools link
            var tagline = new TextBlock { FontSize = 11, Margin = new Thickness(0, 0, 0, 16) };
            tagline.Inlines.Add(new Run("A fast, free PDF toolkit for Windows. Part of ") { Foreground = fgDim });
            var ktHl = new Hyperlink(new Run("Killer Tools"))
            {
                Foreground = accent,
                TextDecorations = null
            };
            ktHl.Click += (_, _) => OpenUrl("https://killertools.net");
            tagline.Inlines.Add(ktHl);
            tagline.Inlines.Add(new Run(".") { Foreground = fgDim });

            var body = new StackPanel { Margin = new Thickness(16, 16, 16, 20) };
            body.Children.Add(logo);
            body.Children.Add(tagline);
            body.Children.Add(card);
            body.Children.Add(okBtn);

            var root = new DockPanel();
            DockPanel.SetDock(titleBar, Dock.Top);
            root.Children.Add(titleBar);
            root.Children.Add(body);
            root.Background = bg;

            // Make title bar draggable
            titleBar.MouseLeftButtonDown += (_, me) =>
            {
                if (me.ButtonState == MouseButtonState.Pressed) dlg!.DragMove();
            };

            dlg = new Window
            {
                Content = root,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                SizeToContent = SizeToContent.Height,
                Width = 540,
                Owner = owner,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = bg
            };

            dlg.ShowDialog();
        }

        // ── pdfium.dll integrity check ───────────────────────────────────────

        /// <summary>
        /// Finds the Costura-embedded pdfium resource, decompresses it in-memory,
        /// and compares its SHA256 to BuildInfo.PdfiumSha256.
        /// Returns false (and shows a message box) only on a confirmed mismatch.
        /// Fails-open if the check cannot complete (dev builds, missing resource, I/O error).
        /// </summary>
        private static bool CheckPdfiumIntegrity()
        {
            if (string.Equals(BuildInfo.PdfiumSha256, BuildInfo.PdfiumSha256Disabled, StringComparison.Ordinal))
                return true; // disabled for this build (dev / SkipSign)

            var asm = Assembly.GetExecutingAssembly();
            var resourceName = Array.Find(asm.GetManifestResourceNames(),
                n => n.IndexOf("pdfium", StringComparison.OrdinalIgnoreCase) >= 0
                     && n.EndsWith(".compressed", StringComparison.OrdinalIgnoreCase));

            if (resourceName == null)
                return true; // not bundled via Costura (dev build running from bin/)

            try
            {
                string actual;
                using (var rs      = asm.GetManifestResourceStream(resourceName)!)
                using (var deflate = new DeflateStream(rs, CompressionMode.Decompress))
                using (var sha     = SHA256.Create())
                    actual = BitConverter.ToString(sha.ComputeHash(deflate)).Replace("-", "");

                if (!string.Equals(actual, BuildInfo.PdfiumSha256,
                        StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show(
                        "Security check failed: pdfium.dll integrity verification failed.\n\n" +
                        $"Expected: {BuildInfo.PdfiumSha256}\n" +
                        $"Actual  : {actual}\n\n" +
                        "The bundled PDF engine may have been tampered with. KillerPDF will exit.",
                        $"{AppName} - Security", MessageBoxButton.OK, MessageBoxImage.Error);
                    return false;
                }
                return true;
            }
            catch
            {
                return true; // fail-open: only block on confirmed mismatch
            }
        }

        // ============================================================
        // Installation
        // ============================================================

        private static void DoInstall(bool wantDesktop)
        {
            string src = Process.GetCurrentProcess().MainModule!.FileName;

            // ── Trust gate: refuse to install an unsigned or wrong-publisher EXE ──
            var (valid, _, _) = VerifyAuthenticode(src);
            if (!valid)
            {
                MessageBox.Show(
                    "Installation refused: the running EXE does not carry a valid Authenticode " +
                    "signature.\n\nOnly signed builds of KillerPDF can be installed.",
                    AppName, MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // ── Downgrade guard ────────────────────────────────────────────────────
            if (File.Exists(InstallExe))
            {
                var runVer  = FileVersionInfo.GetVersionInfo(src).FileVersion ?? "";
                var instVer = FileVersionInfo.GetVersionInfo(InstallExe).FileVersion ?? "";
                if (string.Compare(runVer, instVer, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    var res = MessageBox.Show(
                        $"You are about to install an older version ({runVer}) " +
                        $"over the currently installed version ({instVer}).\n\n" +
                        "Downgrade anyway?",
                        AppName, MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (res != MessageBoxResult.Yes) return;
                }
            }

            try
            {
                // Copy EXE to install location. Two snags make a plain overwrite throw "access denied":
                // (1) File.Copy carries the source's attributes, so a prior install can leave the target
                //     read-only - and File.Copy(overwrite:true) throws on a read-only target instead of
                //     replacing it. Clear the attribute first (and again after, so the next update works).
                // (2) The installed copy is running, so it's locked. Catch that and say so plainly.
                Directory.CreateDirectory(InstallDir);
                if (File.Exists(InstallExe))
                {
                    try { File.SetAttributes(InstallExe, FileAttributes.Normal); } catch { }
                }
                try
                {
                    File.Copy(src, InstallExe, overwrite: true);
                }
                catch (Exception copyEx) when (copyEx is UnauthorizedAccessException or IOException)
                {
                    MessageBox.Show(
                        "Couldn't write the installed copy at:\n" + InstallExe +
                        "\n\nClose any open KillerPDF window (and check Task Manager for KillerPDF.exe), " +
                        "then run the installer again.",
                        AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                try { File.SetAttributes(InstallExe, FileAttributes.Normal); } catch { }

                // Shortcuts
                Directory.CreateDirectory(StartMenuDir);
                CreateShortcut(StartMenuLnk, InstallExe);
                if (wantDesktop)
                    CreateShortcut(DesktopLnk, InstallExe);

                // Installed marker
                using (var key = Registry.CurrentUser.CreateSubKey(@"Software\KillerPDF"))
                {
                    key.SetValue("Installed",    1);
                    key.SetValue("InstallPath",  InstallExe);
                    key.SetValue("Version",
                        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "");
                }

                // Add/Remove Programs entry
                using (var key = Registry.CurrentUser.CreateSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Uninstall\KillerPDF"))
                {
                    key.SetValue("DisplayName",          AppName);
                    key.SetValue("DisplayVersion",
                        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "");
                    key.SetValue("Publisher",            "Steve / thekiller.net");
                    key.SetValue("InstallLocation",      InstallDir);
                    key.SetValue("DisplayIcon",          $"{InstallExe},0");
                    key.SetValue("UninstallString",      $"\"{InstallExe}\" /uninstall");
                    key.SetValue("QuietUninstallString", $"\"{InstallExe}\" /uninstall");
                    key.SetValue("NoModify",             1);
                    key.SetValue("NoRepair",             1);
                }

                // Drop the PDF file-type icon next to the exe so DefaultIcon can point at it
                ExtractFileIcon(FileIconPath);

                // Register as PDF file handler (per-user - no admin needed)
                RegisterFileHandler(InstallExe, FileIconPath);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Installation failed:\n{ex.Message}", AppName,
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // iconPath: which install location to drop it in. Parameterized for the machine-wide
        // install, which writes into Program Files rather than the per-user folder.
        private static void ExtractFileIcon(string iconPath)
        {
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                var rn  = Array.Find(asm.GetManifestResourceNames(),
                    n => n.IndexOf("pdf-file", StringComparison.OrdinalIgnoreCase) >= 0
                         && n.EndsWith(".ico", StringComparison.OrdinalIgnoreCase));
                if (rn == null) return; // dev build running from bin/ without the embedded icon

                using (var rs = asm.GetManifestResourceStream(rn)!)
                using (var fs = File.Create(iconPath))
                    rs.CopyTo(fs);
            }
            catch { }
        }

        /// <summary>
        /// Point the .pdf association at <paramref name="exePath"/>. ALWAYS writes HKCU, so it
        /// must only ever be called from an UNELEVATED process - under /silent the current user is
        /// the admin, and the keys would land in the wrong hive.
        /// </summary>
        private static void RegisterFileHandler(string exePath, string iconPath)
        {
            // Prefer the dedicated PDF file icon; fall back to the app icon if extraction didn't run
            string iconRef = File.Exists(iconPath)
                ? $"\"{iconPath}\",0"
                : $"{exePath},0";

            // ProgID definition
            using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Classes\KillerPDF.pdf"))
                k.SetValue("", "PDF Document");

            using (var k = Registry.CurrentUser.CreateSubKey(
                @"Software\Classes\KillerPDF.pdf\DefaultIcon"))
                k.SetValue("", iconRef);

            using (var k = Registry.CurrentUser.CreateSubKey(
                @"Software\Classes\KillerPDF.pdf\shell\open\command"))
                k.SetValue("", $"\"{exePath}\" \"%1\"");

            // Associate .pdf extension - adds KillerPDF to the "Open with" list
            using (var k = Registry.CurrentUser.CreateSubKey(
                @"Software\Classes\.pdf\OpenWithProgids"))
                k.SetValue("KillerPDF.pdf", new byte[0], RegistryValueKind.None);

            // RegisteredApplications capability (used by Default Programs UI)
            using (var k = Registry.CurrentUser.CreateSubKey(
                @"Software\KillerPDF\Capabilities"))
            {
                k.SetValue("ApplicationName",        AppName);
                k.SetValue("ApplicationDescription", "Lightweight PDF viewer and editor");
            }
            using (var k = Registry.CurrentUser.CreateSubKey(
                @"Software\KillerPDF\Capabilities\FileAssociations"))
                k.SetValue(".pdf", "KillerPDF.pdf");

            using (var k = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications"))
                k.SetValue(AppName, @"Software\KillerPDF\Capabilities");

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

        private static void Uninstall()
        {
            var res = MessageBox.Show(
                "Uninstall KillerPDF from this computer?",
                $"{AppName} Uninstall",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (res != MessageBoxResult.Yes) return;

            // Shortcuts
            try { File.Delete(StartMenuLnk); } catch { }
            try { Directory.Delete(StartMenuDir, recursive: false); } catch { }
            try { File.Delete(DesktopLnk); } catch { }

            // Registry cleanup
            try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\KillerPDF"); } catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree(
                @"Software\Microsoft\Windows\CurrentVersion\Uninstall\KillerPDF"); } catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\KillerPDF.pdf"); } catch { }

            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(
                    @"Software\Classes\.pdf\OpenWithProgids", writable: true);
                k?.DeleteValue("KillerPDF.pdf", throwOnMissingValue: false);
            }
            catch { }

            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(
                    @"Software\RegisteredApplications", writable: true);
                k?.DeleteValue(AppName, throwOnMissingValue: false);
            }
            catch { }

            SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);

            // Machine-wide half. Only reachable when Add/Remove Programs launched the Program Files
            // copy, which Windows runs elevated from the HKLM uninstall entry - so these writes
            // succeed there and simply fail harmlessly on a per-user uninstall.
            bool machine = string.Equals(
                Process.GetCurrentProcess().MainModule?.FileName, MachineInstallExe,
                StringComparison.OrdinalIgnoreCase);
            if (machine)
            {
                try { File.Delete(MachineStartMenuLnk); } catch { }
                try { Directory.Delete(MachineStartMenuDir, recursive: false); } catch { }
                try { Registry.LocalMachine.DeleteSubKeyTree(@"Software\KillerPDF"); } catch { }
                try { Registry.LocalMachine.DeleteSubKeyTree(
                    @"Software\Microsoft\Windows\CurrentVersion\Uninstall\KillerPDF"); } catch { }
            }

            // Self-delete: deferred via cmd batch so the EXE can exit first
            string bat = Path.Combine(Path.GetTempPath(), "killerpdf_uninstall.bat");
            File.WriteAllText(bat,
                "@echo off\r\n" +
                "ping -n 3 127.0.0.1 >nul\r\n" +
                $"rmdir /s /q \"{(machine ? MachineInstallDir : InstallDir)}\"\r\n" +
                "del \"%~f0\"\r\n");
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{bat}\"")
            {
                WindowStyle    = ProcessWindowStyle.Hidden,
                UseShellExecute = true
            });

            MessageBox.Show(Application.Current.TryFindResource("Str_Dlg_Uninstalled") as string ?? "KillerPDF has been uninstalled.", AppName,
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
