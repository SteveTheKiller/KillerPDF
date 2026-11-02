using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Printing;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Diagnostics;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Writing;
using KillerPDF.Services;
// The scrubs, bitmap helpers, and import helpers all live in Services
// (BitmapHelpers.cs and PdfImport.cs; KillerUI refactor,
// 2026-07-31), called qualified below. No Features-to-Shell reaches remain in this file.
// OpenBatchConsole and FlattenBatchDetail are shared with the batch runner.
using static KillerPDF.Features.BatchRunner;

namespace KillerPDF.Features
{
    // ============================================================
    // Command-line interface
    // ============================================================
    //
    // Dispatcher for every headless CLI command. Invoked from App.OnStartup
    // BEFORE the single-instance mutex, so CLI runs work while a GUI instance
    // is open, never forward to it, and never show a window. A launch with no
    // recognized command flag falls through to the normal GUI (including the
    // classic "KillerPDF.exe file.pdf" file-association open).
    //
    // Each command reuses the same pipeline its GUI equivalent runs - the
    // merge named-destination rewrite, the pre-save scrubs, the engine
    // decrypt, the rotation-safe rasterizer, the OCR text-layer builder - so
    // CLI output is byte-for-byte the kind of file the GUI would produce.
    //
    // Exit codes: 0 = success, 1 = operation failed, 2 = bad usage.
    //
    // Console output rides on AttachConsole (GUI-subsystem exe, see
    // BatchMode.cs); lines can interleave with the shell prompt. Exit codes
    // are the scripting contract.
    //
    // Every member here is static and none of them touch the window, so this was never really a
    // MainWindow partial - it just happened to be declared as one. Extracted to its own class
    // 2026-07-31.
    internal static partial class CliRunner
    {
        // Options that consume the next argument as their value.
        private static readonly string[] CliValueOptions =
        [
            "--log", "--dpi", "--format", "--pages", "--printer", "--lang", "--password", "--copies", "--duplex",
            "--color-mode", "--threshold", "--compression", "--jpeg-quality",
            "--every", "--parts", "--bookmarks", "--max-size", "--name", "--strip",
            "--template", "--edge", "--align", "--number-format", "--font-size",
            "--start", "--digits", "--prefix", "--suffix", "--profile", "--data",
            "--sheet", "--margin", "--gutter", "--creep", "--binding", "--grid",
            "--min-size", "--depth", "--pattern", "--width", "--height", "--images",
            "--direction", "--position",
            "--title", "--zoom", "--layout", "--panel", "--page", "--hide-toolbar", "--hide-menu",
            "--hide-window-ui", "--fit-window", "--center-window", "--show-title", "--visible", "--locked",
            "--description", "--relationship", "--rename", "--mime", "--view", "--initial",
            "--user-password", "--owner-password", "--allow", "--recipients", "--text", "--terms",
            "--regex", "--find", "--whole-pages", "--fill", "--overlay", "--sanitize",
            "--pfx", "--pfx-password", "--cert", "--reason", "--location", "--contact", "--timestamp-server",
            "--appearance", "--appearance-text", "--scanned-look", "--scanner", "--review", "--keep-scanned",
            "--author", "--subject", "--keywords", "--creator", "--x", "--y", "--box",
        ];

        /// <summary>
        /// Entry point for all CLI commands. Returns false when args carry no
        /// recognized command (normal GUI launch); otherwise runs the command
        /// and returns true with the process exit code set.
        /// </summary>
        internal static bool TryRunCli(string[] args, out int exitCode)
        {
            exitCode = 0;
            if (args is null || args.Length == 0) return false;
            args = KillerFamily.CommandLine.CommandLineEntry.GetArguments(args,
            [
                "batch-resave", "batch-render", "help", "version", "verify", "merge",
                "extract-pages", "split", "optimize", "number-pages", "bates", "preflight",
                "accessibility", "attachments", "booklet", "nup", "auto-bookmarks", "decrypt",
                "to-image", "flatten", "print", "ocr", "scan-to-text", "rotate-pages",
                "delete-pages", "move-pages", "insert-blank", "duplicate-page", "document-info", "search-text",
                "reverse-pages", "fix-orientation", "straighten", "divide-spreads", "rasterize-region", "print-production",
                "signatures", "layers", "portfolio", "comments", "navigation-audit", "compare-structure",
                "measurements", "separations", "repair-links", "toc", "page-labels", "initial-view",
                "set-layer", "add-attachment", "edit-attachment", "portfolio-view", "redact", "protect",
                "export", "convert-office", "data-merge", "replace-text", "export-form-data", "import-form-data",
                "recognize-fields", "xfa-to-acroform", "flatten-forms", "macro", "sign", "scan",
                "set-metadata", "set-bookmarks", "add-comment", "delete-comments", "crop"
            ], "--") ?? args;

            // The validation resave keeps its dedicated runner in BatchMode.cs.
            // Only the command position counts, so an option value can say anything.
            if (args.Length > 0 && Eq(args[0], "--batch-resave"))
                return BatchRunner.TryRunBatch(args, out exitCode);
            if (args.Length > 0 && Eq(args[0], "--batch-render"))
                return BatchRenderRunner.TryRunBatchRender(args, out exitCode);

            string? command = args.FirstOrDefault(a =>
                Eq(a, "--help") || Eq(a, "-h") || Eq(a, "/?") ||
                Eq(a, "--version") || Eq(a, "-v") ||
                Eq(a, "--verify") || Eq(a, "/verify") ||
                Eq(a, "--merge") || Eq(a, "--extract-pages") || Eq(a, "--split") ||
                Eq(a, "--optimize") || Eq(a, "--number-pages") || Eq(a, "--bates") ||
                Eq(a, "--preflight") || Eq(a, "--accessibility") || Eq(a, "--attachments") ||
                Eq(a, "--booklet") || Eq(a, "--nup") || Eq(a, "--auto-bookmarks") ||
                Eq(a, "--decrypt") || Eq(a, "--to-image") || Eq(a, "--flatten") ||
                Eq(a, "--print") || Eq(a, "--ocr") || Eq(a, "--scan-to-text") || Eq(a, "--rotate-pages") ||
                Eq(a, "--delete-pages") || Eq(a, "--move-pages") || Eq(a, "--insert-blank") ||
                Eq(a, "--duplicate-page") || Eq(a, "--document-info") || Eq(a, "--search-text") ||
                Eq(a, "--reverse-pages") || Eq(a, "--fix-orientation") || Eq(a, "--straighten") ||
                Eq(a, "--divide-spreads") || Eq(a, "--rasterize-region") ||
                Eq(a, "--print-production") || Eq(a, "--signatures") || Eq(a, "--layers") ||
                Eq(a, "--portfolio") || Eq(a, "--comments") || Eq(a, "--navigation-audit") ||
                Eq(a, "--compare-structure") || Eq(a, "--measurements") || Eq(a, "--separations") ||
                Eq(a, "--repair-links") || Eq(a, "--toc") || Eq(a, "--page-labels") ||
                Eq(a, "--set-metadata") || Eq(a, "--set-bookmarks") || Eq(a, "--add-comment") ||
                Eq(a, "--delete-comments") || Eq(a, "--crop") ||
                Eq(a, "--initial-view") || Eq(a, "--set-layer") || Eq(a, "--add-attachment") ||
                Eq(a, "--edit-attachment") || Eq(a, "--portfolio-view") ||
                Eq(a, "--redact") || Eq(a, "--protect") || Eq(a, "--export") || Eq(a, "--convert-office") ||
                Eq(a, "--data-merge") || Eq(a, "--replace-text") || Eq(a, "--export-form-data") || Eq(a, "--import-form-data") ||
                Eq(a, "--recognize-fields") || Eq(a, "--xfa-to-acroform") || Eq(a, "--flatten-forms") ||
                Eq(a, "--macro") || Eq(a, "--sign") || Eq(a, "--scan"));
            if (command is null)
            {
                // A mistyped switch never reaches a window, where a script would wait on it, or a
                // running copy, which would be handed the switch as a file path.
                if (!CliProtectionGate.IsUnknownSwitch(args)) return false;
                OpenBatchConsole().WriteLine(CliHelpText());
                exitCode = 2;
                return true;
            }

            var con = OpenBatchConsole();
            CommandLineCrash.Console = con;
            var (positionals, options) = ParseCliArgs(args, command);
            if (options.ContainsKey(CliSecrets.Option))
            {
                // UTF-8, not the console code page, so passwords outside ASCII arrive intact.
                using var secretInput = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
                string? problem = CliSecrets.Read(secretInput, options);
                if (problem is not null)
                {
                    con.WriteLine(problem);
                    exitCode = 2;
                    return true;
                }
            }

            try
            {
                if (CliProtectionGate.VerbRefusal(command, positionals, options) is string refusal)
                {
                    con.WriteLine(refusal);
                    exitCode = 1;
                    return true;
                }
                switch (command.ToLowerInvariant())
                {
                    case "--help":
                    case "-h":
                    case "/?":
                        con.WriteLine(CliHelpText());
                        break;
                    case "--version":
                    case "-v":
                        con.WriteLine(AppVersion.Display);
                        break;
                    case "--verify":
                    case "/verify":
                        exitCode = CliVerifyPayload(con);
                        break;
                    case "--merge":
                        exitCode = CliMerge(positionals, con);
                        break;
                    case "--extract-pages":
                        exitCode = CliExtractPages(positionals, con);
                        break;
                    case "--split":
                        exitCode = CliSplit(positionals, options, con);
                        break;
                    case "--optimize":
                        exitCode = CliOptimize(positionals, options, con);
                        break;
                    case "--booklet":
                        exitCode = CliImpose(positionals, options, con, booklet: true);
                        break;
                    case "--nup":
                        exitCode = CliImpose(positionals, options, con, booklet: false);
                        break;
                    case "--auto-bookmarks":
                        exitCode = CliAutoBookmarks(positionals, options, con);
                        break;
                    case "--preflight":
                        exitCode = CliPreflight(positionals, options, con);
                        break;
                    case "--accessibility":
                        exitCode = CliAccessibility(positionals, options, con);
                        break;
                    case "--attachments":
                        exitCode = CliAttachments(positionals, options, con);
                        break;
                    case "--number-pages":
                        exitCode = CliNumberPages(positionals, options, con);
                        break;
                    case "--bates":
                        exitCode = CliBates(positionals, options, con);
                        break;
                    case "--decrypt":
                        exitCode = CliDecrypt(positionals, options, con);
                        break;
                    case "--to-image":
                        exitCode = CliToImage(positionals, options, con);
                        break;
                    case "--flatten":
                        exitCode = CliFlatten(positionals, options, con);
                        break;
                    case "--print":
                        exitCode = CliPrint(positionals, options, con);
                        break;
                    case "--ocr":
                        exitCode = CliOcr(positionals, options, con);
                        break;
                    case "--scan-to-text":
                        exitCode = CliScanToText(positionals, options, con);
                        break;
                    case "--rotate-pages":
                        exitCode = CliRotatePages(positionals, con);
                        break;
                    case "--delete-pages":
                        exitCode = CliDeletePages(positionals, con);
                        break;
                    case "--move-pages":
                        exitCode = CliMovePages(positionals, con);
                        break;
                    case "--insert-blank":
                        exitCode = CliInsertBlank(positionals, options, con);
                        break;
                    case "--duplicate-page":
                        exitCode = CliDuplicatePage(positionals, con);
                        break;
                    case "--document-info":
                        exitCode = CliDocumentInfo(positionals, con);
                        break;
                    case "--search-text":
                        exitCode = CliSearchText(positionals, con);
                        break;
                    case "--reverse-pages":
                        exitCode = CliReversePages(positionals, options, con);
                        break;
                    case "--fix-orientation":
                        exitCode = CliFixOrientation(positionals, options, con);
                        break;
                    case "--straighten":
                        exitCode = CliStraighten(positionals, options, con);
                        break;
                    case "--divide-spreads":
                        exitCode = CliDivideSpreads(positionals, options, con);
                        break;
                    case "--rasterize-region":
                        exitCode = CliRasterizeRegion(positionals, options, con);
                        break;
                    case "--print-production":
                    case "--signatures":
                    case "--layers":
                    case "--portfolio":
                    case "--comments":
                    case "--navigation-audit":
                        exitCode = CliDocumentReport(command.ToLowerInvariant(), positionals, options, con);
                        break;
                    case "--compare-structure":
                        exitCode = CliCompareStructure(positionals, options, con);
                        break;
                    case "--measurements":
                        exitCode = CliMeasurements(positionals, options, con);
                        break;
                    case "--separations":
                        exitCode = CliSeparations(positionals, options, con);
                        break;
                    case "--repair-links":
                        exitCode = CliRepairLinks(positionals, options, con);
                        break;
                    case "--toc":
                        exitCode = CliTableOfContents(positionals, options, con);
                        break;
                    case "--page-labels":
                        exitCode = CliPageLabels(positionals, options, con);
                        break;
                    case "--set-metadata":
                        exitCode = CliSetMetadata(positionals, options, con);
                        break;
                    case "--set-bookmarks":
                        exitCode = CliSetBookmarks(positionals, options, con);
                        break;
                    case "--add-comment":
                        exitCode = CliAddComment(positionals, options, con);
                        break;
                    case "--delete-comments":
                        exitCode = CliDeleteComments(positionals, options, con);
                        break;
                    case "--crop":
                        exitCode = CliCrop(positionals, options, con);
                        break;
                    case "--initial-view":
                        exitCode = CliInitialView(positionals, options, con);
                        break;
                    case "--set-layer":
                        exitCode = CliSetLayer(positionals, options, con);
                        break;
                    case "--add-attachment":
                        exitCode = CliAddAttachment(positionals, options, con);
                        break;
                    case "--edit-attachment":
                        exitCode = CliEditAttachment(positionals, options, con);
                        break;
                    case "--portfolio-view":
                        exitCode = CliPortfolioView(positionals, options, con);
                        break;
                    case "--redact":
                        exitCode = CliRedact(positionals, options, con);
                        break;
                    case "--protect":
                        exitCode = CliProtect(positionals, options, con);
                        break;
                    case "--export":
                        exitCode = CliExport(positionals, options, con);
                        break;
                    case "--convert-office":
                        exitCode = CliConvertOffice(positionals, con);
                        break;
                    case "--data-merge":
                        exitCode = CliDataMerge(positionals, options, con);
                        break;
                    case "--replace-text":
                        exitCode = CliReplaceText(positionals, options, con);
                        break;
                    case "--export-form-data":
                        exitCode = CliExportFormData(positionals, options, con);
                        break;
                    case "--import-form-data":
                        exitCode = CliImportFormData(positionals, options, con);
                        break;
                    case "--recognize-fields":
                        exitCode = CliRecognizeFields(positionals, options, con);
                        break;
                    case "--xfa-to-acroform":
                        exitCode = CliXfaToAcroForm(positionals, options, con);
                        break;
                    case "--flatten-forms":
                        exitCode = CliFlattenForms(positionals, con);
                        break;
                    case "--macro":
                        exitCode = CliMacro(positionals, options, con);
                        break;
                    case "--sign":
                        exitCode = CliSign(positionals, options, con);
                        break;
                    case "--scan":
                        exitCode = CliScan(positionals, options, con);
                        break;
                }
            }
            catch (CliUsageException ex)
            {
                con.WriteLine(FlattenBatchDetail(ex.Message));
                exitCode = 2;
            }
            catch (Exception ex)
            {
                con.WriteLine("Error: " + FlattenBatchDetail(CliProtectionGate.FailureMessage(ex.Message, positionals)));
                exitCode = 1;
            }
            finally
            {
                App.CleanupSessionTemps();   // drop any decrypt/rotation temps the run created
            }
            return true;
        }

