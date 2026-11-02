using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using KillerPDF.Services;
using KillerPdf.Engine.Documents;

namespace KillerPDF.Controls;

// The Measure tool. Distance is two points (click twice, or drag); Perimeter clicks out a path and
// finishes on a double-click, Enter, or a click back on its first point (which closes it); Area
// clicks out a polygon the same way; Angle is one arm's end, the corner, then the other arm's end.
// Points snap to the drawing's own corners and lines and to other measurements unless Alt is held,
// and Shift locks the next segment to 45 degree steps.
//
// A finished measurement is a MeasurementAnnotation on the page overlay: selectable, movable,
// deletable and undoable like the other markup, with handles on its points and its label. Save
// writes it as a real PDF measurement annotation (see PdfEngineIntegration.WriteMeasurements) with
// the scale it was made with, and opening the file brings it back for editing. The page scale
// itself lives in the document as each page's /VP viewport, set from the Measure bar.
public partial class PdfViewer
{
    private const string MeasureUnitSetting = "MeasureUnit";
    private const double MeasureSnapPixels = 9;
    private const double MeasureCloseSnapPixels = 9;
    private const string MeasureVertexTag = "V";
    private const string MeasureLabelTag = "L";

    private MeasurementKind _measureMode = MeasurementKind.Distance;
    private string? _measureUnit;

    // Placement in progress.
    private readonly List<Point> _measurePoints = [];
    private int _measurePage = -1;
    private Canvas? _measureCanvas;
    private Point _measureCursor;
    private Point _measurePressAt;
    private bool _measurePressed;
    private bool _measureCalibrating;
    private Polyline? _measurePreview;
    private Polyline? _measureArcPreview;
    private Ellipse? _measureSnapRing;
    private Border? _measureReadout;
    private TextBlock? _measureReadoutText;
    private Border? _measureGrainLayer;

    // Dragging a point or the label of a selected measurement.
    private MeasurementAnnotation? _measureEditTarget;
    private int _measureEditHandle = -2;   // -1 is the label, 0 and up a point
    private bool _measureEditUndoPushed;
    private bool _measureEditing;

    // Drawing geometry to snap to, per page, in crop-relative page points.
    private readonly Dictionary<int, MeasureSnapGeometry> _measureSnapCache = [];
    private readonly HashSet<int> _measureSnapLoading = [];
    private string? _measureSnapFile;

    // The page scales of the open document, read once per engine session.
    private PdfMeasurementProfileMap? _pageScales;
    private object? _pageScalesSession;

    private sealed record MeasureSnapGeometry(List<PdfMeasurementPoint> Vertices, List<PdfMeasurementSegment> Segments);

    internal MeasurementKind MeasureMode
    {
        get => _measureMode;
        set
        {
            if (_measureMode == value) return;
            CancelMeasurePlacement();
            _measureMode = value;
        }
    }

    /// <summary>The unit new measurements use, remembered between sessions. The first default
    /// follows the Windows region: millimeters where it is metric, inches elsewhere.</summary>
    internal string MeasureUnit
    {
        get
        {
            if (_measureUnit is null)
            {
                string? saved = App.GetSetting(MeasureUnitSetting);
                _measureUnit = MeasurementUnits.TryFind(saved, out MeasureUnit unit) && unit.Id != "pt"
                    ? unit.Id : MeasurementUnits.DefaultUnitId(MeasurementUnits.RegionIsMetric());
            }
            return _measureUnit;
        }
        set
        {
            if (!MeasurementUnits.TryFind(value, out _) || value == MeasureUnit) return;
            _measureUnit = value;
            App.SetSetting(MeasureUnitSetting, value);
        }
    }

    internal bool MeasurePlacementActive => _measurePoints.Count > 0;

    /// <summary>Shift+M: the next measurement type, Distance, Perimeter, Area, Angle and around.</summary>
    internal void CycleMeasureMode() =>
        MeasureMode = (MeasurementKind)(((int)_measureMode + 1) % 4);

    // ── Placement ────────────────────────────────────────────────────────────────────────────

    internal void MeasureMouseDown(int pageIndex, Point position, MouseButtonEventArgs e)
    {
        if (_measurePoints.Count > 0 && pageIndex != _measurePage) CancelMeasurePlacement();
        MeasurementKind kind = _measureCalibrating ? MeasurementKind.Distance : _measureMode;

        if (_measurePoints.Count == 0)
        {
            // Not placing yet: a press on an existing measurement picks it up instead.
            if (!_measureCalibrating && MeasurementAt(pageIndex, position) is { } hit)
            {
                BeginMeasurementMove(hit, position);
                return;
            }
            ClearSelection();
            Point first = SnapMeasurePoint(pageIndex, position, null, out _);
            BeginMeasurePlacement(pageIndex, first);
            _measurePressed = true;
            _measurePressAt = position;
            return;
        }

        Point anchor = _measurePoints[^1];
        Point next = SnapMeasurePoint(pageIndex, position, anchor, out _);
        switch (kind)
        {
            case MeasurementKind.Distance:
                _measurePoints.Add(next);
                CommitMeasurement();
                return;
            case MeasurementKind.Angle:
                _measurePoints.Add(next);
                if (_measurePoints.Count >= 3) CommitMeasurement();
                else ShowMeasureHint();
                return;
            default:
            {
                int minimum = MeasurementGeometry.MinimumPoints(kind);
                // The first click of a double-click already placed this point.
                if (e.ClickCount >= 2)
                {
                    if (_measurePoints.Count >= minimum) CommitMeasurement();
                    return;
                }
                // A click back on the first point closes the shape: it finishes an area, and
                // finishes a perimeter as a closed loop.
                if (_measurePoints.Count >= 3 && NearFirstMeasurePoint(position))
                {
                    _measureClosedOnFinish = kind == MeasurementKind.Perimeter;
                    CommitMeasurement();
                    return;
                }
                if ((next - anchor).Length > 0.5) _measurePoints.Add(next);
                ShowMeasureHint();
                UpdateMeasurePreview(next, snapped: false);
                return;
            }
        }
    }

    private bool _measureClosedOnFinish;

    /// <summary>A Distance press that was dragged far enough finishes on release.</summary>
    internal bool MeasureMouseUp(MouseButtonEventArgs e)
    {
        if (!_measurePressed) return _measurePoints.Count > 0;
        _measurePressed = false;
        MeasurementKind kind = _measureCalibrating ? MeasurementKind.Distance : _measureMode;
        if (kind != MeasurementKind.Distance || _measurePoints.Count != 1 || _measureCanvas is null) return true;
        Point release = e.GetPosition(_measureCanvas);
        double pixels = (release - _measurePressAt).Length / MeasurementInversePageScale();
        if (pixels < 5) return true;   // a click: the next click places the end
        _measurePoints.Add(SnapMeasurePoint(_measurePage, release, _measurePoints[0], out _));
        CommitMeasurement();
        return true;
    }

