using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KillerPdf.Engine.Documents;

/// <summary>A calibrated conversion from PDF user-space points to drawing units.</summary>
public sealed partial record PdfMeasurementProfile
{
    private static readonly PdfMeasurementProfileJsonContext CompactJson = new(JsonOptions(false));
    private static readonly PdfMeasurementProfileJsonContext IndentedJson = new(JsonOptions(true));

    /// <summary>Creates a named measurement profile.</summary>
    public PdfMeasurementProfile(string name, double unitsPerPoint, string unitSymbol, int precision = 2)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A profile name is required.", nameof(name));
        if (!double.IsFinite(unitsPerPoint) || unitsPerPoint <= 0)
            throw new ArgumentOutOfRangeException(nameof(unitsPerPoint));
        if (string.IsNullOrWhiteSpace(unitSymbol)) throw new ArgumentException("A unit symbol is required.", nameof(unitSymbol));
        if (precision is < 0 or > 12) throw new ArgumentOutOfRangeException(nameof(precision));
        Name = name;
        UnitsPerPoint = unitsPerPoint;
        UnitSymbol = unitSymbol;
        Precision = precision;
    }

    /// <summary>Gets the profile name.</summary>
    public string Name { get; }
    /// <summary>Gets drawing units represented by one PDF point.</summary>
    public double UnitsPerPoint { get; }
    /// <summary>Gets the display unit symbol.</summary>
    public string UnitSymbol { get; }
    /// <summary>Gets the display precision.</summary>
    public int Precision { get; }
    /// <summary>Serializes the named calibration without document measurements.</summary>
    public string ToJson(bool indented = false) => JsonSerializer.Serialize(
        new MeasurementProfileFile(1, Name, UnitsPerPoint, UnitSymbol, Precision),
        indented ? IndentedJson.MeasurementProfileFile
            : CompactJson.MeasurementProfileFile);

    /// <summary>Reads a saved named calibration.</summary>
    public static PdfMeasurementProfile FromJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        MeasurementProfileFile file = JsonSerializer.Deserialize(
            json, CompactJson.MeasurementProfileFile)
            ?? throw new JsonException("The measurement profile is empty.");
        if (file.Version != 1)
            throw new NotSupportedException(
                $"Measurement profile version {file.Version} is not supported.");
        return new PdfMeasurementProfile(
            file.Name, file.UnitsPerPoint, file.UnitSymbol, file.Precision);
    }

    /// <summary>Creates a calibration from a known real distance and its PDF-space endpoints.</summary>
    public static PdfMeasurementProfile Calibrate(string name, PdfMeasurementPoint start,
        PdfMeasurementPoint end, double knownDistance, string unitSymbol, int precision = 2)
    {
        if (!double.IsFinite(knownDistance) || knownDistance <= 0)
            throw new ArgumentOutOfRangeException(nameof(knownDistance));
        double points = PdfMeasurement.DistanceInPoints(start, end);
        if (points == 0) throw new ArgumentException("Calibration points must be distinct.", nameof(end));
        return new PdfMeasurementProfile(name, knownDistance / points, unitSymbol, precision);
    }

    private sealed record MeasurementProfileFile(
        int Version, string Name, double UnitsPerPoint, string UnitSymbol, int Precision);

    private static JsonSerializerOptions JsonOptions(bool indented) => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = indented
    };

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(MeasurementProfileFile))]
    private sealed partial class PdfMeasurementProfileJsonContext : JsonSerializerContext;
}

/// <summary>A point in PDF user space.</summary>
public readonly record struct PdfMeasurementPoint(double X, double Y);

/// <summary>A finite line segment in PDF user space.</summary>
public readonly record struct PdfMeasurementSegment(
    PdfMeasurementPoint Start, PdfMeasurementPoint End);

/// <summary>Calibrated horizontal and vertical movement.</summary>
public readonly record struct PdfMeasurementDelta(double Horizontal, double Vertical);

/// <summary>Assigns a measurement profile to a document, page, or page region.</summary>
public sealed record PdfMeasurementProfileAssignment
{
    /// <summary>Creates a profile assignment. A null page applies to the document.</summary>
    public PdfMeasurementProfileAssignment(PdfMeasurementProfile profile,
        int? pageIndex = null, PdfContentBounds? region = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (pageIndex < 0) throw new ArgumentOutOfRangeException(nameof(pageIndex));
        if (region is not null && pageIndex is null)
            throw new ArgumentException("A regional profile requires a page index.", nameof(region));
        if (region is { } bounds && (bounds.Width <= 0 || bounds.Height <= 0))
            throw new ArgumentException("A profile region must have positive dimensions.", nameof(region));
        Profile = profile;
        PageIndex = pageIndex;
        Region = region;
    }

