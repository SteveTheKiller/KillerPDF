using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Microsoft.Win32;
using KillerPDF.Services;

namespace KillerPDF.Controls
{
    // Moved from Shell/Forms.cs; the namespace and class line are the only changes. Window members
    // spelled bare here resolve through PdfViewer.Bridge.cs.
    public partial class PdfViewer
    {
        private sealed record FormChoiceItem(string ExportValue, string DisplayValue)
        {
            public override string ToString() => DisplayValue;
        }

        private FrameworkElement? _formDragControl;
        private Canvas? _formDragCanvas;
        private FormFieldInfo _formDragField;
        private Point _formDragStart;
        private Point _formDragOrigin;
        private Size _formDragSize;
        private bool _formDragIsResize;
        private bool _formDragMoved;
        private string? _selectedFormFieldName;

        private const double FormResizeGripSize = 14;
        private static readonly DependencyProperty FormFillCursorProperty =
            DependencyProperty.RegisterAttached(
                "FormFillCursor", typeof(Cursor), typeof(PdfViewer));

        private sealed class CombTextBox : Grid
        {
            private readonly Canvas _characters = new() { IsHitTestVisible = false };
            private readonly int _cellCount;
            private readonly double _fontSize;

            internal TextBox Editor { get; }

            internal CombTextBox(TextBox editor, int cellCount, double fontSize)
            {
                Editor = editor;
                _cellCount = cellCount;
                _fontSize = fontSize;
                Width = editor.Width;
                Height = editor.Height;
                Background = Brushes.Transparent;
                Editor.Width = double.NaN;
                Editor.Height = double.NaN;
                Editor.Foreground = Brushes.Transparent;
                Children.Add(Editor);
                Children.Add(_characters);
                Editor.TextChanged += (_, _) => RefreshCharacters();
                Editor.PreviewMouseLeftButtonDown += (_, e) =>
                {
                    int cell = CombFieldLayout.CellIndexAt(
                        e.GetPosition(Editor).X, Math.Max(1, ActualWidth), _cellCount);
                    Editor.Focus();
                    Editor.CaretIndex = Math.Min(cell, Editor.Text.Length);
                    e.Handled = true;
                };
                SizeChanged += (_, _) => RefreshCharacters();
                RefreshCharacters();
            }

            private void RefreshCharacters()
            {
                _characters.Children.Clear();
                if (ActualWidth <= 0 || ActualHeight <= 0) return;
                double cellWidth = ActualWidth / _cellCount;
                int count = Math.Min(Editor.Text.Length, _cellCount);
                for (int index = 0; index < count; index++)
                {
                    var character = new TextBlock
                    {
                        Text = Editor.Text[index].ToString(),
                        Width = cellWidth,
                        Height = ActualHeight,
                        FontFamily = new FontFamily("Consolas"),
                        FontSize = _fontSize,
                        Foreground = Brushes.Black,
                        TextAlignment = TextAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    Canvas.SetLeft(character,
                        CombFieldLayout.CellLeft(ActualWidth, _cellCount, index));
                    Canvas.SetTop(character, Math.Max(0, (ActualHeight - _fontSize * 1.25) / 2));
                    _characters.Children.Add(character);
                }
            }
        }

        internal void RefreshFormDesignMode()
        {
            bool designMode = _currentTool == EditTool.FormField;
            foreach (Canvas canvas in AllPageCanvases())
            foreach (FrameworkElement element in canvas.Children
                         .OfType<FrameworkElement>()
                         .Where(child => child.Tag as string == FormOverlayTag))
            {
                element.Cursor = designMode
                    ? Cursors.SizeAll
                    : (Cursor?)element.GetValue(FormFillCursorProperty);
                element.ForceCursor = designMode;
            }
        }

