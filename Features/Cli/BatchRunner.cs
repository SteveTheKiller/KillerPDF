using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using KillerPDF.Services;

namespace KillerPDF.Features
{
    // ============================================================
    // Headless CLI batch mode
    // ============================================================
    //
    // KillerPDF.exe --batch-resave <input.pdf|inputDir> <output.pdf|outputDir> [--log <file.csv>] [--quiet]
    //
    // Resaves one PDF (or every *.pdf under a folder tree, mirroring the
    // relative structure into the output folder) through the same pipeline a
    // GUI save uses: PdfReader.Open(Modify), PdfScrub.ScrubEmptyOutlines,
    // PdfScrub.ScrubDegenerateCropBoxes, PdfScrub.StripLinkAnnotationBorders, Save. No window,
    // no dialogs, no repair fallbacks, no encryption stripping - files that
    // cannot go through the plain Modify pipeline are reported as SKIP with a
    // reason instead of silently faking a result.
    //
    // Built for the veraPDF validation harness (validation/): baseline the
    // corpus, --batch-resave it, validate the output tree, then diff with
    // validation/Compare-VeraPDF.ps1. The claim being tested is "a KillerPDF
    // save does not degrade standards conformance".
    //
    // Exit codes: 0 = every file OK or SKIP, 1 = at least one FAIL
    // (file opened but the save failed), 2 = bad usage or bad paths.
    //
    // Invoked from App.OnStartup BEFORE the single-instance mutex, so a batch
    // run works even while a GUI instance is open and never forwards to it.
    //
    // KillerPDF builds as a GUI-subsystem exe, so it has no console of its
    // own; AttachConsole(-1) latches onto the parent terminal when launched
    // from one. Output interleaves with the prompt (standard GUI-app quirk) -
    // the authoritative record is the --log CSV and the exit code.
    //
    // All static, never touches the window - extracted from MainWindow 2026-07-31.
    internal static class BatchRunner
    {
        [DllImport("kernel32.dll", EntryPoint = "AttachConsole", SetLastError = true)]
        private static extern bool BatchAttachConsole(int dwProcessId);
        private const int BatchAttachParentProcess = -1;

        /// <summary>
        /// Entry point for CLI batch mode. Returns false when args do not
        /// request it (normal GUI launch); otherwise runs the whole batch and
        /// returns true with the process exit code in <paramref name="exitCode"/>.
        /// </summary>
        internal static bool TryRunBatch(string[] args, out int exitCode)
        {
            exitCode = 0;
            int flagIdx = Array.FindIndex(args,
                a => string.Equals(a, "--batch-resave", StringComparison.OrdinalIgnoreCase));
            if (flagIdx < 0) return false;

            var con = OpenBatchConsole();

            // Positional args after the flag: input, output. Options anywhere after the flag.
            string? input = null, output = null, logPath = null;
            bool quiet = false, badUsage = false;
            for (int i = flagIdx + 1; i < args.Length; i++)
            {
                var a = args[i];
                if (string.Equals(a, "--log", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length) logPath = args[++i];
                    else badUsage = true;
                }
                else if (string.Equals(a, "--quiet", StringComparison.OrdinalIgnoreCase))
                {
                    quiet = true;
                }
                else if (input is null)  input  = a;
                else if (output is null) output = a;
                else badUsage = true;   // extra positional arg
            }

            if (badUsage || string.IsNullOrWhiteSpace(input) || string.IsNullOrWhiteSpace(output))
            {
                con.WriteLine("Usage: KillerPDF.exe --batch-resave <input.pdf|inputDir> <output.pdf|outputDir> [--log <file.csv>] [--quiet]");
                exitCode = 2;
                return true;
            }

            try
            {
                exitCode = RunBatchResave(input!, output!, logPath, quiet, con);
            }
            catch (Exception ex)
            {
                con.WriteLine("Batch mode failed: " + FlattenBatchDetail(ex.Message));
                exitCode = 2;
            }
            return true;
        }

