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
        PdfMeasurementPoint angled = PdfMeasurement.SnapAngle(
            new(0, 0), new(6, 5), 45);
        Assert.Equal(angled.X, angled.Y, 10);
        Assert.Equal(Math.Sqrt(61),
            PdfMeasurement.Distance(new PdfMeasurementProfile("Points", 1, "pt"),
                new(0, 0), angled), 10);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PdfMeasurement.SnapAngle(new(0, 0), new(1, 1), 0));
    }

    [Fact]
    public void ReportsUseStableJsonAndEscapedCsv()
    {
        PdfMeasurementResult[] results = [new()
        {
            Document = "site,plan.pdf",
            PageIndex = 1,
            Kind = "Distance",
            Label = "Wall, north",
            Comment = "Verify, onsite",
            Value = 12.5,
            Unit = "ft",
            Profile = "Floor plan",
            UnitsPerPoint = 0.5,
            Points = [new(1, 2), new(3, 4)]
        }];

        string json = PdfMeasurementReport.ToJson(results);
        string csv = PdfMeasurementReport.ToCsv(results);

        Assert.Contains("\"PageIndex\": 1", json, StringComparison.Ordinal);
        Assert.Contains("\"Document\": \"site,plan.pdf\"", json, StringComparison.Ordinal);
        Assert.Contains("\"Comment\": \"Verify, onsite\"", json, StringComparison.Ordinal);
        Assert.Contains("\"site,plan.pdf\",2,\"Distance\",\"Wall, north\",\"Verify, onsite\",12.5,\"ft\",\"Floor plan\",0.5,\"1 2;3 4\"", csv,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SnapsToFiniteSegmentIntersectionsWithinTolerance()
    {
        PdfMeasurementSegment[] segments = [
            new(new(0, 0), new(10, 10)),
            new(new(0, 10), new(10, 0)),
            new(new(20, 0), new(20, 10))];

        Assert.Equal(new PdfMeasurementPoint(5, 5),
            PdfMeasurement.SnapToIntersections(new(5.5, 4.5), segments, 1));
        Assert.Equal(new PdfMeasurementPoint(8, 8),
            PdfMeasurement.SnapToIntersections(new(8, 8), segments, 1));
    }

    [Fact]
    public void SnapsToNearestPointAlongFiniteSegments()
    {
        PdfMeasurementSegment[] segments = [
            new(new(0, 0), new(10, 0)),
            new(new(20, 0), new(20, 10)),
            new(new(30, 30), new(30, 30))];

        Assert.Equal(new PdfMeasurementPoint(6, 0),
            PdfMeasurement.SnapToSegments(new(6, 1), segments, 2));
        Assert.Equal(new PdfMeasurementPoint(20, 10),
            PdfMeasurement.SnapToSegments(new(21, 12), segments, 3));
        Assert.Equal(new PdfMeasurementPoint(6, 3),
            PdfMeasurement.SnapToSegments(new(6, 3), segments, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PdfMeasurement.SnapToSegments(new(0, 0), segments, -1));
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

    [Fact]
    public void ProfileMapPrefersRegionThenPageThenDocument()
    {
        var document = new PdfMeasurementProfile("Document", 1, "pt");
        var page = new PdfMeasurementProfile("Page", 0.5, "in");
        var region = new PdfMeasurementProfile("Detail", 2, "mm");
        var profiles = new PdfMeasurementProfileMap([
            new(document),
            new(page, 1),
            new(region, 1, new PdfContentBounds(10, 10, 30, 30))]);

        Assert.Same(document, profiles.Resolve(0));
        Assert.Same(page, profiles.Resolve(1, new PdfMeasurementPoint(5, 5)));
        Assert.Same(region, profiles.Resolve(1, new PdfMeasurementPoint(20, 20)));
        Assert.Throws<ArgumentException>(() => new PdfMeasurementProfileMap([
            new(document), new(page)]));
    }
}
