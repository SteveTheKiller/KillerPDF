using System.Globalization;
using System.IO;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPDF.Services;

namespace KillerPDF.Features
{
    // Metadata, bookmark, comment and page-box edits, each writing a new PDF through the same
    // service calls as the windows that make these edits by hand.
    internal static partial class CliRunner
    {
        // --set-metadata <in.pdf> <out.pdf> [--title t] [--author a] [--subject s] [--keywords k] [--creator c]
        private static int CliSetMetadata(List<string> pos, Dictionary<string, string> options, TextWriter con)
        {
            const string usage = "Usage: KillerPDF.exe --set-metadata <in.pdf> <out.pdf> [--title <text>] [--author <text>] [--subject <text>] [--keywords <text>] [--creator <text>]";
            string[] fields = ["--title", "--author", "--subject", "--keywords", "--creator"];
            if (pos.Count != 2 || !fields.Any(options.ContainsKey)) { con.WriteLine(usage); return 2; }
            if (!CliInspectEditInput(pos[0], pos[1], out string source, out string output, out _, con)) return 2;
            PdfDocumentInformation info = PdfDocumentInformation.Read(
                PdfEngineIntegration.OpenForReading(File.ReadAllBytes(source)));
            string? Field(string flag, string? current) => options.TryGetValue(flag, out string? value) ? value : current;
            PdfDocumentMetadata metadata = PdfEngineIntegration.CompleteDocumentMetadata(info, new PdfDocumentMetadata
            {
                Title = Field("--title", info.Title),
                Author = Field("--author", info.Author),
                Subject = Field("--subject", info.Subject),
                Keywords = Field("--keywords", info.Keywords),
                Creator = Field("--creator", info.Creator),
                Producer = info.Producer
            });
            CliWriteEditedCopy(source, output, path => PdfEngineIntegration.ApplyDocumentMetadata(path, metadata));
            con.WriteLine($"Updated metadata -> {output}");
            return 0;
        }

        // --set-bookmarks <in.pdf> <out.pdf> <bookmarks.json> [--replace]
        private static int CliSetBookmarks(List<string> pos, Dictionary<string, string> options, TextWriter con)
        {
            if (pos.Count != 3)
            {
                con.WriteLine("Usage: KillerPDF.exe --set-bookmarks <in.pdf> <out.pdf> <bookmarks.json> [--replace]");
                return 2;
            }
            if (!CliInspectEditInput(pos[0], pos[1], out string source, out string output, out _, con)) return 2;
            string dataPath = Path.GetFullPath(pos[2]);
            if (!File.Exists(dataPath)) { con.WriteLine($"Input not found: {dataPath}"); return 2; }
            string json = File.ReadAllText(dataPath);
            bool replace = options.ContainsKey("--replace");
            CliWriteEditedCopy(source, output, path => PdfEngineIntegration.ApplyBookmarkData(path, json, replace));
            con.WriteLine($"Imported bookmarks -> {output}");
            return 0;
        }

        // --add-comment <in.pdf> <out.pdf> --page <n> --text <t> [--x <pt>] [--y <pt>] [--author <name>]
        private static int CliAddComment(List<string> pos, Dictionary<string, string> options, TextWriter con)
        {
            const string usage = "Usage: KillerPDF.exe --add-comment <in.pdf> <out.pdf> --page <n> --text <comment> [--x <points>] [--y <points>] [--author <name>]";
            if (pos.Count != 2 || !options.TryGetValue("--text", out string? text) || string.IsNullOrWhiteSpace(text)
                || !options.TryGetValue("--page", out string? pageText) || !int.TryParse(pageText, out int page))
            { con.WriteLine(usage); return 2; }
            if (!CliInspectEditInput(pos[0], pos[1], out string source, out string output, out int pageCount, con)) return 2;
            if (page < 1 || page > pageCount) { con.WriteLine($"--page must be 1-{pageCount}"); return 2; }
            if (!CliPoint(options, "--x", out double? x, con) || !CliPoint(options, "--y", out double? y, con)) return 2;
            options.TryGetValue("--author", out string? author);
            var addition = new CommentAddition(page - 1, text, "KillerPDF-" + Guid.NewGuid().ToString("N"), null, x, y);
            CliWriteEditedCopy(source, output, path => PdfEngineIntegration.ApplyCommentEdits(path, [addition], author));
            con.WriteLine($"Added a comment on page {page} -> {output}");
            return 0;
        }

