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
    public void CidCharsetIsNotAssumedToBeGlyphIdAndFdMatrixScalesBounds()
    {
        byte[] program = [.. Numbers(10, 20), 21, .. Numbers(100, 0, 0, 200), 5, 14];
        byte[] name = Index("CIDExample"u8.ToArray());
        int charsetOffset = 4 + name.Length + Index(new byte[31]).Length + 4;
        int fdSelectOffset = charsetOffset + 3;
        int fdArrayOffset = fdSelectOffset + 3;
        byte[] fdArray = Index([.. Numbers(2, 0, 0, 3, 0, 0), 12, 7]);
        int charstringsOffset = fdArrayOffset + fdArray.Length;
        byte[] top = [139, 139, 139, 12, 30, .. Offset(charsetOffset), 15,
            .. Offset(charstringsOffset), 17, .. Offset(fdArrayOffset), 12, 36, .. Offset(fdSelectOffset), 12, 37];
        byte[] bytes = [1, 0, 4, 4, .. name, .. Index(top), 0, 0, 0, 0,
            0, 0, 42, 0, 0, 0, .. fdArray, .. Index([14], program)];
        var font = Assert.IsType<PdfCffGlyphReader>(PdfCffGlyphReader.TryRead(bytes));
        Assert.Equal(1, font.FindCid(42));
        Assert.Equal(-1, font.FindCid(1));
        Assert.Null(font.GetGlyphName(1));
        Assert.Equal(new PdfGlyphBounds(20, 60, 220, 660), font.GetBounds(1));
    }

    [Fact]
    public void FlexAndArithmeticOperatorsProduceOutlineBounds()
    {
        byte[] program = [.. Numbers(0, 0), 21, .. Numbers(5, 5), 12, 10,
            .. Numbers(20, 30, 40, 50, 60, 70), 12, 34, 14];
        var font = Assert.IsType<PdfCffGlyphReader>(PdfCffGlyphReader.TryRead(Build(program)));
        Assert.Equal(new PdfGlyphBounds(0, 0, 250, 30), font.GetBounds(1));
    }

    internal static byte[] Build(byte[] program, byte[]? subr = null)
    {
        byte[] name = Index("Example"u8.ToArray());
        byte[] charstrings = Index([14], program);
        int charsetOffset = 4 + name.Length + Index(new byte[19]).Length + 2 + 2;
        int charstringsOffset = charsetOffset + 3;
        int privateOffset = charstringsOffset + charstrings.Length;
        byte[] dict = [.. Offset(charsetOffset), 15, .. Offset(charstringsOffset), 17,
            141, .. Offset(privateOffset), 18];
        return [1, 0, 4, 4, .. name, .. Index(dict), 0, 0, 0, 0, 0, 0, 34,
            .. charstrings, 141, 19, .. (subr is null ? [0, 0] : Index(subr))];
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
