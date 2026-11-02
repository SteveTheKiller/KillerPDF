using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using KillerPDF.Services;

namespace KillerPDF.Features
{
    // ============================================================
    // Headless CLI render benchmark
    // ============================================================
    //
    // KillerPDF.exe --batch-render <input.pdf|inputDir> <outputDir> [--size <px>] [--pages <n>] [--parallel <n>] [--log <file.csv>] [--quiet] [--allow-incomplete] [--password <p>]
    //
    // Renders the first N pages of one PDF (or every *.pdf under a folder tree)
    // through the same page-render path the viewer, print, flatten, and image
    // export use, scaled to fit inside a --size square (default 1024), and
    // writes one PNG per page mirroring the input tree. The CSV log records
    // milliseconds per page so two builds can be compared on the same input.
    // A PDF whose owner restricts editing gives up its pages only to --password
    // with the owner password; without it the file is a SKIP row, so a corpus
    // run keeps its tally and the pages stay in the file.
    //
    // Exit codes: 0 = every selected file rendered, 1 = a file could not render
    // or the input contained no PDFs or the run itself failed, 2 = bad usage or bad paths.
    internal static class BatchRenderRunner
    {
        private const int DefaultSize = 1024;
        private const int MaximumSize = 8192;

        // Each file worker holds its own render session, decoded images and page bitmap, so
        // peak memory grows with the worker count rather than the core count. These caps keep
        // a default batch near the previous renderer's peak memory while staying faster.
        private const int LargeRenderSize = 1024;
        private const int LargeRenderWorkers = 3;
        private const int DefaultRenderWorkers = 6;

        internal static int DefaultParallelism(int size, int processorCount) =>
            Math.Clamp(processorCount, 1, size > LargeRenderSize ? LargeRenderWorkers : DefaultRenderWorkers);

        internal static bool TryRunBatchRender(string[] args, out int exitCode)
        {
            exitCode = 0;
            int flagIdx = Array.FindIndex(args,
                a => string.Equals(a, "--batch-render", StringComparison.OrdinalIgnoreCase));
            if (flagIdx < 0) return false;

            var con = BatchRunner.OpenBatchConsole();
            CommandLineCrash.Console = con;
            string? input = null, output = null, logPath = null, password = null;
            int size = DefaultSize, pages = 1;
            int parallelism = 0;
            bool quiet = false, badUsage = false, allowIncomplete = false;
            for (int i = flagIdx + 1; i < args.Length; i++)
            {
                var a = args[i];
                if (string.Equals(a, "--log", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length) logPath = args[++i];
                    else badUsage = true;
                }
                else if (string.Equals(a, "--password", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length) password = args[++i];
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
                else if (string.Equals(a, "--parallel", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length && int.TryParse(args[++i], NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out parallelism) && parallelism >= 1)
                        continue;
                    badUsage = true;
                }
                else if (string.Equals(a, "--quiet", StringComparison.OrdinalIgnoreCase))
                {
                    quiet = true;
                }
                else if (string.Equals(a, "--allow-incomplete", StringComparison.OrdinalIgnoreCase))
                {
                    // Release-gate corpus runs compare every page with its baseline, including pages
                    // with omitted content; their diagnostics stay in the log's Detail column.
                    allowIncomplete = true;
                }
                else if (input is null) input = a;
                else if (output is null) output = a;
                else badUsage = true;
            }

            if (badUsage || string.IsNullOrWhiteSpace(input) || string.IsNullOrWhiteSpace(output))
            {
                con.WriteLine("Usage: KillerPDF.exe --batch-render <input.pdf|inputDir> <outputDir> [--size <px>] [--pages <n>] [--parallel <n>] [--log <file.csv>] [--quiet] [--allow-incomplete] [--password <p>]");
                exitCode = 2;
                return true;
            }

            try
            {
                if (parallelism == 0) parallelism = DefaultParallelism(size, Environment.ProcessorCount);
                exitCode = Run(input!, output!, size, pages, parallelism, logPath, quiet, allowIncomplete, password, con);
            }
            catch (Exception ex)
            {
                con.WriteLine("Render batch failed: " + BatchRunner.FlattenBatchDetail(ex.Message));
                exitCode = 1;
            }
            return true;
        }

        private static int Run(string input, string output, int size, int pageLimit,
            int parallelism, string? logPath, bool quiet, bool allowIncomplete, string? password, TextWriter con)
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
                    work.Add((f[inRoot.Length..].TrimStart('\\', '/'), f));
            }
            else
            {
                con.WriteLine($"Input not found: {input}");
                return 2;
            }

            string outRoot = Path.GetFullPath(output).TrimEnd('\\', '/');
            Directory.CreateDirectory(outRoot);
            var log = new List<string> { "File,Page,Status,Milliseconds,Width,Height,Detail,OpenMilliseconds" };
            var tally = new BatchRenderTally();
            long totalMs = 0;
            var total = Stopwatch.StartNew();

