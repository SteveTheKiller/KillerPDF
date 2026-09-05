using System.Text;
using KillerPdf.Engine.CrossReference;
using KillerPdf.Engine.Syntax;
using Xunit;

namespace KillerPdf.Engine.Tests.CrossReference;

public sealed class PdfStartXrefTests
{
    [Fact]
    public void Find_ReturnsFinalRevisionPointer()
    {
        string source = "old\nstartxref\n1\n%%EOF\nnew revision\nstartxref\r\n17\r\n%%EOF\r\n";

        PdfStartXref result = PdfStartXref.Find(Encoding.ASCII.GetBytes(source));

        Assert.Equal(17, result.Offset);
        Assert.Equal(source.LastIndexOf("startxref", StringComparison.Ordinal), result.MarkerOffset);
    }

    [Fact]
    public void Find_TreatsCommentsAsTriviaAroundOffset()
    {
        string source = "x\nstartxref\n% pointer follows\n1 % offset complete\n%%EOF\n";

        PdfStartXref result = PdfStartXref.Find(
            Encoding.ASCII.GetBytes(source));

        Assert.Equal(1, result.Offset);
    }

    [Fact]
    public void Find_CompatibilityRecoveryRetainsOffsetPastEndOfFile()
    {
        byte[] source = "x\nstartxref\n999\n%%EOF\n"u8.ToArray();

        Assert.Throws<PdfSyntaxException>(() => PdfStartXref.Find(source));
        Assert.Equal(999, PdfStartXref.Find(
            source, allowPastEndOffset: true).Offset);
    }

    [Theory]
    [InlineData("x\nstartxref 1 % trailing startxref startxref text\n%%EOF\n")]
    [InlineData("x\nstartxref % pointer startxref follows\n1\n%%EOF\n")]
    public void Find_IgnoresStartXrefTextInsideOffsetComments(string source)
    {
        PdfStartXref result = PdfStartXref.Find(
            Encoding.ASCII.GetBytes(source));

        Assert.Equal(1, result.Offset);
        Assert.Equal(source.IndexOf("startxref", StringComparison.Ordinal),
            result.MarkerOffset);
    }

    [Theory]
    [InlineData("no marker")]
    [InlineData("startxref\n\n%%EOF")]
    [InlineData("startxref\n999\n%%EOF")]
    [InlineData("startxref\n0\nnot-eof")]
    [InlineData("startxref\n0\n%%EOF")]
    [InlineData("xstartxref\n0\n%%EOF")]
    [InlineData("x startxref0\n%%EOF")]
    [InlineData("x startxref\n0%%EOF")]
    [InlineData("startxref\n0\n%%EOF\ntrash")]
    public void Find_RejectsMalformedFinalDeclaration(string source)
    {
        Assert.Throws<PdfSyntaxException>(() => PdfStartXref.Find(Encoding.ASCII.GetBytes(source)));
    }
}
