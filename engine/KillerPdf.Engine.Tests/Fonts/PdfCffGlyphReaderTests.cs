using KillerPdf.Engine.Fonts;
using Xunit;

namespace KillerPdf.Engine.Tests.Fonts;

public sealed class PdfCffGlyphReaderTests
{
    [Fact]
    public void ReturnsEmptyOutlineForValidBlankGlyph()
    {
        var font = Assert.IsType<PdfCffGlyphReader>(PdfCffGlyphReader.TryRead(Build([14])));

        Assert.Empty(Assert.IsType<PdfGlyphOutline>(font.GetOutline(1)).Contours);
        Assert.Null(font.GetOutline(0));
    }

    [Fact]
    public async Task ConcurrentColdGlyphLookupsKeepOutlineAndBounds()
    {
        var program = new List<byte>([.. Numbers(0, 0), 21]);
        for (int segment = 0; segment < 20_000; segment++)
            program.AddRange([.. Numbers(1, 1), 5]);
        program.Add(14);
        var serial = Assert.IsType<PdfCffGlyphReader>(PdfCffGlyphReader.TryRead(Build([.. program])));
        Assert.Equal(20_001, Assert.Single(Assert.IsType<PdfGlyphOutline>(serial.GetOutline(1)).Contours).Points.Count);
        Assert.Equal(new PdfGlyphBounds(0, 0, 20_000, 20_000), serial.GetBounds(1));
        var font = Assert.IsType<PdfCffGlyphReader>(PdfCffGlyphReader.TryRead(Build([.. program])));
        using var start = new Barrier(8);
        Task<(PdfGlyphOutline? Outline, PdfGlyphBounds? Bounds)>[] requests = [.. Enumerable.Range(0, 8)
            .Select(_ => Task.Factory.StartNew(() =>
            {
                if (!start.SignalAndWait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("Concurrent glyph readers did not start.");
                return (font.GetOutline(1), font.GetBounds(1));
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default))];

        foreach (var result in await Task.WhenAll(requests))
        {
            PdfGlyphContour contour = Assert.Single(Assert.IsType<PdfGlyphOutline>(result.Outline).Contours);
            Assert.Equal(20_001, contour.Points.Count);
            Assert.Equal(new PdfGlyphPoint(20_000, 20_000, true), contour.Points[^1]);
            Assert.Equal(new PdfGlyphBounds(0, 0, 20_000, 20_000), result.Bounds);
        }
    }

    [Fact]
    public void ReadsNamedLineOutlineAndDoesNotIncludeUnusedMove()
    {
        byte[] program = [.. Numbers(10, 20), 21, .. Numbers(100, 0, 0, 200, -100, 0), 5,
            .. Numbers(1000, 1000), 21, 14];
        var font = Assert.IsType<PdfCffGlyphReader>(PdfCffGlyphReader.TryRead(Build(program)));
        Assert.Equal(1, font.FindGlyph("A"));
        Assert.Equal(1, font.FindUnicode('A'));
        Assert.Equal(-1, font.FindGlyph("missing"));
        Assert.Equal(new PdfGlyphBounds(10, 20, 110, 220), font.GetBounds(1));
        PdfGlyphContour contour = Assert.Single(Assert.IsType<PdfGlyphOutline>(
            font.GetOutline(1)).Contours);
        Assert.Equal([
            new PdfGlyphPoint(10, 20, true),
            new PdfGlyphPoint(110, 20, true),
            new PdfGlyphPoint(110, 220, true),
            new PdfGlyphPoint(10, 220, true)], contour.Points);
        Assert.Null(font.GetBounds(0));
        Assert.Null(font.GetOutline(0));
        Assert.Null(font.GetBounds(2));
    }

    [Fact]
    public void CubicBoundsUseCurveExtremaRatherThanControlPoints()
    {
        byte[] program = [.. Numbers(0, 0), 21, .. Numbers(0, 100, 100, 0, 0, -100), 8, 14];
        var font = Assert.IsType<PdfCffGlyphReader>(PdfCffGlyphReader.TryRead(Build(program)));
        Assert.Equal(new PdfGlyphBounds(0, 0, 100, 75), font.GetBounds(1));
        PdfGlyphContour contour = Assert.Single(Assert.IsType<PdfGlyphOutline>(
            font.GetOutline(1)).Contours);
        Assert.Equal([
            new PdfGlyphPoint(0, 0, true),
            new PdfGlyphPoint(0, 100, false, true),
            new PdfGlyphPoint(100, 100, false, true),
            new PdfGlyphPoint(100, 0, true)], contour.Points);
    }

    [Fact]
    public void LocalSubroutineReturnsToGlyphAndHintsDoNotBecomeOutlines()
    {
        byte[] program = [.. Numbers(500, 10, 20), 1, 19, 0x80, .. Numbers(10, 20), 21,
            .. Numbers(-107), 10, .. Numbers(0, 20), 5, 14];
        byte[] local = [.. Numbers(100, 0), 5, 11];
        var font = Assert.IsType<PdfCffGlyphReader>(PdfCffGlyphReader.TryRead(Build(program, local)));
        Assert.Equal(new PdfGlyphBounds(10, 20, 110, 40), font.GetBounds(1));
    }

    [Fact]
    public void SubroutineCycleIsBoundedAndDoesNotReturnInventedGeometry()
    {
        byte[] program = [.. Numbers(-107), 10, 14];
        byte[] local = [.. Numbers(-107), 10, 11];
        var font = Assert.IsType<PdfCffGlyphReader>(PdfCffGlyphReader.TryRead(Build(program, local)));
        Assert.Null(font.GetBounds(1));
    }

    [Fact]
    public void TruncatedIndexOffsetsAndUnfinishedCharstringAreRejected()
    {
        byte[] bytes = Build([.. Numbers(10, 20), 21]);
        Assert.Null(PdfCffGlyphReader.TryRead(bytes.AsMemory()[..^4]));
        var font = Assert.IsType<PdfCffGlyphReader>(PdfCffGlyphReader.TryRead(bytes));
        Assert.Null(font.GetBounds(1));
        bytes[2] = 255;
        Assert.Null(PdfCffGlyphReader.TryRead(bytes));
    }

    [Fact]
    public void ReadsCffTableFromOpenTypeContainer()
    {
        byte[] cff = Build([.. Numbers(10, 20), 21, .. Numbers(100, 0), 5, 14]);
        byte[] otf = [.. "OTTO"u8.ToArray(), 0, 1, 0, 0, 0, 0, 0, 0,
            .. "CFF "u8.ToArray(), 0, 0, 0, 0, 0, 0, 0, 28,
            (byte)(cff.Length >> 24), (byte)(cff.Length >> 16), (byte)(cff.Length >> 8), (byte)cff.Length,
            .. cff];
        var font = Assert.IsType<PdfCffGlyphReader>(PdfCffGlyphReader.TryRead(otf));
        Assert.Equal(new PdfGlyphBounds(10, 20, 110, 20), font.GetBounds(1));
    }

    [Fact]
    public void ReservedDictionaryBytesDoNotRejectTheFont()
    {
        var font = Assert.IsType<PdfCffGlyphReader>(PdfCffGlyphReader.TryRead(
            Build([14], privatePrefix: [255, 30, 31])));

        Assert.Empty(Assert.IsType<PdfGlyphOutline>(font.GetOutline(1)).Contours);
    }

    [Theory]
    [InlineData(new byte[] { 0xFF })]
    [InlineData(new byte[] { 0xC5, 0x7F })]
    [InlineData(new byte[] { 0xEA, 0xC1, 0xFF })]
    public void RealNumbersWithoutMantissaDigitsReadAsZero(byte[] nibbles)
    {
        var font = Assert.IsType<PdfCffGlyphReader>(PdfCffGlyphReader.TryRead(
            Build([14], privatePrefix: [30, .. nibbles, 12, 9])));

        Assert.Empty(Assert.IsType<PdfGlyphOutline>(font.GetOutline(1)).Contours);
    }

    [Theory]
    [InlineData(false, 1000)]
    [InlineData(true, 1)]
    public void CidCharsetIsNotAssumedToBeGlyphIdAndFdMatrixScalesBounds(bool explicitTopMatrix, int scale)
    {
        byte[] program = [.. Numbers(10, 20), 21, .. Numbers(100, 0, 0, 200), 5, 14];
        byte[] name = Index("CIDExample"u8.ToArray());
        byte[] matrix = explicitTopMatrix
            ? [30, 0x0a, 0x00, 0x1f, 139, 139, 30, 0x0a, 0x00, 0x1f, 139, 139, 12, 7] : [];
        int charsetOffset = 4 + name.Length + Index(new byte[31 + matrix.Length]).Length + 4;
        int fdSelectOffset = charsetOffset + 3;
        int fdArrayOffset = fdSelectOffset + 3;
        byte[] fdArray = Index([.. Numbers(2, 0, 0, 3, 0, 0), 12, 7]);
        int charstringsOffset = fdArrayOffset + fdArray.Length;
        byte[] top = [.. matrix, 139, 139, 139, 12, 30, .. Offset(charsetOffset), 15,
            .. Offset(charstringsOffset), 17, .. Offset(fdArrayOffset), 12, 36, .. Offset(fdSelectOffset), 12, 37];
        byte[] bytes = [1, 0, 4, 4, .. name, .. Index(top), 0, 0, 0, 0,
            0, 0, 42, 0, 0, 0, .. fdArray, .. Index([14], program)];
        var font = Assert.IsType<PdfCffGlyphReader>(PdfCffGlyphReader.TryRead(bytes));
        Assert.Equal(1, font.FindCid(42));
        Assert.Equal(-1, font.FindCid(1));
        Assert.Null(font.GetGlyphName(1));
        Assert.Equal(new PdfGlyphBounds(20 * scale, 60 * scale, 220 * scale, 660 * scale), font.GetBounds(1));
    }

    [Fact]
    public void ComposesSeacBaseAndAccentOutlines()
    {
        byte[] baseGlyph = [.. Numbers(0, 0), 21, .. Numbers(100, 0, 0, 200), 5, 14];
        byte[] accentGlyph = [.. Numbers(0, 0), 21, .. Numbers(20, 20), 5, 14];
        byte[] composite = [.. Numbers(10, 200, 65, 194), 14];
        var font = Assert.IsType<PdfCffGlyphReader>(PdfCffGlyphReader.TryRead(
            BuildNamed(("A", baseGlyph), ("acute", accentGlyph), ("Aacute", composite))));

        PdfGlyphOutline outline = Assert.IsType<PdfGlyphOutline>(font.GetOutline(3));
        Assert.Equal(2, outline.Contours.Count);
        Assert.Equal(new PdfGlyphPoint(0, 0, true), outline.Contours[0].Points[0]);
        Assert.Equal(new PdfGlyphPoint(10, 200, true), outline.Contours[1].Points[0]);
    }

    [Fact]
    public void FlexAndArithmeticOperatorsProduceOutlineBounds()
    {
        byte[] program = [.. Numbers(0, 0), 21, .. Numbers(5, 5), 12, 10,
            .. Numbers(20, 30, 40, 50, 60, 70), 12, 34, 14];
        var font = Assert.IsType<PdfCffGlyphReader>(PdfCffGlyphReader.TryRead(Build(program)));
        Assert.Equal(new PdfGlyphBounds(0, 0, 250, 30), font.GetBounds(1));
    }

    [Fact]
    public void RandomOperatorProducesDeterministicOutlineGeometry()
    {
        byte[] program = [.. Numbers(0, 0), 21, 12, 23, .. Numbers(100),
            12, 24, .. Numbers(0), 5, 14];
        var font = Assert.IsType<PdfCffGlyphReader>(PdfCffGlyphReader.TryRead(Build(program)));

        PdfGlyphBounds first = Assert.IsType<PdfGlyphBounds>(font.GetBounds(1));
        PdfGlyphBounds second = Assert.IsType<PdfGlyphBounds>(font.GetBounds(1));

        Assert.Equal(first, second);
        Assert.InRange(first.Right, 0.000001, 100);
        Assert.Equal(0, first.Top);
    }

    [Fact]
    public void RandomOperatorHonorsPrivateDictionarySeed()
    {
        byte[] program = [.. Numbers(0, 0), 21, 12, 23, .. Numbers(100),
            12, 24, .. Numbers(0), 5, 14];
        var first = Assert.IsType<PdfCffGlyphReader>(
            PdfCffGlyphReader.TryRead(Build(program, initialRandomSeed: 1)));
        var second = Assert.IsType<PdfCffGlyphReader>(
            PdfCffGlyphReader.TryRead(Build(program, initialRandomSeed: 2)));

        Assert.NotEqual(first.GetBounds(1), second.GetBounds(1));
    }

    [Theory]
    [InlineData(1, "exclamsmall")]
    [InlineData(2, "dollaroldstyle")]
    public void ReadsPredefinedExpertCharsets(int charset, string secondGlyph)
    {
        var font = Assert.IsType<PdfCffGlyphReader>(PdfCffGlyphReader.TryRead(
            BuildPredefined(charset, [14], [14])));

        Assert.Equal("space", font.GetGlyphName(1));
        Assert.Equal(secondGlyph, font.GetGlyphName(2));
        Assert.Equal(2, font.FindGlyph(secondGlyph));
    }

    [Theory]
    [InlineData(1, 165, "Ydieresissmall")]
    [InlineData(2, 86, "commainferior")]
    public void ReadsCompletePredefinedExpertCharset(
        int charset, int glyphCount, string lastGlyph)
    {
        byte[][] programs = [.. Enumerable.Range(0, glyphCount).Select(_ => new byte[] { 14 })];
        var font = Assert.IsType<PdfCffGlyphReader>(PdfCffGlyphReader.TryRead(
            BuildPredefined(charset, programs)));

        Assert.Equal(lastGlyph, font.GetGlyphName(glyphCount));
        Assert.Equal(glyphCount, font.FindGlyph(lastGlyph));
    }

    internal static byte[] Build(byte[] program, byte[]? subr = null,
        int? initialRandomSeed = null, byte[]? privatePrefix = null)
    {
        byte[] name = Index("Example"u8.ToArray());
        byte[] charstrings = Index([14], program);
        int charsetOffset = 4 + name.Length + Index(new byte[19]).Length + 2 + 2;
        int charstringsOffset = charsetOffset + 3;
        int privateOffset = charstringsOffset + charstrings.Length;
        byte[] seed = initialRandomSeed is null
            ? [] : [.. Numbers(initialRandomSeed.Value), 12, 19];
        if (privatePrefix is not null) seed = [.. privatePrefix, .. seed];
        int subrsOffset = seed.Length + 2;
        byte[] privateDictionary = [.. seed, .. Numbers(subrsOffset), 19];
        byte[] dict = [.. Offset(charsetOffset), 15, .. Offset(charstringsOffset), 17,
            .. Numbers(privateDictionary.Length), .. Offset(privateOffset), 18];
        return [1, 0, 4, 4, .. name, .. Index(dict), 0, 0, 0, 0, 0, 0, 34,
            .. charstrings, .. privateDictionary, .. (subr is null ? [0, 0] : Index(subr))];
    }
    internal static byte[] BuildNamed(params (string Name, byte[] Program)[] glyphs)
    {
        int[] sids = [.. glyphs.Select(glyph => glyph.Name switch
        {
            "A" => 34,
            "f" => 71,
            "i" => 74,
            "acute" => 125,
            "Aacute" => 171,
            _ => throw new ArgumentOutOfRangeException(nameof(glyphs))
        })];
        byte[] name = Index("Example"u8.ToArray());
        byte[][] programs = [[14], .. glyphs.Select(glyph => glyph.Program)];
        byte[] charstrings = Index(programs);
        int charsetOffset = 4 + name.Length + Index(new byte[19]).Length + 2 + 2;
        byte[] charset = [0, .. sids.SelectMany(sid => new byte[] { (byte)(sid >> 8), (byte)sid })];
        int charstringsOffset = charsetOffset + charset.Length;
        int privateOffset = charstringsOffset + charstrings.Length;
        byte[] dict = [.. Offset(charsetOffset), 15, .. Offset(charstringsOffset), 17,
            141, .. Offset(privateOffset), 18];
        return [1, 0, 4, 4, .. name, .. Index(dict), 0, 0, 0, 0, .. charset,
            .. charstrings, 141, 19, 0, 0];
    }
    // Builds a name-keyed CFF with a custom Encoding table, as TeX Type1C subsets carry.
    internal static byte[] BuildEncoded(string[] strings, byte[] encoding,
        params (int Sid, byte[] Program)[] glyphs)
    {
        byte[] name = Index("Example"u8.ToArray());
        byte[] stringIndex = strings.Length == 0 ? [0, 0]
            : Index([.. strings.Select(value => System.Text.Encoding.ASCII.GetBytes(value))]);
        byte[] charstrings = Index([[14], .. glyphs.Select(glyph => glyph.Program)]);
        byte[] charset = [0, .. glyphs.SelectMany(glyph => new byte[] { (byte)(glyph.Sid >> 8), (byte)glyph.Sid })];
        int charsetOffset = 4 + name.Length + Index(new byte[25]).Length + stringIndex.Length + 2;
        int encodingOffset = charsetOffset + charset.Length;
        int charstringsOffset = encodingOffset + encoding.Length;
        int privateOffset = charstringsOffset + charstrings.Length;
        byte[] dict = [.. Offset(charsetOffset), 15, .. Offset(encodingOffset), 16,
            .. Offset(charstringsOffset), 17, 141, .. Offset(privateOffset), 18];
        return [1, 0, 4, 4, .. name, .. Index(dict), .. stringIndex, 0, 0, .. charset,
            .. encoding, .. charstrings, 141, 19, 0, 0];
    }
    private static byte[] BuildPredefined(int charset, params byte[][] glyphs)
    {
        byte[] name = Index("Example"u8.ToArray());
        byte[][] programs = [[14], .. glyphs];
        byte[] charstrings = Index(programs);
        int topLength = 15;
        int charstringsOffset = 4 + name.Length + Index(new byte[topLength]).Length + 2 + 2;
        int privateOffset = charstringsOffset + charstrings.Length;
        byte[] dict = [.. Numbers(charset), 15, .. Offset(charstringsOffset), 17,
            141, .. Offset(privateOffset), 18];
        return [1, 0, 4, 4, .. name, .. Index(dict), 0, 0, 0, 0,
            .. charstrings, 141, 19, 0, 0];
    }
    private static byte[] Offset(int value) => [29, (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];
    internal static byte[] Numbers(params int[] values) => [.. values.SelectMany(value => value is >= -107 and <= 107
        ? new byte[] { (byte)(value + 139) } : [28, (byte)(value >> 8), (byte)value])];
    private static byte[] Index(params byte[][] values)
    {
        var result = new List<byte> { (byte)(values.Length >> 8), (byte)values.Length, 2 };
        int offset = 1;
        foreach (var value in values) { result.Add((byte)(offset >> 8)); result.Add((byte)offset); offset += value.Length; }
        result.Add((byte)(offset >> 8)); result.Add((byte)offset);
        foreach (var value in values) result.AddRange(value);
        return [.. result];
    }
}
