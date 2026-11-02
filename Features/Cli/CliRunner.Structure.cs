using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using KillerPDF.Services;

namespace KillerPDF.Features
{
    // Document structure edits from the command line: link cleanup, a table of contents, page
    // labels, the initial view, layers, attachments and portfolio presentation. Each writes a
    // new file through the same engine calls the Document tools make.
    internal static partial class CliRunner
    {
        // ============================================================
        // --repair-links <in.pdf> <out.pdf> [--unsafe] [--unresolved]
        // ============================================================
        // Removes links that launch programs or reach unsafe addresses, links whose destination
        // no longer exists, or both when neither is named.
        private static int CliRepairLinks(List<string> pos, Dictionary<string, string> options, TextWriter con)
        {
            if (pos.Count != 2)
            {
                con.WriteLine("Usage: KillerPDF.exe --repair-links <in.pdf> <out.pdf> [--unsafe] [--unresolved]   (both by default)");
                return 2;
            }
            if (!CliInspectEditInput(pos[0], pos[1], out string source, out string output, out _, con)) return 2;
            bool removeUnsafe = options.ContainsKey("--unsafe"), removeUnresolved = options.ContainsKey("--unresolved");
            if (!removeUnsafe && !removeUnresolved) removeUnsafe = removeUnresolved = true;
            int before = PdfEngineIntegration.InspectNavigation(source, null).Count;
            CliWriteEditedCopy(source, output, path =>
                PdfEngineIntegration.RepairNavigationLinks(path, removeUnsafe, removeUnresolved));
            int after = PdfEngineIntegration.InspectNavigation(output, null).Count;
            con.WriteLine($"Navigation findings: {before} before, {after} after -> {output}");
            return 0;
        }

        // ============================================================
        // --toc <in.pdf> <out.pdf> [--title <text>] [--depth <1-6>]
        // ============================================================
        // Inserts clickable contents pages at the front, built from the bookmarks.
        private static int CliTableOfContents(List<string> pos, Dictionary<string, string> options, TextWriter con)
        {
            if (pos.Count != 2)
            {
                con.WriteLine("Usage: KillerPDF.exe --toc <in.pdf> <out.pdf> [--title <text>] [--depth <1-6>]");
                return 2;
            }
            if (!CliInspectEditInput(pos[0], pos[1], out string source, out string output, out _, con)) return 2;
            options.TryGetValue("--title", out string? title);
            if (string.IsNullOrWhiteSpace(title)) title = "Contents";
            int depth = 6;
            if (options.TryGetValue("--depth", out string? depthRaw)
                && (!int.TryParse(depthRaw, out depth) || depth is < 1 or > 6))
            {
                con.WriteLine("--depth must be from 1 to 6.");
                return 2;
            }
            if (PdfEngineIntegration.PlanTableOfContents(source, depth, null).Entries.Count == 0)
            {
                con.WriteLine("The document has no bookmarks to build a table of contents from. Add some with --auto-bookmarks first.");
                return 1;
            }
            int inserted = 0;
            CliWriteEditedCopy(source, output, path =>
                inserted = PdfEngineIntegration.InsertTableOfContents(path, title, depth).InsertedPageCount);
            con.WriteLine($"Inserted {inserted} contents pages -> {output}");
            return 0;
        }

        // ============================================================
        // --page-labels <in.pdf> <out.pdf> <page:style[:prefix[:start]],...>
        // ============================================================
        // Each range starts at a page and runs to the next; the first must start at page 1.
        private static int CliPageLabels(List<string> pos, Dictionary<string, string> options, TextWriter con)
        {
            const string usage = "Usage: KillerPDF.exe --page-labels <in.pdf> <out.pdf> <page:style[:prefix[:start]],...>\n"
                + "  style: decimal, upper-roman, lower-roman, upper-letters, lower-letters or none\n"
                + "  example: 1:lower-roman,5:decimal,40:decimal:A-:1";
            if (pos.Count != 3) { con.WriteLine(usage); return 2; }
            if (!CliInspectEditInput(pos[0], pos[1], out string source, out string output, out int pageCount, con)) return 2;
            var ranges = new List<PdfPageLabelMacroRange>();
            foreach (string part in pos[2].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string[] fields = part.Split(':');
                if (fields.Length is < 2 or > 4 || !int.TryParse(fields[0], out int page) || page < 1 || page > pageCount
                    || CliPageLabelStyle(fields[1]) is not PdfPageLabelStyle style)
                {
                    con.WriteLine($"Could not read the range \"{part}\".");
                    con.WriteLine(usage);
                    return 2;
                }
                string? prefix = fields.Length > 2 && fields[2].Length > 0 ? fields[2] : null;
                int start = 1;
                if (fields.Length > 3 && (!int.TryParse(fields[3], out start) || start < 1))
                {
                    con.WriteLine($"The start number in \"{part}\" must be 1 or more.");
                    return 2;
                }
                ranges.Add(new PdfPageLabelMacroRange(page - 1, style, prefix, start));
            }
            ranges = [.. ranges.OrderBy(range => range.PageIndex)];
            if (ranges.Count == 0 || ranges[0].PageIndex != 0 || ranges.Select(range => range.PageIndex).Distinct().Count() != ranges.Count)
            {
                con.WriteLine("The ranges must start at page 1 and each page may start only one range.");
                return 2;
            }
            CliWriteEditedCopy(source, output, path => PdfEngineIntegration.ApplyPageLabels(path, ranges));
            con.WriteLine($"Set {ranges.Count} page-label ranges -> {output}");
            return 0;
        }

