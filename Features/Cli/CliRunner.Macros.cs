using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using KillerPdf.Engine.Documents;
using KillerPDF.Services;

namespace KillerPDF.Features
{
    // Macros from the command line: run a saved macro, an exported macro file or one of the
    // starters through the same engine dispatcher the Macros dialog uses, with the OCR, Redact,
    // Watermark, Data Merge, Rename and Save steps supplied here instead of by the window.
    internal static partial class CliRunner
    {
        private const string MacroUsage =
            "Usage: KillerPDF.exe --macro <in.pdf> <macro> <out.pdf> [--lang <code>] [--text <phrase>] [--terms <file>]\n"
            + "                     [--data <records.csv|json|xlsx>] [--rasterize] [--json] [--password <p>]\n"
            + "       KillerPDF.exe --macro --list\n"
            + "  <macro> is a saved macro's name, an exported macro .json file, or starter:archival, starter:sharing,\n"
            + "  starter:scanning or starter:privacy. --list shows the saved macros and the starters.\n"
            + "  --password opens a protected input; a PDF whose owner restricts editing needs the owner password.\n"
            + "  OCR steps read in --lang (default eng). Redact steps remove --text, each line of --terms and the\n"
            + "  marks saved in the file. If any page requires rasterization, the macro fails without saving\n"
            + "  output unless --rasterize is given. If a page's text, form fields or comments could not be read,\n"
            + "  the Redact step fails naming those pages and no output is saved.\n"
            + "  Rename steps use the out.pdf folder and the macro's file name template.\n"
            + "  Data Merge steps require --data and write one editable PDF per included record into that folder.\n"
            + "  Export steps save an extra file alongside the editable PDF; multiple exports use numbered suffixes.";

