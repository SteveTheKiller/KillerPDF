using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace KillerPDF.Services
{
    /// <summary>
    /// The changelog notes for one version, read from the CHANGELOG.md the app embeds with the
    /// logical name "CHANGELOG.md". The What's new card (Shell/WhatsNew.cs) shows them once after an
    /// update. The text stays English like the changelog itself; only the section headings are
    /// translated, through <see cref="HeadingKey"/>.
    ///
    /// The parser reads the house shape: "## [x.y.z] - date", an optional summary paragraph, then
    /// "### Heading" sections of "- " bullets whose continuation lines are indented. Links keep
    /// their text and emphasis marks are dropped, as KillerMCP's update window does.
    /// </summary>
    internal sealed class ReleaseNotes
    {
        internal sealed class Section
        {
            internal Section(string heading, IReadOnlyList<string> items) { Heading = heading; Items = items; }

            /// <summary>The heading as the changelog writes it, such as "Fixed".</summary>
            internal string Heading { get; }

            internal IReadOnlyList<string> Items { get; }
        }

        internal const string ResourceName = "CHANGELOG.md";

        private static readonly Regex VersionHeading = new Regex(@"^##\s*\[?v?(?<v>[0-9][0-9A-Za-z.+-]*)\]?", RegexOptions.Compiled);
        private static readonly Regex Link = new Regex(@"\[([^\]]+)\]\([^)]*\)", RegexOptions.Compiled);
        private static readonly Regex Emphasis = new Regex(@"(\*\*|__|`)", RegexOptions.Compiled);

        // The headings the family translates; any other heading shows as the changelog writes it.
        private static readonly string[] Known = { "Security", "Fixed", "Changed", "Added", "Removed", "Deprecated" };

        private ReleaseNotes(string version, string summary, IReadOnlyList<Section> sections)
        {
            Version = version;
            Summary = summary;
            Sections = sections;
        }

        internal string Version { get; }
        internal string Summary { get; }
        internal IReadOnlyList<Section> Sections { get; }

        /// <summary>Reads the embedded changelog's section for <paramref name="version"/>, or null
        /// when the app embeds no changelog or the version has no notes.</summary>
        internal static ReleaseNotes? LoadEmbedded(Assembly assembly, string version)
        {
            try
            {
                using Stream? s = assembly.GetManifestResourceStream(ResourceName);
                if (s == null) return null;
                using var r = new StreamReader(s, Encoding.UTF8);
                return FromChangelog(r.ReadToEnd(), version);
            }
            catch { return null; }
        }

        /// <summary>Finds the "## [version]" section of a changelog and parses it. Null when there is
        /// no such section or it holds nothing to show.</summary>
        internal static ReleaseNotes? FromChangelog(string markdown, string version)
        {
            string want = Normalize(version);
            if (want.Length == 0) return null;
            var body = new StringBuilder();
            bool inside = false;
            foreach (string raw in markdown.Replace("\r", string.Empty).Split('\n'))
            {
                Match m = VersionHeading.Match(raw);
                if (m.Success)
                {
                    if (inside) break;
                    inside = Normalize(m.Groups["v"].Value) == want;
                    continue;
                }
                // Any other level-two heading ("## Previously released as KillerFind") ends it too.
                if (inside && raw.StartsWith("## ", StringComparison.Ordinal)) break;
                // A link reference block ("[1.9.5]: https://...") ends the last version's notes.
                if (inside && raw.StartsWith("[", StringComparison.Ordinal) && raw.Contains("]:")) break;
                if (inside) body.Append(raw).Append('\n');
            }
            if (!inside) return null;
            ReleaseNotes notes = Parse(want, body.ToString());
            return notes.Summary.Length == 0 && notes.Sections.Count == 0 ? null : notes;
        }

        /// <summary>Parses one version's body: a summary paragraph, then ### sections of bullets in
        /// the order the changelog lists them.</summary>
        internal static ReleaseNotes Parse(string version, string body)
        {
            var summary = new StringBuilder();
            var sections = new List<Section>();
            string? heading = null;
            List<string>? items = null;
            foreach (string raw in body.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("#", StringComparison.Ordinal))
                {
                    if (items is { Count: > 0 }) sections.Add(new Section(heading ?? string.Empty, items));
                    heading = Clean(line.TrimStart('#'));
                    items = new List<string>();
                }
                else if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal))
                {
                    items ??= new List<string>();
                    items.Add(Clean(line.Substring(2)));
                }
                else if (items is { Count: > 0 } && (raw.StartsWith(" ", StringComparison.Ordinal) || raw.StartsWith("\t", StringComparison.Ordinal)))
                {
                    items[items.Count - 1] += " " + Clean(line);
                }
                else if (items == null)
                {
                    if (summary.Length > 0) summary.Append(' ');
                    summary.Append(Clean(line));
                }
            }
            if (items is { Count: > 0 }) sections.Add(new Section(heading ?? string.Empty, items));
            // The changelog's own order is the family order (Security, Fixed, Changed, Added).
            return new ReleaseNotes(version, summary.ToString(), sections);
        }

        /// <summary>The string key for a known heading ("Str_WhatsNew_Fixed"), or null for a heading
        /// the family does not use, which then shows as the changelog writes it.</summary>
        internal static string? HeadingKey(string heading)
        {
            int i = Array.FindIndex(Known, h => string.Equals(h, heading.Trim(), StringComparison.OrdinalIgnoreCase));
            return i < 0 ? null : "Str_WhatsNew_" + Known[i];
        }

        /// <summary>"v1.9.6-pre+abc123" becomes "1.9.6"; a two-part version gains ".0". Empty when
        /// the text holds no version.</summary>
        internal static string Normalize(string? version)
        {
            if (string.IsNullOrWhiteSpace(version)) return string.Empty;
            string v = version!.Trim().TrimStart('v', 'V');
            int cut = v.IndexOfAny(new[] { '+', '-', ' ' });
            if (cut >= 0) v = v.Substring(0, cut);
            if (!System.Version.TryParse(v.Contains(".") ? v : v + ".0", out Version? parsed)) return string.Empty;
            return parsed.Major + "." + parsed.Minor + "." + Math.Max(parsed.Build, 0);
        }

        /// <summary>True when <paramref name="current"/> is a later version than <paramref name="seen"/>.
        /// An unreadable seen value counts as older, so a damaged setting shows the card once.</summary>
        internal static bool IsNewer(string current, string? seen)
        {
            if (!System.Version.TryParse(Normalize(current), out Version? cur)) return false;
            if (!System.Version.TryParse(Normalize(seen), out Version? old)) return true;
            return cur > old;
        }

        private static string Clean(string text) => Emphasis.Replace(Link.Replace(text, "$1"), string.Empty).Trim();
    }
}