        // --delete-comments <in.pdf> <out.pdf> [--pages <range>]
        private static int CliDeleteComments(List<string> pos, Dictionary<string, string> options, TextWriter con)
        {
            if (pos.Count != 2)
            {
                con.WriteLine("Usage: KillerPDF.exe --delete-comments <in.pdf> <out.pdf> [--pages <range>]");
                return 2;
            }
            if (!CliInspectEditInput(pos[0], pos[1], out string source, out string output, out int pageCount, con)) return 2;
            HashSet<int>? pages = null;
            if (options.TryGetValue("--pages", out string? range))
            {
                if (CliParsePageRange(range, pageCount, out string error) is not { } parsed) { con.WriteLine(error); return 2; }
                pages = [.. parsed];
            }
            // Links and form fields can carry a description, but they are not comments. Replies go
            // with their parent wherever they sit, or removing the parent would orphan them.
            PdfCommentInfo[] annotations = [.. PdfCommentReader.Read(
                    PdfEngineIntegration.OpenForReading(File.ReadAllBytes(source)), includeMarkupWithoutText: true)
                .Where(comment => comment.Subtype is not ("Popup" or "Link" or "Widget"))];
            List<PdfCommentInfo> chosen = [.. annotations.Where(comment =>
                !string.IsNullOrEmpty(comment.Contents) && (pages is null || pages.Contains(comment.PageIndex)))];
            for (int added = 1; added > 0;)
            {
                HashSet<int> parents = [.. chosen.Where(comment => comment.ObjectNumber is not null)
                    .Select(comment => comment.ObjectNumber!.Value)];
                PdfCommentInfo[] replies = [.. annotations.Where(comment => !chosen.Contains(comment)
                    && comment.ReplyToObjectNumber is int parent && parents.Contains(parent))];
                chosen.AddRange(replies);
                added = replies.Length;
            }
            CommentEdit[] removals = [.. chosen.Select(comment => new CommentRemoval(comment))];
            CliWriteEditedCopy(source, output, path =>
            {
                if (removals.Length > 0) PdfEngineIntegration.ApplyCommentEdits(path, removals, null);
            });
            con.WriteLine($"Deleted {removals.Length} comments -> {output}");
            return 0;
        }

        // --crop <in.pdf> <out.pdf> --box <x,y,width,height> [--pages <range>] [--media]
        // --crop <in.pdf> <out.pdf> --clear [--pages <range>]
        private static int CliCrop(List<string> pos, Dictionary<string, string> options, TextWriter con)
        {
            const string usage = "Usage: KillerPDF.exe --crop <in.pdf> <out.pdf> --box <x,y,width,height> [--pages <range>] [--media]\n"
                + "       KillerPDF.exe --crop <in.pdf> <out.pdf> --clear [--pages <range>]\n"
                + "  values are PDF points from the page's lower left; --media resizes the page itself";
            bool clear = options.ContainsKey("--clear"), media = options.ContainsKey("--media");
            options.TryGetValue("--box", out string? boxText);
            if (pos.Count != 2 || clear == (boxText is not null) || (clear && media)) { con.WriteLine(usage); return 2; }
            PdfEngineIntegration.PageRectangle? box = null;
            if (boxText is not null)
            {
                double[] values = [.. boxText.Split(',').Select(part =>
                    double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : double.NaN)];
                if (values.Length != 4 || values.Any(value => !double.IsFinite(value)) || values[2] <= 0 || values[3] <= 0)
                { con.WriteLine(usage); return 2; }
                box = new PdfEngineIntegration.PageRectangle(values[0], values[1], values[2], values[3]);
            }
            if (!CliInspectEditInput(pos[0], pos[1], out string source, out string output, out int pageCount, con)) return 2;
            List<int> pages = [.. Enumerable.Range(0, pageCount)];
            if (options.TryGetValue("--pages", out string? range))
            {
                if (CliParsePageRange(range, pageCount, out string error) is not { } parsed) { con.WriteLine(error); return 2; }
                pages = parsed;
            }
            if (box is { } crop && !media)
            {
                var sizes = PdfPageInformation.Read(PdfEngineIntegration.OpenForReading(File.ReadAllBytes(source)));
                if (pages.Any(page => crop.X >= sizes[page].Width || crop.Y >= sizes[page].Height
                    || crop.X + crop.Width <= 0 || crop.Y + crop.Height <= 0))
                { con.WriteLine("--box does not overlap the page."); return 2; }
            }
            CliWriteEditedCopy(source, output, path =>
            {
                if (media) PdfEngineIntegration.ApplyMediaBoxes(path, pages, box!.Value);
                else PdfEngineIntegration.ApplyCropBoxes(path, pages.ToDictionary(page => page, _ => box));
            });
            con.WriteLine($"{(media ? "Resized" : clear ? "Cleared the crop on" : "Cropped")} {pages.Count} pages -> {output}");
            return 0;
        }

        private static bool CliPoint(Dictionary<string, string> options, string flag, out double? value, TextWriter con)
        {
            value = null;
            if (!options.TryGetValue(flag, out string? text)) return true;
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) && double.IsFinite(parsed))
            {
                value = parsed;
                return true;
            }
            con.WriteLine($"{flag} must be a number of PDF points");
            return false;
        }
    }
}
