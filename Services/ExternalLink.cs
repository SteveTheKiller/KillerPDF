namespace KillerPDF.Services;

/// <summary>Decides which PDF link targets may go to the Windows shell, and what text is launched.</summary>
internal static class ExternalLink
{
    // A PDF can embed ANY URI, and Process.Start(UseShellExecute=true) would happily launch file:// paths,
    // UNC shares, javascript:, or registered protocol handlers (ms-msdt:/search-ms: are real malware
    // vectors). Anything outside this allow-list is refused.
    private static readonly HashSet<string> AllowedSchemes =
        new(StringComparer.OrdinalIgnoreCase) { "http", "https", "mailto" };

    /// <summary>True only for an absolute URI in an allowed scheme.</summary>
    internal static bool IsAllowed(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && AllowedSchemes.Contains(uri.Scheme);

    /// <summary>
    /// Returns the exact text to hand to the shell for a PDF link target, or null when the target is refused.
    /// </summary>
    internal static string? LaunchTarget(string raw)
    {
        // Launch the parsed form, not the raw string, so the shell gets exactly what was checked.
        return Uri.TryCreate(Normalize(raw), UriKind.Absolute, out var uri) && AllowedSchemes.Contains(uri.Scheme)
            ? uri.AbsoluteUri
            : null;
    }

    // A PDF can store a scheme-less link like "www.example.com" or "example.com/page". Treat a domain-
    // shaped target as https so it still opens; anything with an explicit scheme, a backslash (UNC/path),
    // or whitespace is left untouched (and thus refused unless it's http/https/mailto).
    internal static string Normalize(string raw)
    {
        raw = raw.Trim();
        if (raw.Length == 0) return raw;
        if (raw.Contains('\\') || raw.Contains(' ')) return raw;    // Windows path / UNC / junk - don't touch
        if (raw.Contains("://")) return raw;                        // already scheme://...
        int colon = raw.IndexOf(':');
        int slash = raw.IndexOf('/');
        if (colon >= 0 && (slash < 0 || colon < slash)) return raw; // "scheme:" (mailto:, file:, C:) - don't touch
        string host = slash >= 0 ? raw[..slash] : raw;              // host part before any path
        return host.Contains('.') ? "https://" + raw : raw;         // dotted host => assume https
    }
}
