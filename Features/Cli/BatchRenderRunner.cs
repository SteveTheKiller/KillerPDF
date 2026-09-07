using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Docnet.Core;
using Docnet.Core.Models;
using Docnet.Core.Readers;
using KillerPDF.Services;

namespace KillerPDF.Features
{
    // ============================================================
    // Headless CLI render benchmark
    // ============================================================
    //
    // KillerPDF.exe --batch-render <input.pdf|inputDir> <outputDir> [--size <px>] [--pages <n>] [--log <file.csv>] [--quiet]
    //
    // Renders the first N pages of one PDF (or every *.pdf under a folder tree)
    // through the same page-render path the viewer, print, flatten, and image
    // export use, scaled to fit inside a --size square (default 1024), and
    // writes one PNG per page mirroring the input tree. The CSV log records
    // milliseconds per page so two builds can be compared on the same input.
    //
    // Exit codes: 0 = every page rendered or the file was skipped with a
    // reason, 1 = at least one render failed, 2 = bad usage or bad paths.
    internal static class BatchRenderRunner
    {
        private const int DefaultSize = 1024;
        private const int MaximumSize = 8192;

        internal static bool TryRunBatchRender(string[] args, out int exitCode)
        {
            exitCode = 0;
            int flagIdx = Array.FindIndex(args,
                a => string.Equals(a, "--batch-render", StringComparison.OrdinalIgnoreCase));
            if (flagIdx < 0) return false;

            var con = BatchRunner.OpenBatchConsole();
            string? input = null, output = null, logPath = null;
            int size = DefaultSize, pages = 1;
            bool quiet = false, badUsage = false;
            for (int i = flagIdx + 1; i < args.Length; i++)
            {
                var a = args[i];
                if (string.Equals(a, "--log", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length) logPath = args[++i];
                    else badUsage = true;
                }
                else if (string.Equals(a, "--size", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length && int.TryParse(args[++i], NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out size) && size >= 16 && size <= MaximumSize)
                        continue;
                    badUsage = true;
                }
                else if (string.Equals(a, "--pages", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length && int.TryParse(args[++i], NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out pages) && pages >= 1)
                        continue;
                    badUsage = true;
                }
                else if (string.Equals(a, "--quiet", StringComparison.OrdinalIgnoreCase))
                {
                    quiet = true;
                }
                else if (input is null) input = a;
                else if (output is null) output = a;
                else badUsage = true;
            }

            if (badUsage || string.IsNullOrWhiteSpace(input) || string.IsNullOrWhiteSpace(output))
            {
                con.WriteLine("Usage: KillerPDF.exe --batch-render <input.pdf|inputDir> <outputDir> [--size <px>] [--pages <n>] [--log <file.csv>] [--quiet]");
                exitCode = 2;
                return true;
            }

            try
            {
                exitCode = Run(input!, output!, size, pages, logPath, quiet, con);
            }
            catch (Exception ex)
            {
                con.WriteLine("Render batch failed: " + BatchRunner.FlattenBatchDetail(ex.Message));
                exitCode = 2;
            }
            return true;
        }