    /// <summary>Gets the assigned profile.</summary>
    public PdfMeasurementProfile Profile { get; }
    /// <summary>Gets the assigned page, or null for the whole document.</summary>
    public int? PageIndex { get; }
    /// <summary>Gets the assigned page region, or null for the whole page.</summary>
    public PdfContentBounds? Region { get; }
}

/// <summary>Resolves document, page, and drawing-region measurement profiles.</summary>
public sealed class PdfMeasurementProfileMap
{
    private readonly PdfMeasurementProfileAssignment[] _assignments;

    /// <summary>Creates a validated profile map.</summary>
    public PdfMeasurementProfileMap(IEnumerable<PdfMeasurementProfileAssignment> assignments)
    {
        ArgumentNullException.ThrowIfNull(assignments);
        _assignments = assignments.ToArray();
        if (_assignments.Any(assignment => assignment is null))
            throw new ArgumentException("A profile assignment cannot be null.", nameof(assignments));
        if (_assignments.Count(assignment => assignment.PageIndex is null) > 1)
            throw new ArgumentException("Only one document profile can be assigned.", nameof(assignments));
        if (_assignments.Where(assignment => assignment.PageIndex is not null
                && assignment.Region is null)
            .GroupBy(assignment => assignment.PageIndex).Any(group => group.Count() > 1))
            throw new ArgumentException("Only one page profile can be assigned to each page.", nameof(assignments));
        Assignments = Array.AsReadOnly(_assignments);
    }

    /// <summary>Gets assignments in their supplied order.</summary>
    public IReadOnlyList<PdfMeasurementProfileAssignment> Assignments { get; }

    /// <summary>Resolves the most specific profile for a page coordinate.</summary>
    public PdfMeasurementProfile Resolve(int pageIndex, PdfMeasurementPoint? point = null)
    {
        if (pageIndex < 0) throw new ArgumentOutOfRangeException(nameof(pageIndex));
        if (point is { } coordinate)
        {
            PdfMeasurement.DistanceInPoints(coordinate, coordinate);
            PdfMeasurementProfileAssignment? regional = _assignments
                .Where(assignment => assignment.PageIndex == pageIndex
                    && assignment.Region is { } region && Contains(region, coordinate))
                .OrderBy(assignment => assignment.Region!.Value.Width
                    * assignment.Region.Value.Height)
                .FirstOrDefault();
            if (regional is not null) return regional.Profile;
        }
        PdfMeasurementProfileAssignment? page = _assignments.FirstOrDefault(
            assignment => assignment.PageIndex == pageIndex && assignment.Region is null);
        if (page is not null) return page.Profile;
        PdfMeasurementProfileAssignment? document = _assignments.FirstOrDefault(
            assignment => assignment.PageIndex is null);
        return document?.Profile ?? throw new KeyNotFoundException(
            $"No measurement profile applies to page {pageIndex + 1}.");
    }

    private static bool Contains(PdfContentBounds region, PdfMeasurementPoint point) =>
        point.X >= region.Left && point.X <= region.Right
        && point.Y >= region.Bottom && point.Y <= region.Top;
}

/// <summary>Calibrated geometry calculations for PDF measurement tools.</summary>
public static class PdfMeasurement
{
    /// <summary>Measures the straight-line distance between two points.</summary>
    public static double Distance(PdfMeasurementProfile profile, PdfMeasurementPoint start,
        PdfMeasurementPoint end) => CheckedProfile(profile) * DistanceInPoints(start, end);

    /// <summary>Measures signed horizontal and vertical movement.</summary>
    public static PdfMeasurementDelta Delta(
        PdfMeasurementProfile profile, PdfMeasurementPoint start,
        PdfMeasurementPoint end)
    {
        DistanceInPoints(start, end);
        double scale = CheckedProfile(profile);
        return new PdfMeasurementDelta(
            (end.X - start.X) * scale,
            (end.Y - start.Y) * scale);
    }

    /// <summary>Converts a PDF-space point to calibrated coordinates from an origin.</summary>
    public static PdfMeasurementPoint Coordinates(
        PdfMeasurementProfile profile, PdfMeasurementPoint point,
        PdfMeasurementPoint origin)
    {
        PdfMeasurementDelta delta = Delta(profile, origin, point);
        return new PdfMeasurementPoint(delta.Horizontal, delta.Vertical);
    }

