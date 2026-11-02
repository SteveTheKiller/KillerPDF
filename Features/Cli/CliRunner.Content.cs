using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Windows;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Security;
using KillerPDF.Services;

namespace KillerPDF.Features
{
    // Document conversion and content commands: Protect Document, export to Office and text
    // formats, Office files to PDF, data merge, Find and Replace, and form data and fields.
    internal static partial class CliRunner
    {
        // ============================================================
        // --protect <in.pdf> <out.pdf> [--user-password <p>] [--owner-password <p>]
        //           [--allow print,copy,edit] [--recipients <a.cer>[,<b.cer>...]] [--invalidate-signatures]
        // ============================================================
        // AES-256, as Protect Document writes it. Without a permissions password a random one keeps
        // the restrictions in force. Passwords given on the command line can show in process lists.
        private static int CliProtect(List<string> pos, Dictionary<string, string> options, TextWriter con)
        {
            if (pos.Count != 2)
            {
                con.WriteLine("Usage: KillerPDF.exe --protect <in.pdf> <out.pdf> [--user-password <password>] [--owner-password <password>]");
                con.WriteLine("       [--allow print,copy,edit] [--recipients <cert.cer>[,<cert.cer>...]] [--invalidate-signatures]");
                return 2;
            }
            if (!CliInspectEditInput(pos[0], pos[1], out string source, out string output, out _, con)) return 2;
            options.TryGetValue("--user-password", out string? user);
            options.TryGetValue("--owner-password", out string? owner);
            HashSet<string> allow = [.. (options.TryGetValue("--allow", out string? allowRaw) ? allowRaw : string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(item => item.ToLowerInvariant())];
            if (allow.Any(item => item is not ("print" or "copy" or "edit")))
            { con.WriteLine("--allow takes any of print, copy and edit."); return 2; }
            bool print = allow.Contains("print"), copy = allow.Contains("copy"), edit = allow.Contains("edit");
            var recipients = new List<X509Certificate2>();
            if (options.TryGetValue("--recipients", out string? recipientsRaw))
            {
                foreach (string file in recipientsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    string full = Path.GetFullPath(file);
                    if (!File.Exists(full)) { con.WriteLine($"Certificate not found: {full}"); return 2; }
                    X509Certificate2 certificate;
                    try { certificate = X509CertificateLoader.LoadCertificateFromFile(full); }
                    catch (CryptographicException ex) { con.WriteLine($"Could not read the certificate {full}: {ex.Message}"); return 2; }
                    if (!PdfEngineIntegration.CanEncryptForRecipient(certificate))
                    { con.WriteLine($"The certificate {full} cannot be used to encrypt for its holder."); return 2; }
                    recipients.Add(certificate);
                }
            }
            if (recipients.Count > 0 && (!string.IsNullOrEmpty(user) || !string.IsNullOrEmpty(owner)))
            { con.WriteLine("Use either passwords or --recipients, not both."); return 2; }
            if (recipients.Count == 0 && string.IsNullOrEmpty(user) && string.IsNullOrEmpty(owner))
            { con.WriteLine("Give --user-password, --owner-password or --recipients."); return 2; }
            bool signed;
            try { signed = PdfEngineIntegration.HasSignedSignatures(source); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { signed = false; }
            if (signed && !options.ContainsKey("--invalidate-signatures"))
            {
                con.WriteLine("The document is signed, and encrypting it breaks those signatures. Add --invalidate-signatures to go ahead.");
                return 2;
            }
            CliEnsureParentDir(output);
            try
            {
                if (recipients.Count > 0)
                    PdfEngineIntegration.ProtectForRecipients(source, output, new PdfCertificateEncryptionOptions
                    {
                        Recipients = recipients,
                        AllowLowQualityPrinting = print, AllowHighQualityPrinting = print, AllowContentCopying = copy,
                        AllowDocumentModification = edit, AllowAnnotationModification = edit,
                        AllowFormFilling = edit, AllowDocumentAssembly = edit
                    }, signed);
                else
                    PdfEngineIntegration.ProtectWithPasswords(source, output, new PdfPasswordEncryptionOptions
                    {
                        UserPassword = user ?? string.Empty,
                        OwnerPassword = string.IsNullOrEmpty(owner) ? Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)) : owner,
                        AllowLowQualityPrinting = print, AllowHighQualityPrinting = print, AllowContentCopying = copy,
                        AllowDocumentModification = edit, AllowAnnotationModification = edit,
                        AllowFormFilling = edit, AllowDocumentAssembly = edit
                    }, signed);
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or ArgumentException or CryptographicException)
            {
                con.WriteLine("Could not protect the document: " + ex.Message);
                return 1;
            }
            con.WriteLine($"Protected with AES-256 {(recipients.Count > 0 ? $"for {recipients.Count} certificate holders" : "passwords")}"
                + $" (allows {(allow.Count == 0 ? "nothing" : string.Join(", ", allow.Order()))}) -> {output}");
            return 0;
        }

