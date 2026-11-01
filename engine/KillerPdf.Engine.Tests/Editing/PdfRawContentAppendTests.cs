using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Editing;

public sealed class PdfRawContentAppendTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void RawProgramsRemainSeparateAfterRepeatedAppendAndReopen(bool setFirst, bool trailingComment)
    {
        PdfDocument blank = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage(40, 20).Build());
        var editor = new PdfIncrementalPageEditor(blank);
        byte[] red = System.Text.Encoding.ASCII.GetBytes("1 0 0 rg 0 0 20 20 re f"
            + (trailingComment ? "% first program" : ""));
        if (setFirst) editor.SetPageContent(0, red);
        else editor.AppendPageContent(0, red);
        editor.AppendPageContent(0, "0 0 1 rg 20 0 20 20 re f"u8.ToArray());
        PdfDocument reopened = PdfDocument.Open(editor.Build());
        var instructions = new PdfPageContentReader(reopened).Read(0).Instructions;
        Assert.Equal(2, instructions.Count(instruction => instruction.Operator == "f"));
        var result = new PdfPageRenderer(reopened).Render(0, new PdfRenderOptions(40, 20));
        Assert.Empty(result.Diagnostics);
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, result.Pixels.Span.Slice((10 * 40 + 10) * 4, 4).ToArray());
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, result.Pixels.Span.Slice((10 * 40 + 30) * 4, 4).ToArray());
    }
}
