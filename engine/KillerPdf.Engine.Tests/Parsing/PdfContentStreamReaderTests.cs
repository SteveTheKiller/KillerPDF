using System.Text;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Parsing;
using KillerPdf.Engine.Syntax;
using Xunit;

namespace KillerPdf.Engine.Tests.Parsing;

public sealed class PdfContentStreamReaderTests
{
    [Fact]
    public void ReadsTextOperatorsWithOffsetsAndIndependentOperands()
    {
        const string content = "BT /F1 12 Tf 1 0 0 1 40 700 Tm (Hello) Tj ET";
        var instructions = Read(content);
        Assert.Equal(["BT", "Tf", "Tm", "Tj", "ET"], instructions.Select(i => i.Operator));
        Assert.Empty(instructions[0].Operands);
        Assert.Equal("F1", Assert.IsType<PdfName>(instructions[1].Operands[0]).ValueAsLatin1());
        Assert.Equal(12, Assert.IsType<PdfInteger>(instructions[1].Operands[1]).Value);
        Assert.Equal(6, instructions[2].Operands.Count);
        Assert.Equal(content.IndexOf("Tm", StringComparison.Ordinal), instructions[2].Offset);
        Assert.Equal("Hello", Text(instructions[3].Operands[0]));
        Assert.Empty(instructions[4].Operands);
    }

    [Fact]
    public void PreservesStringBytesAndTextSpacingArrays()
    {
        var instructions = Read("% comment\n[(A\\(B\\)) -120 <0043> 2.5] TJ (next) ' 1 2 (last) \"");
        var array = Assert.IsType<PdfArray>(instructions[0].Operands[0]);
        Assert.Equal("A(B)", Text(array[0]));
        Assert.Equal(-120, Assert.IsType<PdfInteger>(array[1]).Value);
        Assert.Equal(new byte[] { 0, 67 }, Assert.IsType<PdfString>(array[2]).Bytes.ToArray());
        Assert.Equal(2.5, Assert.IsType<PdfReal>(array[3]).Value);
        Assert.Equal("'", instructions[1].Operator);
        Assert.Equal("\"", instructions[2].Operator);
        Assert.Equal(3, instructions[2].Operands.Count);
    }

    [Fact]
    public void ReadsMarkedContentDictionaryAndUnknownCompatibilityOperator()
    {
        var instructions = Read("/Span << /ActualText <FEFF0041> /MCID 0 >> BDC BX 9 custom EX EMC");
        Assert.IsType<PdfDictionary>(instructions[0].Operands[1]);
        Assert.Equal("custom", instructions[2].Operator);
        Assert.Equal(9, Assert.IsType<PdfInteger>(instructions[2].Operands[0]).Value);
    }

    [Fact]
    public void ReusesCommonOperatorNames()
    {
        var instructions = Read("q Q BT ET cm Do Tf Tj SCN scn q");

        Assert.Same("q", instructions[0].Operator);
        Assert.Same("Q", instructions[1].Operator);
        Assert.Same("BT", instructions[2].Operator);
        Assert.Same("ET", instructions[3].Operator);
        Assert.Same("cm", instructions[4].Operator);
        Assert.Same("Do", instructions[5].Operator);
        Assert.Same("Tf", instructions[6].Operator);
        Assert.Same("Tj", instructions[7].Operator);
        Assert.Same("SCN", instructions[8].Operator);
        Assert.Same("scn", instructions[9].Operator);
        Assert.Same(instructions[0].Operator, instructions[10].Operator);
    }

    [Fact]
    public void ReadsFiniteIntegerOperandsBeyondTheInt64RangeAsRealNumbers()
    {
        const string oversized = "-2366213136885537460660416106463232";
        PdfContentInstruction instruction = Assert.Single(Read($"{oversized} 2 m"));

        Assert.Equal("m", instruction.Operator);
        Assert.Equal(double.Parse(oversized, System.Globalization.CultureInfo.InvariantCulture),
            Assert.IsType<PdfReal>(instruction.Operands[0]).Value);
        Assert.Equal(2, Assert.IsType<PdfInteger>(instruction.Operands[1]).Value);
        Assert.Throws<PdfSyntaxException>(() =>
            new PdfObjectParser(Encoding.ASCII.GetBytes(oversized)).ParseObject());
    }

