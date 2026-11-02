using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using KillerPDF.Services;

namespace KillerPDF.Shell
{
    public partial class MainWindow
    {
        // ============================================================
        // Measure bar: the measurement type, the page scale, the units, the page size and the
        // report, ahead of the draw bar's color, size and opacity that style each measurement.
        // ============================================================

        private Button? _measureScaleButton;
        private TextBlock? _measurePageSizeText;
        private MeasureScaleDialog.DialogState? _measureScaleState;

        /// <summary>Builds the two Measure groups: type, scale and units; then page size and report.</summary>
        private (StackPanel Main, StackPanel Info) BuildMeasureGroups()
        {
            var main = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 2, 16, 2) };
            main.Children.Add(MeasureModeButton(MeasurementKind.Distance, "Str_Measure_TT_Distance", DistanceGlyph()));
            main.Children.Add(MeasureModeButton(MeasurementKind.Perimeter, "Str_Measure_TT_Perimeter", PerimeterGlyph()));
            main.Children.Add(MeasureModeButton(MeasurementKind.Area, "Str_Measure_TT_Area", AreaGlyph()));
            main.Children.Add(MeasureModeButton(MeasurementKind.Angle, "Str_Measure_TT_Angle", AngleGlyph()));

            _measureScaleButton = MeasureBarButton("", "Str_Measure_TT_Scale", () => OpenMeasureScale());
            _measureScaleButton.ToolTip = ShortcutTip("Str_Measure_TT_Scale", "AltShift:D5");
            _measureScaleButton.Margin = new Thickness(10, 0, 6, 0);
            main.Children.Add(_measureScaleButton);

            var units = new ComboBox
            {
                Width = 132,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = Loc("Str_Measure_TT_Units")
            };
            if (UiKit.DarkComboStyle is Style comboStyle) units.Style = comboStyle;
            System.Windows.Automation.AutomationProperties.SetName(units, Loc("Str_Measure_TT_Units"));
            string current = ActiveViewer.MeasureUnit;
            foreach (MeasureUnit unit in MeasurementUnits.Offered)
            {
                var item = new ComboBoxItem { Content = MeasureScaleDialog.UnitName(unit.Id), Tag = unit.Id };
                units.Items.Add(item);
                if (unit.Id == current) units.SelectedItem = item;
            }
            units.SelectionChanged += (_, _) =>
            {
                if (units.SelectedItem is ComboBoxItem { Tag: string id })
                {
                    ActiveViewer.MeasureUnit = id;
                    RefreshMeasureBar();
                }
            };
            main.Children.Add(units);