        private static PdfPageLabelStyle? CliPageLabelStyle(string value) => value.Trim().ToLowerInvariant() switch
        {
            "decimal" => PdfPageLabelStyle.Decimal,
            "upper-roman" => PdfPageLabelStyle.UpperRoman,
            "lower-roman" => PdfPageLabelStyle.LowerRoman,
            "upper-letters" => PdfPageLabelStyle.UpperLetters,
            "lower-letters" => PdfPageLabelStyle.LowerLetters,
            "none" => PdfPageLabelStyle.None,
            _ => null,
        };

        // ============================================================
        // --initial-view <in.pdf> <out.pdf> [--page <n>] [--zoom ...] [--layout ...] [--panel ...]
        //                [--hide-toolbar on|off] [--hide-menu on|off] [--hide-window-ui on|off]
        //                [--fit-window on|off] [--center-window on|off] [--show-title on|off]
        // ============================================================
        // Anything not named keeps the setting the document already has.
        private static int CliInitialView(List<string> pos, Dictionary<string, string> options, TextWriter con)
        {
            const string usage = "Usage: KillerPDF.exe --initial-view <in.pdf> <out.pdf> [--page <n>]\n"
                + "  [--zoom fit-page|fit-width|fit-height|actual|<percent>]\n"
                + "  [--layout default|single|one-column|two-column-left|two-column-right|two-page-left|two-page-right]\n"
                + "  [--panel default|none|outlines|thumbnails|layers|attachments|full-screen]\n"
                + "  [--hide-toolbar on|off] [--hide-menu on|off] [--hide-window-ui on|off]\n"
                + "  [--fit-window on|off] [--center-window on|off] [--show-title on|off]";
            if (pos.Count != 2) { con.WriteLine(usage); return 2; }
            if (!CliInspectEditInput(pos[0], pos[1], out string source, out string output, out int pageCount, con)) return 2;
            PdfInitialView current = PdfDocumentInformation.Read(
                PdfEngineIntegration.OpenForReading(File.ReadAllBytes(source))).InitialView;

            int? pageIndex = current.PageIndex;
            PdfDestination? destination = current.Destination;
            string? named = current.NamedDestination;
            bool hasPage = options.TryGetValue("--page", out string? pageRaw), hasZoom = options.TryGetValue("--zoom", out string? zoomRaw);
            if (hasPage || hasZoom)
            {
                int page = 1;
                if (hasPage && (!int.TryParse(pageRaw, out page) || page < 1 || page > pageCount))
                {
                    con.WriteLine($"--page must be from 1 to {pageCount}.");
                    return 2;
                }
                pageIndex = hasPage ? page - 1 : current.PageIndex ?? 0;
                named = null;
                destination = !hasZoom ? current.Destination ?? PdfDestination.FitPage() : zoomRaw!.ToLowerInvariant() switch
                {
                    "fit-page" => PdfDestination.FitPage(),
                    "fit-width" => PdfDestination.FitWidth(),
                    "fit-height" => PdfDestination.FitHeight(),
                    "actual" => PdfDestination.At(zoom: 1),
                    _ when double.TryParse(zoomRaw.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out double percent)
                        && percent is >= 1 and <= 6400 => PdfDestination.At(zoom: percent / 100.0),
                    _ => null,
                };
                if (destination is null) { con.WriteLine("--zoom must be fit-page, fit-width, fit-height, actual or a percentage."); return 2; }
            }

            PdfPageLayout? layout = current.PageLayout;
            if (options.TryGetValue("--layout", out string? layoutRaw))
            {
                if (!CliChoice(layoutRaw, out layout, ("default", null), ("single", PdfPageLayout.SinglePage),
                        ("one-column", PdfPageLayout.OneColumn), ("two-column-left", PdfPageLayout.TwoColumnLeft),
                        ("two-column-right", PdfPageLayout.TwoColumnRight), ("two-page-left", PdfPageLayout.TwoPageLeft),
                        ("two-page-right", PdfPageLayout.TwoPageRight)))
                { con.WriteLine(usage); return 2; }
            }
            PdfPageMode? panel = current.PageMode;
            if (options.TryGetValue("--panel", out string? panelRaw))
            {
                if (!CliChoice(panelRaw, out panel, ("default", null), ("none", PdfPageMode.UseNone),
                        ("outlines", PdfPageMode.UseOutlines), ("thumbnails", PdfPageMode.UseThumbs),
                        ("layers", PdfPageMode.UseOptionalContent), ("attachments", PdfPageMode.UseAttachments),
                        ("full-screen", PdfPageMode.FullScreen)))
                { con.WriteLine(usage); return 2; }
            }

            PdfViewerPreferences preferences = current.ViewerPreferences;
            foreach (var (option, apply) in new (string, Func<PdfViewerPreferences, bool, PdfViewerPreferences>)[]
            {
                ("--hide-toolbar", (p, on) => p with { HideToolbar = on }),
                ("--hide-menu", (p, on) => p with { HideMenuBar = on }),
                ("--hide-window-ui", (p, on) => p with { HideWindowUi = on }),
                ("--fit-window", (p, on) => p with { FitWindow = on }),
                ("--center-window", (p, on) => p with { CenterWindow = on }),
                ("--show-title", (p, on) => p with { DisplayDocumentTitle = on }),
            })
            {
                if (!options.TryGetValue(option, out string? value)) continue;
                if (value is not ("on" or "off")) { con.WriteLine($"{option} takes on or off."); return 2; }
                preferences = apply(preferences, value == "on");
            }

            var view = new PdfInitialView
            {
                PageLayout = layout, PageMode = panel, PageIndex = pageIndex, Destination = destination,
                NamedDestination = named, ViewerPreferences = preferences
            };
            CliWriteEditedCopy(source, output, path => PdfEngineIntegration.ApplyInitialView(path, view));
            con.WriteLine($"Set the initial view -> {output}");
            return 0;
        }

