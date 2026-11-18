using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Formats.Epub;
using Xunit;

namespace KillerPdf.Engine.Tests.Formats.Epub;

public sealed class PdfEpubExportTests
{
    private static readonly XNamespace Xhtml = "http://www.w3.org/1999/xhtml";
    private static readonly XNamespace Opf = "http://www.idpf.org/2007/opf";
    private static readonly XNamespace Ops = "http://www.idpf.org/2007/ops";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";

    [Fact]
    public void PackageIsAWellFormedEpub3Book()
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(612, 792, Text(new PdfContentStreamBuilder(), 24, 72, 700, "Chapter One", bold: true)
                .Let(b => Text(b, 11, 72, 660, "Body copy for the first chapter of this book.")))
            .Build());

        byte[] epub = PdfEpubExport.Write(document);

        Package package = ReadPackage(epub);
        Assert.Equal("3.0", package.Opf.Root!.Attribute("version")?.Value);
        Assert.Contains(package.Blocks, block => block.Name == "h1" && block.Text == "Chapter One");
        // The reader's own EPUB loader accepts what the writer produced.
        EpubDocument reopened = EpubDocument.Open(epub);
        Assert.NotEmpty(reopened.Spine);
    }

    [Fact]
    public void ExportIsDeterministic()
    {
        byte[] source = new PdfDocumentBuilder()
            .AddPage(612, 792, Text(new PdfContentStreamBuilder(), 11, 72, 700, "Same input, same book."))
            .Build();

        Assert.Equal(PdfEpubExport.Write(PdfDocument.Open(source)),
            PdfEpubExport.Write(PdfDocument.Open(source)));
    }

    [Fact]
    public void WrappedLinesJoinIntoParagraphsAndHyphenatedWordsMend()
    {
        var content = new PdfContentStreamBuilder();
        Text(content, 11, 72, 700, "The quick brown fox jum-");
        Text(content, 11, 72, 686, "ped over the lazy dog and");
        Text(content, 11, 72, 672, "kept running.");
        Text(content, 11, 72, 630, "A second paragraph starts after");
        Text(content, 11, 72, 616, "a blank gap.");
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(612, 792, content).Build());

        Package package = ReadPackage(PdfEpubExport.Write(document));

        Assert.Equal(
        [
            "The quick brown fox jumped over the lazy dog and kept running.",
            "A second paragraph starts after a blank gap."
        ], package.Blocks.Where(block => block.Name == "p").Select(block => block.Text));
    }

    [Fact]
    public void LargerTextBecomesHeadingsByRank()
    {
        var content = new PdfContentStreamBuilder();
        Text(content, 24, 72, 720, "Part Title", bold: true);
        Text(content, 16, 72, 680, "A Section", bold: true);
        Text(content, 11, 72, 650, "Plenty of ordinary body text sits here so that the body size is");
        Text(content, 11, 72, 636, "clearly the most common size on the page and headings stand out.");
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(612, 792, content).Build());

        Package package = ReadPackage(PdfEpubExport.Write(document));

        Assert.Equal(["h1:Part Title", "h2:A Section", "p:Plenty of ordinary body text sits here so that the body size is clearly the most common size on the page and headings stand out."],
            package.Blocks.Select(block => $"{block.Name}:{block.Text}"));
        // The navigation document lists the headings.
        Assert.Contains("Part Title", package.Nav.Descendants(Xhtml + "a").Select(a => a.Value));
    }

    [Fact]
    public void TwoColumnPagesReadDownTheLeftColumnBeforeTheRight()
    {
        var content = new PdfContentStreamBuilder();
        // Drawn row by row across both columns, so content order interleaves them.
        Text(content, 11, 40, 700, "Left column line one keeps");
        Text(content, 11, 320, 700, "Right column line one keeps");
        Text(content, 11, 40, 686, "going on the left side.");
        Text(content, 11, 320, 686, "going on the right side.");
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(612, 792, content).Build());

        Package package = ReadPackage(PdfEpubExport.Write(document));

        Assert.Equal(
        [
            "Left column line one keeps going on the left side.",
            "Right column line one keeps going on the right side."
        ], package.Blocks.Where(block => block.Name == "p").Select(block => block.Text));
    }

    [Fact]
    public void RunningHeadersFootersAndPageNumbersAreStripped()
    {
        var builder = new PdfDocumentBuilder();
        for (int page = 1; page <= 3; page++)
        {
            var content = new PdfContentStreamBuilder();
            Text(content, 9, 72, 760, "The Running Head");
            Text(content, 11, 72, 700, $"Body text on page {page} is unique here.");
            Text(content, 9, 300, 30, page.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.AddPage(612, 792, content);
        }
        PdfDocument document = PdfDocument.Open(builder.Build());

        Package package = ReadPackage(PdfEpubExport.Write(document));

        string all = string.Join("\n", package.Blocks.Select(block => block.Text));
        Assert.DoesNotContain("Running Head", all);
        Assert.Equal(
        [
            "Body text on page 1 is unique here.",
            "Body text on page 2 is unique here.",
            "Body text on page 3 is unique here."
        ], package.Blocks.Select(block => block.Text));
    }

    [Fact]
    public void SectionRunningHeadsThatRepeatOnlyWithinTheirSectionAreStripped()
    {
        var builder = new PdfDocumentBuilder();
        for (int page = 1; page <= 8; page++)
        {
            var content = new PdfContentStreamBuilder();
            Text(content, 7, 72, 770, page <= 4 ? "WORKSPACE" : "EDITING");
            Text(content, 10, 72, 700, $"Section page {page} body text.");
            builder.AddPage(612, 792, content);
        }
        PdfDocument document = PdfDocument.Open(builder.Build());

        Package package = ReadPackage(PdfEpubExport.Write(document));

        Assert.All(package.Blocks, block => Assert.StartsWith("Section page", block.Text));
    }

    [Fact]
    public void SlightlyLargerLeadInSentencesStayParagraphs()
    {
        var content = new PdfContentStreamBuilder();
        Text(content, 21, 72, 720, "What Is New");
        Text(content, 11.6, 72, 690, "This lead-in sentence is set a little larger than the body.");
        Text(content, 10, 72, 660, "Body text carries most of the characters on this page so it sets");
        Text(content, 10, 72, 648, "the body size that headings are measured against in the export.");
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(612, 792, content).Build());

        Package package = ReadPackage(PdfEpubExport.Write(document));

        Assert.Equal(["h1", "p", "p"], package.Blocks.Select(block => block.Name));
    }

    [Fact]
    public void ImagesLandBetweenTheParagraphsAroundThem()
    {
        byte[] pixels = new byte[4 * 4 * 3];
        Array.Fill(pixels, (byte)200);
        var content = new PdfContentStreamBuilder();
        Text(content, 11, 72, 700, "Text above the picture.");
        content.DrawImage(PdfImage.FromRgb(4, 4, pixels), 72, 500, 200, 150);
        Text(content, 11, 72, 450, "Text below the picture.");
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(612, 792, content).Build());

        Package package = ReadPackage(PdfEpubExport.Write(document));

        Assert.Equal(["p:Text above the picture.", "img:", "p:Text below the picture."],
            package.Blocks.Select(block => $"{block.Name}:{block.Text}"));
        Assert.Contains(package.Manifest, item => item.MediaType == "image/png");
    }

    [Fact]
    public void TaggedPdfsFollowTheStructureTreeAndDropArtifacts()
    {
        // Content order puts the body first and a running header in an artifact;
        // the structure tree says the heading comes first.
        string content =
            "/Artifact BMC BT /F1 9 Tf 72 760 Td (Running Header) Tj ET EMC " +
            "/P << /MCID 1 >> BDC BT /F1 12 Tf 72 400 Td (Tagged body paragraph.) Tj ET EMC " +
            "/Title << /MCID 0 >> BDC BT /F1 12 Tf 72 300 Td (Tagged Heading) Tj ET EMC";
        PdfDocument document = PdfDocument.Open(CreatePdf(
        [
            "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 6 0 R /MarkInfo << /Marked true >> /Lang (en-US) >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R /StructParents 0 >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            Stream(content),
            "<< /Type /StructTreeRoot /K [7 0 R] /RoleMap << /Title /H1 >> >>",
            "<< /Type /StructElem /S /Document /P 6 0 R /K [8 0 R 9 0 R] >>",
            "<< /Type /StructElem /S /Title /P 7 0 R /Pg 3 0 R /K 0 >>",
            "<< /Type /StructElem /S /P /P 7 0 R /Pg 3 0 R /K 1 >>"
        ]));

        Package package = ReadPackage(PdfEpubExport.Write(document));

        Assert.Equal(["h1:Tagged Heading", "p:Tagged body paragraph."],
            package.Blocks.Select(block => $"{block.Name}:{block.Text}"));
        Assert.Equal("en-US", package.Opf.Descendants(Dc + "language").Single().Value);
    }

    [Fact]
    public void StructuredExportRoutesEpubAndReportsNoUnsupportedFormat()
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(612, 792, Text(new PdfContentStreamBuilder(), 11, 72, 700, "Hello book."))
            .Build());

        byte[] epub = PdfStructuredExport.Export(document, PdfStructuredExportFormat.Epub);
        PdfStructuredExportReport report = PdfStructuredExport.InspectLosses(document, PdfStructuredExportFormat.Epub);

        Assert.Contains(ReadPackage(epub).Blocks, block => block.Text == "Hello book.");
        Assert.True(report.IsLossless);
        Assert.Equal("book.epub", PdfStructuredExportBatchRunner.OutputName("book.pdf", PdfStructuredExportFormat.Epub));
    }

    private static PdfContentStreamBuilder Text(PdfContentStreamBuilder builder, double size,
        double x, double y, string text, bool bold = false) =>
        builder.BeginText().SetFont(bold ? PdfStandardFont.HelveticaBold : PdfStandardFont.Helvetica, size)
            .MoveText(x, y).ShowLatin1Text(text).EndText();

    private sealed record Block(string Name, string Text);
    private sealed record ManifestItem(string Href, string MediaType);
    private sealed record Package(XDocument Opf, XDocument Nav, IReadOnlyList<ManifestItem> Manifest,
        IReadOnlyList<Block> Blocks);

    // Checks the parts of the EPUB 3 package that readers and epubcheck reject when missing,
    // then returns the spine's block elements in reading order.
    private static Package ReadPackage(byte[] epub)
    {
        // The mimetype entry is first and stored, so its bytes sit at a fixed offset.
        Assert.Equal("mimetypeapplication/epub+zip", Encoding.ASCII.GetString(epub, 30, 28));
        Assert.Equal(0, BitConverter.ToUInt16(epub, 8));
        using var archive = new ZipArchive(new MemoryStream(epub), ZipArchiveMode.Read);
        Assert.Equal("mimetype", archive.Entries[0].FullName);
        XDocument container = XDocument.Load(archive.GetEntry("META-INF/container.xml")!.Open());
        XNamespace containerNs = "urn:oasis:names:tc:opendocument:xmlns:container";
        string opfPath = container.Descendants(containerNs + "rootfile").Single().Attribute("full-path")!.Value;
        XDocument opf = XDocument.Load(archive.GetEntry(opfPath)!.Open());
        string baseDir = opfPath.Contains('/') ? opfPath[..(opfPath.LastIndexOf('/') + 1)] : "";
        XElement metadata = opf.Root!.Element(Opf + "metadata")!;
        string uid = opf.Root.Attribute("unique-identifier")!.Value;
        Assert.Contains(metadata.Elements(Dc + "identifier"), id => id.Attribute("id")?.Value == uid);
        Assert.False(string.IsNullOrWhiteSpace(metadata.Element(Dc + "title")?.Value));
        Assert.False(string.IsNullOrWhiteSpace(metadata.Element(Dc + "language")?.Value));
        string modified = metadata.Elements(Opf + "meta")
            .Single(meta => meta.Attribute("property")?.Value == "dcterms:modified").Value;
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$", modified);

        var manifest = opf.Root.Element(Opf + "manifest")!.Elements(Opf + "item").ToList();
        var items = new List<ManifestItem>();
        XDocument? nav = null;
        foreach (XElement item in manifest)
        {
            string href = baseDir + item.Attribute("href")!.Value;
            string mediaType = item.Attribute("media-type")!.Value;
            ZipArchiveEntry entry = archive.GetEntry(href) ?? throw new Xunit.Sdk.XunitException($"Missing {href}");
            items.Add(new ManifestItem(href, mediaType));
            if (mediaType == "application/xhtml+xml")
            {
                XDocument page = XDocument.Load(entry.Open());
                Assert.Equal(Xhtml + "html", page.Root!.Name);
                if ((item.Attribute("properties")?.Value ?? "").Split(' ').Contains("nav")) nav = page;
            }
            else if (mediaType == "image/png")
            {
                using var image = new MemoryStream();
                entry.Open().CopyTo(image);
                Assert.Equal(new byte[] { 137, 80, 78, 71 }, image.ToArray()[..4]);
            }
        }
        Assert.NotNull(nav);
        Assert.Contains(nav!.Descendants(Xhtml + "nav"), element => element.Attribute(Ops + "type")?.Value == "toc");

        var ids = manifest.ToDictionary(item => item.Attribute("id")!.Value,
            item => baseDir + item.Attribute("href")!.Value);
        var blocks = new List<Block>();
        foreach (XElement itemref in opf.Root.Element(Opf + "spine")!.Elements(Opf + "itemref"))
        {
            string href = ids[itemref.Attribute("idref")!.Value];
            XDocument page = XDocument.Load(archive.GetEntry(href)!.Open());
            if (page == nav || page.Descendants(Xhtml + "nav").Any()) continue;
            foreach (XElement element in page.Root!.Element(Xhtml + "body")!.Descendants())
            {
                string name = element.Name.LocalName;
                if (name is "p" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6")
                    blocks.Add(new Block(name, element.Value));
                else if (name == "img")
                {
                    string src = element.Attribute("src")!.Value;
                    string resolved = new Uri(new Uri("epub:/" + href), src).AbsolutePath.TrimStart('/');
                    Assert.NotNull(archive.GetEntry(resolved));
                    blocks.Add(new Block("img", ""));
                }
            }
        }
        return new Package(opf, nav, items, blocks);
    }

    private static string Stream(string content) =>
        $"<< /Length {Encoding.Latin1.GetByteCount(content)} >>\nstream\n{content}\nendstream";

    private static byte[] CreatePdf(IReadOnlyList<string> objects)
    {
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int index = 0; index < objects.Count; index++)
        {
            offsets.Add(Encoding.Latin1.GetByteCount(pdf.ToString()));
            pdf.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        int xref = Encoding.Latin1.GetByteCount(pdf.ToString());
        pdf.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) pdf.Append($"{offset:0000000000} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(pdf.ToString());
    }
}

internal static class BuilderExtensions
{
    public static T Let<T>(this T value, Action<T> action) { action(value); return value; }
}
