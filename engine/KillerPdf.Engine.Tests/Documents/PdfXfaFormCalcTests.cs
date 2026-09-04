using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfXfaFormCalcTests
{
    [Fact]
    public void EvaluateHandlesPrecedenceParenthesesUnaryAndVariables()
    {
        var variables = new Dictionary<string, double>
        {
            ["invoice.quantity"] = 3,
            ["invoice.price"] = 12.5,
            ["$tax"] = 0.2
        };

        double value = PdfXfaFormCalc.Evaluate(
            "invoice.quantity * invoice.price * (1 + $tax)", variables);

        Assert.Equal(45, value, 10);
        Assert.Equal(-5, PdfXfaFormCalc.Evaluate("-(2 + 3)"), 10);
        Assert.Equal(100, PdfXfaFormCalc.Evaluate("1e2"), 10);
    }

    [Fact]
    public void EvaluateSupportsBoundedNumericFunctions()
    {
        Assert.Equal(10, PdfXfaFormCalc.Evaluate("Sum(1, 2, 3 * 2) + Abs(-1)"), 10);
        Assert.Equal(2, PdfXfaFormCalc.Evaluate("Avg(1, 2, 3)"), 10);
        Assert.Equal(1, PdfXfaFormCalc.Evaluate("Min(3, 1, 2)"), 10);
        Assert.Equal(3, PdfXfaFormCalc.Evaluate("Max(3, 1, 2)"), 10);
        Assert.Equal(1.24, PdfXfaFormCalc.Evaluate("Round(1.235, 2)"), 10);
    }

    [Fact]
    public void EvaluateFailsClosedForUnsafeUnsupportedOrUnboundedInput()
    {
        Assert.Throws<KeyNotFoundException>(() => PdfXfaFormCalc.Evaluate("missing + 1"));
        Assert.Throws<FormatException>(() => PdfXfaFormCalc.Evaluate("app.openDoc('x')"));
        Assert.Throws<FormatException>(() => PdfXfaFormCalc.Evaluate("Sum()"));
        Assert.Throws<FormatException>(() => PdfXfaFormCalc.Evaluate("Round(1, 16)"));
        Assert.Throws<FormatException>(() => PdfXfaFormCalc.Evaluate("1 / 0"));
        Assert.Throws<ArgumentException>(() => PdfXfaFormCalc.Evaluate(new string('1', 4097)));
        Assert.Throws<ArgumentException>(() => PdfXfaFormCalc.Evaluate("x",
            new Dictionary<string, double> { ["x"] = double.NaN }));
        Assert.Throws<FormatException>(() => PdfXfaFormCalc.Evaluate(
            new string('(', 65) + "1" + new string(')', 65)));
    }
}
