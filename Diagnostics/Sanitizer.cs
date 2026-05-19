using System;
using System.Text.RegularExpressions;

namespace TDPdf.Diagnostics
{
    /// <summary>
    /// Best-effort PII scrubbing for any string that may be written to a
    /// crash log or shipped off-device as telemetry. Shared by
    /// <see cref="CrashReporter"/> and <see cref="Telemetry"/>.
    ///
    /// This is not a security boundary. It is a pragmatic last line of
    /// defense against accidentally serializing user document paths,
    /// usernames, or PDF passwords into telemetry. New scrub rules
    /// should be additive; never remove an existing redaction without a
    /// clear reason.
    /// </summary>
    internal static class Sanitizer
    {
        public static string Scrub(string? value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            string sanitized = value!;

            // PDF passwords surfaced in exception messages and our own
            // status text (Open / Decrypt paths).
            sanitized = Regex.Replace(sanitized, @"(?i)(password\s*[:=]\s*)\S+", "$1[redacted]");
            sanitized = Regex.Replace(sanitized, @"(?i)(passphrase\s*[:=]\s*)\S+", "$1[redacted]");

            // App Insights connection strings — defensive, in case someone
            // ever logs args by mistake.
            sanitized = Regex.Replace(sanitized,
                @"(?i)(InstrumentationKey|IngestionEndpoint|ConnectionString)\s*=\s*[^;\s""]+",
                "$1=[redacted]");

            // Windows paths (UNC, drive-rooted, and bare backslash chains).
            sanitized = Regex.Replace(sanitized, @"\\[^\s:;]+(?:\\[^\s:;]+)+", "[path redacted]");
            sanitized = Regex.Replace(sanitized, @"[A-Za-z]:\\[^\r\n:;]+", "[path redacted]");

            // .NET portable stack-trace "in /foo/bar.cs:line 42" frames.
            sanitized = Regex.Replace(sanitized, @"(?m) in /[^\r\n]+:line \d+", " in [path redacted]");

            // Bare POSIX-style paths that show up in some cross-compiled
            // dependencies (Docnet/PDFium debug strings).
            sanitized = Regex.Replace(sanitized, @"(?<!/)/[^\s\0]+", "[path redacted]");

            return sanitized;
        }

        /// <summary>
        /// Compute a stable, low-entropy grouping key for an exception
        /// suitable for binning crashes in telemetry without revealing
        /// the underlying message text. Returns a short hex string.
        /// </summary>
        public static string GroupingKey(Exception exception)
        {
            try
            {
                string type = exception.GetType().FullName ?? "Unknown";
                string firstFrame = (exception.StackTrace ?? string.Empty)
                    .Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault() ?? string.Empty;

                // First frame already typically contains method names but
                // not user data; scrub paths defensively anyway.
                firstFrame = Scrub(firstFrame);

                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(type + "|" + firstFrame);
                byte[] hash = System.Security.Cryptography.SHA256.HashData(bytes);
                return Convert.ToHexString(hash, 0, 6); // 12 hex chars
            }
            catch
            {
                return "unknown";
            }
        }
    }
}