        // ============================================================
        // --export <in.pdf> <out.docx|xlsx|pptx|html|md|txt|json> [--pages <range>] [--format <name>]
        // ============================================================
        private static int CliExport(List<string> pos, Dictionary<string, string> options, TextWriter con)
        {
            if (pos.Count != 2)
            {
                con.WriteLine("Usage: KillerPDF.exe --export <in.pdf> <out.docx|.xlsx|.pptx|.html|.md|.txt|.json> [--pages <range>]");
                con.WriteLine("       [--format word|excel|powerpoint|html|markdown|text|json]   (default: from the output extension)");
                con.WriteLine("       [--layout editable|images]   (Word and PowerPoint; default: editable)");
                con.WriteLine("       [--password <p>]   (a PDF that forbids copying or editing exports only with the owner password)");
                return 2;
            }
            string inPath = Path.GetFullPath(pos[0]), outPath = Path.GetFullPath(pos[1]);
            if (!File.Exists(inPath)) { con.WriteLine($"Input not found: {inPath}"); return 2; }
            string formatName = options.TryGetValue("--format", out string? formatRaw) ? formatRaw.ToLowerInvariant()
                : Path.GetExtension(outPath).TrimStart('.').ToLowerInvariant();
            PdfStructuredExportFormat? format = formatName switch
            {
                "word" or "docx" => PdfStructuredExportFormat.WordDocument,
                "excel" or "xlsx" => PdfStructuredExportFormat.Spreadsheet,
                "powerpoint" or "pptx" => PdfStructuredExportFormat.Presentation,
                "html" or "htm" => PdfStructuredExportFormat.Html,
                "markdown" or "md" => PdfStructuredExportFormat.Markdown,
                "text" or "txt" => PdfStructuredExportFormat.PlainText,
                "json" => PdfStructuredExportFormat.Json,
                _ => null,
            };
            if (format is null) { con.WriteLine("Name the format with the output extension or --format."); return 2; }
            string layout = options.GetValueOrDefault("--layout", "editable").ToLowerInvariant();
            if (layout is not ("editable" or "images"))
            { con.WriteLine("Export layout must be editable or images."); return 2; }
            var officeMode = layout == "images" ? PdfOfficeExportMode.PageImages : PdfOfficeExportMode.Editable;
            if (officeMode == PdfOfficeExportMode.PageImages
                && format is not (PdfStructuredExportFormat.WordDocument or PdfStructuredExportFormat.Presentation))
            { con.WriteLine("Page-image layout is available only for Word and PowerPoint."); return 2; }
            // Every format is content leaving the file, so every format answers to the same gate the app's export does.
            options.TryGetValue("--password", out string? password);
            PdfAccess access = PdfRestrictions.Open(File.ReadAllBytes(inPath), password, out PdfDocument? opened);
            if (!access.MayModify || opened is null)
            {
                con.WriteLine(CliRestrictionMessage(access));
                return 1;
            }
            PdfDocument document = opened;
            List<int>? pages = null;
            if (options.ContainsKey("--pages"))
            {
                if (!CliPagesOption(options, PdfDocumentInformation.Read(document).PageCount, out List<int> selected, con)) return 2;
                pages = selected;
            }
            PdfStructuredExportReport losses = PdfStructuredExport.InspectLosses(document, format.Value, pages, officeMode: officeMode);
            byte[] exported = PdfStructuredExport.Export(document, format.Value, pages, cancellationToken: CancellationToken.None, officeMode: officeMode);
            if (!CliPublishOutput(inPath, outPath, exported, con)) return 2;
            con.WriteLine($"Exported -> {outPath}" + (losses.IsLossless ? string.Empty
                : $" ({losses.Findings.Count} things could not carry over exactly; run with --report to list them)"));
            if (!losses.IsLossless && options.ContainsKey("--report")) con.WriteLine(losses.ToText());
            return 0;
        }