    /// <summary>Snaps a point to the nearest candidate within a PDF-point tolerance.</summary>
    public static PdfMeasurementPoint SnapToNearest(
        PdfMeasurementPoint point,
        IReadOnlyList<PdfMeasurementPoint> candidates,
        double tolerancePoints)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (!double.IsFinite(tolerancePoints) || tolerancePoints < 0)
            throw new ArgumentOutOfRangeException(nameof(tolerancePoints));
        DistanceInPoints(point, point);
        PdfMeasurementPoint result = point;
        double nearest = tolerancePoints;
        foreach (PdfMeasurementPoint candidate in candidates)
        {
            double distance = DistanceInPoints(point, candidate);
            if (distance <= nearest)
            {
                nearest = distance;
                result = candidate;
            }
        }
        return result;
    }

    /// <summary>Snaps a point to the nearest finite segment intersection within a tolerance.</summary>
    public static PdfMeasurementPoint SnapToIntersections(
        PdfMeasurementPoint point,
        IReadOnlyList<PdfMeasurementSegment> segments,
        double tolerancePoints)
    {
        ArgumentNullException.ThrowIfNull(segments);
        var intersections = new List<PdfMeasurementPoint>();
        for (int first = 0; first < segments.Count; first++)
        {
            DistanceInPoints(segments[first].Start, segments[first].End);
            for (int second = first + 1; second < segments.Count; second++)
                if (TryIntersect(segments[first], segments[second], out PdfMeasurementPoint value))
                    intersections.Add(value);
        }
        return SnapToNearest(point, intersections, tolerancePoints);
    }

    /// <summary>Snaps a point to the nearest point on a finite segment within a tolerance.</summary>
    public static PdfMeasurementPoint SnapToSegments(
        PdfMeasurementPoint point,
        IReadOnlyList<PdfMeasurementSegment> segments,
        double tolerancePoints)
    {
        ArgumentNullException.ThrowIfNull(segments);
        if (!double.IsFinite(tolerancePoints) || tolerancePoints < 0)
            throw new ArgumentOutOfRangeException(nameof(tolerancePoints));
        DistanceInPoints(point, point);
        PdfMeasurementPoint result = point;
        double nearest = tolerancePoints;
        foreach (PdfMeasurementSegment segment in segments)
        {
            double length = DistanceInPoints(segment.Start, segment.End);
            if (length == 0) continue;
            double dx = segment.End.X - segment.Start.X;
            double dy = segment.End.Y - segment.Start.Y;
            double position = Math.Clamp(
                ((point.X - segment.Start.X) * dx + (point.Y - segment.Start.Y) * dy)
                    / (length * length), 0, 1);
            var candidate = new PdfMeasurementPoint(
                segment.Start.X + position * dx,
                segment.Start.Y + position * dy);
            double distance = DistanceInPoints(point, candidate);
            if (distance <= nearest)
            {
                nearest = distance;
                result = candidate;
            }
        }
        return result;
    }

    private static bool TryIntersect(PdfMeasurementSegment first,
        PdfMeasurementSegment second, out PdfMeasurementPoint intersection)
    {
        double firstX = first.End.X - first.Start.X;
        double firstY = first.End.Y - first.Start.Y;
        double secondX = second.End.X - second.Start.X;
        double secondY = second.End.Y - second.Start.Y;
        double denominator = firstX * secondY - firstY * secondX;
        if (Math.Abs(denominator) <= 1e-12)
        {
            intersection = default;
            return false;
        }
        double offsetX = second.Start.X - first.Start.X;
        double offsetY = second.Start.Y - first.Start.Y;
        double firstPosition = (offsetX * secondY - offsetY * secondX) / denominator;
        double secondPosition = (offsetX * firstY - offsetY * firstX) / denominator;
        if (firstPosition is < 0 or > 1 || secondPosition is < 0 or > 1)
        {
            intersection = default;
            return false;
        }
        intersection = new PdfMeasurementPoint(
            first.Start.X + firstPosition * firstX,
            first.Start.Y + firstPosition * firstY);
        return true;
    }

    /// <summary>Locks a point to the nearest horizontal or vertical direction from an origin.</summary>
    public static PdfMeasurementPoint SnapOrthogonal(
        PdfMeasurementPoint origin, PdfMeasurementPoint point)
    {
        DistanceInPoints(origin, point);
        return Math.Abs(point.X - origin.X) >= Math.Abs(point.Y - origin.Y)
            ? new PdfMeasurementPoint(point.X, origin.Y)
            : new PdfMeasurementPoint(origin.X, point.Y);
    }

    /// <summary>Snaps a point to the nearest angular increment around an origin.</summary>
    public static PdfMeasurementPoint SnapAngle(
        PdfMeasurementPoint origin, PdfMeasurementPoint point,
        double incrementDegrees)
    {
        double distance = DistanceInPoints(origin, point);
        if (distance == 0) return point;
        if (!double.IsFinite(incrementDegrees)
            || incrementDegrees <= 0 || incrementDegrees > 360)
            throw new ArgumentOutOfRangeException(nameof(incrementDegrees));
        double angle = Math.Atan2(point.Y - origin.Y, point.X - origin.X);
        double increment = incrementDegrees * Math.PI / 180;
        double snapped = Math.Round(angle / increment,
            MidpointRounding.AwayFromZero) * increment;
        return new PdfMeasurementPoint(
            origin.X + Math.Cos(snapped) * distance,
            origin.Y + Math.Sin(snapped) * distance);
    }

    /// <summary>Measures the perimeter of an open or closed sequence of points.</summary>
    public static double Perimeter(PdfMeasurementProfile profile,
        IReadOnlyList<PdfMeasurementPoint> points, bool close = true)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count < 2) throw new ArgumentException("At least two points are required.", nameof(points));
        double total = 0;
        for (int index = 1; index < points.Count; index++)
            total += DistanceInPoints(points[index - 1], points[index]);
        if (close && points.Count > 2) total += DistanceInPoints(points[^1], points[0]);
        return total * CheckedProfile(profile);
    }

    /// <summary>Measures polygon area using the supplied point order.</summary>
    public static double Area(PdfMeasurementProfile profile, IReadOnlyList<PdfMeasurementPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count < 3) throw new ArgumentException("At least three points are required.", nameof(points));
        double sum = 0;
        for (int index = 0; index < points.Count; index++)
        {
            PdfMeasurementPoint current = points[index];
            PdfMeasurementPoint next = points[(index + 1) % points.Count];
            sum += current.X * next.Y - next.X * current.Y;
        }
        double scale = CheckedProfile(profile);
        return Math.Abs(sum) / 2 * scale * scale;
    }

    /// <summary>Measures the smaller angle at the vertex in degrees.</summary>
    public static double Angle(PdfMeasurementPoint first, PdfMeasurementPoint vertex,
        PdfMeasurementPoint second)
    {
        double ax = first.X - vertex.X;
        double ay = first.Y - vertex.Y;
        double bx = second.X - vertex.X;
        double by = second.Y - vertex.Y;
        double denominator = Math.Sqrt(ax * ax + ay * ay) * Math.Sqrt(bx * bx + by * by);
        if (denominator == 0) throw new ArgumentException("Angle points must differ from the vertex.");
        return Math.Acos(Math.Clamp((ax * bx + ay * by) / denominator, -1, 1)) * 180 / Math.PI;
    }

    internal static double DistanceInPoints(PdfMeasurementPoint start, PdfMeasurementPoint end)
    {
        if (!double.IsFinite(start.X) || !double.IsFinite(start.Y)
            || !double.IsFinite(end.X) || !double.IsFinite(end.Y))
            throw new ArgumentOutOfRangeException(nameof(start), "Measurement coordinates must be finite.");
        return Math.Sqrt(Math.Pow(end.X - start.X, 2) + Math.Pow(end.Y - start.Y, 2));
    }

    private static double CheckedProfile(PdfMeasurementProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return profile.UnitsPerPoint;
    }
}

