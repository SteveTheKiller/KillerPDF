# Calibrated measurement

The measurement APIs convert PDF-space geometry into drawing units. A host can
calibrate a scale, calculate distances and areas, snap interactive points, write
editable measurement annotations, and export its own reviewed results.

The engine does not infer real-world scale from page size or drawing labels.
Calibration is a host decision and should be reviewed before its results are
used for estimating, fabrication, construction, or compliance work.

## Calibrate a drawing

Create a profile from two PDF-space points and a known real distance:

```csharp
using KillerPdf.Engine.Documents;

PdfMeasurementProfile profile = PdfMeasurementProfile.Calibrate(
    "First floor",
    start: new PdfMeasurementPoint(72, 144),
    end: new PdfMeasurementPoint(360, 144),
    knownDistance: 24,
    unitSymbol: "ft",
    precision: 2);

double wallLength = PdfMeasurement.Distance(
    profile,
    new PdfMeasurementPoint(72, 144),
    new PdfMeasurementPoint(360, 144));
```

`UnitsPerPoint` is the number of drawing units represented by one PDF coordinate
point. The calibration points must be distinct, the known distance and scale
must be positive and finite, and precision can range from 0 through 12.

Profiles serialize independently of measurement results with `ToJson()` and
`PdfMeasurementProfile.FromJson()`. The JSON is versioned. Unknown versions are
rejected instead of being interpreted as the current format.

## Resolve document, page, and region scales

`PdfMeasurementProfileMap` is a host-side way to choose among calibrations. A
region profile wins over its page profile, which wins over the document
profile. If overlapping regions contain a point, the smallest region wins.

```csharp
var profiles = new PdfMeasurementProfileMap([
    new PdfMeasurementProfileAssignment(documentProfile),
    new PdfMeasurementProfileAssignment(pageProfile, pageIndex: 2),
    new PdfMeasurementProfileAssignment(
        detailProfile,
        pageIndex: 2,
        region: new PdfContentBounds(100, 100, 300, 300))
]);

PdfMeasurementProfile selected = profiles.Resolve(
    pageIndex: 2,
    point: new PdfMeasurementPoint(180, 220));
```

Page indices are zero-based. Coordinates use PDF user space with a bottom-left
origin. A region requires a page and positive dimensions. The map permits one
document profile and one whole-page profile per page. If no profile applies,
`Resolve` throws `KeyNotFoundException`.

The map is not embedded into a PDF automatically. Save and restore the profile
assignments in the host if the workflow needs them across sessions.

## Calculate and snap geometry

`PdfMeasurement` provides:

| Method | Result |
| --- | --- |
| `Distance` | Straight-line calibrated distance |
| `Delta` | Signed horizontal and vertical movement |
| `Coordinates` | Calibrated coordinates relative to an origin |
| `Perimeter` | Open or closed path length |
| `Area` | Polygon area using the supplied point order |
| `Angle` | Smaller angle at a vertex, in degrees |
| `SnapToNearest` | Nearest candidate point within a tolerance |
| `SnapToSegments` | Nearest point on finite segments within a tolerance |
| `SnapToIntersections` | Nearest finite segment intersection within a tolerance |
| `SnapOrthogonal` | Nearest horizontal or vertical direction |
| `SnapAngle` | Nearest angular increment around an origin |

Snap tolerances are PDF points, not calibrated drawing units. When no candidate
is within tolerance, the original point is returned. Segment and point inputs
must be finite. Area uses the supplied vertex order, so a host should reject or
review self-intersecting outlines rather than treating the numeric result as a
validated parcel or room area.

## Write editable measurement annotations

Measurement annotations are incremental edits. Calculate the value, format the
label with the profile precision, and pass the same profile to the editor:

```csharp
using System.Globalization;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;

static byte[] AddWallMeasurement(
    byte[] sourceBytes,
    PdfMeasurementProfile profile)
{
    var start = new PdfPoint(72, 144);
    var end = new PdfPoint(360, 144);
    double value = PdfMeasurement.Distance(
        profile,
        new PdfMeasurementPoint(start.X, start.Y),
        new PdfMeasurementPoint(end.X, end.Y));
    string label = value.ToString(
        $"F{profile.Precision}", CultureInfo.InvariantCulture)
        + " " + profile.UnitSymbol;

    PdfDocument document = PdfDocument.Open(sourceBytes);
    return new PdfIncrementalAnnotationEditor(document)
        .AddLine(0, start, end, contents: label, measurement: profile)
        .Build();
}
```

`AddLine`, calibrated `AddPolyline`, `AddPerimeterMeasurement`, and
`AddAreaMeasurement` write editable dimension annotations with PDF measurement
dictionaries. The perimeter and area helpers calculate their default labels.
`AddAngleMeasurement` writes a calculated degree label and dimension intent,
but it does not store a calibrated measurement profile.

Annotations do not change the underlying page artwork. Viewers may display or
print annotations differently, and a later flattening step changes editability
and signature behavior. Validate the saved output in the viewers required by
the workflow. Use preflight with `PdfPreflightCheck.MeasurementAnnotations` to
report invalid measurement dictionaries and recognized calibration details.

Incremental edits preserve prior bytes, so they do not remove old revisions or
hidden content. They also modify a signed document after its signed revision.
Check certification permissions, field locks, and signature policy before
editing. See the [security guide](security.md) for those checks.

## Export reviewed results

`PdfMeasurementResult` is a host-created record. It is not populated by scanning
annotations in an arbitrary PDF. Include the profile, source geometry, and any
reviewed label or comment needed to audit the calculation.

```csharp
PdfMeasurementResult result = new()
{
    Document = "floor-plan.pdf",
    PageIndex = 0,
    Kind = "Distance",
    Label = "North wall",
    Value = wallLength,
    Unit = profile.UnitSymbol,
    Profile = profile.Name,
    UnitsPerPoint = profile.UnitsPerPoint,
    Points = [new(72, 144), new(360, 144)]
};

string text = PdfMeasurementReport.ToText([result]);
string json = PdfMeasurementReport.ToJson([result]);
string csv = PdfMeasurementReport.ToCsv([result]);
```

Text and CSV display page numbers as one-based values. JSON retains the
zero-based `PageIndex`. Numeric output is culture-invariant, and CSV follows RFC
4180 quoting. Document names, labels, comments, and geometry can be sensitive,
so the host controls where reports are stored and logged.

## Failure and trust boundaries

Calculation methods reject missing, non-finite, degenerate, or out-of-range
inputs rather than returning an untrusted value. Report generation validates
required strings, page indices, scales, values, and point coordinates.

These APIs perform geometry and serialization. They do not determine whether a
picked point corresponds to the intended object, whether a scan is distorted,
or whether separate drawing regions use the same scale. Keep the source view,
calibration evidence, selected profile, and resulting geometry together for
human review. For untrusted files, open and render them with the isolation,
timeouts, and memory limits described in the [reading guide](reading.md) and
[rendering guide](rendering.md).
