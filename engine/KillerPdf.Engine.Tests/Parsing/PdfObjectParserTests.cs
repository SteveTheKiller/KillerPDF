using System.Text;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Parsing;
using KillerPdf.Engine.Syntax;
using Xunit;

namespace KillerPdf.Engine.Tests.Parsing;

public sealed class PdfObjectParserTests
{
    [Fact]
    public void ParseObject_BuildsNestedArraysAndDictionaries()
    {
        PdfObject result = Parser("<< /Type /Example /Count 2 /Enabled true /Items [(one) <74776f> null] >>")
            .ParseObject();

        var dictionary = Assert.IsType<PdfDictionary>(result);
        Assert.Equal("Example", Assert.IsType<PdfName>(dictionary[Name("Type")]).ValueAsLatin1());
        Assert.Equal(2, Assert.IsType<PdfInteger>(dictionary[Name("Count")]).Value);
        Assert.True(Assert.IsType<PdfBoolean>(dictionary[Name("Enabled")]).Value);

        var items = Assert.IsType<PdfArray>(dictionary[Name("Items")]);
        Assert.Equal(3, items.Count);
        Assert.Equal("one", Text(Assert.IsType<PdfString>(items[0])));
        Assert.Equal(PdfStringForm.Literal, Assert.IsType<PdfString>(items[0]).Form);
        Assert.Equal("two", Text(Assert.IsType<PdfString>(items[1])));
        Assert.Equal(PdfStringForm.Hexadecimal, Assert.IsType<PdfString>(items[1]).Form);
        Assert.Same(PdfNull.Instance, items[2]);
    }

    [Fact]
    public void ParseObject_RecognizesIndirectReferenceWithoutConsumingFollowingObject()
    {
        var parser = Parser("12 4 R /Next");

        var reference = Assert.IsType<PdfIndirectReference>(parser.ParseObject());
        Assert.Equal(12, reference.ObjectNumber);
        Assert.Equal(4, reference.Generation);
        Assert.Equal("Next", Assert.IsType<PdfName>(parser.ParseObject()).ValueAsLatin1());
    }

    [Fact]
    public void ParseObject_DoesNotMistakeAdjacentIntegersForAReference()
    {
        var parser = Parser("12 4 value");

        Assert.Equal(12, Assert.IsType<PdfInteger>(parser.ParseObject()).Value);
        Assert.Equal(4, Assert.IsType<PdfInteger>(parser.ParseObject()).Value);
    }

    [Fact]
    public void ParseSingleObject_AllowsTriviaButRejectsAnotherObject()
    {
        Assert.Equal(12, Assert.IsType<PdfInteger>(Parser("12 % done\n").ParseSingleObject()).Value);
        Assert.Throws<PdfSyntaxException>(() => Parser("12 13").ParseSingleObject());
    }

    [Theory]
    [InlineData("34.", 34.0)]
    [InlineData("-.125", -0.125)]
    public void ParseObject_ParsesRealNumbersInvariantly(string source, double expected)
    {
        Assert.Equal(expected, Assert.IsType<PdfReal>(Parser(source).ParseObject()).Value);
    }

    [Fact]
    public void ParseIndirectObject_ParsesHeaderValueAndTerminator()
    {
        PdfIndirectObject result = Parser("27 3 obj << /Length 5 >> endobj").ParseIndirectObject();

        Assert.Equal(27, result.ObjectNumber);
        Assert.Equal(3, result.Generation);
        Assert.Equal(0, result.Offset);
        Assert.Equal(5, Assert.IsType<PdfInteger>(Assert.IsType<PdfDictionary>(result.Value)[Name("Length")]).Value);
    }

    [Theory]
    [InlineData("-1 0 R")]
    [InlineData("1 65536 R")]
    [InlineData("2147483648 0 R")]
    public void ParseObject_RejectsOutOfRangeReferences(string source)
    {
        Assert.Throws<PdfSyntaxException>(() => Parser(source).ParseObject());
    }

    [Theory]
    [InlineData("<< /A 1 /A 2 >>")]
    [InlineData("<< 1 /A >>")]
    [InlineData("[1 2")]
    [InlineData("12 0 obj true")]
    [InlineData("keyword")]
    public void ParseObject_RejectsMalformedObjectSyntax(string source)
    {
        var parser = Parser(source);
        Assert.Throws<PdfSyntaxException>(() =>
        {
            if (source.Contains(" obj ", StringComparison.Ordinal))
                parser.ParseIndirectObject();
            else
                parser.ParseObject();
        });
    }

    [Fact]
    public void ParseObject_CompatibilityRecoveryUsesLastDuplicateDictionaryValue()
    {
        var parser = new PdfObjectParser(
            Encoding.ASCII.GetBytes("<< /PageMode /UseNone /PageMode /UseOutlines >>"),
            allowDuplicateDictionaryKeys: true);

        PdfDictionary dictionary = Assert.IsType<PdfDictionary>(parser.ParseObject());

        Assert.Equal("UseOutlines",
            Assert.IsType<PdfName>(dictionary[Name("PageMode")]).ValueAsLatin1());
    }

    [Fact]
    public void ParseObject_EnforcesNestingLimit()
    {
        string source = new string('[', PdfObjectParser.MaximumNestingDepth + 1)
                      + new string(']', PdfObjectParser.MaximumNestingDepth + 1);

        PdfSyntaxException error = Assert.Throws<PdfSyntaxException>(() => Parser(source).ParseObject());
        Assert.Contains("nesting limit", error.Message, StringComparison.Ordinal);
    }

    private static PdfObjectParser Parser(string source) =>
        new(Encoding.Latin1.GetBytes(source));

    private static PdfName Name(string value) => new(Encoding.Latin1.GetBytes(value));
    private static string Text(PdfString value) => Encoding.Latin1.GetString(value.Bytes.Span);
}