/// <summary>One exportable PDF measurement result.</summary>
public sealed record PdfMeasurementResult
{
    /// <summary>Gets the optional document identity.</summary>
    public string? Document { get; init; }
    /// <summary>Gets the zero-based page index.</summary>
    public int PageIndex { get; init; }
    /// <summary>Gets the measurement kind.</summary>
    public required string Kind { get; init; }
    /// <summary>Gets the optional label.</summary>
    public string? Label { get; init; }
    /// <summary>Gets the optional measurement comment.</summary>
    public string? Comment { get; init; }
    /// <summary>Gets the calibrated result.</summary>
    public double Value { get; init; }
    /// <summary>Gets the display unit.</summary>
    public required string Unit { get; init; }
    /// <summary>Gets the profile used to calculate the result.</summary>
    public required string Profile { get; init; }
    /// <summary>Gets drawing units represented by one PDF point.</summary>
    public double? UnitsPerPoint { get; init; }
    /// <summary>Gets the source geometry in PDF user space.</summary>
    public IReadOnlyList<PdfMeasurementPoint> Points { get; init; } = [];
}

/// <summary>Exports stable machine-readable measurement reports.</summary>
public static partial class PdfMeasurementReport
{
    private static readonly PdfMeasurementReportJsonContext Json = new(
        new JsonSerializerOptions { WriteIndented = true });

