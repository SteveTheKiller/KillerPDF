using System.Globalization;
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

        internal static string LogDir { get; } = App.IsStartupSmoke ? Path.Combine(App.SmokeDataRoot, "Logs") : Path.Combine(
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
                _statusRing.Enqueue(string.Create(CultureInfo.InvariantCulture, $"[{DateTime.Now:HH:mm:ss.fff}] {text}"));
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
            sb.AppendLine($"KillerPDF v{AppVersion.Display} crash report");
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"Time    : {DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}"));
            sb.AppendLine($"OS      : {Environment.OSVersion}");
            sb.AppendLine($"CLR     : {Environment.Version}");
            sb.AppendLine($"Context : {context}");
            sb.AppendLine();

            List<Exception> failures = ex is AggregateException aggregate
                ? [.. aggregate.Flatten().InnerExceptions]
                : [ex];
            for (int failureIndex = 0; failureIndex < failures.Count; failureIndex++)
            {
                if (failureIndex > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine($"=== Aggregate Failure {failureIndex + 1} ===");
                }
                Exception? inner = failures[failureIndex];
                int depth = 0;
                while (inner is not null && depth < 5)
                {
                    if (depth > 0)
                    {
                        sb.AppendLine();
                        sb.AppendLine("=== Inner Exception ===");
                    }
                    sb.AppendLine($"Type    : {inner.GetType().FullName}");
                    sb.AppendLine($"Message : {inner.Message}");
                    if (inner is FileNotFoundException missingFile)
                    {
                        // Parameterless FileNotFoundException leaves Message empty; FileName still helps.
                        if (!string.IsNullOrEmpty(missingFile.FileName))
                            sb.AppendLine($"FileName: {missingFile.FileName}");
                        if (!string.IsNullOrEmpty(missingFile.FusionLog))
                            sb.AppendLine($"FusionLog: {missingFile.FusionLog}");
                    }
                    sb.AppendLine("Stack trace:");
                    sb.AppendLine(inner.StackTrace ?? "(no stack trace)");
                    inner = inner.InnerException;
                    depth++;
                }
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
                string.Create(CultureInfo.InvariantCulture, $"crash_{DateTime.Now:yyyyMMdd_HHmmss}_{ex.GetType().Name}.log"));

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
