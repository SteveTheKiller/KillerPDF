using System;
using System.IO;
using System.Security.Principal;
using System.Text;

namespace TDPdf.Diagnostics
{
    /// <summary>
    /// Best-effort file logger for the install / uninstall paths.
    ///
    /// Diagnoses Intune Win32 deployments where the install command exits 0
    /// but the detection rule fails. The log location is scope-aware:
    ///
    /// - SYSTEM (Intune install behavior=System) → <c>%ProgramData%\TDPdf\install.log</c>
    ///   so the log is readable both by SYSTEM (the writer) and any
    ///   administrator who later inspects the device.
    /// - Interactive user → <c>%LocalAppData%\TDPdf\install.log</c> so a
    ///   normal user can write without elevation and find the log under their
    ///   own profile.
    ///
    /// Every public method is wrapped in try/catch — a logging failure must
    /// never block install or uninstall.
    /// </summary>
    internal static class InstallLog
    {
        private const long MaxBytes      = 1L * 1024L * 1024L; // 1 MB
        private const long TruncateToBytes = 512L * 1024L;     // keep last 512 KB on rotate

        private static readonly object _gate = new();

        public static string Path
        {
            get
            {
                bool isSystem;
                try { isSystem = WindowsIdentity.GetCurrent().IsSystem; }
                catch { isSystem = false; }

                string baseDir = isSystem
                    ? Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
                    : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

                return System.IO.Path.Combine(baseDir, "TDPdf", "install.log");
            }
        }

        public static void Write(string message) => WriteLevel("INFO", message);

        public static void WriteError(string message, Exception? ex = null)
        {
            if (ex is null) WriteLevel("ERROR", message);
            else            WriteLevel("ERROR", $"{message}: {ex.GetType().FullName}: {ex.Message}\n{ex}");
        }

        public static void WriteHeader(string verb, string[] args)
        {
            try
            {
                string user = "?";
                bool isSystem = false;
                try
                {
                    var id = WindowsIdentity.GetCurrent();
                    user = id.Name;
                    isSystem = id.IsSystem;
                }
                catch { /* best-effort */ }

                WriteLevel("INFO",
                    $"---- {verb} ---- pid={System.Diagnostics.Process.GetCurrentProcess().Id} " +
                    $"user={user} isSystem={isSystem} args=[{string.Join(" ", args)}] " +
                    $"asmVersion={typeof(InstallLog).Assembly.GetName().Version} " +
                    $"os={Environment.OSVersion.VersionString} " +
                    $"64bitProc={Environment.Is64BitProcess} 64bitOS={Environment.Is64BitOperatingSystem}");
            }
            catch { /* best-effort */ }
        }

        private static void WriteLevel(string level, string message)
        {
            try
            {
                string path = Path;
                string dir = System.IO.Path.GetDirectoryName(path)!;
                Directory.CreateDirectory(dir);

                lock (_gate)
                {
                    RotateIfNeeded(path);
                    string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {level} {message}{Environment.NewLine}";
                    File.AppendAllText(path, line, Encoding.UTF8);
                }
            }
            catch
            {
                // Logging is best-effort. A failed log must never break install.
            }
        }

        private static void RotateIfNeeded(string path)
        {
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists || fi.Length < MaxBytes) return;

                // Read tail, rewrite. Cheap and adequate for diagnostic volume.
                byte[] all = File.ReadAllBytes(path);
                long start = Math.Max(0, all.LongLength - TruncateToBytes);
                using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
                fs.Write(all, (int)start, (int)(all.LongLength - start));
            }
            catch
            {
                // If rotation fails, the next append will simply continue past 1 MB.
            }
        }
    }
}
