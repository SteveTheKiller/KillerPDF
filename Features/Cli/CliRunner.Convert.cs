using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using KillerPdf.Engine.Formats.Epub;
using KillerPDF.Services;

namespace KillerPDF.Features
{
    // Convert to PDF from the command line: an EPUB or DjVu book becomes a PDF through the same native conversion,
    // paper sizes, margin and notices the app's Convert to PDF uses.
    internal static partial class CliRunner
    {
        private const string ConvertBookUsage =
            "Usage: KillerPDF.exe --convert-book <in.epub|in.djvu|in.djv> <out.pdf> [--paper a4|letter|legal]\n"
            + "                     [--width <mm> --height <mm>] [--margin <mm>] [--overwrite] [--json]\n"
            + "  The paper, size and margin options apply to EPUB only; a DjVu book keeps its own page sizes.";

        // The conversion dialog's presets and default margin, in millimeters.
        private const double BookDefaultMarginMm = 15;
        private static readonly (string Name, double Width, double Height)[] BookPapers =
            [("a4", 210, 297), ("letter", 215.9, 279.4), ("legal", 215.9, 355.6)];
        private static readonly string[] BookLayoutOptions = ["--paper", "--width", "--height", "--margin"];

        // ============================================================
        // --convert-book <in.epub|in.djvu|in.djv> <out.pdf> [--paper ...] [--width <mm> --height <mm>] [--margin <mm>]
        // ============================================================
        private static int CliConvertBook(List<string> pos, Dictionary<string, string> options, TextWriter con)
        {
            if (pos.Count != 2) { con.WriteLine(ConvertBookUsage); return 2; }
            string inPath = Path.GetFullPath(pos[0]), outPath = Path.GetFullPath(pos[1]);
            if (!File.Exists(inPath)) { con.WriteLine($"Input not found: {inPath}"); return 2; }
            // The app opens a book by its extension, so the command decides the same way.
            bool epub = NativeOpen.IsEpubPath(inPath);
            if (!epub && Path.GetExtension(inPath).ToLowerInvariant() is not (".djvu" or ".djv"))
            {
                con.WriteLine("The input must be an .epub, .djvu or .djv book.");
                return 2;
            }
            if (!epub && BookLayoutOptions.FirstOrDefault(options.ContainsKey) is string layoutOption)
            {
                con.WriteLine($"{layoutOption} applies only to EPUB books; a DjVu book keeps its own page sizes.");
                return 2;
            }
            double width = 0, height = 0, margin = 0;
            if (epub && !CliBookLayout(options, con, out width, out height, out margin)) return 2;

            // Checked before the book is read, so a refused destination costs no layout work.
            if (string.Equals(inPath, outPath, StringComparison.OrdinalIgnoreCase))
            { con.WriteLine(CliText("Str_Convert_SameAsSource")); return 2; }
            if (!Path.GetExtension(outPath).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            { con.WriteLine(CliText("Str_Convert_NeedsPdfName")); return 2; }
            if (App.IsCleanupOwnedPath(outPath))
            { con.WriteLine(CliText("Str_Convert_DestinationNotPermanent")); return 2; }
            bool replace = File.Exists(outPath);
            if (replace && !options.ContainsKey("--overwrite"))
            {
                con.WriteLine(CliText("Str_Convert_DestinationExists") + " Use --overwrite to replace it.");
                return 2;
            }

            var notices = new List<string>();
            try
            {
                CliEnsureParentDir(outPath);
                if (epub)
                {
                    // The same font, face resolver and unavailable-chapter page the window's Convert to PDF uses.
                    var face = PdfEngineIntegration.LoadTextFont("Segoe UI", "A", bold: false, italic: false)
                        ?? throw new NotSupportedException(CliText("Str_Convert_FontUnavailable"));
                    var resolver = PdfEngineIntegration.EpubFontFaceResolver(face.Family);
                    string unavailable = CliText("Str_Epub_ChapterUnavailable");
                    var layout = new EpubLayoutOptions(800, 1000, face.Font)
                    {
                        FontFaceResolver = resolver,
                        IsolateChapterFailures = true,
                        UnavailableChapterLabel = unavailable,
                        NoticeFallbackFamilies = NativePdfConversion.NoticeFallbackFamilies(face.Family, unavailable)
                    };
                    var omitted = new List<EpubChapterDiagnostic>();
                    var conversion = NativePdfConversion.EpubOptions(face.Font, resolver,
                        NativePdfConversion.MillimetersToPoints(width), NativePdfConversion.MillimetersToPoints(height),
                        NativePdfConversion.MillimetersToPoints(margin), unavailable, face.Family)
                        with { DiagnosticReported = omitted.Add };
                    using var source = NativeOpen.Open(inPath, layout);
                    NativePdfConversion.WriteNewEpub(source, inPath, outPath, conversion, replaceExisting: replace);
                    notices.AddRange(omitted.Select(CliEpubNotice));
                }
                else
                {
                    using var source = NativeOpen.Open(inPath, null);
                    var djvu = NativePdfConversion.WriteNewDjvu(source, inPath, outPath, replaceExisting: replace);
                    if (NativePdfConversion.DjvuConversionNote(djvu, CliText) is { } note) notices.AddRange(note.Split('\n'));
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or InvalidOperationException
                or NotSupportedException or FormatException or UnauthorizedAccessException)
            {
                con.WriteLine("Could not convert the book: " + BatchRunner.FlattenBatchDetail(ex.Message));
                return 1;
            }

            int pageCount = KillerPdf.Engine.Documents.PdfDocumentInformation
                .Read(KillerPdf.Engine.Documents.PdfDocument.Open(File.ReadAllBytes(outPath))).PageCount;
            if (options.ContainsKey("--json"))
            {
                con.WriteLine(JsonSerializer.Serialize(new
                {
                    input = inPath, output = outPath, format = epub ? "epub" : "djvu", pageCount,
                    paper = epub ? new { widthMm = width, heightMm = height, marginMm = margin } : null,
                    notices
                }));
                return 0;
            }
            con.WriteLine($"Converted {Path.GetFileName(inPath)} ({pageCount} {(pageCount == 1 ? "page" : "pages")}) -> {outPath}");
            foreach (string notice in notices) con.WriteLine("Warning: " + notice);
            return 0;
        }

        // The paper in millimeters: A4 with the dialog's 15 mm margin unless --paper or --width and --height say
        // otherwise, accepted only when the EPUB layout and a PDF reader accept it, as the dialog does.
        private static bool CliBookLayout(Dictionary<string, string> options, TextWriter con,
            out double width, out double height, out double margin)
        {
            (_, width, height) = BookPapers[0];
            margin = BookDefaultMarginMm;
            bool hasWidth = options.ContainsKey("--width"), hasHeight = options.ContainsKey("--height");
            if (options.TryGetValue("--paper", out string? paper))
            {
                if (hasWidth || hasHeight)
                {
                    con.WriteLine("Give either --paper or --width and --height, not both.");
                    return false;
                }
                int preset = Array.FindIndex(BookPapers, item => item.Name.Equals(paper.Trim(), StringComparison.OrdinalIgnoreCase));
                if (preset < 0) { con.WriteLine("--paper must be a4, letter or legal."); return false; }
                (_, width, height) = BookPapers[preset];
            }
            else if (hasWidth || hasHeight)
            {
                if (!hasWidth || !hasHeight)
                {
                    con.WriteLine("--width and --height go together; give both in millimeters.");
                    return false;
                }
                if (!CliMillimeters(options["--width"], out width) || !CliMillimeters(options["--height"], out height))
                {
                    con.WriteLine("--width and --height must be numbers of millimeters.");
                    return false;
                }
            }
            if (options.TryGetValue("--margin", out string? marginRaw) && !CliMillimeters(marginRaw, out margin))
            {
                con.WriteLine("--margin must be a number of millimeters.");
                return false;
            }
            if (!NativePdfConversion.FitsLayout(width, height, margin))
            {
                con.WriteLine(CliText("Str_NativePdf_InvalidLayout"));
                return false;
            }
            return true;
        }

        private static bool CliMillimeters(string raw, out double value) =>
            double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

        // One line per chapter or item the PDF left out: where, the app's reason, and the engine's diagnosis.
        private static string CliEpubNotice(EpubChapterDiagnostic omitted)
        {
            string where = omitted.SpineIndex >= 0
                ? string.Format(CliText("Str_Epub_Diagnostics_Chapter"), omitted.SpineIndex + 1) + ", " + omitted.Href
                : omitted.Href;
            string what = string.IsNullOrEmpty(omitted.ItemHref) ? "left out." : omitted.ItemHref + " left out.";
            return $"{where}: {what} {CliText(NativeDocumentDiagnostics.ReasonKey(omitted.Reason))} ({omitted.Message})";
        }

        // The command line speaks the app's en-US base strings, which App.xaml merges before any command runs.
        private static string CliText(string key) =>
            System.Windows.Application.Current?.TryFindResource(key) as string ?? key;
    }
}