        // ============================================================
        // --convert-office <in.docx|.xlsx|.doc|.xls> <out.pdf>
        // ============================================================
        // KillerPDF lays out .docx and .xlsx itself; .doc and .xls go through Word or Excel.
        private static int CliConvertOffice(List<string> pos, TextWriter con)
        {
            if (pos.Count != 2)
            {
                con.WriteLine("Usage: KillerPDF.exe --convert-office <in.docx|.xlsx|.doc|.xls> <out.pdf>");
                return 2;
            }
            string inPath = Path.GetFullPath(pos[0]), outPath = Path.GetFullPath(pos[1]);
            if (!File.Exists(inPath)) { con.WriteLine($"Input not found: {inPath}"); return 2; }
            if (!OfficeConversion.IsSupported(inPath)) { con.WriteLine("The input must be a .docx, .xlsx, .doc or .xls file."); return 2; }
            byte[] pdf;
            try { pdf = OfficeConversion.ToPdf(inPath); }
            catch (OfficeNotInstalledException ex)
            {
                con.WriteLine($"Converting this file needs {ex.Application} installed.");
                return 1;
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or InvalidOperationException or NotSupportedException)
            {
                con.WriteLine("Could not convert the file: " + ex.Message);
                return 1;
            }
            if (!CliPublishOutput(inPath, outPath, pdf, con)) return 2;
            con.WriteLine($"Converted {Path.GetFileName(inPath)} -> {outPath}");
            return 0;
        }

