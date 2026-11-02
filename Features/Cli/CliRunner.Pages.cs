using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerPdf.Engine.Documents;
using KillerPDF.Services;

namespace KillerPDF.Features
{
    // Page menu commands from the command line: Reverse Page Order, Fix Page Orientation,
    // Straighten Scanned Pages (#436), Divide Spreads (#382) and Rasterize Region (#369). Each
    // writes a new file and runs the same page code as the page menu.
    internal static partial class CliRunner
    {
        // ============================================================
        // --reverse-pages <in.pdf> <out.pdf> [--pages <range>]
        // ============================================================
        private static int CliReversePages(List<string> pos, Dictionary<string, string> options, TextWriter con)
        {
            if (pos.Count != 2)
            {
                con.WriteLine("Usage: KillerPDF.exe --reverse-pages <in.pdf> <out.pdf> [--pages <range>]");
                return 2;
            }
            if (!CliInspectEditInput(pos[0], pos[1], out string source, out string output, out int pageCount, con)) return 2;
            if (!CliPagesOption(options, pageCount, out List<int> pages, con)) return 2;
            if (pages.Count < 2) { con.WriteLine("Reversing needs at least two pages."); return 2; }
            IReadOnlyList<int> order = PdfEngineIntegration.PageOrderAfterReverse(pageCount, pages);
            CliWriteEditedCopy(source, output, path => PdfEngineIntegration.ReorderPages(path, order));
            con.WriteLine($"Reversed the order of {pages.Count} pages -> {output}");
            return 0;
        }

        // ============================================================
        // --fix-orientation <in.pdf> <out.pdf> [--pages <range>] [--lang <code>] [--password <p>]
        // ============================================================
        // Pages with text turn to the way their text runs; scans without text are read with OCR
        // in the directions their lines allow. The fix is a page rotation, nothing is redrawn.
        private static int CliFixOrientation(List<string> pos, Dictionary<string, string> options, TextWriter con)
        {
            if (pos.Count != 2)
            {
                con.WriteLine("Usage: KillerPDF.exe --fix-orientation <in.pdf> <out.pdf> [--pages <range>] [--lang <code>] [--password <password>]");
                return 2;
            }
            options.TryGetValue("--password", out string? password);
            if (!CliInspectEditInput(pos[0], pos[1], out string source, out string output, out int pageCount, con, password)) return 2;
            if (!CliPagesOption(options, pageCount, out List<int> pages, con)) return 2;
            var (renderPath, rotations, _) = CliPrepareRenderSource(source, password, con);
            int Rotation(int page) => rotations is not null && page < rotations.Length ? rotations[page] : 0;

            var turns = new Dictionary<int, int>();
            int unread = 0;
            Dictionary<int, int?> fromText = PageArrangementService.UprightFromText(renderPath, pages, CancellationToken.None);
            var scans = new List<int>();
            foreach (int page in pages)
            {
                if (!fromText.TryGetValue(page, out int? upright)) { scans.Add(page); continue; }
                if (upright is not int target) { unread++; continue; }
                int turn = PageGeometry.TurnBetween(Rotation(page), target);
                if (turn != 0) turns[page] = turn;
            }

            if (scans.Count > 0)
            {
                options.TryGetValue("--lang", out string? langRaw);
                string lang = string.IsNullOrWhiteSpace(langRaw) ? "eng" : langRaw.Trim().ToLowerInvariant();
                if (!CliEnsureOcrLanguage(lang, con)) return 1;
                using var ocr = new OcrService(language: lang);
                using var session = PdfPageRenderSession.OpenEngineFirst(renderPath, 1600, 1600, null);
                for (int i = 0; i < scans.Count; i++)
                {
                    con.WriteLine($"Reading scan {i + 1}/{scans.Count}");
                    var (pixels, width, height) = CliDisplayedPage(session.RenderPage(scans[i]), Rotation(scans[i]));
                    int? turn = PageArrangementService.ScanUprightTurn(ocr, pixels, width, height, CancellationToken.None);
                    if (turn is null) unread++;
                    else if (turn != 0) turns[scans[i]] = turn.Value;
                }
            }

            CliWriteEditedCopy(source, output, path =>
            {
                foreach (var group in turns.GroupBy(pair => pair.Value))
                    PdfEngineIntegration.RotatePages(path, [.. group.Select(pair => pair.Key)], group.Key, password);
            });
            string skipped = unread > 0 ? $" ({unread} pages could not be read and were left as they were)" : "";
            con.WriteLine(turns.Count == 0
                ? $"Every page already reads upright{skipped} -> {output}"
                : $"Turned {turns.Count} pages upright{skipped} -> {output}");
            return 0;
        }

