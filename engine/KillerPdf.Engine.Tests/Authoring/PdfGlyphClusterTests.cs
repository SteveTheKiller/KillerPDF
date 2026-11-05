using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Filters;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Tests.Fonts;
using Xunit;

namespace KillerPdf.Engine.Tests.Authoring;

public sealed class PdfGlyphClusterTests
{
    [Fact]
    public void FullCharacterCodeSpaceKeepsTheLastNonzeroSlotAndReusesExistingClusterCodes()
    {
        TrueTypeFont font = FullMappedFont();
        string text = "\0" + string.Concat(Enumerable.Range(0x10000, 65534).Select(char.ConvertFromUtf32));
        var content = new PdfContentStreamBuilder().BeginText().SetFont(font, 10)
            .ShowUnicodeText(text).EndText();

        // Natural mappings occupy CID 0 through FFFE. FFFF is still available.
        content.DrawGlyphCluster(font, 10, "a", [new(1, 0, 0)]);
        byte[] full = content.Build();
        Assert.Contains("<FFFF> Tj", Encoding.ASCII.GetString(full));
        Assert.Throws<InvalidOperationException>(() => content.DrawGlyphCluster(font, 10, "b", [new(2, 1, 0)]));
        Assert.Equal(full, content.Build());
        content.DrawGlyphCluster(font, 10, "c", [new(1, 2, 0)]);
        Assert.Equal(2, Encoding.ASCII.GetString(content.Build()).Split("<FFFF> Tj").Length - 1);
    }

