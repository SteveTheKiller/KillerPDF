using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Media;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPDF.Services;

namespace KillerPDF.Features
{
    // The Redact tool from the command line (#341): find words, patterns or sensitive numbers,
    // mark whole pages or apply the marks saved in the file, then permanently remove everything
    // under them with the same verified engine pass, and optionally strip hidden information.
    internal static partial class CliRunner
    {
        private const string RedactUsage =
            "Usage: KillerPDF.exe --redact <in.pdf> <out.pdf> [what] [how] [--dry-run]\n"
            + "  what (any mix): --text <phrase>  --terms <file with one phrase per line>  --regex <pattern>\n"
            + "                  --find email,phone,ssn,card,iban  --whole-pages <range>  --marked (marks saved in the file)\n"
            + "                  [--match-case]\n"
            + "  how:  [--fill <#RRGGBB or color name>] [--overlay <text>] [--rasterize]\n"
            + "        [--sanitize all|comments,attachments,forms,layers,scripts,links,bookmarks]\n"
            + "  --rasterize lets pages whose marked content cannot be removed on its own become images;\n"
            + "  without it the command fails without saving output and lists the affected pages.\n"
            + "  A search that could not read a page's text, form fields or comments exits 3 without saving\n"
            + "  output and lists those pages, because a term there might be missed.\n"
            + "  --dry-run lists what would be redacted.";

