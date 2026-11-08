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

    [Fact]
    public void InDesignFontObfuscationAndLegacyFontTypesOpenAndOnlyRealEncryptionRefusesItsChapter()
    {
        const string encryption = """
            <encryption xmlns="urn:oasis:names:tc:opendocument:xmlns:container" xmlns:enc="http://www.w3.org/2001/04/xmlenc#">
              <enc:EncryptedData><enc:EncryptionMethod Algorithm="http://ns.adobe.com/pdf/enc#RC"/><enc:CipherData><enc:CipherReference URI="OEBPS/fonts/Minion.otf"/></enc:CipherData></enc:EncryptedData>
              <enc:EncryptedData><enc:EncryptionMethod Algorithm="http://www.idpf.org/2008/embedding"/><enc:CipherData><enc:CipherReference URI="OEBPS/fonts/Legacy.ttf"/></enc:CipherData></enc:EncryptedData>
              <enc:EncryptedData><enc:EncryptionMethod Algorithm="http://www.w3.org/2001/04/xmlenc#aes128-cbc"/><enc:CipherData><enc:CipherReference URI="OEBPS/text/c1.xhtml"/></enc:CipherData></enc:EncryptedData>
            </encryption>
            """;
        var book = new Book
        {
            Encryption = encryption,
            Chapters = { Chapter("<p>A</p>"), Chapter("<p>A</p>") },
            ExtraManifest = "<item id='f1' href='fonts/Minion.otf' media-type='application/vnd.ms-opentype'/>"
                + "<item id='f2' href='fonts/Legacy.ttf' media-type='application/x-font-ttf'/>"
        };
        book.Resources.Add(("fonts/Minion.otf", new byte[2048]));
        book.Resources.Add(("fonts/Legacy.ttf", new byte[2048]));
        var document = EpubDocument.Open(book.Build());
        var layout = document.CreateLayout(Options());
        Assert.Equal(new[] { 0, 1 }, layout.Pages.Select(page => page.SpineIndex));
        var diagnostic = Assert.Single(layout.ChapterDiagnostics);
        Assert.Equal("OEBPS/text/c1.xhtml", diagnostic.Href);
        Assert.Contains("encrypted", diagnostic.Message);
        Assert.Throws<NotSupportedException>(() => document.ReadText(1));
        Assert.Equal("A", document.ReadText(0));
    }

    [Fact]
    public void CalibreStylesheetsSkipAtRulesAndCommentsAndDropOnlyUnsupportedRules()
    {
        const string css = """
            @CHARSET "UTF-8";
            /* page_styles.css from Calibre */
            @page { margin-bottom: 5pt; margin-top: 5pt }
            @font-face { font-family: "Minion"; src: url(../fonts/Minion.otf) }
            @media print { p { font-size: 40px } }
            @media amzn-kf8 { p { font-size: 41px } }
            @supports (display: grid) { p { font-size: 42px } }
            p.x { font-size: 20px /* inline comment */ }
            p:first-child { font-size: 30px }
            p + p { font-size: 50px }
            p::first-letter { font-size: 60px }
            * { color: #f00 }
            @media only screen and (min-width: 300px) { p.x.y { font-size: 25px } }
            """;
        var layout = Layout(new Book { Css = css, Chapters = { Chapter("<div><p class='x'>A</p><p class='x'>A</p><p class='x y'>A</p></div>") } });
        Assert.Empty(layout.ChapterDiagnostics);
        var placements = layout.Pages.SelectMany(page => page.TextPlacements).ToArray();
        // Specificity ties go to the later rule: :first-child (0,1,1) beats p.x for the first paragraph only;
        // the sibling and pseudo-element rules are dropped; the screen media rule (0,2,1) wins for the third.
        Assert.Equal(new double[] { 30, 20, 25 }, placements.Select(placement => placement.Height));
        Assert.All(placements, placement => Assert.Equal(new PdfRgbColor(1, 0, 0), placement.Color));
    }

    [Fact]
    public void CalibreAndSigilMarkupLaysOutWithPhrasingElementsInertAttributesAndHeadStyles()
    {
        const string head = "<meta http-equiv=\"Content-Type\" content=\"text/html; charset=utf-8\"/>"
            + "<style type=\"text/css\">p.big { font-size: 20px } .calibre1 { display: block }</style>";
        const string body = """
            <section epub:type="chapter" role="doc-chapter" aria-labelledby="h" dir="ltr" id="ch1">
              <p class="big" lang="en" title="Tip" data-x="1">A</p>
              <p>A<sup>A</sup><sub>A</sub><small>A</small><u>A</u><s>A</s><code>A</code><cite>A</cite><abbr title="x">A</abbr><q>A</q><ins>A</ins><del>A</del><time>A</time><bdi>A</bdi><mark>A</mark><unknown-tag>A</unknown-tag></p>
              <ol><li>A</li><li>A</li></ol>
              <dl><dt>A</dt><dd>A</dd></dl>
              <figure><figcaption>A</figcaption></figure>
              <a name="old-anchor"></a>
            </section>
            """;
        var layout = Layout(new Book { Chapters = { Chapter(body, head) } });
        Assert.Empty(layout.ChapterDiagnostics);
        var placements = layout.Pages.SelectMany(page => page.TextPlacements).ToArray();
        // The head style sets the first paragraph to 20px; sup is font-size smaller, 10px / 1.2 (HTML user-agent sheet).
        Assert.Equal(20, placements[0].Height, 6);
        Assert.Equal(10 / 1.2, placements[2].Height, 6);
        // Every letter is placed: 1 + 16 in the paragraphs, 2 list items, a term and definition, and a caption.
        Assert.Equal(22, placements.Length);
    }

    /// <summary>A construct that used to refuse its chapter now lays out the chapter without a diagnosis page.</summary>
    internal static EpubLayoutDocument AssertLaysOut(EpubDocument book, EpubLayoutOptions options)
    {
        var layout = book.CreateLayout(options);
        Assert.Empty(layout.ChapterDiagnostics);
        Assert.NotEmpty(layout.Pages);
        return layout;
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
        internal string SpineToc { get; init; } = "";
        internal Dictionary<int, string> ItemrefAttributes { get; } = new();
        internal string ExtraSpine { get; init; } = "";
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
                    spine.Append($"<itemref idref=\"c{index}\"{(index == Chapters.Count - 1 ? SpineAttributes : "")}{ItemrefAttributes.GetValueOrDefault(index, "")}/>");
                    Add($"OEBPS/text/c{index}.xhtml", Encoding.UTF8.GetBytes(Chapters[index]));
                }
                Add("OEBPS/text/style.css", Encoding.UTF8.GetBytes(Css));
                foreach (var (path, data) in Resources) Add("OEBPS/" + path, data);
                Add("OEBPS/nav.xhtml", Encoding.UTF8.GetBytes(Nav ?? "<html xmlns=\"http://www.w3.org/1999/xhtml\" xmlns:epub=\"http://www.idpf.org/2007/ops\"><head><title>Contents</title></head><body><nav epub:type=\"toc\"><ol><li><a href=\"text/c0.xhtml\">Start</a></li></ol></nav></body></html>"));
                Add("OEBPS/content.opf", Encoding.UTF8.GetBytes($"""
                    <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="id"><metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:identifier id="id">urn:uuid:12345678-1234-1234-1234-123456789abc</dc:identifier><dc:title>Compat</dc:title><dc:language>en</dc:language></metadata>
                    <manifest>{manifest}<item id="css" href="text/style.css" media-type="text/css"/><item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>{ExtraManifest}</manifest><spine{(SpineToc.Length > 0 ? " toc=\"" + SpineToc + "\"" : "")}>{spine}{ExtraSpine}</spine></package>
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