        private void AttachFormDesignDrag(
            UIElement control, FormFieldInfo field, int pageIndex, Canvas canvas)
        {
            if (control is not FrameworkElement element) return;
            element.Tag = FormOverlayTag;
            element.SetValue(FormFillCursorProperty, element.Cursor);
            if (_currentTool == EditTool.FormField && field.ObjNum > 0)
            {
                element.Cursor = Cursors.SizeAll;
                element.ForceCursor = true;
            }

            element.PreviewMouseLeftButtonDown += (_, e) =>
            {
                bool selectTextField = _currentTool == EditTool.Select
                    && field.FieldType == "/Tx";
                if ((_currentTool != EditTool.FormField && !selectTextField)
                    || field.ObjNum <= 0) return;
                _selectedFormFieldName = field.FieldName;
                if (selectTextField && e.ClickCount > 1) return;
                Point local = e.GetPosition(element);
                bool resize = local.X >= element.ActualWidth - FormResizeGripSize
                    && local.Y >= element.ActualHeight - FormResizeGripSize;
                _formDragControl = element;
                _formDragCanvas = canvas;
                _formDragField = field;
                _formDragStart = e.GetPosition(canvas);
                _formDragOrigin = new Point(Canvas.GetLeft(element), Canvas.GetTop(element));
                _formDragSize = new Size(element.ActualWidth, element.ActualHeight);
                _formDragIsResize = resize;
                _formDragMoved = false;
                HideFormSizeBar();
                Keyboard.ClearFocus();
                Window.GetWindow(element)?.Focus();
                element.CaptureMouse();
                Panel.SetZIndex(element, 30);
                e.Handled = true;
            };
            element.PreviewMouseRightButtonDown += (_, e) =>
            {
                if (_currentTool != EditTool.FormField || field.FieldType != "/Tx") return;
                e.Handled = true;
                OpenFormFieldColorPicker(field, pageIndex);
            };
            element.PreviewMouseMove += (_, e) =>
            {
                if (!ReferenceEquals(_formDragControl, element))
                {
                    bool selectTextField = _currentTool == EditTool.Select
                        && field.FieldType == "/Tx";
                    if (_currentTool == EditTool.FormField || selectTextField)
                    {
                        Point local = e.GetPosition(element);
                        bool resize = local.X >= element.ActualWidth - FormResizeGripSize
                            && local.Y >= element.ActualHeight - FormResizeGripSize;
                        element.Cursor = resize ? Cursors.SizeNWSE : Cursors.SizeAll;
                    }
                    return;
                }
                if (e.LeftButton != MouseButtonState.Pressed) return;
                Point position = e.GetPosition(canvas);
                if (!_formDragMoved
                    && Math.Abs(position.X - _formDragStart.X) < 0.5
                    && Math.Abs(position.Y - _formDragStart.Y) < 0.5) return;
                if (!_formDragMoved)
                {
                    _formDragMoved = true;
                    SetStatus(_formDragIsResize
                        ? string.Format(Loc("Str_St_FormFieldResizing"), field.FieldName)
                        : string.Format(Loc("Str_St_FormFieldMoving"), field.FieldName));
                }
                if (_formDragIsResize)
                {
                    element.Width = Math.Max(12, Math.Min(
                        _formDragSize.Width + position.X - _formDragStart.X,
                        canvas.ActualWidth - _formDragOrigin.X));
                    element.Height = Math.Max(12, Math.Min(
                        _formDragSize.Height + position.Y - _formDragStart.Y,
                        canvas.ActualHeight - _formDragOrigin.Y));
                }
                else
                {
                    double left = Math.Clamp(
                        _formDragOrigin.X + position.X - _formDragStart.X,
                        0, Math.Max(0, canvas.ActualWidth - element.ActualWidth));
                    double top = Math.Clamp(
                        _formDragOrigin.Y + position.Y - _formDragStart.Y,
                        0, Math.Max(0, canvas.ActualHeight - element.ActualHeight));
                    Canvas.SetLeft(element, left);
                    Canvas.SetTop(element, top);
                }
                e.Handled = true;
            };
            element.PreviewMouseLeftButtonUp += (_, e) =>
            {
                if (!ReferenceEquals(_formDragControl, element)) return;
                element.ReleaseMouseCapture();
                _formDragControl = null;
                _formDragCanvas = null;
                Panel.SetZIndex(element, -1);
                bool resized = _formDragIsResize;
                bool moved = _formDragMoved;
                _formDragIsResize = false;
                _formDragMoved = false;
                if (moved) CommitFormFieldRectangle(pageIndex, field, element, canvas, resized);
                e.Handled = true;
            };
        }

        internal bool HasSelectedFormField => _currentTool is EditTool.Select or EditTool.FormField
            && !string.IsNullOrEmpty(_selectedFormFieldName);

        internal void DeleteSelectedFormField()
        {
            if (_currentFile is null || string.IsNullOrEmpty(_selectedFormFieldName)) return;
            string fieldName = _selectedFormFieldName;
            _selectedFormFieldName = null;
            int pageIndex = _currentPage;
            SaveTempAndReload(
                keepAnnotations: true,
                preserveZoom: true,
                finalizeSavedFile: path => PdfEngineIntegration.RemoveFormField(path, fieldName),
                selectedPageAfterReload: pageIndex,
                preserveRenderedPages: true);
            SetStatus(string.Format(Loc("Str_St_FormFieldDeleted"), fieldName));
        }

        private void OpenFormFieldColorPicker(FormFieldInfo field, int pageIndex)
        {
            Color initial = field.BackgroundColor ?? Colors.White;
            var dialog = new ColorPickerDialog(Window.GetWindow(this), initial);
            dialog.ShowDialog();
            if (!dialog.Accepted || _currentFile is null) return;
            string value = _formTextValues.TryGetValue(field.FieldName, out string? pending)
                ? pending : field.CurrentValue;
            double? fontSize = _formFontSizes.TryGetValue(field.FieldName, out double size)
                ? size : null;
            Color selected = dialog.SelectedColor;
            SaveTempAndReload(
                keepAnnotations: true,
                preserveZoom: true,
                finalizeSavedFile: path => PdfEngineIntegration.SetTextFieldBackground(
                    path, field.FieldName, value, selected, fontSize),
                selectedPageAfterReload: pageIndex,
                preserveRenderedPages: true);
            _selectedFormFieldName = field.FieldName;
            SetStatus(string.Format(Loc("Str_St_FormFieldFillChanged"), field.FieldName));
        }

        private void CommitFormFieldRectangle(
            int pageIndex, FormFieldInfo field, FrameworkElement element, Canvas canvas, bool resized)
        {
            if (_currentFile is null) return;
            Rect canvasRectangle = new(
                Canvas.GetLeft(element), Canvas.GetTop(element),
                element.ActualWidth, element.ActualHeight);
            IReadOnlyList<KillerPdf.Engine.Documents.PdfPageInformation> pages =
                PdfEngineIntegration.ReadPageInformation(_currentFile);
            if ((uint)pageIndex >= (uint)pages.Count) return;
            KillerPdf.Engine.Documents.PdfPageInformation page = pages[pageIndex];
            int rotation = _pageRotations.TryGetValue(pageIndex, out int storedRotation)
                ? ((storedRotation % 360) + 360) % 360
                : page.Rotation;
            (double left, double bottom, double right, double top) = CanvasToPdfRect(
                canvasRectangle, page.Width, page.Height,
                Math.Max(1, canvas.ActualWidth), Math.Max(1, canvas.ActualHeight), rotation);
            SaveTempAndReload(
                keepAnnotations: true,
                preserveZoom: true,
                finalizeSavedFile: path => PdfEngineIntegration.MoveFormWidget(
                    path, field.ObjNum, field.Generation, left, bottom, right, top),
                selectedPageAfterReload: pageIndex,
                preserveRenderedPages: true);
            SetStatus(resized
                ? string.Format(Loc("Str_St_FormFieldResized"), field.FieldName)
                : string.Format(Loc("Str_St_FormFieldMoved"), field.FieldName));
        }