        // ============================================================
        // --macro <in.pdf> <macro> <out.pdf> ... | --macro --list
        // ============================================================
        private static int CliMacro(List<string> pos, Dictionary<string, string> options, TextWriter con)
        {
            if (options.ContainsKey("--list") && pos.Count == 0) return CliListMacros(con);
            if (pos.Count != 3) { con.WriteLine(MacroUsage); return 2; }
            if (!CliInspectEditInput(pos[0], pos[2], out string source, out string output, out _, con)) return 2;
            if (CliResolveMacro(pos[1], con) is not PdfMacro macro) return 2;
            string[] protectedPaths = [source, .. new[] { pos[1], options.GetValueOrDefault("--data"),
                options.GetValueOrDefault("--terms") }.OfType<string>().Where(File.Exists).Select(Path.GetFullPath)];
            bool merges = macro.Steps.Any(step => step.Operation == PdfMacroOperation.DataMerge);
            bool renames = !merges && macro.Steps.Any(step => step.Operation == PdfMacroOperation.Rename);
            PdfDataMergeMacroPlan? mergePlan = null;
            if (merges)
            {
                if (!options.TryGetValue("--data", out string? dataPath) || string.IsNullOrWhiteSpace(dataPath))
                {
                    con.WriteLine("Data Merge macro steps require --data <records.csv|json|xlsx>.");
                    return 2;
                }
                try { mergePlan = PdfDataMergeWorkflow.PlanMacro(macro,
                    PdfDataMergeWorkflow.LoadRecords(dataPath), source, output, protectedSources: protectedPaths); }
                catch (Exception error) when (error is ArgumentException or InvalidOperationException
                    or IOException or UnauthorizedAccessException or FormatException or JsonException or NotSupportedException)
                {
                    con.WriteLine(error.Message);
                    return 2;
                }
            }
            if (renames)
            {
                try { output = PdfEngineIntegration.PlanMacroOutputs(macro, [source], output).Single(); }
                catch (ArgumentException error) { con.WriteLine(error.Message); return 2; }
                if (!CliInspectEditInput(source, output, out source, out output, out _, con)) return 2;
            }

            string[] plannedFiles;
            try { plannedFiles = PdfMacroExportFiles.Plan(macro, [output], protectedPaths); }
            catch (ArgumentException error) { con.WriteLine(error.Message); return 2; }

            options.TryGetValue("--password", out string? password);
            options.TryGetValue("--lang", out string? langRaw);
            string language = string.IsNullOrWhiteSpace(langRaw) ? "eng" : langRaw.Trim().ToLowerInvariant();
            var redaction = new CliMacroRedaction { Rasterize = options.ContainsKey("--rasterize") };
            if (options.TryGetValue("--text", out string? text) && text.Trim().Length > 0)
                redaction.Terms.Add(text.Trim());
            if (options.TryGetValue("--terms", out string? termsPath))
            {
                string full = Path.GetFullPath(termsPath);
                if (!File.Exists(full)) { con.WriteLine($"Terms file not found: {full}"); return 2; }
                redaction.Terms.AddRange(File.ReadAllLines(full).Select(line => line.Trim())
                    .Where(line => line.Length > 0));
            }
            redaction.Terms = [.. redaction.Terms.Distinct(StringComparer.Ordinal)];

            PdfMacroDispatcher dispatcher = new([
                new PdfMacroOperationRegistration(PdfMacroOperation.Save,
                    (_, data, _, token) =>
                    {
                        token.ThrowIfCancellationRequested();
                        return new PdfMacroOperationResult(data);
                    }),
                new PdfMacroOperationRegistration(PdfMacroOperation.Ocr,
                    (_, data, _, token) => CliMacroOcr(data, language, password, con, token)),
                new PdfMacroOperationRegistration(PdfMacroOperation.Redact,
                    (_, data, _, token) => CliMacroRedact(data, redaction, token)),
                new PdfMacroOperationRegistration(PdfMacroOperation.Watermark,
                    PdfEngineBurn.ExecuteWatermarkMacro),
                new PdfMacroOperationRegistration(PdfMacroOperation.Resize, PdfResizeMacro.Execute),
                new PdfMacroOperationRegistration(PdfMacroOperation.ConvertColor, PdfColorConversionMacro.Execute),
                new PdfMacroOperationRegistration(PdfMacroOperation.DataMerge,
                    (step, data, _, token) => PdfDataMergeMacro.Execute(step, data,
                        mergePlan?.Merge.Records ?? throw new InvalidOperationException("Merge data has not been selected."),
                        token, PdfDataMergeWorkflow.LoadImage)),
                new PdfMacroOperationRegistration(PdfMacroOperation.Rename, PdfRenameMacro.Execute)]);

            PdfMacroOperation[] unsupported = [.. macro.Steps.Select(step => step.Operation)
                .Where(operation => !dispatcher.CanExecute(operation)).Distinct()];
            if (unsupported.Length > 0)
            {
                con.WriteLine($"The macro \"{macro.Name}\" uses steps the command line cannot run: "
                    + string.Join(", ", unsupported) + ".");
                return 2;
            }
            bool redacts = macro.Steps.Any(step => step.Operation == PdfMacroOperation.Redact);
            if (redacts)
            {
                redaction.Marked = [.. PdfEngineIntegration.ReadRedactionMarks(
                        PdfEngineIntegration.OpenForReading(File.ReadAllBytes(source)))
                    .Select(mark => new PdfRedactionRegion(mark.PageIndex,
                        new PdfContentBounds(mark.Left, mark.Bottom, mark.Right, mark.Top), null, mark.OverlayText)
                    { FillColor = mark.FillColor })];
                if (redaction.Terms.Count == 0 && redaction.Marked.Count == 0)
                {
                    con.WriteLine($"The macro \"{macro.Name}\" redacts, but there is nothing to remove: "
                        + "name --text or --terms, or save redaction marks in the file.");
                    return 2;
                }
            }
            if (macro.Steps.Any(step => step.Operation == PdfMacroOperation.Ocr)
                && !CliEnsureOcrLanguage(language, con))
                return 1;

            con.WriteLine($"Running \"{macro.Name}\": " + string.Join(", ", macro.Steps.Select(step => step.Operation)));
            if (renames) con.WriteLine($"Output: {output}");
            if (mergePlan is not null)
                foreach (string path in mergePlan.OutputPaths) con.WriteLine($"Output: {path}");
            PdfMacroRunReport report = dispatcher.RunReport(macro, [new ReadOnlyMemory<byte>(File.ReadAllBytes(source))],
                renames || merges ? PdfRenameMacro.CreateContext(Path.GetFileName(source), 1) : null);
            PdfMacroFileResult? result = report.Results.Count == 1 ? report.Results[0] : null;
            bool outputsWritten = true;
            if (mergePlan is not null && result?.Outputs is not null && (!redacts || result.Succeeded))
                outputsWritten = CliWriteMacroOutputs(result.Outputs, mergePlan.OutputPaths, con);
            if (result is { Succeeded: true, Data: ReadOnlyMemory<byte> data })
            {
                if (renames && result.OutputFileName != Path.GetFileName(output))
                {
                    con.WriteLine("The macro output name differs from its planned target.");
                    return 1;
                }
                outputsWritten = CliWriteMacroOutputs([new(data, Path.GetFileName(output))
                    { Exports = result.Exports }], plannedFiles, con);
            }

            con.WriteLine(options.ContainsKey("--json") ? report.ToJson(indented: true) : report.ToText());
            if (redacts && result?.Succeeded == true && redaction.Areas > 0)
                con.WriteLine($"Redacted {redaction.Areas} areas on {redaction.Pages} pages"
                    + (redaction.Rasterized > 0 ? $", turning {redaction.Rasterized} pages into images." : "."));
            else if (redacts && result?.Succeeded == true && redaction.Blocked.Count == 0)
                con.WriteLine("The Redact step found nothing to remove.");
            if (redaction.Blocked.Count > 0)
                con.WriteLine($"No macro output was saved. Pages {string.Join(", ", redaction.Blocked.Order().Select(page => page + 1))} "
                    + "require rasterization; add --rasterize to allow that.");
            if (result?.Succeeded != true || !outputsWritten) return 1;
            return 0;
        }

