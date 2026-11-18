using System.IO;
using System.Text;
using KillerPdf.Engine.Documents;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

// The app's macro Redact step and the command line's used to be two copies that had already
// drifted (the app opened the document without the repair fallback). Both now call one pass, and
// this runs one document through it under each caller's page policy.
public sealed class MacroRedactionPassTests : IDisposable
{
    private readonly List<string> _paths = [];

    public void Dispose()
    {
        foreach (string path in _paths) try { File.Delete(path); } catch (IOException) { }
    }

    private string WorkingPath()
    {
        string path = Path.Combine(Path.GetTempPath(), "killerpdf-macro-redact-" + Guid.NewGuid().ToString("N") + ".pdf");
        _paths.Add(path);
        return path;
    }

    [Fact]
    public void TheAppAndCommandLinePoliciesRemoveTheSameWords()
    {
        byte[] source = Pdf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            Stream("BT /F1 12 Tf 72 700 Td (Account 4417 belongs to Jordan) Tj ET"));
        string[] terms = ["Jordan", "4417"];

        // The app asks and the user approves; the command line approves only with --rasterize.
        MacroRedactionPass.Outcome? app = MacroRedactionPass.Run(source, terms, [], WorkingPath(),
            (pages, approved, _) => { approved.AddRange(pages); return true; }, _ => true, CancellationToken.None);
        MacroRedactionPass.Outcome? cli = MacroRedactionPass.Run(source, terms, [], WorkingPath(),
            (pages, approved, _) => { approved.AddRange(pages); return true; }, null, CancellationToken.None);

        Assert.NotNull(app);
        Assert.NotNull(cli);
        Assert.Equal(app!.Areas, cli!.Areas);
        Assert.Equal(app.Pages, cli.Pages);
        Assert.True(PdfContentRedaction.FindSurvivingText(app.Bytes, terms).Succeeded);
        Assert.True(PdfContentRedaction.FindSurvivingText(cli.Bytes, terms).Succeeded);
    }

    [Fact]
    public void NothingToRemovePassesTheDocumentOn()
    {
        byte[] source = Pdf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            Stream("BT /F1 12 Tf 72 700 Td (Nothing private) Tj ET"));
        Assert.Null(MacroRedactionPass.Run(source, ["Jordan"], [], WorkingPath(),
            (_, _, _) => throw new InvalidOperationException("No page should need a decision."), null, CancellationToken.None));
    }

    // BF-C002: a word only in a bookmark title gives the step no area to remove, so the step passes the
    // document on with Areas 0, yet the word is still in the output. The app's survivor check must run
    // whenever there are terms, the way the command line's does, not only when something was redacted.
    [Fact]
    public void AWordOnlyInABookmarkRedactsNothingYetSurvives()
    {
        byte[] source = Pdf(
            "<< /Type /Catalog /Pages 2 0 R /Outlines 6 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            Stream("BT /F1 12 Tf 72 700 Td (Nothing private) Tj ET"),
            "<< /Type /Outlines /First 7 0 R /Last 7 0 R /Count 1 >>",
            "<< /Title (Jordan's file) /Parent 6 0 R /Dest [3 0 R /Fit] >>");

        Assert.Null(MacroRedactionPass.Run(source, ["Jordan"], [], WorkingPath(),
            (_, _, _) => throw new InvalidOperationException("No page should need a decision."), null, CancellationToken.None));
        Assert.False(PdfContentRedaction.FindSurvivingText(source, ["Jordan"]).Succeeded);

        string macros = ShellSource.Read("Shell", "Macros.cs");
        string note = ShellSource.Between(macros, "private string? MacroSurvivorNote(", "PdfRedactionVerificationReport report;");
        Assert.Contains("if (_macroRedaction.Terms.Length == 0) return null;", note);
        Assert.DoesNotContain("Areas", note);
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
