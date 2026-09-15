using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace KillerPDF.Services;

internal static partial class ReleaseUpdateCheck
{
    internal const string Setting = "CheckForUpdatesOnStartup";
    internal static bool IsEnabled(string? setting) => setting != "0";

    internal static async Task<string?> FindNewerReleaseAsync(HttpClient http, Version current)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                "https://api.github.com/repos/SteveTheKiller/KillerPDF/releases/latest");
            request.Headers.UserAgent.ParseAdd("KillerPDF-UpdateCheck");
            using var response = await http.SendAsync(request).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return ParseNewerRelease(await response.Content.ReadAsStringAsync().ConfigureAwait(false), current);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    internal static string? ParseNewerRelease(string json, Version current)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || root.TryGetProperty("draft", out var draft) && draft.ValueKind != JsonValueKind.False
            || root.TryGetProperty("prerelease", out var prerelease) && prerelease.ValueKind != JsonValueKind.False
            || !root.TryGetProperty("tag_name", out var tagElement)
            || tagElement.ValueKind != JsonValueKind.String)
            return null;
        string? tag = tagElement.GetString();
        if (tag is null || !StableTag().IsMatch(tag)
            || !Version.TryParse(tag[1..], out var latest))
            return null;
        var installed = new Version(current.Major, current.Minor, Math.Max(0, current.Build));
        return latest > installed ? tag : null;
    }

[GeneratedRegex(@"\Av[0-9]+\.[0-9]+\.[0-9]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex StableTag();
}