        private static bool CliChoice<T>(string value, out T? choice, params (string Name, T? Value)[] choices) where T : struct
        {
            foreach (var (name, candidate) in choices)
                if (string.Equals(value, name, StringComparison.OrdinalIgnoreCase)) { choice = candidate; return true; }
            choice = null;
            return false;
        }

        // ============================================================
        // --set-layer <in.pdf> <out.pdf> <layer> [--name <new>] [--visible on|off] [--locked on|off]
        // ============================================================
        // The layer is its name, or # and its object number as --layers lists it.
        private static int CliSetLayer(List<string> pos, Dictionary<string, string> options, TextWriter con)
        {
            const string usage = "Usage: KillerPDF.exe --set-layer <in.pdf> <out.pdf> <layer name or #object> [--name <new name>] [--visible on|off] [--locked on|off]";
            if (pos.Count != 3) { con.WriteLine(usage); return 2; }
            if (!CliInspectEditInput(pos[0], pos[1], out string source, out string output, out _, con)) return 2;
            PdfOptionalContentInfo layers = PdfEngineIntegration.InspectLayers(source, null);
            string wanted = pos[2];
            PdfOptionalContentGroupInfo[] matches = wanted.StartsWith('#') && int.TryParse(wanted[1..], out int number)
                ? [.. layers.Groups.Where(group => group.ObjectNumber == number)]
                : [.. layers.Groups.Where(group => string.Equals(group.Name, wanted, StringComparison.OrdinalIgnoreCase))];
            if (matches.Length != 1)
            {
                con.WriteLine(matches.Length == 0 ? $"No layer is named \"{wanted}\"." : $"{matches.Length} layers are named \"{wanted}\"; name one by its object number, such as #{matches[0].ObjectNumber}.");
                foreach (PdfOptionalContentGroupInfo group in layers.Groups) con.WriteLine($"  #{group.ObjectNumber} {group.Name}");
                return 2;
            }
            PdfOptionalContentGroupInfo layer = matches[0];
            string name = options.TryGetValue("--name", out string? newName) && !string.IsNullOrWhiteSpace(newName) ? newName : layer.Name;
            bool visible = layers.ViewVisibleGroupObjectNumbers.Contains(layer.ObjectNumber), locked = layer.IsLocked;
            foreach (var (option, set) in new (string, Action<bool>)[] { ("--visible", on => visible = on), ("--locked", on => locked = on) })
            {
                if (!options.TryGetValue(option, out string? value)) continue;
                if (value is not ("on" or "off")) { con.WriteLine($"{option} takes on or off."); return 2; }
                set(value == "on");
            }
            CliWriteEditedCopy(source, output, path => PdfEngineIntegration.ApplyLayerEdits(path,
                [new PdfEngineIntegration.LayerEdit(layer.ObjectNumber, name, visible, locked)]));
            con.WriteLine($"Layer \"{name}\" is {(visible ? "shown" : "hidden")} and {(locked ? "locked" : "unlocked")} -> {output}");
            return 0;
        }

