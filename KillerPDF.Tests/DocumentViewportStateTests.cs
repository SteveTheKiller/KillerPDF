using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class DocumentViewportStateTests
{
    [Fact]
    public void TrueZoomExtensionPreservesEveryExistingFieldAndUnroundedRatio()
    {
        const string legacy = "C:\\docs\\report.pdf|None|0.2604166666666667|Single|3|12.5|44.75";
        Assert.Equal(legacy + "|2.1234567890123457",
            DocumentViewportState.AppendTrueZoom(legacy, 2.1234567890123457));
    }

    [Fact]
    public void OptionalTrueZoomOverridesTheModeDependentLegacyScale()
    {
        string[] fields = "report.pdf|None|0.2604166666666667|Single|3|12.5|44.75|2.1234567890123457".Split('|');
        Assert.Equal(2.1234567890123457, DocumentViewportState.ReadTrueZoom(fields, 0.2604166666666667, false));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("bad")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1,5")]
    public void MissingOrInvalidExtensionFallsBackOnlyForLegacyContinuous(string? optional)
    {
        string row = "report.pdf|None|2|Continuous|3|0|0" + (optional is null ? "" : "|" + optional);
        string[] fields = row.Split('|');
        Assert.Equal(2, DocumentViewportState.ReadTrueZoom(fields, 2, true));
        Assert.Null(DocumentViewportState.ReadTrueZoom(fields, 0.2604166666666667, false));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(0)]
    [InlineData(-1)]
    public void InvalidCaptureOmitsExtensionAndCannotBecomeLegacyTrueZoom(double invalid)
    {
        const string legacy = "report.pdf|None|1|Continuous|0|0|0";
        Assert.Equal(legacy, DocumentViewportState.AppendTrueZoom(legacy, invalid));
        Assert.Null(DocumentViewportState.ReadTrueZoom(legacy.Split('|'), invalid, true));
    }

    [Theory]
    [InlineData(double.Epsilon)]
    [InlineData(double.MaxValue)]
    [InlineData(0.384)]
    public void FinitePositiveRatiosRoundTripWithoutPrematureClamp(double ratio)
    {
        string row = DocumentViewportState.AppendTrueZoom("report.pdf|None|1|Single|0|0|0", ratio);
        Assert.Equal(ratio, DocumentViewportState.ReadTrueZoom(row.Split('|'), 1, false));
    }
}