        // ============================================================
        // --straighten <in.pdf> <out.pdf> [--pages <range>] [--password <p>]
        // ============================================================
        // Levels tilted scans without a text layer, keeping each page's size. A scan that
        // already has text is left alone, since redrawing it would drop that text.
        private static int CliStraighten(List<string> pos, Dictionary<string, string> options, TextWriter con)
        {
            if (pos.Count != 2)
            {
                con.WriteLine("Usage: KillerPDF.exe --straighten <in.pdf> <out.pdf> [--pages <range>] [--password <password>]");
                return 2;
            }
            options.TryGetValue("--password", out string? password);
            if (!CliInspectEditInput(pos[0], pos[1], out string source, out string output, out int pageCount, con, password)) return 2;
            if (!CliPagesOption(options, pageCount, out List<int> pages, con)) return 2;
            var (renderPath, rotations, dims) = CliPrepareRenderSource(source, password, con);
            int Rotation(int page) => rotations is not null && page < rotations.Length ? rotations[page] : 0;

            var (scans, withText) = PageArrangementService.ScansWithoutText(renderPath, pages, CancellationToken.None);
            string skipped = withText > 0 ? $" ({withText} scans with a text layer were left as they were)" : "";
            var tilts = new Dictionary<int, double>();
            using (var preview = PdfPageRenderSession.OpenEngineFirst(renderPath, 1600, 1600, null))
            {
                foreach (int page in scans)
                {
                    var (pixels, width, height) = CliDisplayedPage(preview.RenderPage(page), Rotation(page));
                    double tilt = PageArrangementService.MeasureTilt(pixels, width, height);
                    if (Math.Abs(tilt) >= PageArrangementService.MinimumTilt) tilts[page] = tilt;
                }
            }
            if (tilts.Count == 0)
            {
                CliEnsureParentDir(output);
                File.Copy(source, output, overwrite: true);
                con.WriteLine($"No scanned page needed straightening{skipped} -> {output}");
                return 0;
            }

            // The pages are redrawn level on the unrotated copy the renderer reads, then every
            // page keeps the rotation it had except the redrawn ones, which are drawn as shown.
            var replacements = new Dictionary<int, string>();
            using (var session = PdfPageRenderSession.OpenEngineFirst(renderPath, 1.0, null))
            {
                foreach (var (page, tilt) in tilts.OrderBy(pair => pair.Key))
                {
                    con.WriteLine($"Straightening page {page + 1} by {-tilt:0.##} degrees");
                    int rotation = Rotation(page);
                    var (width, height) = dims is not null && page < dims.Length ? dims[page] : (612.0, 792.0);
                    if (rotation is 90 or 270) (width, height) = (height, width);
                    int budget = Math.Clamp((int)Math.Ceiling(Math.Max(width, height) * 300 / 72.0), 1000, 6000);
                    var (pixels, pixelWidth, pixelHeight) = CliDisplayedPage(
                        session.RenderFittedPage(page, budget, budget), rotation);
                    BitmapSource bitmap = BitmapSource.Create(pixelWidth, pixelHeight, 96, 96,
                        PixelFormats.Bgra32, null, pixels, pixelWidth * 4);
                    bitmap.Freeze();
                    string temporary = App.MakeTempFile("clistraighten");
                    File.WriteAllBytes(temporary, PdfEngineIntegration.CreateRasterDocument([
                        PageArrangementService.LevelPage(bitmap, tilt, width, height)]));
                    replacements[page] = temporary;
                }
            }
            int[] changed = [.. replacements.Keys.OrderBy(index => index)];
            CliWriteEditedCopy(renderPath, output, path =>
            {
                if (changed.Length == pageCount)
                    PdfEngineIntegration.ReplaceAllPagesAndCompact(path, [.. changed.Select(page => replacements[page])]);
                else
                    PdfEngineIntegration.ReplacePagesAndCompact(path, replacements);
                PdfEngineIntegration.ApplyPageRotations(path, Enumerable.Range(0, pageCount)
                    .Where(page => !replacements.ContainsKey(page) && Rotation(page) != 0)
                    .ToDictionary(page => page, Rotation));
            });
            con.WriteLine($"Straightened {changed.Length} scanned pages{skipped} -> {output}");
            return 0;
        }

