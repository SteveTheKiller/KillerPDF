using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfStructuredExportMacroTests
{
    [Fact]
    public void ExportStepRoundTripsAndExportsSelectedPages()
    {
        byte[] source = new PdfDocumentBuilder()
            .AddPage(200, 300, Text("First"))
            .AddPage(200, 300, Text("Second"))
            .Build();
        PdfMacro macro = PdfMacro.FromJson(new PdfMacro("Export", [
            PdfStructuredExportMacro.Step(PdfStructuredExportFormat.Html, [1])]).ToJson());

        string html = Encoding.UTF8.GetString(PdfStructuredExportMacro.Execute(
            Assert.Single(macro.Steps), source).Span);

        Assert.DoesNotContain("First", html, StringComparison.Ordinal);
        Assert.Contains("Second", html, StringComparison.Ordinal);
        Assert.Contains("data-page=\"2\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void ExportStepValidatesOperationSettingsAndSelection()
    {
        byte[] source = new PdfDocumentBuilder().AddBlankPage().Build();

        Assert.Throws<ArgumentException>(() => PdfStructuredExportMacro.Execute(
            new PdfMacroStep(PdfMacroOperation.Save), source));
        Assert.Throws<ArgumentException>(() => PdfStructuredExportMacro.Execute(
            new PdfMacroStep(PdfMacroOperation.Export), source));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PdfStructuredExportMacro.Step(PdfStructuredExportFormat.Json, [0, 0]));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PdfStructuredExportMacro.Step((PdfStructuredExportFormat)99));
    }

    private static PdfContentStreamBuilder Text(string value) =>
        new PdfContentStreamBuilder().BeginText()
            .SetFont(PdfStandardFont.Helvetica, 12).MoveText(10, 30)
            .ShowLatin1Text(value).EndText();
}
