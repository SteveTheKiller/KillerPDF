using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Objects;
using Xunit;

namespace KillerPdf.Engine.Tests.Fonts;

public sealed class PdfType1GlyphReaderTests
{
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(4)]
    public void ReadsEncryptedOrPlainCharstringsWithExactLineBounds(int lenIv)
    {
        byte[] program = [.. Number(0), .. Number(600), 13, .. Number(20), .. Number(0), 21,
            .. Number(0), .. Number(700), 5, .. Number(500), .. Number(0), 5,
            .. Number(0), .. Number(-700), 5, 9, 14];
        var font = Font(program, lenIv);
        Assert.Equal(new PdfGlyphBounds(20, 0, 520, 700), font.GetGlyphBounds(65));
    }

    [Fact]
    public void FindsActualBezierExtremaInsteadOfControlPointBounds()
    {
        byte[] program = [.. Number(0), .. Number(600), 13, .. Number(0), .. Number(0), 21,
            .. Number(0), .. Number(100), .. Number(100), .. Number(0), .. Number(0), .. Number(-100), 8, 14];
        Assert.Equal(new PdfGlyphBounds(0, 0, 100, 75), Font(program).GetGlyphBounds(65));
    }

    [Fact]
    public void FollowsSubroutinesAndBoundsRecursiveSubroutines()
    {
        byte[] main = [.. Number(0), .. Number(600), 13, .. Number(0), 10, 14];
        byte[] subr = [.. Number(10), .. Number(20), 21, .. Number(100), .. Number(200), 5, 11];
        Assert.Equal(new PdfGlyphBounds(10, 20, 110, 220), Font(main, subroutine: subr).GetGlyphBounds(65));
        Assert.Null(Font(main, subroutine: [.. Number(0), 10, 11]).GetGlyphBounds(65));
    }

    [Fact]
    public void ClosePathPreservesCurrentPointForTheNextContour()
    {
        byte[] program = [.. Number(0), .. Number(600), 13, .. Number(0), .. Number(0), 21,
            .. Number(100), .. Number(100), 5, 9, .. Number(50), .. Number(50), 21,
            .. Number(100), .. Number(100), 5, 14];
        Assert.Equal(new PdfGlyphBounds(0, 0, 250, 250), Font(program).GetGlyphBounds(65));
    }

    [Fact]
    public void FlexUsesOriginalStartAndSkipsItsReferencePoint()
    {
        var program = new List<byte>();
        program.AddRange([.. Number(0), .. Number(600), 13, .. Number(100), .. Number(-10), 21,
            .. Number(0), .. Number(1), 12, 16]);
        foreach (var (x, y) in new[] { (50, 0), (-35, 0), (10, 10), (25, 0), (25, 0), (10, -10), (15, 0) })
            program.AddRange([.. Number(x), .. Number(y), 21, .. Number(0), .. Number(2), 12, 16]);
        program.AddRange([.. Number(50), .. Number(200), .. Number(-10), .. Number(3), .. Number(0),
            12, 16, 12, 17, 12, 17, 12, 33, 14]);
        Assert.Equal(new PdfGlyphBounds(100, -10, 200, 0), Font(program.ToArray()).GetGlyphBounds(65));
    }

    [Fact]
    public void ReadsPostScriptDashPipeBinaryReaderToken()
    {
        byte[] program = [.. Number(0), .. Number(600), 13, .. Number(10), .. Number(20), 21,
            .. Number(100), .. Number(200), 5, 14];
        Assert.Equal(new PdfGlyphBounds(10, 20, 110, 220), Font(program, 0, reader: "-|").GetGlyphBounds(65));
    }

    [Fact]
    public void UsesEmbeddedEncodingWhenPdfResourceOmitsEncoding()
    {
        var font = Font([14], encoding: "/Encoding 256 array 0 1 255 {1 index exch /.notdef put} for dup 11 /ff put dup 12 /fi put dup 16 /zeta put readonly def");
        Assert.Equal("ff", Assert.Single(font.Decode(new byte[] { 11 })).Text.Normalize(NormalizationForm.FormKC));
        Assert.Equal("fi", Assert.Single(font.Decode(new byte[] { 12 })).Text.Normalize(NormalizationForm.FormKC));
        Assert.Equal("\u03b6", Assert.Single(font.Decode(new byte[] { 16 })).Text);
    }

    private static PdfExtractionFont Font(byte[] program, int lenIv = -1, byte[]? subroutine = null, string reader = "RD", string encoding = "")
    {
        byte[] header = Encoding.ASCII.GetBytes($"%!PS\n/FontMatrix [0.001 0 0 0.001 0 0] def {encoding} currentfile eexec\n");
        using var plain = new MemoryStream();
        plain.Write(new byte[4]);
        plain.Write(Encoding.ASCII.GetBytes($"/lenIV {lenIv} def /Subrs 1 array "));
        if (subroutine is not null)
        {
            byte[] encoded = lenIv == -1 ? subroutine : Encrypt([.. new byte[lenIv], .. subroutine], 4330);
            plain.Write(Encoding.ASCII.GetBytes($"dup 0 {encoded.Length} {reader} "));
            plain.Write(encoded);
            plain.Write(" NP "u8);
        }
        byte[] glyph = lenIv == -1 ? program : Encrypt([.. new byte[lenIv], .. program], 4330);
        plain.Write(Encoding.ASCII.GetBytes($"/CharStrings 1 dict dup begin /A {glyph.Length} {reader} "));
        plain.Write(glyph);
        plain.Write(" ND end currentfile closefile"u8);
        byte[] cipher = Encrypt(plain.ToArray(), 55665);
        var file = new PdfStream(D(("Length1", new PdfInteger(header.Length)), ("Length2", new PdfInteger(cipher.Length))), [.. header, .. cipher]);
        var dictionary = D(("Subtype", new PdfName("Type1"u8)), ("BaseFont", new PdfName("Test"u8)),
            ("FontDescriptor", D(("FontFile", file))));
        return PdfFontResourceReader.Read(PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage().Build()), dictionary);
    }

    private static byte[] Encrypt(byte[] source, int seed)
    {
        byte[] result = new byte[source.Length];
        for (int i = 0; i < source.Length; i++)
        {
            byte value = (byte)(source[i] ^ (seed >> 8));
            result[i] = value;
            seed = ((value + seed) * 52845 + 22719) & 65535;
        }
        return result;
    }

    private static byte[] Number(int value)
    {
        if (value is >= -107 and <= 107) return [(byte)(value + 139)];
        if (value is >= 108 and <= 1131) return [(byte)((value - 108) / 256 + 247), (byte)((value - 108) % 256)];
        if (value is >= -1131 and <= -108) return [(byte)((-value - 108) / 256 + 251), (byte)((-value - 108) % 256)];
        return [255, (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];
    }

    private static PdfDictionary D(params (string Key, PdfObject Value)[] pairs) =>
        new(pairs.Select(p => new KeyValuePair<PdfName, PdfObject>(new PdfName(Encoding.ASCII.GetBytes(p.Key)), p.Value)));
}