    [Theory]
    [InlineData("null-font")]
    [InlineData("restricted-font")]
    [InlineData("null-text")]
    [InlineData("empty-text")]
    [InlineData("long-text")]
    [InlineData("high-surrogate")]
    [InlineData("low-surrogate")]
    [InlineData("zero-size")]
    [InlineData("negative-size")]
    [InlineData("nan-size")]
    [InlineData("infinite-size")]
    [InlineData("null-glyphs")]
    [InlineData("empty-glyphs")]
    [InlineData("many-glyphs")]
    [InlineData("glyph-zero")]
    [InlineData("foreign-glyph")]
    [InlineData("nan-x")]
    [InlineData("infinite-y")]
    public void InvalidClusterLeavesOutputResourcesMappingsAndFontSelectionUntouched(string invalid)
    {
        TrueTypeFont font = UnmappedFont();
        TrueTypeFont original = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        PdfContentStreamBuilder actual = Start();
        var glyphs = new PdfPositionedGlyph[] { new(1, 10, 10), new(2, 30, 10) };
        string text = "ab";
        double size = 20;
        switch (invalid)
        {
            case "null-font": font = null!; break;
            case "restricted-font": font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, embeddingFlags: 2)); break;
            case "null-text": text = null!; break;
            case "empty-text": text = ""; break;
            case "long-text": text = new string('a', 65537); break;
            case "high-surrogate": text = "a\uD800"; break;
            case "low-surrogate": text = "a\uDC00"; break;
            case "zero-size": size = 0; break;
            case "negative-size": size = -1; break;
            case "nan-size": size = double.NaN; break;
            case "infinite-size": size = double.PositiveInfinity; break;
            case "null-glyphs": glyphs = null!; break;
            case "empty-glyphs": glyphs = []; break;
            case "many-glyphs": glyphs = Enumerable.Repeat(new PdfPositionedGlyph(1, 10, 10), 4097).ToArray(); break;
            case "glyph-zero": glyphs[1] = new(0, 30, 10); break;
            case "foreign-glyph": glyphs[1] = new(3, 30, 10); break;
            case "nan-x": glyphs[1] = new(2, double.NaN, 10); break;
            case "infinite-y": glyphs[1] = new(2, 30, double.NegativeInfinity); break;
        }
        Assert.ThrowsAny<Exception>(() => actual.DrawGlyphCluster(font, size, text, glyphs));
        Assert.Equal(Start().Build(), actual.Build());
        Assert.Equal(Finish(Start()), Finish(actual));

        PdfContentStreamBuilder Start() => new PdfContentStreamBuilder().BeginText()
            .SetFont(original, 10).ShowUnicodeText("A").EndText();
        byte[] Finish(PdfContentStreamBuilder builder) => new PdfDocumentBuilder().AddPage(100, 100,
            builder.BeginText().ShowUnicodeText("A").EndText()).Build();
    }

    [Fact]
    public void ClusterInsideTextIsRejectedWithoutChangingTheOpenTextObject()
    {
        TrueTypeFont font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false));
        var actual = new PdfContentStreamBuilder().BeginText().SetFont(font, 10);
        Assert.Throws<InvalidOperationException>(() => actual.DrawGlyphCluster(font, 10, "ab", [new(1, 10, 10)]));
        actual.ShowUnicodeText("A").EndText();
        var expected = new PdfContentStreamBuilder().BeginText().SetFont(font, 10).ShowUnicodeText("A").EndText();
        Assert.Equal(new PdfDocumentBuilder().AddPage(100, 100, expected).Build(),
            new PdfDocumentBuilder().AddPage(100, 100, actual).Build());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitGlyphCodesCoexistWithNaturalAndSubstituteMappings(bool clusterFirst)
    {
        TrueTypeFont font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var content = new PdfContentStreamBuilder();
        if (clusterFirst) Cluster();
        content.BeginText().SetFont(font, 10).ShowUnicodeText("A").ShowUnicodeText("A", "alias").EndText();
        if (!clusterFirst) Cluster();
        content.BeginText().ShowUnicodeText("A").EndText();
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(100, 100, content).Build());
        Assert.Equal(clusterFirst ? "xyAaliasA" : "AaliasxyA",
            string.Concat(new PdfPageContentReader(document).Read(0).Letters.Select(letter => letter.Value)));
        PdfDictionary embedded = ResolveDictionary(document, Fonts(document)[new PdfName("F1"u8)]);
        PdfExtractionFont reopened = PdfFontResourceReader.Read(document, embedded);
        Assert.NotNull(reopened.GetGlyphOutline(1));
        Assert.NotNull(reopened.GetGlyphOutline(2));
        Assert.NotNull(reopened.GetGlyphOutline(3));
        string cmap = Encoding.ASCII.GetString(PdfStreamDecoder.Decode(Assert.IsType<PdfStream>(
            document.Resolve(Assert.IsType<PdfIndirectReference>(embedded[new PdfName("ToUnicode"u8)])))));
        Assert.DoesNotContain("> <>", cmap);
        Assert.Contains("<0041>", cmap);
        Assert.Contains("<0061006C006900610073>", cmap);

        void Cluster() => content.DrawGlyphCluster(font, 10, "xy", [new(1, 10, 10), new(1, 30, 20)]);
    }

    [Fact]
    public void NonCmapClustersPreserveLogicalTextPositionedInkAndSurroundingTextState()
    {
        TrueTypeFont original = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        TrueTypeFont shaped = UnmappedFont();
        Assert.Equal((ushort)0, shaped.GetGlyphId('a'));
        Assert.Equal((ushort)0, shaped.GetGlyphId('b'));
        var content = new PdfContentStreamBuilder().SetFillRgb(1, 0, 0)
            .BeginText().SetFont(original, 10).SetHorizontalTextScale(50)
            .SetTextRise(7).SetCharacterSpacing(3).SetWordSpacing(9)
            .SetTextRenderingMode(PdfTextRenderingMode.Invisible)
            .ShowUnicodeText("A").EndText();
        content.SaveState().Transform(2, 0, 0, 1, 5, 3);
        Draw(content, shaped, 20, "ab", [(1, 30, 30), (2, 60, 40)]);
        Draw(content, shaped, 20, "c", [(2, 5, 30)]);
        content.RestoreState().BeginText().SetTextMatrix(1, 0, 0, 1, 20, 10)
            .ShowUnicodeText("AA").EndText();

        byte[] pdf = new PdfDocumentBuilder().AddPage(200, 100, content).Build();
        string? artifact = Environment.GetEnvironmentVariable("KILLERPDF_GLYPH_CLUSTER_TEST_PDF");
        if (!string.IsNullOrEmpty(artifact))
        {
            using (var output = new FileStream(artifact, FileMode.CreateNew, FileAccess.Write))
                output.Write(pdf);
            Assert.Equal(System.Security.Cryptography.SHA256.HashData(pdf),
                System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(artifact)));
        }
        PdfDocument document = PdfDocument.Open(pdf);
        var page = new PdfPageContentReader(document).Read(0);
        Assert.Equal("AabcAA", string.Concat(page.Letters.Select(letter => letter.Value)));
        var following = page.Letters.TakeLast(2).ToArray();
        Assert.Equal(20, following[0].StartBaseLine.X);
        Assert.Equal(24.5, following[1].StartBaseLine.X);
        Assert.All(following, letter => Assert.Equal(17, letter.StartBaseLine.Y));
        Assert.All(following, letter => Assert.Equal(10, letter.FontSize));

        PdfRenderedPage image = new PdfPageRenderer(document).Render(0, new PdfRenderOptions(200, 100));
        Assert.Empty(image.Diagnostics);
        AssertPixel(image, 89, 40, red: true);
        AssertPixel(image, 145, 50, red: true);
        AssertPixel(image, 35, 40, red: true);
        AssertPixel(image, 65, 40, red: false);
        AssertPixel(image, 145, 35, red: false);
        AssertPixel(image, 89, 55, red: false);
        AssertPixel(image, 22, 19, red: false);

        PdfDictionary fonts = Fonts(document);
        Assert.Equal(2, fonts.Count);
        PdfDictionary embedded = ResolveDictionary(document, fonts[new PdfName("F2"u8)]);
        PdfExtractionFont reopened = PdfFontResourceReader.Read(document, embedded);
        Assert.Equal(600, reopened.GetWidth(1));
        Assert.Equal(600, reopened.GetWidth(2));
        Assert.Equal(100, Assert.Single(reopened.GetGlyphOutline(1)!.Contours).Points[0].X);
        Assert.Equal(0, Assert.Single(reopened.GetGlyphOutline(2)!.Contours).Points[0].X);
        string cmap = Encoding.ASCII.GetString(PdfStreamDecoder.Decode(Assert.IsType<PdfStream>(
            document.Resolve(Assert.IsType<PdfIndirectReference>(embedded[new PdfName("ToUnicode"u8)])))));
        Assert.DoesNotContain("> <>", cmap);
        Assert.DoesNotContain("beginbfchar", cmap);
        Assert.Contains("+KillerTest", reopened.FontName);
    }

    private static void AssertPixel(PdfRenderedPage image, int x, int y, bool red)
    {
        int offset = ((image.Height - 1 - y) * image.Width + x) * 4;
        byte[] pixel = image.Pixels.Slice(offset, 4).ToArray();
        Assert.Equal(red ? new byte[] { 0, 0, 255, 255 } : new byte[] { 255, 255, 255, 255 }, pixel);
    }

    private static TrueTypeFont FullMappedFont()
    {
        byte[] source = TrueTypeFontTests.BuildTestFont(false);
        int count = BinaryPrimitives.ReadUInt16BigEndian(source.AsSpan(4));
        var tables = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        for (int record = 12; record < 12 + count * 16; record += 16)
        {
            string tag = Encoding.ASCII.GetString(source, record, 4);
            int offset = (int)BinaryPrimitives.ReadUInt32BigEndian(source.AsSpan(record + 8));
            int length = (int)BinaryPrimitives.ReadUInt32BigEndian(source.AsSpan(record + 12));
            tables[tag] = source.AsSpan(offset, length).ToArray();
        }
        BinaryPrimitives.WriteUInt16BigEndian(tables["maxp"].AsSpan(4), ushort.MaxValue);
        BinaryPrimitives.WriteUInt16BigEndian(tables["hhea"].AsSpan(34), ushort.MaxValue);
        tables["hmtx"] = new byte[ushort.MaxValue * 4];
        for (int glyph = 0; glyph < ushort.MaxValue; glyph++)
            BinaryPrimitives.WriteUInt16BigEndian(tables["hmtx"].AsSpan(glyph * 4), 600);
        byte[] cmap = new byte[40];
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(4), 3);
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(6), 10);
        BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(8), 12);
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(12), 12);
        BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(16), 28);
        BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(24), 1);
        BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(28), 0x10000);
        BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(32), 0x1FFFD);
        BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(36), 1);
        tables["cmap"] = cmap;
        int directory = 12 + count * 16;
        byte[] bytes = new byte[directory + tables.Values.Sum(table => (table.Length + 3) & ~3)];
        source.AsSpan(0, 12).CopyTo(bytes);
        int position = directory, recordOffset = 12;
        foreach (var (tag, data) in tables.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            Encoding.ASCII.GetBytes(tag).CopyTo(bytes, recordOffset);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(recordOffset + 8), (uint)position);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(recordOffset + 12), (uint)data.Length);
            data.CopyTo(bytes, position);
            position += (data.Length + 3) & ~3;
            recordOffset += 16;
        }
        return TrueTypeFont.Load(bytes);
    }

    private static TrueTypeFont UnmappedFont()
    {
        byte[] bytes = TrueTypeFontTests.BuildTestFont(false, includeOutlines: true, includeCompound: true);
        int count = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(4));
        for (int record = 12; record < 12 + count * 16; record += 16)
        {
            if (Encoding.ASCII.GetString(bytes, record, 4) != "cmap") continue;
            int offset = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(record + 8));
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(offset + 36), 0xFFBF);
        }
        TrueTypeFont font = TrueTypeFont.Load(bytes);
        Assert.Equal((ushort)0, font.GetGlyphId('A'));
        return font;
    }

    private static void Draw(PdfContentStreamBuilder content, TrueTypeFont font, double size,
        string text, (ushort Glyph, double X, double Y)[] positions)
    {
        MethodInfo? method = typeof(PdfContentStreamBuilder).GetMethod("DrawGlyphCluster");
        if (method is null)
        {
            content.BeginText().SetFont(font, size).ShowUnicodeText(text).EndText();
            return;
        }
        Type glyphType = typeof(PdfContentStreamBuilder).Assembly.GetType("KillerPdf.Engine.Authoring.PdfPositionedGlyph")!;
        Array glyphs = Array.CreateInstance(glyphType, positions.Length);
        for (int index = 0; index < positions.Length; index++)
            glyphs.SetValue(Activator.CreateInstance(glyphType, positions[index].Glyph,
                positions[index].X, positions[index].Y), index);
        try { method.Invoke(content, [font, size, text, glyphs]); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(error.InnerException).Throw();
        }
    }

    private static PdfDictionary Fonts(PdfDocument document)
    {
        PdfDictionary catalog = ResolveDictionary(document, document.Trailer[new PdfName("Root"u8)]);
        PdfDictionary pages = ResolveDictionary(document, catalog[new PdfName("Pages"u8)]);
        PdfDictionary page = ResolveDictionary(document, Assert.IsType<PdfArray>(pages[new PdfName("Kids"u8)])[0]);
        return ResolveDictionary(document, ResolveDictionary(document, page[new PdfName("Resources"u8)])[new PdfName("Font"u8)]);
    }

    private static PdfDictionary ResolveDictionary(PdfDocument document, PdfObject value) =>
        Assert.IsType<PdfDictionary>(value is PdfIndirectReference reference ? document.Resolve(reference) : value);
}