        private static int Run(string input, string output, int size, int pageLimit,
            string? logPath, bool quiet, TextWriter con)
        {
            var work = new List<(string Rel, string Src)>();
            if (File.Exists(input))
            {
                work.Add((Path.GetFileName(input), Path.GetFullPath(input)));
            }
            else if (Directory.Exists(input))
            {
                string inRoot = Path.GetFullPath(input).TrimEnd('\\', '/');
                foreach (var f in Directory.GetFiles(inRoot, "*.pdf", SearchOption.AllDirectories))
                    work.Add((f.Substring(inRoot.Length).TrimStart('\\', '/'), f));
            }
            else
            {
                con.WriteLine($"Input not found: {input}");
                return 2;
            }

            string outRoot = Path.GetFullPath(output).TrimEnd('\\', '/');
            Directory.CreateDirectory(outRoot);
            var log = new List<string> { "File,Page,Status,Milliseconds,Width,Height,Detail" };
            int ok = 0, skip = 0, fail = 0;
            long totalMs = 0;
            var total = Stopwatch.StartNew();

            foreach (var (rel, src) in work)
            {
                string dstBase = Path.Combine(outRoot, rel);
                var dstDir = Path.GetDirectoryName(dstBase);
                if (!string.IsNullOrEmpty(dstDir)) Directory.CreateDirectory(dstDir);
                foreach (var row in RenderFile(src, dstBase, size, pageLimit))
                {
                    if (row.Status == "OK") ok++;
                    else if (row.Status == "SKIP") skip++;
                    else fail++;
                    totalMs += row.Milliseconds;
                    if (!quiet)
                        con.WriteLine(row.Detail.Length > 0
                            ? $"{row.Status} {rel} p{row.Page + 1} {row.Milliseconds} ms ({row.Detail})"
                            : $"{row.Status} {rel} p{row.Page + 1} {row.Milliseconds} ms");
                    log.Add(string.Join(",", Csv(rel), (row.Page + 1).ToString(CultureInfo.InvariantCulture),
                        row.Status, row.Milliseconds.ToString(CultureInfo.InvariantCulture),
                        row.Width.ToString(CultureInfo.InvariantCulture),
                        row.Height.ToString(CultureInfo.InvariantCulture), Csv(row.Detail)));
                }
            }

            total.Stop();
            con.WriteLine($"Done. {work.Count} files, {ok} pages OK, {skip} skipped, {fail} failed, {totalMs} ms rendering, {total.ElapsedMilliseconds} ms total.");
            if (!string.IsNullOrWhiteSpace(logPath))
            {
                try
                {
                    File.WriteAllLines(logPath, log, new UTF8Encoding(false));
                    con.WriteLine($"Log written to {logPath}");
                }
                catch (Exception ex)
                {
                    con.WriteLine("Could not write log: " + BatchRunner.FlattenBatchDetail(ex.Message));
                }
            }
            return fail > 0 ? 1 : 0;
        }

        private struct RenderRow
        {
            public int Page;
            public string Status;
            public long Milliseconds;
            public int Width;
            public int Height;
            public string Detail;
        }

        // Fits each page inside a size-by-size box through PDFium, the same as the
        // image export path (annotations and form fields painted).
        private static IEnumerable<RenderRow> RenderFile(string src, string dstBase, int size, int pageLimit)
        {
            var rows = new List<RenderRow>();
            IDocReader? reader = null;
            int pageCount;
            var open = Stopwatch.StartNew();
            try
            {
                reader = DocLib.Instance.GetDocReader(src, new PageDimensions(size, size));
                pageCount = reader.GetPageCount();
            }
            catch (Exception ex)
            {
                reader?.Dispose();
                rows.Add(new RenderRow { Page = 0, Status = "SKIP", Milliseconds = open.ElapsedMilliseconds,
                    Detail = "open failed: " + BatchRunner.FlattenBatchDetail(ex.Message) });
                return rows;
            }

            using (reader)
            {
                if (pageCount <= 0)
                {
                    rows.Add(new RenderRow { Page = 0, Status = "SKIP", Milliseconds = open.ElapsedMilliseconds,
                        Detail = "no pages" });
                    return rows;
                }
                int last = Math.Min(pageCount, pageLimit);
                for (int idx = 0; idx < last; idx++)
                {
                    var row = new RenderRow { Page = idx, Status = "OK", Detail = string.Empty };
                    var sw = Stopwatch.StartNew();
                    try
                    {
                        byte[] raw; int w, h;
                        using (var pr = reader.GetPageReader(idx))
                        {
                            w = pr.GetPageWidth();
                            h = pr.GetPageHeight();
                            raw = PdfiumInterop.RenderPageWithAnnotations(src, idx, w, h)
                                ?? pr.GetImage(new Docnet.Core.Converters.NaiveTransparencyRemover());
                        }
                        sw.Stop();
                        row.Width = w;
                        row.Height = h;
                        File.WriteAllBytes($"{dstBase}-page-{(idx + 1).ToString(CultureInfo.InvariantCulture).PadLeft(3, '0')}.png",
                            BitmapHelpers.RenderToPng(raw, w, h));
                    }
                    catch (Exception ex)
                    {
                        sw.Stop();
                        row.Status = "FAIL";
                        row.Detail = BatchRunner.FlattenBatchDetail(ex.Message);
                    }
                    row.Milliseconds = sw.ElapsedMilliseconds;
                    rows.Add(row);
                }
            }
            return rows;
        }

        private static string Csv(string s)
        {
            if (s.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0) return s;
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }
    }
}