        // ============================================================
        // --data-merge <template.pdf> <data.csv|json|xlsx> <outDir> [--name <file name>] [--flatten]
        // ============================================================
        // One filled copy of the form per record, named name.pdf, name-2.pdf and so on.
        private static int CliDataMerge(List<string> pos, Dictionary<string, string> options, TextWriter con)
        {
            if (pos.Count != 3)
            {
                con.WriteLine("Usage: KillerPDF.exe --data-merge <template.pdf> <data.csv|.json|.xlsx> <outputFolder> [--name <file name>] [--flatten] [--overwrite]");
                return 2;
            }
            string template = Path.GetFullPath(pos[0]), data = Path.GetFullPath(pos[1]), outDir = Path.GetFullPath(pos[2]);
            foreach (string path in new[] { template, data })
                if (!File.Exists(path)) { con.WriteLine($"Input not found: {path}"); return 2; }
            IReadOnlyList<IReadOnlyDictionary<string, string?>> records;
            PdfDataMergePlan plan;
            byte[] templateBytes = File.ReadAllBytes(template);
            try
            {
                records = PdfDataMergeWorkflow.LoadRecords(data);
                if (records.Count == 0) { con.WriteLine("The data file has no records."); return 1; }
                string name = options.TryGetValue("--name", out string? nameRaw) && !string.IsNullOrWhiteSpace(nameRaw)
                    ? Path.GetFileName(nameRaw) : Path.GetFileNameWithoutExtension(template) + "-merged.pdf";
                plan = PdfDataMergeWorkflow.CreatePlan(PdfDocument.Open(templateBytes), records, name);
            }
            catch (Exception ex) when (ex is NotSupportedException or ArgumentException or InvalidOperationException
                or FormatException or System.Text.Json.JsonException)
            {
                con.WriteLine("Could not prepare the merge: " + ex.Message);
                return 1;
            }
            Directory.CreateDirectory(outDir);
            // Existing outputs are kept unless --overwrite names them all, matching --split.
            if (plan.OutputFileNames.Select(name => Path.GetFullPath(Path.Combine(outDir, name)))
                .Any(path => string.Equals(path, template, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(path, data, StringComparison.OrdinalIgnoreCase)))
            {
                con.WriteLine("Data-merge output cannot replace an input template or data file.");
                return 2;
            }
            HashSet<string>? approved = options.ContainsKey("--overwrite")
                ? [.. plan.OutputFileNames.Select(name => Path.GetFullPath(Path.Combine(outDir, name)))]
                : null;
            PdfDataMergeBatchReport report = PdfDataMergeWorkflow.RunToDirectory(templateBytes, plan, outDir,
                options.ContainsKey("--flatten") ? PdfDataMergeOutputMode.Flattened : PdfDataMergeOutputMode.Editable,
                CancellationToken.None, approvedOverwrites: approved);
            con.WriteLine(report.ToText());
            return report.FailedRecords == 0 ? 0 : 1;
        }

        // ============================================================
        // --replace-text <in.pdf> <out.pdf> <find> <replace> [--match-case] [--whole-word]
        // ============================================================
        // Rewrites each matching line in the page content in its own font, the way Find and
        // Replace does. Turned text is left alone, and a line the page content cannot take is
        // reported rather than covered, since covers are an app annotation.
        private static int CliReplaceText(List<string> pos, Dictionary<string, string> options, TextWriter con)
        {
            if (pos.Count != 4 || pos[2].Length == 0)
            {
                con.WriteLine("Usage: KillerPDF.exe --replace-text <in.pdf> <out.pdf> <find> <replace> [--match-case] [--whole-word]");
                return 2;
            }
            if (!CliInspectEditInput(pos[0], pos[1], out string source, out string output, out _, con)) return 2;
            string find = pos[2], replace = pos[3];
            bool matchCase = options.ContainsKey("--match-case"), wholeWord = options.ContainsKey("--whole-word");
            IReadOnlyList<PdfPageInformation> geometry = PdfEngineIntegration.ReadPageInformation(source);
            var edits = new List<PdfTextLineEdit>();
            var matchCounts = new List<int>();
            int occurrences = 0, skippedTurned = 0;
            using (PdfContentDocument content = PdfContentDocument.Open(source))
            {
                for (int pageIndex = 0; pageIndex < content.NumberOfPages; pageIndex++)
                {
                    var page = content.GetPage(pageIndex + 1);
                    int rotation = RedactionGeometry.NormalizeRotation(pageIndex < geometry.Count ? geometry[pageIndex].Rotation : 0);
                    double pageWidth = page.Width, pageHeight = page.Height;
                    double canvasWidth = rotation is 90 or 270 ? pageHeight : pageWidth;
                    double canvasHeight = rotation is 90 or 270 ? pageWidth : pageHeight;
                    Point ToCanvas(double x, double y)
                    {
                        Rect point = RedactionGeometry.PageToCanvas(x, y, x, y, pageWidth, pageHeight, canvasWidth, canvasHeight, rotation);
                        return new Point(point.X, point.Y);
                    }
                    var level = new List<ReplaceWord>();
                    var levelSource = new List<PdfExtractedWord>();
                    foreach (var word in page.GetWords())
                    {
                        var box = word.BoundingBox;
                        Rect rect = RedactionGeometry.PageToCanvas(box.Left, box.Bottom, box.Right, box.Top,
                            pageWidth, pageHeight, canvasWidth, canvasHeight, rotation);
                        int turn = word.Letters.Count == 0 ? 0 : TextTurn.FromBaseline(
                            ToCanvas(word.Letters[0].StartBaseLine.X, word.Letters[0].StartBaseLine.Y),
                            ToCanvas(word.Letters[0].EndBaseLine.X, word.Letters[0].EndBaseLine.Y));
                        if (TextTurn.Normalize(turn) != 0)
                        {
                            skippedTurned += TextReplacePlanner.FindSpans(word.Text, find, matchCase, wholeWord).Count;
                            continue;
                        }
                        level.Add(new ReplaceWord(word.Text, rect));
                        levelSource.Add(word);
                    }
                    foreach (ReplaceLine plan in TextReplacePlanner.Plan(level, find, replace, matchCase, wholeWord, null))
                    {
                        var (left, bottom, right, top) = RedactionGeometry.CanvasToPage(plan.Bounds,
                            pageWidth, pageHeight, canvasWidth, canvasHeight, rotation);
                        edits.Add(new PdfTextLineEdit(pageIndex, new PdfContentBounds(left - 0.5, bottom - 0.5, right + 0.5, top + 0.5), plan.NewText)
                        {
                            Substitute = Controls.PdfViewer.SubstituteFont(levelSource[plan.WordIndices[0]], plan.NewText)
                        });
                        occurrences += plan.MatchBounds.Count;
                        matchCounts.Add(plan.MatchBounds.Count);
                    }
                }
            }
            string turnedNote = skippedTurned > 0 ? $" {skippedTurned} matches in turned text were left as they were." : string.Empty;
            if (edits.Count == 0)
            {
                CliEnsureParentDir(output);
                File.Copy(source, output, overwrite: true);
                con.WriteLine($"No matches to replace.{turnedNote} -> {output}");
                return skippedTurned > 0 ? 3 : 0;
            }
            IReadOnlyList<int> failed = [];
            CliWriteEditedCopy(source, output, path => failed = PdfEngineIntegration.ReplaceTextLines(path, edits));
            occurrences -= failed.Sum(index => matchCounts[index]);
            con.WriteLine($"Replaced {occurrences} matches on {edits.Count - failed.Count} lines.{turnedNote}"
                + (failed.Count > 0 ? $" {failed.Count} lines could not be rewritten in the page content (on pages "
                    + string.Join(", ", failed.Select(index => edits[index].PageIndex + 1).Distinct()) + ") and were left as they were." : string.Empty)
                + $" -> {output}");
            return failed.Count == edits.Count ? 1 : failed.Count > 0 || skippedTurned > 0 ? 3 : 0;
        }

        // ============================================================
        // --export-form-data <in.pdf> <out.fdf|.xfdf|.json> [--no-comments]
        // --import-form-data <in.pdf> <data.fdf|.xfdf> <out.pdf> [--flatten]
        // ============================================================
        private static int CliExportFormData(List<string> pos, Dictionary<string, string> options, TextWriter con)
        {
            if (pos.Count != 2)
            {
                con.WriteLine("Usage: KillerPDF.exe --export-form-data <in.pdf> <out.fdf|.xfdf|.json> [--no-comments]");
                return 2;
            }
            string inPath = Path.GetFullPath(pos[0]), outPath = Path.GetFullPath(pos[1]);
            if (!File.Exists(inPath)) { con.WriteLine($"Input not found: {inPath}"); return 2; }
            string extension = Path.GetExtension(outPath).ToLowerInvariant();
            if (extension is not (".fdf" or ".xfdf" or ".json")) { con.WriteLine("The output must end in .fdf, .xfdf or .json."); return 2; }
            PdfFormDataSet data = PdfFormDataExporter.Export(PdfEngineIntegration.OpenForReading(File.ReadAllBytes(inPath)),
                inPath, includeAnnotations: !options.ContainsKey("--no-comments"));
            byte[] bytes = extension switch
            {
                ".fdf" => PdfFdfFormData.Write(data),
                ".xfdf" => PdfXfdfFormData.Write(data),
                _ => System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(data,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true })),
            };
            if (!CliPublishOutput(inPath, outPath, bytes, con)) return 2;
            con.WriteLine($"Exported {data.Fields.Count} fields and {data.Annotations.Count} comments -> {outPath}");
            return 0;
        }

