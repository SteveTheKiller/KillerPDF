using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfImpositionMacroTests
{
    [Fact]
    public void NUpStepRoundTripsAndExportsPresetSheetGeometry()
    {
        byte[] source = new PdfDocumentBuilder()
            .AddBlankPage(200, 300)
            .AddBlankPage(300, 200)
            .AddBlankPage(200, 300)
            .Build();
        var preset = new PdfImpositionPreset(
            "Two-up", 2, 1, 792, 612, margin: 18, gutter: 12, duplex: true);
        PdfMacro macro = PdfMacro.FromJson(new PdfMacro(
            "Impose", [PdfImpositionMacro.NUpStep(preset)]).ToJson());

        ReadOnlyMemory<byte> output = PdfImpositionMacro.Execute(
            Assert.Single(macro.Steps), source);
        PdfDocument imposed = PdfDocument.Open(output);
        IReadOnlyList<PdfPageBoxInformation> pages =
            PdfPageBoxInformation.Read(imposed);

        Assert.Equal(2, pages.Count);
        Assert.All(pages, page => Assert.Equal(
            new PdfPageBoxBounds(0, 0, 792, 612), page.MediaBox));
        Assert.Throws<ArgumentException>(() => PdfImpositionMacro.Execute(
            new PdfMacroStep(PdfMacroOperation.Save), source));
    }
}
