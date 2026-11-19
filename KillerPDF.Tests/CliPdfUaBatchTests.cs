using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using KillerPDF.Features;
using Xunit;

namespace KillerPDF.Tests;

/// <summary>
/// --pdfua checks files and whole folders against PDF/UA-1 and writes one report per file plus a
/// summary, in text or JSON, so a corpus can be compared with another validator (#491).
/// </summary>
public sealed class CliPdfUaBatchTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "killerpdf-pdfua-" + Guid.NewGuid().ToString("N"));

    public CliPdfUaBatchTests() => Directory.CreateDirectory(folder);

    public void Dispose()
    {
        try { Directory.Delete(folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void AFolderIsSearchedWithItsSubfoldersInPathOrderAndSummarized()
    {
        string corpus = Path.Combine(folder, "corpus");
        Directory.CreateDirectory(Path.Combine(corpus, "nested"));
        File.WriteAllBytes(Path.Combine(corpus, "b.pdf"), UntaggedPdf());
        File.WriteAllBytes(Path.Combine(corpus, "nested", "a.PDF"), UntaggedPdf());
        File.WriteAllText(Path.Combine(corpus, "notes.txt"), "not a pdf");
        var output = new StringWriter();

        int exit = PdfUaBatch.Run([corpus], json: false, output);

        string text = output.ToString();
        Assert.Equal(3, exit);
        int first = text.IndexOf("== " + Path.Combine(corpus, "b.pdf"), StringComparison.Ordinal);
        int second = text.IndexOf("== " + Path.Combine(corpus, "nested", "a.PDF"), StringComparison.Ordinal);
        Assert.True(first >= 0 && second > first, text);
        Assert.DoesNotContain("notes.txt", text);
        Assert.Contains("PDF/UA-1 result: Failed", text);
        Assert.EndsWith("Checked 2 files: 2 failed, 0 incomplete, 0 passed machine checks, 0 could not be read."
            + Environment.NewLine, text);
    }

    [Fact]
    public void JsonCarriesEachFileVerdictFailedConditionsFullReportAndSummary()
    {
        string untagged = Path.Combine(folder, "untagged.pdf");
        string garbage = Path.Combine(folder, "garbage.pdf");
        File.WriteAllBytes(untagged, UntaggedPdf());
        File.WriteAllBytes(garbage, Encoding.ASCII.GetBytes("not a pdf"));
        var output = new StringWriter();

        int exit = PdfUaBatch.Run([untagged, garbage], json: true, output);

        Assert.Equal(1, exit);
        using JsonDocument json = JsonDocument.Parse(output.ToString());
        JsonElement root = json.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        JsonElement[] files = [.. root.GetProperty("files").EnumerateArray()];
        Assert.Equal(2, files.Length);

        JsonElement bad = Assert.Single(files, file => file.GetProperty("path").GetString() == garbage);
        Assert.Equal("unreadable", bad.GetProperty("verdict").GetString());
        Assert.False(string.IsNullOrWhiteSpace(bad.GetProperty("error").GetString()));

        JsonElement checkedFile = Assert.Single(files, file => file.GetProperty("path").GetString() == untagged);
        Assert.Equal("failed", checkedFile.GetProperty("verdict").GetString());
        string[] conditions = [.. checkedFile.GetProperty("failedConditions").EnumerateArray().Select(item => item.GetString()!)];
        Assert.Contains("06-001", conditions);
        Assert.Equal(conditions.Order(StringComparer.Ordinal), conditions);
        Assert.Equal(31, checkedFile.GetProperty("report").GetProperty("checkpoints").GetArrayLength());

        JsonElement summary = root.GetProperty("summary");
        Assert.Equal(2, summary.GetProperty("files").GetInt32());
        Assert.Equal(1, summary.GetProperty("failed").GetInt32());
        Assert.Equal(1, summary.GetProperty("unreadable").GetInt32());
    }

    [Fact]
    public void JsonNamesTheStandardTheFilesWereHeldTo()
    {
        string ua1 = Path.Combine(folder, "ua1.pdf");
        string ua2 = Path.Combine(folder, "ua2.pdf");
        File.WriteAllBytes(ua1, UntaggedPdf());
        File.WriteAllBytes(ua2, UntaggedPdf(pdfUaPart: 2));

        string Standard(params string[] inputs)
        {
            var output = new StringWriter();
            PdfUaBatch.Run(inputs, json: true, output);
            using JsonDocument json = JsonDocument.Parse(output.ToString());
            return json.RootElement.GetProperty("standard").GetString()!;
        }

        Assert.Equal("PDF/UA-1", Standard(ua1));
        Assert.Equal("PDF/UA-2", Standard(ua2));
        Assert.Equal("mixed", Standard(ua1, ua2));
    }

    [Fact]
    public void AMissingInputOrAnEmptyFolderIsBadUsage()
    {
        string empty = Path.Combine(folder, "empty");
        Directory.CreateDirectory(empty);

        Assert.Equal(2, PdfUaBatch.Run([], json: false, new StringWriter()));
        Assert.Equal(2, PdfUaBatch.Run([Path.Combine(folder, "missing.pdf")], json: false, new StringWriter()));
        Assert.Equal(2, PdfUaBatch.Run([empty], json: false, new StringWriter()));
    }

    [Fact]
    public void TheCommandLineRoutesPdfUaToTheBatch()
    {
        string source = File.ReadAllText(Path.Combine(RepositoryRoot(), "Features", "Cli", "CliRunner.cs"));
        Assert.Contains("case \"--pdfua\":", source, StringComparison.Ordinal);
        Assert.Contains("PdfUaBatch.Run(", source, StringComparison.Ordinal);
        Assert.Contains("Eq(a, \"--pdfua\")", source, StringComparison.Ordinal);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "KillerPDF.csproj")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }

    // An untagged one-page file; pdfUaPart adds XMP declaring that PDF/UA part.
    private static byte[] UntaggedPdf(int? pdfUaPart = null)
    {
        string xmp = "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">"
            + "<rdf:Description rdf:about=\"\" xmlns:pdfuaid=\"http://www.aiim.org/pdfua/ns/id/\"><pdfuaid:part>"
            + pdfUaPart + "</pdfuaid:part></rdf:Description></rdf:RDF></x:xmpmeta>";
        List<string> objects =
        [
            pdfUaPart is null ? "<< /Type /Catalog /Pages 2 0 R >>" : "<< /Type /Catalog /Pages 2 0 R /Metadata 4 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 300] /Resources << >> >>"
        ];
        if (pdfUaPart is not null)
            objects.Add("<< /Type /Metadata /Subtype /XML /Length " + xmp.Length + " >>\nstream\n" + xmp + "\nendstream");
        var pdf = new StringBuilder(pdfUaPart == 2 ? "%PDF-2.0\n" : "%PDF-1.7\n");
        int count = objects.Count;
        var offsets = new List<int>();
        for (int index = 0; index < count; index++)
        {
            offsets.Add(pdf.Length);
            pdf.Append(index + 1).Append(" 0 obj\n").Append(objects[index]).Append("\nendobj\n");
        }
        int xref = pdf.Length;
        pdf.Append("xref\n0 ").Append(count + 1).Append("\n0000000000 65535 f \n");
        foreach (int offset in offsets) pdf.Append(offset.ToString("D10")).Append(" 00000 n \n");
        pdf.Append("trailer\n<< /Size ").Append(count + 1).Append(" /Root 1 0 R >>\nstartxref\n")
            .Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
