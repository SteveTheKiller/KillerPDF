using System.IO;
using System.Text;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

// The macro Redact step redacts its terms through these areas. A term that appears only in a form
// field value or an annotation note is not page content, so the page-text search alone misses it.
public sealed class RedactionTermAreasTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "killerpdf-term-areas-" + Guid.NewGuid().ToString("N") + ".pdf");

    public void Dispose()
    {
        try { File.Delete(_path); } catch (IOException) { }
    }

    [Fact]
    public void ATermInAFieldValueOrNoteGetsAnArea()
    {
        File.WriteAllBytes(_path, Pdf(
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [6 0 R] >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R /Annots [6 0 R 7 0 R] >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            Stream("BT /F1 12 Tf 72 700 Td (Plain page text) Tj ET"),
            "<< /Type /Annot /Subtype /Widget /FT /Tx /T (name) /V (Jordan Q. Private) /Rect [100 600 300 620] /P 3 0 R /F 4 >>",
            "<< /Type /Annot /Subtype /FreeText /Contents (Ask Jordan first) /DA (/F1 10 Tf 0 g) /Rect [100 500 300 540] /F 4 >>"));

        Assert.Empty(SearchService.FindExactMatches(_path, "jordan"));
        var areas = SearchService.FindRedactionTermAreas(_path, "jordan");

        Assert.Equal([(100, 600, 300, 620), (100, 500, 300, 540)], Assert.Single(areas).Value);
        // Page text still comes from the page search, letter by letter.
        Assert.Equal(SearchService.FindExactMatches(_path, "page text")[0],
            Assert.Single(SearchService.FindRedactionTermAreas(_path, "page text")).Value);
    }

    // A macro with several terms parses the file once for all of them, and the merged areas are the
    // union of what each term finds on its own (BF-A008).
    [Fact]
    public void SeveralTermsShareOneParseAndMergeTheirAreas()
    {
        File.WriteAllBytes(_path, Pdf(
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [6 0 R] >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R /Annots [6 0 R 7 0 R] >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            Stream("BT /F1 12 Tf 72 700 Td (Plain page text about Casey) Tj ET"),
            "<< /Type /Annot /Subtype /Widget /FT /Tx /T (name) /V (Jordan Q. Private) /Rect [100 600 300 620] /P 3 0 R /F 4 >>",
            "<< /Type /Annot /Subtype /FreeText /Contents (Ask Jordan first) /DA (/F1 10 Tf 0 g) /Rect [100 500 300 540] /F 4 >>"));

        var merged = SearchService.FindRedactionTermAreas(_path, ["jordan", "casey", "   ", "page text"]);

        var expected = new List<(double, double, double, double)>();
        foreach (string term in new[] { "jordan", "casey", "page text" })
            expected.AddRange(SearchService.FindRedactionTermAreas(_path, term)[0]);
        Assert.Equal(expected.OrderBy(area => area), Assert.Single(merged).Value.OrderBy(area => area));
        Assert.Empty(SearchService.FindRedactionTermAreas(_path, []));
    }

    [Fact]
    public void BothMacroCallersPassAllTermsAtOnce()
    {
        // Guard pin: both callers go through the one shared pass, which MacroRedactionPassTests
        // runs with several terms. A caller with its own copy would drift again.
        string root = FindRepositoryRoot();
        Assert.Contains("MacroRedactionPass.Run(source, _macroRedaction.Terms",
            File.ReadAllText(Path.Combine(root, "Shell", "Macros.cs")));
        Assert.Contains("MacroRedactionPass.Run(data, redaction.Terms",
            File.ReadAllText(Path.Combine(root, "Features", "Cli", "CliRunner.Macros.cs")));
        Assert.Contains("SearchService.FindRedactionTermAreas(workingPath, terms)",
            File.ReadAllText(Path.Combine(root, "Services", "MacroRedactionPass.cs")));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "KillerPDF.csproj"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new InvalidOperationException("KillerPDF.csproj was not found above the test output.");
    }

    private static string Stream(string content) =>
        $"<< /Length {Encoding.Latin1.GetByteCount(content)} >>\nstream\n{content}\nendstream";

    private static byte[] Pdf(params string[] objects)
    {
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int index = 0; index < objects.Length; index++)
        {
            offsets.Add(Encoding.Latin1.GetByteCount(pdf.ToString()));
            pdf.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        int xref = Encoding.Latin1.GetByteCount(pdf.ToString());
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) pdf.Append($"{offset:0000000000} 00000 n \n");
        pdf.Append($"trailer << /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(pdf.ToString());
    }
}
