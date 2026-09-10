using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class LegacyPageGeometryTests
{
    [Theory]
    [InlineData(649.134, 649.134033203125)]
    [InlineData(841.997, 841.9970703125)]
    [InlineData(841.89, 841.8900146484375)]
    [InlineData(123.456, 123.45600128173828)]
    [InlineData(430.866, 430.8659973144531)]
    [InlineData(-649.134, -649.134033203125)]
    [InlineData(0.001, 0.0010000000474974513)]
    public void CoordinatesMatchNativeDecimalProbes(double source, double expected)
        => Assert.Equal((float)expected, PdfLegacyPageGeometry.Coordinate(source));
}