        // ============================================================
        // --divide-spreads <in.pdf> <out.pdf> [--pages <range>] [--direction vertical|horizontal]
        //                  [--position <0.1-0.9>]
        // ============================================================
        private static int CliDivideSpreads(List<string> pos, Dictionary<string, string> options, TextWriter con)
        {
            if (pos.Count != 2)
            {
                con.WriteLine("Usage: KillerPDF.exe --divide-spreads <in.pdf> <out.pdf> [--pages <range>] "
                    + "[--direction vertical|horizontal] [--position <0.1-0.9>]");
                return 2;
            }
            if (!CliInspectEditInput(pos[0], pos[1], out string source, out string output, out int pageCount, con)) return 2;
            if (!CliPagesOption(options, pageCount, out List<int> pages, con)) return 2;
            var direction = PdfSpreadDivisionDirection.Vertical;
            if (options.TryGetValue("--direction", out string? directionRaw))
            {
                switch (directionRaw.ToLowerInvariant())
                {
                    case "vertical": break;
                    case "horizontal": direction = PdfSpreadDivisionDirection.Horizontal; break;
                    default: con.WriteLine("--direction must be vertical (left and right) or horizontal (top and bottom)."); return 2;
                }
            }
            double position = 0.5;
            if (options.TryGetValue("--position", out string? positionRaw)
                && (!double.TryParse(positionRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out position)
                    || position < 0.1 || position > 0.9))
            {
                con.WriteLine("--position must be a fraction of the page from 0.1 to 0.9, such as 0.5 for the middle.");
                return 2;
            }
            PdfSpreadDivisionRequest[] requests = [.. pages.Select(page => new PdfSpreadDivisionRequest(page, direction, position))];
            CliWriteEditedCopy(source, output, path => PdfEngineIntegration.DivideSpreads(path, requests));
            con.WriteLine($"Divided {pages.Count} pages into {pages.Count * 2} -> {output}");
            return 0;
        }

        // ============================================================
        // --rasterize-region <in.pdf> <page> <left,bottom,right,top> <out.pdf> [--dpi <n>]
        // ============================================================
        // The box is in PDF points on the unrotated page, measured from its bottom-left corner.
        private static int CliRasterizeRegion(List<string> pos, Dictionary<string, string> options, TextWriter con)
        {
            const string usage = "Usage: KillerPDF.exe --rasterize-region <in.pdf> <page> <left,bottom,right,top> <out.pdf> [--dpi <n>]   (box in points on the unrotated page; default 300 dpi)";
            if (pos.Count != 4 || !int.TryParse(pos[1], out int pageNumber))
            {
                con.WriteLine(usage);
                return 2;
            }
            double[] box = [.. pos[2].Split(',').Select(part =>
                double.TryParse(part.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : double.NaN)];
            if (box.Length != 4 || box.Any(double.IsNaN)) { con.WriteLine(usage); return 2; }
            if (!CliInspectEditInput(pos[0], pos[3], out string source, out string output, out int pageCount, con)) return 2;
            if (pageNumber < 1 || pageNumber > pageCount) { con.WriteLine($"Page must be from 1 to {pageCount}."); return 2; }
            PdfPageInformation page = PdfEngineIntegration.ReadPageInformation(source)[pageNumber - 1];
            var region = new PdfContentBounds(
                Math.Clamp(Math.Min(box[0], box[2]), 0, page.Width), Math.Clamp(Math.Min(box[1], box[3]), 0, page.Height),
                Math.Clamp(Math.Max(box[0], box[2]), 0, page.Width), Math.Clamp(Math.Max(box[1], box[3]), 0, page.Height));
            if (region.Right - region.Left < 1 || region.Top - region.Bottom < 1)
            {
                con.WriteLine("The box must lie on the page and be at least a point wide and tall.");
                return 2;
            }
            if (!TryReadDpi(options, 300, con, out double dpi)) return 2;
            CliWriteEditedCopy(source, output, path => PdfEngineIntegration.RasterizeRegion(path, pageNumber - 1, region, dpi));
            con.WriteLine($"Rasterized a {region.Right - region.Left:0.#} by {region.Top - region.Bottom:0.#} point box on page {pageNumber} at {dpi:0} dpi -> {output}");
            return 0;
        }

        // The selected pages from --pages, or every page.
        private static bool CliPagesOption(Dictionary<string, string> options, int pageCount, out List<int> pages, TextWriter con)
        {
            pages = [.. Enumerable.Range(0, pageCount)];
            if (!options.TryGetValue("--pages", out string? spec)) return true;
            List<int>? parsed = CliParsePageRange(spec, pageCount, out string error);
            if (parsed is null) { con.WriteLine(error); return false; }
            pages = parsed;
            return true;
        }

        // A rendered page turned by its /Rotate, so it reads the way a viewer shows it.
        private static (byte[] Pixels, int Width, int Height) CliDisplayedPage(PdfRenderedPage rendered, int rotation)
        {
            (byte[] pixels, int width, int height) = (rendered.Pixels, rendered.Width, rendered.Height);
            return rotation == 0 ? (pixels, width, height) : BitmapHelpers.RotateBitmap(pixels, width, height, rotation);
        }
    }
}
