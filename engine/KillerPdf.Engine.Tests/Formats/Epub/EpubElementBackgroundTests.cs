using System.IO.Compression;
using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Formats.Epub;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Tests.Fonts;
using Xunit;

namespace KillerPdf.Engine.Tests.Formats.Epub;

public sealed class EpubElementBackgroundTests
{
    [Fact]
    public void ParagraphBackgroundFillsContentWidthBehindSelectableText()
    {
        Verify("<p style=\"background-color:yellow\">A</p>", 30, 16,
            [(0, 0, "blue"), (2, 11, "yellow"), (27, 11, "yellow"),
             (28, 11, "blue"), (7, 5, "black"), (15, 12, "blue")]);
    }

    [Fact]
    public void NestedInlineBackgroundsKeepTransparentChildrenAndTextAboveTheirAncestors()
    {
        Verify("<p style=\"background-color:yellow\"><span style=\"background-color:red\">A"
            + "<span style=\"background-color:transparent\">A</span>"
            + "<span style=\"background-color:lime\">A</span>A</span>A</p>", 50, 16,
            [(0, 0, "blue"), (2, 11, "red"), (10, 11, "red"), (13, 11, "red"),
             (14, 11, "lime"), (19, 11, "lime"), (20, 11, "red"), (25, 11, "red"),
             (26, 11, "yellow"), (47, 11, "yellow"), (7, 5, "black")], "AAAAA");
    }

    [Fact]
    public void TightLineHeightKeepsAllElementBackgroundsBehindOverlappingGlyphInk()
    {
        Verify("<p style=\"background-color:yellow;line-height:5px\"><span style=\"background-color:red\">A<br/>A</span></p>", 30, 20,
            [(0, 0, "blue"), (3, 6, "black"), (20, 6, "yellow"),
             (20, 11, "yellow"), (20, 12, "blue")], "AA");
    }

    [Fact]
    public void WrappedParagraphAndInlineFragmentsResumeAtTheNextPageMargin()
    {
        Verify("<p style=\"background-color:yellow\"><span style=\"background-color:red\">A A A A</span></p>", 16, 26,
            [(0, 0, "blue"), (2, 11, "red"), (2, 21, "red"), (12, 11, "yellow"),
             (12, 21, "yellow"), (14, 11, "blue"), (12, 22, "blue"), (7, 5, "black")],
            "AA", ["A A ", "A A"]);
    }

    [Fact]
    public void RgbaImageCompositesOverItsInlineBackgroundAboveTheParagraph()
    {
        // Frozen 32x16 PNG quadrants: red, half-alpha green, transparent blue, black.
        byte[] png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAACAAAAAQCAYAAAB3AH1ZAAAALklEQVR42mP4z8DwnxIMhA0U4lEHjDpg1AED7ACQGZRhig0YdcCoA0YdMLAOAAB/i72fxwsyHAAAAABJRU5ErkJggg==");
        Verify("<p style=\"background-color:yellow\"><span style=\"background-color:fuchsia\"><img src=\"check.png\" alt=\"\"/>A</span></p>", 50, 30,
            [(0, 0, "blue"), (6, 6, "red"), (26, 6, "halfGreenOverFuchsia"),
             (6, 14, "fuchsia"), (26, 14, "black"), (36, 19, "fuchsia"),
             (45, 19, "yellow"), (39, 13, "black"), (45, 20, "blue")], png: png);
    }

    [Fact]
    public void TransparentParagraphAndUnstyledSiblingRevealTheCanvas()
    {
        Verify("<p style=\"background-color:transparent\"><span style=\"background-color:red\">A</span><span>A</span></p>", 30, 16,
            [(2, 11, "red"), (7, 11, "red"), (8, 11, "blue"),
             (13, 11, "blue"), (27, 11, "blue"), (7, 5, "black")], "AA");
    }

    [Fact]
    public void PackagedClassCascadeAndInlineOverrideChooseThePaintedBackground()
    {
        Verify("<p class=\"marked\" style=\"background-color:yellow\"><span class=\"marked\">A</span></p>", 30, 16,
            [(2, 11, "lime"), (7, 11, "lime"), (8, 11, "yellow"), (27, 11, "yellow")],
            css: "p{background-color:red}p.marked{background-color:blue}span{background-color:red}span.marked{background-color:lime}");
    }

    [Fact]
    public void ConsecutiveExplicitBreaksKeepTheBlankParagraphLineBackground()
    {
        Verify("<p style=\"background-color:yellow\"><span style=\"background-color:red\">A<br/><br/>A</span></p>", 30, 40,
            [(20, 5, "yellow"), (20, 15, "yellow"), (20, 25, "yellow"),
             (2, 15, "yellow"), (20, 32, "blue"), (7, 5, "black")], "AA");
    }

    [Fact]
    public void ParagraphContainingOnlyAnExplicitBreakStillPaintsItsLineBox()
    {
        Verify("<p style=\"background-color:yellow\"><br/></p>", 30, 16,
            [(2, 2, "yellow"), (27, 11, "yellow"), (20, 12, "blue")], "");
    }

    [Fact]
    public void PercentageInlineBackgroundRetainsNativeSubBytePrecision()
    {
        Verify("<p><span style=\"background-color:rgb(50.01%,50.01%,50.01%)\">A</span></p>", 30, 16,
            [(2, 11, "preciseGray"), (7, 11, "preciseGray"), (8, 11, "blue")]);
    }

