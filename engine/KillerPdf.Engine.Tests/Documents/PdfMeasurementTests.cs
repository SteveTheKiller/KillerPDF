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
    public void DeltaCoordinatesAndSnappingPreservePdfSpaceDirection()
    {
        var profile = new PdfMeasurementProfile("Plan", 0.5, "m");

        Assert.Equal(new PdfMeasurementDelta(3, -4),
            PdfMeasurement.Delta(profile, new(2, 10), new(8, 2)));
        Assert.Equal(new PdfMeasurementPoint(3, -4),
            PdfMeasurement.Coordinates(profile, new(8, 2), new(2, 10)));
        Assert.Equal(new PdfMeasurementPoint(10, 10),
            PdfMeasurement.SnapToNearest(new(9, 11),
                [new(10, 10), new(20, 20)], 2));
        Assert.Equal(new PdfMeasurementPoint(9, 11),
            PdfMeasurement.SnapToNearest(new(9, 11),
                [new(10, 10)], 1));
        Assert.Equal(new PdfMeasurementPoint(8, 2),
            PdfMeasurement.SnapOrthogonal(new(2, 2), new(8, 4)));
        Assert.Equal(new PdfMeasurementPoint(2, 9),
            PdfMeasurement.SnapOrthogonal(new(2, 2), new(4, 9)));
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

    [Fact]
    public void MeasurementProfilesRoundTripWithoutMeasurementResults()
    {
        var profile = new PdfMeasurementProfile("Site plan", 0.125, "ft", 3);

        string json = profile.ToJson();
        PdfMeasurementProfile restored = PdfMeasurementProfile.FromJson(json);

        Assert.Equal(profile.Name, restored.Name);
        Assert.Equal(profile.UnitsPerPoint, restored.UnitsPerPoint);
        Assert.Equal(profile.UnitSymbol, restored.UnitSymbol);
        Assert.Equal(profile.Precision, restored.Precision);
        Assert.DoesNotContain("PageIndex", json, StringComparison.Ordinal);
        Assert.Throws<NotSupportedException>(() => PdfMeasurementProfile.FromJson(
            "{\"version\":2,\"name\":\"Future\",\"unitsPerPoint\":1,\"unitSymbol\":\"m\",\"precision\":2}"));
    }
}