        private readonly record struct FormFieldInfo(
            int    ObjNum,        // widget annotation object number (used as key)
            int    Generation,
            string FieldType,     // /Tx, /Btn, /Ch
            bool   IsCheckBox,
            bool   IsRadio,
            bool   IsMultiSelectChoice,
            bool   IsMultiLine,   // /Tx with Multiline flag (bit 12)
            string FieldName,
            string CurrentValue,
            IReadOnlyList<string> CurrentValues,
            string OnValue,       // radio/checkbox on-state value (e.g. "/Yes")
            bool   IsReadOnly,
            double Cx, double Cy, double Cw, double Ch,
            List<FormChoiceItem> Options,
            double DaFontPt,   // font size from the field's /DA (points); 0 = auto-size
            double Scale,      // canvas units per PDF point, for converting DaFontPt to canvas size
            bool   IsComb,     // #158: /Tx with the Comb flag (bit 25) and a MaxLen
            int    MaxLen,     // #158: comb cell count (also the input length cap)
            Color? BackgroundColor,
            Color? BorderColor);

        /// <summary>
        /// Scans the current page's /Annots for Widget subtypes and overlays interactive
        /// WPF controls on the annotation canvas so the user can fill in form fields.
        /// </summary>
        private void RenderFormFields(int pageIndex, int canvasW, int canvasH)
        {
            if (_doc is null || _currentFile is null) return;
            if (pageIndex >= _doc.PageCount) return;

            // Render onto the page's OWN surface: the per-page overlay used by continuous / grid /
            // two-page views, or the single-page canvas otherwise. Previously this always used the
            // single-page canvas, so interactive fields only appeared in Single Page view.
            var canvas = CanvasForPage(pageIndex);

            // Remove stale overlays without wiping the entire canvas.
            for (int i = canvas.Children.Count - 1; i >= 0; i--)
                if (canvas.Children[i] is FrameworkElement fe && fe.Tag as string == FormOverlayTag)
                    canvas.Children.RemoveAt(i);

            var fields = GetPageFormFields(pageIndex, canvasW, canvasH);
            if (fields.Count == 0) return;

            // Focus highlight (accent). Fields are NOT outlined at rest - the page's own field boxes
            // already show where to type - so we only tint a faint fill and show the accent on focus,
            // matching how Chrome/Brave render fields instead of drawing a green line around each one.
            var fieldBorder = new SolidColorBrush(Color.FromArgb(0x55, 0x88, 0x88, 0x88)); // faint gray, check/radio only
            var darkBrush   = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22));
            var fieldBg     = new SolidColorBrush(Color.FromArgb(200, 255, 253, 231));

            // Collect radio buttons per group so we can wire mutual exclusion after the loop.
            var radioGroups = new Dictionary<string, List<(Ellipse dot, string onVal)>>();