    /// <summary>Writes a readable measurement report.</summary>
    public static string ToText(IEnumerable<PdfMeasurementResult> results)
    {
        PdfMeasurementResult[] values = Checked(results);
        var output = new StringBuilder();
        output.Append("Measurements: ").AppendLine(values.Length.ToString(CultureInfo.InvariantCulture));
        foreach (PdfMeasurementResult result in values)
        {
            output.Append("  Page ").Append((result.PageIndex + 1).ToString(CultureInfo.InvariantCulture))
                .Append(": ").Append(result.Kind).Append(" = ")
                .Append(result.Value.ToString("R", CultureInfo.InvariantCulture)).Append(' ')
                .Append(result.Unit).Append(" (profile ").Append(Quoted(result.Profile));
            if (result.UnitsPerPoint.HasValue)
                output.Append(", ").Append(result.UnitsPerPoint.Value.ToString("R", CultureInfo.InvariantCulture))
                    .Append(' ').Append(result.Unit).Append(" per PDF point");
            output.AppendLine(")");
            if (!string.IsNullOrWhiteSpace(result.Document))
                output.Append("    Document: ").AppendLine(Quoted(result.Document));
            if (!string.IsNullOrWhiteSpace(result.Label))
                output.Append("    Label: ").AppendLine(Quoted(result.Label));
            if (!string.IsNullOrWhiteSpace(result.Comment))
                output.Append("    Comment: ").AppendLine(Quoted(result.Comment));
            if (result.Points.Count > 0)
                output.Append("    Geometry: ").AppendLine(string.Join("; ", result.Points.Select(point =>
                    $"{point.X.ToString("R", CultureInfo.InvariantCulture)}, {point.Y.ToString("R", CultureInfo.InvariantCulture)}")));
        }
        return output.ToString().TrimEnd();
    }

    /// <summary>Writes measurement results as JSON.</summary>
    public static string ToJson(IEnumerable<PdfMeasurementResult> results) =>
        JsonSerializer.Serialize(Checked(results), Json.PdfMeasurementResultArray);

    [JsonSerializable(typeof(PdfMeasurementResult[]))]
    private sealed partial class PdfMeasurementReportJsonContext : JsonSerializerContext;

    /// <summary>Writes measurement results as RFC 4180-compatible CSV.</summary>
    public static string ToCsv(IEnumerable<PdfMeasurementResult> results)
    {
        var output = new StringBuilder(
            "Document,Page,Kind,Label,Comment,Value,Unit,Profile,UnitsPerPoint,Geometry\r\n");
        foreach (PdfMeasurementResult result in Checked(results))
        {
            output.Append(Csv(result.Document ?? string.Empty)).Append(',')
                .Append(result.PageIndex + 1).Append(',').Append(Csv(result.Kind)).Append(',')
                .Append(Csv(result.Label ?? string.Empty)).Append(',')
                .Append(Csv(result.Comment ?? string.Empty)).Append(',')
                .Append(result.Value.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                .Append(Csv(result.Unit)).Append(',').Append(Csv(result.Profile)).Append(',')
                .Append(result.UnitsPerPoint?.ToString("R", CultureInfo.InvariantCulture))
                .Append(',').Append(Csv(string.Join(";", result.Points.Select(point =>
                    $"{point.X.ToString("R", CultureInfo.InvariantCulture)} {point.Y.ToString("R", CultureInfo.InvariantCulture)}"))))
                .Append("\r\n");
        }
        return output.ToString();
    }

    private static PdfMeasurementResult[] Checked(IEnumerable<PdfMeasurementResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        PdfMeasurementResult[] values = results.ToArray();
        if (values.Any(result => result.PageIndex < 0 || !double.IsFinite(result.Value)
            || result.UnitsPerPoint is double scale && (!double.IsFinite(scale) || scale <= 0)
            || result.Points.Any(point => !double.IsFinite(point.X) || !double.IsFinite(point.Y))
            || string.IsNullOrWhiteSpace(result.Kind) || string.IsNullOrWhiteSpace(result.Unit)
            || string.IsNullOrWhiteSpace(result.Profile)))
            throw new ArgumentException("A measurement report contains an invalid result.", nameof(results));
        return values;
    }

    private static string Csv(string value) => '"' + value.Replace("\"", "\"\"") + '"';

    private static string Quoted(string value) =>
        $"\"{value.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal)}\"";
}