        private static bool Eq(string a, string b) =>
            string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        private static string CliHelpText() => string.Join(Environment.NewLine,
        [
            "KillerPDF " + AppVersion.Display + " command line usage",
            "Existing --command forms stay compatible.",
            "",
            "  killerpdf <file.pdf>                                    open in the app",
            "  killerpdf version | -v                                print version",
            "  killerpdf help | -h | /?                              this text",
            "  killerpdf verify | /verify                            verify every installed payload file",
            "",
            "  killerpdf merge <out.pdf> <in1> <in2> ...        merge PDFs (and images) into one PDF",
            "  killerpdf extract-pages <in.pdf> <pages> <out.pdf>",
            "                                           pull pages into a new PDF (pages like 1-3,5,9-12)",
            "  killerpdf split <in.pdf> <outDir>                write one PDF per page",
            "                                           [--every <n>] fixed page count per part",
            "                                           [--parts <n>] equal parts",
            "                                           [--bookmarks <level>] a part per bookmark at that level",
            "                                           [--max-size <n[KB|MB]>] grow parts up to a size budget",
            "                                           [--name <template>] {name} {index} {first} {last} {title}",
            "                                           [--overwrite] replace existing parts, never the source",
            "  killerpdf optimize <in.pdf> <out.pdf> [--strip <list>] [--images <dpi>] [--jpeg-quality <n>]",
            "                                           [--invalidate-signatures] [--report] [--password <p>]",
            "                                           reduce file size, keeping any password protection;",
            "                                           --images resamples images drawn",
            "                                           above 1.5 times that resolution; strip takes any of metadata,",
            "                                           attachments, javascript, comments, thumbnails,",
            "                                           bookmarks, forms, xfa, openaction, layers;",
            "                                           a signed file or one with Reader usage rights is refused",
            "                                           unless --invalidate-signatures gives those up",
            "  killerpdf number-pages <in.pdf> <out.pdf> [--template <text>] [--pages <range>]",
            "                                           [--edge header|footer] [--align left|center|right]",
            "                                           [--number-format decimal|upper-roman|lower-roman|",
            "                                           upper-letters|lower-letters] [--font-size <n>]",
            "                                           tokens: {page} {pages} {label} {filename} {title}",
            "                                           {author} {date}",
            "  killerpdf bates <outDir> <in1.pdf> [in2.pdf ...] [--start <n>] [--digits <n>]",
            "                                           [--prefix <text>] [--suffix <text>] [--edge ...]",
            "                                           [--align ...] [--font-size <n>]",
            "                                           stamp one continuous Bates sequence across files",
            "  killerpdf booklet <in.pdf> <out.pdf> [--sheet <name|WxH>] [--margin <n>] [--gutter <n>]",
            "                                           [--creep <n>] [--binding long|short] [--marks]",
            "                                           two-up saddle-stitch imposition",
            "  killerpdf nup <in.pdf> <out.pdf> --grid <cols>x<rows> [--sheet <name|WxH>] [--margin <n>]",
            "                                           [--gutter <n>] [--marks]   place several pages per sheet",
            "  killerpdf auto-bookmarks <in.pdf> <out.pdf> [--min-size <pt>] [--depth <n>] [--pattern <regex>]",
            "                                           build an outline from detected headings; exit 3 when no headings are found",
            "  killerpdf preflight <in.pdf> [--profile general|attachments|print|<profile.json>]",
            "                                           [--json] [--password <p>]   exit 3 when findings",
            "  killerpdf accessibility <in.pdf> [--json]        report accessibility findings; exit 3 when found",
            "  killerpdf attachments <in.pdf> [<outDir>] [--json] [--password <p>]",
            "                                           list embedded files, or extract them to a folder",
            "  killerpdf decrypt <in.pdf> <out.pdf> [--password <p>]",
            "                                           remove encryption (lossless when possible); a PDF that",
            "                                           declares a limit takes the owner password",
            "  killerpdf to-image <in.pdf> <outDir> [--dpi <n>] [--format png|jpg] [--pages <range>] [--transparent] [--overwrite]",
            "                                           render pages to images (default 150 dpi, png;",
            "                                           background composites to white unless --transparent, png only)",
            "  killerpdf flatten <in.pdf> <out.pdf> [--dpi <n>] [--color-mode color|grayscale|black-white]",
            "                                           [--threshold <0-255>] [--compression lossless|jpeg]",
            "                                           [--jpeg-quality <1-100>] [--scanned-look all|tilt,paper,grain,soft-focus,shadow]",
            "                                           rasterize into an uneditable PDF, optionally looking scanned",
            "  killerpdf print <in.pdf> [--printer <name>] [--pages <range>] [--copies <n>]",
            "                                           [--duplex none|long|short] [--reverse]",
            "                                           print silently (default printer if none named)",
            "  --password <p> works on preflight, decrypt, to-image, flatten, print, ocr, scan-to-text, fix-orientation,",
            "                                           straighten, separations, attachments, export, comments, measurements,",
            "                                           recognize-fields, macro and batch-render;",
            "                                           other commands read a PDF that opens with no password. On a PDF with",
            "                                           limits, new PDFs and page images need printing and copying allowed,",
            "                                           text and data need copying allowed, an edit-forbidden PDF gives up",
            "                                           nothing, and the owner password lifts all of that",
            "  killerpdf scan <out.pdf> [--scanner <name>] [--show-settings]",
            "                                           scan through a TWAIN scanner into a PDF; --scan --list shows scanners",
            "  killerpdf ocr <in.pdf> <out.pdf> [--lang <code>] add an invisible searchable text layer (default eng;",
            "                                           other languages download on first use)",
            "  killerpdf scan-to-text <in.pdf> <out.pdf> [--lang <code>] [--review <folder>] [--keep-scanned <file>]",
            "                                           turn scanned pages into real text in a matching typeface;",
            "                                           --review writes each word as scanned beside it as text",
            "  killerpdf rotate-pages <in.pdf> <pages> <degrees> <out.pdf>",
            "                                           rotate selected pages by 90, 180, or 270 degrees",
            "  killerpdf delete-pages <in.pdf> <pages> <out.pdf> remove selected pages",
            "  killerpdf move-pages <in.pdf> <pages> <position> <out.pdf>",
            "                                           move pages before a one-based position",
            "  killerpdf insert-blank <in.pdf> <position> <out.pdf> [--width <pt>] [--height <pt>]",
            "                                           insert a blank page before a one-based position",
            "  killerpdf duplicate-page <in.pdf> <page> <out.pdf> duplicate one page",
            "  killerpdf reverse-pages <in.pdf> <out.pdf> [--pages <range>]",
            "                                           reverse the order of all or the selected pages",
            "  killerpdf fix-orientation <in.pdf> <out.pdf> [--pages <range>] [--lang <code>]",
            "                                           turn pages to read upright (OCR reads scans)",
            "  killerpdf straighten <in.pdf> <out.pdf> [--pages <range>]",
            "                                           level tilted scans that have no text layer",
            "  killerpdf divide-spreads <in.pdf> <out.pdf> [--pages <range>] [--direction vertical|horizontal]",
            "                                           [--position <0.1-0.9>]   split two-page spreads",
            "  killerpdf rasterize-region <in.pdf> <page> <left,bottom,right,top> <out.pdf> [--dpi <n>]",
            "                                           turn one box (points, unrotated page) into an image",
            "  killerpdf print-production <in.pdf> [--json]    page boxes, inks and output intents",
            "  killerpdf signatures <in.pdf> [--json]          each signature's signer, time, trust and changes",
            "  killerpdf layers <in.pdf> [--json]              list layers and their visibility and locks",
            "  killerpdf set-layer <in.pdf> <out.pdf> <layer|#object> [--name <new>] [--visible on|off] [--locked on|off]",
            "  killerpdf portfolio <in.pdf> [--json]           portfolio presentation, fields and folders",
            "  killerpdf portfolio-view <in.pdf> <out.pdf> (--view details|tile|hidden [--initial <name>] | --clear)",
            "  killerpdf add-attachment <in.pdf> <out.pdf> <file> [<file> ...] [--description <text>]",
            "                                           [--relationship data|source|alternative|supplement|unspecified]",
            "  killerpdf edit-attachment <in.pdf> <out.pdf> <name> [--rename <new>] [--description <text>]",
            "                                           [--mime <type>] [--relationship <name>] [--remove]",
            "  killerpdf comments <in.pdf> [--json] [--password <p>]",
            "                                           list review comments and their replies",
            "  killerpdf measurements <in.pdf> [--json | --csv] [--password <p>]",
            "                                           report the saved measurements",
            "  killerpdf separations <in.pdf> [<outDir>] [--pages <range>] [--dpi <n>] [--json] [--overwrite]",
            "                                           list inks, or write each page's plates as images",
            "  killerpdf compare-structure <original.pdf> <changed.pdf> [--json]",
            "                                           compare content, resources and geometry; exit 3 when they differ",
            "  killerpdf navigation-audit <in.pdf> [--json]    check links, bookmarks and destinations; exit 3 when found",
            "  killerpdf repair-links <in.pdf> <out.pdf> [--unsafe] [--unresolved]",
            "                                           remove unsafe or broken links (both by default)",
            "  killerpdf toc <in.pdf> <out.pdf> [--title <text>] [--depth <1-6>]",
            "                                           insert clickable contents pages built from the bookmarks",
            "  killerpdf page-labels <in.pdf> <out.pdf> <page:style[:prefix[:start]],...>",
            "                                           set page-label ranges, such as 1:lower-roman,5:decimal",
            "  killerpdf initial-view <in.pdf> <out.pdf> [--page <n>] [--zoom fit-page|fit-width|fit-height|actual|<percent>]",
            "                                           [--layout <name>] [--panel <name>] [--hide-toolbar on|off]",
            "                                           [--hide-menu on|off] [--hide-window-ui on|off] [--fit-window on|off]",
            "                                           [--center-window on|off] [--show-title on|off]",
            "  killerpdf redact <in.pdf> <out.pdf> [--text <phrase>] [--terms <file>] [--regex <pattern>]",
            "                                           [--find email,phone,ssn,card,iban] [--whole-pages <range>] [--marked]",
            "                                           [--match-case] [--fill <color>] [--overlay <text>] [--rasterize]",
            "                                           [--sanitize all|<list>] [--dry-run]   permanently remove content;",
            "                                           exit 3 and no output when pages could not be read",
            "  killerpdf protect <in.pdf> <out.pdf> [--user-password <p>] [--owner-password <p>] [--allow print,copy,edit]",
            "                                           [--recipients <cert.cer>,...] [--invalidate-signatures]   AES-256 copy",
            "  killerpdf sign <in.pdf> <out.pdf> (--pfx <file> [--pfx-password <p>] | --cert <thumbprint or name>)",
            "                                           [--reason <text>] [--location <text>] [--contact <text>]",
            "                                           [--timestamp] [--timestamp-server <url>] [--appearance <page>[:<corner>]]",
            "                                           [--appearance-text <text>] [--width <pt>] [--height <pt>] [--font-size <pt>]",
            "                                           add a digital signature, optionally timestamped and visible",
            "  killerpdf export <in.pdf> <out.docx|.xlsx|.pptx|.html|.md|.txt|.json> [--pages <range>] [--report] [--password <p>]",
            "                                           export to Office, web and text formats",
            "  killerpdf convert-office <in.docx|.xlsx|.doc|.xls> <out.pdf>",
            "                                           convert a Word or Excel file to PDF",
            "  killerpdf data-merge <template.pdf> <data.csv|.json|.xlsx> <outDir> [--name <file name>] [--flatten] [--overwrite]",
            "                                           fill one copy of the form per record",
            "  killerpdf replace-text <in.pdf> <out.pdf> <find> <replace> [--match-case] [--whole-word]",
            "                                           rewrite matching lines in their own fonts",
            "  killerpdf export-form-data <in.pdf> <out.fdf|.xfdf|.json> [--no-comments]",
            "  killerpdf import-form-data <in.pdf> <data.fdf|.xfdf> <out.pdf> [--flatten]",
            "  killerpdf recognize-fields <in.pdf> [<out.pdf>] [--json] [--password <p>]",
            "                                           find form fields; with an output, add the text fields and check boxes",
            "  killerpdf xfa-to-acroform <in.pdf> <out.pdf> [--flatten]   convert an XFA form to standard fields",
            "  killerpdf flatten-forms <in.pdf> <out.pdf>      turn form fields into page content",
            "  killerpdf set-metadata <in.pdf> <out.pdf> [--title <t>] [--author <a>] [--subject <s>] [--keywords <k>] [--creator <c>]",
            "                                           change document properties; fields not named stay as they are",
            "  killerpdf set-bookmarks <in.pdf> <out.pdf> <bookmarks.json> [--replace]",
            "                                           import bookmarks from a bookmark data file",
            "  killerpdf add-comment <in.pdf> <out.pdf> --page <n> --text <t> [--x <pt>] [--y <pt>] [--author <name>]",
            "  killerpdf delete-comments <in.pdf> <out.pdf> [--pages <range>]",
            "  killerpdf crop <in.pdf> <out.pdf> --box <x,y,w,h> [--pages <range>] [--media]   or --clear",
            "                                           crop pages in PDF points; --media resizes the page itself",
            "  killerpdf macro <in.pdf> <saved name | macro.json | starter:<name>> <out.pdf> [--lang <code>]",
            "                                           [--text <phrase>] [--terms <file>] [--rasterize] [--json] [--password <p>]",
            "                                           run a macro; --macro --list shows the saved ones",
            "  killerpdf document-info <in.pdf>                 print document details as JSON",
            "  killerpdf search-text <in.pdf> <query>            print page hits as JSON; exit 3 when pages",
            "                                           could not be read (listed as unreadablePages)",
            "  killerpdf batch-resave <in> <out> [--log <f.csv>] [--quiet]",
            "                                           resave a file or tree through the standard",
            "                                           open/save pipeline (validation harness)",
            "  killerpdf batch-render <in> <outDir> [--size <px>] [--pages <n>] [--parallel <n>] [--log <f.csv>] [--quiet] [--password <p>]",
            "                                           render the first pages of a file or tree to PNG",
            "                                           with per-page timings (render benchmark)",
            "                                           --allow-incomplete also writes pages with omitted",
            "                                           content (release-gate comparisons)",
            "  --secrets-stdin                          read password, user-password, owner-password and",
            "                                           pfx-password from standard input as one JSON object",
            "                                           instead of the command line, where other programs can see them",
            "  --allow-incomplete                       keep a page whose content cannot be drawn exactly instead of",
            "                                           failing: to-image, flatten and print name it; batch-render",
            "                                           marks it INCOMPLETE and counts it, keeping its log row OK",
            "",
            "Exit codes: 0 success, 1 operation failed, 2 bad usage, 3 findings or nothing to do (where noted).",
            "Runs headless and works while the KillerPDF window is open.",
        ]);