            bool anyField = false;
            foreach (var f in fields)
            {
                UIElement? ctrl = null;

                // Text field
                var fillRole = ClassifyFormField(f);
                if (fillRole == FormFillRole.Signature || fillRole == FormFillRole.Initials)
                {
                    ctrl = BuildSignZone(f, fillRole == FormFillRole.Initials, pageIndex);
                }
                else if (!f.IsCheckBox && !f.IsRadio && f.FieldType != "/Ch")
                {
                    string cur     = _formTextValues.TryGetValue(f.FieldName, out var tv) ? tv : f.CurrentValue;
                    // Size text the way the field intends: use its /DA font size when one is given;
                    // otherwise auto-size - single-line fits the box height (capped so a tall field
                    // isn't giant), multi-line uses a steady readable size rather than shrinking with
                    // the box. This replaces the old box-height guess that made fields huge or tiny.
                    double fontSize;
                    if (_formFontSizes.TryGetValue(f.FieldName, out var userPt) && userPt > 0 && f.Scale > 0)
                        fontSize = userPt * f.Scale;          // user override (the new per-field size control)
                    else if (f.DaFontPt > 0.5 && f.Scale > 0)
                        fontSize = f.DaFontPt * f.Scale;
                    else if (f.IsMultiLine)
                        fontSize = f.Scale > 0 ? 11.5 * f.Scale : Math.Max(11, Math.Min(f.Cw, f.Ch) * 0.5);
                    else
                        fontSize = f.Scale > 0 ? Math.Min(f.Ch * 0.62, 15 * f.Scale) : f.Ch * 0.62;
                    fontSize = Math.Max(9, Math.Min(fontSize, 400));
                    if (f.IsComb) fontSize = Math.Max(9, Math.Min(fontSize, (f.Cw / f.MaxLen) / 0.55));
                    // #158: a comb field types one character per printed cell. The overlay
                    // approximates the cell walk with a monospace face sized to the cell width
                    // (Consolas advance is ~0.55em), capped by MaxLen; the SAVED appearance
                    // stream places each character exactly at its cell center.
                    double combCellW = f.IsComb ? f.Cw / f.MaxLen : 0;
                    var restingBorder = new SolidColorBrush(
                        f.BorderColor ?? Color.FromRgb(0x88, 0x88, 0x88));
                    var tb = new TextBox
                    {
                        Tag              = FormOverlayTag,
                        Width            = f.Cw,
                        Height           = f.Ch,
                        Text             = cur,
                        MaxLength        = f.IsComb ? f.MaxLen : 0,
                        IsReadOnly       = f.IsReadOnly,
                        AcceptsReturn    = f.IsMultiLine,
                        TextWrapping     = f.IsMultiLine ? TextWrapping.Wrap : TextWrapping.NoWrap,
                        VerticalScrollBarVisibility = f.IsMultiLine
                            ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden,
                        // A comb field is laid over the PDF's printed cells. An opaque live-field
                        // fill hides those dividers and makes it look like an ordinary text box.
                        // Keep the overlay transparent so the cells remain visible while the
                        // editable characters, selection, and caret stay above the page artwork.
                        Background       = f.IsComb ? Brushes.Transparent
                            : new SolidColorBrush(f.BackgroundColor ?? fieldBg.Color),
                        Foreground       = Brushes.Black,
                        CaretBrush       = Brushes.Black,
                        Cursor           = Cursors.IBeam,
                        ForceCursor      = true,
                        SelectionBrush   = (System.Windows.Media.Brush)FindResource("HeaderLineBrush"),
                        Style            = (Style)FindResource("FormFieldTextBox"),
                        BorderBrush      = f.IsComb ? Brushes.Transparent : restingBorder,
                        BorderThickness  = new Thickness(1),
                        FontSize         = fontSize,
                        Padding          = f.IsComb
                            ? new Thickness(Math.Max(0, combCellW / 2 - fontSize * 0.275), 0, 0, 0)
                            : new Thickness(3, 0, 3, 0),
                        VerticalContentAlignment = f.IsMultiLine
                            ? VerticalAlignment.Top : VerticalAlignment.Center,
                        ToolTip          = string.IsNullOrEmpty(f.FieldName) ? null : f.FieldName,
                    };
                    if (f.IsComb) tb.FontFamily = new FontFamily("Consolas");
                    // Focus also raises the per-field font-size stepper (and hides it on blur).
                    string capturedKey   = f.FieldName;
                    double capturedScale = f.Scale;
                    tb.GotFocus  += (_, _) => { _selectedFormFieldName = capturedKey; tb.SetResourceReference(Control.BorderBrushProperty, "HeaderLineBrush"); ShowFormSizeBar(tb, capturedKey, capturedScale); };
                    tb.LostFocus += (_, _) => { tb.BorderBrush = f.IsComb ? Brushes.Transparent : restingBorder; HideFormSizeBar(); };
                    tb.TextChanged += (_, _) => { _formTextValues[capturedKey] = tb.Text; MarkDirty(true); };
                    ctrl = f.IsComb ? new CombTextBox(tb, f.MaxLen, fontSize) : tb;
                }

                // Dropdown / choice
                else if (f.FieldType == "/Ch" && f.Options.Count > 0 && f.IsMultiSelectChoice)
                {
                    IReadOnlyList<string> selected = _formMultiChoiceValues.TryGetValue(
                        f.FieldName, out IReadOnlyList<string>? pending) ? pending : f.CurrentValues;
                    var list = new ListBox
                    {
                        Tag = FormOverlayTag,
                        Width = f.Cw,
                        Height = f.Ch,
                        IsEnabled = !f.IsReadOnly,
                        ItemsSource = f.Options,
                        DisplayMemberPath = nameof(FormChoiceItem.DisplayValue),
                        SelectionMode = SelectionMode.Multiple,
                        ItemContainerStyle = (Style)FindResource("FormFieldListBoxItem"),
                        Foreground = Brushes.Black,
                        Background = fieldBg,
                        FontSize = f.DaFontPt > 0.5 && f.Scale > 0
                            ? f.DaFontPt * f.Scale : Math.Min(Math.Max(10, f.Ch * 0.22), 16),
                        ToolTip = string.IsNullOrEmpty(f.FieldName) ? null : f.FieldName,
                    };
                    foreach (FormChoiceItem item in f.Options.Where(item =>
                                 selected.Contains(item.ExportValue, StringComparer.Ordinal)))
                        list.SelectedItems.Add(item);
                    string capturedKey = f.FieldName;
                    list.SelectionChanged += (_, _) =>
                    {
                        _formMultiChoiceValues[capturedKey] = [.. list.SelectedItems
                            .Cast<FormChoiceItem>().Select(item => item.ExportValue)];
                        MarkDirty(true);
                    };
                    ctrl = list;
                }
                else if (f.FieldType == "/Ch" && f.Options.Count > 0)
                {
                    string cur = _formChoiceValues.TryGetValue(f.FieldName, out var tv) ? tv : f.CurrentValue;
                    var combo = new ComboBox
                    {
                        Tag       = FormOverlayTag,
                        Width     = f.Cw,
                        Height    = f.Ch,
                        IsEnabled = !f.IsReadOnly,
                        ItemsSource = f.Options,
                        DisplayMemberPath = nameof(FormChoiceItem.DisplayValue),
                        SelectedValuePath = nameof(FormChoiceItem.ExportValue),
                        SelectedValue = cur,
                        IsTextSearchEnabled = true,
                        Foreground = Brushes.Black,
                        FontSize  = f.DaFontPt > 0.5 && f.Scale > 0
                            ? f.DaFontPt * f.Scale
                            : Math.Min(Math.Max(10, f.Ch * 0.55), 16),
                        ToolTip   = string.IsNullOrEmpty(f.FieldName) ? null : f.FieldName,
                    };
                    string capturedKey = f.FieldName;
                    combo.SelectionChanged += (_, _) =>
                    {
                        if (combo.SelectedValue is string selectedExport)
                        {
                            _formChoiceValues[capturedKey] = selectedExport;
                            MarkDirty(true);
                        }
                    };
                    ctrl = combo;
                }

                // Checkbox
                else if (f.IsCheckBox)
                {
                    bool isChecked = _formCheckValues.TryGetValue(f.FieldName, out var cv) ? cv
                        : !string.IsNullOrEmpty(f.CurrentValue)
                          && f.CurrentValue != "/Off" && f.CurrentValue != "Off";

                    // Custom border-based checkbox - WPF's built-in CheckBox indicator
                    // doesn't scale with Width/Height, so we draw it ourselves.
                    double checkFs = Math.Min(f.Cw, f.Ch) * 0.72;
                    var checkMark = new TextBlock
                    {
                        Text       = "✓",
                        FontSize   = checkFs,
                        FontWeight = FontWeights.Bold,
                        Foreground = darkBrush,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment   = VerticalAlignment.Center,
                        Visibility = isChecked ? Visibility.Visible : Visibility.Collapsed,
                    };
                    var box = new Border
                    {
                        Tag             = FormOverlayTag,
                        Width           = f.Cw,
                        Height          = f.Ch,
                        Background      = fieldBg,
                        BorderBrush     = fieldBorder,
                        BorderThickness = new Thickness(1.5),
                        CornerRadius    = new CornerRadius(2),
                        Cursor          = f.IsReadOnly ? Cursors.Arrow : Cursors.Hand,
                        Child           = checkMark,
                        ToolTip         = string.IsNullOrEmpty(f.FieldName) ? null : f.FieldName,
                    };
                    if (!f.IsReadOnly)
                    {
                        string capturedKey = f.FieldName;
                        box.MouseLeftButtonDown += (_, e) =>
                        {
                            bool now = !(_formCheckValues.TryGetValue(capturedKey, out var v) ? v : isChecked);
                            _formCheckValues[capturedKey] = now;
                            checkMark.Visibility = now ? Visibility.Visible : Visibility.Collapsed;
                            MarkDirty(true);
                            e.Handled = true;
                        };
                    }
                    ctrl = box;
                }

                // Radio button
                else if (f.IsRadio)
                {
                    string groupSelected = _formRadioValues.TryGetValue(f.FieldName, out var rv) ? rv
                        : f.CurrentValue; // CurrentValue = parent /V = currently selected on-value
                    bool isSelected = groupSelected == f.OnValue;

                    double size  = Math.Min(f.Cw, f.Ch) * 0.88;
                    double inner = size * 0.52;

                    var dot = new Ellipse
                    {
                        Width      = inner,
                        Height     = inner,
                        Fill       = darkBrush,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment   = VerticalAlignment.Center,
                        Visibility = isSelected ? Visibility.Visible : Visibility.Collapsed,
                    };
                    var ring = new Ellipse
                    {
                        Width           = size,
                        Height          = size,
                        Stroke          = fieldBorder,
                        StrokeThickness = 1.5,
                        Fill            = fieldBg,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment   = VerticalAlignment.Center,
                    };
                    var grid = new Grid { Width = f.Cw, Height = f.Ch };
                    grid.Children.Add(ring);
                    grid.Children.Add(dot);

                    var radioBorder = new Border
                    {
                        Tag    = FormOverlayTag,
                        Width  = f.Cw,
                        Height = f.Ch,
                        Background = Brushes.Transparent,
                        Cursor = f.IsReadOnly ? Cursors.Arrow : Cursors.Hand,
                        Child  = grid,
                        ToolTip = string.IsNullOrEmpty(f.FieldName) ? null : f.FieldName,
                    };

                    // Register dot for mutual-exclusion wiring after the loop.
                    if (!radioGroups.TryGetValue(f.FieldName, out var groupList))
                        radioGroups[f.FieldName] = groupList = [];
                    groupList.Add((dot, f.OnValue));

                    if (!f.IsReadOnly)
                    {
                        string capturedGroup = f.FieldName;
                        string capturedOn    = f.OnValue;
                        radioBorder.MouseLeftButtonDown += (_, e) =>
                        {
                            _formRadioValues[capturedGroup] = capturedOn;
                            // Deselect all in group, then select this one.
                            if (radioGroups.TryGetValue(capturedGroup, out var gl))
                                foreach (var (d, ov) in gl)
                                    d.Visibility = ov == capturedOn ? Visibility.Visible : Visibility.Collapsed;
                            MarkDirty(true);
                            e.Handled = true;
                        };
                    }
                    ctrl = radioBorder;
                }

                if (ctrl is null) continue;
                AttachFormDesignDrag(ctrl, f, pageIndex, canvas);
                Canvas.SetLeft(ctrl, f.Cx);
                Canvas.SetTop(ctrl, f.Cy);
                // #156: field overlays sit BELOW the annotation layer. RenderAllAnnotations paints
                // the annotations and then restores these, and a Canvas paints later children on
                // top - so a signature dropped on a fill-in field disappeared behind the field's
                // own control. Annotations render at the default ZIndex 0, so -1 puts the fields
                // under them without touching the annotation paths. Clicking a covered field still
                // works: every annotation visual is IsHitTestVisible=false, so it never swallows
                // the click that reaches the field beneath it.
                Panel.SetZIndex(ctrl, -1);
                canvas.Children.Add(ctrl);
                anyField = true;
            }