    /// <summary>Rubber-bands the segment to the pointer while a measurement is being placed.</summary>
    internal bool MeasureMouseMove(MouseEventArgs e)
    {
        if (_measurePoints.Count == 0 || _measureCanvas is null) return false;
        Point position = e.GetPosition(_measureCanvas);
        position.X = Math.Clamp(position.X, 0, _measureCanvas.ActualWidth);
        position.Y = Math.Clamp(position.Y, 0, _measureCanvas.ActualHeight);
        Point point = SnapMeasurePoint(_measurePage, position, _measurePoints[^1], out bool snapped);
        UpdateMeasurePreview(point, snapped);
        return true;
    }

    /// <summary>Enter: finishes a perimeter or area that has enough points.</summary>
    internal bool FinishMeasurePlacement()
    {
        if (_measurePoints.Count == 0) return false;
        MeasurementKind kind = _measureCalibrating ? MeasurementKind.Distance : _measureMode;
        if (kind is MeasurementKind.Perimeter or MeasurementKind.Area
            && _measurePoints.Count >= MeasurementGeometry.MinimumPoints(kind))
            CommitMeasurement();
        return true;
    }

    /// <summary>Backspace: takes back the last placed point.</summary>
    internal bool RemoveLastMeasurePoint()
    {
        if (_measurePoints.Count == 0) return false;
        _measurePoints.RemoveAt(_measurePoints.Count - 1);
        if (_measurePoints.Count == 0) CancelMeasurePlacement();
        else UpdateMeasurePreview(_measureCursor, snapped: false);
        return true;
    }

    /// <summary>Esc: abandons the measurement being placed (and a calibration line).</summary>
    internal bool CancelMeasurePlacement()
    {
        bool active = _measurePoints.Count > 0 || _measureCalibrating;
        foreach (UIElement? element in new UIElement?[]
                 { _measurePreview, _measureArcPreview, _measureSnapRing, _measureReadout })
            if (element is not null) (VisualTreeHelper.GetParent(element) as Panel)?.Children.Remove(element);
        _measurePreview = null;
        _measureArcPreview = null;
        _measureSnapRing = null;
        _measureReadout = null;
        _measureReadoutText = null;
        _measureGrainLayer = null;
        _measurePoints.Clear();
        _measurePage = -1;
        _measurePressed = false;
        _measureClosedOnFinish = false;
        if (_measureCanvas is not null && Mouse.Captured == _measureCanvas) _measureCanvas.ReleaseMouseCapture();
        _measureCanvas = null;
        if (_measureCalibrating)
        {
            _measureCalibrating = false;
            _calibrationDone = null;
        }
        return active;
    }

    /// <summary>Leaving the Measure tool drops a measurement still being placed.</summary>
    private void ClearMeasurement() => CancelMeasurePlacement();

    private void BeginMeasurePlacement(int pageIndex, Point first)
    {
        _measurePage = pageIndex;
        _measureCanvas = _activeCanvas;
        _measurePoints.Clear();
        _measurePoints.Add(first);
        _measureClosedOnFinish = false;
        Color color = _measureCalibrating ? AccentBrush().Color : DrawMeasureColor();
        var brush = new SolidColorBrush(color);
        double width = _measureCalibrating ? 2 * MeasurementInversePageScale() : Math.Max(1, _drawWidth);
        _measurePreview = new Polyline
        {
            Stroke = brush,
            StrokeThickness = width,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            IsHitTestVisible = false
        };
        if (_measureCalibrating) _measurePreview.StrokeDashArray = [4, 2];
        _measureArcPreview = new Polyline { Stroke = brush, StrokeThickness = width, IsHitTestVisible = false };
        _measureSnapRing = new Ellipse
        {
            Stroke = AccentBrush(),
            Fill = AccentBrush(60),
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed
        };
        BuildMeasureReadout();
        foreach (UIElement element in new UIElement[] { _measurePreview, _measureArcPreview, _measureSnapRing, _measureReadout! })
        {
            Panel.SetZIndex(element, 9500);
            _measureCanvas.Children.Add(element);
        }
        _measureCanvas.CaptureMouse();
        ShowMeasureHint();
        UpdateMeasurePreview(first, snapped: false);
    }

    private void BuildMeasureReadout()
    {
        var content = new Grid();
        _measureGrainLayer = new Border { CornerRadius = UiKit.RadCard, IsHitTestVisible = false };
        _measureGrainLayer.SetResourceReference(Border.BackgroundProperty, "GrainBrushShared");
        _measureGrainLayer.SetResourceReference(UIElement.OpacityProperty, "GrainOpacity");
        content.Children.Add(_measureGrainLayer);
        _measureReadoutText = new TextBlock { FontFamily = UiKit.UiFont, FontSize = 12, Margin = new Thickness(8, 5, 8, 5) };
        _measureReadoutText.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        content.Children.Add(_measureReadoutText);
        _measureReadout = new Border
        {
            BorderBrush = AccentBrush(),
            BorderThickness = new Thickness(1),
            CornerRadius = UiKit.RadCard,
            IsHitTestVisible = false,
            Child = content
        };
        _measureReadout.SetResourceReference(Border.BackgroundProperty, "BgFlyout");
        TextOptions.SetTextFormattingMode(_measureReadout, TextFormattingMode.Ideal);
        TextOptions.SetTextRenderingMode(_measureReadout, TextRenderingMode.Grayscale);
    }