        private static int CliVerifyPayload(TextWriter output)
        {
            PayloadIntegrityResult result = PayloadIntegrityVerifier.Verify(AppContext.BaseDirectory);
            if (result.Success)
            {
                output.WriteLine($"PASS: verified {result.VerifiedFiles} payload files.");
                return 0;
            }
            output.WriteLine("FAIL: installed payload verification failed.");
            foreach (string error in result.Errors) output.WriteLine("  " + error);
            return 1;
        }

        /// <summary>
        /// Splits args into positionals (everything after the command flag that
        /// is not an option) and an option dictionary (case-insensitive keys).
        /// </summary>
        private static (List<string> Positionals, Dictionary<string, string> Options)
            ParseCliArgs(string[] args, string command)
        {
            var positionals = new List<string>();
            var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int start = Array.FindIndex(args, a => Eq(a, command)) + 1;
            for (int i = start; i < args.Length; i++)
            {
                var a = args[i];
                if (a.StartsWith("--", StringComparison.Ordinal))
                {
                    if (CliValueOptions.Any(o => Eq(o, a)) && i + 1 < args.Length)
                        options[a] = args[++i];
                    else
                        options[a] = string.Empty;
                }
                else
                {
                    positionals.Add(a);
                }
            }
            return (positionals, options);
        }

        /// <summary>
        /// Parses a 1-based page range spec like "1-3,5,9-12" into sorted,
        /// distinct 0-based indices. Returns null with a message in error when
        /// the spec is malformed or out of range.
        /// </summary>
        // internal, not private: FileOperations still calls these two. They are page-range parsing
        // and JPEG encoding, neither of which is really CLI-specific - they want a home in
        // Services/ eventually.
        internal static List<int>? CliParsePageRange(string spec, int pageCount, out string error)
        {
            error = string.Empty;
            var pages = new SortedSet<int>();
            foreach (var rawPart in spec.Split(','))
            {
                var part = rawPart.Trim();
                if (part.Length == 0) continue;
                int a, b;
                int dash = part.IndexOf('-');
                if (dash > 0)
                {
                    if (!int.TryParse(part[..dash].Trim(), out a) ||
                        !int.TryParse(part[(dash + 1)..].Trim(), out b))
                    { error = $"Bad page range: \"{part}\""; return null; }
                }
                else
                {
                    if (!int.TryParse(part, out a)) { error = $"Bad page number: \"{part}\""; return null; }
                    b = a;
                }
                if (a > b) (a, b) = (b, a);
                if (a < 1 || b > pageCount)
                { error = $"Pages {part} out of range - the document has {pageCount} pages"; return null; }
                for (int p = a; p <= b; p++) pages.Add(p - 1);
            }
            if (pages.Count == 0) { error = "Empty page range"; return null; }
            return [.. pages];
        }

        // ============================================================
        // --merge <out.pdf> <in1> <in2> ...
        // ============================================================
        // The engine imports complete PDF graphs and authors one page per image
        // frame, retaining input order and a leading PDF's original byte prefix.
        private static int CliMerge(List<string> pos, TextWriter con)
        {
            // One input is allowed too, so a single image becomes a PDF.
            if (pos.Count < 2)
            {
                con.WriteLine("Usage: KillerPDF.exe --merge <out.pdf> <in1> [<in2> ...]   (PDFs and images)");
                return 2;
            }
            string outPath = Path.GetFullPath(pos[0]);
            var inputs = pos.Skip(1).Select(Path.GetFullPath).ToList();

            foreach (var f in inputs)
            {
                if (!File.Exists(f)) { con.WriteLine($"Input not found: {f}"); return 2; }
                if (string.Equals(f, outPath, StringComparison.OrdinalIgnoreCase))
                { con.WriteLine("Output file cannot also be an input."); return 2; }
            }

            byte[] merged = PdfEngineIntegration.MergeFiles(inputs, out IReadOnlyList<string> warnings);
            CliEnsureParentDir(outPath);
            AtomicFileOutput.Write(outPath, merged);
            int pageCount = KillerPdf.Engine.Documents.PdfDocumentInformation
                .Read(KillerPdf.Engine.Documents.PdfDocument.Open(merged)).PageCount;
            foreach (string warning in warnings) con.WriteLine("Warning: " + warning);
            con.WriteLine($"Merged {inputs.Count} files ({pageCount} pages) -> {outPath}");
            return 0;
        }

        // ============================================================
        // --extract-pages <in.pdf> <range> <out.pdf>
        // ============================================================
        // Same primitive as the GUI extract (PageOperations.cs Split_Click):
        // Import-mode open, AddPage per selected index, save a fresh document.
        private static int CliExtractPages(List<string> pos, TextWriter con)
        {
            if (pos.Count != 3)
            {
                con.WriteLine("Usage: KillerPDF.exe --extract-pages <in.pdf> <pages> <out.pdf>   (pages like 1-3,5,9-12)");
                return 2;
            }
            string inPath = Path.GetFullPath(pos[0]), spec = pos[1], outPath = Path.GetFullPath(pos[2]);
            if (!File.Exists(inPath)) { con.WriteLine($"Input not found: {inPath}"); return 2; }

            byte[] source = File.ReadAllBytes(inPath);
            int pageCount = KillerPdf.Engine.Documents.PdfDocumentInformation
                .Read(PdfEngineIntegration.OpenForReading(source)).PageCount;
            var indices = CliParsePageRange(spec, pageCount, out string err);
            if (indices is null) { con.WriteLine(err); return 2; }

            if (!CliPublishOutput(inPath, outPath, PdfEngineIntegration.ExtractPages(source, indices), con)) return 2;
            con.WriteLine($"Extracted {indices.Count} pages -> {outPath}");
            return 0;
        }

        // ============================================================
        // --split <in.pdf> <outDir>
        // ============================================================
        private static int CliSplit(List<string> pos, Dictionary<string, string> options, TextWriter con)
        {
            if (pos.Count != 2)
            {
                con.WriteLine("Usage: KillerPDF.exe --split <in.pdf> <outputFolder> "
                    + "[--every <n> | --parts <n> | --bookmarks <level> | --max-size <n[KB|MB]>] "
                    + "[--name <template>] [--overwrite]");
                return 2;
            }
            string inPath = Path.GetFullPath(pos[0]), outDir = Path.GetFullPath(pos[1]);
            if (!File.Exists(inPath)) { con.WriteLine($"Input not found: {inPath}"); return 2; }

            // Keep the source protected against replacement through another filesystem alias.
            using var sourceGuard = new FileStream(inPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            byte[] source = File.ReadAllBytes(inPath);
            string baseName = Path.GetFileNameWithoutExtension(inPath);
            PdfSplitOptions? rule = ParseSplitRule(options, con, out bool invalid);
            if (invalid) return 2;

            var outputs = new List<(string Path, ReadOnlyMemory<byte> Data)>();
            string success;
            if (rule is null)
            {
                // Default, unchanged: one PDF per page.
                IReadOnlyList<byte[]> pages = PdfEngineIntegration.SplitPages(source);
                int digits = Math.Max(3, pages.Count.ToString().Length);
                for (int i = 0; i < pages.Count; i++)
                    outputs.Add((
                        Path.Combine(outDir, $"{baseName}-page-{(i + 1).ToString().PadLeft(digits, '0')}.pdf"),
                        pages[i]));
                success = $"Split {pages.Count} pages into {outDir}";
            }
            else
            {
                IReadOnlyList<PdfSplitOutput> parts;
                try
                {
                    parts = PdfDocumentSplitter.Split(PdfEngineIntegration.OpenForReading(source), rule, baseName);
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
                {
                    con.WriteLine(ex.Message);
                    return 2;
                }
                outputs.AddRange(parts.Select(part =>
                    (Path.GetFullPath(Path.Combine(outDir, part.Name + ".pdf")), part.Document)));
                success = $"Split into {parts.Count} files in {outDir}";
            }

            string[] outputPaths = [.. outputs.Select(output => output.Path)];
            if (outputPaths.Any(path => string.Equals(path, inPath, StringComparison.OrdinalIgnoreCase))
                || outputPaths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != outputPaths.Length)
            {
                con.WriteLine("Split output filenames must be unique and cannot replace the source file.");
                return 2;
            }
            bool overwrite = options.ContainsKey("--overwrite");
            foreach (string path in outputPaths)
            {
                if (Directory.Exists(path) || (!overwrite && File.Exists(path)))
                {
                    con.WriteLine($"Split output already exists: {path}. Use --overwrite to replace existing files.");
                    return 2;
                }
            }

            Directory.CreateDirectory(outDir);
            var staged = new List<(string Temporary, string Destination)>();
            try
            {
                foreach (var output in outputs)
                {
                    string temporary = Path.Combine(outDir, $".killerpdf-split-{Guid.NewGuid():N}.tmp");
                    using var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    staged.Add((temporary, output.Path));
                    stream.Write(output.Data.Span);
                    stream.Flush(flushToDisk: true);
                }
                foreach (var file in staged)
                {
                    if (overwrite)
                        PdfEngineIntegration.ReplaceFileAtomically(file.Temporary, file.Destination);
                    else
                        File.Move(file.Temporary, file.Destination, overwrite: false);
                }
            }
            finally
            {
                foreach (var file in staged)
                    if (File.Exists(file.Temporary)) File.Delete(file.Temporary);
            }
            con.WriteLine(success);
            return 0;
        }

        // Returns null when no split rule flag was supplied, so --split keeps its
        // original one-file-per-page behavior. Sets invalid when a flag is malformed.
        private static PdfSplitOptions? ParseSplitRule(
            Dictionary<string, string> options, TextWriter con, out bool invalid)
        {
            invalid = false;
            string[] rules = ["--every", "--parts", "--bookmarks", "--max-size"];
            string[] supplied = [.. rules.Where(options.ContainsKey)];
            if (supplied.Length == 0) return null;
            if (supplied.Length > 1)
            {
                con.WriteLine("Use only one of --every, --parts, --bookmarks, or --max-size.");
                invalid = true;
                return null;
            }
            string template = options.TryGetValue("--name", out string? name)
                && !string.IsNullOrWhiteSpace(name) ? name : "{name}-{index}";
            string raw = options[supplied[0]];
            if (supplied[0] == "--max-size")
            {
                if (!TryParseByteSize(raw, out long bytes))
                {
                    con.WriteLine("--max-size must be a byte count, optionally suffixed KB, MB, or GB.");
                    invalid = true;
                    return null;
                }
                return new PdfSplitOptions
                {
                    Mode = PdfSplitMode.MaximumBytes,
                    MaximumBytes = bytes,
                    NameTemplate = template
                };
            }
            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture,
                out int value) || value <= 0)
            {
                con.WriteLine($"{supplied[0]} must be a whole number greater than zero.");
                invalid = true;
                return null;
            }
            return supplied[0] switch
            {
                "--every" => new PdfSplitOptions
                {
                    Mode = PdfSplitMode.PageCount,
                    PagesPerPart = value,
                    NameTemplate = template
                },
                "--parts" => new PdfSplitOptions
                {
                    Mode = PdfSplitMode.PartCount,
                    PartCount = value,
                    NameTemplate = template
                },
                _ => new PdfSplitOptions
                {
                    Mode = PdfSplitMode.BookmarkLevel,
                    BookmarkLevel = value,
                    NameTemplate = template
                }
            };
        }

        private static bool TryParseByteSize(string text, out long bytes)
        {
            bytes = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string trimmed = text.Trim();
            long multiplier = 1;
            foreach ((string suffix, long scale) in new[]
                { ("GB", 1L << 30), ("MB", 1L << 20), ("KB", 1L << 10) })
            {
                if (!trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
                multiplier = scale;
                trimmed = trimmed[..^suffix.Length].Trim();
                break;
            }
            if (!long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture,
                out long count) || count <= 0) return false;
            try
            {
                bytes = checked(count * multiplier);
            }
            catch (OverflowException) { return false; }
            return true;
        }