        private static int CliImportFormData(List<string> pos, Dictionary<string, string> options, TextWriter con)
        {
            if (pos.Count != 3)
            {
                con.WriteLine("Usage: KillerPDF.exe --import-form-data <in.pdf> <data.fdf|.xfdf> <out.pdf> [--flatten]");
                return 2;
            }
            if (!CliInspectEditInput(pos[0], pos[2], out string source, out string output, out _, con)) return 2;
            string dataPath = Path.GetFullPath(pos[1]);
            if (!File.Exists(dataPath)) { con.WriteLine($"Data file not found: {dataPath}"); return 2; }
            PdfFormDataSet data;
            try
            {
                byte[] raw = File.ReadAllBytes(dataPath);
                data = Path.GetExtension(dataPath).Equals(".xfdf", StringComparison.OrdinalIgnoreCase)
                    ? PdfXfdfFormData.Read(raw) : PdfFdfFormData.Read(raw);
            }
            catch (Exception ex) when (ex is FormatException or InvalidOperationException or NotSupportedException
                or System.Xml.XmlException or ArgumentException)
            {
                con.WriteLine("Could not read the form data: " + ex.Message);
                return 1;
            }
            PdfDocument document = PdfDocument.Open(File.ReadAllBytes(source));
            PdfFormDataImportReport report = PdfFormDataImporter.CreateReport(document, data);
            CliEnsureParentDir(output);
            AtomicFileOutput.Write(output, PdfFormDataImporter.Apply(document, data,
                options.ContainsKey("--flatten") ? PdfFormDataImportOutputMode.Flattened : PdfFormDataImportOutputMode.Editable));
            con.WriteLine(report.ToText());
            con.WriteLine($"-> {output}");
            return 0;
        }

