using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using KillerPdf.Engine.Diagnostics;
using KillerPdf.Engine.Documents;

namespace KillerPDF.Features
{
    /// <summary>
    /// Runs the PDF/UA validator over files and folders for --pdfua and reports each file, then a
    /// summary. A file declaring PDF/UA-2 is checked against the PDF/UA-2 rules and the rest against
    /// PDF/UA-1; each file's report names its standard and rule set. Folders are searched with their
    /// subfolders, and the files run in path order so two runs over one corpus line up.
    /// </summary>
    internal static class PdfUaBatch
    {
        internal const string Usage = "Usage: KillerPDF.exe --pdfua <in.pdf|folder> [more...] [--json]";

        private sealed record FileResult(string Path, PdfUaReport? Report, string? Error);

        /// <summary>
        /// Checks every input and writes the report. Returns 0 when every file passed the machine
        /// checks, 3 when any file failed or was only partly checked, 1 when any file could not be
        /// read, and 2 for bad usage.
        /// </summary>
        internal static int Run(IReadOnlyList<string> inputs, bool json, TextWriter con)
        {
            if (inputs.Count == 0)
            {
                con.WriteLine(Usage);
                return 2;
            }
            var files = new List<string>();
            foreach (string input in inputs)
            {
                string full = System.IO.Path.GetFullPath(input);
                if (File.Exists(full)) files.Add(full);
                else if (Directory.Exists(full))
                    files.AddRange(Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories)
                        .Where(path => path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)));
                else
                {
                    con.WriteLine($"Input not found: {full}");
                    return 2;
                }
            }
            files = [.. files.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal)];
            if (files.Count == 0)
            {
                con.WriteLine("No PDF files were found.");
                return 2;
            }

            var results = new List<FileResult>(files.Count);
            foreach (string path in files)
                results.Add(Check(path));

            con.WriteLine(json ? ToJson(results) : ToText(results));
            if (results.Any(result => result.Report is null)) return 1;
            return results.All(result => result.Report!.Verdict == PdfUaVerdict.PassedMachineChecks) ? 0 : 3;
        }

        private static FileResult Check(string path)
        {
            try
            {
                PdfDocument document = Open(File.ReadAllBytes(path));
                if (!document.IsDecrypted)
                    return new FileResult(path, null, "The document needs a password to open.");
                return new FileResult(path, PdfUaValidator.Validate(document), null);
            }
            catch (Exception error) when (error is InvalidOperationException or NotSupportedException
                or ArgumentException or FormatException or IOException or UnauthorizedAccessException
                or InvalidDataException or OverflowException)
            {
                return new FileResult(path, null, error.Message);
            }
        }

        // An owner-password file opens with its empty user password, the way the app reads it.
        private static PdfDocument Open(byte[] source)
        {
            PdfDocument document = PdfDocument.OpenWithCompatibilityRecovery(source);
            if (!document.IsEncrypted) return document;
            try { return PdfDocument.OpenWithCompatibilityRecovery(source, string.Empty); }
            catch (Exception error) when (error is not OutOfMemoryException) { return document; }
        }

        private static string VerdictName(PdfUaVerdict verdict) => verdict switch
        {
            PdfUaVerdict.Failed => "failed",
            PdfUaVerdict.Incomplete => "incomplete",
            _ => "passedMachineChecks"
        };

        private static string ToText(IReadOnlyList<FileResult> results)
        {
            var output = new StringBuilder();
            foreach (FileResult result in results)
            {
                output.Append("== ").AppendLine(result.Path);
                if (result.Report is null)
                    output.Append("Could not check the document: ").AppendLine(result.Error);
                else
                    output.Append(result.Report.ToText());
                output.AppendLine();
            }
            (int failed, int incomplete, int passed, int unreadable) = Tally(results);
            output.Append("Checked ").Append(results.Count).Append(results.Count == 1 ? " file: " : " files: ")
                .Append(failed).Append(" failed, ")
                .Append(incomplete).Append(" incomplete, ")
                .Append(passed).Append(" passed machine checks, ")
                .Append(unreadable).Append(" could not be read.");
            return output.ToString();
        }

        private static string ToJson(IReadOnlyList<FileResult> results)
        {
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteNumber("schemaVersion", 1);
                writer.WriteString("standard", "PDF/UA-1");
                writer.WriteStartArray("files");
                foreach (FileResult result in results)
                {
                    writer.WriteStartObject();
                    writer.WriteString("path", result.Path);
                    if (result.Report is null)
                    {
                        writer.WriteString("verdict", "unreadable");
                        writer.WriteString("error", result.Error);
                    }
                    else
                    {
                        writer.WriteString("verdict", VerdictName(result.Report.Verdict));
                        writer.WriteStartArray("failedConditions");
                        foreach (string condition in result.Report.Failures.Select(failure => failure.Condition)
                            .Distinct().Order(StringComparer.Ordinal))
                            writer.WriteStringValue(condition);
                        writer.WriteEndArray();
                        writer.WritePropertyName("report");
                        writer.WriteRawValue(result.Report.ToJson());
                    }
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                (int failed, int incomplete, int passed, int unreadable) = Tally(results);
                writer.WriteStartObject("summary");
                writer.WriteNumber("files", results.Count);
                writer.WriteNumber("failed", failed);
                writer.WriteNumber("incomplete", incomplete);
                writer.WriteNumber("passedMachineChecks", passed);
                writer.WriteNumber("unreadable", unreadable);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
            return Encoding.UTF8.GetString(buffer.ToArray());
        }

        private static (int Failed, int Incomplete, int Passed, int Unreadable) Tally(IReadOnlyList<FileResult> results) => (
            results.Count(result => result.Report?.Verdict == PdfUaVerdict.Failed),
            results.Count(result => result.Report?.Verdict == PdfUaVerdict.Incomplete),
            results.Count(result => result.Report?.Verdict == PdfUaVerdict.PassedMachineChecks),
            results.Count(result => result.Report is null));
    }
}