        private static bool CliWriteMacroOutputs(IReadOnlyList<PdfMacroOutput> generated,
            IReadOnlyList<string> planned, TextWriter con)
        {
            var targets = planned.ToDictionary(path => Path.GetFileName(path), path => path, StringComparer.OrdinalIgnoreCase);
            PdfMacroOutput[] successful = [.. generated.Where(result => result.Succeeded).SelectMany(PdfMacroExportFiles.Expand)];
            if (successful.Any(result => result.OutputFileName is null || !targets.ContainsKey(result.OutputFileName))
                || successful.Select(result => result.OutputFileName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != successful.Length)
            {
                con.WriteLine("The macro output names differ from their planned targets.");
                return false;
            }
            bool allWritten = true;
            foreach (PdfMacroOutput result in successful)
            {
                string destination = targets[result.OutputFileName!];
                string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    CliEnsureParentDir(destination);
                    File.WriteAllBytes(temporary, result.Data!.Value.ToArray());
                    File.Move(temporary, destination, overwrite: true);
                    con.WriteLine($"Saved -> {destination}");
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    con.WriteLine(destination + ": " + error.Message);
                    allWritten = false;
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            return allWritten;
        }

        private static int CliListMacros(TextWriter con)
        {
            IReadOnlyList<PdfMacro> saved;
            try { saved = new PdfMacroStore().LoadAll(); }
            catch (Exception ex) when (ex is JsonException or ArgumentException or NotSupportedException
                or IOException or UnauthorizedAccessException)
            {
                con.WriteLine("Could not read the saved macros: " + ex.Message);
                return 1;
            }
            con.WriteLine(saved.Count == 0 ? "No saved macros." : "Saved macros:");
            foreach (PdfMacro macro in saved)
                con.WriteLine($"  {macro.Name}: " + string.Join(", ", macro.Steps.Select(step => step.Operation)));
            con.WriteLine("Starters:");
            foreach (PdfMacroStarterKind kind in Enum.GetValues<PdfMacroStarterKind>())
                con.WriteLine($"  starter:{kind.ToString().ToLowerInvariant()}: "
                    + string.Join(", ", PdfMacro.CreateStarter(kind).Steps.Select(step => step.Operation)));
            return 0;
        }

        // A starter by name, an exported macro file, or a saved macro by name; null after reporting why not.
        private static PdfMacro? CliResolveMacro(string value, TextWriter con)
        {
            const string StarterPrefix = "starter:";
            if (value.StartsWith(StarterPrefix, StringComparison.OrdinalIgnoreCase))
            {
                if (Enum.TryParse(value[StarterPrefix.Length..], ignoreCase: true, out PdfMacroStarterKind kind)
                    && Enum.IsDefined(kind))
                    return PdfMacro.CreateStarter(kind);
                con.WriteLine("The starters are starter:archival, starter:sharing, starter:scanning and starter:privacy.");
                return null;
            }
            try
            {
                if (File.Exists(value)) return PdfMacro.FromJson(File.ReadAllText(value));
                PdfMacro? saved = new PdfMacroStore().LoadAll()
                    .FirstOrDefault(macro => string.Equals(macro.Name, value, StringComparison.OrdinalIgnoreCase));
                if (saved is not null) return saved;
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException or NotSupportedException
                or IOException or UnauthorizedAccessException)
            {
                con.WriteLine("Could not read the macro: " + ex.Message);
                return null;
            }
            con.WriteLine($"No saved macro or macro file named \"{value}\". Run --macro --list to see the saved macros.");
            return null;
        }

        // Adds the searchable text layer exactly as --ocr does, on temporary copies.
        private static PdfMacroOperationResult CliMacroOcr(ReadOnlyMemory<byte> data, string language,
            string? password, TextWriter con, CancellationToken token)
        {
            string input = App.MakeTempFile("climacroocrsrc");
            string output = App.MakeTempFile("climacroocr");
            try
            {
                File.WriteAllBytes(input, data.ToArray());
                var (renderSource, rotations, _) = CliPrepareRenderSource(input, password, con);
                OcrController.BuildSearchablePdf(renderSource, output,
                    (i, n) => { if (i == 0 || i + 1 == n || (i + 1) % 10 == 0) con.WriteLine($"OCR page {i + 1}/{n}"); },
                    language, formAware: false, ct: token);
                if (rotations != null && rotations.Any(rotation => rotation != 0))
                    PdfEngineIntegration.ApplyPageRotations(output, rotations
                        .Select((rotation, page) => (page, rotation)).Where(item => item.rotation != 0)
                        .ToDictionary(item => item.page, item => item.rotation));
                return new PdfMacroOperationResult(File.ReadAllBytes(output));
            }
            finally
            {
                foreach (string path in new[] { input, output })
                    if (File.Exists(path)) File.Delete(path);
            }
        }

        // What a macro's Redact steps remove, and what they did.
        private sealed class CliMacroRedaction
        {
            internal List<string> Terms = [];
            internal List<PdfRedactionRegion> Marked = [];
            internal bool Rasterize;
            internal int Areas;
            internal int Pages;
            internal int Rasterized;
            internal HashSet<int> Blocked = [];
        }

        // The Redact tool's verified engine pass over the terms and saved marks.
        private static PdfMacroOperationResult CliMacroRedact(ReadOnlyMemory<byte> data,
            CliMacroRedaction redaction, CancellationToken token)
        {
            string path = App.MakeTempFile("climacroredact");
            try
            {
                File.WriteAllBytes(path, data.ToArray());
                List<PdfRedactionRegion> regions = [.. redaction.Marked];
                token.ThrowIfCancellationRequested();
                // All terms in one pass, so the document is parsed once rather than twice per term. A page
                // it could not search throws, which fails this step with the pages named in the report.
                foreach (var (page, rects) in SearchService.FindRedactionTermAreas(path, redaction.Terms))
                    foreach (var (left, bottom, right, top) in rects)
                        regions.Add(new PdfRedactionRegion(page, new PdfContentBounds(left, bottom, right, top)));
                if (regions.Count == 0) return new PdfMacroOperationResult(data);

                PdfContentRedactionPlan plan = PdfEngineIntegration.AnalyzeRedaction(
                    PdfEngineIntegration.OpenForReading(data.ToArray()), regions);
                List<int> approved = redaction.Rasterize ? [.. plan.RasterPages] : [];
                HashSet<int> skipped = [.. plan.RasterPages.Except(approved)];
                redaction.Blocked.UnionWith(skipped);
                if (skipped.Count > 0)
                    throw new InvalidOperationException($"Redaction requires rasterization on pages "
                        + $"{string.Join(", ", skipped.Order().Select(page => page + 1))}; add --rasterize to allow that.");

                token.ThrowIfCancellationRequested();
                PdfEngineIntegration.ApplyRedaction(path, regions, approved);
                redaction.Areas += regions.Count;
                redaction.Pages += regions.Select(region => region.PageIndex).Distinct().Count();
                redaction.Rasterized += approved.Count;
                return new PdfMacroOperationResult(File.ReadAllBytes(path));
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }
    }
}
