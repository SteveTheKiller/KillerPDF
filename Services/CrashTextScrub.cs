using System.Text.RegularExpressions;

namespace KillerPDF.Services;

/// <summary>
/// Takes file paths out of crash text before it goes into a public GitHub issue link. A message
/// such as "Could not find C:\Users\jose\Clients\Smith divorce.pdf" names the user and the
/// document; only the last part of each path stays, which is usually what a bug report needs.
/// </summary>
internal static partial class CrashTextScrub
{
    [GeneratedRegex(@"(?:[A-Za-z]:\\|\\\\[^\\\s]+\\)[^\r\n""'`<>|*?]*", RegexOptions.CultureInvariant)]
    private static partial Regex WindowsPath();

    internal static string Paths(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        return WindowsPath().Replace(text, match =>
        {
            string path = match.Value.TrimEnd(' ', '.', ',', ';', ':', ')');
            string trailing = match.Value[path.Length..];
            int slash = path.TrimEnd('\\').LastIndexOf('\\');
            string last = slash < 0 ? "" : path.TrimEnd('\\')[(slash + 1)..];
            return "<path>\\" + last + trailing;
        });
    }
}