    private static void Verify(string body, int width, int height,
        (int X, int Y, string Color)[] checks, string expectedText = "A",
        string[]? sourcePages = null, byte[]? png = null, string? css = null)
    {
        byte[] source = Book(body, png, css);
        byte[] original = source.ToArray();
        var book = EpubDocument.Open(source);
        byte[] chapter = book.ReadResource("EPUB/text.xhtml");
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var layout = book.CreateLayout(new EpubLayoutOptions(width, height, font) { FontSize = 10, Margin = 2 });
        if (sourcePages is null) Assert.Single(layout.Pages);
        else Assert.Equal(sourcePages, layout.Pages.Select(page => page.Text));
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        {
            PageWidthPoints = width * .75, PageHeightPoints = height * .75,
            MarginPoints = 1.5, FontSizePoints = 7.5
        }));
        var reader = new PdfPageContentReader(pdf);
        Assert.Equal(layout.Pages.Count, reader.PageCount);
        for (int pageIndex = 0; pageIndex < layout.Pages.Count; pageIndex++)
        {
            var native = layout.RenderPage(pageIndex);
            var enlarged = layout.RenderPage(pageIndex, new PdfRenderOptions(width * 2, height * 2));
            var rendered = new PdfPageRenderer(pdf).Render(pageIndex, new PdfRenderOptions(width, height));
            Assert.Empty(rendered.Diagnostics);
            Assert.Equal(expectedText, string.Concat(reader.Read(pageIndex).Letters.Select(letter => letter.Value)));
            foreach (var check in checks)
            {
                (float[] Linear, byte[] Bgra) expected = check.Color switch
                {
                    "blue" => ([0, 0, 1, 1], [255, 0, 0, 255]),
                    "yellow" => ([1, 1, 0, 1], [0, 255, 255, 255]),
                    "red" => ([1, 0, 0, 1], [0, 0, 255, 255]),
                    "lime" => ([0, 1, 0, 1], [0, 255, 0, 255]),
                    "fuchsia" => ([1, 0, 1, 1], [255, 0, 255, 255]),
                    "halfGreenOverFuchsia" => ([0.21223076f, 0.2158605f, 0.21223076f, 1], [127, 128, 127, 255]),
                    "preciseGray" => ([0.21413372f, 0.21413372f, 0.21413372f, 1], [128, 128, 128, 255]),
                    "black" => ([0, 0, 0, 1], [0, 0, 0, 255]),
                    _ => throw new InvalidOperationException()
                };
                int offset = (check.Y * width + check.X) * 4;
                int enlargedOffset = ((check.Y * 2 + 1) * width * 2 + check.X * 2 + 1) * 4;
                for (int channel = 0; channel < 4; channel++)
                {
                    Assert.InRange(native.Pixels[offset + channel], expected.Linear[channel] - .0000003f, expected.Linear[channel] + .0000003f);
                    Assert.InRange(enlarged.Pixels[enlargedOffset + channel], expected.Linear[channel] - .0000003f, expected.Linear[channel] + .0000003f);
                }
                Assert.Equal(expected.Bgra, rendered.Pixels.Slice(offset, 4).ToArray());
            }
        }
        Assert.Equal(original, source);
        Assert.Equal(chapter, book.ReadResource("EPUB/text.xhtml"));
        if (png is not null) Assert.Equal(png, book.ReadResource("EPUB/check.png"));
        if (css is not null) Assert.Equal(Encoding.UTF8.GetBytes(css), book.ReadResource("EPUB/book.css"));
    }

    private static byte[] Book(string body, byte[]? png, string? css)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, true))
        {
            Add("mimetype", "application/epub+zip");
            Add("META-INF/container.xml", "<container xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\" version=\"1.0\"><rootfiles><rootfile full-path=\"EPUB/book.opf\" media-type=\"application/oebps-package+xml\"/></rootfiles></container>");
            Add("EPUB/book.opf", "<package xmlns=\"http://www.idpf.org/2007/opf\" version=\"3.0\" unique-identifier=\"id\"><metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:identifier id=\"id\">urn:test:element-background</dc:identifier><dc:title>Background</dc:title><dc:language>en</dc:language></metadata><manifest><item id=\"chapter\" href=\"text.xhtml\" media-type=\"application/xhtml+xml\"/>"
                + (png is null ? "" : "<item id=\"image\" href=\"check.png\" media-type=\"image/png\"/>")
                + (css is null ? "" : "<item id=\"css\" href=\"book.css\" media-type=\"text/css\"/>")
                + "</manifest><spine><itemref idref=\"chapter\"/></spine></package>");
            Add("EPUB/text.xhtml", "<html xmlns=\"http://www.w3.org/1999/xhtml\"><head><title>Background</title>"
                + (css is null ? "" : "<link rel=\"stylesheet\" href=\"book.css\"/>")
                + "</head><body style=\"background-color:blue\">" + body + "</body></html>");
            if (css is not null) Add("EPUB/book.css", css);
            if (png is not null)
            {
                var entry = zip.CreateEntry("EPUB/check.png", CompressionLevel.NoCompression);
                entry.LastWriteTime = new DateTimeOffset(2026, 11, 6, 0, 0, 0, TimeSpan.Zero);
                using var stream = entry.Open();
                stream.Write(png);
            }
            void Add(string path, string text)
            {
                var entry = zip.CreateEntry(path, CompressionLevel.NoCompression);
                entry.LastWriteTime = new DateTimeOffset(2026, 11, 6, 0, 0, 0, TimeSpan.Zero);
                using var stream = entry.Open();
                stream.Write(Encoding.UTF8.GetBytes(text));
            }
        }
        return buffer.ToArray();
    }
}
