using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Markup;

namespace TDPdf.Diagnostics
{
    public sealed class CrashReport
    {
        public CrashReport(Exception exception, string source, string? logPath, string logDirectory, bool recoverable, string summary, string details)
        {
            Exception = exception;
            Source = source;
            LogPath = logPath;
            LogDirectory = logDirectory;
            Recoverable = recoverable;
            Summary = summary;
            Details = details;
        }

        public Exception Exception { get; }
        public string Source { get; }
        public string? LogPath { get; }
        public string LogDirectory { get; }
        public bool Recoverable { get; }
        public string Summary { get; }
        public string Details { get; }
    }

    public static class CrashReporter
    {
        private const long MaxLogDirectoryBytes = 20L * 1024L * 1024L;
        private static int _writing;

        // TODO(#26): Feed this from MainWindow.SetStatus(...) after the MVVM refactor in #18/#21 lands.
        public static StatusRingBuffer StatusMessages { get; } = new StatusRingBuffer(50);

        public static string LogDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TDPdf",
            "Logs");

        public static CrashReport Report(Exception exception, string source)
        {
            if (Interlocked.Exchange(ref _writing, 1) != 0)
            {
                string reentrantSummary = $"{source}: {exception.GetType().FullName}: crash reporter already active";
                return new CrashReport(exception, source, null, LogDirectory, false, reentrantSummary, reentrantSummary);
            }

            bool recoverable = false;
            string summary = "Crash report unavailable.";
            string details = "Crash report unavailable.";
            string? logPath = null;

            try
            {
                recoverable = IsRecoverable(exception);
                summary = BuildSummary(exception, source, recoverable);
                details = BuildDetails(exception, source, recoverable);

                Directory.CreateDirectory(LogDirectory);
                logPath = Path.Combine(LogDirectory, $"crash-{DateTime.Now:yyyyMMdd-HHmmss-fff}.log");
                File.WriteAllText(logPath, details, Encoding.UTF8);
                TrimLogDirectory();
            }
            catch
            {
                logPath = null;
            }
            finally
            {
                Interlocked.Exchange(ref _writing, 0);
            }

            // Best-effort sanitized telemetry. No-op unless the admin
            // has provisioned Application Insights via TelemetryStore.
            Telemetry.TrackCrash(exception, source, recoverable);

            return new CrashReport(exception, source, logPath, LogDirectory, recoverable, summary, details);
        }

        public static bool IsRecoverable(Exception exception)
        {
            if (ContainsFatalException(exception) || ContainsNativePdfStack(exception))
                return false;

            if (ContainsRecoverableException(exception))
                return true;

            return false;
        }

        private static bool ContainsFatalException(Exception exception)
        {
            for (Exception? current = exception; current is not null; current = current.InnerException)
            {
                if (current is OutOfMemoryException || current is AccessViolationException || current is StackOverflowException)
                    return true;
            }

            return false;
        }

        private static bool ContainsRecoverableException(Exception exception)
        {
            for (Exception? current = exception; current is not null; current = current.InnerException)
            {
                if (current is IOException ||
                    current is UnauthorizedAccessException ||
                    current is ArgumentException ||
                    current is XamlParseException ||
                    current is InvalidOperationException)
                    return true;
            }

            return false;
        }

        private static bool ContainsNativePdfStack(Exception exception)
        {
            for (Exception? current = exception; current is not null; current = current.InnerException)
            {
                string stack = current.StackTrace ?? string.Empty;
                if (stack.IndexOf("Docnet.", StringComparison.Ordinal) >= 0 ||
                    stack.IndexOf("PDFium", StringComparison.Ordinal) >= 0)
                    return true;
            }

            return false;
        }

        private static string BuildSummary(Exception exception, string source, bool recoverable)
        {
            return $"{source}: {exception.GetType().FullName}: {Sanitize(exception.Message)} ({(recoverable ? "recoverable" : "fatal")})";
        }

        private static string BuildDetails(Exception exception, string source, bool recoverable)
        {
            var sb = new StringBuilder();
            var assembly = Assembly.GetExecutingAssembly().GetName();

            sb.AppendLine("TDPdf crash report");
            sb.AppendLine($"Timestamp: {DateTimeOffset.Now:O}");
            sb.AppendLine($"Source: {source}");
            sb.AppendLine($"Recoverable: {(recoverable ? "yes" : "no")}");
            sb.AppendLine($"App version: {assembly.Version}");
            sb.AppendLine($"OS version: {Environment.OSVersion}");
            sb.AppendLine($"CLR version: {Environment.Version}");
            sb.AppendLine($"Working set: {Environment.WorkingSet} bytes");
            AppendDocumentState(sb);
            AppendStatusMessages(sb);
            sb.AppendLine();
            sb.AppendLine("Exception chain:");
            AppendExceptionChain(sb, exception);

            return sb.ToString();
        }

        private static void AppendDocumentState(StringBuilder sb)
        {
            bool documentOpen = false;
            bool dirty = false;

            try
            {
                Window? mainWindow = Application.Current?.MainWindow;
                if (mainWindow is not null)
                {
                    Type type = mainWindow.GetType();
                    const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                    object? doc = type.GetField("_doc", flags)?.GetValue(mainWindow);
                    object? dirtyValue = type.GetField("_isDirty", flags)?.GetValue(mainWindow);

                    documentOpen = doc is not null;
                    dirty = dirtyValue is bool b && b;
                }
            }
            catch
            {
                sb.AppendLine("Document state: unavailable");
                return;
            }

            sb.AppendLine($"Document state: (document open: {(documentOpen ? "yes" : "no")}, dirty: {(dirty ? "yes" : "no")})");
        }

        private static void AppendStatusMessages(StringBuilder sb)
        {
            string[] statuses;

            try
            {
                statuses = StatusMessages.Snapshot();
            }
            catch
            {
                statuses = Array.Empty<string>();
            }

            sb.AppendLine("Recent status messages:");
            if (statuses.Length == 0)
            {
                sb.AppendLine("  (none captured)");
                return;
            }

            foreach (string status in statuses)
                sb.AppendLine("  " + Sanitize(status));
        }

        private static void AppendExceptionChain(StringBuilder sb, Exception exception)
        {
            int depth = 0;
            for (Exception? current = exception; current is not null; current = current.InnerException)
            {
                sb.AppendLine();
                sb.AppendLine($"[{depth}] {current.GetType().FullName}");
                sb.AppendLine(Sanitize(current.Message));
                sb.AppendLine(Sanitize(current.StackTrace ?? "(no stack trace)"));
                depth++;
            }
        }

        private static string Sanitize(string value) => Sanitizer.Scrub(value);

        private static void TrimLogDirectory()
        {
            try
            {
                var directory = new DirectoryInfo(LogDirectory);
                if (!directory.Exists)
                    return;

                FileInfo[] files = directory.GetFiles("crash-*.log")
                    .OrderBy(file => file.CreationTimeUtc)
                    .ToArray();
                long totalBytes = files.Sum(file => SafeLength(file));

                foreach (FileInfo file in files)
                {
                    if (totalBytes <= MaxLogDirectoryBytes)
                        break;

                    long length = SafeLength(file);
                    try
                    {
                        file.Delete();
                        totalBytes -= length;
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
        }

        private static long SafeLength(FileInfo file)
        {
            try { return file.Length; }
            catch { return 0; }
        }
    }
}
