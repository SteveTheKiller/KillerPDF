using System.Text;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Syntax;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class CalculatorProgramTests
{
    private static PdfPageRenderer.CalculatorProgram Compile(string source)
    {
        var tokenizer = new PdfTokenizer(Encoding.ASCII.GetBytes(source));
        Assert.Equal(PdfTokenKind.BraceStart, tokenizer.Read().Kind);
        PdfPageRenderer.CalculatorProgram program =
            PdfPageRenderer.CalculatorProgram.Compile(tokenizer, "test function");
        Assert.Equal(PdfTokenKind.EndOfInput, tokenizer.Read().Kind);
        return program;
    }

    private static double[] Run(string source, double[] inputs, int outputCount)
    {
        var domain = new double[inputs.Length * 2];
        var range = new double[outputCount * 2];
        for (int index = 0; index < inputs.Length; index++)
        {
            domain[index * 2] = -1e9;
            domain[index * 2 + 1] = 1e9;
        }
        for (int index = 0; index < outputCount; index++)
        {
            range[index * 2] = -1e9;
            range[index * 2 + 1] = 1e9;
        }
        var outputs = new double[outputCount];
        Compile(source).Evaluate(inputs, domain, range, outputs);
        return outputs;
    }

    [Theory]
    [InlineData("{ 3 1 roll }", new double[] { 1, 2, 3 }, new double[] { 3, 1, 2 })]
    [InlineData("{ 3 -1 roll }", new double[] { 1, 2, 3 }, new double[] { 2, 3, 1 })]
    [InlineData("{ 3 2 roll }", new double[] { 1, 2, 3 }, new double[] { 2, 3, 1 })]
    [InlineData("{ 4 5 roll }", new double[] { 1, 2, 3, 4 }, new double[] { 4, 1, 2, 3 })]
    [InlineData("{ 2 0 roll }", new double[] { 1, 2, 3 }, new double[] { 1, 2, 3 })]
    [InlineData("{ 2 1 roll }", new double[] { 1, 2, 3 }, new double[] { 1, 3, 2 })]
    public void Evaluate_RollsInPlaceLikeTheSpecification(string source, double[] inputs,
        double[] expected)
    {
        Assert.Equal(expected, Run(source, inputs, expected.Length));
    }

    [Theory]
    [InlineData("{ 2 copy }", new double[] { 1, 2 }, new double[] { 1, 2, 1, 2 })]
    [InlineData("{ 0 copy }", new double[] { 1, 2 }, new double[] { 1, 2 })]
    [InlineData("{ 1 index }", new double[] { 7, 9 }, new double[] { 7, 9, 7 })]
    [InlineData("{ dup exch }", new double[] { 4 }, new double[] { 4, 4 })]
    [InlineData("{ 7 3 idiv 7 3 mod }", new double[] { }, new double[] { 2, 1 })]
    [InlineData("{ 1 3 bitshift -8 -2 bitshift }", new double[] { }, new double[] { 8, -2 })]
    [InlineData("{ 2.5 round -2.5 round 2.7 truncate 2.2 ceiling }", new double[] { },
        new double[] { 3, -3, 2, 3 })]
    [InlineData("{ 1 1 atan -1 0 atan }", new double[] { }, new double[] { 45, 270 })]
    [InlineData("{ 5 3 and 5 3 or 5 3 xor 5 not }", new double[] { }, new double[] { 1, 7, 6, -6 })]
    public void Evaluate_MatchesPostScriptOperatorSemantics(string source, double[] inputs,
        double[] expected)
    {
        Assert.Equal(expected, Run(source, inputs, expected.Length));
    }

    [Theory]
    [InlineData(0.25, 1)]
    [InlineData(0.75, 0)]
    [InlineData(2, 2)]
    public void Evaluate_RunsNestedConditionals(double input, double expected)
    {
        const string source = "{ dup 1 gt { pop 2 } { 0.5 lt { 1 } { 0 } ifelse } ifelse }";
        Assert.Equal([expected], Run(source, [input], 1));
    }

    [Fact]
    public void Evaluate_ComparesBooleansAndProceduresForEquality()
    {
        Assert.Equal([1, 0, 1], Run("{ true true eq { 1 } { 0 } ifelse "
            + "1 true eq { 1 } { 0 } ifelse 2 2 ne { 0 } { 1 } ifelse }", [], 3));
    }

    [Fact]
    public void Evaluate_ClampsInputsToDomainAndOutputsToRange()
    {
        var outputs = new double[1];
        Compile("{ 2 mul }").Evaluate([5], [0, 1], [0, 1.5], outputs);
        Assert.Equal([1.5], outputs);
        Compile("{ 2 mul }").Evaluate([-5], [0, 1], [-0.5, 1.5], outputs);
        Assert.Equal([0], outputs);
    }

    [Fact]
    public void Evaluate_ReusesTheStackAcrossCallsWithoutLeakingValues()
    {
        PdfPageRenderer.CalculatorProgram program = Compile("{ dup }");
        var outputs = new double[2];
        for (int pass = 0; pass < 3; pass++)
        {
            program.Evaluate([pass], [0, 10], [0, 10, 0, 10], outputs);
            Assert.Equal([pass, pass], outputs);
        }
        // A program that leaves too few values fails the same way on every call.
        PdfPageRenderer.CalculatorProgram shallow = Compile("{ pop }");
        for (int pass = 0; pass < 2; pass++)
            Assert.Throws<FormatException>(() => shallow.Evaluate([1], [0, 10], [0, 10], outputs));
    }

    [Fact]
    public void Evaluate_ReportsUnknownOperatorsOnlyWhenExecuted()
    {
        PdfPageRenderer.CalculatorProgram program = Compile("{ dup 0 gt { frobnicate } if }");
        var outputs = new double[1];
        program.Evaluate([-1], [-10, 10], [-10, 10], outputs);
        Assert.Equal([-1], outputs);
        var error = Assert.Throws<NotSupportedException>(() =>
            program.Evaluate([1], [-10, 10], [-10, 10], outputs));
        Assert.Contains("frobnicate", error.Message);
    }

    [Theory]
    [InlineData("{ pop pop }")]
    [InlineData("{ 1 0 div }")]
    [InlineData("{ 300 { dup } repeat }")]
    [InlineData("{ 5 copy }")]
    [InlineData("{ 1 { 1 } add }")]
    [InlineData("{ true 1 and }")]
    [InlineData("{ 1.5 3 roll }")]
    public void Evaluate_RejectsInvalidPrograms(string source)
    {
        var outputs = new double[1];
        Assert.ThrowsAny<Exception>(() => Compile(source).Evaluate([1], [0, 10], [0, 10], outputs));
    }

    [Fact]
    public void Evaluate_RejectsStackOverflow()
    {
        var builder = new StringBuilder("{ ");
        for (int index = 0; index < 260; index++) builder.Append("1 ");
        builder.Append('}');
        var outputs = new double[1];
        var error = Assert.Throws<FormatException>(() =>
            Compile(builder.ToString()).Evaluate([], [], [0, 10], outputs));
        Assert.Contains("stack is invalid", error.Message);
    }

    [Fact]
    public void Compile_RejectsUnterminatedAndOversizedPrograms()
    {
        Assert.Throws<FormatException>(() => Compile("{ 1 2 add"));
        var builder = new StringBuilder("{ ");
        for (int index = 0; index < 4100; index++) builder.Append("0 ");
        builder.Append('}');
        Assert.Throws<FormatException>(() => Compile(builder.ToString()));
    }
}
