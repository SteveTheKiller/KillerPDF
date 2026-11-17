using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;

namespace KillerLauncher
{
    internal static class Program
    {
        private const string ProductName = "KillerPDF";
        private const string InnerExeName = "KillerPDF.App.exe";
        private const string PayloadResourceName = "KillerLauncher.payload.zip";
        private const string ManifestName = "payload.manifest";
        private const string PortableMarkerName = ".killerpdf-portable";
        private const string TestInstallRootEnvironmentVariable = "KILLERPDF_TEST_INSTALL_ROOT";
        private const string SkipRegistrationEnvironmentVariable = "KILLERPDF_SKIP_REGISTRATION";

        private static readonly string UserInstallDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", ProductName);
        private static readonly string MachineInstallDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), ProductName);
        private static readonly string PortableRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductName, "Portable");

        [STAThread]
        private static int Main(string[] args)
        {
            // A setup exe is usually run from Downloads, where a planted version.dll or similar
            // would load before any system copy. Later loads search System32 and the exe's own
            // folder only, and the current directory drops out of the search.
            SetDefaultDllDirectories(LoadLibrarySearchSystem32 | LoadLibrarySearchApplicationDir);
            SetDllDirectory("");
            KillerFamily.Accessibility.AccessibleControls.Initialize();
            try
            {
#if INSTALLER_PACKAGE
                // An inherited installation-folder working directory prevents Windows from
                // renaming that folder, even after the installed application has exited.
                Directory.SetCurrentDirectory(Environment.SystemDirectory);
                TaskbarIdentity.UseInstaller();
                if (args.Any(a => string.Equals(a, "/install-user", StringComparison.OrdinalIgnoreCase)))
                {
                    // KillerPDF 1.7.x uses /install-user for its F12 update handoff. Keep that
                    // automatic path when the runtime is ready, but show the prerequisite-aware
                    // wizard on machines that cannot launch the new app yet.
                    if (!RuntimeReady())
                    {
                        Application.EnableVisualStyles();
                        Application.SetCompatibleTextRenderingDefault(false);
                        return InstallerWizard.Run(args);
                    }
                    return RelaunchAfterInstall(Install(machine: false, desktop: args.Any(a => string.Equals(a, "/desktop", StringComparison.OrdinalIgnoreCase)),
                        destinationOverride: ReadInstallDirectory(args), preview: ReadPreview(args)), args, machine: false);
                }
                if (args.Any(a => string.Equals(a, "/silent", StringComparison.OrdinalIgnoreCase)))
                {
                    // Unattended installs cannot show the prerequisite wizard. Refuse before
                    // extraction so automation receives a stable failure code and the machine is
                    // left completely untouched when the .NET 10 Desktop Runtime is absent (#275).
                    int? rejection = InstallerPrerequisitePolicy.SilentInstallRejection(RuntimeReady());
                    if (rejection.HasValue) return rejection.Value;
                    // With admin rights (winget, choco, RMM) the install is machine-wide. Without
                    // them, as the Microsoft Store runs installers, it is the normal per-user
                    // install with no prompt. The app never needs admin rights.
                    bool machine = KillerFamily.Deployment.InstallerExitCodes.IsElevated();
                    return RelaunchAfterInstall(Install(machine: machine, desktop: args.Any(a =>
                        string.Equals(a, "/desktop", StringComparison.OrdinalIgnoreCase)), destinationOverride: ReadInstallDirectory(args),
                        preview: ReadPreview(args)), args, machine);
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                return InstallerWizard.Run(args);
#else
                // The running portable app's footer Install button. The app waits for this exit
                // code and relaunches the installed copy itself.
                if (PortableLauncherPolicy.ReadInAppInstall(args) is { } inApp)
                {
                    Directory.SetCurrentDirectory(Environment.SystemDirectory);
                    return Install(machine: inApp.Machine, desktop: inApp.Desktop);
                }
                if (args.Any(PortableLauncherPolicy.IsInstallationArgument))
                    throw new InvalidOperationException(
                        LauncherStrings.Get("PortablePackage"));

                return RunPortable(args);
#endif
            }
            catch (Exception ex)
            {
#if INSTALLER_PACKAGE
                // The application's own Update button runs a machine install as /silent with /relaunch.
                // The app has already closed and nobody is watching a console, so that run shows the
                // failure and brings the old copy back; plain /silent (winget, an RMM) stays quiet.
                bool updateHandoff = UpdateRelaunchArguments.Requested(args);
                if (!updateHandoff && args.Any(a => string.Equals(a, "/silent", StringComparison.OrdinalIgnoreCase)))
                {
                    Console.Error.WriteLine(ProductName + " setup failed: " + ex.Message);
                    return KillerFamily.Deployment.InstallerExitCodes.FromException(ex);
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                try
                {
                    return InstallerWizard.ShowFailure(ex.Message, KillerFamily.Deployment.InstallerExitCodes.FromException(ex));
                }
                finally
                {
                    // Exit code 0 here only lets the relaunch run; the failure code is returned above.
                    if (updateHandoff)
                        RelaunchAfterInstall(0, args,
                            machine: !args.Any(a => string.Equals(a, "/install-user", StringComparison.OrdinalIgnoreCase))
                                && KillerFamily.Deployment.InstallerExitCodes.IsElevated());
                }
#else
                if (args.Any(a => string.Equals(a, "--startup-smoke", StringComparison.OrdinalIgnoreCase)))
                {
                    Console.Error.WriteLine(ProductName + " startup check failed: " + ex);
                    return 1;
                }
                LauncherDialog.ShowError(LauncherStrings.Format("StartFailed", ex.Message));
                return 1;
#endif
            }
        }

        private static int RunPortable(string[] args)
        {
            bool smoke = args.Any(a => string.Equals(a, "--startup-smoke", StringComparison.OrdinalIgnoreCase));
            if (!smoke) SweepAbandonedPortableDirectories();
            string root = smoke ? Path.GetTempPath() : PortableRoot;
            Directory.CreateDirectory(root);

            string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown";
            string directory = Path.Combine(root,
                version + "-" + Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N"));

            try
            {
                ExtractAndVerify(directory);
                WritePortableMarker(directory, version, null);

                var start = new ProcessStartInfo(Path.Combine(directory, InnerExeName), QuoteArguments(args))
                {
                    UseShellExecute = false,
                    WorkingDirectory = directory
                };
                start.EnvironmentVariables["KILLERPDF_LAUNCHER_PATH"] = CurrentExecutablePath();
                start.EnvironmentVariables["KILLERPDF_LAUNCHER_PID"] =
                    Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture);

                using (var child = Process.Start(start))
                {
                    if (child == null) throw new InvalidOperationException(LauncherStrings.Get("ProcessFailed"));
                    WritePortableMarker(directory, version, child);
                    child.WaitForExit();
                    return child.ExitCode;
                }
            }
            finally
            {
                // A scanner or another late reader can briefly retain a payload file after the
                // child exits. Leave that directory for the next launcher's abandoned-run sweep
                // instead of replacing the child's real exit result with a startup error.
                PortableDirectoryCleanup.TryDelete(directory);
            }
        }

        internal static int Install(bool machine, bool desktop, string? destinationOverride = null, bool? preview = null)
        {
            using var installLease = KillerFamily.Deployment.InstallerExitCodes.Acquire(ProductName);
            // The Store package leaves out the preview handler because it needs the shared .NET runtime.
            if (IsStorePackage && preview == true) preview = false;
            if (!IsTrustedForInstall(CurrentExecutablePath()))
                throw new System.Security.SecurityException(
                    LauncherStrings.Get("SignatureInvalid"));

            string? testRoot = Environment.GetEnvironmentVariable(TestInstallRootEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(testRoot) && !machine &&
                (File.Exists(Path.Combine(MachineInstallDirectory, InnerExeName)) ||
                 File.Exists(Path.Combine(MachineInstallDirectory, "KillerPDF.exe"))))
                throw new InvalidOperationException(
                    LauncherStrings.Get("AlreadyMachine"));

            string destination = ValidateInstallDestination(!string.IsNullOrWhiteSpace(testRoot)
                ? testRoot
                : destinationOverride ?? DefaultInstallDirectory(machine));
            if (string.IsNullOrWhiteSpace(testRoot))
                CloseInstalledCopies(machine
                    ? new[] { destination, UserInstallDirectory }
                    : new[] { destination });
            string parent = Path.GetDirectoryName(destination) ?? throw new InvalidOperationException(LauncherStrings.Get("InvalidDirectory"));
            Directory.CreateDirectory(parent);
            // A previous copy a late reader held, or staging a failed install could not remove, is
            // cleared before this install adds its own.
            foreach (string leftover in PortableDirectoryCleanup.LeftoverSiblings(destination))
                PortableDirectoryCleanup.TryDelete(leftover);

            string staging = destination + ".staging-" + Guid.NewGuid().ToString("N");
            string backup = destination + ".previous-" + Guid.NewGuid().ToString("N");
            bool movedExisting = false;
            bool registered = false;
            try
            {
                ExtractAndVerify(staging, machine && string.IsNullOrWhiteSpace(testRoot));

                ValidateInstallDestination(destination);
                if (Directory.Exists(destination))
                {
                    // An empty folder, such as one made in the folder picker, holds no previous copy,
                    // so a failed first install there must not be "restored" to it.
                    if (!Directory.EnumerateFileSystemEntries(destination).Any()) DeleteEmptyDirectoryWithRetries(destination);
                    else
                    {
                        MoveDirectoryWithRetries(destination, backup);
                        movedExisting = true;
                    }
                }
                MoveDirectoryWithRetries(staging, destination);

                int registrationExit = string.Equals(
                    Environment.GetEnvironmentVariable(SkipRegistrationEnvironmentVariable), "1", StringComparison.Ordinal)
                    ? 0
                    : RunRegistration(destination, machine, desktop, preview, rollsBack: movedExisting);
                if (registrationExit != 0)
                    throw new System.ComponentModel.Win32Exception(registrationExit,
                        LauncherStrings.Format("RegistrationFailed", registrationExit));
                // Windows now lists the new copy, so nothing after this point rolls it back.
                registered = true;

                // A machine-wide install supersedes the current account's per-user copy. This
                // also covers unattended /silent installs that do not return through the portable
                // app's InstallAndRelaunch cleanup path. Like its exit code, a failure to start it
                // leaves a per-user copy beside a complete install instead of failing the install.
                if (machine && string.IsNullOrWhiteSpace(testRoot))
                {
                    try { RunMaintenance(destination, "/remove-user-install"); }
                    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception
                        or InvalidOperationException or IOException) { }
                }

                // A previous copy a late reader still holds is left beside the install rather than
                // failing an install that is already complete; the next install removes it.
                if (movedExisting) PortableDirectoryCleanup.TryDelete(backup);
                return 0;
            }
            catch
            {
                // The previous version comes back first; cleanup of the staging folder is best
                // effort afterward, so a locked file there can neither strand the backup nor
                // replace the exception that explains what went wrong.
                if (!registered && movedExisting && Directory.Exists(backup))
                {
                    try
                    {
                        if (Directory.Exists(destination)) DeleteDirectoryWithRetries(destination);
                        if (!Directory.Exists(destination)) MoveDirectoryWithRetries(backup, destination);
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
                PortableDirectoryCleanup.TryDelete(staging);
                throw;
            }
        }

        internal static string DefaultInstallDirectory(bool machine) =>
            machine ? MachineInstallDirectory : UserInstallDirectory;

        // The self-update hands off to this installer directly, with no script in between, and
        // asks it to start the new copy. An elevated installer starts it through explorer.exe so
        // the application comes back at the user's normal integrity level; explorer cannot pass
        // arguments, so the document to reopen is lost on that path.
        private static int RelaunchAfterInstall(int exitCode, string[] args, bool machine)
        {
            if (exitCode != 0 || !UpdateRelaunchArguments.Requested(args)) return exitCode;
            try
            {
                string destination = ReadInstallDirectory(args) ?? DefaultInstallDirectory(machine);
                string executable = Path.Combine(destination, InnerExeName);
                if (!File.Exists(executable)) return exitCode;
                string? reopen = UpdateRelaunchArguments.ReopenFile(args);
                if (KillerFamily.Deployment.InstallerExitCodes.IsElevated())
                    Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),"\"" + executable + "\"") { UseShellExecute = true });
                else
                    Process.Start(new ProcessStartInfo(executable, reopen == null ? string.Empty : "\"" + reopen + "\"")
                    {
                        UseShellExecute = true,
                        WorkingDirectory = destination
                    });
            }
            catch (Exception)
            {
                // The installation itself succeeded; the user can start the new copy normally.
            }
            return exitCode;
        }

        internal sealed class InstalledCopy
        {
            internal InstalledCopy(string directory, string executable, string version, bool machine, bool desktopShortcut,
                bool previewHandler)
            {
                Directory = directory;
                Executable = executable;
                Version = version;
                Machine = machine;
                DesktopShortcut = desktopShortcut;
                PreviewHandler = previewHandler;
            }

            internal string Directory { get; }
            internal string Executable { get; }
            internal string Version { get; }
            internal bool Machine { get; }
            internal bool DesktopShortcut { get; }
            internal bool PreviewHandler { get; }
        }

        internal static InstalledCopy? FindInstalledCopy()
        {
            InstalledCopy? machine = FindInstalledCopy(MachineInstallDirectory, true);
            return machine ?? FindInstalledCopy(UserInstallDirectory, false);
        }

        private static InstalledCopy? FindInstalledCopy(string directory, bool machine)
        {
            string executable = Path.Combine(directory, InnerExeName);
            if (!File.Exists(executable))
            {
                executable = Path.Combine(directory, "KillerPDF.exe");
                if (!File.Exists(executable)) return null;
            }

            string rawVersion = FileVersionInfo.GetVersionInfo(executable).FileVersion ?? "installed";
            string version = Version.TryParse(rawVersion, out Version? parsed) ? parsed.ToString(3) : rawVersion;
            return new InstalledCopy(directory, executable, version, machine, File.Exists(DesktopShortcutPath(machine)),
                HasPreviewHandler(machine));
        }

        internal static string DesktopShortcutPath(bool machine) => Path.Combine(Environment.GetFolderPath(machine
            ? Environment.SpecialFolder.CommonDesktopDirectory
            : Environment.SpecialFolder.DesktopDirectory), "KillerPDF.lnk");

        /// <summary>
        /// True when the desktop shortcut exists and opens the copy installed in
        /// <paramref name="installDirectory"/>. A link left by a portable copy or an earlier install
        /// does not count. When the link's target cannot be read, its presence is taken as success.
        /// </summary>
        internal static bool DesktopShortcutOpensInstalledCopy(bool machine, string installDirectory)
        {
            string link = DesktopShortcutPath(machine);
            if (!File.Exists(link)) return false;
            string? target = ShortcutTarget(link);
            return target == null || string.Equals(Path.GetFullPath(target),
                Path.GetFullPath(Path.Combine(installDirectory, InnerExeName)), StringComparison.OrdinalIgnoreCase);
        }

        // Reads a shortcut's target through the Windows Script Host, or null where it is unavailable.
        private static string? ShortcutTarget(string linkPath)
        {
            object? shell = null, shortcut = null;
            try
            {
                Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) return null;
                shell = Activator.CreateInstance(shellType);
                shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { linkPath });
                return shortcut?.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, shortcut, null) as string
                    is { Length: > 0 } target ? target : null;
            }
            catch (Exception) { return null; }
            finally
            {
                if (shortcut != null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
                if (shell != null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
            }
        }

        // The File Explorer and Outlook preview handler (#410), registered by the app's /preview pass.
        private const string PreviewHandlerServerKey =
            @"Software\Classes\CLSID\{0EB8F20D-6E18-4B80-9CDF-FF663C52B5BD}\InprocServer32";

        private static bool HasPreviewHandler(bool machine)
        {
            try
            {
                using (Microsoft.Win32.RegistryKey? key = (machine ? Microsoft.Win32.Registry.LocalMachine
                    : Microsoft.Win32.Registry.CurrentUser).OpenSubKey(PreviewHandlerServerKey))
                    return key?.GetValue("") is string path && path.Length > 0;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                return false;
            }
        }

        private static bool? ReadPreview(IEnumerable<string> args)
        {
            if (args.Any(a => string.Equals(a, "/preview", StringComparison.OrdinalIgnoreCase))) return true;
            if (args.Any(a => string.Equals(a, "/no-preview", StringComparison.OrdinalIgnoreCase))) return false;
            return null;
        }

        internal static string SetupVersion =>
            Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown";

        // Older 1.7.x uninstallers could remove their Installed Apps entry while leaving the
        // application running and its files locked. Setup owns that migration now: ask any copy
        // running from a folder we are about to replace or remove to close normally, preserving
        // its unsaved-work prompt, and do not touch the installation until it has exited.
        private static void CloseInstalledCopies(IEnumerable<string> installDirectories)
        {
            string[] roots = installDirectories
                .Where(directory => !string.IsNullOrWhiteSpace(directory))
                .Select(directory => Path.GetFullPath(directory)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (roots.Length == 0) return;
            StopPreviewHosts(roots);

            int currentProcessId;
            using (Process current = Process.GetCurrentProcess()) currentProcessId = current.Id;
            var running = new List<Process>();
            try
            {
                foreach (Process process in Process.GetProcesses())
                {
                    if (process.Id == currentProcessId)
                    {
                        process.Dispose();
                        continue;
                    }
                    try
                    {
                        string path = Path.GetFullPath(process.MainModule?.FileName ?? string.Empty);
                        if (!roots.Any(root => path.StartsWith(root, StringComparison.OrdinalIgnoreCase)))
                        {
                            process.Dispose();
                            continue;
                        }
                        running.Add(process);
                    }
                    catch { process.Dispose(); }
                }

                foreach (Process process in running)
                {
                    if (process.HasExited) continue;
                    // A copy that is still running is the busy case deployment tools retry on
                    // (exit 1618), so the exception carries that code with the localized text.
                    if (!process.CloseMainWindow())
                        throw new System.ComponentModel.Win32Exception(
                            KillerFamily.Deployment.InstallerExitCodes.Busy, LauncherStrings.Get("StillRunning"));
                }
                foreach (Process process in running)
                {
                    if (!process.HasExited && !process.WaitForExit(60000))
                        throw new System.ComponentModel.Win32Exception(
                            KillerFamily.Deployment.InstallerExitCodes.Busy, LauncherStrings.Get("StillOpen"));
                }
            }
            finally
            {
                foreach (Process process in running) process.Dispose();
            }
        }

        // A prevhost.exe surrogate that loaded the preview handler (#410) from a folder about to be
        // replaced keeps its files open. It holds no user work, and the shell starts a new one.
        private static void StopPreviewHosts(string[] roots)
        {
            foreach (Process process in Process.GetProcessesByName("prevhost"))
            {
                using (process)
                {
                    try
                    {
                        bool loaded = false;
                        foreach (ProcessModule module in process.Modules)
                        {
                            string path = module.FileName ?? string.Empty;
                            if (roots.Any(root => path.StartsWith(root, StringComparison.OrdinalIgnoreCase))) loaded = true;
                            module.Dispose();
                        }
                        if (!loaded) continue;
                        process.Kill();
                        process.WaitForExit(5000);
                    }
                    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException
                        or NotSupportedException) { }
                }
            }
        }

        internal static string ValidateInstallDirectory(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory))
                throw new InvalidOperationException(LauncherStrings.Get("ChooseDirectory"));
            string path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(directory.Trim()));
            string? root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root) || string.Equals(
                    path.TrimEnd(Path.DirectorySeparatorChar), root.TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(LauncherStrings.Get("DriveRoot"));
            return path.TrimEnd(Path.DirectorySeparatorChar);
        }

        internal static string ValidateInstallDestination(string directory)
        {
            string path = ValidateInstallDirectory(directory);
            for (string? ancestor = path; ancestor != null; ancestor = Path.GetDirectoryName(ancestor))
                if (Directory.Exists(ancestor) &&
                    (File.GetAttributes(ancestor) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException(LauncherStrings.Get("RequestedDirectoryInvalid"));
            if (!Directory.Exists(path)) return path;
            if (!Directory.EnumerateFileSystemEntries(path).Any()) return path;

            string manifestPath = Path.Combine(path, ManifestName);
            if (!File.Exists(manifestPath) ||
                (File.GetAttributes(manifestPath) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException(LauncherStrings.Get("RequestedDirectoryInvalid"));
            Dictionary<string, ManifestFile> manifest;
            using (var reader = File.OpenText(manifestPath)) manifest = ReadManifest(reader);
            if (!manifest.ContainsKey(InnerExeName) || manifest.ContainsKey(ManifestName))
                throw new InvalidOperationException(LauncherStrings.Get("RequestedDirectoryInvalid"));

            var pending = new Stack<string>();
            pending.Push(path);
            while (pending.Count > 0)
            {
                foreach (string entry in Directory.EnumerateFileSystemEntries(pending.Pop()))
                {
                    FileAttributes attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidOperationException(LauncherStrings.Get("RequestedDirectoryInvalid"));
                    string relative = entry.Substring(path.Length + 1).Replace('\\', '/');
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        if (!manifest.Keys.Any(k => k.StartsWith(relative + "/", StringComparison.OrdinalIgnoreCase)))
                            throw new InvalidOperationException(LauncherStrings.Get("RequestedDirectoryInvalid"));
                        pending.Push(entry);
                    }
                    else if (!string.Equals(relative, ManifestName, StringComparison.OrdinalIgnoreCase) &&
                        !manifest.ContainsKey(relative))
                    {
                        // Registration creates this wrapper beside the verified payload.
                        if (!string.Equals(relative, "KillerPDF.cmd", StringComparison.OrdinalIgnoreCase) ||
                            File.ReadAllText(entry) != "@\"%~dp0KillerPDF.App.exe\" %*\r\n@exit /b %ERRORLEVEL%\r\n")
                            throw new InvalidOperationException(LauncherStrings.Get("RequestedDirectoryInvalid"));
                    }
                }
            }
            foreach (var item in manifest)
            {
                string file = Path.Combine(path, item.Key.Replace('/', Path.DirectorySeparatorChar));
                var info = new FileInfo(file);
                if (!info.Exists || info.Length != item.Value.Size ||
                    !string.Equals(HashFile(file), item.Value.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(LauncherStrings.Get("RequestedDirectoryInvalid"));
            }
            return path;
        }

        internal static string InstalledExecutable(string directory) =>
            Path.Combine(ValidateInstallDirectory(directory), InnerExeName);

        internal static string EncodeInstallDirectoryArgument(string directory) =>
            "/install-dir64:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(ValidateInstallDirectory(directory)));

        private static string? ReadInstallDirectory(IEnumerable<string> args)
        {
            const string prefix = "/install-dir64:";
            string? value = args.FirstOrDefault(a => a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            if (value == null) return null;
            try { return Encoding.UTF8.GetString(Convert.FromBase64String(value.Substring(prefix.Length))); }
            catch (FormatException) { throw new InvalidOperationException(LauncherStrings.Get("RequestedDirectoryInvalid")); }
        }

        /// <summary>
        /// True for the Microsoft Store package, whose payload carries its own .NET runtime because the
        /// Store installs silently on machines that may not have the .NET 10 Desktop Runtime.
        /// </summary>
        internal static bool IsStorePackage =>
#if STORE_PACKAGE
            true;
#else
            false;
#endif

        internal static bool RuntimeReady() => IsStorePackage || HasDesktopRuntime10();

        internal static bool HasDesktopRuntime10() => InstallerPrerequisitePolicy.HasDesktopRuntime10(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), IsArm64Host());

        private const ushort ImageFileMachineArm64 = 0xAA64;
        private const uint LoadLibrarySearchApplicationDir = 0x00000200;
        private const uint LoadLibrarySearchSystem32 = 0x00000800;

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetDefaultDllDirectories(uint directoryFlags);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetDllDirectory(string pathName);

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWow64Process2(IntPtr process, out ushort processMachine, out ushort nativeMachine);

        /// <summary>True on Arm64 Windows, where this x64 setup runs under emulation.</summary>
        private static bool IsArm64Host()
        {
            try
            {
                return IsWow64Process2(Process.GetCurrentProcess().Handle, out _, out ushort native) &&
                    native == ImageFileMachineArm64;
            }
            catch (EntryPointNotFoundException) { return false; }
        }

        private static int RunRegistration(string directory, bool machine, bool desktop, bool? preview, bool rollsBack)
        {
            var arguments = new List<string> { machine ? "/register-machine" : "/register-user" };
            if (desktop) arguments.Add("/desktop");
            // Registration writes the version last only when a failure here restores the previous copy.
            if (!rollsBack) arguments.Add("/no-rollback");
            if (preview.HasValue) arguments.Add(preview.Value ? "/preview" : "/no-preview");
            var start = new ProcessStartInfo(Path.Combine(directory, InnerExeName), QuoteArguments(arguments.ToArray()))
            {
                UseShellExecute = false,
                WorkingDirectory = directory
            };
            using (var process = Process.Start(start))
            {
                if (process == null) return 1;
                process.WaitForExit();
                return process.ExitCode;
            }
        }

        private static int RunMaintenance(string directory, string argument)
        {
            var start = new ProcessStartInfo(Path.Combine(directory, InnerExeName), argument)
            {
                UseShellExecute = false,
                WorkingDirectory = directory
            };
            using (var process = Process.Start(start))
            {
                if (process == null) return 1;
                process.WaitForExit();
                return process.ExitCode;
            }
        }

        // An all-users install is what HKLM file types, the protocol, the preview handler and the
        // elevated uninstall point at, so its folder must not be writable by other users. A custom
        // folder such as C:\KillerPDF inherits Authenticated Users Modify from the drive root, so the
        // staging folder gets its own protected ACL before any file is written, and the files inherit
        // it. The move into place keeps the ACL. Program Files already looks like this.
        internal static void CreateProtectedDirectory(string path)
        {
            if (Directory.Exists(path) || File.Exists(path))
                throw new IOException(LauncherStrings.Get("InvalidDirectory"));
            var security = new System.Security.AccessControl.DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            var administrators = new System.Security.Principal.SecurityIdentifier(
                System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null);
            security.SetOwner(administrators);
            const System.Security.AccessControl.InheritanceFlags everything =
                System.Security.AccessControl.InheritanceFlags.ContainerInherit
                | System.Security.AccessControl.InheritanceFlags.ObjectInherit;
            void Allow(System.Security.Principal.WellKnownSidType sid, System.Security.AccessControl.FileSystemRights rights) =>
                security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                    new System.Security.Principal.SecurityIdentifier(sid, null), rights, everything,
                    System.Security.AccessControl.PropagationFlags.None,
                    System.Security.AccessControl.AccessControlType.Allow));
            Allow(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, System.Security.AccessControl.FileSystemRights.FullControl);
            Allow(System.Security.Principal.WellKnownSidType.LocalSystemSid, System.Security.AccessControl.FileSystemRights.FullControl);
            Allow(System.Security.Principal.WellKnownSidType.BuiltinUsersSid, System.Security.AccessControl.FileSystemRights.ReadAndExecute);
            security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                new System.Security.Principal.SecurityIdentifier("S-1-15-2-1"),
                System.Security.AccessControl.FileSystemRights.ReadAndExecute, everything,
                System.Security.AccessControl.PropagationFlags.None,
                System.Security.AccessControl.AccessControlType.Allow));
            Directory.CreateDirectory(path, security);
            // Another user could have made the folder between the check and the create, in which
            // case CreateDirectory leaves their ACL alone. Only a folder owned by Administrators
            // is ours.
            var owner = Directory.GetAccessControl(path).GetOwner(typeof(System.Security.Principal.SecurityIdentifier));
            if (!administrators.Equals(owner))
                throw new UnauthorizedAccessException(LauncherStrings.Get("InvalidDirectory"));
        }

        private static void ExtractAndVerify(string destination, bool machine = false)
        {
            if (machine) CreateProtectedDirectory(destination);
            else Directory.CreateDirectory(destination);
            using (var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream(PayloadResourceName))
            {
                if (payload == null) throw new InvalidOperationException(LauncherStrings.Get("PayloadMissing"));
                using (var archive = new ZipArchive(payload, ZipArchiveMode.Read, leaveOpen: false))
                {
                    var manifestEntry = archive.GetEntry(ManifestName)
                        ?? throw new InvalidDataException(LauncherStrings.Get("ManifestMissing"));
                    Dictionary<string, ManifestFile> manifest;
                    using (var reader = new StreamReader(manifestEntry.Open(), Encoding.UTF8, true))
                        manifest = ReadManifest(reader);

                    var payloadEntries = archive.Entries
                        .Where(e => !string.IsNullOrEmpty(e.Name) && !string.Equals(e.FullName, ManifestName, StringComparison.Ordinal))
                        .ToDictionary(e => NormalizeRelativePath(e.FullName), StringComparer.OrdinalIgnoreCase);

                    if (payloadEntries.Count != manifest.Count || manifest.Keys.Any(k => !payloadEntries.ContainsKey(k)))
                        throw new InvalidDataException(LauncherStrings.Get("ManifestMismatch"));

                    string destinationRoot = EnsureTrailingSeparator(Path.GetFullPath(destination));
                    foreach (var item in manifest.OrderBy(p => p.Key, StringComparer.Ordinal))
                    {
                        string outputPath = Path.GetFullPath(Path.Combine(destination, item.Key.Replace('/', Path.DirectorySeparatorChar)));
                        if (!outputPath.StartsWith(destinationRoot, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException(LauncherStrings.Get("UnsafePath"));

                        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? destination);
                        using (var input = payloadEntries[item.Key].Open())
                        using (var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                            input.CopyTo(output);

                        var info = new FileInfo(outputPath);
                        if (info.Length != item.Value.Size || !string.Equals(HashFile(outputPath), item.Value.Sha256, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException(LauncherStrings.Format("VerificationFailed", item.Key));
                    }

                    File.WriteAllLines(Path.Combine(destination, ManifestName),
                        manifest.OrderBy(p => p.Key, StringComparer.Ordinal)
                            .Select(p => p.Value.Sha256 + "\t" +
                                p.Value.Size.ToString(CultureInfo.InvariantCulture) + "\t" + p.Key),
                        new UTF8Encoding(false));
                }
            }

            if (!File.Exists(Path.Combine(destination, InnerExeName)))
                throw new InvalidDataException(LauncherStrings.Format("ExecutableMissing", InnerExeName));
        }

        private static Dictionary<string, ManifestFile> ReadManifest(TextReader reader)
        {
            var result = new Dictionary<string, ManifestFile>(StringComparer.OrdinalIgnoreCase);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#", StringComparison.Ordinal)) continue;
                var parts = line.Split(new[] { '\t' }, 3);
                if (parts.Length != 3 || parts[0].Length != 64 || !long.TryParse(parts[1], NumberStyles.None,
                        CultureInfo.InvariantCulture, out long size) || size < 0)
                    throw new InvalidDataException(LauncherStrings.Get("ManifestInvalid"));
                string path = NormalizeRelativePath(parts[2]);
                if (result.ContainsKey(path))
                    throw new InvalidDataException(LauncherStrings.Get("DuplicatePath"));
                result.Add(path, new ManifestFile(parts[0], size));
            }
            return result;
        }

        private static string NormalizeRelativePath(string path)
        {
            string normalized = path.Replace('\\', '/').TrimStart('/');
            if (string.IsNullOrWhiteSpace(normalized) || Path.IsPathRooted(path) || normalized.Contains(":") ||
                normalized.Split('/').Any(p => p.Length == 0 || p == "." || p == ".."))
                throw new InvalidDataException(LauncherStrings.Format("UnsafePathDetail", path));
            return normalized;
        }

        private static void SweepAbandonedPortableDirectories()
        {
            if (!Directory.Exists(PortableRoot)) return;
            foreach (string directory in Directory.GetDirectories(PortableRoot))
            {
                string marker = Path.Combine(directory, PortableMarkerName);
                if (!File.Exists(marker)) continue;
                try
                {
                    var lines = File.ReadAllLines(marker);
                    if (lines.Length > 0 && string.Equals(lines[0], ProductName, StringComparison.Ordinal) &&
                        !MarkerHasLiveProcess(directory, lines))
                        PortableDirectoryCleanup.TryDelete(directory);
                }
                catch { }
            }
        }

        private static void WritePortableMarker(string directory, string version, Process? child)
        {
            using (Process launcher = Process.GetCurrentProcess())
            File.WriteAllLines(Path.Combine(directory, PortableMarkerName), new[]
            {
                ProductName,
                version,
                launcher.Id.ToString(CultureInfo.InvariantCulture),
                child?.Id.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                launcher.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture),
                child?.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture) ?? string.Empty
            }, new UTF8Encoding(false));
        }

        private static bool MarkerHasLiveProcess(string directory, string[] lines)
        {
            if (lines.Length >= 6)
            {
                if (PortableProcessIdentity.IsLive(lines[2], lines[4])) return true;
                if (PortableProcessIdentity.IsLive(lines[3], lines[5], directory)) return true;
                return false;
            }

            // Legacy markers carried only PIDs. Ignore their launcher PID because Windows may
            // have reused it, and retain the directory only if its child still runs from there.
            return lines.Length >= 4
                && PortableProcessIdentity.IsLive(lines[3], null, directory);
        }

        private static void MoveDirectoryWithRetries(string source, string destination)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    Directory.Move(source, destination);
                    return;
                }
                catch (IOException) when (attempt < 20)
                {
                    System.Threading.Thread.Sleep(250);
                }
            }
        }

        // Never recursive: a file that appears in the folder meanwhile stops the delete.
        private static void DeleteEmptyDirectoryWithRetries(string directory)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    Directory.Delete(directory);
                    return;
                }
                catch (IOException) when (attempt < 20)
                {
                    System.Threading.Thread.Sleep(250);
                }
            }
        }

        private static void DeleteDirectoryWithRetries(string directory)
        {
            if (!PortableDirectoryCleanup.TryDelete(directory))
                throw new IOException(LauncherStrings.Format("CleanupFailed", directory));
        }

        private static bool IsTrustedForInstall(string path)
        {
#if ALLOW_UNSIGNED_INSTALL
            // Local packages produced by build-portable.ps1 are intentionally unsigned and must
            // remain installable for end-to-end testing. release.ps1 omits this compile-time flag;
            // its public launcher has no environment-variable or command-line bypass.
            return true;
#else
            return AuthenticodeTrust.IsValid(path);
#endif
        }

        private static string HashFile(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty);
        }

        private static string CurrentExecutablePath() => Process.GetCurrentProcess().MainModule?.FileName
            ?? Assembly.GetExecutingAssembly().Location;

        private static string EnsureTrailingSeparator(string path) =>
            path.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? path
                : path + Path.DirectorySeparatorChar;

        private static string QuoteArguments(IEnumerable<string> arguments) =>
            string.Join(" ", arguments.Select(QuoteArgument));

        private static string QuoteArgument(string argument)
        {
            if (argument.Length > 0 && argument.All(c => !char.IsWhiteSpace(c) && c != '"')) return argument;
            var sb = new StringBuilder("\"");
            int slashes = 0;
            foreach (char c in argument)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') sb.Append('\\', slashes * 2 + 1).Append('"');
                else { sb.Append('\\', slashes).Append(c); }
                slashes = 0;
            }
            sb.Append('\\', slashes * 2).Append('"');
            return sb.ToString();
        }

        private sealed class ManifestFile
        {
            internal ManifestFile(string sha256, long size) { Sha256 = sha256; Size = size; }
            internal string Sha256 { get; }
            internal long Size { get; }
        }
    }

    internal static class AuthenticodeTrust
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct WinTrustFileInfo
        {
            internal uint Size;
            internal IntPtr FilePath;
            internal IntPtr File;
            internal IntPtr KnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WinTrustData
        {
            internal uint Size;
            internal IntPtr PolicyCallbackData;
            internal IntPtr SipClientData;
            internal uint UiChoice;
            internal uint RevocationChecks;
            internal uint UnionChoice;
            internal IntPtr Union;
            internal uint StateAction;
            internal IntPtr StateData;
            internal IntPtr UrlReference;
            internal uint ProviderFlags;
            internal uint UiContext;
            internal IntPtr SignatureSettings;
        }

        private static readonly Guid VerifyGeneric = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern uint WinVerifyTrust(IntPtr window, ref Guid action, IntPtr trustData);

        internal static bool IsValid(string path)
        {
            IntPtr pathPointer = Marshal.StringToHGlobalUni(path);
            IntPtr filePointer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WinTrustFileInfo)));
            IntPtr dataPointer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WinTrustData)));
            try
            {
                Marshal.StructureToPtr(new WinTrustFileInfo
                {
                    Size = (uint)Marshal.SizeOf(typeof(WinTrustFileInfo)),
                    FilePath = pathPointer
                }, filePointer, false);
                Marshal.StructureToPtr(new WinTrustData
                {
                    Size = (uint)Marshal.SizeOf(typeof(WinTrustData)),
                    UiChoice = 2,
                    UnionChoice = 1,
                    Union = filePointer,
                    ProviderFlags = 0x1000
                }, dataPointer, false);
                var action = VerifyGeneric;
                return WinVerifyTrust(IntPtr.Zero, ref action, dataPointer) == 0;
            }
            finally
            {
                Marshal.FreeHGlobal(dataPointer);
                Marshal.FreeHGlobal(filePointer);
                Marshal.FreeHGlobal(pathPointer);
            }
        }
    }
}
