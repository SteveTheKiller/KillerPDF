using System.Globalization;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Interop;
using System.Windows.Threading;
using KillerPDF.Services;

namespace KillerPDF.Features
{
    // Scan to PDF from the command line: scans through the scanner's TWAIN driver into a new PDF,
    // without the scanner's settings window unless asked for.
    internal static partial class CliRunner
    {
        private const string ScanUsage =
            "Usage: KillerPDF.exe --scan <out.pdf> [--scanner <name>] [--show-settings]\n"
            + "       KillerPDF.exe --scan --list\n"
            + "  Uses the named scanner, or the one last used for Scan to PDF, or the first one found.\n"
            + "  --show-settings opens the scanner's own settings window; without it the scan starts at once.";

        // ============================================================
        // --scan <out.pdf> [--scanner <name>] [--show-settings] | --scan --list
        // ============================================================
        private static int CliScan(List<string> pos, Dictionary<string, string> options, TextWriter con)
        {
            bool list = options.ContainsKey("--list") && pos.Count == 0;
            if (!list && pos.Count != 1) { con.WriteLine(ScanUsage); return 2; }
            if (!TwainScan.ManagerInstalled)
            {
                con.WriteLine("The 64-bit TWAIN manager (TWAINDSM.dll) is not installed, so no scanner can be reached.");
                return 1;
            }

            // TWAIN talks to the driver through a window's messages, so the scan gets an invisible one
            // and this thread keeps its messages flowing until the scanner is done.
            using var window = new HwndSource(new HwndSourceParameters("KillerPDF scan") { Width = 0, Height = 0, WindowStyle = 0 });
            string folder = Path.Combine(Path.GetTempPath(), string.Create(CultureInfo.InvariantCulture, $"KillerPDF-scan-{DateTime.Now:yyyyMMdd-HHmmss-fff}"));
            bool publishedCompleteScan = false;
            try
            {
                using TwainScan scan = TwainScan.Open(window.Handle);
                IReadOnlyList<NTwain.DataSource> sources = scan.Sources;
                if (list)
                {
                    con.WriteLine(sources.Count == 0 ? "No scanners found." : "Scanners:");
                    foreach (NTwain.DataSource source in sources) con.WriteLine("  " + source.Name);
                    return 0;
                }
                if (sources.Count == 0) { con.WriteLine("No scanners found."); return 1; }

                NTwain.DataSource? chosen;
                if (options.TryGetValue("--scanner", out string? wanted))
                {
                    chosen = sources.FirstOrDefault(source => string.Equals(source.Name, wanted, StringComparison.OrdinalIgnoreCase));
                    if (chosen is null)
                    {
                        con.WriteLine($"No scanner named \"{wanted}\". Run --scan --list to see them.");
                        return 2;
                    }
                }
                else
                {
                    string? last = App.GetSetting("ScanSource");
                    chosen = sources.FirstOrDefault(source => string.Equals(source.Name, last, StringComparison.Ordinal))
                        ?? sources[0];
                }

                string output = Path.GetFullPath(pos[0]);
                con.WriteLine($"Scanning with {chosen.Name}...");
                Task<TwainScanResult> scanning = scan.ScanAsync(chosen, options.ContainsKey("--show-settings"), folder);
                var frame = new DispatcherFrame();
                Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
                scanning.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
                Dispatcher.PushFrame(frame);
                TwainScanResult result = scanning.GetAwaiter().GetResult();
                IReadOnlyList<string> pages = result.Pages;
                if (!result.Completed)
                {
                    con.WriteLine(result.Errors.Count > 0 ? "Scan incomplete." : "Scan canceled.");
                    foreach (Exception error in result.Errors) con.WriteLine(error.Message);
                }
                if (pages.Count == 0) { con.WriteLine("The scanner returned no pages."); return 1; }

                CliEnsureParentDir(output);
                AtomicFileOutput.Write(output, PdfEngineIntegration.MergeFiles(pages));
                con.WriteLine($"Scanned {pages.Count} pages -> {output}");
                publishedCompleteScan = result.Completed;
                return result.Completed ? 0 : 1;
            }
            finally
            {
                try
                {
                    if (Directory.Exists(folder))
                    {
                        if (publishedCompleteScan || !Directory.EnumerateFiles(folder).Any())
                            Directory.Delete(folder, recursive: true);
                        else con.WriteLine($"Recovered scan files are kept in: {folder}");
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }
}
