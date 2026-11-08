using System.IO.Compression;
using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Formats.Epub;
using KillerPdf.Engine.Tests.Fonts;
using Xunit;

namespace KillerPdf.Engine.Tests.Formats.Epub;

// Synthetic books shaped like Calibre, Sigil, InDesign and Project Gutenberg output.
// Unknown or unsupported constructs must degrade as CSS and HTML require instead of refusing.
public sealed class EpubCompatibilityTests
{
    [Fact]
    public void UnknownPropertiesAndValuesAreIgnoredAndAbsoluteUnitsResolveAtNinetySixDpi()
    {
        const string css = """
            p { font-size: 12pt; background: white; page-break-before: avoid; -webkit-hyphens: auto; color: rgba(0,0,0,.5) !important }
            p.pica { font-size: 1pc; text-align: start; display: inline-block }
            p.inch { font-size: .5in; margin: 0 0 1em 0; widows: 2 }
            p.mm { font-size: 10mm; line-height: 1.2 }
            """;
        var layout = Layout(new Book { Css = css, Chapters = { Chapter("<p>A</p><p class='pica'>A</p><p class='inch'>A</p><p class='mm'>A</p>") } });
        Assert.Empty(layout.ChapterDiagnostics);
        var heights = layout.Pages.SelectMany(page => page.TextPlacements).Select(placement => placement.Height).ToArray();
        // CSS Values 4: 1pt = 96/72 px, 1pc = 12pt, 1in = 96px, 1mm = 96/25.4 px.
        Assert.Equal(16, heights[0], 6);
        Assert.Equal(16, heights[1], 6);
        Assert.Equal(48, heights[2], 6);
        Assert.Equal(960 / 25.4, heights[3], 6);
    }

    /// <summary>An ignored declaration, attribute or element lays out exactly like the document without it.</summary>
    internal static void AssertSameLayout(EpubLayoutDocument expected, EpubLayoutDocument actual)
    {
        Assert.Empty(actual.ChapterDiagnostics);
        Assert.Equal(expected.Pages.Count, actual.Pages.Count);
        for (int index = 0; index < expected.Pages.Count; index++)
        {
            var left = expected.Pages[index];
            var right = actual.Pages[index];
            Assert.Equal(left.Text, right.Text);
            Assert.Equal(left.TextStart, right.TextStart);
            Assert.Equal(left.TextPlacements, right.TextPlacements);
            Assert.Equal(left.CanvasBackground, right.CanvasBackground);
            Assert.Equal(left.Glyphs.Count, right.Glyphs.Count);
            Assert.Equal(expected.RenderPage(index).Pixels.ToArray(), actual.RenderPage(index).Pixels.ToArray());
        }
    }

    internal static EpubLayoutDocument Layout(Book book, double width = 400, double height = 600) =>
        EpubDocument.Open(book.Build()).CreateLayout(Options(width, height));

    internal static EpubLayoutOptions Options(double width = 400, double height = 600) => new(width, height,
        TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true)))
    {
        FontSize = 10, Margin = 2, IsolateChapterFailures = true
    };

    internal static string Chapter(string body, string head = "", string doctype = "") =>
        doctype + "<html xmlns=\"http://www.w3.org/1999/xhtml\" xmlns:epub=\"http://www.idpf.org/2007/ops\"><head><title>Chapter</title>"
        + "<link rel=\"stylesheet\" type=\"text/css\" href=\"style.css\"/>" + head + "</head><body>" + body + "</body></html>";

    internal sealed class Book
    {
        internal List<string> Chapters { get; } = new();
        internal string Css { get; init; } = "";
        internal string SpineAttributes { get; init; } = "";
        internal string ExtraManifest { get; init; } = "";
        internal string? Encryption { get; init; }
        internal string? Nav { get; init; }
        internal List<(string Path, byte[] Data)> Resources { get; } = new();

        internal byte[] Build()
        {
            using var output = new MemoryStream();
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
            {
                Add("mimetype", Encoding.ASCII.GetBytes("application/epub+zip"), CompressionLevel.NoCompression);
                Add("META-INF/container.xml", Encoding.UTF8.GetBytes("<container xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\" version=\"1.0\"><rootfiles><rootfile full-path=\"OEBPS/content.opf\" media-type=\"application/oebps-package+xml\"/></rootfiles></container>"));
                if (Encryption is not null) Add("META-INF/encryption.xml", Encoding.UTF8.GetBytes(Encryption));
                var manifest = new StringBuilder();
                var spine = new StringBuilder();
                for (int index = 0; index < Chapters.Count; index++)
                {
                    manifest.Append($"<item id=\"c{index}\" href=\"text/c{index}.xhtml\" media-type=\"application/xhtml+xml\"/>");
                    spine.Append($"<itemref idref=\"c{index}\"{(index == Chapters.Count - 1 ? SpineAttributes : "")}/>");
                    Add($"OEBPS/text/c{index}.xhtml", Encoding.UTF8.GetBytes(Chapters[index]));
                }
                Add("OEBPS/text/style.css", Encoding.UTF8.GetBytes(Css));
                foreach (var (path, data) in Resources) Add("OEBPS/" + path, data);
                Add("OEBPS/nav.xhtml", Encoding.UTF8.GetBytes(Nav ?? "<html xmlns=\"http://www.w3.org/1999/xhtml\" xmlns:epub=\"http://www.idpf.org/2007/ops\"><head><title>Contents</title></head><body><nav epub:type=\"toc\"><ol><li><a href=\"text/c0.xhtml\">Start</a></li></ol></nav></body></html>"));
                Add("OEBPS/content.opf", Encoding.UTF8.GetBytes($"""
                    <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="id"><metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:identifier id="id">urn:uuid:12345678-1234-1234-1234-123456789abc</dc:identifier><dc:title>Compat</dc:title><dc:language>en</dc:language></metadata>
                    <manifest>{manifest}<item id="css" href="text/style.css" media-type="text/css"/><item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>{ExtraManifest}</manifest><spine>{spine}</spine></package>
                    """));

                void Add(string path, byte[] data, CompressionLevel level = CompressionLevel.Optimal)
                {
                    var entry = zip.CreateEntry(path, level);
                    entry.LastWriteTime = new DateTimeOffset(2026, 11, 8, 0, 0, 0, TimeSpan.Zero);
                    using var stream = entry.Open();
                    stream.Write(data);
                }
            }
            return output.ToArray();
        }
    }
}