        private static int RunBatchResave(string input, string output, string? logPath, bool quiet, TextWriter con)
        {
            // Build the work list: (relative path, source, destination).
            var work = new List<(string Rel, string Src, string Dst)>();

            if (File.Exists(input))
            {
                string dst = Directory.Exists(output)
                    ? Path.Combine(output, Path.GetFileName(input))
                    : output;
                work.Add((Path.GetFileName(input), Path.GetFullPath(input), Path.GetFullPath(dst)));
            }
            else if (Directory.Exists(input))
            {
                string inRoot  = Path.GetFullPath(input).TrimEnd('\\', '/');
                string outRoot = Path.GetFullPath(output).TrimEnd('\\', '/');
                // Snapshot before any output is written, so an output folder nested
                // under the input tree cannot feed the enumeration.
                foreach (var f in Directory.GetFiles(inRoot, "*.pdf", SearchOption.AllDirectories))
                {
                    string rel = f.Substring(inRoot.Length).TrimStart('\\', '/');
                    work.Add((rel, f, Path.Combine(outRoot, rel)));
                }
            }
            else
            {
                con.WriteLine($"Input not found: {input}");
                return 2;
            }

            var log = new List<string> { "File,Status,Detail" };
            int ok = 0, skip = 0, fail = 0;

            foreach (var item in work)
            {
                string status, detail;
                try
                {
                    var dstDir = Path.GetDirectoryName(item.Dst);
                    if (!string.IsNullOrEmpty(dstDir)) Directory.CreateDirectory(dstDir);
                    status = BatchResaveOne(item.Src, item.Dst, out detail);
                }
                catch (Exception ex)
                {
                    status = "FAIL";
                    detail = FlattenBatchDetail(ex.Message);
                }

                if (status == "OK") ok++;
                else if (status == "SKIP") skip++;
                else fail++;

                if (!quiet)
                    con.WriteLine(detail.Length > 0 ? $"{status} {item.Rel} ({detail})" : $"{status} {item.Rel}");
                log.Add($"{BatchCsvField(item.Rel)},{status},{BatchCsvField(detail)}");
            }

            con.WriteLine($"Done. {work.Count} files: {ok} OK, {skip} skipped, {fail} failed.");

            if (!string.IsNullOrWhiteSpace(logPath))
            {
                try
                {
                    File.WriteAllLines(logPath, log, new UTF8Encoding(false));
                    con.WriteLine($"Log written to {logPath}");
                }
                catch (Exception ex)
                {
                    con.WriteLine("Could not write log: " + FlattenBatchDetail(ex.Message));
                }
            }

            return fail > 0 ? 1 : 0;
        }

        /// <summary>
        /// Resaves a single PDF through the standard save pipeline.
        /// Returns "OK", "SKIP" (could not enter the plain Modify pipeline;
        /// reason in <paramref name="detail"/>), or "FAIL" (opened but the
        /// save itself failed - the case the harness exists to catch).
        /// </summary>
        private static string BatchResaveOne(string src, string dst, out string detail)
        {
            detail = string.Empty;

            // The GUI strips encryption at open time (PDFium round-trip) before editing.
            // Batch mode deliberately does not: an encryption strip is not a plain resave,
            // and reporting it as one would poison the conformance comparison.
            try
            {
                if (PdfImport.PdfFileHasEncryption(src))
                {
                    detail = "encrypted - batch mode does not strip encryption";
                    return "SKIP";
                }
            }
            catch (Exception ex)
            {
                detail = "unreadable: " + FlattenBatchDetail(ex.Message);
                return "SKIP";
            }

            PdfDocument doc;
            try
            {
                doc = PdfReader.Open(src, PdfDocumentOpenMode.Modify);
            }
            catch (Exception ex)
            {
                detail = "open failed: " + FlattenBatchDetail(ex.Message);
                return "SKIP";
            }

            try
            {
                // Same pre-save pipeline as SaveInPlace for a document with no user edits.
                PdfScrub.ScrubEmptyOutlines(doc);          // #103: never write a dangling /Outlines reference
                PdfScrub.ScrubDegenerateCropBoxes(doc);    // never write a zero-size /CropBox (Adobe out-of-range)
                PdfScrub.ScrubDeadSignatures(doc);         // a rewrite voids signatures; never ship a dead one (PDF/A 6.4.3)
                PdfScrub.StripLinkAnnotationBorders(doc);  // link borders are stripped on every GUI save
                doc.Save(dst);
                doc.Close();
                return "OK";
            }
            catch (Exception ex)
            {
                try { doc.Close(); } catch { }
                detail = "save failed: " + FlattenBatchDetail(ex.Message);
                return "FAIL";
            }
        }

        // Attaches to the parent terminal's console when launched from one.
        // Returns TextWriter.Null when there is no parent console (e.g. double-click),
        // so batch code can write unconditionally.
        // internal: CliRunner shares the console attach and the CSV detail flattener.
        internal static TextWriter OpenBatchConsole()
        {
            try
            {
                if (BatchAttachConsole(BatchAttachParentProcess))
                    return new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            }
            catch { }
            return TextWriter.Null;
        }

        internal static string FlattenBatchDetail(string? s) =>
            (s ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();

        private static string BatchCsvField(string s)
        {
            if (s.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0) return s;
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }
    }
}