        // ============================================================
        // --recognize-fields <in.pdf> [<out.pdf>] [--json] [--password <password>]
        // ============================================================
        // Finds blanks, boxes and lines that read as form fields. With an output file, the text
        // fields and check boxes it finds are added as real fields. The report alone prints the page
        // labels beside each field, so it reads --password and the gate holds it to the copy permission.
        private static int CliRecognizeFields(List<string> pos, Dictionary<string, string> options, TextWriter con)
        {
            if (pos.Count is < 1 or > 2)
            {
                con.WriteLine("Usage: KillerPDF.exe --recognize-fields <in.pdf> [<out.pdf>] [--json] [--password <password>]");
                return 2;
            }
            string inPath = Path.GetFullPath(pos[0]);
            if (!File.Exists(inPath)) { con.WriteLine($"Input not found: {inPath}"); return 2; }
            if (pos.Count == 1)
            {
                options.TryGetValue("--password", out string? password);
                if (CliProtectionGate.OpenForReport(inPath, password, out string? refusal) is not PdfDocument document)
                {
                    con.WriteLine(refusal);
                    return 1;
                }
                PdfFormRecognitionReview report = PdfFormRecognizer.Recognize(document);
                con.WriteLine(options.ContainsKey("--json") ? report.ToJson(true) : report.ToText());
                return 0;
            }
            PdfFormRecognitionReview review = PdfFormRecognizer.Recognize(PdfEngineIntegration.OpenForReading(File.ReadAllBytes(inPath)));
            if (!CliInspectEditInput(pos[0], pos[1], out string source, out string output, out _, con)) return 2;
            var used = new HashSet<string>(PdfFormDataExporter.Export(PdfDocument.Open(File.ReadAllBytes(source)), includeAnnotations: false)
                .Fields.Select(field => field.Name), StringComparer.Ordinal);
            string Unique(string name)
            {
                string candidate = string.IsNullOrWhiteSpace(name) ? "Field" : name;
                for (int index = 2; !used.Add(candidate); index++) candidate = $"{name}_{index}";
                return candidate;
            }
            int added = 0;
            var skipped = new Dictionary<PdfRecognizedFieldKind, int>();
            CliWriteEditedCopy(source, output, path =>
            {
                var editor = new PdfIncrementalPageEditor(PdfDocument.Open(File.ReadAllBytes(path)));
                foreach (PdfFormFieldProposal proposal in review.Proposals)
                {
                    PdfContentBounds box = proposal.Bounds;
                    switch (proposal.Kind)
                    {
                        case PdfRecognizedFieldKind.Text:
                            editor.AddTextField(proposal.PageIndex, Unique(proposal.SuggestedName), box.Left, box.Bottom,
                                box.Width, box.Height, fontSize: proposal.SuggestedFontSize);
                            added++;
                            break;
                        case PdfRecognizedFieldKind.CheckBox:
                            editor.AddCheckBox(proposal.PageIndex, Unique(proposal.SuggestedName), box.Left, box.Bottom,
                                box.Width, box.Height);
                            added++;
                            break;
                        default:
                            skipped[proposal.Kind] = skipped.GetValueOrDefault(proposal.Kind) + 1;
                            break;
                    }
                }
                if (added > 0) File.WriteAllBytes(path, editor.Build());
            });
            con.WriteLine($"Added {added} fields" + (skipped.Count == 0 ? string.Empty
                : " (left for the Forms tool: " + string.Join(", ", skipped.Select(pair => $"{pair.Value} {pair.Key}")) + ")")
                + $" -> {output}");
            return 0;
        }