            if (anyField)
                SetStatus(string.Format(Loc("Str_PageFormFields"), pageIndex + 1, _doc.PageCount));
        }

        /// <summary>
        /// Parses Widget annotations from the given page into field descriptors with canvas coordinates.
        /// Walks the parent chain for each widget to resolve inherited /FT, /T, /V, and /Ff.
        /// </summary>
        private List<FormFieldInfo> GetPageFormFields(int pageIndex, int canvasW, int canvasH)
        {
            var result = new List<FormFieldInfo>();
            if (_doc is null || pageIndex >= _doc.PageCount) return result;
            try
            {
                KillerPdf.Engine.Documents.PdfDocument engineDocument = EnsureEngineDocumentSession().Document;
                foreach (KillerPdf.Engine.Documents.PdfFormWidgetInfo widget in
                    PdfEngineIntegration.ReadPageFormWidgets(engineDocument, pageIndex))
                {
                    double fx1 = widget.Left - widget.PageBoxLeft;
                    double fy1 = widget.Bottom - widget.PageBoxBottom;
                    double fx2 = widget.Right - widget.PageBoxLeft;
                    double fy2 = widget.Top - widget.PageBoxBottom;
                    double pageW = widget.PageBoxWidth;
                    double pageH = widget.PageBoxHeight;
                    int rotation = widget.PageRotation;
                    double cx, cy, cw, ch;
                    switch (rotation)
                    {
                        case 90:
                            cx = fy1 / pageH * canvasW;
                            cy = fx1 / pageW * canvasH;
                            cw = (fy2 - fy1) / pageH * canvasW;
                            ch = (fx2 - fx1) / pageW * canvasH;
                            break;
                        case 180:
                            cx = (pageW - fx2) / pageW * canvasW;
                            cy = fy1 / pageH * canvasH;
                            cw = (fx2 - fx1) / pageW * canvasW;
                            ch = (fy2 - fy1) / pageH * canvasH;
                            break;
                        case 270:
                            cx = (pageH - fy2) / pageH * canvasW;
                            cy = (pageW - fx2) / pageW * canvasH;
                            cw = (fy2 - fy1) / pageH * canvasW;
                            ch = (fx2 - fx1) / pageW * canvasH;
                            break;
                        default:
                            cx = fx1 / pageW * canvasW;
                            cy = (pageH - fy2) / pageH * canvasH;
                            cw = (fx2 - fx1) / pageW * canvasW;
                            ch = (fy2 - fy1) / pageH * canvasH;
                            break;
                    }
                    if (!IsFinite(cx) || !IsFinite(cy)
                        || !IsFinitePositive(cw) || !IsFinitePositive(ch)
                        || cw < 2 || ch < 2) continue;
                    string fieldType = widget.FieldKind switch
                    {
                        KillerPdf.Engine.Documents.PdfFormFieldKind.Text => "/Tx",
                        KillerPdf.Engine.Documents.PdfFormFieldKind.Button => "/Btn",
                        KillerPdf.Engine.Documents.PdfFormFieldKind.Choice => "/Ch",
                        KillerPdf.Engine.Documents.PdfFormFieldKind.Signature => "/Sig",
                        _ => string.Empty
                    };
                    if (fieldType.Length == 0 || widget.FieldName.Length == 0) continue;
                    int flags = checked((int)widget.Flags);
                    bool isMultiLine = fieldType == "/Tx" && (flags & 4096) != 0;
                    bool isComb = fieldType == "/Tx" && (flags & (1 << 24)) != 0
                        && widget.MaximumLength > 0 && !isMultiLine;
                    bool isPushButton = fieldType == "/Btn" && (flags & (1 << 16)) != 0;
                    bool isRadio = fieldType == "/Btn" && !isPushButton
                        && (flags & (1 << 15)) != 0;
                    bool isCheckBox = fieldType == "/Btn" && !isPushButton && !isRadio;
                    if (fieldType == "/Btn" && (isPushButton || widget.HasAction
                        || !widget.HasAppearanceState)) continue;
                    int objectNumber = widget.ObjectNumber > 0
                        ? widget.ObjectNumber : -(pageIndex * 10000 + widget.AnnotationIndex);
                    double fontSize = ParseDaFontSize(widget.DefaultAppearance);
                    double scale = rotation is 90 or 270 ? canvasH / pageW : canvasH / pageH;
                    result.Add(new FormFieldInfo(objectNumber, widget.Generation,
                        fieldType, isCheckBox, isRadio, (flags & (1 << 21)) != 0,
                        isMultiLine, widget.FieldName, widget.Value, widget.Values, widget.OnValue,
                        (flags & 1) != 0, cx, cy, cw, ch,
                        [.. widget.Options.Select(option => new FormChoiceItem(
                            option.ExportValue, option.DisplayValue))],
                        fontSize, scale, isComb, widget.MaximumLength,
                        widget.BackgroundColor is { } background
                            ? Color.FromRgb(
                                (byte)Math.Round(background.Red * 255),
                                (byte)Math.Round(background.Green * 255),
                                (byte)Math.Round(background.Blue * 255))
                            : null,
                        widget.BorderColor is { } border
                            ? Color.FromRgb(
                                (byte)Math.Round(border.Red * 255),
                                (byte)Math.Round(border.Green * 255),
                                (byte)Math.Round(border.Blue * 255))
                            : null));
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"GetPageFormFields (engine): {ex}"); }
            return result;
        }


