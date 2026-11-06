using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Formats.Epub;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Tests.Fonts;
using Xunit;
using Xunit.Abstractions;

namespace KillerPdf.Engine.Tests.Formats.Epub;

public sealed class EpubFontWeightTests
{
    public EpubFontWeightTests(ITestOutputHelper output) => output.WriteLine("Loaded engine SHA256: " +
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(EpubDocument).Assembly.Location))));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WeightSelectsRealFaceMetricsAndInkWithInheritedAndNormalSpans(bool weighted)
    {
        byte[] source = Book(weighted), original = source.ToArray();
        var regular = Face(false); var bold = Face(true);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var book = EpubDocument.Open(source, deadline.Token);
        Assert.Equal("AAA", book.ReadText(0, deadline.Token));
        var options = new EpubLayoutOptions(40, 26, regular)
        {
            FontSize = 10, Margin = 2, FontFamilyResolver = _ => regular,
            FontFaceResolver = request => new(request.Weight == 700 ? bold : regular, request.Weight, EpubFontMatch.Exact)
        };
        var layout = book.CreateLayout(options, deadline.Token);
        var page = Assert.Single(layout.Pages);
        Assert.Equal("AAA", page.Text);
        Assert.Equal(new[] { 0, 1, 2 }, page.TextPlacements.Select(item => item.Start));
        Assert.Equal(weighted ? new double[] { 12, 6, 12 } : [6, 6, 6], page.TextPlacements.Select(item => item.Width));
        Assert.Equal(weighted ? new double[] { 2, 14, 20 } : [2, 8, 14], page.TextPlacements.Select(item => item.Left));
        Assert.All(page.TextPlacements, item => { Assert.Equal(2, item.Top); Assert.Equal(10, item.Height); });
        var frame = layout.RenderPage(0, cancellationToken: deadline.Token);
        Assert.Equal(weighted ? new float[] { 0, 0, 0, 1 } : [1, 1, 1, 1], frame.Pixels.Slice((9 * 40 + 32) * 4, 4).ToArray());
        Assert.Equal(original, source);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConversionEmbedsTheSelectedWeightWithIndependentTextGeometryAndInk(bool weighted)
    {
        byte[] source = Book(weighted), original = source.ToArray();
        var regular = Face(false); var bold = Face(true);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var book = EpubDocument.Open(source, deadline.Token);
        var options = new EpubPdfConversionOptions(regular)
        {
            PageWidthPoints = 30, PageHeightPoints = 19.5, MarginPoints = 1.5, FontSizePoints = 7.5,
            FontFamilyResolver = _ => regular,
            FontFaceResolver = request => new(request.Weight == 700 ? bold : regular, request.Weight, EpubFontMatch.Exact)
        };
        var pdf = PdfDocument.Open(book.ConvertToPdf(options, deadline.Token));
        var reader = new PdfPageContentReader(pdf);
        Assert.Equal(1, reader.PageCount);
        var letters = reader.Read(0).Letters;
        Assert.Equal(new[] { "A", "A", "A" }, letters.Select(item => item.Value));
        Assert.Equal(weighted ? new double[] { 1.5, 10.5, 15 } : [1.5, 6, 10.5], letters.Select(item => item.StartBaseLine.X));
        Assert.Equal(weighted ? new double[] { 9, 4.5, 9 } : [4.5, 4.5, 4.5], letters.Select(item => item.EndBaseLine.X - item.StartBaseLine.X));
        Assert.All(letters, item => Assert.Equal(12, item.StartBaseLine.Y));
        Assert.EndsWith(weighted ? "KillerBold" : "KillerTest", letters[0].FontName);
        Assert.EndsWith("KillerTest", letters[1].FontName);
        Assert.EndsWith(weighted ? "KillerBold" : "KillerTest", letters[2].FontName);
        var frame = new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(40, 26), deadline.Token);
        Assert.Empty(frame.Diagnostics);
        Assert.Equal(weighted ? new byte[] { 0, 0, 0, 255 } : [255, 255, 255, 255], frame.Pixels.Slice((9 * 40 + 32) * 4, 4).ToArray());
        Assert.Equal(original, source);
    }

    [Fact]
    public void StylesheetSpecificityAndInlineResetPreserveNumericWeightMetrics()
    {
        var regular = Face(false); var bold = Face(true);
        byte[] source = Book(false,
            "<p class='heavy'>A<span style='font-weight:normal'>A</span><span style='font-weight:625.5'>A</span></p>",
            "p.heavy{font-weight:700} p{font-weight:400}");
        var layout = EpubDocument.Open(source).CreateLayout(new EpubLayoutOptions(40, 26, regular)
        {
            FontSize = 10, Margin = 2,
            FontFaceResolver = request => request.Weight switch
            {
                400 => new(regular, 400, EpubFontMatch.Exact),
                700 => new(bold, 700, EpubFontMatch.Exact),
                625.5 => new(bold, 700, EpubFontMatch.Nearest),
                _ => throw new InvalidOperationException("Unexpected computed weight.")
            }
        });
        Assert.Equal(new double[] { 12, 6, 12 }, Assert.Single(layout.Pages).TextPlacements.Select(item => item.Width));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnresolvedBoldCannotSilentlyUseALegacyRegularFace(bool typedResolver)
    {
        var regular = Face(false);
        var options = new EpubLayoutOptions(40, 26, regular)
        {
            FontSize = 10, Margin = 2, FontFamilyResolver = _ => regular,
            FontFaceResolver = typedResolver ? _ => null : null
        };
        var error = Assert.Throws<NotSupportedException>(() => EpubDocument.Open(Book(true)).CreateLayout(options));
        Assert.Contains("requested weight", error.Message);
    }

    [Theory]
    [InlineData(0, EpubFontMatch.Nearest)]
    [InlineData(1001, EpubFontMatch.Nearest)]
    [InlineData(400, EpubFontMatch.Exact)]
    [InlineData(700, EpubFontMatch.Nearest)]
    [InlineData(700, (EpubFontMatch)99)]
    public void InconsistentHostFaceMetadataIsRejected(double actualWeight, EpubFontMatch match)
    {
        var regular = Face(false);
        var options = new EpubLayoutOptions(40, 26, regular)
        {
            FontSize = 10, Margin = 2,
            FontFaceResolver = _ => new(regular, actualWeight, match)
        };
        Assert.Throws<InvalidDataException>(() => EpubDocument.Open(Book(true)).CreateLayout(options));
    }

    [Fact]
    public void ExplicitDefaultVariantAndRealNearestFaceRenderWithoutADeclaredFamily()
    {
        var regular = Face(false); var bold = Face(true);
        var layout = EpubDocument.Open(Book(false, "<p style='font-weight:650'>AAA</p>"))
            .CreateLayout(new EpubLayoutOptions(40, 26, regular)
            {
                FontSize = 10, Margin = 2,
                FontFaceResolver = request => request.Family is null && ReferenceEquals(request.DefaultFont, regular)
                    ? new(bold, 700, EpubFontMatch.Nearest) : null
            });
        Assert.Equal(new double[] { 12, 12, 12 }, Assert.Single(layout.Pages).TextPlacements.Select(item => item.Width));
    }

    [Fact]
    public void CancellationFromAHostResolverStopsLayoutBeforePublication()
    {
        var regular = Face(false);
        using var cancellation = new CancellationTokenSource();
        var options = new EpubLayoutOptions(40, 26, regular)
        {
            FontSize = 10, Margin = 2,
            FontFaceResolver = _ =>
            {
                cancellation.Cancel();
                return new(regular, 700, EpubFontMatch.Exact);
            }
        };
        Assert.ThrowsAny<OperationCanceledException>(() => EpubDocument.Open(Book(true)).CreateLayout(options, cancellation.Token));
    }

    private static TrueTypeFont Face(bool bold)
    {
        byte[] bytes = TrueTypeFontTests.BuildTestFont(false, includeOutlines: true);
        if (!bold) return TrueTypeFont.Load(bytes);
        TrueTypeFontTests.RenameFace(bytes, "KillerBold");
        int tables = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(4, 2));
        for (int entry = 12; entry < 12 + tables * 16; entry += 16)
        {
            string tag = Encoding.ASCII.GetString(bytes, entry, 4);
            int offset = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(entry + 8, 4));
            if (tag == "hmtx") BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(offset + 4, 2), 1200);
            if (tag == "head") BinaryPrimitives.WriteInt16BigEndian(bytes.AsSpan(offset + 40, 2), 1500);
            if (tag == "glyf")
            {
                BinaryPrimitives.WriteInt16BigEndian(bytes.AsSpan(offset + 6, 2), 1500);
                BinaryPrimitives.WriteInt16BigEndian(bytes.AsSpan(offset + 17, 2), 1500);
                BinaryPrimitives.WriteInt16BigEndian(bytes.AsSpan(offset + 19, 2), -750);
            }
        }
        return TrueTypeFont.Load(bytes);
    }

    private static byte[] Book(bool weighted, string? body = null, string? css = null)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            Add("mimetype", "application/epub+zip");
            Add("META-INF/container.xml", "<container xmlns='urn:oasis:names:tc:opendocument:xmlns:container' version='1.0'><rootfiles><rootfile full-path='package.opf' media-type='application/oebps-package+xml'/></rootfiles></container>");
            Add("package.opf", "<package xmlns='http://www.idpf.org/2007/opf' version='3.0' unique-identifier='id'><metadata xmlns:dc='http://purl.org/dc/elements/1.1/'><dc:identifier id='id'>weight</dc:identifier><dc:title>Weight</dc:title><dc:language>en</dc:language></metadata><manifest><item id='doc' href='doc.xhtml' media-type='application/xhtml+xml'/>" + (css is null ? "" : "<item id='css' href='style.css' media-type='text/css'/>") + "</manifest><spine><itemref idref='doc'/></spine></package>");
            if (css is not null) Add("style.css", css);
            string head = css is null ? "" : "<head><link rel='stylesheet' href='style.css'/></head>";
            Add("doc.xhtml", "<html xmlns='http://www.w3.org/1999/xhtml'>" + head + "<body>" + (body ?? (weighted
                ? "<p style='font-family:Fixture;font-weight:bold'>A<span style='font-weight:normal'>A</span><span style='font-weight:700'>A</span></p>"
                : "<p style='font-family:Fixture'>AAA</p>")) + "</body></html>");
            void Add(string name, string value)
            {
                using var stream = zip.CreateEntry(name, CompressionLevel.NoCompression).Open();
                stream.Write(Encoding.UTF8.GetBytes(value));
            }
        }
        return output.ToArray();
    }
}