        // ============================================================
        // --xfa-to-acroform <in.pdf> <out.pdf> [--flatten]
        // --flatten-forms <in.pdf> <out.pdf>
        // ============================================================
        private static int CliXfaToAcroForm(List<string> pos, Dictionary<string, string> options, TextWriter con)
        {
            if (pos.Count != 2)
            {
                con.WriteLine("Usage: KillerPDF.exe --xfa-to-acroform <in.pdf> <out.pdf> [--flatten]");
                return 2;
            }
            if (!CliInspectEditInput(pos[0], pos[1], out string source, out string output, out _, con)) return 2;
            try
            {
                byte[] converted = PdfXfaAcroFormConverter.Convert(PdfDocument.Open(File.ReadAllBytes(source)),
                    options.ContainsKey("--flatten") ? PdfXfaConversionMode.Flattened : PdfXfaConversionMode.Editable);
                CliEnsureParentDir(output);
                AtomicFileOutput.Write(output, converted);
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or FormatException)
            {
                con.WriteLine("Could not convert the XFA form: " + ex.Message);
                return 1;
            }
            con.WriteLine($"Converted the XFA form to standard form fields -> {output}");
            return 0;
        }

        private static int CliFlattenForms(List<string> pos, TextWriter con)
        {
            if (pos.Count != 2)
            {
                con.WriteLine("Usage: KillerPDF.exe --flatten-forms <in.pdf> <out.pdf>   (fields become page content; the rest stays text and vectors)");
                return 2;
            }
            if (!CliInspectEditInput(pos[0], pos[1], out string source, out string output, out _, con)) return 2;
            CliEnsureParentDir(output);
            AtomicFileOutput.Write(output, PdfFormFlattener.Flatten(PdfDocument.Open(File.ReadAllBytes(source))));
            con.WriteLine($"Flattened the form fields -> {output}");
            return 0;
        }
    }
}