    private void UpdateMeasurePreview(Point cursor, bool snapped)
    {
        if (_measurePreview is null || _measureCanvas is null) return;
        _measureCursor = cursor;
        double inv = MeasurementInversePageScale();
        MeasurementKind kind = _measureCalibrating ? MeasurementKind.Distance : _measureMode;
        var points = new List<Point>(_measurePoints);
        if ((cursor - points[^1]).Length > 0.01) points.Add(cursor);
        bool closing = kind == MeasurementKind.Area && points.Count >= 3;
        _measurePreview.Points = new PointCollection(closing ? [.. points, points[0]] : points);

        var draft = DraftMeasurement(kind, points, closing && kind == MeasurementKind.Perimeter);
        _measureArcPreview!.Points = kind == MeasurementKind.Angle && points.Count >= 3
            ? new PointCollection(MeasurementGeometry.ArcPoints(draft)) : [];

        double ring = 10 * inv;
        _measureSnapRing!.Width = ring;
        _measureSnapRing.Height = ring;
        _measureSnapRing.StrokeThickness = 1.5 * inv;
        _measureSnapRing.Visibility = snapped ? Visibility.Visible : Visibility.Collapsed;
        Canvas.SetLeft(_measureSnapRing, cursor.X - ring / 2);
        Canvas.SetTop(_measureSnapRing, cursor.Y - ring / 2);

        if (_measureReadout is null || _measureReadoutText is null) return;
        _measureReadout.BorderThickness = new Thickness(inv);
        double radius = UiKit.RadCard.TopLeft * inv;
        _measureReadout.CornerRadius = new CornerRadius(radius);
        _measureGrainLayer?.CornerRadius = new CornerRadius(Math.Max(0, radius - inv));
        _measureReadoutText.FontSize = 12 * inv;
        _measureReadoutText.Margin = new Thickness(8 * inv, 5 * inv, 8 * inv, 5 * inv);
        _measureReadoutText.Text = !TryPointsPerCanvasUnit(_measurePage, out double perUnit)
            ? Loc("Str_Measurement_Unavailable")
            : MeasurementGeometry.MinimumPoints(kind) <= points.Count && MeasurementGeometry.IsUsable(draft)
                ? MeasurementGeometry.Label(draft, perUnit, CultureInfo.CurrentCulture)
                : MeasureHintText(kind, _measurePoints.Count);
        _measureReadout.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double edge = 4 * inv, offset = 14 * inv;
        double x = Math.Min(Math.Max(edge, cursor.X + offset),
            Math.Max(edge, _measureCanvas.ActualWidth - _measureReadout.DesiredSize.Width - edge));
        double y = Math.Min(Math.Max(edge, cursor.Y + offset),
            Math.Max(edge, _measureCanvas.ActualHeight - _measureReadout.DesiredSize.Height - edge));
        Canvas.SetLeft(_measureReadout, x);
        Canvas.SetTop(_measureReadout, y);
    }

    private void ShowMeasureHint()
    {
        MeasurementKind kind = _measureCalibrating ? MeasurementKind.Distance : _measureMode;
        SetStatus(_measureCalibrating ? Loc("Str_St_MeasureCalibrate") : MeasureHintText(kind, _measurePoints.Count));
    }

    private string MeasureHintText(MeasurementKind kind, int placed) => kind switch
    {
        MeasurementKind.Distance => Loc("Str_St_MeasureDistanceEnd"),
        MeasurementKind.Angle => Loc(placed <= 1 ? "Str_St_MeasureAngleCorner" : "Str_St_MeasureAngleEnd"),
        MeasurementKind.Area => Loc("Str_St_MeasureAreaNext"),
        _ => Loc("Str_St_MeasurePathNext"),
    };

    private bool NearFirstMeasurePoint(Point position) =>
        _measurePoints.Count > 0
        && (position - _measurePoints[0]).Length <= MeasureCloseSnapPixels * MeasurementInversePageScale();

    private void CommitMeasurement()
    {
        MeasurementKind kind = _measureCalibrating ? MeasurementKind.Distance : _measureMode;
        int page = _measurePage;
        bool closed = _measureClosedOnFinish && kind == MeasurementKind.Perimeter;
        var points = new List<Point>();
        foreach (Point point in _measurePoints)
            if (points.Count == 0 || (point - points[^1]).Length > 0.5) points.Add(point);
        if (kind is MeasurementKind.Area or MeasurementKind.Perimeter && points.Count > 2
            && (points[^1] - points[0]).Length <= 0.5)
            points.RemoveAt(points.Count - 1);

        if (_measureCalibrating)
        {
            Action<double>? done = _calibrationDone;
            double paperPoints = points.Count >= 2 && (points[1] - points[0]).Length > 1
                && TryPointsPerCanvasUnit(page, out double perUnit)
                    ? (points[1] - points[0]).Length * perUnit : 0;
            _calibrationDone = null;
            CancelMeasurePlacement();
            if (paperPoints > 0) done?.Invoke(paperPoints);
            return;
        }

        MeasurementAnnotation measurement = DraftMeasurement(kind, points, closed);
        CancelMeasurePlacement();
        if (page < 0 || !MeasurementGeometry.IsUsable(measurement)) return;
        measurement.PageIndex = page;
        AddAnnotation(measurement);
        RenderAllAnnotations(page);
        SelectAnnotation(measurement, AnnotBounds(measurement));
        if (TryPointsPerCanvasUnit(page, out double unitPoints))
            SetStatus(string.Format(Loc("Str_St_Measured"),
                MeasurementGeometry.Label(measurement, unitPoints, CultureInfo.CurrentCulture)));
    }

    // A measurement with the draw bar's look and the scale that applies where it starts.
    private MeasurementAnnotation DraftMeasurement(MeasurementKind kind, List<Point> points, bool closed)
    {
        var measurement = new MeasurementAnnotation
        {
            PageIndex = _measurePage,
            Kind = kind,
            Points = [.. points],
            Closed = closed,
            StrokeWidth = Math.Max(1, _drawWidth)
        };
        measurement.SetColor(DrawMeasureColor());
        if (TryPointsPerCanvasUnit(_measurePage, out double perUnit) && perUnit > 0)
            measurement.LabelSize = 10 / perUnit;
        ApplyMeasureScale(measurement, _measurePage, points.Count > 0 ? points[0] : default);
        return measurement;
    }

    private Color DrawMeasureColor() => Color.FromArgb(_drawOpacity, _drawColor.R, _drawColor.G, _drawColor.B);

    // ── Scales ───────────────────────────────────────────────────────────────────────────────

    /// <summary>The scale saved in the document for a page point, or null when the page has none.</summary>
    internal PdfMeasurementProfile? PageScaleAt(int pageIndex, Point? canvasPoint = null)
    {
        if (_doc is null || pageIndex < 0) return null;
        PdfMeasurementProfileMap map = PageScales();
        PdfMeasurementPoint? point = null;
        if (canvasPoint is Point canvas
            && TryPageGeometry(pageIndex, out double pw, out double ph, out double cw, out double ch, out int rot))
        {
            var (x, y) = RedactionGeometry.CanvasPointToPage(canvas, pw, ph, cw, ch, rot);
            point = new PdfMeasurementPoint(x, y);
        }
        try { return map.Resolve(pageIndex, point); }
        catch (KeyNotFoundException) { return null; }
    }

