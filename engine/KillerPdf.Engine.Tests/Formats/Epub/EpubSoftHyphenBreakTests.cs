using System.Buffers.Binary;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Formats.Epub;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Tests.Fonts;
using Xunit;
using static KillerPdf.Engine.Tests.Formats.Epub.EpubCompatibilityTests;

namespace KillerPdf.Engine.Tests.Formats.Epub;

// CSS Text 3 hyphens: manual: a soft hyphen is a break opportunity that shows a hyphen only where the line breaks.
// The test font draws A and the hyphen-minus with the same 6px glyph at 10px; a 22px page with 2px margins ends lines at 20.
public sealed class EpubSoftHyphenBreakTests
{
    private static readonly string SoftHyphen = ((char)0xAD).ToString();

    [Fact]
    public void ALineBrokenAtASoftHyphenShowsAHyphenWhileTheTextKeepsTheWholeWord()
    {
        var font = HyphenFont();
        var book = EpubDocument.Open(new Book { Chapters = { Chapter("<p>A&#173;AAA</p>") } }.Build());

        var layout = book.CreateLayout(new EpubLayoutOptions(22, 40, font) { FontSize = 10, Margin = 2, IsolateChapterFailures = true });

        Assert.Empty(layout.ChapterDiagnostics);
        var page = Assert.Single(layout.Pages);
        // Copy and search read the word without a hyphen: the soft hyphen stays the only character between the parts.
        Assert.Equal("A" + SoftHyphen + "AAA", page.Text);
        var hyphen = page.TextPlacements.Single(placement => placement.Start == 1);
        Assert.Equal((8d, 2d, 6d), (hyphen.Left, hyphen.Top, hyphen.Width));
        Assert.True(Inked(layout.RenderPage(0), 8, 2, 14, 12));
        // The PDF draws the hyphen as a glyph whose text is the soft hyphen, which readers join across the break.
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 16.5, PageHeightPoints = 30, MarginPoints = 1.5, FontSizePoints = 7.5 }));
        Assert.Equal("A" + SoftHyphen + "AAA", string.Concat(new PdfPageContentReader(pdf).Read(0).Letters.Select(letter => letter.Value)));
        var rendered = new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(22, 40));
        Assert.Empty(rendered.Diagnostics);
        Assert.True(Inked(rendered, 8, 2, 14, 12));
    }

    [Fact]
    public void ASoftHyphenWhereTheLineDoesNotBreakStaysInvisible()
    {
        var font = HyphenFont();
        var book = EpubDocument.Open(new Book { Chapters = { Chapter("<p>A&#173;A</p>") } }.Build());

        var layout = book.CreateLayout(new EpubLayoutOptions(60, 40, font) { FontSize = 10, Margin = 2, IsolateChapterFailures = true });

        var page = Assert.Single(layout.Pages);
        Assert.Equal(0, page.TextPlacements.Single(placement => placement.Start == 1).Width);
        Assert.Equal(8, page.TextPlacements.Single(placement => placement.Start == 2).Left);
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 45, PageHeightPoints = 30, MarginPoints = 1.5, FontSizePoints = 7.5 }));
        Assert.Equal("AA", string.Concat(new PdfPageContentReader(pdf).Read(0).Letters.Select(letter => letter.Value)));
    }

    private static bool Inked(PdfRenderedFrame frame, int left, int top, int right, int bottom)
    {
        var pixels = frame.Pixels;
        for (int y = top; y < bottom; y++)
            for (int x = left; x < right; x++)
                if (pixels[(y * frame.Width + x) * 4] < 0.5f) return true;
        return false;
    }

    private static bool Inked(PdfRenderedPage page, int left, int top, int right, int bottom)
    {
        var pixels = page.Pixels.Span;
        for (int y = top; y < bottom; y++)
            for (int x = left; x < right; x++)
                if (pixels[(y * page.Width + x) * 4] < 128) return true;
        return false;
    }

    // Format 12 cmap: space to the empty glyph 2, hyphen-minus and A to glyph 1.
    private static TrueTypeFont HyphenFont()
    {
        byte[] cmap = new byte[64];
        void U16(int offset, ushort value) => BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(offset), value);
        void U32(int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(offset), value);
        U16(2, 1); U16(4, 3); U16(6, 10); U32(8, 12);
        U16(12, 12); U32(16, 52); U32(24, 3);
        U32(28, 32); U32(32, 32); U32(36, 2);
        U32(40, 45); U32(44, 45); U32(48, 1);
        U32(52, 65); U32(56, 65); U32(60, 1);
        byte[] bytes = TrueTypeFontTests.BuildTestFont(true, includeOutlines: true, includeCompound: true, cmap: cmap);
        int count = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(4));
        for (int record = 12; record < 12 + count * 16; record += 16)
        {
            if (!bytes.AsSpan(record, 4).SequenceEqual("loca"u8)) continue;
            int offset = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(record + 8));
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset + 4), 20);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset + 8), 44);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset + 12), 44);
        }
        return TrueTypeFont.Load(bytes);
    }
}