    [Theory]
    [InlineData("12")]
    [InlineData("[(unterminated) TJ")]
    [InlineData("1 0 R Do")]
    [InlineData("[1 0 R] TJ")]
    [InlineData("<< /K 1 /K 2 >> DP")]
    [InlineData("ID abc EI")]
    public void RejectsMalformedOrIndirectOperands(string content) =>
        Assert.Throws<PdfSyntaxException>(() => Read(content));

    [Fact]
    public void RejectsMalformedInlineImagesBeforeTreatingTheirBytesAsText() =>
        Assert.Throws<PdfSyntaxException>(() => Read("q BI /W 1 /H 1 ID (fake) Tj EI Q"));

    [Theory]
    [InlineData("CalGray", "A")]
    [InlineData("CalRGB", "ABC")]
    [InlineData("Lab", "ABC")]
    public void ReadsUnfilteredInlineImagesWithCalibratedColorSpaces(
        string colorSpace, string samples)
    {
        string content = $"BI /W 1 /H 1 /BPC 8 /CS [/{colorSpace} <<>>] ID {samples} EI";

        PdfContentInstruction image = Assert.Single(Read(content));

        Assert.Equal(samples.Length, image.InlineImageData!.Value.Length);
    }

    [Fact]
    public void EnforcesInstructionAndOperandBudgets()
    {
        Assert.Single(PdfContentStreamReader.Read("q"u8.ToArray(), maximumInstructions: 1));
        Assert.Throws<PdfSyntaxException>(() => PdfContentStreamReader.Read("q Q"u8.ToArray(), maximumInstructions: 1));
        Assert.Throws<PdfSyntaxException>(() => PdfContentStreamReader.Read("1 2 m"u8.ToArray(), maximumOperands: 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfContentStreamReader.Read(ReadOnlyMemory<byte>.Empty, maximumOperands: 0));
    }

    [Fact]
    public void HonorsCancellation() => Assert.Throws<OperationCanceledException>(() =>
        PdfContentStreamReader.Read("q"u8.ToArray(), cancellationToken: new CancellationToken(true)));

    [Fact]
    public void AcceptsEmptyAndCommentOnlyContent()
    {
        Assert.Empty(Read(""));
        Assert.Empty(Read("% no drawing\r\n"));
    }

    private static IReadOnlyList<PdfContentInstruction> Read(string content) =>
        PdfContentStreamReader.Read(Encoding.Latin1.GetBytes(content));

    [Theory]
    [InlineData("BT /F1 12..5 Tf (a) Tj ET", "ET")]
    [InlineData("BT /F1 12 Tf Hello) Tj ET 1 0 0 rg", "rg")]
    [InlineData("BT /F1 12 Tf (Hello Tj ET 0 g", "g")]
    [InlineData("1 0 0 RG 5 0 R 0 g", "g")]
    [InlineData("0 g 1 2 3", "g")]
    public void CompatibilityRecoverySkipsMalformedTokensAndKeepsReading(
        string content, string lastOperator)
    {
        byte[] bytes = Encoding.Latin1.GetBytes(content);

        Assert.Throws<PdfSyntaxException>(() => PdfContentStreamReader.Read(bytes));
        IReadOnlyList<PdfContentInstruction> instructions =
            PdfContentStreamReader.Read(bytes, compatibilityRecovery: true);

        Assert.NotEmpty(instructions);
        Assert.Equal(lastOperator, instructions[^1].Operator);
    }

    [Theory]
    [InlineData("q 1 0 0 1 20 30 cm % interrupted comment\nBT (nested (text) and \\) escape) Tj ET Q")]
    [InlineData("/Span << /ActualText <FEFF0041> /MCID 0 >> BDC [(A) -20 (B)] TJ EMC")]
    [InlineData("q BI /W 8 /H 1 /BPC 8 /CS /G ID A EI B CEI Q")]
    public void PrefixParsingPreservesInstructionsAcrossEveryByteBoundary(string content)
    {
        byte[] bytes = Encoding.Latin1.GetBytes(content);
        var expected = PdfContentStreamReader.Read(bytes, compatibilityRecovery: true);
        for (int split = 0; split <= bytes.Length; split++)
        {
            var prefix = PdfContentStreamReader.ReadPrefix(bytes.AsMemory(0, split),
                false, out int consumed, compatibilityRecovery: true);
            var suffix = PdfContentStreamReader.ReadPrefix(bytes.AsMemory(consumed),
                true, out int remainder, compatibilityRecovery: true);
            Assert.Equal(bytes.Length, consumed + remainder);
            Assert.Equal(expected.Count, prefix.Count + suffix.Count);
            var actual = prefix.Concat(suffix).ToArray();
            for (int index = 0; index < expected.Count; index++)
            {
                Assert.Equal(expected[index].Operator, actual[index].Operator);
                Assert.Equal(expected[index].Offset, actual[index].Offset
                    + (index < prefix.Count ? 0 : consumed));
                Assert.Equal(expected[index].Operands.Count, actual[index].Operands.Count);
                Assert.Equal(expected[index].InlineImageData?.ToArray(),
                    actual[index].InlineImageData?.ToArray());
            }
        }
    }

    [Fact]
    public void PrefixDoesNotRecoverAnUnfinishedStringOrOperator()
    {
        var prefix = PdfContentStreamReader.ReadPrefix("q (unfinished Tj Q"u8.ToArray(),
            false, out int consumed, compatibilityRecovery: true);
        Assert.Equal("q", Assert.Single(prefix).Operator);
        Assert.Equal(1, consumed);
        Assert.Empty(PdfContentStreamReader.ReadPrefix("c"u8.ToArray(), false, out consumed));
        Assert.Equal(0, consumed);
        Assert.Throws<PdfSyntaxException>(() => PdfContentStreamReader.ReadPrefix(
            "(unfinished"u8.ToArray(), true, out _));
    }

    [Fact]
    public void StreamingReaderGrowsForImagesAndPreservesGlobalOffsets()
    {
        const string unit = "q BI /W 8 /H 1 /BPC 8 /CS /G ID A EI B CEI Q\n";
        byte[] bytes = Encoding.Latin1.GetBytes(string.Concat(Enumerable.Range(0, 100)
            .Select(index => unit.Replace("A EI B C", $"{index:D2} EI BC", StringComparison.Ordinal))));
        using var stream = new MemoryStream(bytes);
        var actual = PdfContentStreamReader.Enumerate(stream, initialBufferBytes: 7,
            maximumBufferedBytes: 128, compatibilityRecovery: true).ToArray();
        Assert.Equal(300, actual.Length);
        for (int index = 0; index < 100; index++)
        {
            Assert.Equal(index * unit.Length, actual[index * 3].Offset);
            Assert.Equal("BI", actual[index * 3 + 1].Operator);
            Assert.Equal(index * unit.Length + 2, actual[index * 3 + 1].Offset);
            Assert.Equal(Encoding.Latin1.GetBytes($"{index:D2} EI BC"),
                actual[index * 3 + 1].InlineImageData!.Value.ToArray());
            Assert.Equal("Q", actual[index * 3 + 2].Operator);
        }
        Assert.True(stream.CanRead);
    }

    [Fact]
    public void StreamingReaderReportsLimitsAndCancellation()
    {
        using var exact = new MemoryStream(Encoding.ASCII.GetBytes("(" + new string('a', 27) + ") Tj"));
        var complete = Assert.Single(PdfContentStreamReader.Enumerate(exact,
            initialBufferBytes: 8, maximumBufferedBytes: 32));
        Assert.Equal("Tj", complete.Operator);
        Assert.Equal(new string('a', 27), Text(Assert.Single(complete.Operands)));
        using var instructions = new MemoryStream("q Q q Q q Q"u8.ToArray());
        Assert.Throws<PdfSyntaxException>(() => PdfContentStreamReader.Enumerate(instructions,
            initialBufferBytes: 4, maximumInstructions: 3, compatibilityRecovery: true).ToArray());
        using var oversized = new MemoryStream(Encoding.ASCII.GetBytes("(" + new string('a', 100)));
        Assert.Throws<PdfSyntaxException>(() => PdfContentStreamReader.Enumerate(oversized,
            initialBufferBytes: 8, maximumBufferedBytes: 32, compatibilityRecovery: true).ToArray());
        using var canceled = new MemoryStream("q Q"u8.ToArray());
        Assert.Throws<OperationCanceledException>(() => PdfContentStreamReader.Enumerate(canceled,
            cancellationToken: new CancellationToken(true)).ToArray());
    }

    private static string Text(PdfObject value) =>
        Encoding.Latin1.GetString(Assert.IsType<PdfString>(value).Bytes.Span);
}
