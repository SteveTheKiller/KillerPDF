using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfStructuredExportMacroTests
{
    [Fact]
    public void DispatcherKeepsPdfEditableAfterExportAndRunsLaterPdfSteps()
    {
        byte[] source = new PdfDocumentBuilder().AddPage(200, 300, Text("Invoice")).Build();
        var macro = new PdfMacro("Export then validate", [
            PdfStructuredExportMacro.Step(PdfStructuredExportFormat.PlainText),
            new(PdfMacroOperation.Validate)]);

        PdfMacroFileResult result = Assert.Single(new PdfMacroDispatcher().RunReport(macro, [source]).Results);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(source, result.Data!.Value.ToArray());
        PdfMacroExport export = Assert.Single(result.Exports);
        Assert.Equal(PdfStructuredExportFormat.PlainText, export.Format);
        Assert.Contains("Invoice", Encoding.UTF8.GetString(export.Data.Span), StringComparison.Ordinal);
    }

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
    public void EpubExportStepRoundTripsAndWritesABook()
    {
        byte[] source = new PdfDocumentBuilder().AddPage(200, 300, Text("Chapter text")).Build();
        PdfMacro macro = PdfMacro.FromJson(new PdfMacro("Book", [
            PdfStructuredExportMacro.Step(PdfStructuredExportFormat.Epub)]).ToJson());
        PdfMacroStep step = Assert.Single(macro.Steps);

        byte[] epub = PdfStructuredExportMacro.Execute(step, source).ToArray();

        Assert.Equal(PdfStructuredExportFormat.Epub, PdfStructuredExportMacro.ReadFormat(step));
        Assert.Equal(".epub", PdfStructuredExportMacro.Extension(PdfStructuredExportFormat.Epub));
        Assert.Equal("mimetypeapplication/epub+zip", Encoding.ASCII.GetString(epub, 30, 28));
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

    [Fact]
    public void MergeExportsContainEachRecordAndKeepItsPdfFieldsEditable()
    {
        byte[] source = new PdfDocumentBuilder().AddBlankPage(200, 300)
            .AddTextField(0, "name", 10, 20, 150, 24).Build();
        var profile = new PdfDataMergeProfile("People", [new("Name", "name")], "{{Name}}.pdf");
        IReadOnlyDictionary<string, string?>[] records = [
            new Dictionary<string, string?> { ["Name"] = "Ada" },
            new Dictionary<string, string?> { ["Name"] = "Grace" }];
        var dispatcher = new PdfMacroDispatcher([
            new(PdfMacroOperation.DataMerge, (step, data, _, token) =>
                PdfDataMergeMacro.Execute(step, data, records, token))]);
        var macro = new PdfMacro("People", [profile.ToMacroStep(),
            PdfStructuredExportMacro.Step(PdfStructuredExportFormat.PlainText), new(PdfMacroOperation.Validate)]);

        PdfMacroFileResult result = Assert.Single(dispatcher.RunReport(macro, [source]).Results);

        Assert.True(result.Succeeded, result.Error);
        Assert.Collection(result.Outputs!, output => Check(output, "Ada"), output => Check(output, "Grace"));
        static void Check(PdfMacroOutput output, string name)
        {
            Assert.Equal(name, Assert.Single(PdfFormWidgetReader.ReadPage(PdfDocument.Open(output.Data!.Value), 0)).Value);
            Assert.Contains(name, Encoding.UTF8.GetString(Assert.Single(output.Exports).Data.Span), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void LaterFailureReturnsNeitherPdfNorEarlierExports()
    {
        byte[] source = new PdfDocumentBuilder().AddPage(200, 300, Text("Invoice")).Build();
        var macro = new PdfMacro("Fail after export", [PdfStructuredExportMacro.Step(PdfStructuredExportFormat.Html),
            new(PdfMacroOperation.Save)]);
        var dispatcher = new PdfMacroDispatcher([new(PdfMacroOperation.Save,
            (_, _, _, _) => throw new InvalidOperationException("Cannot save"))]);
        PdfMacroFileResult result = Assert.Single(dispatcher.RunReport(macro, [source]).Results);
        Assert.False(result.Succeeded);
        Assert.Null(result.Data);
        Assert.Empty(result.Exports);
        Assert.Equal(PdfMacroOperation.Save, result.FailedOperation);
    }

    [Fact]
    public void FormatChangesRetainPageSelectionAndMultipleExportsStayInStepOrder()
    {
        byte[] source = new PdfDocumentBuilder().AddPage(200, 300, Text("First"))
            .AddPage(200, 300, Text("Second")).Build();
        PdfMacroStep changed = PdfStructuredExportMacro.WithFormat(
            PdfStructuredExportMacro.Step(PdfStructuredExportFormat.PlainText, [1]), PdfStructuredExportFormat.Html);
        var macro = new PdfMacro("Two exports", [changed,
            PdfStructuredExportMacro.Step(PdfStructuredExportFormat.PlainText, [0])]);
        PdfMacroFileResult result = Assert.Single(new PdfMacroDispatcher().RunReport(macro, [source]).Results);
        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(source, result.Data!.Value.ToArray());
        Assert.Collection(result.Exports,
            html => { Assert.Contains("Second", Encoding.UTF8.GetString(html.Data.Span), StringComparison.Ordinal);
                Assert.DoesNotContain("First", Encoding.UTF8.GetString(html.Data.Span), StringComparison.Ordinal); },
            text => { Assert.Contains("First", Encoding.UTF8.GetString(text.Data.Span), StringComparison.Ordinal);
                Assert.DoesNotContain("Second", Encoding.UTF8.GetString(text.Data.Span), StringComparison.Ordinal); });
    }

    private static PdfContentStreamBuilder Text(string value) =>
        new PdfContentStreamBuilder().BeginText()
            .SetFont(PdfStandardFont.Helvetica, 12).MoveText(10, 30)
            .ShowLatin1Text(value).EndText();
}
