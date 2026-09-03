using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfMeasurementTests
{
    [Fact]
    public void CalibrationDrivesDistancePerimeterAreaAndAngle()
    {
        PdfMeasurementProfile profile = PdfMeasurementProfile.Calibrate("Plan", new(0, 0),
            new(10, 0), 5, "m");
        PdfMeasurementPoint[] triangle = [new(0, 0), new(6, 0), new(6, 8)];

        Assert.Equal(5, PdfMeasurement.Distance(profile, new(0, 0), new(6, 8)), 10);
        Assert.Equal(12, PdfMeasurement.Perimeter(profile, triangle), 10);
        Assert.Equal(6, PdfMeasurement.Area(profile, triangle), 10);
        Assert.Equal(90, PdfMeasurement.Angle(new(0, 1), new(0, 0), new(1, 0)), 10);
    }

    [Fact]
    public void ReportsUseStableJsonAndEscapedCsv()
    {
        PdfMeasurementResult[] results = [new()
        {
            PageIndex = 1,
            Kind = "Distance",
            Label = "Wall, north",
            Value = 12.5,
            Unit = "ft",
            Profile = "Floor plan"
        }];

        string json = PdfMeasurementReport.ToJson(results);
        string csv = PdfMeasurementReport.ToCsv(results);

        Assert.Contains("\"PageIndex\": 1", json, StringComparison.Ordinal);
        Assert.Contains("2,\"Distance\",\"Wall, north\",12.5,\"ft\",\"Floor plan\"", csv,
            StringComparison.Ordinal);
    }
}
