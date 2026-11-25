namespace KillerPDF.Services
{
    /// <summary>
    /// The hosts a user chose to always open browser PDFs from, kept as one setting value of
    /// semicolon-separated lowercase ASCII host names.
    /// </summary>
    internal static class BrowserHostTrust
    {
        internal const string Setting = "TrustedBrowserHosts";

        /// <summary>
        /// The host as the user sees it in the question. Punycode is kept as is, so a look-alike
        /// international name cannot pass for a familiar one.
        /// </summary>
        internal static string DisplayHost(Uri target) => target.IdnHost.ToLowerInvariant();

        internal static bool IsTrusted(string? saved, Uri target) =>
            Hosts(saved).Contains(DisplayHost(target), StringComparer.Ordinal);

        /// <summary>Returns the setting value with the target's host added.</summary>
        internal static string Add(string? saved, Uri target)
        {
            List<string> hosts = Hosts(saved);
            string host = DisplayHost(target);
            if (!hosts.Contains(host, StringComparer.Ordinal)) hosts.Add(host);
            return string.Join(';', hosts);
        }

        private static List<string> Hosts(string? saved) =>
            [.. (saved ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(host => host.ToLowerInvariant()).Distinct(StringComparer.Ordinal)];
    }
}
