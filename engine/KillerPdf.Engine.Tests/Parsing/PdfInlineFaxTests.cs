using System.Text;
using KillerPdf.Engine.Parsing;
using KillerPdf.Engine.Syntax;
using Xunit;

namespace KillerPdf.Engine.Tests.Parsing;

public sealed class PdfInlineFaxTests
{
    [Theory]
    [InlineData("/K 0 /Rows 8 /EndOfBlock false /EndOfLine true",
        "ABdDofHQ+Oh06HQ+Oh8dDp0ACaofHQ+Oh06HQ+Oh8dDp0Oh0ACOh8dDp0Oh8dD46HTodD46HABNUOh06HQ+Oh8dDp0Oh8dD46ABNU6HQ+Oh8dDp0Oh8dD46HTgAjofHQ+Oh06HQ+Oh8dDp0OhwATVDofHQ6dDofHQ+Oh06HQ+OgATXHQ6dDofHQ+Oh06HQ+Oh8c=")]
    [InlineData("/K -1",
        "Lojoj5HRHyOiOi6I6I+R0R8jojpQgrQQTQQVoIK0EE0EFaCC2EE0EFaCCtBBNBBWggrSpIIK0EFaCCaCCtBBWggmu0EFaCCaCCtBBWggmggrYVoIJoIK0EFaCCaCCtBKkggmggrQQVoIJoIK0EFa2ggrQQVoIJoIK0EFaCCcAEAE")]
    public void FindsIndependentLibTiffEncodedImageBoundary(string parameters, string encoded)
    {
        // libtiff encoded 37x8 bilevel image with a different alternating pattern on each row.
        byte[] data = Convert.FromBase64String(encoded);
        var instructions = Read(parameters + " /Columns 37", data);
        Assert.Equal(data, instructions[0].InlineImageData!.Value.ToArray());
        Assert.Equal("Q", instructions[1].Operator);
    }

    [Theory]
    [InlineData("/K 0 /Columns 8 /Rows 1 /EndOfBlock false", "10011")]
    [InlineData("/K -1 /Columns 8 /Rows 1 /EndOfBlock false", "1")]
    [InlineData("/K -1 /Columns 8", "1000000000001000000000001")]
    [InlineData("/K 0 /Columns 8 /EndOfLine true", "00000000000110011000000000001000000000001000000000001000000000001000000000001000000000001")]
    [InlineData("/K 1 /Columns 8 /Rows 2 /EndOfBlock false", "00000000000111001100000000000101")]
    [InlineData("/K 0 /Columns 8 /Rows 2 /EndOfBlock false /EncodedByteAlign true", "1001100010011000")]
    [InlineData("/K 0 /Columns 5120 /Rows 1 /EndOfBlock false", "00000001111100000001111100110101")]
    [InlineData("/K 0 /Columns 1728 /Rows 1 /EndOfBlock false", "0011010100000011001010000110111")]
    public void ReadsWhiteRowsWithExplicitRowsOrEndMarkers(string parameters, string encoded)
    {
        byte[] data = Pack(encoded);
        var instructions = Read(parameters, data);
        Assert.Equal(data, instructions[0].InlineImageData!.Value.ToArray());
        Assert.Equal("Q", instructions[1].Operator);
    }

    [Theory]
    [InlineData("/K 0 /Columns 7 /Rows 1 /EndOfBlock false", "10011")]
    [InlineData("/K 0 /Columns 8 /Rows 1 /EndOfBlock false", "10011111")]
    [InlineData("/K -1 /Columns 8", "1")]
    [InlineData("/K -1 /Columns 8 /Rows 1 /EndOfBlock false", "011")]
    public void RejectsOverlongRowsInvalidPaddingAndTruncation(string parameters, string encoded)
    {
        Exception? error = Record.Exception(() => Read(parameters, Pack(encoded)));
        Assert.True(error is PdfSyntaxException or NotSupportedException, $"Unexpected error: {error}");
    }

    [Fact]
    public void RejectsUnboundedRowTermination() => Assert.Throws<NotSupportedException>(() =>
        Read("/EndOfBlock false", Pack("10011")));

    private static IReadOnlyList<PdfContentInstruction> Read(string parameters, byte[] data)
    {
        byte[] prefix = Encoding.ASCII.GetBytes($"BI /F /CCF /DP << {parameters} >> ID ");
        return PdfContentStreamReader.Read((byte[])[.. prefix, .. data, .. " EI Q"u8.ToArray()]);
    }
    private static byte[] Pack(string bits)
    {
        var data = new byte[(bits.Length + 7) / 8];
        for (int i = 0; i < bits.Length; i++) data[i / 8] |= (byte)((bits[i] - '0') << (7 - i % 8));
        return data;
    }
}