        // ============================================================
        // --optimize <in.pdf> <out.pdf> [--strip <list>] [--report]
        // ============================================================
        // Acrobat calls this Reduce File Size plus Sanitize. The defaults prune what the
        // trailer can no longer reach, drop resources the pages no longer name, and
        // recompress unfiltered streams. Everything destructive is opt in through --strip.
        private static int CliOptimize(
            List<string> pos, Dictionary<string, string> options, TextWriter con)
        {
            if (pos.Count != 2)
            {
                con.WriteLine("Usage: KillerPDF.exe --optimize <in.pdf> <out.pdf> "
                    + "[--strip <list>] [--images <dpi>] [--jpeg-quality <n>] [--invalidate-signatures] [--report] [--password <p>]");
                return 2;
            }
            string inPath = Path.GetFullPath(pos[0]), outPath = Path.GetFullPath(pos[1]);
            if (!File.Exists(inPath)) { con.WriteLine($"Input not found: {inPath}"); return 2; }

            // The output is a rewrite, so a damaged signed, certified, usage-rights or encrypted file is
            // refused as the save paths refuse it, instead of written with its protection quietly gone.
            try { PdfEngineIntegration.ThrowIfDamagedProtected(inPath); }
            catch (DamagedSaveRefusedException ex)
            {
                con.WriteLine(ex.Message);
                return 1;
            }

            // An encrypted input is rewritten from its plain content and keeps its protection, as Reduce Size does.
            options.TryGetValue("--password", out string? password);
            PdfDocument document;
            OpenedProtection protection;
            try
            {
                PdfDocument? opened = CliProtectionGate.OpenForRewrite(inPath, password, out protection, out string? refusal);
                if (opened is null)
                {
                    con.WriteLine(refusal);
                    return 1;
                }
                document = opened;
            }
            catch (Exception ex) when (ex is InvalidOperationException
                or NotSupportedException or ArgumentException or FormatException)
            {
                con.WriteLine("Could not read the document: " + ex.Message);
                return 1;
            }

            long originalBytes = new FileInfo(inPath).Length;
            if (options.TryGetValue("--images", out string? imageDpiText))
            {
                if (!double.TryParse(imageDpiText, NumberStyles.Float, CultureInfo.InvariantCulture,
                        out double imageDpi) || imageDpi < 36 || imageDpi > 1200)
                {
                    con.WriteLine("--images takes a resolution from 36 to 1200 dpi.");
                    return 2;
                }
                int quality = options.TryGetValue("--jpeg-quality", out string? qualityText)
                    && int.TryParse(qualityText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedQuality)
                    && parsedQuality is >= 1 and <= 100 ? parsedQuality : 80;
                try
                {
                    PdfImageDownsampleResult images = PdfImageDownsampler.Downsample(document,
                        new PdfImageDownsampleOptions
                        {
                            TargetDpi = imageDpi,
                            ThresholdDpi = imageDpi * 1.5,
                            JpegQuality = quality,
                            JpegEncoder = PdfImageJpeg.Encode
                        });
                    if (images.ImagesResampled > 0)
                    {
                        document = PdfEngineIntegration.OpenForReading(images.Data);
                        con.WriteLine($"Resampled {images.ImagesResampled} images from "
                            + $"{images.ImageBytesBefore} to {images.ImageBytesAfter} bytes");
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException
                    or NotSupportedException or ArgumentException or FormatException)
                {
                    con.WriteLine("Could not resample the images: " + ex.Message);
                    return 1;
                }
            }

            PdfOptimizationOptions settings = PdfOptimizationOptions.ForDocument(document);
            if (options.TryGetValue("--strip", out string? strip))
            {
                foreach (string item in strip.Split(',',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    switch (item.ToLowerInvariant())
                    {
                        case "metadata": settings = settings with { RemoveMetadata = true }; break;
                        case "attachments": settings = settings with { RemoveAttachments = true }; break;
                        case "javascript": settings = settings with { RemoveDocumentJavaScript = true }; break;
                        case "comments": settings = settings with { RemoveComments = true }; break;
                        case "thumbnails": settings = settings with { RemovePageThumbnails = true }; break;
                        case "bookmarks": settings = settings with { RemoveBookmarks = true }; break;
                        case "forms": settings = settings with { RemoveFormFields = true }; break;
                        case "xfa": settings = settings with { RemoveXfaData = true }; break;
                        case "openaction": settings = settings with { RemoveOpenAction = true }; break;
                        case "layers": settings = settings with { FlattenOptionalContent = true }; break;
                        default:
                            con.WriteLine($"--strip does not recognize '{item}'.");
                            return 2;
                    }
                }
            }
            // The rewrite drops signatures and Reader usage rights, and nothing here can ask, so it takes the flag.
            settings = settings with
            {
                RepairHarmlessArtifacts = true,
                ReplaceDanglingReferences = true,
                AllowSignatureInvalidation = options.ContainsKey("--invalidate-signatures")
            };

            PdfOptimizationResult result;
            try
            {
                result = PdfOptimizer.CreatePlan(document, settings).Apply();
            }
            catch (InvalidOperationException ex) when (PdfRewriteRefusal.Matches(ex))
            {
                con.WriteLine("Could not optimize the document: "
                    + PdfRewriteRefusal.Restate(ex, "Add --invalidate-signatures to go ahead.").Message);
                return 1;
            }
            catch (Exception ex) when (ex is InvalidOperationException
                or NotSupportedException or ArgumentException or FormatException)
            {
                con.WriteLine("Could not optimize the document: " + ex.Message);
                return 1;
            }
            byte[] output = CliProtectionGate.SealRewrite(result.Data.ToArray(), protection);
            if (!CliPublishOutput(inPath, outPath, output, con)) return 2;
            if (options.ContainsKey("--report")) con.WriteLine(result.ToJson(true));
            if (result.ReferenceRepairs.Count > 0)
                con.WriteLine("Saved with repairs to broken references. Missing content was not recovered.");
            long saved = originalBytes - output.Length;
            string percent = originalBytes > 0
                ? (saved * 100.0 / originalBytes).ToString("0.0", CultureInfo.InvariantCulture)
                : "0.0";
            con.WriteLine($"Optimized {originalBytes} to {output.Length} bytes "
                + $"({percent}% smaller, {result.OriginalObjectCount} to {result.OutputObjectCount} objects)");
            return 0;
        }

        // ============================================================
        // --booklet / --nup <in.pdf> <out.pdf> [...]
        // ============================================================
        private static int CliImpose(List<string> pos, Dictionary<string, string> options,
            TextWriter con, bool booklet)
        {
            string verb = booklet ? "--booklet" : "--nup";
            if (pos.Count != 2)
            {
                con.WriteLine($"Usage: KillerPDF.exe {verb} <in.pdf> <out.pdf> "
                    + (booklet ? string.Empty : "--grid <cols>x<rows> ")
                    + "[--sheet <name|WxH>] [--margin <n>] [--gutter <n>] "
                    + (booklet ? "[--creep <n>] [--binding long|short] " : string.Empty)
                    + "[--marks]");
                return 2;
            }
            string inPath = Path.GetFullPath(pos[0]), outPath = Path.GetFullPath(pos[1]);
            if (!File.Exists(inPath)) { con.WriteLine($"Input not found: {inPath}"); return 2; }

            double sheetWidth = 612, sheetHeight = 792;
            bool namedSheet = true;
            if (options.TryGetValue("--sheet", out string? sheet)
                && !TryParseSheet(sheet, out sheetWidth, out sheetHeight, out namedSheet))
            {
                con.WriteLine("--sheet must be letter, legal, tabloid, a3, a4, a5, "
                    + "or a width and height in points such as 842x1191.");
                return 2;
            }
            // A saddle-stitch sheet is folded down the middle, so a named size is used
            // landscape. An explicit width and height is taken exactly as written.
            if (booklet && namedSheet && sheetHeight > sheetWidth)
                (sheetWidth, sheetHeight) = (sheetHeight, sheetWidth);

            int columns = 2, rows = 1;
            if (!booklet)
            {
                if (!options.TryGetValue("--grid", out string? grid)
                    || !TryParseGrid(grid, out columns, out rows))
                {
                    con.WriteLine("--grid must be columns by rows, such as 2x2.");
                    return 2;
                }
            }

            if (!TryReadNonNegative(options, "--margin", 0, con, out double margin)
                || !TryReadNonNegative(options, "--gutter", 0, con, out double gutter)
                || !TryReadNonNegative(options, "--creep", 0, con, out double creep)) return 2;

            var binding = PdfImpositionBindingEdge.Long;
            if (options.TryGetValue("--binding", out string? bindingRaw))
            {
                switch (bindingRaw.ToLowerInvariant())
                {
                    case "long": binding = PdfImpositionBindingEdge.Long; break;
                    case "short": binding = PdfImpositionBindingEdge.Short; break;
                    default: con.WriteLine("--binding must be long or short."); return 2;
                }
            }
            bool marks = options.ContainsKey("--marks");

            PdfImpositionPreset preset;
            PdfMacroStep step;
            try
            {
                preset = new PdfImpositionPreset(
                    booklet ? "Booklet" : $"{columns} by {rows}",
                    columns, rows, sheetWidth, sheetHeight, margin, gutter,
                    duplex: booklet, rotateToFit: true,
                    includeCropMarks: marks, includeRegistrationMarks: marks,
                    creepPerSheet: creep, includeFoldMarks: marks && booklet,
                    bindingEdge: binding);
                step = booklet
                    ? PdfImpositionMacro.BookletStep(preset)
                    : PdfImpositionMacro.NUpStep(preset);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                con.WriteLine("Could not build the layout: " + ex.Message);
                return 2;
            }

            try
            {
                ReadOnlyMemory<byte> output = PdfImpositionMacro.Execute(
                    step, File.ReadAllBytes(inPath));
                if (!CliPublishOutput(inPath, outPath, output.ToArray(), con)) return 2;
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException
                or ArgumentException or FormatException or KeyNotFoundException)
            {
                con.WriteLine("Could not impose the document: " + ex.Message);
                return 1;
            }
            con.WriteLine($"Imposed onto {sheetWidth:0.##} by {sheetHeight:0.##} point sheets "
                + $"-> {outPath}");
            return 0;
        }

        // ============================================================
        // --auto-bookmarks <in.pdf> <out.pdf> [...]
        // ============================================================
        private static int CliAutoBookmarks(
            List<string> pos, Dictionary<string, string> options, TextWriter con)
        {
            if (pos.Count != 2)
            {
                con.WriteLine("Usage: KillerPDF.exe --auto-bookmarks <in.pdf> <out.pdf> "
                    + "[--min-size <points>] [--depth <n>] [--pattern <regex>]");
                return 2;
            }
            string inPath = Path.GetFullPath(pos[0]), outPath = Path.GetFullPath(pos[1]);
            if (!File.Exists(inPath)) { con.WriteLine($"Input not found: {inPath}"); return 2; }

            if (!TryReadNonNegative(options, "--min-size", 14, con, out double minimumSize))
                return 2;
            if (minimumSize <= 0) { con.WriteLine("--min-size must be greater than zero."); return 2; }
            int depth = ParseBoundedIntOption(options, "--depth", 6, 1, 16);

            var detection = new PdfBookmarkDetectionOptions
            {
                MinimumPointSize = minimumSize,
                MaximumDepth = depth,
                TitlePattern = options.TryGetValue("--pattern", out string? pattern)
                    && pattern.Length > 0 ? pattern : null
            };

            try
            {
                PdfDocument document = PdfEngineIntegration.OpenForReading(File.ReadAllBytes(inPath));
                IReadOnlyList<PdfBookmarkProposal> proposals =
                    PdfBookmarkGeneration.DetectHeadings(document, detection);
                if (proposals.Count == 0)
                {
                    con.WriteLine("No headings were detected. Try a smaller --min-size.");
                    return 3;
                }
                byte[] output = PdfBookmarkGeneration.Apply(document,
                    proposals.Select(proposal => proposal with
                    {
                        Decision = PdfBookmarkProposalDecision.Accepted
                    }));
                if (!CliPublishOutput(inPath, outPath, output, con)) return 2;
                con.WriteLine($"Added {proposals.Count} bookmarks -> {outPath}");
                return 0;
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException
                or ArgumentException or FormatException or KeyNotFoundException
                or System.Text.RegularExpressions.RegexParseException)
            {
                con.WriteLine("Could not build the outline: " + ex.Message);
                return ex is ArgumentException or System.Text.RegularExpressions.RegexParseException
                    ? 2 : 1;
            }
        }

        private static bool TryReadNonNegative(Dictionary<string, string> options, string name,
            double fallback, TextWriter con, out double value)
        {
            value = fallback;
            if (!options.TryGetValue(name, out string? raw)) return true;
            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                && double.IsFinite(value) && value >= 0) return true;
            con.WriteLine($"{name} must be a number of zero or more.");
            return false;
        }

        private static bool TryParseGrid(string text, out int columns, out int rows)
        {
            columns = 0;
            rows = 0;
            string[] parts = text.Split('x', 'X', '*');
            return parts.Length == 2
                && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out columns) && columns is > 0 and <= 64
                && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out rows) && rows is > 0 and <= 64;
        }