        // Parses the font size (points) from a PDF /DA default-appearance string, e.g.
        // "/Helv 11 Tf 0 g" -> 11. Returns 0 when the size is "auto" (0) or there's no Tf operator.
        private static double ParseDaFontSize(string da)
        {
            if (string.IsNullOrWhiteSpace(da)) return 0;
            var t = da.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            for (int i = 1; i < t.Length; i++)
                if (t[i] == "Tf" && double.TryParse(t[i - 1],
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double sz) && sz > 0)
                    return sz;
            return 0;
        }

        /// <summary>
        /// Applies all pending form values to a saved PDF as one engine revision.
        /// </summary>
        private void WriteFormValuesToDocument(string path)
        {
            PdfEngineIntegration.ApplyFormValues(path, new PdfEngineIntegration.FormEdits(
                _formTextValues, _formChoiceValues, _formMultiChoiceValues, _formCheckValues,
                _formRadioValues, _formFontSizes));
        }


        // Form-field font-size stepper
        // A small "Font size: - N +" bar shown while a form text field is focused, so the user can
        // resize that field's text (PDF forms otherwise lock the size to the field's /DA). The chosen
        // size is stored per field and baked into the field's /DA on save.
        //
        // Dressed like the annotate bars (same surface, grain, shadow, fade) but ANCHORED TO THE
        // FIELD: the bar drips down from the box being typed in, dropdown-style, and follows it
        // through scrolling and zoom. It flips above the field when there is no room below, so it
        // can never collide with the annotate bars or float detached over the page.
        private ScrollChangedEventHandler? _formBarScrollHook;   // detached in HideFormSizeBar

