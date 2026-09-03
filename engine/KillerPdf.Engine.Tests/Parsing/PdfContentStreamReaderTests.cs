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
        Assert.Equal(new[] { "BT", "Tf", "Tm", "Tj", "ET" }, instructions.Select(i => i.Operator));
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

    private static string Text(PdfObject value) =>
        Encoding.Latin1.GetString(Assert.IsType<PdfString>(value).Bytes.Span);
}
