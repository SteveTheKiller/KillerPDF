using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Formats.Epub;
using KillerPdf.Engine.Tests.Fonts;
using Xunit;
using static KillerPdf.Engine.Tests.Formats.Epub.EpubCompatibilityTests;

namespace KillerPdf.Engine.Tests.Formats.Epub;

// EP-BF-021, 031: a face the reader cannot draw, or one lookup too many, falls back to the default face.
public sealed class EpubFontFallbackTests
{
    [Fact]
    public void AnInstalledCffFaceIsPassedOverForTheDefaultFace()
    {
        var cff = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(format12: false, cffOutlines: true));
        Assert.True(cff.HasCffOutlines);
        var options = Options() with
        {
            FontFaceResolver = request => request.Family?.Name == "Minion"
                ? new EpubFontFaceResult(cff, request.Weight, EpubFontMatch.Exact) { ActualStyle = request.Style } : null
        };
        var book = EpubDocument.Open(new Book { Css = "p { font-family: Minion }", Chapters = { Chapter("<p>A</p>") } }.Build());

        var layout = book.CreateLayout(options);

        Assert.Empty(layout.ChapterDiagnostics);
        var placement = Assert.Single(layout.Pages[0].TextPlacements);
        Assert.Equal(6, placement.Width);
    }

    [Fact]
    public void MoreThan256DistinctFamiliesFallBackInsteadOfRefusingTheChapter()
    {
        var spans = string.Concat(Enumerable.Range(0, 300).Select(index => $"<span style='font-family: F{index}'>A</span> "));
        var options = Options(4000, 600) with { FontFaceResolver = _ => null };
        var book = EpubDocument.Open(new Book { Chapters = { Chapter("<p>" + spans + "</p>") } }.Build());

        var layout = book.CreateLayout(options);

        Assert.Empty(layout.ChapterDiagnostics);
        Assert.Equal(300, layout.Pages.SelectMany(page => page.TextPlacements).Count(placement => placement.Width == 6));
    }
}