            // Structural change: render files in parallel across the ThreadPool. Each file gets
            // its own PdfPageRenderSession so their per-instance caches (font, image, instruction)
            // stay independent. Per-file directory creation is done up-front, single-threaded, to
            // avoid Directory.CreateDirectory races on shared parents. The console and log writes
            // happen under a lock so their line order is coherent, but the render themselves run
            // concurrently. File-level parallelism defaults to DefaultParallelism workers (or --parallel)
            // and each session internally still uses its own row-parallelism cap.
            foreach (var (rel, _) in work)
            {
                string dstBase = Path.Combine(outRoot, rel);
                var dstDir = Path.GetDirectoryName(dstBase);
                if (!string.IsNullOrEmpty(dstDir)) Directory.CreateDirectory(dstDir);
            }

            var perFile = new (string Rel, string Src, List<RenderRow> Rows)[work.Count];
            for (int i = 0; i < work.Count; i++)
                perFile[i] = (work[i].Rel, work[i].Src, new List<RenderRow>());

            // Build the installed-font catalog while the first documents open instead of on the
            // render thread of the first page that needs a non-standard face.
            _ = Task.Run(InstalledFontCatalog.Warm);

            Parallel.For(0, perFile.Length, new ParallelOptions
            {
                MaxDegreeOfParallelism = parallelism
            }, index =>
            {
                string rel = perFile[index].Rel;
                string src = perFile[index].Src;
                string dstBase = Path.Combine(outRoot, rel);
                foreach (var row in RenderFile(src, dstBase, size, pageLimit, allowIncomplete, password))
                    perFile[index].Rows.Add(row);
                // A finished document's source bytes and decoded images stay in the large-object
                // heap until a full collection; collect now so they do not add to the next file's peak.
                GC.Collect();
            });

            foreach (var (rel, _, rows) in perFile)
                foreach (var row in rows)
                {
                    tally.Add(row.Status, row.Incomplete);
                    totalMs += row.Milliseconds;
                    string status = BatchRenderTally.ConsoleStatus(row.Status, row.Incomplete);
                    if (!quiet)
                        con.WriteLine(row.Detail.Length > 0
                            ? $"{status} {rel} p{row.Page + 1} {row.Milliseconds} ms ({row.Detail})"
                            : $"{status} {rel} p{row.Page + 1} {row.Milliseconds} ms");
                    log.Add(string.Join(",", Csv(rel), (row.Page + 1).ToString(CultureInfo.InvariantCulture),
                        row.Status, row.Milliseconds.ToString(CultureInfo.InvariantCulture),
                        row.Width.ToString(CultureInfo.InvariantCulture),
                        row.Height.ToString(CultureInfo.InvariantCulture), Csv(row.Detail),
                        row.OpenMilliseconds.ToString(CultureInfo.InvariantCulture)));
                }

            total.Stop();
            con.WriteLine(tally.Summary(work.Count, totalMs, total.ElapsedMilliseconds));
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
            return tally.ExitCode(work.Count);
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
            public bool Incomplete;
        }

        // Fits each page inside a size-by-size box through the engine render session,
        // the same as the image export path (annotations and form fields painted).
        private static List<RenderRow> RenderFile(string src, string dstBase, int size, int pageLimit,
            bool allowIncomplete, string? password)
        {
            var rows = new List<RenderRow>();
            PdfPageRenderSession session;
            int pageCount;
            var open = Stopwatch.StartNew();
            try
            {
                // The gate reads a plain file no further than the open does, so two builds still
                // compare, and nothing renders before it has answered.
                if (string.IsNullOrEmpty(password))
                {
                    session = PdfPageRenderSession.OpenEngineFirst(src, size, size, null);
                    if (session.IsEncrypted && CliProtectionGate.BatchRender(src, null).Refusal is string refusal)
                    {
                        session.Dispose();
                        return Skipped(refusal);
                    }
                }
                else
                {
                    (string? refusal, string? readWith) = CliProtectionGate.BatchRender(src, password);
                    if (refusal is not null) return Skipped(refusal);
                    session = readWith is null
                        ? PdfPageRenderSession.OpenEngineFirst(src, size, size, null)
                        : PdfPageRenderSession.OpenEngineFirstWithPassword(src, size, size, readWith);
                }
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
                        var rendered = session.RenderPageForEncoding(idx, requireComplete: !allowIncomplete);
                        sw.Stop();
                        row.Width = rendered.Width;
                        row.Height = rendered.Height;
                        row.Detail = string.Join(" ", rendered.Diagnostics);
                        row.Incomplete = PdfRenderCompleteness.Classify(rendered.Diagnostics)
                            .Any(diagnostic => diagnostic.BlocksPublication);
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

            List<RenderRow> Skipped(string detail)
            {
                rows.Add(new RenderRow { Page = 0, Status = "SKIP", Milliseconds = open.ElapsedMilliseconds,
                    OpenMilliseconds = open.ElapsedMilliseconds, Detail = BatchRunner.FlattenBatchDetail(detail) });
                return rows;
            }
        }

        // File names and renderer details can carry text a spreadsheet would run as a formula.
        private static string Csv(string s) => KillerPdf.Engine.Documents.PdfCsv.Field(s, alwaysQuote: false);
    }
}