    private PdfMeasurementProfileMap PageScales()
    {
        PdfEngineDocumentSession session = EnsureEngineDocumentSession();
        if (_pageScales is not null && ReferenceEquals(_pageScalesSession, session)) return _pageScales;
        try { _pageScales = PdfEngineIntegration.ReadPageScales(session.Document); }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or NotSupportedException
            or ArgumentException)
        {
            _pageScales = new PdfMeasurementProfileMap([]);
        }
        _pageScalesSession = session;
        return _pageScales;
    }

    // Gives a measurement the page's scale in the Measure bar's unit, or actual size in that unit.
    private void ApplyMeasureScale(MeasurementAnnotation measurement, int pageIndex, Point at)
    {
        string unit = MeasureUnit;
        PdfMeasurementProfile? scale = PageScaleAt(pageIndex, at);
        if (scale is null)
        {
            measurement.Unit = unit;
            measurement.UnitsPerPoint = MeasurementUnits.ActualSizeUnitsPerPoint(unit);
            measurement.ScaleName = MeasurementUnits.ActualSizeText(unit);
            measurement.Precision = MeasurementUnits.DefaultPrecision(unit);
            return;
        }
        measurement.ScaleName = scale.Name;
        if (MeasurementUnits.CanConvert(scale.UnitSymbol))
        {
            measurement.Unit = unit;
            measurement.UnitsPerPoint = MeasurementUnits.ConvertScale(scale.UnitsPerPoint, scale.UnitSymbol, unit);
            measurement.Precision = scale.UnitSymbol == unit ? scale.Precision : MeasurementUnits.DefaultPrecision(unit);
        }
        else
        {
            measurement.Unit = scale.UnitSymbol;
            measurement.UnitsPerPoint = scale.UnitsPerPoint;
            measurement.Precision = scale.Precision;
        }
    }

    /// <summary>The page scale as the Measure bar shows it, such as "1 in = 10 ft".</summary>
    internal string MeasureScaleText(int pageIndex) =>
        PageScaleAt(pageIndex)?.Name is { Length: > 0 } name ? name : Loc("Str_Measure_ActualSize");

    /// <summary>The page size in the bar's unit system, such as "8.5 × 11 in" or "210 × 297 mm".</summary>
    internal string MeasurePageSizeText(int pageIndex)
    {
        if (_doc is null) return string.Empty;
        PdfEngineDocumentSession session = EnsureEngineDocumentSession();
        if (pageIndex < 0 || pageIndex >= session.PageCount) return string.Empty;
        var (width, height) = session.VisualPageSize(pageIndex, _pageRotations);
        bool metric = MeasurementUnits.Find(MeasureUnit).Metric;
        string unit = metric ? "mm" : "in";
        double factor = metric ? 25.4 / 72 : 1.0 / 72;
        string format = metric ? "N0" : "0.##";
        var culture = CultureInfo.CurrentCulture;
        return (width * factor).ToString(format, culture) + " × "
            + (height * factor).ToString(format, culture) + " " + unit;
    }

    private Action<double>? _calibrationDone;

    /// <summary>Starts a calibration line; <paramref name="done"/> gets its length in page points.</summary>
    internal void BeginMeasureCalibration(Action<double> done)
    {
        CancelMeasurePlacement();
        ClearSelection();
        _measureCalibrating = true;
        _calibrationDone = done;
        ShowMeasureHint();
    }

    /// <summary>
    /// Saves a scale into the listed pages' viewports through one undoable document step, and
    /// optionally moves the measurements already on those pages onto it.
    /// </summary>
    internal void ApplyPageScale(IReadOnlyList<int> pages, PdfMeasurementProfile? profile, bool updateExisting)
    {
        if (_doc is null || _currentFile is null || pages.Count == 0) return;
        UndoEntry? undo = CaptureSerializedDocumentUndoExt(_currentFile);
        var previousScales = new List<(MeasurementAnnotation Measurement, string Unit, double UnitsPerPoint, string ScaleName, int Precision)>();
        if (updateExisting)
        {
            foreach (int page in pages)
            {
                if (!_annotations.TryGetValue(page, out var list)) continue;
                foreach (MeasurementAnnotation measurement in list.OfType<MeasurementAnnotation>())
                {
                    if (measurement.Kind == MeasurementKind.Angle) continue;
                    previousScales.Add((measurement, measurement.Unit, measurement.UnitsPerPoint,
                        measurement.ScaleName, measurement.Precision));
                    if (profile is null)
                    {
                        measurement.UnitsPerPoint = MeasurementUnits.ActualSizeUnitsPerPoint(measurement.Unit);
                        measurement.ScaleName = MeasurementUnits.ActualSizeText(measurement.Unit);
                    }
                    else
                    {
                        string unit = MeasurementUnits.CanConvert(measurement.Unit) && MeasurementUnits.CanConvert(profile.UnitSymbol)
                            ? measurement.Unit : profile.UnitSymbol;
                        measurement.Unit = unit;
                        measurement.UnitsPerPoint = MeasurementUnits.CanConvert(unit) && MeasurementUnits.CanConvert(profile.UnitSymbol)
                            ? MeasurementUnits.ConvertScale(profile.UnitsPerPoint, profile.UnitSymbol, unit)
                            : profile.UnitsPerPoint;
                        measurement.ScaleName = profile.Name;
                        measurement.Precision = profile.Precision;
                    }
                }
            }
        }
        int[] pageList = [.. pages];
        try
        {
            SaveTempAndReload(keepAnnotations: true, preserveZoom: true,
                finalizeSavedFile: path => PdfEngineIntegration.SetPageScales(path, pageList, profile),
                documentUndo: undo);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            foreach (var previous in previousScales)
            {
                previous.Measurement.Unit = previous.Unit;
                previous.Measurement.UnitsPerPoint = previous.UnitsPerPoint;
                previous.Measurement.ScaleName = previous.ScaleName;
                previous.Measurement.Precision = previous.Precision;
            }
            ReportEditFailure(ex);
            return;
        }
        _pageScales = null;
        RenderAnnotationsOnAllVisiblePages();
    }

    /// <summary>How many measurements sit on the listed pages.</summary>
    internal int CountMeasurements(IEnumerable<int> pages) =>
        pages.Distinct().Sum(page => _annotations.TryGetValue(page, out var list)
            ? list.Count(annotation => annotation is MeasurementAnnotation measurement && measurement.Kind != MeasurementKind.Angle)
            : 0);

    // ── Snapping ─────────────────────────────────────────────────────────────────────────────

    // Alt turns snapping off; Shift locks the step from the anchor to 45 degree angles. Otherwise
    // the point jumps to a nearby corner or end (the drawing's or another measurement's), then to
    // the nearest spot on a nearby line.
    private Point SnapMeasurePoint(int pageIndex, Point position, Point? anchor, out bool snapped)
    {
        snapped = false;
        ModifierKeys modifiers = Keyboard.Modifiers;
        if ((modifiers & ModifierKeys.Shift) != 0 && anchor is Point origin && (position - origin).Length > 0.5)
        {
            PdfMeasurementPoint locked = PdfMeasurement.SnapAngle(
                new PdfMeasurementPoint(origin.X, origin.Y), new PdfMeasurementPoint(position.X, position.Y), 45);
            return new Point(locked.X, locked.Y);
        }
        if ((modifiers & ModifierKeys.Alt) != 0) return position;
        if (!TryPageGeometry(pageIndex, out double pw, out double ph, out double cw, out double ch, out int rot))
            return position;
        double perUnit = RedactionGeometry.PointsPerCanvasUnit(pw, ph, ch, rot);
        if (!(perUnit > 0)) return position;
        double tolerance = MeasureSnapPixels * MeasurementInversePageScale() * perUnit;
        var (px, py) = RedactionGeometry.CanvasPointToPage(position, pw, ph, cw, ch, rot);
        var pagePoint = new PdfMeasurementPoint(px, py);

        var vertices = new List<PdfMeasurementPoint>();
        var segments = new List<PdfMeasurementSegment>();
        if (_annotations.TryGetValue(pageIndex, out var list))
        {
            foreach (MeasurementAnnotation other in list.OfType<MeasurementAnnotation>())
            {
                if (!other.IsVisibleOnScreen) continue;
                PdfMeasurementPoint? previous = null;
                foreach (Point point in other.Points)
                {
                    var (x, y) = RedactionGeometry.CanvasPointToPage(point, pw, ph, cw, ch, rot);
                    var current = new PdfMeasurementPoint(x, y);
                    vertices.Add(current);
                    if (previous is PdfMeasurementPoint start) segments.Add(new PdfMeasurementSegment(start, current));
                    previous = current;
                }
            }
        }
        // The first point of a path being placed, so it can close on itself.
        if (_measurePoints.Count > 1)
        {
            var (x, y) = RedactionGeometry.CanvasPointToPage(_measurePoints[0], pw, ph, cw, ch, rot);
            vertices.Add(new PdfMeasurementPoint(x, y));
        }
        if (MeasureSnapGeometryFor(pageIndex) is { } drawing)
        {
            vertices.AddRange(drawing.Vertices);
            segments.AddRange(drawing.Segments);
        }

        PdfMeasurementPoint result = PdfMeasurement.SnapToNearest(pagePoint, vertices, tolerance);
        if (result == pagePoint) result = PdfMeasurement.SnapToSegments(pagePoint, segments, tolerance);
        if (result == pagePoint) return position;
        snapped = true;
        return RedactionGeometry.PageToCanvas(result.X, result.Y, result.X, result.Y, pw, ph, cw, ch, rot).TopLeft;
    }

    // The drawing's corners and line ends, read once per page in the background. Until it arrives
    // the tool snaps only to measurements.
    private MeasureSnapGeometry? MeasureSnapGeometryFor(int pageIndex)
    {
        if (_currentFile is null) return null;
        if (!string.Equals(_measureSnapFile, _currentFile, StringComparison.OrdinalIgnoreCase))
        {
            _measureSnapCache.Clear();
            _measureSnapLoading.Clear();
            _measureSnapFile = _currentFile;
        }
        if (_measureSnapCache.TryGetValue(pageIndex, out MeasureSnapGeometry? cached)) return cached;
        if (!_measureSnapLoading.Add(pageIndex)) return null;
        string path = _currentFile;
        Services.OpenedProtection protection = _protection;
        Task.Run(() => ReadMeasureSnapGeometry(path, protection, pageIndex)).ContinueWith(task =>
        {
            if (!string.Equals(_measureSnapFile, path, StringComparison.OrdinalIgnoreCase)) return;
            _measureSnapLoading.Remove(pageIndex);
            _measureSnapCache[pageIndex] = task.Status == TaskStatus.RanToCompletion
                ? task.Result : new MeasureSnapGeometry([], []);
        }, TaskScheduler.FromCurrentSynchronizationContext());
        return null;
    }

    private static MeasureSnapGeometry ReadMeasureSnapGeometry(string path, Services.OpenedProtection protection, int pageIndex)
    {
        const int Limit = 20000;
        var vertices = new List<PdfMeasurementPoint>();
        var segments = new List<PdfMeasurementSegment>();
        using var document = KillerPDF.Services.PdfContentDocument.Open(path, protection);
        if (pageIndex >= document.NumberOfPages) return new MeasureSnapGeometry(vertices, segments);
        PdfPageContent page = document.GetPage(pageIndex + 1);
        foreach (PdfExtractedPath drawn in page.Paths)
        {
            if (drawn.IsClippingPath && drawn.PaintOperator == "n") continue;
            PdfMeasurementPoint? current = null, subpathStart = null;
            foreach (PdfExtractedPathSegment segment in drawn.Segments)
            {
                if (vertices.Count >= Limit || segments.Count >= Limit) break;
                switch (segment.Operator)
                {
                    case "m" when segment.Points.Count > 0:
                        current = subpathStart = Measure(segment.Points[^1]);
                        vertices.Add(current.Value);
                        break;
                    case "l" when segment.Points.Count > 0:
                    {
                        PdfMeasurementPoint end = Measure(segment.Points[^1]);
                        if (current is PdfMeasurementPoint start) segments.Add(new PdfMeasurementSegment(start, end));
                        vertices.Add(end);
                        current = end;
                        break;
                    }
                    case "c" or "v" or "y" when segment.Points.Count > 0:
                        current = Measure(segment.Points[^1]);
                        vertices.Add(current.Value);
                        break;
                    case "h":
                        if (current is PdfMeasurementPoint last && subpathStart is PdfMeasurementPoint first && last != first)
                            segments.Add(new PdfMeasurementSegment(last, first));
                        current = subpathStart;
                        break;
                    case "re" when segment.Points.Count == 4:
                        for (int corner = 0; corner < 4; corner++)
                        {
                            PdfMeasurementPoint a = Measure(segment.Points[corner]);
                            vertices.Add(a);
                            segments.Add(new PdfMeasurementSegment(a, Measure(segment.Points[(corner + 1) % 4])));
                        }
                        break;
                }
            }
        }
        return new MeasureSnapGeometry(vertices, segments);

        static PdfMeasurementPoint Measure(KillerPdf.Engine.Authoring.PdfPoint point) => new(point.X, point.Y);
    }

    // ── Showing measurements ─────────────────────────────────────────────────────────────────

    private bool TryPointsPerCanvasUnit(int pageIndex, out double pointsPerUnit)
    {
        pointsPerUnit = 0;
        if (!TryPageGeometry(pageIndex, out double pw, out double ph, out _, out double ch, out int rot)) return false;
        pointsPerUnit = RedactionGeometry.PointsPerCanvasUnit(pw, ph, ch, rot);
        return pointsPerUnit > 0;
    }

    /// <summary>Draws one measurement and its label on the page overlay.</summary>
    private void DrawMeasurementOverlay(MeasurementAnnotation measurement)
    {
        if (!measurement.IsVisibleOnScreen) return;
        if (!MeasurementGeometry.IsUsable(measurement)) return;
        var brush = new SolidColorBrush(measurement.GetColor());
        brush.Freeze();
        bool closed = measurement.Kind == MeasurementKind.Area
            || (measurement.Kind == MeasurementKind.Perimeter && measurement.Closed);
        Shape outline = closed
            ? new Polygon { Points = new PointCollection(measurement.Points) }
            : new Polyline { Points = new PointCollection(measurement.Points) };
        outline.Stroke = brush;
        outline.StrokeThickness = Math.Max(0.5, measurement.StrokeWidth);
        outline.StrokeLineJoin = PenLineJoin.Round;
        outline.StrokeStartLineCap = PenLineCap.Round;
        outline.StrokeEndLineCap = PenLineCap.Round;
        outline.IsHitTestVisible = false;
        _activeCanvas.Children.Add(outline);

        TryPointsPerCanvasUnit(measurement.PageIndex, out double perUnit);
        foreach (var (a, b) in MeasurementGeometry.DistanceTicks(measurement, perUnit))
            _activeCanvas.Children.Add(new Line
            {
                X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y,
                Stroke = brush,
                StrokeThickness = outline.StrokeThickness,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                IsHitTestVisible = false
            });
        if (measurement.Kind == MeasurementKind.Angle)
            _activeCanvas.Children.Add(new Polyline
            {
                Points = new PointCollection(MeasurementGeometry.ArcPoints(measurement)),
                Stroke = brush,
                StrokeThickness = outline.StrokeThickness,
                IsHitTestVisible = false
            });

        if (!(perUnit > 0)) return;
        string label = MeasurementGeometry.Label(measurement, perUnit, CultureInfo.CurrentCulture);
        double size = Math.Max(1, measurement.LabelSize);
        var text = new TextBlock
        {
            Text = label,
            FontFamily = new FontFamily("Arial"),
            FontSize = size,
            Foreground = new SolidColorBrush(Color.FromRgb(measurement.ColorR, measurement.ColorG, measurement.ColorB)),
            LineHeight = size,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight
        };
        TextOptions.SetTextFormattingMode(text, TextFormattingMode.Ideal);
        var box = new Border
        {
            Background = Brushes.White,
            Padding = new Thickness(size * 0.3),
            Opacity = measurement.ColorA / 255.0,
            IsHitTestVisible = false,
            Child = text
        };
        box.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Point center = measurement.LabelCenter ?? MeasurementGeometry.AutoLabelCenter(measurement);
        Size desired = box.DesiredSize;
        measurement.LabelBounds = new Rect(center.X - desired.Width / 2, center.Y - desired.Height / 2,
            desired.Width, desired.Height);
        Canvas.SetLeft(box, measurement.LabelBounds.X);
        Canvas.SetTop(box, measurement.LabelBounds.Y);
        _activeCanvas.Children.Add(box);
    }

    /// <summary>The measurement's whole footprint: its points and its label.</summary>
    internal static Rect MeasurementBounds(MeasurementAnnotation measurement)
    {
        if (measurement.Points.Count == 0) return Rect.Empty;
        double minX = measurement.Points.Min(point => point.X), minY = measurement.Points.Min(point => point.Y);
        double maxX = measurement.Points.Max(point => point.X), maxY = measurement.Points.Max(point => point.Y);
        var bounds = new Rect(new Point(minX, minY), new Point(maxX, maxY));
        if (!measurement.LabelBounds.IsEmpty && measurement.LabelBounds.Width > 0) bounds.Union(measurement.LabelBounds);
        if (bounds.Width < 4) bounds.Inflate((4 - bounds.Width) / 2, 0);
        if (bounds.Height < 4) bounds.Inflate(0, (4 - bounds.Height) / 2);
        return bounds;
    }

    internal static bool HitTestMeasurement(MeasurementAnnotation measurement, Point position)
    {
        if (!measurement.IsVisibleOnScreen) return false;
        var points = measurement.Points;
        if (points.Count == 0) return false;
        if (!measurement.LabelBounds.IsEmpty && measurement.LabelBounds.Contains(position)) return true;
        double tolerance = Math.Max(8, measurement.StrokeWidth / 2 + 6);
        for (int index = 0; index + 1 < points.Count; index++)
            if (DistPointToSegment(position, points[index], points[index + 1]) <= tolerance) return true;
        bool closed = measurement.Kind == MeasurementKind.Area
            || (measurement.Kind == MeasurementKind.Perimeter && measurement.Closed);
        if (closed && points.Count > 2 && DistPointToSegment(position, points[^1], points[0]) <= tolerance)
            return true;
        return measurement.Kind == MeasurementKind.Area && MeasurementGeometry.Contains(points, position);
    }

    internal static void MoveMeasurement(MeasurementAnnotation measurement, Point topLeft)
    {
        Rect bounds = MeasurementBounds(measurement);
        if (bounds.IsEmpty) return;
        Vector delta = topLeft - bounds.Location;
        for (int index = 0; index < measurement.Points.Count; index++) measurement.Points[index] += delta;
        if (measurement.LabelCenter is Point label) measurement.LabelCenter = label + delta;
        if (!measurement.LabelBounds.IsEmpty) measurement.LabelBounds.Offset(delta);
    }

    private MeasurementAnnotation? MeasurementAt(int pageIndex, Point position)
    {
        if (!_annotations.TryGetValue(pageIndex, out var list)) return null;
        for (int index = list.Count - 1; index >= 0; index--)
            if (list[index] is MeasurementAnnotation measurement && HitTestMeasurement(measurement, position))
                return measurement;
        return null;
    }

    private void BeginMeasurementMove(MeasurementAnnotation measurement, Point position)
    {
        ClearSelection();
        RenderAllAnnotations(measurement.PageIndex);
        SelectAnnotation(measurement, AnnotBounds(measurement));
        _dragGroupOrig.Clear();
        _isDraggingAnnot = true;
        _dragAnnotStart = position;
        _dragAnnotOrigPos = AnnotGetPos(measurement);
        _dragAnnot = measurement;
        _activeCanvas.CaptureMouse();
    }

    // ── Editing a placed measurement ─────────────────────────────────────────────────────────

    /// <summary>A round handle on each point and a square one on the label of a selected measurement.</summary>
    private void AddMeasurementHandles(MeasurementAnnotation measurement, Brush brush, double inv)
    {
        _resizeHandles.Clear();
        double size = 12 * inv;
        for (int index = 0; index < measurement.Points.Count; index++)
            _resizeHandles.Add(MeasurementHandle(MeasureVertexTag + index.ToString(CultureInfo.InvariantCulture),
                size, size / 2, brush, inv));
        _resizeHandles.Add(MeasurementHandle(MeasureLabelTag, size * 0.85, 2 * inv, brush, inv));
        foreach (Rectangle handle in _resizeHandles) _activeCanvas.Children.Add(handle);
        LayoutMeasurementHandles(measurement);
    }

    private static Rectangle MeasurementHandle(string tag, double size, double radius, Brush brush, double inv) => new()
    {
        Width = size,
        Height = size,
        RadiusX = radius,
        RadiusY = radius,
        Fill = brush,
        Stroke = Brushes.White,
        StrokeThickness = inv,
        Cursor = Cursors.SizeAll,
        IsHitTestVisible = true,
        Tag = tag
    };

    private void LayoutMeasurementHandles(MeasurementAnnotation measurement)
    {
        Point label = measurement.LabelCenter ?? MeasurementGeometry.AutoLabelCenter(measurement);
        foreach (Rectangle handle in _resizeHandles)
        {
            if (handle.Tag is not string tag) continue;
            Point at;
            if (tag == MeasureLabelTag)
            {
                // The label handle sits on the label's top-left corner so it never hides the value.
                at = measurement.LabelBounds.IsEmpty ? label : measurement.LabelBounds.TopLeft;
            }
            else if (tag.StartsWith(MeasureVertexTag, StringComparison.Ordinal)
                && int.TryParse(tag.AsSpan(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int index)
                && index < measurement.Points.Count)
                at = measurement.Points[index];
            else continue;
            Canvas.SetLeft(handle, at.X - handle.Width / 2);
            Canvas.SetTop(handle, at.Y - handle.Height / 2);
        }
    }

    /// <summary>Starts dragging the point or label handle under the pointer, if any.</summary>
    private bool TryBeginMeasureEdit(MeasurementAnnotation measurement, Point position)
    {
        foreach (Rectangle handle in _resizeHandles)
        {
            if (handle.Tag is not string tag) continue;
            double x = Canvas.GetLeft(handle), y = Canvas.GetTop(handle);
            if (position.X < x || position.X > x + handle.Width || position.Y < y || position.Y > y + handle.Height)
                continue;
            if (tag == MeasureLabelTag) _measureEditHandle = -1;
            else if (int.TryParse(tag.AsSpan(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int index)
                && index < measurement.Points.Count) _measureEditHandle = index;
            else continue;
            _measureEditTarget = measurement;
            _measureEditUndoPushed = false;
            _measureEditing = true;
            _measureEditLabelGrab = position - (measurement.LabelCenter ?? MeasurementGeometry.AutoLabelCenter(measurement));
            _gestureCanvas = _activeCanvas;
            _activeCanvas.CaptureMouse();
            return true;
        }
        return false;
    }

    private Vector _measureEditLabelGrab;

    private bool MeasureEditMove(MouseEventArgs e)
    {
        if (!_measureEditing || _measureEditTarget is not { } measurement) return false;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            FinishMeasureEdit();
            return true;
        }
        Canvas canvas = _gestureCanvas ?? _activeCanvas;
        Point position = e.GetPosition(canvas);
        position.X = Math.Clamp(position.X, 0, canvas.ActualWidth);
        position.Y = Math.Clamp(position.Y, 0, canvas.ActualHeight);
        if (!_measureEditUndoPushed)
        {
            PushPageSnapshotUndo(measurement.PageIndex);
            _measureEditUndoPushed = true;
        }
        if (_measureEditHandle == -1)
            measurement.LabelCenter = position - _measureEditLabelGrab;
        else if (_measureEditHandle >= 0 && _measureEditHandle < measurement.Points.Count)
        {
            Point? anchor = measurement.Points.Count > 1
                ? measurement.Points[_measureEditHandle == 0 ? 1 : _measureEditHandle - 1] : null;
            measurement.Points[_measureEditHandle] = SnapMeasurePointExcluding(measurement, position, anchor);
        }
        RenderAllAnnotations(measurement.PageIndex);
        Rect bounds = AnnotBounds(measurement);
        if (_selectionBorder is not null)
        {
            _selectionBorder.Width = bounds.Width + 8;
            _selectionBorder.Height = bounds.Height + 8;
            Canvas.SetLeft(_selectionBorder, bounds.X - 4);
            Canvas.SetTop(_selectionBorder, bounds.Y - 4);
        }
        LayoutMeasurementHandles(measurement);
        ReattachSelectionVisuals();
        return true;
    }

    // Snapping while a point is dragged, ignoring the point's own position.
    private Point SnapMeasurePointExcluding(MeasurementAnnotation measurement, Point position, Point? anchor)
    {
        List<Point> original = measurement.Points;
        int dragged = _measureEditHandle;
        measurement.Points = [.. original.Where((_, index) => index != dragged)];
        try { return SnapMeasurePoint(measurement.PageIndex, position, anchor, out _); }
        finally { measurement.Points = original; }
    }

    private void FinishMeasureEdit()
    {
        if (!_measureEditing) return;
        _measureEditing = false;
        (_gestureCanvas ?? _activeCanvas)?.ReleaseMouseCapture();
        if (_measureEditTarget is { } measurement)
        {
            RenderAllAnnotations(measurement.PageIndex);
            SelectAnnotation(measurement, AnnotBounds(measurement));
            if (_measureEditUndoPushed) MarkDirty();
        }
        _measureEditTarget = null;
        _measureEditHandle = -2;
    }

    // ── Opening, saving and reporting ────────────────────────────────────────────────────────

    /// <summary>
    /// Turns the measurements read from a newly opened file into overlay measurements. They load
    /// as part of the document, so they make no undo step and leave it clean.
    /// </summary>
    internal void LoadMeasurements(IReadOnlyList<PdfEngineIntegration.MeasurementData> measurements)
    {
        if (_doc is null || measurements.Count == 0) return;
        PdfEngineDocumentSession session = EnsureEngineDocumentSession();
        foreach (PdfEngineIntegration.MeasurementData data in measurements)
        {
            if (data.PageIndex < 0 || data.PageIndex >= session.PageCount) continue;
            EnsureRenderDims(data.PageIndex, session.Pages[data.PageIndex].Width, session.Pages[data.PageIndex].Height);
            if (!TryPageGeometry(data.PageIndex, out double pw, out double ph, out double cw, out double ch, out int rot))
                continue;
            double perUnit = RedactionGeometry.PointsPerCanvasUnit(pw, ph, ch, rot);
            if (!(perUnit > 0)) continue;
            Point Canvas(KillerPdf.Engine.Authoring.PdfPoint point) =>
                RedactionGeometry.PageToCanvas(point.X, point.Y, point.X, point.Y, pw, ph, cw, ch, rot).TopLeft;
            var measurement = new MeasurementAnnotation
            {
                PageIndex = data.PageIndex,
                AnnotationFlags = data.AnnotationFlags,
                Kind = (MeasurementKind)(int)data.Kind,
                Points = [.. data.Points.Select(Canvas)],
                Closed = data.Closed,
                StrokeWidth = Math.Max(0.5, data.LineWidth / perUnit),
                LabelSize = (data.LabelSize ?? 10) / perUnit,
                LabelCenter = data.LabelCenter is { } center ? Canvas(center) : null
            };
            measurement.SetColor(Color.FromArgb((byte)Math.Round(Math.Clamp(data.Opacity, 0, 1) * 255),
                ToByte(data.Color.Red), ToByte(data.Color.Green), ToByte(data.Color.Blue)));
            PdfMeasurementProfile? profile = data.Profile ?? PageScaleAt(data.PageIndex);
            if (profile is null && measurement.Kind != MeasurementKind.Angle)
            {
                string unit = MeasureUnit;
                measurement.Unit = unit;
                measurement.UnitsPerPoint = MeasurementUnits.ActualSizeUnitsPerPoint(unit);
                measurement.ScaleName = MeasurementUnits.ActualSizeText(unit);
                measurement.Precision = MeasurementUnits.DefaultPrecision(unit);
            }
            else if (profile is not null)
            {
                measurement.Unit = profile.UnitSymbol;
                measurement.UnitsPerPoint = profile.UnitsPerPoint;
                measurement.ScaleName = profile.Name;
                measurement.Precision = profile.Precision;
            }
            if (!MeasurementGeometry.IsUsable(measurement)) continue;
            if (!_annotations.TryGetValue(data.PageIndex, out var list)) _annotations[data.PageIndex] = list = [];
            list.Add(measurement);
        }
    }

    /// <summary>The document's measurements in crop-relative page points, for the save to write.</summary>
    internal List<PdfEngineIntegration.MeasurementData> MeasurementsForSave()
    {
        var result = new List<PdfEngineIntegration.MeasurementData>();
        if (_doc is null) return result;
        foreach (var (pageIndex, list) in _annotations)
        {
            foreach (MeasurementAnnotation measurement in list.OfType<MeasurementAnnotation>())
            {
                if (!MeasurementGeometry.IsUsable(measurement)) continue;
                if (!TryPageGeometry(pageIndex, out double pw, out double ph, out double cw, out double ch, out int rot))
                    continue;
                double perUnit = RedactionGeometry.PointsPerCanvasUnit(pw, ph, ch, rot);
                if (!(perUnit > 0)) continue;
                KillerPdf.Engine.Authoring.PdfPoint Page(Point point)
                {
                    var (x, y) = RedactionGeometry.CanvasPointToPage(point, pw, ph, cw, ch, rot);
                    return new KillerPdf.Engine.Authoring.PdfPoint(x, y);
                }
                // The label point is saved wherever it shows, so other viewers put it in the same spot.
                Point label = measurement.LabelCenter ?? MeasurementGeometry.AutoLabelCenter(measurement);
                PdfMeasurementProfile? profile = measurement.Kind == MeasurementKind.Angle ? null
                    : new PdfMeasurementProfile(string.IsNullOrWhiteSpace(measurement.ScaleName)
                            ? MeasurementUnits.ActualSizeText(measurement.Unit) : measurement.ScaleName,
                        measurement.UnitsPerPoint > 0 ? measurement.UnitsPerPoint : MeasurementUnits.ActualSizeUnitsPerPoint("in"),
                        string.IsNullOrWhiteSpace(measurement.Unit) ? "in" : measurement.Unit,
                        Math.Clamp(measurement.Precision, 0, 12));
                result.Add(new PdfEngineIntegration.MeasurementData(pageIndex,
                    (PdfMeasurementKind)(int)measurement.Kind, [.. measurement.Points.Select(Page)],
                    measurement.Closed, profile,
                    MeasurementGeometry.Label(measurement, perUnit, CultureInfo.CurrentCulture),
                    new KillerPdf.Engine.Authoring.PdfRgbColor(measurement.ColorR / 255.0, measurement.ColorG / 255.0,
                        measurement.ColorB / 255.0),
                    Math.Max(0.1, measurement.StrokeWidth * perUnit), measurement.ColorA / 255.0, Page(label),
                    Math.Clamp(measurement.LabelSize * perUnit, 1, 144),
                    measurement.Kind == MeasurementKind.Angle ? MeasurementGeometry.ArcRadius(measurement) * perUnit : null,
                    measurement.AnnotationFlags));
            }
        }
        return result;
    }

    /// <summary>Writes the document's measurements into the file being saved.</summary>
    internal void WriteMeasurementsToDocument(string path) =>
        PdfEngineIntegration.WriteMeasurements(path, MeasurementsForSave());

    /// <summary>The report rows: page, kind, value text and scale, in page order.</summary>
    internal List<PdfMeasurementResult> MeasurementResults(string? documentName)
    {
        var results = new List<PdfMeasurementResult>();
        foreach (var (pageIndex, list) in _annotations.OrderBy(pair => pair.Key))
        {
            if (!TryPointsPerCanvasUnit(pageIndex, out double perUnit)) continue;
            TryPageGeometry(pageIndex, out double pw, out double ph, out double cw, out double ch, out int rot);
            foreach (MeasurementAnnotation measurement in list.OfType<MeasurementAnnotation>())
            {
                if (!MeasurementGeometry.IsUsable(measurement)) continue;
                bool angle = measurement.Kind == MeasurementKind.Angle;
                results.Add(new PdfMeasurementResult
                {
                    Document = documentName,
                    PageIndex = pageIndex,
                    Kind = MeasureKindName(measurement.Kind),
                    Label = MeasurementGeometry.Label(measurement, perUnit, CultureInfo.CurrentCulture),
                    Value = MeasurementGeometry.Value(measurement, perUnit),
                    Unit = angle ? "deg"
                        : measurement.Kind == MeasurementKind.Area
                            ? "sq " + (measurement.Unit == MeasurementUnits.FeetAndInches ? "ft" : MeasurementUnits.Symbol(measurement.Unit))
                            : measurement.Unit == MeasurementUnits.FeetAndInches ? "ft" : MeasurementUnits.Symbol(measurement.Unit),
                    Profile = angle ? "-" : measurement.ScaleName,
                    UnitsPerPoint = angle ? null : measurement.UnitsPerPoint,
                    Points = [.. measurement.Points.Select(point =>
                    {
                        var (x, y) = RedactionGeometry.CanvasPointToPage(point, pw, ph, cw, ch, rot);
                        return new PdfMeasurementPoint(x, y);
                    })]
                });
            }
        }
        return results;
    }

    internal string MeasureKindName(MeasurementKind kind) => kind switch
    {
        MeasurementKind.Perimeter => Loc("Str_Measure_Perimeter"),
        MeasurementKind.Area => Loc("Str_Measure_Area"),
        MeasurementKind.Angle => Loc("Str_Measure_Angle"),
        _ => Loc("Str_Measure_Distance"),
    };

    // ── Shared page-scale helper ─────────────────────────────────────────────────────────────

    private double MeasurementInversePageScale()
    {
        double scale = 1.0;
        DependencyObject? current = _activeCanvas;
        while (current is not null && !ReferenceEquals(current, this))
        {
            if (current is FrameworkElement element &&
                element.LayoutTransform is ScaleTransform layoutScale &&
                layoutScale.ScaleX > 0.0001)
                scale *= layoutScale.ScaleX;
            if (current is UIElement visual &&
                visual.RenderTransform is ScaleTransform renderScale &&
                renderScale.ScaleX > 0.0001)
                scale *= renderScale.ScaleX;
            current = VisualTreeHelper.GetParent(current);
        }
        return scale > 0.0001 ? 1.0 / scale : 1.0;
    }
}