        private void ShowFormSizeBar(TextBox tb, string fieldName, double scale)
        {
            HideFormSizeBar();
            _activeFormTb    = tb;
            _activeFormName  = fieldName;
            _activeFormScale = scale > 0 ? scale : 1;

            double curPt = Math.Round(_activeFormTb.FontSize / _activeFormScale);

            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(3, 1, 3, 1), Background = Brushes.Transparent };

            // Fixed light text: the InlineFlyout pill is dark regardless of the app theme.
            var lbl = new TextBlock
            {
                Text = Loc("Str_Forms_FontSize"),
                FontFamily = UiKit.UiFont, FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(0xB8, 0xB8, 0xB8)),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0)
            };
            panel.Children.Add(lbl);

            var sizeLbl = new TextBlock
            {
                Text = curPt.ToString("0"),
                FontFamily = UiKit.UiFont, FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF2)),
                MinWidth = 20, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };

            panel.Children.Add(MakeFormSizeStep("", () => AdjustFormFontSize(-1, sizeLbl)));  // minus
            panel.Children.Add(sizeLbl);
            panel.Children.Add(MakeFormSizeStep("", () => AdjustFormFontSize(+1, sizeLbl)));  // plus

            // The on-document "inline flyout" style: translucent pill, solidifies on hover.
            _formSizeBar = UiKit.InlineFlyout(panel);
            _formSizeBar.HorizontalAlignment = HorizontalAlignment.Left;
            _formSizeBar.VerticalAlignment   = VerticalAlignment.Top;

            if (PagePreviewPanel.Parent is Grid g)
            {
                var bar = _formSizeBar;
                // Just under the field, aligned to its left edge (clamped inside the pane); flips
                // above the field when it sits at the bottom. Re-run on scroll/zoom and on the
                // bar's own size changes so it rides with the box instead of hanging in space.
                void Reposition()
                {
                    if (_formSizeBar != bar || _activeFormTb is null) return;
                    try
                    {
                        double barW = bar.ActualWidth  > 0 ? bar.ActualWidth  : 160;
                        double barH = bar.ActualHeight > 0 ? bar.ActualHeight : 34;
                        var below = _activeFormTb.TranslatePoint(new Point(0, _activeFormTb.ActualHeight), g);
                        double x = Math.Max(0, Math.Min(below.X, g.ActualWidth - barW));
                        double y = below.Y + 4;
                        if (y + barH > g.ActualHeight)
                            y = Math.Max(0, _activeFormTb.TranslatePoint(new Point(0, 0), g).Y - barH - 4);
                        bar.Margin = new Thickness(x, y, 0, 0);
                    }
                    catch { /* field mid-layout; the next scroll/size tick repositions */ }
                }

                Panel.SetZIndex(bar, 100);
                g.Children.Add(bar);
                Reposition();
                bar.SizeChanged += (_, _) => Reposition();   // the first real measure replaces the estimate
                _formBarScrollHook = (_, _) => Reposition();
                PagePreviewPanel.ScrollChanged += _formBarScrollHook;

                // Fade in to the pill's translucent rest state (hover solidifies it from there).
                bar.Opacity = 0;
                bar.BeginAnimation(UIElement.OpacityProperty,
                    new DoubleAnimation(0, UiKit.InlineFlyoutRestOpacity, new Duration(TimeSpan.FromMilliseconds(120)))
                    { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
            }
        }

        // A flat, non-focusable +/- step. It's a Border (not a Button) so clicking it doesn't move
        // keyboard focus out of the text field, which would otherwise blur the field and dismiss this
        // bar. The minus/plus are DRAWN (centered rounded rectangles), not font glyphs: the icon font
        // and the number's text font carry different line metrics, so glyph-based signs sat on a
        // slightly different vertical axis than the size readout between them and read as misaligned.
        // Fixed light color: the InlineFlyout pill is dark regardless of the app theme.
        // Shim for the original glyph-string call sites: E710 is the MDL2 Add glyph, anything
        // else is the minus. The glyphs themselves are no longer rendered (see above).
        private static Border MakeFormSizeStep(string glyph, Action onClick) => MakeFormSizeStep(glyph == "", onClick);

        private static Border MakeFormSizeStep(bool plus, Action onClick)
        {
            var fill = new SolidColorBrush(Color.FromRgb(0xEE, 0xEE, 0xEE));
            var shape = new Grid
            {
                Width = 9, Height = 9, SnapsToDevicePixels = true,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };
            shape.Children.Add(new Border
            { Width = 9, Height = 1.6, CornerRadius = new CornerRadius(0.8), Background = fill, VerticalAlignment = VerticalAlignment.Center });
            if (plus)
                shape.Children.Add(new Border
                { Width = 1.6, Height = 9, CornerRadius = new CornerRadius(0.8), Background = fill, HorizontalAlignment = HorizontalAlignment.Center });
            var b = new Border
            {
                Width = 21, Height = 19, CornerRadius = new CornerRadius(9), Cursor = Cursors.Hand,
                Margin = new Thickness(2, 0, 2, 0), Background = Brushes.Transparent, Child = shape
            };
            b.MouseEnter += (_, _) => b.Background = new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF));
            b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
            b.MouseLeftButtonDown += (_, e) => { e.Handled = true; onClick(); };
            return b;
        }

        private void AdjustFormFontSize(int delta, TextBlock sizeLbl)
        {
            if (_activeFormTb is null) return;
            double scale = _activeFormScale > 0 ? _activeFormScale : 1;
            double pt = Math.Round(_activeFormTb.FontSize / scale);
            pt = Math.Max(4, Math.Min(96, pt + delta));
            _formFontSizes[_activeFormName] = pt;
            _activeFormTb.FontSize = pt * scale;
            sizeLbl.Text = pt.ToString("0");
            MarkDirty(true);
        }

        private void HideFormSizeBar()
        {
            if (_formBarScrollHook is not null)
            {
                PagePreviewPanel.ScrollChanged -= _formBarScrollHook;
                _formBarScrollHook = null;
            }
            if (_formSizeBar is not null)
            {
                FadeOutAndRemoveBar(_formSizeBar);   // annotate-bar fade-out; removes it from its parent
                _formSizeBar = null;
            }
        }

        // Returns a /DA default-appearance string with its font size replaced (or a sensible default
        // when none exists), used to bake a user font-size override into the saved field.
        private static string WithDaFontSize(string? da, double pt)
        {
            string size = pt.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(da)) return $"/Helv {size} Tf 0 g";
            var t = da!.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).ToList();
            for (int i = 1; i < t.Count; i++)
                if (t[i] == "Tf") { t[i - 1] = size; return string.Join(" ", t); }
            return $"/Helv {size} Tf " + da;   // no Tf operator present; prepend a font selection
        }

        // Guided AcroForm signing -------------------------------------------------------------------
        private enum FormFillRole { None, Signature, Initials, Date }

        // Classifies a fillable field into a guided-signing role. A true PDF signature field
        // (/FT /Sig) is authoritative. Otherwise the name is matched on WHOLE WORDS, so labels
        // like "Computer Assigned" (contains the letters "sign") or "candidate"/"update" (contain
        // "date") are not mistaken for sign/date zones. Checkboxes, radios, dropdowns are never roles.
        private static FormFillRole ClassifyFormField(FormFieldInfo f)
        {
            if (f.IsCheckBox || f.IsRadio || f.FieldType == "/Ch") return FormFillRole.None;

            // A real signature field declares /FT /Sig - trust it regardless of name.
            if (f.FieldType.Contains("Sig")) return FormFillRole.Signature;

            string n = (f.FieldName ?? string.Empty).ToLowerInvariant();
            bool Word(string pattern) => System.Text.RegularExpressions.Regex.IsMatch(n, pattern);

            if (Word(@"\binitials?\b"))               return FormFillRole.Initials;
            if (Word(@"\b(signature|signed|sign)\b")) return FormFillRole.Signature;
            if (Word(@"\bdated?\b"))                   return FormFillRole.Date;
            return FormFillRole.None;
        }

        // A highlighted, clickable overlay sized to the field rectangle. Clicking fills it.
        private Border BuildSignZone(FormFieldInfo f, bool initials, int pageIndex)
        {
            var accent = Color.FromRgb(0x2a, 0x6e, 0xa5);
            var zone = new Border
            {
                Tag             = FormOverlayTag,
                Width           = f.Cw,
                Height          = f.Ch,
                Background      = new SolidColorBrush(Color.FromArgb(38, accent.R, accent.G, accent.B)),
                BorderBrush     = new SolidColorBrush(Color.FromArgb(190, accent.R, accent.G, accent.B)),
                BorderThickness = new Thickness(1.4),
                CornerRadius    = new CornerRadius(2),
                Cursor          = Cursors.Hand,
                ToolTip         = Loc(initials ? "Str_Form_ClickInitials" : "Str_Form_ClickSign"),
                Child = new TextBlock
                {
                    Text                = Loc(initials ? "Str_Form_Initial" : "Str_Sign_Sign"),
                    FontSize            = Math.Max(8, Math.Min(f.Ch * 0.45, 12)),
                    FontWeight          = FontWeights.SemiBold,
                    Foreground          = new SolidColorBrush(accent),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment   = VerticalAlignment.Center,
                    IsHitTestVisible    = false,
                },
            };
            double zx = f.Cx, zy = f.Cy, zw = f.Cw, zh = f.Ch; int zp = pageIndex, zo = f.ObjNum;
            zone.MouseLeftButtonDown += (_, e) => { e.Handled = true; FillSignField(initials, zo, zp, zx, zy, zw, zh); };
            return zone;
        }
    }
}
