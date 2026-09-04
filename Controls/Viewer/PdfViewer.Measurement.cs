using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using KillerPDF.Services;
using KillerPdf.Engine.Documents;

namespace KillerPDF.Controls;

public partial class PdfViewer
{
    private Line? _measurementLine;
    private Line? _measurementStartCap;
    private Line? _measurementEndCap;
    private Border? _measurementReadout;
    private TextBlock? _measurementReadoutText;
    private Border? _measurementGrainLayer;
    private int _measurementPage = -1;
    private PdfPageInformation? _measurementPageInfo;
    private (int w, int h) _measurementRenderSize;

    private void BeginMeasurement(int pageIndex, Point start)
    {
        ClearMeasurement();
        _measurementPage = pageIndex;
        if (_currentFile is not null)
        {
            try
            {
                var pages = PdfEngineIntegration.ReadPageInformation(_currentFile);
                if ((uint)pageIndex < (uint)pages.Count && _renderDims.TryGetValue(pageIndex, out var render))
                {
                    _measurementPageInfo = pages[pageIndex];
                    _measurementRenderSize = render;
                }
            }
            catch
            {
                _measurementPageInfo = null;
            }
        }
        _drawStart = start;
        _isDrawing = true;

        Brush accent = AccentBrush();
        _measurementLine = new Line
        {
            X1 = start.X, Y1 = start.Y, X2 = start.X, Y2 = start.Y,
            Stroke = accent, StrokeThickness = 2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            IsHitTestVisible = false
        };
        _measurementStartCap = MeasurementCap(accent);
        _measurementEndCap = MeasurementCap(accent);
        var readoutContent = new Grid();
        _measurementGrainLayer = new Border
        {
            CornerRadius = UiKit.RadCard,
            IsHitTestVisible = false
        };
        _measurementGrainLayer.SetResourceReference(Border.BackgroundProperty, "GrainBrushShared");
        _measurementGrainLayer.SetResourceReference(UIElement.OpacityProperty, "GrainOpacity");
        readoutContent.Children.Add(_measurementGrainLayer);
        _measurementReadoutText = new TextBlock
        {
            FontFamily = UiKit.UiFont,
            FontSize = 12,
            Margin = new Thickness(8, 5, 8, 5)
        };
        _measurementReadoutText.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        readoutContent.Children.Add(_measurementReadoutText);
        _measurementReadout = new Border
        {
            BorderBrush = accent,
            BorderThickness = new Thickness(1),
            CornerRadius = UiKit.RadCard,
            IsHitTestVisible = false,
            Child = readoutContent
        };
        _measurementReadout.SetResourceReference(Border.BackgroundProperty, "BgFlyout");
        TextOptions.SetTextFormattingMode(_measurementReadout, TextFormattingMode.Ideal);
        TextOptions.SetTextRenderingMode(_measurementReadout, TextRenderingMode.Grayscale);

        foreach (UIElement element in new UIElement[]
                 { _measurementLine, _measurementStartCap, _measurementEndCap, _measurementReadout })
        {
            Panel.SetZIndex(element, 9500);
            _activeCanvas.Children.Add(element);
        }
        _activePreview = _measurementLine;
        UpdateMeasurement(start);
        _activeCanvas.CaptureMouse();
    }

    private static Line MeasurementCap(Brush brush) => new()
    {
        Stroke = brush,
        StrokeThickness = 2,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        IsHitTestVisible = false
    };

    private void UpdateMeasurement(Point end)
    {
        if (_measurementLine is null || _measurementStartCap is null ||
            _measurementEndCap is null || _measurementReadout is null ||
            _measurementPage < 0 || _currentFile is null) return;

        double inv = MeasurementInversePageScale();
        _measurementLine.X2 = end.X;
        _measurementLine.Y2 = end.Y;
        _measurementLine.StrokeThickness = 2 * inv;
        _measurementStartCap.StrokeThickness = 2 * inv;
        _measurementEndCap.StrokeThickness = 2 * inv;
        _measurementReadout.BorderThickness = new Thickness(inv);
        double radius = UiKit.RadCard.TopLeft * inv;
        _measurementReadout.CornerRadius = new CornerRadius(radius);
        _measurementGrainLayer?.CornerRadius = new CornerRadius(Math.Max(0, radius - inv));
        if (_measurementReadoutText is not null)
        {
            _measurementReadoutText.FontSize = 12 * inv;
            _measurementReadoutText.Margin = new Thickness(8 * inv, 5 * inv, 8 * inv, 5 * inv);
        }
        Vector direction = end - _drawStart;
        double length = direction.Length;
        Vector normal = length > 0.001
            ? new Vector(-direction.Y / length * 6 * inv, direction.X / length * 6 * inv)
            : new Vector(0, 6 * inv);
        PositionCap(_measurementStartCap, _drawStart, normal);
        PositionCap(_measurementEndCap, end, normal);

        string text = MeasurementText(direction);
        _measurementReadoutText?.Text = text;
        _measurementReadout.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double edge = 4 * inv;
        double offset = 12 * inv;
        double x = Math.Min(Math.Max(edge, end.X + offset),
            Math.Max(edge, _activeCanvas.ActualWidth - _measurementReadout.DesiredSize.Width - edge));
        double y = Math.Min(Math.Max(edge, end.Y + offset),
            Math.Max(edge, _activeCanvas.ActualHeight - _measurementReadout.DesiredSize.Height - edge));
        Canvas.SetLeft(_measurementReadout, x);
        Canvas.SetTop(_measurementReadout, y);
    }

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

    private static void PositionCap(Line cap, Point center, Vector normal)
    {
        cap.X1 = center.X - normal.X;
        cap.Y1 = center.Y - normal.Y;
        cap.X2 = center.X + normal.X;
        cap.Y2 = center.Y + normal.Y;
    }

    private string MeasurementText(Vector canvasDelta)
    {
        if (_measurementPageInfo is null || _measurementRenderSize.w <= 0 || _measurementRenderSize.h <= 0)
            return Loc("Str_Measurement_Unavailable");

        MeasurementValues value = MeasurementCalculator.Calculate(
            _measurementPageInfo.Width, _measurementPageInfo.Height,
            _measurementPageInfo.Rotation, _measurementRenderSize.w, _measurementRenderSize.h,
            canvasDelta.X, canvasDelta.Y);
        return string.Create(CultureInfo.CurrentCulture,
            $"{value.Inches:0.###} in  |  {value.Millimetres:0.##} mm  |  {value.Points:0.##} pt\n" +
            $"Page {value.PageWidthPoints / 72.0:0.##} × {value.PageHeightPoints / 72.0:0.##} in  |  " +
            $"{value.PageWidthPoints / 72.0 * 25.4:0.#} × {value.PageHeightPoints / 72.0 * 25.4:0.#} mm");
    }

    private void FinishMeasurement(Point end)
    {
        UpdateMeasurement(end);
        _isDrawing = false;
        _activePreview = null;
        _activeCanvas.ReleaseMouseCapture();
    }

    private void ClearMeasurement()
    {
        foreach (UIElement? element in new UIElement?[]
                 { _measurementLine, _measurementStartCap, _measurementEndCap, _measurementReadout })
            if (element is not null)
                (VisualTreeHelper.GetParent(element) as Panel)?.Children.Remove(element);
        _measurementLine = null;
        _measurementStartCap = null;
        _measurementEndCap = null;
        _measurementReadout = null;
        _measurementReadoutText = null;
        _measurementGrainLayer = null;
        _measurementPage = -1;
        _measurementPageInfo = null;
        _measurementRenderSize = default;
    }
}
