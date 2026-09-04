using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfLayerMacroTests
{
    [Fact]
    public void MacroRoundTripsAndFlattensExplicitVisibleLayerNames()
    {
        var hidden = new PdfOptionalContentGroup("Hidden", initiallyVisible: false);
        var visible = new PdfOptionalContentGroup("Visible");
        byte[] source = new PdfDocumentBuilder().AddPage(200, 200,
            new PdfContentStreamBuilder()
                .BeginOptionalContent(hidden).BeginText()
                    .SetFont(PdfStandardFont.Helvetica, 12).MoveText(10, 20)
                    .ShowLatin1Text("Hidden text").EndText().EndMarkedContent()
                .BeginOptionalContent(visible).BeginText()
                    .SetFont(PdfStandardFont.Helvetica, 12).MoveText(10, 40)
                    .ShowLatin1Text("Visible text").EndText().EndMarkedContent()).Build();
        var macro = new PdfMacro("Layers", [PdfLayerMacro.FlattenStep(["Hidden"])]);
        PdfMacroStep step = Assert.Single(PdfMacro.FromJson(macro.ToJson()).Steps);

        PdfDocument output = PdfDocument.Open(PdfLayerMacro.Execute(step, source));

        Assert.Equal("Hidden text", new PdfPageContentReader(output).Read(0).Text);
        Assert.Empty(PdfOptionalContentReader.Read(output).Groups);
        Assert.Throws<ArgumentException>(() => PdfLayerMacro.Execute(
            PdfLayerMacro.FlattenStep(["Missing"]), source));
    }
}
