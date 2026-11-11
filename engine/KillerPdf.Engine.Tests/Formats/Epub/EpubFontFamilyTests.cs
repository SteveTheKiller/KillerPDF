using System.Buffers.Binary;
using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Formats.Epub;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Tests.Fonts;
using Xunit;

namespace KillerPdf.Engine.Tests.Formats.Epub;

public sealed class EpubFontFamilyTests
{
    [Fact]
    public void UnavailableFamilyListUsesExplicitFallbackWithoutChangingTextOrSource()
    {
        byte[] source = EpubNavigationDestinationTests.Book(
            firstBody: "<p style='font-family: MissingFace, serif'>A</p>");
        byte[] original = source.ToArray();
        var book = EpubDocument.Open(source);
        byte[] chapter = book.ReadResource("EPUB/first.xhtml");
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));

        var layout = book.CreateLayout(new EpubLayoutOptions(30, 23, font) { FontSize = 10, Margin = 2 });

        var placement = Assert.Single(layout.Pages[0].TextPlacements);
        Assert.Equal(new EpubTextPlacement(0, 0, 1, 2, 2, 6, 10), placement);
        Assert.Equal("A", layout.Pages[0].Text);
        Assert.Equal(chapter, book.ReadResource("EPUB/first.xhtml"));
        Assert.Equal(original, source);
    }

    [Fact]
    public void NamedFaceChangesNativeAndSavedPdfAdvancesWithoutChangingAdjacentFallback()
    {
        var book = EpubDocument.Open(EpubNavigationDestinationTests.Book(
            firstBody: "<p><span style='font-family: WideFace'>A</span>A</p>"));
        var fallback = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var wide = WideFont();
        TrueTypeFont? Resolve(EpubFontFamily family) => family.Name == "WideFace" ? wide : null;

        var layout = book.CreateLayout(new EpubLayoutOptions(30, 23, fallback)
            { FontSize = 10, Margin = 2, FontFamilyResolver = Resolve });

        var placements = layout.Pages[0].TextPlacements;
        Assert.Equal(new[] { 12d, 6d }, placements.Select(item => item.Width));
        Assert.Equal(new[] { 2d, 14d }, placements.Select(item => item.Left));
        Assert.Equal(new float[] { 0, 0, 0, 1 }, layout.RenderPage(0).Pixels.Slice((5 * 30 + 19) * 4, 4).ToArray());
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(fallback)
        {
            PageWidthPoints = 22.5, PageHeightPoints = 17.25, MarginPoints = 1.5,
            FontSizePoints = 7.5, FontFamilyResolver = Resolve
        }));
        Assert.Equal("AA", string.Concat(new PdfPageContentReader(pdf).Read(0).Letters.Select(letter => letter.Value)));
        var pixels = new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(30, 23));
        Assert.Empty(pixels.Diagnostics);
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, pixels.Pixels.Slice((5 * 30 + 19) * 4, 4).ToArray());
    }

    [Fact]
    public void FamilyFallbackSelectsDifferentFacesForAdjacentCharacters()
    {
        var book = EpubDocument.Open(EpubNavigationDestinationTests.Book(
            firstBody: "<p style='font-family: FirstFace, SecondFace'>AB</p>"));
        var first = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var second = WideFont(bOnly: true);
        TrueTypeFont? Resolve(EpubFontFamily family) => family.Name == "FirstFace" ? first
            : family.Name == "SecondFace" ? second : null;

        var layout = book.CreateLayout(new EpubLayoutOptions(30, 23, first)
            { FontSize = 10, Margin = 2, FontFamilyResolver = Resolve });

        Assert.Equal(new[] { 6d, 12d }, layout.Pages[0].TextPlacements.Select(item => item.Width));
        Assert.Equal(new[] { 2d, 8d }, layout.Pages[0].TextPlacements.Select(item => item.Left));
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(first)
        {
            PageWidthPoints = 22.5, PageHeightPoints = 17.25, MarginPoints = 1.5,
            FontSizePoints = 7.5, FontFamilyResolver = Resolve
        }));
        Assert.Equal("AB", string.Concat(new PdfPageContentReader(pdf).Read(0).Letters.Select(letter => letter.Value)));
    }

    [Fact]
    public void QuotedFamiliesKeepCommasAndGenericNamesDistinctFromGenericRequests()
    {
        var book = EpubDocument.Open(EpubNavigationDestinationTests.Book(firstBody:
            "<p><span style='font-family: &quot;serif&quot;'>A</span>"
            + "<span style='font-family: serif'>A</span>"
            + "<span style='font-family: &quot;Wide, Face&quot;'>A</span>"
            + "<span style='font-family: Wide Face'>A</span></p>"));
        var fallback = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var wide = WideFont();
        TrueTypeFont? Resolve(EpubFontFamily family) => family.IsGeneric ? fallback
            : family.Name is "serif" or "Wide, Face" or "Wide Face" ? wide : null;

        var layout = book.CreateLayout(new EpubLayoutOptions(64, 23, fallback)
            { FontSize = 10, Margin = 2, FontFamilyResolver = Resolve });

        Assert.Equal(new[] { 12d, 6d, 12d, 12d }, layout.Pages[0].TextPlacements.Select(item => item.Width));
    }

    [Fact]
    public void MixedFaceMetricsKeepOneBaselineAndEmbedSelectedAdvances()
    {
        var book = EpubDocument.Open(EpubNavigationDestinationTests.Book(
            firstBody: "<p><span style='font-family: TallFace'>A</span>A</p>"));
        var fallback = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var tall = WideFont(taller: true);
        TrueTypeFont? Resolve(EpubFontFamily family) => family.Name == "TallFace" ? tall : null;

        var layout = book.CreateLayout(new EpubLayoutOptions(30, 23, fallback)
            { FontSize = 10, Margin = 2, FontFamilyResolver = Resolve });

        Assert.Equal(new[] { 2d, 6d }, layout.Pages[0].TextPlacements.Select(item => item.Top));
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(fallback)
        {
            PageWidthPoints = 22.5, PageHeightPoints = 17.25, MarginPoints = 1.5,
            FontSizePoints = 7.5, FontFamilyResolver = Resolve
        }));
        var letters = new PdfPageContentReader(pdf).Read(0).Letters;
        Assert.Equal(new[] { 6.75, 6.75 }, letters.Select(letter => letter.StartBaseLine.Y));
        Assert.Equal(new[] { 9d, 4.5 }, letters.Select(letter => letter.EndBaseLine.X - letter.StartBaseLine.X));
    }

    [Theory]
    [InlineData("")]
    [InlineData(",")]
    [InlineData("Face,")]
    [InlineData(",Face")]
    [InlineData("Face,,serif")]
    [InlineData("\"Face")]
    [InlineData("\"Face\"junk")]
    [InlineData("Face/Name")]
    [InlineData("url(https://example.invalid/font)")]
    [InlineData("123Face")]
    [InlineData("\"Escaped\\Face\"")]
    [InlineData("inherit")]
    [InlineData("initial")]
    [InlineData("unset")]
    // A malformed or unsupported family list is an ignored declaration; the inherited family stays.
    public void UnsupportedAndMalformedFamilyListsAreIgnored(string families)
    {
        var book = EpubDocument.Open(EpubNavigationDestinationTests.Book(
            firstBody: "<p style='font-family:" + System.Security.SecurityElement.Escape(families) + "'>A</p>"));
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var options = new EpubLayoutOptions(30, 23, font) { FontSize = 10, Margin = 2 };
        EpubCompatibilityTests.AssertSameLayout(
            EpubDocument.Open(EpubNavigationDestinationTests.Book(firstBody: "<p>A</p>")).CreateLayout(options), book.CreateLayout(options));
    }

    [Theory]
    [InlineData(32, false)]
    [InlineData(33, true)]
    public void FamilyListsBeyondTheProcessingLimitDropOnlyTheirDeclaration(int count, bool exceeds)
    {
        string names = string.Join(',', Enumerable.Repeat("MissingFace", count));
        var book = EpubDocument.Open(EpubNavigationDestinationTests.Book(firstBody:
            "<p style='font-family:" + names + "'>A</p>"));
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var options = new EpubLayoutOptions(30, 23, font) { FontSize = 10, Margin = 2 };

        var layout = book.CreateLayout(options);
        Assert.Equal(6, Assert.Single(layout.Pages[0].TextPlacements).Width);
        Assert.Equal(exceeds ? 1 : 0, layout.IgnoredDeclarations.Sum(item => item.Count));
    }

    [Fact]
    public void FontResolutionObservesMidLayoutCancellation()
    {
        var book = EpubDocument.Open(EpubNavigationDestinationTests.Book(
            firstBody: "<p style='font-family: Face'>A</p>"));
        var font = WideFont();
        using var canceled = new CancellationTokenSource();
        var options = new EpubLayoutOptions(30, 23, font)
        {
            FontSize = 10, Margin = 2,
            FontFamilyResolver = _ => { canceled.Cancel(); return font; }
        };

        Assert.Throws<OperationCanceledException>(() => book.CreateLayout(options, canceled.Token));
    }

    [Theory]
    [InlineData(256)]
    [InlineData(257)]
    // Lookups stay bounded at 256 families; later families use the default face instead of refusing (EP-BF-031).
    public void UnavailableFontFamiliesAreBoundedAndFallBackAcrossOneLayout(int count)
    {
        string body = string.Concat(Enumerable.Range(0, count).Select(index =>
            "<p style='font-family: Face" + index + "'>A</p>"));
        var book = EpubDocument.Open(EpubNavigationDestinationTests.Book(firstBody: body));
        var font = WideFont();
        var options = new EpubLayoutOptions(30, 23, font)
            { FontSize = 10, Margin = 2, FontFamilyResolver = _ => null };

        var layout = book.CreateLayout(options);
        Assert.Equal(count, layout.Pages.Where(page => page.SpineIndex == 0).Sum(page => page.Text.Count(character => character == 'A')));
    }

    private static TrueTypeFont WideFont(bool bOnly = false, bool taller = false)
    {
        byte[] bytes = TrueTypeFontTests.BuildTestFont(false, includeOutlines: true);
        int tables = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(4, 2));
        for (int entry = 12; entry < 12 + tables * 16; entry += 16)
        {
            string tag = Encoding.ASCII.GetString(bytes, entry, 4);
            int offset = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(entry + 8, 4));
            if (tag == "hmtx")
            {
                BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(offset + 4, 2), 1200);
            }
            if (taller && tag == "hhea")
                BinaryPrimitives.WriteInt16BigEndian(bytes.AsSpan(offset + 4, 2), 1200);
            if (bOnly && tag == "cmap")
            {
                BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(offset + 12 + 14, 2), 'B');
                BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(offset + 12 + 20, 2), 'B');
                BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(offset + 12 + 24, 2), 0xFFBF);
            }
        }
        return TrueTypeFont.Load(bytes);
    }
}
