using System.Globalization;
using System.Text;
using System.Text.Json;

namespace KillerPdf.Engine.Documents;

/// <summary>A calibrated conversion from PDF user-space points to drawing units.</summary>
public sealed record PdfMeasurementProfile
{
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
}

/// <summary>A point in PDF user space.</summary>
public readonly record struct PdfMeasurementPoint(double X, double Y);

/// <summary>Calibrated geometry calculations for PDF measurement tools.</summary>
public static class PdfMeasurement
{
    /// <summary>Measures the straight-line distance between two points.</summary>
    public static double Distance(PdfMeasurementProfile profile, PdfMeasurementPoint start,
        PdfMeasurementPoint end) => CheckedProfile(profile) * DistanceInPoints(start, end);

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
    /// <summary>Gets the zero-based page index.</summary>
    public int PageIndex { get; init; }
    /// <summary>Gets the measurement kind.</summary>
    public required string Kind { get; init; }
    /// <summary>Gets the optional label.</summary>
    public string? Label { get; init; }
    /// <summary>Gets the calibrated result.</summary>
    public double Value { get; init; }
    /// <summary>Gets the display unit.</summary>
    public required string Unit { get; init; }
    /// <summary>Gets the profile used to calculate the result.</summary>
    public required string Profile { get; init; }
}

/// <summary>Exports stable machine-readable measurement reports.</summary>
public static class PdfMeasurementReport
{
    /// <summary>Writes measurement results as JSON.</summary>
    public static string ToJson(IEnumerable<PdfMeasurementResult> results) =>
        JsonSerializer.Serialize(Checked(results), new JsonSerializerOptions { WriteIndented = true });

    /// <summary>Writes measurement results as RFC 4180-compatible CSV.</summary>
    public static string ToCsv(IEnumerable<PdfMeasurementResult> results)
    {
        var output = new StringBuilder("Page,Kind,Label,Value,Unit,Profile\r\n");
        foreach (PdfMeasurementResult result in Checked(results))
        {
            output.Append(result.PageIndex + 1).Append(',').Append(Csv(result.Kind)).Append(',')
                .Append(Csv(result.Label ?? string.Empty)).Append(',')
                .Append(result.Value.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                .Append(Csv(result.Unit)).Append(',').Append(Csv(result.Profile)).Append("\r\n");
        }
        return output.ToString();
    }

    private static PdfMeasurementResult[] Checked(IEnumerable<PdfMeasurementResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        PdfMeasurementResult[] values = results.ToArray();
        if (values.Any(result => result.PageIndex < 0 || !double.IsFinite(result.Value)
            || string.IsNullOrWhiteSpace(result.Kind) || string.IsNullOrWhiteSpace(result.Unit)
            || string.IsNullOrWhiteSpace(result.Profile)))
            throw new ArgumentException("A measurement report contains an invalid result.", nameof(results));
        return values;
    }

    private static string Csv(string value) => '"' + value.Replace("\"", "\"\"") + '"';
}