            var info = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 2, 16, 2) };
            _measurePageSizeText = new TextBlock
            {
                FontFamily = UiKit.UiFont,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
                ToolTip = Loc("Str_Measure_TT_PageSize")
            };
            _measurePageSizeText.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            info.Children.Add(_measurePageSizeText);
            var report = MeasureBarButton(Loc("Str_Measure_Report"), "Str_Measure_TT_Report", ShowMeasurementReport);
            report.ToolTip = Loc("Str_Measure_TT_Report") + " (" + GestureFor("CtrlShift:D") + ", Y)";
            info.Children.Add(report);
            RefreshMeasureBar();
            return (main, info);
        }

        /// <summary>Updates the scale and page size readouts for the page in view.</summary>
        private void RefreshMeasureBar()
        {
            if (_doc is null) return;
            int page = Math.Max(0, _currentPage);
            if (_measureScaleButton is not null)
                _measureScaleButton.Content = string.Format(Loc("Str_Measure_Scale"), ActiveViewer.MeasureScaleText(page));
            _measurePageSizeText?.Text = string.Format(Loc("Str_Measure_PageSize"), ActiveViewer.MeasurePageSizeText(page));
        }

        private Button MeasureBarButton(string text, string tipKey, Action click)
        {
            var button = UiKit.Make(text, false);
            button.Padding = new Thickness(10, 3, 10, 3);
            button.Margin = new Thickness(0, 0, 6, 0);
            button.VerticalAlignment = VerticalAlignment.Center;
            button.ToolTip = Loc(tipKey);
            button.Click += (_, _) => click();
            return button;
        }

        // Radio-style type buttons whose glyphs are drawn shapes, like the Shapes tool's picker.
        private Border MeasureModeButton(MeasurementKind kind, string tipKey, FrameworkElement glyph)
        {
            bool active = ActiveViewer.MeasureMode == kind;
            foreach (Shape shape in GlyphShapes(glyph))
                shape.SetResourceReference(Shape.StrokeProperty, active ? "SelectionAccent" : "MutedTextBrush");
            var button = new Border
            {
                Width = 28,
                Height = 22,
                CornerRadius = new CornerRadius(3),
                BorderThickness = new Thickness(active ? 1.5 : 1),
                Background = Brushes.Transparent,
                Margin = new Thickness(1, 0, 1, 0),
                Cursor = Cursors.Hand,
                ToolTip = ShortcutTip(tipKey, "AltShift:D" + ((int)kind + 1)),
                Child = glyph
            };
            System.Windows.Automation.AutomationProperties.SetName(button, Loc(tipKey));
            if (active) button.SetResourceReference(Border.BorderBrushProperty, "SelectionAccent");
            else button.BorderBrush = _swatchDimBorder;
            button.MouseLeftButtonDown += (_, _) =>
            {
                if (ActiveViewer.MeasureMode == kind) return;
                ActiveViewer.MeasureMode = kind;
                ShowDrawSettings(EditTool.Measure);
            };
            return button;
        }

        private static IEnumerable<Shape> GlyphShapes(FrameworkElement glyph) => glyph switch
        {
            Shape shape => [shape],
            Panel panel => panel.Children.OfType<Shape>(),
            _ => [],
        };

        private static Canvas GlyphCanvas(params Shape[] shapes)
        {
            var canvas = new Canvas { Width = 16, Height = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            foreach (Shape shape in shapes)
            {
                shape.StrokeThickness = 1.4;
                shape.StrokeStartLineCap = PenLineCap.Round;
                shape.StrokeEndLineCap = PenLineCap.Round;
                shape.StrokeLineJoin = PenLineJoin.Round;
                shape.Fill = Brushes.Transparent;
                canvas.Children.Add(shape);
            }
            return canvas;
        }

        private static Canvas DistanceGlyph() => GlyphCanvas(
            new Line { X1 = 1, Y1 = 6, X2 = 15, Y2 = 6 },
            new Line { X1 = 1, Y1 = 2.5, X2 = 1, Y2 = 9.5 },
            new Line { X1 = 15, Y1 = 2.5, X2 = 15, Y2 = 9.5 });

        private static Canvas PerimeterGlyph() => GlyphCanvas(
            new Polyline { Points = [new Point(1, 10), new Point(5, 2), new Point(10, 7), new Point(15, 2)] });

        private static Canvas AreaGlyph() => GlyphCanvas(
            new Polygon { Points = [new Point(2, 10), new Point(4, 2), new Point(13, 3), new Point(15, 10)] });

        private static Canvas AngleGlyph() => GlyphCanvas(
            new Polyline { Points = [new Point(15, 2), new Point(2, 11), new Point(15, 11)] },
            new Path { Data = Geometry.Parse("M 8,11 A 6,6 0 0 0 7.2,7.6") });

        /// <summary>Shift+M: steps to the next measurement type and arms the Measure tool.</summary>
        private void CycleMeasureMode()
        {
            ActiveViewer.CycleMeasureMode();
            if (_currentTool == EditTool.Measure) ShowDrawSettings(EditTool.Measure);
            else SetTool(EditTool.Measure);
        }

        /// <summary>
        /// Set Scale for the page in view. Choosing to draw a calibration line closes the dialog,
        /// arms the Measure tool for one line, and reopens the dialog with that line's length.
        /// </summary>
        private void OpenMeasureScale(double? linePoints = null)
        {
            if (_doc is null) return;
            if (IsOwnerRestricted && !UnlockOwnerRestrictions()) return;
            var viewer = ActiveViewer;
            int page = Math.Max(0, _currentPage);
            int pageCount = EnsureEngineDocumentSession().PageCount;
            var dialog = new MeasureScaleDialog(this, viewer.PageScaleAt(page), viewer.MeasureUnit, linePoints,
                linePoints is null ? null : _measureScaleState,
                viewer.CountMeasurements([page]), viewer.CountMeasurements(Enumerable.Range(0, pageCount)));
            bool accepted = dialog.ShowDialog() == true;
            _measureScaleState = dialog.State;
            if (dialog.RequestCalibration)
            {
                if (_currentTool != EditTool.Measure) SetTool(EditTool.Measure);
                viewer.BeginMeasureCalibration(points => OpenMeasureScale(points));
                return;
            }
            if (!accepted) return;
            IReadOnlyList<int> pages = dialog.AllPages ? [.. Enumerable.Range(0, pageCount)] : [page];
            viewer.ApplyPageScale(pages, dialog.Profile, dialog.UpdateExisting);
            if (dialog.Profile is { } profile && MeasurementUnits.CanConvert(profile.UnitSymbol))
                viewer.MeasureUnit = profile.UnitSymbol;
            if (_currentTool == EditTool.Measure || _annotBarTool == EditTool.Measure) ShowDrawSettings(EditTool.Measure);
            string scale = dialog.Profile?.Name ?? Loc("Str_Measure_ActualSize");
            SetStatus(pages.Count == 1
                ? string.Format(Loc("Str_St_ScaleSetOne"), scale)
                : string.Format(Loc("Str_St_ScaleSetMany"), scale, pages.Count));
        }

        /// <summary>The Measurements report for the active document.</summary>
        private void ShowMeasurementReport()
        {
            if (_doc is null) { SetStatus(Loc("Str_Msg_OpenFirst")); return; }
            string name = System.IO.Path.GetFileNameWithoutExtension(_originalFile ?? _currentFile ?? "document");
            new MeasurementReportDialog(this, ActiveViewer.MeasurementResults(name), name,
                () => EnsureMayWrite(Services.PdfOutput.Data)).ShowDialog();
        }
    }
}
