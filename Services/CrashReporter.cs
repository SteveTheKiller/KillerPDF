using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

namespace KillerPDF
{
    /// <summary>
    /// Writes structured crash logs to %LOCALAPPDATA%\KillerPDF\Logs\ and maintains
    /// a rolling buffer of recent status-bar messages for post-mortem context.
    /// </summary>
    internal static class CrashReporter
    {
        private const int  StatusBufferSize = 50;
        private const long LogDirCapBytes   = 20L * 1024 * 1024; // 20 MB

        private static readonly Queue<string> _statusRing = new();

        // ── Public properties ────────────────────────────────────────────────

        internal static string LogDir { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KillerPDF", "Logs");

        /// <summary>Path of the log file written by the most recent Capture() call.</summary>
        internal static string? LastLogPath { get; private set; }

        // ── Status ring buffer ───────────────────────────────────────────────

        /// <summary>
        /// Called by MainWindow.SetStatus so crash logs include the last N status messages.
        /// Thread-safe.
        /// </summary>
        internal static void PushStatusMessage(string text)
        {
            lock (_statusRing)
            {
                _statusRing.Enqueue($"[{DateTime.Now:HH:mm:ss.fff}] {text}");
                while (_statusRing.Count > StatusBufferSize)
                    _statusRing.Dequeue();
            }
        }

        // ── Log capture ──────────────────────────────────────────────────────

        /// <summary>
        /// Writes a structured crash log to LogDir and returns the path.
        /// Safe to call from any thread; best-effort on I/O failure.
        /// </summary>
        internal static string Capture(Exception ex, string context)
        {
            try { Directory.CreateDirectory(LogDir); } catch { /* best-effort */ }

            var sb  = new StringBuilder();
            var ver = Assembly.GetExecutingAssembly().GetName().Version;

            sb.AppendLine($"KillerPDF v{ver?.ToString(3)} crash report");
            sb.AppendLine($"Time    : {DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}");
            sb.AppendLine($"OS      : {Environment.OSVersion}");
            sb.AppendLine($"CLR     : {Environment.Version}");
            sb.AppendLine($"Context : {context}");
            sb.AppendLine();

            var inner = ex;
            var depth = 0;
            while (inner != null && depth < 5)
            {
                if (depth > 0) { sb.AppendLine(); sb.AppendLine("=== Inner Exception ==="); }
                sb.AppendLine($"Type    : {inner.GetType().FullName}");
                sb.AppendLine($"Message : {inner.Message}");
                sb.AppendLine("Stack trace:");
                sb.AppendLine(inner.StackTrace ?? "(no stack trace)");
                inner = inner.InnerException;
                depth++;
            }

            sb.AppendLine();
            sb.AppendLine("=== Last Status Messages ===");
            lock (_statusRing)
            {
                if (_statusRing.Count == 0)
                    sb.AppendLine("(none)");
                else
                    foreach (var msg in _statusRing)
                        sb.AppendLine(msg);
            }

            string logPath = Path.Combine(LogDir,
                $"crash_{DateTime.Now:yyyyMMdd_HHmmss}_{ex.GetType().Name}.log");

            try
            {
                File.WriteAllText(logPath, sb.ToString());
                LastLogPath = logPath;
                RotateLogs();
            }
            catch { /* best-effort */ }

            return logPath;
        }

        // ── Log rotation ─────────────────────────────────────────────────────

        private static void RotateLogs()
        {
            try
            {
                var files = new DirectoryInfo(LogDir).GetFiles("crash_*.log");
                long total = 0;
                foreach (var f in files) total += f.Length;
                if (total <= LogDirCapBytes) return;

                // Delete oldest first until we're at half-cap
                Array.Sort(files, (a, b) => a.LastWriteTime.CompareTo(b.LastWriteTime));
                foreach (var f in files)
                {
                    if (total <= LogDirCapBytes / 2) break;
                    try { total -= f.Length; f.Delete(); } catch { }
                }
            }
            catch { /* best-effort */ }
        }
    }
}
