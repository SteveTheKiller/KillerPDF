using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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
            var log = new List<string> { "File,Page,Status,Milliseconds,Width,Height,Detail,OpenMilliseconds" };
            int ok = 0, skip = 0, fail = 0;
            long totalMs = 0;
            var total = Stopwatch.StartNew();

            // Structural change: render files in parallel across the ThreadPool. Each file gets
            // its own PdfPageRenderSession so their per-instance caches (font, image, instruction)
            // stay independent. Per-file directory creation is done up-front, single-threaded, to
            // avoid Directory.CreateDirectory races on shared parents. The console and log writes
            // happen under a lock so their line order is coherent, but the render themselves run
            // concurrently. File-level parallelism uses ProcessorCount workers and each session
            // internally still uses its own row-parallelism cap, so total in-flight thread count
            // grows: acceptable for a batch benchmark, where wall-clock throughput is the goal.
            foreach (var (rel, _) in work)
            {
                string dstBase = Path.Combine(outRoot, rel);
                var dstDir = Path.GetDirectoryName(dstBase);
                if (!string.IsNullOrEmpty(dstDir)) Directory.CreateDirectory(dstDir);
            }

            var perFile = new (string Rel, string Src, List<RenderRow> Rows)[work.Count];
            for (int i = 0; i < work.Count; i++)
                perFile[i] = (work[i].Rel, work[i].Src, new List<RenderRow>());

            int parallelism = Math.Max(1, Environment.ProcessorCount);
            Parallel.For(0, perFile.Length, new ParallelOptions
            {
                MaxDegreeOfParallelism = parallelism
            }, index =>
            {
                string rel = perFile[index].Rel;
                string src = perFile[index].Src;
                string dstBase = Path.Combine(outRoot, rel);
                foreach (var row in RenderFile(src, dstBase, size, pageLimit))
                    perFile[index].Rows.Add(row);
            });

            foreach (var (rel, _, rows) in perFile)
                foreach (var row in rows)
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
                        row.Height.ToString(CultureInfo.InvariantCulture), Csv(row.Detail),
                        row.OpenMilliseconds.ToString(CultureInfo.InvariantCulture)));
                }

            total.Stop();
            con.WriteLine($"Done. {work.Count} files, {ok} pages OK, {skip} skipped, {fail} failed, {totalMs} ms summed page rendering, {total.ElapsedMilliseconds} ms elapsed.");
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
            public long OpenMilliseconds;
            public int Width;
            public int Height;
            public string Detail;
        }

        // Fits each page inside a size-by-size box through the engine render session,
        // the same as the image export path (annotations and form fields painted).
        private static IEnumerable<RenderRow> RenderFile(string src, string dstBase, int size, int pageLimit)
        {
            var rows = new List<RenderRow>();
            PdfPageRenderSession session;
            int pageCount;
            var open = Stopwatch.StartNew();
            try
            {
                session = PdfPageRenderSession.OpenEngineFirst(src, size, size);
                pageCount = session.PageCount;
            }
            catch (Exception ex)
            {
                rows.Add(new RenderRow { Page = 0, Status = "SKIP", Milliseconds = open.ElapsedMilliseconds,
                    OpenMilliseconds = open.ElapsedMilliseconds,
                    Detail = "open failed: " + BatchRunner.FlattenBatchDetail(ex.Message) });
                return rows;
            }

            open.Stop();
            long openMs = open.ElapsedMilliseconds;

            using (session)
            {
                if (pageCount <= 0)
                {
                    rows.Add(new RenderRow { Page = 0, Status = "SKIP", Milliseconds = openMs,
                        OpenMilliseconds = openMs, Detail = "no pages" });
                    return rows;
                }
                int last = Math.Min(pageCount, pageLimit);
                for (int idx = 0; idx < last; idx++)
                {
                    // Open time is attributed to the first page so a per-file sum stays exact.
                    var row = new RenderRow { Page = idx, Status = "OK", Detail = string.Empty,
                        OpenMilliseconds = idx == 0 ? openMs : 0 };
                    var sw = Stopwatch.StartNew();
                    try
                    {
                        var rendered = session.RenderPageForEncoding(idx);
                        sw.Stop();
                        row.Width = rendered.Width;
                        row.Height = rendered.Height;
                        row.Detail = string.Join(" ", rendered.Diagnostics);
                        using var output = File.Create($"{dstBase}-page-{(idx + 1).ToString(CultureInfo.InvariantCulture).PadLeft(3, '0')}.png");
                        BitmapHelpers.WritePng(output, rendered.Pixels, rendered.Width, rendered.Height);
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
