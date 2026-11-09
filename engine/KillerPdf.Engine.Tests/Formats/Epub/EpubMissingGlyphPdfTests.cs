using System.Buffers.Binary;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Formats.Epub;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Tests.Fonts;
using Xunit;
using static KillerPdf.Engine.Tests.Formats.Epub.EpubCompatibilityTests;

namespace KillerPdf.Engine.Tests.Formats.Epub;

// A character no font maps shows the font's .notdef glyph, in the reader and in the converted PDF alike. The fixture's
// .notdef is a composite of the A outline, 5px wide at 10px, between two 6px letters A on a 60 by 40 page.
public sealed class EpubMissingGlyphPdfTests
{
    [Fact]
    public void TheMissingGlyphBoxIsDrawnInTheConvertedPdfWhereTheReaderDrawsIt()
    {
        var font = FontWithDrawnNotdef();
        // The unmapped character alone in its paragraph, so any ink on the page is the box.
        var book = EpubDocument.Open(new Book { Chapters = { Chapter("<p>&#x2720;</p><p>A</p>") } }.Build());

        var layout = book.CreateLayout(new EpubLayoutOptions(60, 40, font) { FontSize = 10, Margin = 2, IsolateChapterFailures = true });

        Assert.Empty(layout.ChapterDiagnostics);
        Assert.Equal(EpubOmissionReason.MissingGlyph, Assert.Single(layout.ContentDiagnostics).Reason);
        var page = Assert.Single(layout.Pages);
        var box = page.TextPlacements.Single(placement => placement.Start == 0);
        Assert.Equal((2d, 2d, 5d), (box.Left, box.Top, box.Width));
        Assert.True(page.TextPlacements.Single(placement => placement.Start > 0).Top >= 12);
        var native = layout.RenderPage(0);
        Assert.True(Inked(native.Pixels, native.Width, 1f, 0, 0, 60, 12));
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 45, PageHeightPoints = 30, MarginPoints = 1.5, FontSizePoints = 7.5 }));
        var rendered = new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(60, 40));
        Assert.Empty(rendered.Diagnostics);
        Assert.True(Inked<byte>(rendered.Pixels.Span, rendered.Width, 255, 0, 0, 60, 12));
        // The box stands for no character the font can encode, so the PDF text keeps only the letter.
        Assert.Equal("A", string.Concat(new PdfPageContentReader(pdf).Read(0).Letters.Select(letter => letter.Value)));
    }

    private static bool Inked<T>(ReadOnlySpan<T> pixels, int width, T white, int left, int top, int right, int bottom)
        where T : IComparable<T>
    {
        for (int y = top; y < bottom; y++)
            for (int x = left; x < right; x++)
                if (pixels[(y * width + x) * 4].CompareTo(white) < 0) return true;
        return false;
    }

    // Glyph 0 becomes the composite (it places glyph 2), glyph 1 is empty, and A maps to glyph 2.
    private static TrueTypeFont FontWithDrawnNotdef()
    {
        byte[] cmap = new byte[40];
        void U16(int offset, ushort value) => BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(offset), value);
        void U32(int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(offset), value);
        U16(2, 1); U16(4, 3); U16(6, 10); U32(8, 12);
        U16(12, 12); U32(16, 28); U32(24, 1);
        U32(28, 65); U32(32, 65); U32(36, 2);
        byte[] bytes = TrueTypeFontTests.BuildTestFont(true, includeOutlines: true, includeCompound: true, cmap: cmap);
        int count = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(4));
        for (int record = 12; record < 12 + count * 16; record += 16)
        {
            if (!bytes.AsSpan(record, 4).SequenceEqual("loca"u8)) continue;
            int offset = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(record + 8));
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset + 4), 20);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset + 8), 20);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset + 12), 44);
        }
        return TrueTypeFont.Load(bytes);
    }
}
