using System;
using KillerPDF.Controls;
using KillerPDF.Shell;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
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
        // The public portable file is KillerPDF-Portable.exe. Once installed, shortcuts launch the
        // loose-file application directly so installed startup never pays launcher/extraction cost.
        private static readonly string ExeName   = "KillerPDF.App.exe";
        private static readonly string InstallDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", AppName);
        private static readonly string InstallExe = Path.Combine(InstallDir, ExeName);
        private static readonly string LegacyUserInstallExe = Path.Combine(InstallDir, "KillerPDF.exe");
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
        private static readonly string LegacyMachineInstallExe = Path.Combine(MachineInstallDir, "KillerPDF.exe");
        private static readonly string MachineFileIconPath = Path.Combine(MachineInstallDir, "pdf-file.ico");
        private static readonly string MachineStartMenuDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), AppName);
        private static readonly string MachineStartMenuLnk = Path.Combine(MachineStartMenuDir, $"{AppName}.lnk");

        // ============================================================
        // Shell interop
        // ============================================================

        [LibraryImport("shell32.dll")]
        private static partial void SHChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);
        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool AllowSetForegroundWindow(int dwProcessId);
        private const uint SHCNE_ASSOCCHANGED = 0x08000000;
        private const uint SHCNF_IDLIST       = 0x0000;

        // ============================================================
        // Startup
        // ============================================================

        protected override void OnStartup(StartupEventArgs e)
        {
            // The elevated machine-update helper (Services/TrustedUpdate.cs) runs headless and exits.
            if (e.Args.Length > 0 && string.Equals(e.Args[0], Services.TrustedUpdate.HelperArgument, StringComparison.OrdinalIgnoreCase))
            {
                Shutdown(Services.TrustedUpdate.Apply(e.Args));
                return;
            }
            Services.PdfEngineBurn.MacroTempFile = MakeTempFile;
            KillerFamily.Accessibility.AccessibleControls.Initialize();
            KillerFamily.Theming.AccentShadow.Initialize();
            KillerFamily.Layout.ShortcutHints.Initialize(GetSetting, SetSetting);
            if (IsStartupSmoke)
            {
                RunStartupSmoke(e);
                return;
            }
            StartupTrace.Mark("App.OnStartup entered");
            DispatcherUnhandledException                    += OnDispatcherException;
            AppDomain.CurrentDomain.UnhandledException      += OnDomainException;
            TaskScheduler.UnobservedTaskException           += OnUnobservedTaskException;

            base.OnStartup(e);
            StartupTrace.Mark("Application base startup complete");

            // The app-level grain tile normally comes from the main window. Install, uninstall
            // and install-repair dialogs can open before one exists, so publish it now.
            EnsureCrashPreviewGrain();

            // Private launcher hand-off. The verified launcher has already staged the complete
            // payload; this fast headless pass only registers shortcuts, associations, protocol,
            // and uninstall metadata from the final installed path.
            if (e.Args.Any(a => string.Equals(a, "/register-user", StringComparison.OrdinalIgnoreCase)) ||
                e.Args.Any(a => string.Equals(a, "/register-machine", StringComparison.OrdinalIgnoreCase)))
            {
                bool machine = e.Args.Any(a => string.Equals(a, "/register-machine", StringComparison.OrdinalIgnoreCase));
                bool desktop = e.Args.Any(a => string.Equals(a, "/desktop", StringComparison.OrdinalIgnoreCase));
                // #410: setup passes /preview or /no-preview; without either the copy keeps its choice.
                bool? preview = e.Args.Any(a => string.Equals(a, "/preview", StringComparison.OrdinalIgnoreCase)) ? true
                    : e.Args.Any(a => string.Equals(a, "/no-preview", StringComparison.OrdinalIgnoreCase)) ? false : null;
                // Setup restores the previous copy when registration fails, unless there was none.
                bool setupRollsBack = !e.Args.Any(a => string.Equals(a, "/no-rollback", StringComparison.OrdinalIgnoreCase));
                try
                {
                    if (RegisterInstalledCopy(machine, desktop, preview, setupRollsBack: setupRollsBack) is { } desktopProblem)
                        Console.Error.WriteLine("Installed without the desktop shortcut: " + desktopProblem);
                    Shutdown(0);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("Install registration failed: " + ex.Message);
                    Shutdown(KillerFamily.Deployment.InstallerExitCodes.FromException(ex));
                }
                return;
            }

            if (e.Args.Any(a => string.Equals(a, "/remove-user-install", StringComparison.OrdinalIgnoreCase)))
            {
                RemovePerUserInstall();
                Shutdown(0);
                return;
            }

            // Elevated half of the dual-install repair (OfferInstallConflictRepair): removes the
            // machine-wide copy.
            if (e.Args.Any(a => string.Equals(a, "/remove-machine-conflict", StringComparison.OrdinalIgnoreCase)))
            {
                RemoveMachineInstallConflict();
                Shutdown(0);
                return;
            }

            // Machine-wide install, no UI. Used by winget / choco / RMM deployments and by the
            // all-users checkbox, which re-runs this exe elevated with /silent. Checked before
            // everything else: it must never show a window or touch the single-instance mutex.
            if (e.Args.Length > 0 &&
                string.Equals(e.Args[0], "/silent", StringComparison.OrdinalIgnoreCase))
            {
                Shutdown(DoSilentInstall());
                return;
            }

            // Handle uninstall flags (called by Add/Remove Programs and package managers)
            if (e.Args.Length > 0 &&
                (string.Equals(e.Args[0], "/uninstall", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(e.Args[0], "/uninstall-silent", StringComparison.OrdinalIgnoreCase)))
            {
                TaskbarIdentity.UseUninstall();
                Shutdown(Uninstall(string.Equals(e.Args[0], "/uninstall-silent", StringComparison.OrdinalIgnoreCase)));
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

            // Developer-only visual check for the crash dialog. It runs before single-instance
            // forwarding so the preview can open beside a normal KillerPDF session.
            if (e.Args.Any(a => string.Equals(
                    a, "--crash-dialog-preview", StringComparison.OrdinalIgnoreCase)))
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                AppTheme.Configure();
                ThemeManager.Initialize();
                LocaleManager.Initialize();
                EnsureCrashPreviewGrain();
                string previewLog = Path.Combine(Path.GetTempPath(), "KillerPDF-crash-preview.log");
                var previewException = new NotSupportedException(
                    "Adding form widgets requires one top-level Document structure element.");
                try
                {
                    File.WriteAllText(previewLog, BuildFullCrashReport(previewException));
                    ShowCrashDialog(previewException, previewLog, isFatal: false);
                }
                finally
                {
                    try { File.Delete(previewLog); } catch { }
                }
                Shutdown(0);
                return;
            }

            // #456: this process replaces one whose window could no longer draw. Wait for it to
            // release the single-instance mutex, then render in software for this session, since
            // the hardware path just failed.
            int recoveryAt = Array.FindIndex(e.Args, a => string.Equals(a, RenderFailureRecovery.RecoverySwitch, StringComparison.Ordinal));
            if (recoveryAt >= 0)
            {
                RestartedAfterRenderFailure = true;
                if (recoveryAt + 1 < e.Args.Length && int.TryParse(e.Args[recoveryAt + 1], out int previous))
                {
                    try { using var old = Process.GetProcessById(previous); old.WaitForExit(15000); }
                    catch (ArgumentException) { /* already gone */ }
                    catch (InvalidOperationException) { }
                }
                if (recoveryAt + 2 < e.Args.Length) RenderRecoveryManifest = e.Args[recoveryAt + 2];
                if (recoveryAt + 3 < e.Args.Length && e.Args[recoveryAt + 3].Length > 0)
                    RenderRecoveryRetainedSession = e.Args[recoveryAt + 3];
                if (recoveryAt + 4 < e.Args.Length) RenderRecoveryNotes = e.Args[(recoveryAt + 4)..];
                System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
            }

            // Single instance: a second launch (e.g. double-clicking another PDF in Explorer)
            // forwards its file path to the already-running instance, which opens it as a new
            // tab, then this process exits. Without this, every launch spawned its own window.
            bool isPrimary;
            try { _instanceMutex = new Mutex(true, MutexName, out isPrimary); }
            catch { isPrimary = true; }
            if (!isPrimary)
            {
                var fwd = e.Args.FirstOrDefault(a => !a.StartsWith('/'));
                GrantPrimaryForegroundPermission();
                Shutdown(InstanceChannel.Value.ForwardLaunch(fwd));
                return;
            }
            StartPipeServer();
            StartupTrace.Mark("Single-instance initialization complete");

            // Refresh the browser-extension handoff only after this process is known to be the
            // primary instance. A portable launch that merely forwards and exits must not take
            // the protocol handler from the installed copy (#267).
            Services.ProtocolRegistrar.RemoveStaleRegistration(Registry.CurrentUser);
            string registrationPath = GetPortableLauncherPath()
                ?? Process.GetCurrentProcess().MainModule?.FileName
                ?? Environment.ProcessPath
                ?? throw new InvalidOperationException("The current executable path is unavailable.");
            if (Services.ProtocolRegistrar.ShouldRefreshPerUser(
                    Registry.CurrentUser, Registry.LocalMachine, registrationPath))
                Services.ProtocolRegistrar.Register(Registry.CurrentUser, registrationPath);
            StartupTrace.Mark("Protocol registration refresh complete");

            ShutdownMode = ShutdownMode.OnLastWindowClose;
            CleanupStaleTemps();
            StartupTrace.Mark("Stale temporary-file cleanup complete");
            AppTheme.Configure();
            ThemeManager.Initialize();
            FilmGrain.Initialize();   // Services/FilmGrain.cs - after the theme, before any window
            StartupTrace.Mark("Theme initialized");
            // #211: translator test mode - load an external translation file as the language
            // override and re-apply it on every save (see LocaleManager). Documented in
            // TRANSLATING.md; must be set before LocaleManager.Initialize applies the locale.
            for (int i = 0; i < e.Args.Length - 1; i++)
                if (string.Equals(e.Args[i], "--lang-file", StringComparison.OrdinalIgnoreCase))
                { try { LocaleManager.ExternalFile = Path.GetFullPath(e.Args[i + 1]); } catch { /* bad path: normal locale */ } }
            LocaleManager.Initialize();
            StartupTrace.Mark("Locale initialized");
            LoadPendingUpdateRestart();
            var mainWindow = new MainWindow();
            StartupTrace.Mark("MainWindow constructed");
            mainWindow.Show();
            StartupTrace.Mark("MainWindow.Show returned");
            // After Show: under OnLastWindowClose, a prompt shown before the main window is the app's
            // only window, and answering it closed KillerPDF instead of opening it.
            OfferInstallConflictRepair();
            StartFolderAutomationIfConfigured();
        }

        internal bool SessionEndingConfirmed { get; private set; }

        protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
        {
            SessionEndingConfirmed = false;
            base.OnSessionEnding(e);
            if (e.Cancel) return;

            // Keep the request canceled until every document window has approved it.
            e.Cancel = true;
            var approvals = new List<(MainWindow Window, long Revision)>();
            foreach (MainWindow window in Windows.OfType<MainWindow>().ToArray())
            {
                if (!window.ConfirmSessionEnding(out long revision)) return;
                approvals.Add((window, revision));
            }

            MainWindow[] remaining = Windows.OfType<MainWindow>().ToArray();
            if (remaining.Length != approvals.Count
                || remaining.Any(window => !approvals.Any(approval => ReferenceEquals(approval.Window, window)))
                || approvals.Any(approval => approval.Window.SessionEndingEditRevision != approval.Revision))
                return;

            foreach (var approval in approvals) approval.Window.SaveSessionEndingSettings();
            SessionEndingConfirmed = true;
            e.Cancel = false;
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                // The installer can close us while shell activation is still returning.
                RetainUpdateRestartForExit();
                StopFolderAutomation();
                if (SessionTempFiles.IsValueCreated) SessionTempFiles.Value.Dispose();
            }
            finally { base.OnExit(e); }
        }

        // ============================================================
        // Single-instance IPC (mutex + named pipe)
        // ============================================================

        private static readonly Lazy<SingleInstanceChannel> InstanceChannel = new(() => new(
            "KillerPDF", WindowsIdentity.GetCurrent().User!.Value,
            Process.GetCurrentProcess().SessionId));
        private static string MutexName => InstanceChannel.Value.MutexName;
        private const string UninstallCloseCommand = "::KILLERPDF_CLOSE_FOR_UNINSTALL::";
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
                    // Disposed before the next accept, so a stalled or oversized sender is dropped
                    // after the message deadline instead of holding the only instance (BF-A008).
                    using var server = InstanceChannel.Value.CreateServer();
                    server.WaitForConnection();
                    string? path = SingleInstanceChannel.ReadMessage(server,
                        SingleInstanceChannel.MaximumMessageBytes, SingleInstanceChannel.MessageDeadline);
                    if (path is null) continue;
                    Dispatcher.BeginInvoke(new Action(() => DeliverExternalOpen(path)));
                }
                catch
                {
                    System.Threading.Thread.Sleep(150);   // pipe error - back off, keep serving
                }
            }
        }

        private static readonly HashSet<string> ClosedWindowFiles = new(StringComparer.OrdinalIgnoreCase);
        internal static bool HadMultipleDocumentWindows { get; private set; }

        internal static bool RegisterDocumentWindow(MainWindow window)
        {
            bool first = !Current.Windows.OfType<MainWindow>().Any(other => !ReferenceEquals(other, window));
            if (first)
            {
                ClosedWindowFiles.Clear();
                HadMultipleDocumentWindows = false;
            }
            else HadMultipleDocumentWindows = true;
            return first;
        }

        internal static string[] RestorableWindowFiles() => [.. Current.Windows.OfType<MainWindow>()
            .SelectMany(window => window.FilesForSessionRestore()).Concat(ClosedWindowFiles)
            .Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase)];

        internal static void RememberClosedWindow(MainWindow window)
        {
            if (GetSetting("RememberOpenFiles") == "0") ClosedWindowFiles.Clear();
            else ClosedWindowFiles.UnionWith(window.FilesForSessionRestore());
        }

        internal static void ForgetClosedWindowFile(string? path)
        {
            if (path is not null) ClosedWindowFiles.Remove(path);
        }

        internal static void ForgetClosedWindowFiles() => ClosedWindowFiles.Clear();

        internal static MainWindow? PrimaryWindow(MainWindow? excluded = null)
        {
            MainWindow[] windows = Current?.Windows.OfType<MainWindow>()
                .Where(window => !ReferenceEquals(window, excluded)).ToArray() ?? [];
            return windows.FirstOrDefault(window => window.IsActive) ?? windows.FirstOrDefault();
        }

        private static void DeliverExternalOpen(string? path)
        {
            if (PrimaryWindow() is { } mw)
            {
                if (string.Equals(path, UninstallCloseCommand, StringComparison.Ordinal))
                {
                    // Add or Remove Programs owns the foreground when it starts uninstall. Bring
                    // KillerPDF forward before closing so its quit or unsaved-work prompt cannot
                    // open invisibly behind Settings.
                    mw.RestoreAndActivate();
                    mw.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
                        new Action(mw.Close));
                    return;
                }
                mw.RestoreAndActivate();
                if (!string.IsNullOrEmpty(path)) mw.OpenFromExternal(path);
                mw.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
                    new Action(mw.RestoreAndActivate));
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

        private static bool SendToPrimary(string? path) => InstanceChannel.Value.TrySend(path);

        private static bool CloseRunningInstanceForUninstall()
        {
            using (Process current = Process.GetCurrentProcess())
            {
                // Instance channels are session-scoped. An unattended uninstall must not
                // remove files used by a desktop instance it cannot ask to close.
                foreach (Process candidate in Process.GetProcessesByName(current.ProcessName))
                {
                    using (candidate)
                    {
                        try
                        {
                            if (candidate.Id != current.Id && candidate.SessionId != current.SessionId)
                                return false;
                        }
                        catch (InvalidOperationException) { } // The process exited during enumeration.
                        catch (System.ComponentModel.Win32Exception) { return false; }
                    }
                }
            }
            Mutex? running;
            try { running = Mutex.OpenExisting(MutexName); }
            catch (WaitHandleCannotBeOpenedException) { return true; }

            using (running)
            {
                // Transfer the foreground privilege received from Windows Settings to the running
                // app before asking it to activate itself and show any close confirmation.
                GrantPrimaryForegroundPermission();
                if (!SendToPrimary(UninstallCloseCommand)) return false;
                try
                {
                    if (!running.WaitOne(TimeSpan.FromSeconds(60))) return false;
                    running.ReleaseMutex();
                    return true;
                }
                catch (AbandonedMutexException) { return true; }
            }
        }

        // Explorer launches the secondary process with foreground permission. Pass that permission
        // to the already-running KillerPDF process before asking it to activate its window.
        private static void GrantPrimaryForegroundPermission()
        {
            try
            {
                using Process current = Process.GetCurrentProcess();
                foreach (Process candidate in Process.GetProcessesByName(current.ProcessName)
                             .Where(p => p.Id != current.Id)
                             .OrderBy(p =>
                             {
                                 try { return p.StartTime; }
                                 catch { return DateTime.MaxValue; }
                             }))
                {
                    using (candidate)
                    {
                        if (AllowSetForegroundWindow(candidate.Id)) return;
                    }
                }
            }
            catch { }
        }

        // ============================================================
        // Crash handling
        // ============================================================
        //
        // NOTE: AccessViolationException is not catchable on .NET 4.8 without
        // [HandleProcessCorruptedStateExceptions], which we deliberately omit.

        private int _recoverableCrashDialogPending;

        // #456: set when this process was started to replace one whose window could no longer draw.
        internal static bool RestartedAfterRenderFailure { get; set; }
        internal static string? RenderRecoveryManifest { get; set; }
        internal static string? RenderRecoveryRetainedSession { get; set; }
        internal static IReadOnlyList<string> RenderRecoveryNotes { get; set; } = [];
        private int _renderRecoveryStarted;

        /// <summary>
        /// The render thread failed, so this window will stay black. Save unsaved documents aside,
        /// start a fresh KillerPDF that waits for this one to exit, and leave without prompts that
        /// could not be drawn. The new process renders in software for that session.
        /// </summary>
        private void RestartAfterRenderFailure(string logPath)
        {
            if (System.Threading.Interlocked.Exchange(ref _renderRecoveryStarted, 1) != 0) return;
            string? manifest = null;
            KillerPDF.Shell.MainWindow? window = null;
            try
            {
                window = MainWindow as MainWindow;
                manifest = window?.PrepareRenderFailureRecovery();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                RetainRenderRecoveryTemps(manifest);
                manifest ??= System.IO.Path.Combine(AppDataPaths.UserRoot, "Recovery", "recovery-unavailable.json");
                CrashReporter.Capture(ex, "Render recovery");
            }
            try
            {
                string exe = GetPortableLauncherPath() ?? Environment.ProcessPath
                    ?? throw new InvalidOperationException("The executable path is unavailable.");
                var start = new ProcessStartInfo(exe) { UseShellExecute = false };
                start.ArgumentList.Add(RenderFailureRecovery.RecoverySwitch);
                start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (manifest is not null)
                {
                    start.ArgumentList.Add(manifest);
                    // The session folder kept for this recovery, which the restart keeps even when its
                    // marker could not be written, then the lines to list if the manifest cannot be read.
                    start.ArgumentList.Add(SessionTempFiles.Value.RetainedDirectory ?? string.Empty);
                    foreach (string note in window?.RecoveryNotes ?? []) start.ArgumentList.Add(note);
                }
                if (Process.Start(start) is null) throw new InvalidOperationException("The recovery process did not start.");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                RetainRenderRecoveryTemps(manifest);
                CrashReporter.Capture(ex, "Render recovery restart");
            }
            try
            {
                StopFolderAutomation();
                _instanceMutex?.ReleaseMutex();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { }
            CleanupSessionTemps();
            Environment.Exit(1);
        }

        internal static void RetainRenderRecoveryTemps(string? manifest)
        {
            try { SessionTempFiles.Value.RetainForRecovery(manifest); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                CrashReporter.Capture(ex, "Retaining incomplete render recovery");
            }
        }

        private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            if (KillerPDF.Features.CommandLineCrash.Console is not null)
            {
                // A command-line run has no window to recover; report it and end the run.
                e.Handled = true;
                KillerPDF.Features.CommandLineCrash.TryReport(e.Exception,
                    CrashReporter.Capture(e.Exception, "Dispatcher (command line)"));
                // Exit now: a Shutdown already requested with the command's own code would keep it.
                CleanupSessionTemps();
                Environment.Exit(1);
                return;
            }
            if (RenderFailureRecovery.IsRenderThreadFailure(e.Exception))
            {
                e.Handled = true;
                RestartAfterRenderFailure(CrashReporter.Capture(e.Exception, "Dispatcher (render thread)"));
                return;
            }
            // Let WPF unwind the failing input/layout callback before creating another window.
            // Opening a modal dialog inside this event re-enters the dispatcher while its visual
            // tree may still be inconsistent, which can turn one recoverable error into a loop.
            e.Handled = true;
            var logPath = CrashReporter.Capture(e.Exception, "Dispatcher");
            QueueRecoverableCrashDialog(e.Exception, logPath);
        }

        private void OnDomainException(object sender, UnhandledExceptionEventArgs e)
        {
            var ex = e.ExceptionObject as Exception
                     ?? new Exception(e.ExceptionObject?.ToString() ?? "Unknown error");
            var logPath = CrashReporter.Capture(ex, "AppDomain");
            // A command-line run reports on its console; only a desktop session gets the dialog.
            if (KillerPDF.Features.CommandLineCrash.TryReport(ex, logPath))
            {
                // Exit with the documented failure code instead of the runtime's crash code.
                CleanupSessionTemps();
                Environment.Exit(1);
            }
            // A render-thread failure can surface on a worker thread too. The window can no longer
            // draw a crash dialog, so take the same recovery restart the UI-thread handler takes.
            if (RenderFailureRecovery.IsRenderThreadFailure(ex) && Dispatcher != null && !Dispatcher.HasShutdownStarted)
            {
                try
                {
                    Dispatcher.Invoke(() => RestartAfterRenderFailure(logPath));
                    return;
                }
                catch { /* fall back to the crash dialog below */ }
            }

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
            // An unobserved task is reported later by the finalizer, after the task and its caller
            // are already gone. It is not an active UI crash and there is no operation left for a
            // Retry/Continue dialog to recover. 1.7.5 surfaced the wrapper AggregateException as a
            // crash with no useful stack, which is both alarming and diagnostically empty. Keep the
            // complete flattened failure in the log, mark it observed, and leave the running app
            // alone. Live dispatcher and AppDomain failures still use the normal crash dialog.
            e.SetObserved();
            CrashReporter.Capture(e.Exception.Flatten(), "TaskScheduler (observed background fault)");
        }

        private void QueueRecoverableCrashDialog(Exception exception, string logPath)
        {
            if (System.Threading.Interlocked.Exchange(ref _recoverableCrashDialogPending, 1) != 0)
                return;
            try
            {
                if (Dispatcher != null && !Dispatcher.HasShutdownStarted)
                {
                    Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
                    {
                        try
                        {
                            if (!ShowCrashDialog(exception, logPath, isFatal: false))
                            {
                                CleanupSessionTemps();
                                Shutdown(1);
                            }
                        }
                        finally
                        {
                            System.Threading.Interlocked.Exchange(ref _recoverableCrashDialogPending, 0);
                        }
                    }));
                    return;
                }
            }
            catch { /* the error was still captured even if the dispatcher is unavailable */ }

            System.Threading.Interlocked.Exchange(ref _recoverableCrashDialogPending, 0);
        }

        /// <summary>
        /// Dark-themed crash report dialog. Returns true if the user chose Continue.
        /// Must be called on the UI thread.
        /// </summary>
        private static string CrashText(string key, string fallback) =>
            System.Windows.Application.Current?.TryFindResource(key) as string ?? fallback;

        // Install and uninstall wording. These run before or without the main window, so each line
        // carries its English text in case the language dictionary has not loaded.
        private static string SetupText(string key, string fallback) => CrashText(key, fallback);

        private static void EnsureCrashPreviewGrain() => Controls.GrainTexture.Publish();

        private static bool ShowCrashDialog(Exception ex, string logPath, bool isFatal)
        {
            bool shouldContinue = false;
            var win = CreateCrashDialog(ex, logPath, isFatal, result => shouldContinue = result);
            win.ShowDialog();
            return shouldContinue;
        }

        private static Window CreateCrashDialog(Exception ex, string logPath, bool isFatal, Action<bool> complete)
        {
            Window? owner = Current?.MainWindow is { IsVisible: true } main ? main : null;
            Brush pane = UiKit.Brush("PaneBrush", new SolidColorBrush(Color.FromRgb(0x2a, 0x2a, 0x2a)));
            Brush code = UiKit.Brush("MenuBackgroundBrush", new SolidColorBrush(Color.FromRgb(0x12, 0x12, 0x12)));
            Brush border = UiKit.Brush("CardBorderBrush", new SolidColorBrush(Color.FromRgb(0x3a, 0x3a, 0x3a)));
            Brush text = UiKit.Brush("TextBrush", Brushes.White);
            Brush muted = UiKit.Brush("MutedTextBrush", new SolidColorBrush(Color.FromRgb(0xaa, 0xaa, 0xaa)));
            Brush dim = UiKit.Brush("DimTextBrush", new SolidColorBrush(Color.FromRgb(0x77, 0x77, 0x77)));
            Brush danger = UiKit.Brush("DangerRed", new SolidColorBrush(Color.FromRgb(0xef, 0x44, 0x44)));
            Brush grain = UiKit.Brush("GrainTileBrush", Brushes.Transparent);
            double grainOpacity = Current?.TryFindResource("GrainOpacity") is double opacity
                ? opacity : 0.05;
            var paneShadow = Current?.TryFindResource("PaneShadowEffect")
                as System.Windows.Media.Effects.Effect;
            string title = CrashText("Str_Crash_Title", "KillerPDF - Unexpected Error");
            var win = new Window
            {
                Title = title,
                Width = 760,
                Height = 540,
                MinWidth = 620,
                MinHeight = 430,
                ShowInTaskbar = owner is null
            };
            DialogFrame.Configure(win, owner, resizable: true, fade: false);
            // Use the family scroll host, including its transparent scrollbar intersection.
            win.Resources[typeof(ScrollViewer)] = win.FindResource("BareScrollViewer");

            var root = new Grid { Background = Brushes.Transparent };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var summary = new Controls.PickerBorder
            {
                Background = pane,
                BorderBrush = border,
                BorderThickness = new Thickness(1),
                CornerRadius = UiKit.RadCard
            };
            var summaryGrid = new Grid { Margin = new Thickness(18, 16, 18, 15) };
            summaryGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
            summaryGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            summaryGrid.Children.Add(new Border
            {
                Width = 32,
                Height = 32,
                CornerRadius = new CornerRadius(16),
                BorderBrush = danger,
                BorderThickness = new Thickness(1.5),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Child = new TextBlock
                {
                    Text = "!",
                    Foreground = danger,
                    FontFamily = UiKit.MonoFont,
                    FontSize = 20,
                    FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            });
            var summaryText = new StackPanel();
            summaryText.Children.Add(new TextBlock
            {
                Text = ex.GetType().Name,
                Foreground = danger,
                FontFamily = UiKit.MonoFont,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold
            });
            summaryText.Children.Add(new TextBlock
            {
                Text = ex.Message,
                Foreground = text,
                FontSize = 14,
                LineHeight = 20,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 7, 0, 0)
            });
            summaryText.Children.Add(new TextBlock
            {
                Text = string.Format(CrashText("Str_Crash_Log", "Log: {0}"), logPath),
                Foreground = dim,
                FontFamily = UiKit.MonoFont,
                FontSize = 10.5,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 10, 0, 0)
            });
            Grid.SetColumn(summaryText, 1);
            summaryGrid.Children.Add(summaryText);
            var summaryLayers = new Grid();
            summaryLayers.Children.Add(new Border
            {
                Background = grain,
                Opacity = grainOpacity,
                CornerRadius = UiKit.RadCard,
                IsHitTestVisible = false
            });
            summaryLayers.Children.Add(summaryGrid);
            summary.Child = summaryLayers;
            var summaryHost = new Grid { Margin = new Thickness(18, 8, 18, 12) };
            summaryHost.Children.Add(new Border
            {
                Background = pane,
                CornerRadius = UiKit.RadCard,
                IsHitTestVisible = false,
                Effect = paneShadow?.CloneCurrentValue()
            });
            summaryHost.Children.Add(summary);
            Grid.SetRow(summaryHost, 0);
            root.Children.Add(summaryHost);

            var traceBox = UiKit.Field();
            traceBox.Text = FormatExceptionChain(ex);
            traceBox.Background = Brushes.Transparent;
            traceBox.Foreground = muted;
            traceBox.FontFamily = UiKit.MonoFont;
            traceBox.FontSize = 10.5;
            traceBox.IsReadOnly = true;
            traceBox.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            traceBox.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            traceBox.TextWrapping = TextWrapping.NoWrap;
            traceBox.BorderThickness = new Thickness(0);
            traceBox.Padding = new Thickness(12, 10, 12, 10);
            var traceLayers = new Grid();
            traceLayers.Children.Add(new Border
            {
                Background = grain,
                Opacity = grainOpacity,
                CornerRadius = UiKit.RadCard,
                IsHitTestVisible = false
            });
            traceLayers.Children.Add(traceBox);
            var traceCard = new Controls.PickerBorder
            {
                Background = code,
                BorderBrush = border,
                BorderThickness = new Thickness(1),
                CornerRadius = UiKit.RadCard,
                Child = traceLayers
            };
            var traceHost = new Grid { Margin = new Thickness(18, 0, 18, 14) };
            traceHost.Children.Add(new Border
            {
                Background = code,
                CornerRadius = UiKit.RadCard,
                IsHitTestVisible = false,
                Effect = paneShadow?.CloneCurrentValue()
            });
            traceHost.Children.Add(traceCard);
            Grid.SetRow(traceHost, 1);
            root.Children.Add(traceHost);

            var footer = new Border
            {
                Background = Brushes.Transparent,
                BorderBrush = border,
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(18, 11, 18, 11)
            };
            var btnPanel = new DockPanel { LastChildFill = false };
            var leftBtns = new StackPanel { Orientation = Orientation.Horizontal };
            var copyBtn = MakeCrashButton(CrashText("Str_Crash_Copy", "Copy Report"), 100);
            copyBtn.Click += (_, _) =>
            {
                try { Clipboard.SetText(BuildFullCrashReport(ex)); } catch { }
            };
            leftBtns.Children.Add(copyBtn);
            var logsBtn = MakeCrashButton(CrashText("Str_Crash_OpenLogs", "Open Logs"), 88);
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
            var githubBtn = MakeCrashButton(CrashText("Str_Crash_Report", "Report on GitHub"), 128);
            githubBtn.Margin = new Thickness(8, 0, 0, 0);
            githubBtn.Click += (_, _) =>
            {
                try
                {
                    var msgLen = Math.Min(80, ex.Message.Length);
                    var title  = Uri.EscapeDataString(
                        $"Crash: {ex.GetType().Name}: {ex.Message[..msgLen]}");
                    var stack  = ex.StackTrace?.Length > 800
                        ? ex.StackTrace[..800] + "\n... (truncated)"
                        : ex.StackTrace ?? "(no stack trace)";
                    var body = Uri.EscapeDataString(
                        $"**Version:** {AppVersion.Display}\n" +
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
            var rightBtns = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            var contBtn = MakeCrashButton(CrashText("Str_Crash_Continue", "Continue"), 88);
            contBtn.IsEnabled = !isFatal;
            contBtn.FontWeight = isFatal ? FontWeights.Normal : FontWeights.SemiBold;
            contBtn.Margin = new Thickness(0, 0, 8, 0);
            contBtn.Click += (_, _) => { complete(true); win.Close(); };
            var quitBtnCtrl = MakeCrashButton(CrashText("Str_Crash_Quit", "Quit"), 72);
            quitBtnCtrl.FontWeight = isFatal ? FontWeights.SemiBold : FontWeights.Normal;
            quitBtnCtrl.Click += (_, _) => { complete(false); win.Close(); };
            rightBtns.Children.Add(contBtn);
            rightBtns.Children.Add(quitBtnCtrl);
            DockPanel.SetDock(rightBtns, Dock.Right);
            btnPanel.Children.Add(rightBtns);
            footer.Child = btnPanel;
            Grid.SetRow(footer, 2);
            root.Children.Add(footer);

            win.Content = DialogFrame.Frame(win, owner, title,
                () => { complete(false); win.Close(); }, root,
                new Thickness(20, 20, 20, 24));
            return win;
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

        private static Button MakeCrashButton(string label, double width = 88)
        {
            var btn = UiKit.Make(label, accent: true);
            btn.Padding = new Thickness(10, 4, 10, 4);
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
            sb.AppendLine($"KillerPDF v{AppVersion.Display}");
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
                string currentExe = Environment.ProcessPath
                    ?? throw new InvalidOperationException("The current executable path is unavailable.");
                return !IsRegisteredCopy(Registry.CurrentUser, currentExe)
                    && !IsRegisteredCopy(Registry.LocalMachine, currentExe);
            }
            catch { return false; }
        }

        /// <summary>True when KillerPDF is already installed machine-wide.</summary>
        internal static bool MachineInstallExists() => ExistingRegisteredExecutable(Registry.LocalMachine) != null
            || File.Exists(MachineInstallExe) || File.Exists(LegacyMachineInstallExe);

        /// <summary>True when KillerPDF is already installed for the current user.</summary>
        internal static bool UserInstallExists() => ExistingRegisteredExecutable(Registry.CurrentUser) != null
            || File.Exists(InstallExe) || File.Exists(LegacyUserInstallExe);

        private static string? ExistingRegisteredExecutable(RegistryKey root)
        {
            try
            {
                using var key = root.OpenSubKey(@"Software\KillerPDF");
                string? path = key?.GetValue("InstallPath") as string;
                return !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? Path.GetFullPath(path) : null;
            }
            catch { return null; }
        }

        internal static bool IsRegisteredCopy(RegistryKey root, string? executable)
        {
            string? registered = ExistingRegisteredExecutable(root);
            return registered != null && !string.IsNullOrWhiteSpace(executable)
                && string.Equals(registered, Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Repairs a machine that carries BOTH a per-user and a machine-wide install -
        /// the state where each Add/Remove Programs entry describes the other copy's version and
        /// launching gets whichever exe the shell resolves first. Detected at startup; offers to
        /// remove whichever copy is NOT running. Removing the machine copy needs elevation, so
        /// that path re-runs this exe with /remove-machine-conflict under UAC.</summary>
        private static void OfferInstallConflictRepair()
        {
            if (!UserInstallExists() || !MachineInstallExists()) return;
            string current = Process.GetCurrentProcess().MainModule?.FileName ?? "";
            bool runningMachine = IsRegisteredCopy(Registry.LocalMachine, current)
                               || string.Equals(current, MachineInstallExe, StringComparison.OrdinalIgnoreCase)
                               || string.Equals(current, LegacyMachineInstallExe, StringComparison.OrdinalIgnoreCase);
            bool runningUser = IsRegisteredCopy(Registry.CurrentUser, current)
                            || string.Equals(current, InstallExe, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(current, LegacyUserInstallExe, StringComparison.OrdinalIgnoreCase);
            if (!runningMachine && !runningUser) return;

            string message = runningMachine
                ? SetupText("Str_Dlg_DuplicateInstallMachine", "KillerPDF found two installed copies on this computer.\n\nYou are currently running the all-users installation. An older per-user copy is also present, usually because it was in use during an earlier update.\n\nRemove the unused per-user copy now? Your settings and PDF files will not be removed.")
                : SetupText("Str_Dlg_DuplicateInstallUser", "KillerPDF found two installed copies on this computer.\n\nYou are currently running the per-user installation. An all-users copy is also present.\n\nRemove the unused all-users copy now? Windows will ask for administrator permission. Your settings and PDF files will not be removed.");
            if (KillerDialog.Show(Current.MainWindow, message,
                SetupText("Str_Dlg_DuplicateInstallTitle", "Duplicate KillerPDF installation"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

            if (runningMachine)
            {
                RemovePerUserInstall();
            }
            else
            {
                try
                {
                    using var p = Process.Start(new ProcessStartInfo(current, "/remove-machine-conflict")
                    { UseShellExecute = true, Verb = "runas" });
                    p?.WaitForExit();
                }
                catch { /* declining UAC leaves both copies in place */ }
            }
        }

        private static void RemoveMachineInstallConflict()
        {
            string? registeredExe = ExistingRegisteredExecutable(Registry.LocalMachine);
            try { File.Delete(InstalledShortcuts.DesktopPath(machine: true)); } catch { }
            try { File.Delete(MachineStartMenuLnk); } catch { }
            try { Directory.Delete(MachineStartMenuDir, recursive: false); } catch { }
            UnregisterFileHandler(Registry.LocalMachine);
            Services.ProtocolRegistrar.Unregister(Registry.LocalMachine);
            Services.PreviewHandlerRegistration.Unregister(Registry.LocalMachine);
            try { Registry.LocalMachine.DeleteSubKeyTree(@"Software\KillerPDF"); } catch { }
            try { Registry.LocalMachine.DeleteSubKeyTree(
                @"Software\Microsoft\Windows\CurrentVersion\Uninstall\KillerPDF"); } catch { }
            string directory = Path.GetDirectoryName(registeredExe ?? MachineInstallExe) ?? MachineInstallDir;
            Services.CommandLinePath.Unregister(directory, machine: true);
            Services.PreviewHandlerRegistration.StopHosts(directory);
            // Only the payload the installer wrote is removed; a folder without its manifest is left alone.
            if (Services.InstallPayloadOwnership.Read(directory) is { } owned) Services.InstallPayloadOwnership.Remove(owned);
            SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
        }

        /// <summary>
        /// Installs KillerPDF, offers to set it as the default PDF handler, then relaunches from
        /// the installed location. Returns false if the install did not happen, so the caller can
        /// put the portable badge back rather than leaving the user with a half-finished state.
        /// The footer's Install button (Shell/Install.cs) calls it; the command it runs comes from
        /// Shell/PortableInstallPlan.cs.
        ///
        /// An all-users install runs elevated, so UAC only appears when the user actually asked for it.
        /// </summary>
        internal static bool InstallAndRelaunch(bool wantDesktop, bool allUsers, string? fileToOpen = null)
            => InstallAndRelaunchAsync(wantDesktop, allUsers, fileToOpen, offThread: false).GetAwaiter().GetResult();

        /// <summary>
        /// The footer's install. The install command runs off the UI thread, so the window keeps
        /// painting while the launcher extracts, verifies, waits for UAC and closes running copies.
        /// Dialogs, the relaunch and shutdown stay on the UI thread. With offThread false every step
        /// completes synchronously, which is what the shared kit's synchronous entry point needs.
        /// </summary>
        internal static async System.Threading.Tasks.Task<bool> InstallAndRelaunchAsync(
            bool wantDesktop, bool allUsers, string? fileToOpen, bool offThread = true)
        {
            var plan = Shell.PortableInstallPlan.Choose(allUsers, wantDesktop, MachineInstallExists(),
                GetPortableLauncherPath(), Environment.ProcessPath, InstallExe, MachineInstallExe,
                CurrentExeIsSelfContained());

            if (plan.Kind == Shell.PortableInstallKind.RefusedSecondCopy)
            {
                KillerDialog.Show(Current.MainWindow,
                    Current.TryFindResource("Str_Dlg_OneInstallOnly") as string ??
                        "KillerPDF is already installed for everyone on this computer. Update that installation, or uninstall it before choosing a per-user install. KillerPDF will not create two installed copies.",
                    Current.TryFindResource("Str_Dlg_InstallTitle") as string ?? "Install KillerPDF",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }
            if (plan.Kind == Shell.PortableInstallKind.Unavailable || plan.TargetExe is null) return false;

            if (plan.Kind == Shell.PortableInstallKind.InProcess)
            {
                // Compatibility for old woven builds and direct developer runs.
                if (!DoInstall(wantDesktop)) return false;
            }
            else
            {
                string program = plan.Program!, arguments = plan.Arguments!;
                bool elevate = plan.Elevate;
                bool installed = offThread
                    ? await System.Threading.Tasks.Task.Run(() => RunInstallCommand(program, arguments, elevate))
                    : RunInstallCommand(program, arguments, elevate);
                if (!installed) return false;
            }
            if (!File.Exists(plan.TargetExe)) return false;   // trust gate / copy failure already reported

            // Only ever one install: drop the per-user copy so there is a single Start Menu
            // entry and a single uninstall entry. This also removes per-user file and protocol
            // handlers that could shadow the machine registration. Settings remain intact.
            if (allUsers) RemovePerUserInstall();
            string targetExe = plan.TargetExe;

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

            ProcessStartInfo psi = Shell.PortableInstallPlan.InstalledRelaunch(targetExe, fileToOpen);
            // Free the single-instance mutex first so the launched copy becomes primary (and shows a
            // window) instead of treating itself as a duplicate and exiting.
            (Current as App)?.ReleaseInstanceMutex();
            Process.Start(psi);
            Application.Current.Shutdown();
            return true;
        }

        /// <summary>Runs the plan's install command and waits for it. Elevated runs go through
        /// "runas"; declining UAC throws Win32Exception 1223 (ERROR_CANCELLED), which is a failure.</summary>
        private static bool RunInstallCommand(string program, string arguments, bool elevate)
        {
            try
            {
                var psi = new ProcessStartInfo(program, arguments) { UseShellExecute = elevate };
                if (elevate) psi.Verb = "runas";
                using var process = Process.Start(psi);
                process?.WaitForExit();
                return process is not null && process.ExitCode == 0;
            }
            catch { return false; }
        }

        /// <summary>The DemoThumbprint idea in the other apps: KillerPDF has no demo build, so the
        /// footer badge always shows on a portable copy.</summary>
        internal static bool IsDemo => false;

        /// <summary>True when this exe carries the whole app, as old woven single-file builds did.
        /// The packaged portable's inner app and developer builds keep KillerPDF.dll beside the exe,
        /// so copying the exe alone would install something that cannot start.</summary>
        private static bool CurrentExeIsSelfContained()
        {
            try
            {
                string? exe = Environment.ProcessPath;
                return !string.IsNullOrEmpty(exe) && !File.Exists(Path.ChangeExtension(exe, ".dll"));
            }
            catch { return false; }
        }

        /// <summary>The packaged portable launcher that started this copy, when there is one.</summary>
        private static string? GetPortableLauncherPath()
        {
            try
            {
                string? path = Environment.GetEnvironmentVariable("KILLERPDF_LAUNCHER_PATH");
                return !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? Path.GetFullPath(path) : null;
            }
            catch { return null; }
        }

        /// <summary>Remove a per-user install: files, shortcuts and its HKCU install markers.
        /// The settings are deliberately left alone so theme, accent, locale, recent files and
        /// window placement survive the move to a machine-wide install.</summary>
        private static void RemovePerUserInstall()
        {
            string? registeredExe = ExistingRegisteredExecutable(Registry.CurrentUser);
            try { if (File.Exists(StartMenuLnk)) File.Delete(StartMenuLnk); } catch { }
            try { if (Directory.Exists(StartMenuDir)) Directory.Delete(StartMenuDir, true); } catch { }
            try { if (File.Exists(DesktopLnk)) File.Delete(DesktopLnk); } catch { }
            string directory = Path.GetDirectoryName(registeredExe ?? InstallExe) ?? InstallDir;
            Services.CommandLinePath.Unregister(directory, machine: false);
            Services.PreviewHandlerRegistration.Unregister(Registry.CurrentUser);
            Services.PreviewHandlerRegistration.StopHosts(directory);
            // Only the payload the installer wrote is removed; a folder without its manifest is left alone.
            if (Services.InstallPayloadOwnership.Read(directory) is { } owned) Services.InstallPayloadOwnership.Remove(owned);
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\KillerPDF", writable: true);
                key?.DeleteValue("Installed", throwOnMissingValue: false);
                key?.DeleteValue("InstallPath", throwOnMissingValue: false);
            }
            catch { }
            UnregisterFileHandler(Registry.CurrentUser);
            Services.ProtocolRegistrar.Unregister(Registry.CurrentUser);
            try { Registry.CurrentUser.DeleteSubKeyTree(
                @"Software\Microsoft\Windows\CurrentVersion\Uninstall\KillerPDF",
                throwOnMissingSubKey: false); }
            catch { }
        }

        // ============================================================
        // Silent install - winget / choco / RMM / the all-users checkbox / the Microsoft Store
        // ============================================================

        /// <summary>
        /// Silent install with no UI, so the exit code is the only signal a deployment tool - or
        /// the footer's all-users install - gets back. Elevated, it installs machine-wide; without admin
        /// rights it installs for the current user.
        ///
        /// A machine-wide install registers file associations in HKLM so every account sees KillerPDF
        /// in Open With and Default apps. Windows still leaves the actual default choice to each user.
        /// </summary>
        private static int DoSilentInstall()
        {
            try
            {
                using var installLease = KillerFamily.Deployment.InstallerExitCodes.Acquire(AppName);
                string src = Environment.ProcessPath
                    ?? throw new InvalidOperationException("The current executable path is unavailable.");

                // Only this exe is copied, so it has to carry the whole app. The installed application's
                // own KillerPDF.App.exe does not, and registering it would leave a copy that cannot start.
                if (!CurrentExeIsSelfContained())
                {
                    Console.Error.WriteLine("Silent install needs the KillerPDF.exe installer, not the installed application.");
                    return KillerFamily.Deployment.InstallerExitCodes.Failure;
                }

                // Same trust gate as the interactive path - an unsigned or wrong-publisher exe must
                // not be able to write itself into Program Files, least of all while elevated.
                var (valid, _, _) = VerifyAuthenticode(src);
                if (!valid)
                {
                    Console.Error.WriteLine("Silent install refused: EXE has no valid Authenticode signature.");
                    return KillerFamily.Deployment.InstallerExitCodes.SecurityRejected;
                }

                // With admin rights (winget, choco, RMM) the install is machine-wide. Without them,
                // as the Microsoft Store runs installers, it is the normal per-user install with no
                // prompt. The app never needs admin rights.
                bool machine = KillerFamily.Deployment.InstallerExitCodes.IsElevated();
                string installDir = machine ? MachineInstallDir : InstallDir;
                string installExe = machine ? MachineInstallExe : InstallExe;

                Directory.CreateDirectory(installDir);
                if (File.Exists(installExe))
                {
                    try { File.SetAttributes(installExe, FileAttributes.Normal); } catch { }
                }
                File.Copy(src, installExe, overwrite: true);
                try { File.SetAttributes(installExe, FileAttributes.Normal); } catch { }
                if (RegisterInstalledCopy(machine: machine, desktop: Environment.GetCommandLineArgs()
                        .Any(argument => string.Equals(argument, "/desktop", StringComparison.OrdinalIgnoreCase)),
                        installedExecutable: installExe) is { } desktopProblem)
                    Console.Error.WriteLine("Installed without the desktop shortcut: " + desktopProblem);
                return KillerFamily.Deployment.InstallerExitCodes.Success;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Silent install failed: " + ex.Message);
                return KillerFamily.Deployment.InstallerExitCodes.FromException(ex);
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
        private static readonly string UserTempDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KillerPDF", "Temp");
        private static readonly Lazy<ProcessTempFiles> SessionTempFiles = new(() => new(
            AppDataPaths.StartupSmokeRoot is { } isolatedRoot ? Path.Combine(isolatedRoot, "Temp")
                : IsStartupSmoke ? Path.Combine(SmokeDataRoot, "Temp") : UserTempDir));
        internal static string TempDir => SessionTempFiles.Value.DirectoryPath;

        /// <summary>
        /// Creates a tracked temp path of the form killerpdf_&lt;tag&gt;_&lt;guid&gt;.pdf
        /// in a process-owned directory under %LOCALAPPDATA%\KillerPDF\Temp\.
        /// All registered paths are deleted when CleanupSessionTemps() is called.
        /// </summary>
        internal static string MakeTempFile(string tag) => SessionTempFiles.Value.CreatePath(tag);
        internal static string MakeImportDirectory() => SessionTempFiles.Value.CreateImportDirectoryPath();

        /// <summary>True for a temp path that cleanup deletes, which must never be a document's saved home.</summary>
        internal static bool IsCleanupOwnedPath(string path) => SessionTempFiles.Value.Owns(path);

        // The file name to offer when a temp-backed import (a browser download) is first saved.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> SuggestedNames =
            new(StringComparer.OrdinalIgnoreCase);
        internal static void RememberSuggestedName(string tempPath, string fileName) => SuggestedNames[tempPath] = fileName;
        internal static string? TakeSuggestedName(string tempPath) =>
            SuggestedNames.TryRemove(tempPath, out string? name) ? name : null;

        /// <summary>Deletes all temp files registered this session (best-effort).</summary>
        internal static void CleanupSessionTemps() => SessionTempFiles.Value.CleanupOwned();

        /// <summary>
        /// Reclaims process-owned temporary files only after their owner releases its lease.
        /// Legacy files have no ownership proof and are left alone.
        /// </summary>
        internal static void CleanupStaleTemps() => SessionTempFiles.Value.CleanupStale(RenderRecoveryRetainedSession);

        internal static string? GetSetting(string name)
        {
            if (IsStartupSmoke) return SmokeSettings.GetValueOrDefault(name);
            if (Services.AppDataPaths.PortableRoot is not null)
                return Services.AppDataPaths.GetPortableSetting(name);
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\KillerPDF\Settings");
                return key?.GetValue(name) as string;
            }
            catch { return null; }
        }

        internal static void SetSetting(string name, string value)
        {
            if (IsStartupSmoke) { SmokeSettings[name] = value; return; }
            if (Services.AppDataPaths.PortableRoot is not null)
            {
                try { Services.AppDataPaths.SetPortableSetting(name, value); } catch { }
                return;
            }
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(@"Software\KillerPDF\Settings");
                key.SetValue(name, value);
            }
            catch { /* best-effort */ }
        }

        internal static void RemoveSetting(string name)
        {
            if (IsStartupSmoke) { SmokeSettings.Remove(name); return; }
            if (Services.AppDataPaths.PortableRoot is not null)
            {
                try { Services.AppDataPaths.RemovePortableSetting(name); } catch { }
                return;
            }
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
        internal static bool ClearAllData()
        {
            try { _folderAutomation?.Pause(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException) { }
            if (!StopFolderAutomation()) return false;
            TryDeleteDir(FolderAutomationDirectory);
            if (Services.AppDataPaths.PortableRoot is not null)
                TryDeleteDir(Services.AppDataPaths.UserRoot);
            else
                try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\KillerPDF\Settings", throwOnMissingSubKey: false); } catch { }

            TryDeleteDir(Services.AppDataPaths.TessDataDirectory);
            TryDeleteDir(Path.Combine(Services.AppDataPaths.LocalRoot, "ocr"));
            TryDeleteDir(Path.Combine(Services.AppDataPaths.LocalRoot, "UpdateRestart"));
            TryDeleteDir(TempDir);                              // temp working files

            // Legacy temp PDFs that may linger in %TEMP%.
            try
            {
                foreach (var f in Directory.GetFiles(Path.GetTempPath(), "killerpdf_*.pdf"))
                    try { File.Delete(f); } catch { }
            }
            catch { }
            return true;
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
            var list = Services.RecentFilesList.With(GetRecentFiles(), path, RecentFilesMax);
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
        /// Returns (canceled, install, wantDesktopShortcut).
        /// </summary>
        private static (bool canceled, bool install, bool desktop) ShowLauncher(bool alreadyInstalled)
        {
            bool canceled = true;
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

            content.Children.Add(new TextBlock
            {
                Text       = $"{Current.TryFindResource("Str_About_Version")} {AppVersion.Display}",
                Foreground = dimText,
                FontSize   = 12,
                Margin     = new Thickness(0, 2, 0, 18)
            });

            content.Children.Add(new TextBlock
            {
                Text         = alreadyInstalled
                    ? Current.TryFindResource("Str_Dlg_UpdateUserMsg") as string ?? string.Empty
                    : (Current.TryFindResource("Str_Dlg_InstallMsg") as string ?? string.Empty) + "\n\n"
                        + (Current.TryFindResource("Str_Dlg_InstallBullets") as string ?? string.Empty),
                Foreground   = Brushes.White,
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 0, 0, 16)
            });

            var desktopChk = new CheckBox
            {
                IsChecked = true,
                Margin    = new Thickness(0, 0, 0, 22),
                Content   = new TextBlock { Text = Current.TryFindResource("Str_Chk_Desktop") as string ?? string.Empty, Foreground = Brushes.White }
            };
            content.Children.Add(desktopChk);

            var btnRow = new StackPanel
            {
                Orientation         = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            var runBtn = UiKit.Make(Current.TryFindResource("Str_Btn_Run") as string ?? string.Empty,
                new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x30)),
                new SolidColorBrush(Color.FromRgb(0x16, 0x63, 0x34)),
                Brushes.White, Brushes.White);
            runBtn.Width  = 88;
            runBtn.Margin = new Thickness(0, 0, 8, 0);

            var installBtn = UiKit.Make(Current.TryFindResource(alreadyInstalled ? "Str_Btn_DoUpdate" : "Str_Btn_DoInstall") as string ?? string.Empty,
                accent,
                new SolidColorBrush(Color.FromRgb(0x4a, 0xf0, 0x90)),
                new SolidColorBrush(Color.FromRgb(0x0a, 0x0a, 0x0a)),
                new SolidColorBrush(Color.FromRgb(0x0a, 0x0a, 0x0a)));
            installBtn.Width      = 110;
            installBtn.FontWeight = FontWeights.SemiBold;

            runBtn.Click += (_, _) =>
            {
                canceled = false; install = false;
                win.Close();
            };
            installBtn.Click += (_, _) =>
            {
                canceled = false; install = true;
                desktop = desktopChk.IsChecked == true;
                win.Close();
            };

            btnRow.Children.Add(runBtn);
            btnRow.Children.Add(installBtn);
            content.Children.Add(btnRow);

            root.Children.Add(content);
            win.Content = root;
            win.ShowDialog();

            return (canceled, install, desktop);
        }

        // ============================================================
        // Security - Authenticode verification
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

        [LibraryImport("wintrust.dll")]
        private static partial uint WinVerifyTrust(
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
#pragma warning disable SYSLIB0057 // Required to extract the signer certificate from a PE Authenticode signature.
                var raw = X509Certificate.CreateFromSignedFile(filePath);
#pragma warning restore SYSLIB0057
                using var cert = X509CertificateLoader.LoadCertificate(raw.GetRawCertData());
                subject    = cert.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
                thumbprint = cert.Thumbprint ?? string.Empty;
            }
            catch { /* unsigned or unreadable */ }

            // Full chain check via WinVerifyTrust. Revocation uses what Windows has cached, with no online lookup.
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

        // ============================================================
        // Installation
        // ============================================================

        private static bool DoInstall(bool wantDesktop)
        {
            string src = Environment.ProcessPath
                ?? throw new InvalidOperationException("The current executable path is unavailable.");

            // ── Trust gate: refuse to install an unsigned or wrong-publisher EXE ──
            var (valid, _, _) = VerifyAuthenticode(src);
            if (!valid)
            {
                KillerDialog.Show(Current.MainWindow,
                    SetupText("Str_Dlg_InstallUnsigned",
                        "Installation refused: the running EXE does not carry a valid Authenticode " +
                        "signature.\n\nOnly signed builds of KillerPDF can be installed."),
                    AppName, MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

            // ── Downgrade guard ────────────────────────────────────────────────────
            if (File.Exists(InstallExe))
            {
                var runVer  = FileVersionInfo.GetVersionInfo(src).FileVersion ?? "";
                var instVer = FileVersionInfo.GetVersionInfo(InstallExe).FileVersion ?? "";
                if (string.Compare(runVer, instVer, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    var res = KillerDialog.Show(Current.MainWindow,
                        string.Format(SetupText("Str_Dlg_InstallDowngrade",
                            "You are about to install an older version ({0}) " +
                            "over the currently installed version ({1}).\n\n" +
                            "Downgrade anyway?"), runVer, instVer),
                        AppName, MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (res != MessageBoxResult.Yes) return false;
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
                    KillerDialog.Show(Current.MainWindow,
                        string.Format(SetupText("Str_Dlg_InstallWriteFailed",
                            "Couldn't write the installed copy at:\n{0}" +
                            "\n\nClose any open KillerPDF window (and check Task Manager for KillerPDF.exe), " +
                            "then run the installer again."), InstallExe),
                        AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
                try { File.SetAttributes(InstallExe, FileAttributes.Normal); } catch { }
                if (RegisterInstalledCopy(machine: false, desktop: wantDesktop, installedExecutable: InstallExe)
                    is { } desktopProblem)
                    KillerDialog.Show(Current.MainWindow,
                        string.Format(SetupText("Str_Dlg_InstallNoDesktopShortcut",
                            "KillerPDF is installed, but Windows did not allow the desktop shortcut:\n{0}\n\n" +
                            "Open KillerPDF from the Start menu."), desktopProblem),
                        AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
                return true;
            }
            catch (Exception ex)
            {
                KillerDialog.Show(Current.MainWindow,
                    string.Format(SetupText("Str_Dlg_InstallFailed", "Installation failed:\n{0}"), ex.Message), AppName,
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
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
                    n => n.Contains("pdf-file", StringComparison.OrdinalIgnoreCase)
                         && n.EndsWith(".ico", StringComparison.OrdinalIgnoreCase));
                if (rn == null) return; // dev build running from bin/ without the embedded icon

                using var rs = asm.GetManifestResourceStream(rn)!;
                using var fs = File.Create(iconPath);
                rs.CopyTo(fs);
            }
            catch { }
        }

        /// <summary>
        /// Register KillerPDF under either HKCU for a per-user install or HKLM for a machine-wide
        /// install. This advertises the handler without changing any user's chosen PDF default.
        /// </summary>
        private static void RegisterFileHandler(RegistryKey root, string exePath, string iconPath)
        {
            // Prefer the dedicated PDF file icon; fall back to the app icon if extraction didn't run
            string iconRef = File.Exists(iconPath)
                ? $"\"{iconPath}\",0"
                : $"{exePath},0";

            // ProgID definition
            using (var k = root.CreateSubKey(@"Software\Classes\KillerPDF.pdf"))
                k.SetValue("", "PDF Document");

            using (var k = root.CreateSubKey(
                @"Software\Classes\KillerPDF.pdf\DefaultIcon"))
                k.SetValue("", iconRef);

            using (var k = root.CreateSubKey(
                @"Software\Classes\KillerPDF.pdf\shell\open\command"))
                k.SetValue("", $"\"{exePath}\" \"%1\"");

            // Existing Open with choices can retain the launcher name after an upgrade.
            // Both application identities must launch the installed payload, not setup.
            foreach (string application in new[] { "KillerPDF.App.exe", "KillerPDF.exe" })
            {
                string applicationKey = @"Software\Classes\Applications\" + application;
                using (var k = root.CreateSubKey(applicationKey))
                    k.SetValue("FriendlyAppName", AppName);
                using (var k = root.CreateSubKey(applicationKey + @"\shell\open\command"))
                    k.SetValue("", $"\"{exePath}\" \"%1\"");
                using (var k = root.CreateSubKey(applicationKey + @"\SupportedTypes"))
                    k.SetValue(".pdf", "");
            }

            // Associate .pdf extension - adds KillerPDF to the "Open with" list
            using (var k = root.CreateSubKey(
                @"Software\Classes\.pdf\OpenWithProgids"))
                k.SetValue("KillerPDF.pdf", Array.Empty<byte>(), RegistryValueKind.None);

            // RegisteredApplications capability (used by Default Programs UI)
            using (var k = root.CreateSubKey(
                @"Software\KillerPDF\Capabilities"))
            {
                k.SetValue("ApplicationName",        AppName);
                k.SetValue("ApplicationDescription", "Lightweight PDF viewer and editor");
            }
            using (var k = root.CreateSubKey(
                @"Software\KillerPDF\Capabilities\FileAssociations"))
                k.SetValue(".pdf", "KillerPDF.pdf");

            // #183: without this the killerpdf:// handler never appears in Windows Settings >
            // Default apps > Choose defaults by link type. Maps the scheme to its ProgID
            // (Software\Classes\killerpdf, written by ProtocolRegistrar).
            using (var k = root.CreateSubKey(
                @"Software\KillerPDF\Capabilities\UrlAssociations"))
                k.SetValue(Services.ProtocolRegistrar.Scheme, Services.ProtocolRegistrar.Scheme);

            using (var k = root.CreateSubKey(@"Software\RegisteredApplications"))
                k.SetValue(AppName, @"Software\KillerPDF\Capabilities");

            // Tell the shell file associations have changed
            SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
        }

        private static void UnregisterFileHandler(RegistryKey root)
        {
            try { root.DeleteSubKeyTree(@"Software\Classes\KillerPDF.pdf", false); } catch { }
            try { root.DeleteSubKeyTree(@"Software\Classes\Applications\KillerPDF.App.exe", false); } catch { }
            try { root.DeleteSubKeyTree(@"Software\Classes\Applications\KillerPDF.exe", false); } catch { }
            try { root.DeleteSubKeyTree(@"Software\KillerPDF\Capabilities", false); } catch { }
            try
            {
                using var k = root.OpenSubKey(@"Software\Classes\.pdf\OpenWithProgids", writable: true);
                k?.DeleteValue("KillerPDF.pdf", throwOnMissingValue: false);
            }
            catch { }
            try
            {
                using var k = root.OpenSubKey(@"Software\RegisteredApplications", writable: true);
                k?.DeleteValue(AppName, throwOnMissingValue: false);
            }
            catch { }
        }

        private static void CreateShortcut(string lnkPath, string targetPath)
        {
            InstalledShortcuts.Create(lnkPath, targetPath);
        }

        // ============================================================
        // Uninstall
        // ============================================================

        // Null when this process may uninstall a machine copy itself. Otherwise the exit code of the
        // elevated uninstall it started, or why it could not start one, so deployment tools see a
        // declined or failed uninstall instead of success.
        private static int? RelaunchMachineUninstallElevatedIfNeeded(bool machine, bool silent)
        {
            if (!machine) return null;
            try
            {
                using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                var principal = new System.Security.Principal.WindowsPrincipal(identity);
                if (principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
                    return null;

                using Process elevated = Process.Start(new ProcessStartInfo(
                    Environment.ProcessPath
                        ?? throw new InvalidOperationException("The current executable path is unavailable."),
                    silent ? "/uninstall-silent" : "/uninstall")
                {
                    UseShellExecute = true,
                    Verb = "runas",
                }) ?? throw new InvalidOperationException("The elevated uninstall did not start.");
                elevated.WaitForExit();
                return elevated.ExitCode;
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                // UAC was declined. Leave the installation untouched.
                return 1223;
            }
            catch (Exception ex)
            {
                if (!silent)
                    KillerDialog.Show(null,
                        string.Format(SetupText("Str_Dlg_UninstallElevationFailed",
                            "Uninstall could not request administrator access:\n{0}"), ex.Message),
                        AppName, MessageBoxButton.OK, MessageBoxImage.Error);
                return 1;
            }
        }

        // Returns why the optional desktop shortcut could not be created, or null. Every other step
        // is required and throws.
        private static string? RegisterInstalledCopy(bool machine, bool desktop, bool? preview = null,
            string? installedExecutable = null, bool setupRollsBack = false)
        {
            string exePath = Path.GetFullPath(installedExecutable ?? Process.GetCurrentProcess().MainModule?.FileName
                ?? throw new InvalidOperationException("The installed application path is unavailable."));
            string installDirectory = Path.GetDirectoryName(exePath)
                ?? throw new InvalidOperationException("The installed application folder is unavailable.");
            string iconPath = Path.Combine(installDirectory, "pdf-file.ico");
            string startMenuShortcut = machine ? MachineStartMenuLnk : StartMenuLnk;
            RegistryKey registryRoot = machine ? Registry.LocalMachine : Registry.CurrentUser;

            if (!File.Exists(exePath))
                throw new FileNotFoundException("The installed application is missing.", exePath);

            // The copy is registered for uninstall first, so a shortcut or association that fails
            // below still leaves an installation Windows can remove instead of an orphaned payload.
            // The entry must keep naming the version that is actually installed. Setup's launcher
            // rolls a failed upgrade back to the old copy, so there the version comes last. A fresh
            // install and the app's own installs have nothing to roll back to, so they record it
            // first. The size is measured last on every path, once every file is in place.
            const string uninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\KillerPDF";
            using (var key = registryRoot.CreateSubKey(@"Software\KillerPDF"))
            {
                key.SetValue("Installed", 1);
                key.SetValue("InstallPath", exePath);
            }

            using (var key = registryRoot.CreateSubKey(uninstallKey))
            {
                key.SetValue("DisplayName", AppName);
                key.SetValue("Publisher", "Steve the Killer");
                key.SetValue("InstallLocation", installDirectory);
                key.SetValue("DisplayIcon", $"{exePath},0");
                key.SetValue("UninstallString", $"\"{exePath}\" /uninstall");
                key.SetValue("QuietUninstallString", $"\"{exePath}\" /uninstall-silent");
                key.SetValue("NoModify", 1);
                key.SetValue("NoRepair", 1);
            }
            if (!setupRollsBack) RecordVersion();

            CreateShortcut(startMenuShortcut, exePath);
            // The desktop shortcut is optional. When Windows refuses it, for example under
            // Controlled folder access, the install finishes and the caller reports a warning.
            string? desktopProblem = null;
            if (desktop)
            {
                try { CreateShortcut(InstalledShortcuts.DesktopPath(machine), exePath); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { desktopProblem = ex.Message; }
            }

            if (!File.Exists(iconPath)) ExtractFileIcon(iconPath);
            RegisterFileHandler(registryRoot, exePath, iconPath);
            Services.ProtocolRegistrar.Register(registryRoot, exePath);
            // "killerpdf" from any new terminal runs the command line of this copy.
            Services.CommandLinePath.Register(installDirectory, Path.GetFileName(exePath), machine);

            // #410: PDF previews in File Explorer and Outlook when setup asked for them. Without a
            // choice, a copy that already had them keeps them, as long as the handler is present.
            bool handlerPresent = File.Exists(Path.Combine(installDirectory, Services.PreviewHandlerRegistration.HostFileName));
            if (preview ?? (handlerPresent && Services.PreviewHandlerRegistration.IsRegistered(registryRoot)))
                Services.PreviewHandlerRegistration.Register(registryRoot, installDirectory);
            else
                Services.PreviewHandlerRegistration.Unregister(registryRoot);

            RecordVersion();

            SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
            return desktopProblem;

            void RecordVersion()
            {
                using (var key = registryRoot.CreateSubKey(@"Software\KillerPDF"))
                    key.SetValue("Version", AppVersion.Display);
                using (var key = registryRoot.CreateSubKey(uninstallKey))
                {
                    key.SetValue("DisplayVersion", AppVersion.Display);
                    key.SetValue("EstimatedSize", GetInstalledSizeKilobytes(installDirectory), RegistryValueKind.DWord);
                }
            }
        }

        private static int GetInstalledSizeKilobytes(string installDirectory)
        {
            long bytes = 0;
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            };
            foreach (var file in new DirectoryInfo(installDirectory).EnumerateFiles("*", options))
                bytes += file.Length;
            return (int)Math.Min(int.MaxValue, (bytes + 1023) / 1024);
        }

        private static int Uninstall(bool silent)
        {
            Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            if (!silent) { AppTheme.Configure(); ThemeManager.InitializeInstaller(); }
            string currentExe = Path.GetFullPath(Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty);
            bool machine = IsRegisteredCopy(Registry.LocalMachine, currentExe);
            if (RelaunchMachineUninstallElevatedIfNeeded(machine, silent) is int elevatedExit) return elevatedExit;

            // Uninstall removes only the payload the installer wrote, found through its manifest,
            // and only from a registered copy. A portable copy or a loose exe in some other folder
            // never gets its folder emptied.
            Services.InstallPayloadOwnership.OwnedPayload? owned =
                machine || IsRegisteredCopy(Registry.CurrentUser, currentExe)
                    ? Services.InstallPayloadOwnership.Read(Path.GetDirectoryName(currentExe) ?? string.Empty)
                    : null;
            if (owned is null)
            {
                if (!silent)
                    KillerDialog.Show(null,
                        SetupText("Str_Dlg_UninstallNotInstalled",
                            "This KillerPDF was not installed here, so nothing was removed.\nDelete the folder yourself if you no longer want it."),
                        AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
                return 1;
            }

            if (!silent)
            {
                var res = KillerDialog.Show(
                    null,
                    SetupText("Str_Dlg_UninstallConfirm", "Uninstall KillerPDF from this computer?"),
                    AppName,
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question,
                    confirmLabel: Application.Current.TryFindResource("Str_Btn_Uninstall") as string ?? "Uninstall",
                    cancelLabel: Application.Current.TryFindResource("Str_Btn_Cancel") as string ?? "Cancel");
                if (res != MessageBoxResult.Yes) return 1223;
            }

            if (!CloseRunningInstanceForUninstall())
            {
                if (!silent)
                    KillerDialog.Show(null,
                        SetupText("Str_Dlg_UninstallStillRunning",
                            "KillerPDF is still running.\nClose it, then try uninstalling again."),
                        AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
                return 1618;
            }

            // Shortcuts
            try { File.Delete(StartMenuLnk); } catch { }
            try { Directory.Delete(StartMenuDir, recursive: false); } catch { }
            try { File.Delete(DesktopLnk); } catch { }

            // Registry cleanup
            Services.PreviewHandlerRegistration.Unregister(Registry.CurrentUser);
            try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\KillerPDF"); } catch { }
            Services.ProtocolRegistrar.Unregister();
            try { Registry.CurrentUser.DeleteSubKeyTree(
                @"Software\Microsoft\Windows\CurrentVersion\Uninstall\KillerPDF"); } catch { }
            UnregisterFileHandler(Registry.CurrentUser);
            string installedDirectory = Path.GetDirectoryName(currentExe) ?? AppContext.BaseDirectory;
            Services.CommandLinePath.Unregister(installedDirectory, machine: false);
            // The kit update stack's staging folders (Services/UpdateService.cs): per-user here,
            // the elevated one under Program Files with the machine-wide half below.
            TryDeleteDir(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                AppName, "UpdateStage"));

            // Machine-wide half. Only reachable when Add/Remove Programs launched the Program Files
            // copy, which Windows runs elevated from the HKLM uninstall entry - so these writes
            // succeed there and simply fail harmlessly on a per-user uninstall.
            if (machine)
            {
                try { File.Delete(InstalledShortcuts.DesktopPath(machine: true)); } catch { }
                try { File.Delete(MachineStartMenuLnk); } catch { }
                try { Directory.Delete(MachineStartMenuDir, recursive: false); } catch { }
                UnregisterFileHandler(Registry.LocalMachine);
                Services.ProtocolRegistrar.Unregister(Registry.LocalMachine);   // #183
                Services.PreviewHandlerRegistration.Unregister(Registry.LocalMachine);
                Services.CommandLinePath.Unregister(installedDirectory, machine: true);
                TryDeleteDir(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    AppName + " Update"));
                try { Registry.LocalMachine.DeleteSubKeyTree(@"Software\KillerPDF"); } catch { }
                try { Registry.LocalMachine.DeleteSubKeyTree(
                    @"Software\Microsoft\Windows\CurrentVersion\Uninstall\KillerPDF"); } catch { }
            }

            // A preview surrogate holding the handler would keep the folder from being removed.
            Services.PreviewHandlerRegistration.StopHosts(installedDirectory);

            // Self-delete: deferred via cmd batch so the EXE can exit first. The batch names every
            // owned file and removes folders only when they are empty, never recursively.
            string bat = Path.Combine(Path.GetTempPath(), $"killerpdf_uninstall_{Guid.NewGuid():N}.bat");
            Services.InstallPayloadOwnership.WriteRemovalScript(bat, owned);
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{bat}\"")
            {
                WindowStyle    = ProcessWindowStyle.Hidden,
                UseShellExecute = true
            });

            if (!silent)
            {
                KillerDialog.Show(null,
                    Application.Current.TryFindResource("Str_Dlg_Uninstalled") as string ?? "KillerPDF has been uninstalled.", AppName,
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            return 0;
        }
    }
}