        // ============================================================
        // --redact <in.pdf> <out.pdf> ...
        // ============================================================
        private static int CliRedact(List<string> pos, Dictionary<string, string> options, TextWriter con)
        {
            if (pos.Count != 2) { con.WriteLine(RedactUsage); return 2; }
            if (!CliInspectEditInput(pos[0], pos[1], out string source, out string output, out int pageCount, con)) return 2;

            // What to look for, each phrase or pattern searched on its own.
            var searches = new List<(PdfRedactionSearchKind Kind, string Query)>();
            if (options.TryGetValue("--text", out string? text) && text.Trim().Length > 0)
                searches.Add((PdfRedactionSearchKind.ExactText, text.Trim()));
            if (options.TryGetValue("--terms", out string? termsPath))
            {
                string full = Path.GetFullPath(termsPath);
                if (!File.Exists(full)) { con.WriteLine($"Terms file not found: {full}"); return 2; }
                searches.AddRange(File.ReadAllLines(full).Select(line => line.Trim()).Where(line => line.Length > 0)
                    .Distinct(StringComparer.Ordinal).Select(line => (PdfRedactionSearchKind.ExactText, line)));
            }
            if (options.TryGetValue("--regex", out string? regex) && regex.Length > 0)
                searches.Add((PdfRedactionSearchKind.RegularExpression, regex));
            if (options.TryGetValue("--find", out string? findRaw))
            {
                foreach (string kind in findRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    PdfRedactionSearchKind? parsed = kind.ToLowerInvariant() switch
                    {
                        "email" => PdfRedactionSearchKind.EmailAddress,
                        "phone" => PdfRedactionSearchKind.PhoneNumber,
                        "ssn" => PdfRedactionSearchKind.SocialSecurityNumber,
                        "card" => PdfRedactionSearchKind.PaymentCardNumber,
                        "iban" => PdfRedactionSearchKind.InternationalBankAccountNumber,
                        _ => null,
                    };
                    if (parsed is null) { con.WriteLine($"--find takes email, phone, ssn, card or iban, not {kind}."); return 2; }
                    searches.Add((parsed.Value, string.Empty));
                }
            }
            List<int> wholePages = [];
            if (options.TryGetValue("--whole-pages", out string? wholeRaw))
            {
                List<int>? parsed = CliParsePageRange(wholeRaw, pageCount, out string error);
                if (parsed is null) { con.WriteLine(error); return 2; }
                wholePages = parsed;
            }
            bool marked = options.ContainsKey("--marked");
            if (searches.Count == 0 && wholePages.Count == 0 && !marked)
            {
                con.WriteLine("Nothing to redact: name --text, --terms, --regex, --find, --whole-pages or --marked.");
                con.WriteLine(RedactUsage);
                return 2;
            }

            PdfRgbColor? fill = null;
            if (options.TryGetValue("--fill", out string? fillRaw))
            {
                object? parsed = null;
                try { parsed = ColorConverter.ConvertFromString(fillRaw.StartsWith('#') || !fillRaw.All(char.IsAsciiHexDigit) ? fillRaw : "#" + fillRaw); }
                catch (Exception ex) when (ex is FormatException or NotSupportedException) { }
                if (parsed is not Color color) { con.WriteLine("--fill must be a color such as #000000 or white."); return 2; }
                fill = new PdfRgbColor(color.R / 255.0, color.G / 255.0, color.B / 255.0);
            }
            options.TryGetValue("--overlay", out string? overlay);
            overlay = string.IsNullOrWhiteSpace(overlay) ? null : overlay.Trim();
            if (overlay is not null && !PdfContentRedaction.SupportsOverlayText(overlay))
            {
                con.WriteLine("The overlay text uses characters a redaction label cannot draw; use plain letters, numbers and punctuation.");
                return 2;
            }
            PdfEngineIntegration.SanitizeChoices? sanitize = null;
            if (options.TryGetValue("--sanitize", out string? sanitizeRaw))
            {
                if (CliSanitizeChoices(sanitizeRaw) is not { } choices)
                {
                    con.WriteLine("--sanitize takes all, or any of comments, attachments, forms, layers, scripts, links, bookmarks.");
                    return 2;
                }
                sanitize = choices;
            }

            // The areas, exactly as the Redact tool marks them: each match letter by letter on
            // each line it covers, whole pages edge to edge, and the marks saved in the file.
            var areas = new List<(int Page, PdfContentBounds Bounds, string Label)>();
            // Form field values, annotation text and the saved marks are not page content; they all
            // come from one engine document, parsed once and only when something asks for it.
            var document = new Lazy<PdfDocument>(() => PdfEngineIntegration.OpenForReading(File.ReadAllBytes(source)));
            var unreadable = new SortedSet<int>();
            if (searches.Count > 0)
            {
                (IReadOnlyList<PdfPageContent?> pages, IReadOnlyList<int> unreadablePages) = SearchService.LoadPagesForReview(source, null);
                unreadable.UnionWith(unreadablePages);
                var found = new Dictionary<string, PdfRedactionMatch>();
                try
                {
                    foreach (var (kind, query) in searches)
                    {
                        var search = new PdfRedactionSearchOptions
                        {
                            Kind = kind,
                            Query = query,
                            MatchCase = options.ContainsKey("--match-case"),
                            Timeout = TimeSpan.FromSeconds(2)
                        };
                        foreach (PdfRedactionMatch match in PdfRedactionSearch.Find(pages, search).Matches)
                            found.TryAdd(match.Id, match);
                        PdfRedactionReview annotations = PdfRedactionSearch.FindInAnnotations(document.Value, search);
                        foreach (PdfRedactionMatch match in annotations.Matches)
                            found.TryAdd(match.Id, match);
                        unreadable.UnionWith(annotations.UnreadableAnnotationPages);
                    }
                }
                catch (Exception ex) when (ex is ArgumentException or System.Text.RegularExpressions.RegexMatchTimeoutException)
                {
                    con.WriteLine("Could not search the document: " + ex.Message);
                    return 2;
                }
                foreach (PdfRedactionMatch match in found.Values.OrderBy(match => match.PageIndex)
                    .ThenByDescending(match => match.Bounds.Top).ThenBy(match => match.Bounds.Left))
                    foreach (PdfContentBounds area in match.Areas.Count > 0 ? match.Areas : [match.Bounds])
                        areas.Add((match.PageIndex, area, match.Text));
            }
            // A page the search could not finish might hold a term, so this is never "nothing matched".
            // Nothing is saved, and a script reads 3 as partial, the same as --search-text.
            if (unreadable.Count > 0)
            {
                con.WriteLine("Redaction was not saved because the text, form fields or comments on some pages "
                    + "could not be read, so a term there might be missed.");
                con.WriteLine(JsonSerializer.Serialize(new
                {
                    path = source,
                    unreadablePages = unreadable.Select(page => page + 1).ToArray()
                }));
                return 3;
            }
            IReadOnlyList<PdfPageInformation> geometry = PdfEngineIntegration.ReadPageInformation(source);
            foreach (int page in wholePages)
                areas.Add((page, new PdfContentBounds(0, 0, geometry[page].Width, geometry[page].Height), "whole page"));
            var regions = new List<PdfRedactionRegion>();
            if (marked)
                foreach (PdfEngineIntegration.RedactionMarkData mark in PdfEngineIntegration.ReadRedactionMarks(document.Value))
                {
                    regions.Add(new PdfRedactionRegion(mark.PageIndex,
                        new PdfContentBounds(mark.Left, mark.Bottom, mark.Right, mark.Top), null, mark.OverlayText ?? overlay)
                    { FillColor = mark.FillColor ?? fill });
                    areas.Add((mark.PageIndex, new PdfContentBounds(mark.Left, mark.Bottom, mark.Right, mark.Top), "saved mark"));
                }
            regions.AddRange(areas.Where(area => area.Label != "saved mark").Distinct().Select(area =>
                new PdfRedactionRegion(area.Page, area.Bounds, null, overlay) { FillColor = fill }));

            if (options.ContainsKey("--dry-run"))
            {
                con.WriteLine($"Would redact {areas.Count} areas on {areas.Select(area => area.Page).Distinct().Count()} pages:");
                foreach (var area in areas)
                    con.WriteLine($"  Page {area.Page + 1}: {area.Label}");
                return 0;
            }
            if (regions.Count == 0)
            {
                // Nothing to black out, but a requested cleanup of hidden information still
                // happens, so a pipeline that trusts the exit code gets the document it asked for.
                CliWriteEditedCopy(source, output, path =>
                {
                    if (sanitize is not null) PdfEngineIntegration.SanitizeFile(path, sanitize);
                });
                con.WriteLine(sanitize is not null
                    ? $"Nothing matched, so nothing was redacted; hidden information was removed -> {output}"
                    : $"Nothing matched, so nothing was redacted -> {output}");
                return 0;
            }

            PdfContentRedactionPlan plan = PdfEngineIntegration.AnalyzeRedaction(
                PdfEngineIntegration.OpenForReading(File.ReadAllBytes(source)), regions);
            List<int> approved = options.ContainsKey("--rasterize") ? [.. plan.RasterPages] : [];
            HashSet<int> skipped = [.. plan.RasterPages.Except(approved)];
            if (skipped.Count > 0)
            {
                con.WriteLine($"Redaction was not saved. Pages {string.Join(", ", skipped.Order().Select(page => page + 1))} "
                    + "require rasterization; add --rasterize to allow that.");
                return 1;
            }

            PdfContentRedactionResult? result = null;
            try
            {
                CliWriteEditedCopy(source, output, path =>
                {
                    result = PdfEngineIntegration.ApplyRedaction(path, regions, approved);
                    if (sanitize is not null) PdfEngineIntegration.SanitizeFile(path, sanitize);
                });
            }
            catch (PdfRedactionVerificationException ex)
            {
                con.WriteLine("The redaction was not saved because verification found content still exposed:");
                con.WriteLine(ex.Report.ToText());
                return 1;
            }
            int pagesTouched = regions.Select(region => region.PageIndex).Distinct().Count();
            con.WriteLine($"Redacted {regions.Count} areas on {pagesTouched} pages"
                + (approved.Count > 0 ? $", turning {approved.Count} pages into images" : string.Empty)
                + (sanitize is not null ? ", and removed hidden information" : string.Empty)
                + $". Verification {(result?.Verification.Succeeded == true ? "passed" : "reported findings")}. -> {output}");
            return 0;
        }

        // The hidden information Finish Redaction offers to remove, by name; null when a name is unknown.
        private static PdfEngineIntegration.SanitizeChoices? CliSanitizeChoices(string value)
        {
            string[] names = [.. value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(name => name.ToLowerInvariant())];
            string[] known = ["comments", "attachments", "forms", "layers", "scripts", "links", "bookmarks"];
            if (names.Length == 1 && names[0] == "all") names = known;
            if (names.Length == 0 || names.Any(name => !known.Contains(name))) return null;
            return new PdfEngineIntegration.SanitizeChoices(names.Contains("comments"), names.Contains("attachments"),
                names.Contains("forms"), names.Contains("layers"), names.Contains("scripts"), names.Contains("links"),
                names.Contains("bookmarks"));
        }
    }
}
