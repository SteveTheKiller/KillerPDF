using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Tests.Fonts;
using Xunit;

namespace KillerPdf.Engine.Tests.Editing;

public sealed class PdfCombFieldTests
{
    [Fact]
    public void Recognizer_RowOfThreeOrMoreSquaresBecomesOneComb()
    {
        var content = new PdfContentStreamBuilder();
        for (int cell = 0; cell < 5; cell++)
            content.Rectangle(100 + cell * 14, 500, 14, 14).Stroke();

        PdfFormRecognitionReview review = PdfFormRecognizer.Recognize(
            PdfDocument.Open(new PdfDocumentBuilder().AddPage(600, 800, content).Build()));

        PdfFormFieldProposal comb = Assert.Single(review.Proposals);
        Assert.Equal(PdfRecognizedFieldKind.Text, comb.Kind);
        Assert.True(comb.SuggestedComb);
        Assert.Equal(5, comb.SuggestedMaximumLength);
    }

    [Fact]
    public void Recognizer_AdjacentYesNoPairStaysTwoCheckBoxes()
    {
        var content = new PdfContentStreamBuilder()
            .Rectangle(100, 500, 12, 12).Stroke()
            .Rectangle(112, 500, 12, 12).Stroke();

        PdfFormRecognitionReview review = PdfFormRecognizer.Recognize(
            PdfDocument.Open(new PdfDocumentBuilder().AddPage(600, 800, content).Build()));

        Assert.Equal(2, review.Proposals.Count);
        Assert.All(review.Proposals, proposal =>
        {
            Assert.Equal(PdfRecognizedFieldKind.CheckBox, proposal.Kind);
            Assert.False(proposal.SuggestedComb);
        });
    }

    [Fact]
    public void Recognizer_OrdinaryTextBoxGetsNoClearAppearanceOrPrintedTextFlag()
    {
        var content = new PdfContentStreamBuilder()
            .Rectangle(100, 500, 160, 20).Stroke()
            .BeginText().SetFont(PdfStandardFont.Helvetica, 12)
            .MoveText(104, 505).ShowLatin1Text("Printed").EndText();

        PdfFormFieldProposal proposal = Assert.Single(PdfFormRecognizer.Recognize(
            PdfDocument.Open(new PdfDocumentBuilder().AddPage(600, 800, content).Build())).Proposals);

        Assert.Equal(PdfRecognizedFieldKind.Text, proposal.Kind);
        Assert.False(proposal.CoversPrintedText);
        Assert.False(proposal.SuggestedAppearanceStyle.MarkedTransparent);
    }

    [Fact]
    public void SetTextFieldValue_CombValueKeepsRectAndMaxLen()
    {
        byte[] source = new PdfDocumentBuilder().AddBlankPage()
            .AddTextField(0, "code", 20, 20, 120, 20, "12", 12,
                new PdfTextFieldOptions { Comb = true, MaximumLength = 4 })
            .Build();
        PdfFormWidgetInfo before = Assert.Single(PdfFormWidgetReader.ReadPage(PdfDocument.Open(source), 0));

        byte[] saved = new PdfIncrementalPageEditor(PdfDocument.Open(source))
            .SetTextFieldValue("code", "4321")
            .Build();

        PdfFormWidgetInfo after = Assert.Single(PdfFormWidgetReader.ReadPage(PdfDocument.Open(saved), 0));
        Assert.Equal(4, after.MaximumLength);
        Assert.Equal((before.Left, before.Bottom, before.Right, before.Top),
            (after.Left, after.Bottom, after.Right, after.Top));
        Assert.Equal("4321", after.Value);
    }

    [Fact]
    public void SetTextFieldValue_LongPlainSingleLineValueKeepsRect()
    {
        byte[] source = new PdfDocumentBuilder().AddBlankPage()
            .AddTextField(0, "name", 20, 20, 60, 20, "", 12)
            .Build();

        byte[] saved = new PdfIncrementalPageEditor(PdfDocument.Open(source))
            .SetTextFieldValue("name", new string('W', 80))
            .Build();

        PdfFormWidgetInfo after = Assert.Single(PdfFormWidgetReader.ReadPage(PdfDocument.Open(saved), 0));
        Assert.Equal((20d, 20d, 80d, 40d), (after.Left, after.Bottom, after.Right, after.Top));
    }

    [Fact]
    public void SetTextFieldTextStyle_FontColorAndDecorationsRoundTrip()
    {
        TrueTypeFont font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(format12: true));
        byte[] source = new PdfDocumentBuilder().AddBlankPage()
            // The test font only carries this glyph.
            .AddTextField(0, "name", 20, 20, 160, 24, "😀", 12, embeddedFont: font)
            .Build();

        byte[] saved = new PdfIncrementalPageEditor(PdfDocument.Open(source))
            .SetTextFieldTextStyle("name", _ => font, new PdfRgbColor(1, 0, 0), underline: true, strikeThrough: true)
            .Build();
        PdfFormWidgetInfo styled = Assert.Single(PdfFormWidgetReader.ReadPage(PdfDocument.Open(saved), 0));
        Assert.Equal("😀", styled.Value);
        Assert.Equal(font.PostScriptName, styled.AppearanceFontName);
        Assert.Contains("1 0 0 rg", styled.DefaultAppearance);
        Assert.True(styled.Underline);
        Assert.True(styled.StrikeThrough);

        // A later value change keeps the saved decorations and color.
        byte[] edited = new PdfIncrementalPageEditor(PdfDocument.Open(saved))
            .SetTextFieldValue("name", "World")
            .Build();
        PdfFormWidgetInfo reread = Assert.Single(PdfFormWidgetReader.ReadPage(PdfDocument.Open(edited), 0));
        Assert.Equal("World", reread.Value);
        Assert.True(reread.Underline);
        Assert.True(reread.StrikeThrough);
        Assert.Contains("1 0 0 rg", reread.DefaultAppearance);

        // Turning the decorations off clears them.
        byte[] cleared = new PdfIncrementalPageEditor(PdfDocument.Open(edited))
            .SetTextFieldTextStyle("name", null, null, underline: false, strikeThrough: false)
            .Build();
        PdfFormWidgetInfo plain = Assert.Single(PdfFormWidgetReader.ReadPage(PdfDocument.Open(cleared), 0));
        Assert.False(plain.Underline);
        Assert.False(plain.StrikeThrough);
    }
}