        private static bool TryParseSheet(string text, out double width, out double height,
            out bool named)
        {
            named = true;
            switch (text.Trim().ToLowerInvariant())
            {
                case "letter": width = 612; height = 792; return true;
                case "legal": width = 612; height = 1008; return true;
                case "tabloid": width = 792; height = 1224; return true;
                case "a3": width = 841.89; height = 1190.55; return true;
                case "a4": width = 595.28; height = 841.89; return true;
                case "a5": width = 419.53; height = 595.28; return true;
            }
            named = false;
            width = 0;
            height = 0;
            string[] parts = text.Split('x', 'X', '*');
            return parts.Length == 2
                && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture,
                    out width) && double.IsFinite(width) && width > 0
                && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture,
                    out height) && double.IsFinite(height) && height > 0;
        }

        // ============================================================
        // --preflight <in.pdf> [--profile <name|file>] [--json] [--password <p>]
        // ============================================================
        // Exit 3 means the document was read and findings were reported, which is
        // different from exit 1, where the document could not be checked at all.
        private static int CliPreflight(
            List<string> pos, Dictionary<string, string> options, TextWriter con)
        {
            if (pos.Count != 1)
            {
                con.WriteLine("Usage: KillerPDF.exe --preflight <in.pdf> "
                    + "[--profile general|attachments|print|<profile.json>] [--json] "
                    + "[--password <password>]");
                return 2;
            }
            string inPath = Path.GetFullPath(pos[0]);
            if (!File.Exists(inPath)) { con.WriteLine($"Input not found: {inPath}"); return 2; }

            PdfPreflightProfile profile = PdfPreflightProfile.General;
            if (options.TryGetValue("--profile", out string? profileName))
            {
                switch (profileName.ToLowerInvariant())
                {
                    case "general": profile = PdfPreflightProfile.General; break;
                    case "attachments": profile = PdfPreflightProfile.Attachments; break;
                    case "print": profile = PdfPreflightProfile.PrintProduction; break;
                    default:
                        string profilePath = Path.GetFullPath(profileName);
                        if (!File.Exists(profilePath))
                        {
                            con.WriteLine("--profile must be general, attachments, print, "
                                + "or the path of a saved profile.");
                            return 2;
                        }
                        try
                        {
                            profile = PdfPreflightProfile.FromJson(File.ReadAllText(profilePath));
                        }
                        catch (Exception ex) when (ex is ArgumentException
                            or InvalidOperationException or NotSupportedException
                            or System.Text.Json.JsonException)
                        {
                            con.WriteLine("Could not read the profile: " + ex.Message);
                            return 2;
                        }
                        break;
                }
            }

            options.TryGetValue("--password", out string? password);
            PdfPreflightReport report;
            try
            {
                report = PdfPreflightRunner.Run(File.ReadAllBytes(inPath), profile, password);
            }
            catch (Exception ex) when (ex is InvalidOperationException
                or NotSupportedException or ArgumentException or FormatException)
            {
                con.WriteLine("Could not check the document: " + ex.Message);
                return 1;
            }
            con.WriteLine(options.ContainsKey("--json") ? report.ToJson(true) : report.ToText());
            return report.Passed && report.Complete ? 0 : 3;
        }

        // ============================================================
        // --accessibility <in.pdf> [--json]
        // ============================================================
        private static int CliAccessibility(
            List<string> pos, Dictionary<string, string> options, TextWriter con)
        {
            if (pos.Count != 1)
            {
                con.WriteLine("Usage: KillerPDF.exe --accessibility <in.pdf> [--json]");
                return 2;
            }
            string inPath = Path.GetFullPath(pos[0]);
            if (!File.Exists(inPath)) { con.WriteLine($"Input not found: {inPath}"); return 2; }

            PdfAccessibilityReport report;
            try
            {
                report = PdfAccessibilityInspector.Inspect(
                    PdfEngineIntegration.OpenForReading(File.ReadAllBytes(inPath)));
            }
            catch (Exception ex) when (ex is InvalidOperationException
                or NotSupportedException or ArgumentException or FormatException)
            {
                con.WriteLine("Could not check the document: " + ex.Message);
                return 1;
            }
            con.WriteLine(options.ContainsKey("--json") ? report.ToJson(true) : report.ToText());
            return report.PassesImplementedChecks ? 0 : 3;
        }

        // ============================================================
        // --attachments <in.pdf> [<outDir>] [--json]
        // ============================================================
        // A PDF whose owner forbids editing gives up its pages as images and its embedded files only to
        // the owner password, the way the app asks for it before it exports them.
        private static string CliRestrictionMessage(PdfAccess access) => CliProtectionGate.Message(access);

        private static int CliAttachments(
            List<string> pos, Dictionary<string, string> options, TextWriter con)
        {
            if (pos.Count is < 1 or > 2)
            {
                con.WriteLine("Usage: KillerPDF.exe --attachments <in.pdf> [<outputFolder>] [--json] [--password <p>]");
                return 2;
            }
            string inPath = Path.GetFullPath(pos[0]);
            if (!File.Exists(inPath)) { con.WriteLine($"Input not found: {inPath}"); return 2; }

            options.TryGetValue("--password", out var password);
            PdfDocument document;
            IReadOnlyList<PdfAttachmentInfo> attachments;
            try
            {
                PdfAccess access = PdfRestrictions.Open(File.ReadAllBytes(inPath), password, out PdfDocument? opened);
                if (opened is null || access.Role == PdfAccessRole.Refused)
                {
                    con.WriteLine("Could not read the document: " + CliRestrictionMessage(access));
                    return 1;
                }
                document = opened;
                attachments = PdfAttachmentReader.Read(document);
            }
            catch (Exception ex) when (ex is InvalidOperationException
                or NotSupportedException or ArgumentException or FormatException)
            {
                con.WriteLine("Could not read the document: " + ex.Message);
                return 1;
            }

            if (pos.Count == 1)
            {
                con.WriteLine(options.ContainsKey("--json")
                    ? PdfAttachmentReader.ToJson(document, true)
                    : PdfAttachmentReader.ToText(document));
                return 0;
            }

            string outDir = Path.GetFullPath(pos[1]);
            if (attachments.Count == 0)
            {
                con.WriteLine("The document has no embedded files.");
                return 0;
            }
            Directory.CreateDirectory(outDir);
            IReadOnlyList<string> written;
            try
            {
                written = PdfAttachmentReader.ExtractAll(attachments, outDir, overwrite: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException
                or NotSupportedException or ArgumentException or IOException
                or UnauthorizedAccessException)
            {
                con.WriteLine("Could not extract the embedded files: " + ex.Message);
                return 1;
            }
            con.WriteLine($"Extracted {written.Count} embedded files into {outDir}");
            return 0;
        }

        // ============================================================
        // --number-pages <in.pdf> <out.pdf> [...]
        // ============================================================
        private static int CliNumberPages(
            List<string> pos, Dictionary<string, string> options, TextWriter con)
        {
            if (pos.Count != 2)
            {
                con.WriteLine("Usage: KillerPDF.exe --number-pages <in.pdf> <out.pdf> "
                    + "[--template <text>] [--pages <range>] [--edge header|footer] "
                    + "[--align left|center|right] [--number-format <format>] [--font-size <n>]");
                return 2;
            }
            string inPath = Path.GetFullPath(pos[0]), outPath = Path.GetFullPath(pos[1]);
            if (!File.Exists(inPath)) { con.WriteLine($"Input not found: {inPath}"); return 2; }

            if (!TryReadFurnitureLayout(options, con,
                PdfPageFurnitureEdge.Footer, PdfPageFurnitureAlignment.Center,
                out PdfPageFurnitureEdge edge, out PdfPageFurnitureAlignment align,
                out double fontSize)) return 2;

            var numberFormat = PdfPageNumberFormat.Decimal;
            if (options.TryGetValue("--number-format", out string? formatRaw))
            {
                switch (formatRaw.ToLowerInvariant())
                {
                    case "decimal": numberFormat = PdfPageNumberFormat.Decimal; break;
                    case "upper-roman": numberFormat = PdfPageNumberFormat.UpperRoman; break;
                    case "lower-roman": numberFormat = PdfPageNumberFormat.LowerRoman; break;
                    case "upper-letters": numberFormat = PdfPageNumberFormat.UpperLetters; break;
                    case "lower-letters": numberFormat = PdfPageNumberFormat.LowerLetters; break;
                    default:
                        con.WriteLine("--number-format must be decimal, upper-roman, lower-roman, "
                            + "upper-letters, or lower-letters.");
                        return 2;
                }
            }

            byte[] source = File.ReadAllBytes(inPath);
            List<int>? pages = null;
            if (options.TryGetValue("--pages", out string? rangeSpec))
            {
                int pageCount;
                try
                {
                    pageCount = PdfPageInformation.Read(PdfEngineIntegration.OpenForReading(source)).Count;
                }
                catch (Exception ex) when (ex is InvalidOperationException
                    or NotSupportedException or ArgumentException or FormatException)
                {
                    con.WriteLine("Could not read the document: " + ex.Message);
                    return 1;
                }
                pages = CliParsePageRange(rangeSpec, pageCount, out string error);
                if (pages is null) { con.WriteLine(error); return 2; }
            }

            var settings = new PdfPageNumberMacroOptions
            {
                Template = options.TryGetValue("--template", out string? template)
                    && template.Length > 0 ? template : "{page}",
                Date = DateOnly.FromDateTime(DateTime.Now),
                FileName = Path.GetFileName(inPath),
                Edge = edge,
                Alignment = align,
                NumberFormat = numberFormat,
                FontSize = fontSize,
                PageIndices = pages,
                AllowCollisions = true
            };

            try
            {
                ReadOnlyMemory<byte> output = PdfPageFurnitureMacro.Execute(
                    PdfPageFurnitureMacro.NumberPagesStep(settings), source);
                if (!CliPublishOutput(inPath, outPath, output.ToArray(), con)) return 2;
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException
                or ArgumentException or FormatException or KeyNotFoundException)
            {
                con.WriteLine("Could not number the pages: " + ex.Message);
                return ex is KeyNotFoundException or FormatException ? 2 : 1;
            }
            con.WriteLine($"Numbered pages into {outPath}");
            return 0;
        }

        // ============================================================
        // --bates <outDir> <in1.pdf> [in2.pdf ...] [...]
        // ============================================================
        // One continuous sequence across every input, the way a legal production is stamped.
        private static int CliBates(
            List<string> pos, Dictionary<string, string> options, TextWriter con)
        {
            if (pos.Count < 2)
            {
                con.WriteLine("Usage: KillerPDF.exe --bates <outputFolder> <in1.pdf> [in2.pdf ...] "
                    + "[--start <n>] [--digits <n>] [--prefix <text>] [--suffix <text>] "
                    + "[--edge header|footer] [--align left|center|right] [--font-size <n>]");
                return 2;
            }
            string outDir = Path.GetFullPath(pos[0]);
            string[] inputs = [.. pos.Skip(1).Select(Path.GetFullPath)];
            foreach (string input in inputs)
                if (!File.Exists(input)) { con.WriteLine($"Input not found: {input}"); return 2; }

            string[] outputPaths = [.. inputs.Select(input =>
                Path.GetFullPath(Path.Combine(outDir, Path.GetFileName(input))))];
            var sourcePaths = new HashSet<string>(inputs, StringComparer.OrdinalIgnoreCase);
            if (outputPaths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != outputPaths.Length
                || outputPaths.Any(sourcePaths.Contains))
            {
                con.WriteLine("Bates output filenames must be unique and cannot replace an input file.");
                return 2;
            }

            if (!TryReadFurnitureLayout(options, con,
                PdfPageFurnitureEdge.Footer, PdfPageFurnitureAlignment.Right,
                out PdfPageFurnitureEdge edge, out PdfPageFurnitureAlignment align,
                out double fontSize)) return 2;

            long start = 1;
            if (options.TryGetValue("--start", out string? startRaw)
                && (!long.TryParse(startRaw, NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out start) || start < 0))
            { con.WriteLine("--start must be a whole number of zero or more."); return 2; }
            int digits = ParseBoundedIntOption(options, "--digits", 6, 1, 18);

            var settings = new PdfBatesMacroOptions
            {
                StartNumber = start,
                DigitCount = digits,
                Prefix = options.TryGetValue("--prefix", out string? prefix) ? prefix : string.Empty,
                Suffix = options.TryGetValue("--suffix", out string? suffix) ? suffix : string.Empty,
                Edge = edge,
                Alignment = align,
                FontSize = fontSize,
                AllowCollisions = true
            };

            Directory.CreateDirectory(outDir);
            IReadOnlyList<byte[]> stamped;
            try
            {
                stamped = PdfPageFurnitureMacro.ExecuteBatesBatch(
                    PdfPageFurnitureMacro.BatesBatchStep(settings),
                    [.. inputs.Select(input => (ReadOnlyMemory<byte>)File.ReadAllBytes(input))]);
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException
                or ArgumentException or FormatException or KeyNotFoundException)
            {
                con.WriteLine("Could not stamp the documents: " + ex.Message);
                return ex is KeyNotFoundException or FormatException ? 2 : 1;
            }
            for (int index = 0; index < stamped.Count; index++)
                AtomicFileOutput.Write(outputPaths[index], stamped[index]);   // each file whole or untouched; the batch is not one transaction
            con.WriteLine($"Stamped {stamped.Count} files into {outDir}");
            return 0;
        }

        private static bool TryReadFurnitureLayout(
            Dictionary<string, string> options, TextWriter con,
            PdfPageFurnitureEdge defaultEdge, PdfPageFurnitureAlignment defaultAlignment,
            out PdfPageFurnitureEdge edge, out PdfPageFurnitureAlignment alignment,
            out double fontSize)
        {
            edge = defaultEdge;
            alignment = defaultAlignment;
            fontSize = 10;
            if (options.TryGetValue("--edge", out string? edgeRaw))
            {
                switch (edgeRaw.ToLowerInvariant())
                {
                    case "header": edge = PdfPageFurnitureEdge.Header; break;
                    case "footer": edge = PdfPageFurnitureEdge.Footer; break;
                    default: con.WriteLine("--edge must be header or footer."); return false;
                }
            }
            if (options.TryGetValue("--align", out string? alignRaw))
            {
                switch (alignRaw.ToLowerInvariant())
                {
                    case "left": alignment = PdfPageFurnitureAlignment.Left; break;
                    case "center": alignment = PdfPageFurnitureAlignment.Center; break;
                    case "right": alignment = PdfPageFurnitureAlignment.Right; break;
                    default:
                        con.WriteLine("--align must be left, center, or right.");
                        return false;
                }
            }
            if (options.TryGetValue("--font-size", out string? sizeRaw))
            {
                if (!double.TryParse(sizeRaw, NumberStyles.Float, CultureInfo.InvariantCulture,
                    out fontSize) || !double.IsFinite(fontSize) || fontSize < 1 || fontSize > 400)
                { con.WriteLine("--font-size must be between 1 and 400."); return false; }
            }
            return true;
        }

        // ============================================================
        // --decrypt <in.pdf> <out.pdf> [--password <p>]
        // ============================================================
        // Without a password: the same lossless engine rewrite the GUI uses at
        // open time (owner/permissions encryption), with the Import-rebuild
        // fallback. With a password, The KillerPDF.Engine authenticates and
        // fully rewrites the document without its encryption dictionary.
        private static int CliDecrypt(List<string> pos, Dictionary<string, string> options, TextWriter con)
        {
            if (pos.Count != 2)
            {
                con.WriteLine("Usage: KillerPDF.exe --decrypt <in.pdf> <out.pdf> [--password <password>]");
                return 2;
            }
            string inPath = Path.GetFullPath(pos[0]), outPath = Path.GetFullPath(pos[1]);
            if (!File.Exists(inPath)) { con.WriteLine($"Input not found: {inPath}"); return 2; }
            CliEnsureParentDir(outPath);

            options.TryGetValue("--password", out string? password);
            if (!string.IsNullOrEmpty(password))
            {
                PdfEngineIntegration.RemoveEncryption(inPath, outPath, password!);
                con.WriteLine($"Decrypted -> {outPath}");
                return 0;
            }

            if (PdfEngineIntegration.TryRemoveEncryptionWithoutPassword(inPath, outPath))
            {
                con.WriteLine($"Decrypted (lossless) -> {outPath}");
                return 0;
            }
            if (PdfImport.TryImportRepairToPath(inPath, outPath))
            {
                con.WriteLine($"Decrypted via page rebuild -> {outPath} (bookmarks/forms may be dropped)");
                return 0;
            }
            con.WriteLine("Could not decrypt. If the file needs a password to open, pass --password.");
            return 1;
        }

        // ============================================================
        // Shared rasterization prep
        // ============================================================
        // The renderer sizes its bitmap from the unrotated MediaBox, so pages with
        // /Rotate 90/270 clip if rendered directly (same reason TempReload
        // strips rotations app-wide). Prep: decrypt if needed, capture per-page
        // /Rotate + point dims, strip rotations to a temp, and let callers
        // rotate the pixel buffers afterward (BitmapHelpers.RotateBitmap).
        // Falls back to rendering the file as-is when PdfSharpCore cannot open
        // it after compatibility recovery; callers then derive
        // dimensions from the rendered pixels. Every command that writes what it renders into a file
        // gets a PDF whose owner forbids editing only with the owner password; print passes
        // ownerOnly: false because it answers to the print permission instead.
        private static (string RenderPath, int[]? Rotations, (double WPt, double HPt)[]? Dims)
            CliPrepareRenderSource(string inPath, string? password, TextWriter con, bool ownerOnly = true)
        {
            PdfAccess access = PdfRestrictions.Read(inPath, password);
            if (access.Role == PdfAccessRole.Refused || (ownerOnly && !access.MayModify))
                throw new InvalidOperationException(CliRestrictionMessage(access));
            string workPath = inPath;
            if (access.Role != PdfAccessRole.Unencrypted)
            {
                var dec = App.MakeTempFile("clidec");
                if (!string.IsNullOrEmpty(password))
                {
                    // Only print reaches here without the right to edit, and a viewing copy is all it needs.
                    if (access.MayModify) PdfEngineIntegration.RemoveEncryption(inPath, dec, password!);
                    else PdfEngineIntegration.WriteViewingCopy(inPath, dec, password!);
                }
                else if (!PdfEngineIntegration.TryRemoveEncryptionWithoutPassword(inPath, dec)
                    && !PdfImport.TryImportRepairToPath(inPath, dec))
                {
                    // Owner restrictions can block a rewrite while the file still reads without
                    // a password; the engine renders that original directly.
                    if (!PdfEngineIntegration.CanReadWithoutPassword(inPath))
                        throw new InvalidOperationException(
                            "File is encrypted and could not be unlocked - pass --password if it needs one.");
                    dec = inPath;
                }
                workPath = dec;
            }

            try
            {
                IReadOnlyList<KillerPdf.Engine.Documents.PdfPageInformation> pages =
                    PdfEngineIntegration.ReadPageInformation(workPath);
                var rotations = new int[pages.Count];
                var dims = new (double WPt, double HPt)[pages.Count];
                bool anyRot = false;
                for (int i = 0; i < pages.Count; i++)
                {
                    rotations[i] = pages[i].Rotation;
                    dims[i] = (pages[i].PhysicalWidth, pages[i].PhysicalHeight);
                    if (rotations[i] != 0) anyRot = true;
                }
                if (!anyRot) return (workPath, rotations, dims);

                var renderTemp = App.MakeTempFile("clirender");
                PdfEngineIntegration.CreateZeroRotationCopy(workPath, renderTemp);
                return (renderTemp, rotations, dims);
            }
            catch (Exception ex)
            {
                con.WriteLine("Note: structure parse failed (" + FlattenBatchDetail(ex.Message) +
                              ") - rendering as-is; rotated pages may clip.");
                return (workPath, null, null);
            }
        }

        // Both encoders live in Services/BitmapHelpers.cs (RenderToPng, and EncodeJpeg - which
        // was born here as CliEncodeJpeg when --to-image needed a JPEG encoder).

        private static void CliEnsureParentDir(string path)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        }

        private static bool TryReadDpi(Dictionary<string, string> options, double fallback, TextWriter con, out double dpi)
        {
            dpi = fallback;
            if (!options.TryGetValue("--dpi", out string? raw)) return true;
            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out dpi)
                && double.IsFinite(dpi) && dpi >= 24 && dpi <= 1200) return true;
            con.WriteLine("--dpi must be between 24 and 1200.");
            return false;
        }

        /// <summary>A command-line option value the user must correct: the run ends with exit code 2.</summary>
        private sealed class CliUsageException(string message) : ArgumentException(message);

        // ============================================================
        // --to-image <in.pdf> <outDir> [--dpi n] [--format png|jpg] [--pages range] [--transparent]
        // ============================================================
        // The engine leaves unpainted background pixels as BGRA 0,0,0,0. Encoders
        // that drop alpha (JPEG) then show them BLACK, and PNG/flatten output
        // carries a useless full-page alpha channel (issue #148, Ryokoxx).
        // Default is composite-over-white unless transparent PNG output is requested;
        // remover; --transparent keeps the raw alpha for PNG output.
        // Print and export commands refuse a page whose content cannot be drawn exactly. With
        // --allow-incomplete they keep it as rendered and name it here, like the desktop does.
        private static void CliReportIncomplete(IncompletePageLog incomplete, TextWriter con)
        {
            IReadOnlyList<int> pages = incomplete.Pages;
            if (pages.Count > 0)
                con.WriteLine($"Pages {IncompletePageLog.Describe(pages)} could not be drawn exactly and were kept as rendered.");
        }

        private static int CliToImage(List<string> pos, Dictionary<string, string> options, TextWriter con)
        {
            if (pos.Count != 2)
            {
                con.WriteLine("Usage: KillerPDF.exe --to-image <in.pdf> <outputFolder> [--dpi <n>] [--format png|jpg] [--pages <range>] [--transparent] [--allow-incomplete] [--overwrite]");
                return 2;
            }
            string inPath = Path.GetFullPath(pos[0]), outDir = Path.GetFullPath(pos[1]);
            if (!File.Exists(inPath)) { con.WriteLine($"Input not found: {inPath}"); return 2; }
            if (!TryReadDpi(options, 150, con, out double dpi)) return 2;
            options.TryGetValue("--format", out var fmtRaw);
            string fmt = (fmtRaw ?? "png").ToLowerInvariant();
            if (fmt == "jpeg") fmt = "jpg";
            if (fmt != "png" && fmt != "jpg") { con.WriteLine("--format must be png or jpg"); return 2; }
            // JPEG has no alpha channel, so --transparent only means anything for png.
            bool transparent = fmt == "png" && options.ContainsKey("--transparent");
            bool allowIncomplete = options.ContainsKey("--allow-incomplete");
            var incomplete = new IncompletePageLog();
            options.TryGetValue("--password", out var password);
            Directory.CreateDirectory(outDir);

            var (renderPath, rotations, _) = CliPrepareRenderSource(inPath, password, con);

            using var renderSession =
                PdfPageRenderSession.OpenEngineFirst(renderPath, dpi / 72.0);
            int pageCount = renderSession.PageCount;

            List<int> selected;
            if (options.TryGetValue("--pages", out var rangeSpec))
            {
                var parsed = CliParsePageRange(rangeSpec, pageCount, out string err);
                if (parsed is null) { con.WriteLine(err); return 2; }
                selected = parsed;
            }
            else
            {
                selected = [.. Enumerable.Range(0, pageCount)];
            }

            string baseName = Path.GetFileNameWithoutExtension(inPath);
            int digits = Math.Max(3, pageCount.ToString().Length);
            string[] paths = PdfRasterize.PlanPageImagePaths(inPath, selected, outDir, baseName, digits, fmt == "jpg");
            HashSet<string>? approved = options.ContainsKey("--overwrite")
                ? paths.ToHashSet(StringComparer.OrdinalIgnoreCase) : null;
            try { PdfRasterize.ValidateImageOutputs(paths, approved); }
            catch (IOException error) { con.WriteLine(error.Message + " Use --overwrite to replace existing images."); return 2; }
            int written = 0;
            try
            {
                foreach (var idx in selected)
                {
                    PdfRenderedPage rendered = renderSession.RenderPage(idx, transparent,
                        requireComplete: !allowIncomplete);
                    incomplete.Note(idx, rendered);
                    byte[] raw = rendered.Pixels;
                    int w = rendered.Width, h = rendered.Height;
                    int rot = rotations != null && idx < rotations.Length ? rotations[idx] : 0;
                    if (rot != 0) (raw, w, h) = BitmapHelpers.RotateBitmap(raw, w, h, rot);
                    var bytes = fmt == "png" ? BitmapHelpers.RenderToPng(raw, w, h, dpi) : BitmapHelpers.EncodeJpeg(raw, w, h, dpi);
                    if (approved is not null) AtomicFileOutput.Write(paths[written], bytes);
                    else AtomicFileOutput.WriteNew(paths[written], bytes);
                    written++;
                }
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                con.WriteLine($"Image export stopped after {written} of {selected.Count} images in {outDir}. " + error.Message);
                return 1;
            }
            CliReportIncomplete(incomplete, con);
            con.WriteLine($"Rendered {selected.Count} pages at {dpi:0} dpi ({fmt}) into {outDir}");
            return 0;
        }

        // ============================================================
        // --flatten <in.pdf> <out.pdf> [output options]
        // ============================================================
        // Same rasterize-and-rebuild the GUI's Save Flattened runs (150 dpi
        // default, engine-authored image pages sized in points), plus the rotation
        // handling the GUI gets for free from its normalized working copy.
        private static int CliFlatten(List<string> pos, Dictionary<string, string> options, TextWriter con)
        {
            if (pos.Count != 2)
            {
                con.WriteLine("Usage: KillerPDF.exe --flatten <in.pdf> <out.pdf> [--dpi <n>] [--color-mode color|grayscale|black-white] [--threshold <0-255>] [--compression lossless|jpeg] [--jpeg-quality <1-100>] [--scanned-look all|tilt,paper,grain,soft-focus,shadow] [--allow-incomplete]");
                return 2;
            }
            string inPath = Path.GetFullPath(pos[0]), outPath = Path.GetFullPath(pos[1]);
            if (!File.Exists(inPath)) { con.WriteLine($"Input not found: {inPath}"); return 2; }
            if (!TryReadDpi(options, 150, con, out double dpi)) return 2;
            PageColorMode colorMode = PageColorMode.Color;
            if (options.TryGetValue("--color-mode", out string? colorRaw))
            {
                colorMode = colorRaw.ToLowerInvariant() switch
                {
                    "color" => PageColorMode.Color,
                    "grayscale" or "gray" => PageColorMode.Grayscale,
                    "black-white" or "blackandwhite" or "bitonal" => PageColorMode.BlackAndWhite,
                    _ => throw new CliUsageException(
                        "--color-mode must be color, grayscale, or black-white."),
                };
            }
            int threshold = ParseBoundedIntOption(options, "--threshold", 160, 0, 255);
            int jpegQuality = ParseBoundedIntOption(options, "--jpeg-quality", 85, 1, 100);
            bool useJpeg = false;
            if (options.TryGetValue("--compression", out string? compressionRaw))
            {
                useJpeg = compressionRaw.ToLowerInvariant() switch
                {
                    "lossless" => false,
                    "jpeg" => true,
                    _ => throw new CliUsageException("--compression must be lossless or jpeg."),
                };
            }
            if (colorMode == PageColorMode.BlackAndWhite && useJpeg)
                throw new CliUsageException("Black-and-white flattening requires lossless compression.");
            ScanLookOptions? look = null;
            if (options.TryGetValue("--scanned-look", out string? lookRaw))
            {
                string[] effects = [.. lookRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(effect => effect.ToLowerInvariant())];
                string[] known = ["tilt", "paper", "grain", "soft-focus", "shadow"];
                if (effects.Length == 1 && effects[0] == "all") effects = known;
                if (effects.Length == 0 || effects.Any(effect => !known.Contains(effect)))
                    throw new CliUsageException("--scanned-look takes all, or any of tilt, paper, grain, soft-focus, shadow.");
                look = new ScanLookOptions(effects.Contains("tilt"), effects.Contains("paper"), effects.Contains("grain"),
                    effects.Contains("soft-focus"), effects.Contains("shadow"));
            }
            options.TryGetValue("--password", out var password);

            var (renderPath, rotations, dims) = CliPrepareRenderSource(inPath, password, con);

            using var renderSession =
                PdfPageRenderSession.OpenEngineFirst(renderPath, dpi / 72.0);
            int pageCount = renderSession.PageCount;
            PdfDocument renderDocument = PdfEngineIntegration.OpenForReading(File.ReadAllBytes(renderPath));
            IReadOnlyList<bool> bitonalHints =
                PdfPageRasterInformation.ReadBitonalImagePageHints(renderDocument);
            IReadOnlyList<bool> jpegHints =
                PdfPageRasterInformation.ReadJpegImagePageHints(renderDocument);

            bool allowIncomplete = options.ContainsKey("--allow-incomplete");
            var incomplete = new IncompletePageLog();
            var pages = new List<PdfEngineIntegration.RasterPage>(pageCount);
            for (int i = 0; i < pageCount; i++)
            {
                // Composite over white (#148): keeps the /SMask alpha channel out
                // of the rebuilt page images entirely.
                // #141: WithAnnotations, or the rebuild drops the file's own markup.
                PdfRenderedPage rendered = renderSession.RenderPage(i, requireComplete: !allowIncomplete);
                incomplete.Note(i, rendered);
                byte[] raw = rendered.Pixels;
                int w = rendered.Width, h = rendered.Height;
                int rot = rotations != null && i < rotations.Length ? rotations[i] : 0;
                if (rot != 0) (raw, w, h) = BitmapHelpers.RotateBitmap(raw, w, h, rot);
                double wPt, hPt;
                if (dims != null && i < dims.Length)
                {
                    // Page keeps its point size; swap for the viewed orientation.
                    bool swap = rot == 90 || rot == 270;
                    wPt = swap ? dims[i].HPt : dims[i].WPt;
                    hPt = swap ? dims[i].WPt : dims[i].HPt;
                }
                else
                {
                    wPt = w * 72.0 / dpi;
                    hPt = h * 72.0 / dpi;
                }

                if (colorMode == PageColorMode.Color && !useJpeg && look is null && rot == 0
                    && PdfPageRasterInformation.TryReadFullPageJpeg(
                        renderDocument, i, out PdfImage? original)
                    && original is not null
                    && OutputPixelDimensions.MatchesDpi(
                        original.Width, original.Height, wPt, hPt, dpi))
                {
                    pages.Add(new PdfEngineIntegration.RasterPage(
                        original.Width, original.Height, wPt, hPt,
                        ReadOnlyMemory<byte>.Empty, original.Data));
                    continue;
                }

                // The scanned look goes on before the color mode, as in Save Flattened.
                if (look is not null) raw = ScanLook.Apply(raw, w, h, dpi, look, i);
                PageQualityConverter.ApplyColorModeToBgra(raw, colorMode, threshold);
                bool bitonal = colorMode == PageColorMode.BlackAndWhite
                    || colorMode == PageColorMode.Color && look is null
                    && i < bitonalHints.Count && bitonalHints[i]
                    && BitonalPageDetector.IsOpaqueGrayscaleBgra(raw, w, h);
                bool grayscale = colorMode == PageColorMode.Grayscale;
                bool encodeJpeg = !bitonal && (useJpeg
                    || colorMode == PageColorMode.Color
                    && i < jpegHints.Count && jpegHints[i]);
                ReadOnlyMemory<byte> jpeg = encodeJpeg
                    ? BitmapHelpers.EncodeJpeg(raw, w, h, dpi, jpegQuality, grayscale)
                    : default;
                pages.Add(new PdfEngineIntegration.RasterPage(
                    w, h, wPt, hPt, raw, jpeg,
                    Bitonal: bitonal, Grayscale: grayscale));
            }
            if (!CliPublishOutput(inPath, outPath, PdfEngineIntegration.CreateRasterDocument(pages), con)) return 2;
            CliReportIncomplete(incomplete, con);
            con.WriteLine($"Flattened {pageCount} pages at {dpi:0} dpi -> {outPath}");
            return 0;
        }

        private static int ParseBoundedIntOption(
            Dictionary<string, string> options, string name, int fallback, int minimum, int maximum)
        {
            if (!options.TryGetValue(name, out string? raw)) return fallback;
            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
                || value < minimum || value > maximum)
                throw new CliUsageException($"{name} must be between {minimum} and {maximum}.");
            return value;
        }

        // ============================================================
        // --print <in.pdf> [--printer name] [--pages range] [--copies n]
        // ============================================================
        // Slimmed headless version of the GUI print spool: rasterize at 300
        // dpi (the GUI's print resolution), fit-scale each page centered on
        // the printable area, and write a disk-backed paginator to the
        // queue via XPS. Copies replicate the page sequence (ticket CopyCount
        // is unreliable across drivers - same reason the GUI does this, #83).
        private static int CliPrint(List<string> pos, Dictionary<string, string> options, TextWriter con)
        {
            if (pos.Count != 1)
            {
                con.WriteLine("Usage: KillerPDF.exe --print <in.pdf> [--printer <name>] [--pages <range>] [--copies <n>] [--duplex none|long|short] [--reverse] [--password <p>] [--allow-incomplete]");
                return 2;
            }
            string inPath = Path.GetFullPath(pos[0]);
            if (!File.Exists(inPath)) { con.WriteLine($"Input not found: {inPath}"); return 2; }

            int copies = 1;
            if (options.TryGetValue("--copies", out var copiesRaw) &&
                (!int.TryParse(copiesRaw, out copies) || copies < 1 || copies > 99))
            { con.WriteLine("--copies must be 1-99"); return 2; }
            bool twoSided = false, shortEdge = false;
            bool setDuplex = options.TryGetValue("--duplex", out var duplexRaw);
            if (setDuplex && !PrintJobTicket.TryParseDuplex(duplexRaw!, out twoSided, out shortEdge))
            { con.WriteLine("--duplex must be none, long or short"); return 2; }
            bool reverse = options.ContainsKey("--reverse");
            bool allowIncomplete = options.ContainsKey("--allow-incomplete");
            var incomplete = new IncompletePageLog();

            options.TryGetValue("--password", out var password);
            // The file's own print permission stands unless --password is its owner password.
            PdfAccess printAccess = PdfRestrictions.Read(inPath, password);
            PrintAllowance printAllowance = printAccess.Print;
            if (printAllowance == PrintAllowance.Forbidden)
            {
                con.WriteLine(CliProtectionGate.PrintRefusal(printAccess));
                return 1;
            }
            var (renderPath, rotations, _) = CliPrepareRenderSource(inPath, password, con, ownerOnly: false);

            // Resolve the print queue. Match --printer against FullName,
            // exact first then substring, both case-insensitive.
            using var server = new LocalPrintServer();
            PrintQueue? queue = null;
            if (options.TryGetValue("--printer", out var printerName) && !string.IsNullOrWhiteSpace(printerName))
            {
                var queues = server.GetPrintQueues(
                    [EnumeratedPrintQueueTypes.Local, EnumeratedPrintQueueTypes.Connections]).ToList();
                queue = queues.FirstOrDefault(q =>
                            string.Equals(q.FullName, printerName, StringComparison.OrdinalIgnoreCase))
                     ?? queues.FirstOrDefault(q =>
                            q.FullName.Contains(printerName, StringComparison.OrdinalIgnoreCase));
                if (queue is null)
                {
                    con.WriteLine($"Printer not found: {printerName}. Available:");
                    foreach (var q in queues) con.WriteLine("  " + q.FullName);
                    return 2;
                }
            }
            else
            {
                queue = LocalPrintServer.GetDefaultPrintQueue();
            }

            // Rasterize the selected pages at 300 dpi (150 where only low quality printing is allowed),
            // rotation-corrected.
            using var document = new PrintRasterDocument(copies);
            List<int> selected;
            using (var renderSession = PrintPermission.OpenRenderSession(renderPath, printAllowance))
            {
                int pageCount = renderSession.PageCount;
                if (options.TryGetValue("--pages", out var rangeSpec))
                {
                    var parsed = CliParsePageRange(rangeSpec, pageCount, out string err);
                    if (parsed is null) { con.WriteLine(err); return 2; }
                    selected = parsed;
                }
                else
                {
                    selected = [.. Enumerable.Range(0, pageCount)];
                }
                if (reverse) selected.Reverse();
                foreach (var idx in selected)
                {
                    PdfRenderedPage rendered = renderSession.RenderPage(idx, requireComplete: !allowIncomplete);
                    incomplete.Note(idx, rendered);
                    byte[] raw = rendered.Pixels;
                    int w = rendered.Width, h = rendered.Height;
                    int rot = rotations != null && idx < rotations.Length ? rotations[idx] : 0;
                    if (rot != 0) (raw, w, h) = BitmapHelpers.RotateBitmap(raw, w, h, rot);
                    var bs = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, raw, w * 4);
                    bs.Freeze();
                    document.Add(bs);
                }
            }

            // Orient the sheet to the majority of the selected pages.
            bool landscape = document.Landscape;

            var pd = new System.Windows.Controls.PrintDialog { PrintQueue = queue };
            var ticket = pd.PrintTicket;
            ticket.CopyCount = 1;
            ticket.PageOrientation = landscape ? PageOrientation.Landscape : PageOrientation.Portrait;
            if (setDuplex)
                ticket.Duplexing = !twoSided ? Duplexing.OneSided
                    : shortEdge ? Duplexing.TwoSidedShortEdge : Duplexing.TwoSidedLongEdge;
            pd.PrintTicket = ticket;
            double aw = pd.PrintableAreaWidth, ah = pd.PrintableAreaHeight;
            if (landscape && ah > aw) (aw, ah) = (ah, aw);

            document.PageSize = new Size(aw, ah);
            // All pages passed render validation before any job is submitted. The paginator
            // decodes only the current small cache instead of retaining the entire job's pixels.
            var writer = PrintQueue.CreateXpsDocumentWriter(queue);
            writer.Write(document, ticket);
            CliReportIncomplete(incomplete, con);
            con.WriteLine($"Sent {selected.Count} pages x{copies} to \"{queue.FullName}\".");
            return 0;
        }

        // ============================================================
        // --ocr <in.pdf> <out.pdf> [--lang code]
        // ============================================================
        // Reuses the GUI's searchable-PDF core (OcrController.BuildSearchablePdf):
        // Engine render, OCR per page, invisible text drawn over each
        // word. The GUI's model-download gate is dialog-driven, so the CLI
        // has its own silent equivalent honoring the OcrHighQuality setting.
        private static int CliOcr(List<string> pos, Dictionary<string, string> options, TextWriter con)
        {
            if (pos.Count != 2)
            {
                con.WriteLine("Usage: KillerPDF.exe --ocr <in.pdf> <out.pdf> [--lang <code>]   (default eng)");
                return 2;
            }
            string inPath = Path.GetFullPath(pos[0]), outPath = Path.GetFullPath(pos[1]);
            if (!File.Exists(inPath)) { con.WriteLine($"Input not found: {inPath}"); return 2; }
            options.TryGetValue("--lang", out var langRaw);
            string lang = string.IsNullOrWhiteSpace(langRaw) ? "eng" : langRaw!.Trim().ToLowerInvariant();

            if (!CliEnsureOcrLanguage(lang, con)) return 1;

            options.TryGetValue("--password", out var password);
            var (srcForOcr, rotations, _) = CliPrepareRenderSource(inPath, password, con);

            CliEnsureParentDir(outPath);
            var (pages, words) = OcrController.BuildSearchablePdf(srcForOcr, outPath,
                (i, n) => { if (i == 0 || i + 1 == n || (i + 1) % 10 == 0) con.WriteLine($"OCR page {i + 1}/{n}"); },
                lang, formAware: false, ct: CancellationToken.None);

            // The render source had /Rotate stripped; put the angles back on the
            // output so rotated pages still display rotated. Content and text
            // layer share page space, so they stay aligned.
            if (rotations != null && rotations.Any(r => r != 0))
            {
                var restoredRotations = rotations
                    .Select((rotation, pageIndex) => (pageIndex, rotation))
                    .Where(item => item.rotation != 0)
                    .ToDictionary(item => item.pageIndex, item => item.rotation);
                PdfEngineIntegration.ApplyPageRotations(outPath, restoredRotations);
            }

            con.WriteLine($"OCR complete: {pages} pages, {words} words -> {outPath}");
            return 0;
        }

        // ============================================================
        // --scan-to-text <in.pdf> <out.pdf> [--lang code] [--review <folder>] [--keep-scanned <file>]
        // ============================================================
        // Reuses the GUI's core (OcrController.BuildScanTextPdf): scanned pages become real
        // text in the best-matching installed typeface, drawn over the page image with the
        // replaced words erased. Pages with existing text or drawings are left as they are.
        // --review writes each converted word as scanned beside it as text, the way the app's
        // review shows it, and --keep-scanned keeps the listed words (page:word) as scanned.
        private static int CliScanToText(List<string> pos, Dictionary<string, string> options, TextWriter con)
        {
            if (pos.Count != 2)
            {
                con.WriteLine("Usage: KillerPDF.exe --scan-to-text <in.pdf> <out.pdf> [--lang <code>] [--review <folder>] [--keep-scanned <file>]");
                con.WriteLine("  --review writes p<page>-w<word>.png, the word as scanned beside it as text, and review.json listing them,");
                con.WriteLine("  least certain first. --keep-scanned reads page:word ids, one or more per line, to keep as scanned.");
                return 2;
            }
            string inPath = Path.GetFullPath(pos[0]), outPath = Path.GetFullPath(pos[1]);
            if (!File.Exists(inPath)) { con.WriteLine($"Input not found: {inPath}"); return 2; }
            string? reviewFolder = options.TryGetValue("--review", out string? reviewRaw) && reviewRaw.Length > 0
                ? Path.GetFullPath(reviewRaw) : null;
            HashSet<string>? keep = null;
            if (options.TryGetValue("--keep-scanned", out string? keepRaw))
            {
                string keepPath = Path.GetFullPath(keepRaw);
                if (!File.Exists(keepPath)) { con.WriteLine($"Keep list not found: {keepPath}"); return 2; }
                keep = [.. File.ReadAllLines(keepPath)
                    .SelectMany(line => line.Split([',', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))];
                string? bad = keep.FirstOrDefault(id => id.Split(':') is not [{ } page, { } word]
                    || !int.TryParse(page, out int p) || p < 1 || !int.TryParse(word, out int w) || w < 1);
                if (bad is not null) { con.WriteLine($"--keep-scanned ids look like 3:17 (page 3, word 17), not {bad}."); return 2; }
            }
            options.TryGetValue("--lang", out var langRaw);
            string lang = string.IsNullOrWhiteSpace(langRaw) ? "eng" : langRaw!.Trim().ToLowerInvariant();

            if (!CliEnsureOcrLanguage(lang, con)) return 1;

            options.TryGetValue("--password", out var password);
            var (source, rotations, _) = CliPrepareRenderSource(inPath, password, con);
            // Conversion reads each page the way it is displayed and writes the text in that
            // orientation, so the rotation the render copy dropped goes back on that copy first.
            if (rotations != null && rotations.Any(r => r != 0))
                PdfEngineIntegration.ApplyPageRotations(source, rotations
                    .Select((rotation, pageIndex) => (pageIndex, rotation))
                    .Where(item => item.rotation != 0)
                    .ToDictionary(item => item.pageIndex, item => item.rotation));

            CliEnsureParentDir(outPath);
            int reviewed = 0;
            var summary = OcrController.BuildScanTextPdf(source, outPath,
                (i, n) => { if (i == 0 || i + 1 == n || (i + 1) % 10 == 0) con.WriteLine($"Page {i + 1}/{n}"); },
                lang, CancellationToken.None, keep,
                reviewFolder is null ? null : words =>
                {
                    reviewed = CliWriteScanTextReview(reviewFolder, words);
                    return true;
                });
            if (summary.ScannedPages == 0)
            {
                con.WriteLine("No scanned pages to convert; nothing was written.");
                return 1;
            }

            if (reviewFolder is not null) con.WriteLine($"Wrote {reviewed} word comparisons -> {reviewFolder}");
            con.WriteLine($"Converted {summary.ScannedPages} scanned pages: {summary.ConvertedWords} words are now text, "
                + $"{summary.KeptWords} kept as scanned -> {outPath}");
            return 0;
        }

        // Each converted word as scanned, a gap, then as text, in one PNG per word, and an index.
        private static int CliWriteScanTextReview(string folder, IReadOnlyList<OcrController.ScanTextReviewWord> words)
        {
            Directory.CreateDirectory(folder);
            const int Gap = 12;
            var index = new List<object>();
            foreach (OcrController.ScanTextReviewWord word in words
                .OrderBy(word => double.IsNaN(word.Word.Match) ? 1 : word.Word.Match).ThenBy(word => word.PageIndex))
            {
                int width = word.Width * 2 + Gap, height = word.Height;
                byte[] pixels = Enumerable.Repeat((byte)255, width * height).ToArray();
                for (int y = 0; y < height; y++)
                {
                    Array.Copy(word.Scan, y * word.Width, pixels, y * width, word.Width);
                    if (word.Typeset.Length == word.Scan.Length)
                        Array.Copy(word.Typeset, y * word.Width, pixels, y * width + word.Width + Gap, word.Width);
                }
                string name = "p" + word.Id.Replace(":", "-w", StringComparison.Ordinal) + ".png";
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray8, null, pixels, width)));
                using (FileStream file = File.Create(Path.Combine(folder, name)))
                    encoder.Save(file);
                index.Add(new { id = word.Id, page = word.PageIndex + 1, text = word.Word.Word.Text,
                    match = double.IsNaN(word.Word.Match) ? (double?)null : Math.Round(word.Word.Match, 3), image = name });
            }
            File.WriteAllText(Path.Combine(folder, "review.json"),
                JsonSerializer.Serialize(index, new JsonSerializerOptions { WriteIndented = true }));
            return index.Count;
        }

        private static int CliRotatePages(List<string> pos, TextWriter con)
        {
            if (pos.Count != 4 || !int.TryParse(pos[2], out int degrees) || degrees is not (90 or 180 or 270))
            {
                con.WriteLine("Usage: KillerPDF.exe --rotate-pages <in.pdf> <pages> <90|180|270> <out.pdf>");
                return 2;
            }
            if (!CliInspectEditInput(pos[0], pos[3], out string source, out string output, out int pageCount, con)) return 2;
            List<int>? pages = CliParsePageRange(pos[1], pageCount, out string error);
            if (pages is null) { con.WriteLine(error); return 2; }
            CliWriteEditedCopy(source, output, path => PdfEngineIntegration.RotatePages(path, pages, degrees));
            con.WriteLine($"Rotated {pages.Count} pages by {degrees} degrees -> {output}");
            return 0;
        }

        private static int CliDeletePages(List<string> pos, TextWriter con)
        {
            if (pos.Count != 3)
            {
                con.WriteLine("Usage: KillerPDF.exe --delete-pages <in.pdf> <pages> <out.pdf>");
                return 2;
            }
            if (!CliInspectEditInput(pos[0], pos[2], out string source, out string output, out int pageCount, con)) return 2;
            List<int>? pages = CliParsePageRange(pos[1], pageCount, out string error);
            if (pages is null || pages.Count >= pageCount)
            {
                con.WriteLine(pages is null ? error : "A PDF must retain at least one page.");
                return 2;
            }
            CliWriteEditedCopy(source, output, path => PdfEngineIntegration.RemovePages(path, pages));
            con.WriteLine($"Deleted {pages.Count} pages -> {output}");
            return 0;
        }

        private static int CliMovePages(List<string> pos, TextWriter con)
        {
            if (pos.Count != 4 || !int.TryParse(pos[2], out int position))
            {
                con.WriteLine("Usage: KillerPDF.exe --move-pages <in.pdf> <pages> <position> <out.pdf>");
                return 2;
            }
            if (!CliInspectEditInput(pos[0], pos[3], out string source, out string output, out int pageCount, con)) return 2;
            List<int>? pages = CliParsePageRange(pos[1], pageCount, out string error);
            if (pages is null || position < 1 || position > pageCount + 1)
            {
                con.WriteLine(pages is null ? error : $"Position must be between 1 and {pageCount + 1}.");
                return 2;
            }
            CliWriteEditedCopy(source, output, path => PdfEngineIntegration.MovePages(path, pages, position - 1));
            con.WriteLine($"Moved {pages.Count} pages before position {position} -> {output}");
            return 0;
        }

        private static int CliInsertBlank(List<string> pos, Dictionary<string, string> options, TextWriter con)
        {
            if (pos.Count != 3 || !int.TryParse(pos[1], out int position))
            {
                con.WriteLine("Usage: KillerPDF.exe --insert-blank <in.pdf> <position> <out.pdf> [--width <points>] [--height <points>]");
                return 2;
            }
            if (!CliInspectEditInput(pos[0], pos[2], out string source, out string output, out int pageCount, con)) return 2;
            double width = CliPositiveDouble(options, "--width", 612);
            double height = CliPositiveDouble(options, "--height", 792);
            if (position < 1 || position > pageCount + 1 || width <= 0 || height <= 0)
            {
                con.WriteLine(position < 1 || position > pageCount + 1
                    ? $"Position must be between 1 and {pageCount + 1}."
                    : "Width and height must be positive numbers.");
                return 2;
            }
            CliWriteEditedCopy(source, output, path => PdfEngineIntegration.InsertBlankPage(path, position - 1, width, height));
            con.WriteLine($"Inserted a {width:0.##} x {height:0.##} point page at position {position} -> {output}");
            return 0;
        }

        private static int CliDuplicatePage(List<string> pos, TextWriter con)
        {
            if (pos.Count != 3 || !int.TryParse(pos[1], out int page))
            {
                con.WriteLine("Usage: KillerPDF.exe --duplicate-page <in.pdf> <page> <out.pdf>");
                return 2;
            }
            if (!CliInspectEditInput(pos[0], pos[2], out string source, out string output, out int pageCount, con)) return 2;
            if (page < 1 || page > pageCount)
            {
                con.WriteLine($"Page must be between 1 and {pageCount}.");
                return 2;
            }
            CliWriteEditedCopy(source, output, path => PdfEngineIntegration.DuplicatePage(path, page - 1));
            con.WriteLine($"Duplicated page {page} -> {output}");
            return 0;
        }

        private static int CliDocumentInfo(List<string> pos, TextWriter con)
        {
            if (pos.Count != 1 || !File.Exists(pos[0]))
            {
                con.WriteLine("Usage: KillerPDF.exe --document-info <in.pdf>");
                return 2;
            }
            string path = Path.GetFullPath(pos[0]);
            PdfDocumentInformation info = PdfDocumentInformation.Read(PdfEngineIntegration.OpenForReading(File.ReadAllBytes(path)));
            con.WriteLine(JsonSerializer.Serialize(new
            {
                path, info.PageCount, version = info.Version.ToString(), info.Title, info.Author,
                info.Subject, info.Keywords, info.Creator, info.Producer, info.Language,
                info.CreationDate, info.ModificationDate, trapped = info.Trapped?.ToString()
            }));
            return 0;
        }

        private static int CliSearchText(List<string> pos, TextWriter con)
        {
            if (pos.Count != 2 || !File.Exists(pos[0]) || string.IsNullOrWhiteSpace(pos[1]))
            {
                con.WriteLine("Usage: KillerPDF.exe --search-text <in.pdf> <query>");
                return 2;
            }
            string path = Path.GetFullPath(pos[0]);
            // A script needs to tell "not found" from "could not read", so that gets its own exit code.
            try { using (PdfContentDocument.Open(path)) { } }
            catch (Exception ex)
            {
                con.WriteLine("Could not read the document: " + FlattenBatchDetail(ex.Message));
                return 1;
            }
            SearchResult found;
            try { found = SearchService.Search(path, pos[1]); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                con.WriteLine("Could not read the document: " + FlattenBatchDetail(ex.Message));
                return 1;
            }
            con.WriteLine(JsonSerializer.Serialize(new
            {
                path, query = pos[1], totalHits = found.TotalHits,
                pages = found.ResultPages.Select(page => page + 1).ToArray(),
                unreadablePages = found.UnreadablePages.Select(page => page + 1).ToArray()
            }));
            // A hit count that left pages out is not a "not found": a script reads 3 as partial, never as absent.
            return found.UnreadablePages.Count > 0 ? 3 : 0;
        }

        private static bool CliInspectEditInput(
            string input, string destination, out string source, out string output, out int pageCount, TextWriter con,
            string? password = null)
        {
            source = Path.GetFullPath(input);
            output = Path.GetFullPath(destination);
            pageCount = 0;
            if (!File.Exists(source)) { con.WriteLine($"Input not found: {source}"); return false; }
            if (string.Equals(source, output, StringComparison.OrdinalIgnoreCase))
            { con.WriteLine("Output file cannot also be the input."); return false; }
            byte[] bytes = File.ReadAllBytes(source);
            PdfDocument? authenticated = null;
            if (!string.IsNullOrEmpty(password)) PdfRestrictions.Open(bytes, password, out authenticated);
            pageCount = PdfDocumentInformation.Read(authenticated ?? PdfEngineIntegration.OpenForReading(bytes)).PageCount;
            return true;
        }

        // Every single-output command publishes through here: the output may not be the input
        // under any spelling, and the bytes land through a sibling temp file and one move, so a
        // failure part way leaves either the old file or none, never a truncated one.
        private static bool CliPublishOutput(string inPath, string outPath, byte[] data, TextWriter con)
        {
            if (string.Equals(Path.GetFullPath(inPath), Path.GetFullPath(outPath), StringComparison.OrdinalIgnoreCase))
            {
                con.WriteLine("Output file cannot also be the input.");
                return false;
            }
            CliEnsureParentDir(outPath);
            AtomicFileOutput.Write(outPath, data);
            return true;
        }

        private static void CliWriteEditedCopy(string source, string output, Action<string> edit)
        {
            CliEnsureParentDir(output);
            string temporary = Path.Combine(Path.GetDirectoryName(output)!,
                "." + Path.GetFileName(output) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.Copy(source, temporary);
                edit(temporary);
                File.Move(temporary, output, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        private static double CliPositiveDouble(Dictionary<string, string> options, string name, double fallback)
        {
            if (!options.TryGetValue(name, out string? raw)) return fallback;
            return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                && double.IsFinite(value) && value > 0 ? value : -1;
        }

        /// <summary>
        /// Silent equivalent of the GUI's model-download gate: nothing is
        /// bundled - every language model streams from the tessdata repos on
        /// first use, honoring the app's High Quality setting, with the same
        /// .part-then-move atomicity. Runs the download on the thread pool -
        /// OnStartup's dispatcher is not pumping, so awaiting here directly
        /// would deadlock on the captured WPF context.
        /// </summary>
        private static bool CliEnsureOcrLanguage(string lang, TextWriter con)
        {
            // The code becomes a file name under the models folder and a download URL, so only
            // a real tessdata model name is accepted.
            if (!OcrCatalog.IsModelCode(lang))
            {
                con.WriteLine($"'{lang}' is not an OCR language code (e.g. eng, spa, fra, deu, jpn, tur, ben, chi_sim, chi_tra).");
                return false;
            }
            OcrNativeBootstrap.EnsureLanguageData();
            var dest = Path.Combine(OcrNativeBootstrap.TessDataDir, lang + ".traineddata");
            if (File.Exists(dest)) return true;

            bool hq = App.GetSetting("OcrHighQuality") == "1";
            string url = (hq
                ? "https://raw.githubusercontent.com/tesseract-ocr/tessdata_best/main/"
                : "https://raw.githubusercontent.com/tesseract-ocr/tessdata_fast/main/") + lang + ".traineddata";
            con.WriteLine($"Downloading OCR language '{lang}' ({(hq ? "high quality" : "standard")})...");
            try
            {
                Task.Run(async () =>
                {
                    using var http = OcrLanguages.MakeDownloadClient();
                    var part = dest + "." + Guid.NewGuid().ToString("N") + ".part";
                    try
                    {
                        using (var f = File.Create(part))
                            await HttpDownload.CopyToAsync(http, new Uri(url), f, TimeSpan.FromSeconds(100)).ConfigureAwait(false);
                        File.Move(part, dest, overwrite: true);
                    }
                    finally
                    {
                        try { if (File.Exists(part)) File.Delete(part); } catch { }
                    }
                }).GetAwaiter().GetResult();
                return true;
            }
            catch (Exception ex)
            {
                con.WriteLine($"Could not download language '{lang}': " + FlattenBatchDetail(ex.Message));
                con.WriteLine("Check the language code (e.g. eng, spa, fra, deu, jpn, tur, ben, chi_sim, chi_tra) and your connection.");
                return false;
            }
        }
    }
}