        // ============================================================
        // --add-attachment <in.pdf> <out.pdf> <file> [<file> ...] [--description <text>]
        //                  [--relationship <name>]
        // ============================================================
        private static int CliAddAttachment(List<string> pos, Dictionary<string, string> options, TextWriter con)
        {
            if (pos.Count < 3)
            {
                con.WriteLine("Usage: KillerPDF.exe --add-attachment <in.pdf> <out.pdf> <file> [<file> ...] [--description <text>] [--relationship data|source|alternative|supplement|unspecified]");
                return 2;
            }
            if (!CliInspectEditInput(pos[0], pos[1], out string source, out string output, out _, con)) return 2;
            string[] files = [.. pos.Skip(2).Select(Path.GetFullPath)];
            foreach (string file in files)
                if (!File.Exists(file)) { con.WriteLine($"File not found: {file}"); return 2; }
            if (files.Select(Path.GetFileName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Length)
            { con.WriteLine("Each attached file needs a different name."); return 2; }
            HashSet<string> existing = [.. PdfEngineIntegration.ReadAttachmentEditorItems(source, null).Select(item => item.FileName)];
            string? clash = files.Select(Path.GetFileName).FirstOrDefault(name => existing.Contains(name!, StringComparer.OrdinalIgnoreCase));
            if (clash is not null) { con.WriteLine($"The document already has an attachment named {clash}."); return 2; }
            options.TryGetValue("--description", out string? description);
            var relationship = PdfAssociatedFileRelationship.Data;
            if (options.TryGetValue("--relationship", out string? relationshipRaw)
                && !Enum.TryParse(relationshipRaw, ignoreCase: true, out relationship))
            { con.WriteLine("--relationship must be data, source, alternative, supplement or unspecified."); return 2; }
            CliWriteEditedCopy(source, output, path =>
            {
                var editor = new PdfIncrementalPageEditor(PdfDocument.Open(File.ReadAllBytes(path)));
                foreach (string file in files)
                {
                    var info = new FileInfo(file);
                    editor.AddAttachment(info.Name, File.ReadAllBytes(file), CliMimeType(info.Extension),
                        string.IsNullOrWhiteSpace(description) ? null : description, relationship,
                        new DateTimeOffset(info.LastWriteTimeUtc), new DateTimeOffset(info.CreationTimeUtc));
                }
                File.WriteAllBytes(path, editor.Build());
            });
            con.WriteLine($"Attached {files.Length} files -> {output}");
            return 0;
        }

        private static string CliMimeType(string extension) => extension.ToLowerInvariant() switch
        {
            ".pdf" => "application/pdf", ".txt" => "text/plain", ".csv" => "text/csv", ".xml" => "application/xml",
            ".json" => "application/json", ".html" or ".htm" => "text/html", ".md" => "text/markdown",
            ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif", ".tif" or ".tiff" => "image/tiff",
            ".svg" => "image/svg+xml", ".zip" => "application/zip",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            _ => "application/octet-stream",
        };

        // ============================================================
        // --edit-attachment <in.pdf> <out.pdf> <name> [--rename <new>] [--description <text>]
        //                   [--mime <type>] [--relationship <name>] [--remove]
        // ============================================================
        private static int CliEditAttachment(List<string> pos, Dictionary<string, string> options, TextWriter con)
        {
            if (pos.Count != 3)
            {
                con.WriteLine("Usage: KillerPDF.exe --edit-attachment <in.pdf> <out.pdf> <name> [--rename <new name>] [--description <text>] [--mime <type>] [--relationship <name>] [--remove]");
                return 2;
            }
            if (!CliInspectEditInput(pos[0], pos[1], out string source, out string output, out _, con)) return 2;
            IReadOnlyList<PdfEngineIntegration.AttachmentEditorItem> items = PdfEngineIntegration.ReadAttachmentEditorItems(source, null);
            PdfEngineIntegration.AttachmentEditorItem? target =
                PdfEngineIntegration.FindAttachmentEditorItem(items, pos[2], out bool ambiguous);
            if (ambiguous)
            {
                con.WriteLine($"Several attachments are named {pos[2]}.");
                return 2;
            }
            if (target is null)
            {
                con.WriteLine($"The document has no attachment named {pos[2]}.");
                foreach (var item in items) con.WriteLine("  " + item.FileName);
                return 2;
            }
            PdfEngineIntegration.AttachmentEditorItem edited = target;
            if (options.ContainsKey("--remove")) edited = edited with { Remove = true };
            if (options.TryGetValue("--rename", out string? rename) && !string.IsNullOrWhiteSpace(rename)) edited = edited with { FileName = rename };
            if (options.TryGetValue("--description", out string? description)) edited = edited with { Description = description };
            if (options.TryGetValue("--mime", out string? mime) && !string.IsNullOrWhiteSpace(mime)) edited = edited with { MimeType = mime };
            if (options.TryGetValue("--relationship", out string? relationshipRaw))
            {
                if (!Enum.TryParse(relationshipRaw, ignoreCase: true, out PdfAssociatedFileRelationship relationship))
                { con.WriteLine("--relationship must be data, source, alternative, supplement or unspecified."); return 2; }
                edited = edited with { Relationship = relationship };
            }
            if (edited == target) { con.WriteLine("Nothing to change: name --rename, --description, --mime, --relationship or --remove."); return 2; }
            CliWriteEditedCopy(source, output, path => PdfEngineIntegration.ApplyAttachmentEdits(path,
                [.. items.Select(item => ReferenceEquals(item, target) ? edited : item)]));
            con.WriteLine((edited.Remove ? $"Removed {target.FileName}" : $"Updated {edited.FileName}") + $" -> {output}");
            return 0;
        }

        // ============================================================
        // --portfolio-view <in.pdf> <out.pdf> (--view details|tile|hidden [--initial <name>] | --clear)
        // ============================================================
        private static int CliPortfolioView(List<string> pos, Dictionary<string, string> options, TextWriter con)
        {
            const string usage = "Usage: KillerPDF.exe --portfolio-view <in.pdf> <out.pdf> (--view details|tile|hidden [--initial <attachment>] | --clear)";
            if (pos.Count != 2) { con.WriteLine(usage); return 2; }
            if (!CliInspectEditInput(pos[0], pos[1], out string source, out string output, out _, con)) return 2;
            if (options.ContainsKey("--clear"))
            {
                CliWriteEditedCopy(source, output, PdfEngineIntegration.ClearPortfolioMetadata);
                con.WriteLine($"Removed the portfolio presentation -> {output}");
                return 0;
            }
            if (!options.TryGetValue("--view", out string? viewRaw)
                || !CliChoice(viewRaw, out PdfCollectionView? view, ("details", PdfCollectionView.Details),
                    ("tile", PdfCollectionView.Tile), ("hidden", PdfCollectionView.Hidden)) || view is null)
            { con.WriteLine(usage); return 2; }
            options.TryGetValue("--initial", out string? initial);
            if (!string.IsNullOrWhiteSpace(initial) && !PdfEngineIntegration.ReadAttachmentEditorItems(source, null)
                    .Any(item => string.Equals(item.FileName, initial, StringComparison.Ordinal)))
            { con.WriteLine($"The document has no attachment named {initial}."); return 2; }
            CliWriteEditedCopy(source, output, path => PdfEngineIntegration.ApplyPortfolioPresentation(
                path, view.Value, string.IsNullOrWhiteSpace(initial) ? null : initial));
            con.WriteLine($"Set the portfolio to open in {viewRaw.ToLowerInvariant()} view -> {output}");
            return 0;
        }
    }
}
