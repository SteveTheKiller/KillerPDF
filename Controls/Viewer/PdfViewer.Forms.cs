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
        private readonly Dictionary<string, PasswordBox> _formPasswordBoxes = [];

        private const double FormResizeGripSize = 14;
        private static readonly DependencyProperty FormFillCursorProperty =
            DependencyProperty.RegisterAttached(
                "FormFillCursor", typeof(Cursor), typeof(PdfViewer));

        // A comb editor's own text stays transparent because CombTextBox draws each character in its
        // cell; painting it would show the raw string over the cells as well.
        private static void SetFormValueForeground(TextBox box, Brush brush)
        {
            if (box.Parent is CombTextBox) return;
            box.Foreground = brush;
        }

        internal void RefreshFormDesignMode()
        {
            bool designMode = _currentTool == EditTool.FormField;
            bool panMode = _isPanning || _spaceHeld || _currentTool == EditTool.Pan;
            Cursor panCursor = _isPanning ? DragCursors.Closed : DragCursors.Open;
            foreach (Canvas canvas in AllPageCanvases())
            foreach (FrameworkElement element in canvas.Children
                         .OfType<FrameworkElement>()
                         .Where(child => child.Tag as string == FormOverlayTag))
            {
                element.Cursor = designMode
                    ? Cursors.SizeAll
                    : panMode
                        ? panCursor
                        : (Cursor?)element.GetValue(FormFillCursorProperty);
                element.ForceCursor = designMode || panMode;
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
            else if (_isPanning || _spaceHeld || _currentTool == EditTool.Pan)
            {
                element.Cursor = _isPanning ? DragCursors.Closed : DragCursors.Open;
                element.ForceCursor = true;
            }

            element.PreviewMouseLeftButtonDown += (_, e) =>
            {
                bool selectTextField = _currentTool == EditTool.Select
                    && field.FieldType == "/Tx";
                if ((_currentTool != EditTool.FormField && !selectTextField)
                    || field.ObjNum <= 0) return;
                // A field being typed in takes the press itself, to place the caret or select text.
                if (selectTextField && element.IsKeyboardFocusWithin) return;
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
                // A text box that grows with its line keeps the field's width as its least; a resize may go under it.
                if (resize) element.MinWidth = 0;
                HideFormSizeBar();
                Keyboard.ClearFocus();
                Window.GetWindow(element)?.Focus();
                element.CaptureMouse();
                Panel.SetZIndex(element, 30);
                e.Handled = true;
            };
            element.PreviewMouseRightButtonDown += (_, e) =>
            {
                // A field being typed in keeps its own edit menu (cut, copy, paste) outside the Form Field tool.
                if (_currentTool != EditTool.FormField && element.IsKeyboardFocusWithin) return;
                e.Handled = true;
                ShowFormFieldCard(field, pageIndex, element);
            };
            element.PreviewMouseMove += (_, e) =>
            {
                if (!ReferenceEquals(_formDragControl, element))
                {
                    bool selectTextField = _currentTool == EditTool.Select
                        && field.FieldType == "/Tx";
                    if (selectTextField && element.IsKeyboardFocusWithin)
                        element.Cursor = Cursors.IBeam;
                    else if (_currentTool == EditTool.FormField || selectTextField)
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

        /// <summary>Drops this pane's selected form field and the size bar that goes with it. A pane that loses focus to
        /// the other pane calls this, so a later Delete in it cannot remove a field the user has moved away from.</summary>
        internal void ClearSelectedFormField()
        {
            _selectedFormFieldName = null;
            HideFormSizeBar();
        }

        // The box that edits the selected field: its text or password box, or null when the selection is a
        // check box, button or other field, when the field is read-only, or when it is not on screen. Only
        // the Select tool edits; the Form Field tool drags its fields.
        private Control? SelectedFormFieldEditor()
        {
            if (_currentTool != EditTool.Select || string.IsNullOrEmpty(_selectedFormFieldName)) return null;
            if (_formValueBoxes.TryGetValue(_selectedFormFieldName, out TextBox? box))
                return box.IsVisible && box.IsEnabled && !box.IsReadOnly ? box : null;
            if (_formPasswordBoxes.TryGetValue(_selectedFormFieldName, out PasswordBox? password))
                return password.IsVisible && password.IsEnabled ? password : null;
            return null;
        }

        internal bool CanTypeIntoSelectedFormField => SelectedFormFieldEditor() is not null;

        // A single click only selects a text field (#307), so the keys that follow reach the window and
        // run tool shortcuts. Typing a character moves the keyboard into the field and adds to its value,
        // which a stray key must not wipe. A password box hides its value and offers no caret, so it is
        // replaced as a whole.
        internal bool BeginTypedFormFieldEdit()
        {
            if (SelectedFormFieldEditor() is not Control editor || !editor.Focus()) return false;
            if (editor is TextBox textBox) textBox.CaretIndex = textBox.Text.Length;
            else if (editor is PasswordBox password) password.SelectAll();
            return true;
        }

        internal void DeleteSelectedFormField()
        {
            if (_currentFile is null || string.IsNullOrEmpty(_selectedFormFieldName)) return;
            string fieldName = _selectedFormFieldName;
            int pageIndex = _currentPage;
            try
            {
                SaveTempAndReload(
                    keepAnnotations: true,
                    preserveZoom: true,
                    finalizeSavedFile: path => PdfEngineIntegration.RemoveFormField(path, fieldName),
                    selectedPageAfterReload: pageIndex,
                    preserveRenderedPages: true,
                    onPublished: () =>
                    {
                        _formTextValues.Remove(fieldName);
                        _formChoiceValues.Remove(fieldName);
                        _formMultiChoiceValues.Remove(fieldName);
                        _formCheckValues.Remove(fieldName);
                        _formRadioValues.Remove(fieldName);
                        _formFontSizes.Remove(fieldName);
                        _formTextStyles.Remove(fieldName);
                    });
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // The field is still in the document, so it stays selected.
                ReportEditFailure(ex);
                return;
            }
            _selectedFormFieldName = null;
            SetStatus(string.Format(Loc("Str_St_FormFieldDeleted"), fieldName));
        }

        private void OpenFormFieldColorPicker(FormFieldInfo field, int pageIndex)
        {
            Color initial = field.BackgroundColor ?? Colors.White;
            var dialog = new ColorPickerDialog(Window.GetWindow(this), initial);
            dialog.ShowDialog();
            if (!dialog.Accepted || _currentFile is null) return;
            double? fontSize = _formFontSizes.TryGetValue(field.FieldName, out double size)
                ? size : null;
            Color selected = dialog.SelectedColor;
            try
            {
                SaveTempAndReload(
                    keepAnnotations: true,
                    preserveZoom: true,
                    finalizeSavedFile: path => PdfEngineIntegration.SetTextFieldBackground(
                        path, field.FieldName, selected, fontSize),
                    selectedPageAfterReload: pageIndex,
                    preserveRenderedPages: true);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                ReportEditFailure(ex);
                return;
            }
            _selectedFormFieldName = field.FieldName;
            SetStatus(string.Format(Loc("Str_St_FormFieldFillChanged"), field.FieldName));
        }

        // The Border tile on a text field's card: color, width and style, saved into the field as one revision.
        private void OpenFormFieldBorderDialog(FormFieldInfo field, int pageIndex)
        {
            if (Window.GetWindow(this) is not { } owner) return;
            var dialog = new FieldBorderDialog(owner, field.BorderColor, field.BorderWidth, field.BorderStyle,
                field.BackgroundColor ?? Colors.White);
            if (dialog.ShowDialog() != true || _currentFile is null) return;
            double? fontSize = _formFontSizes.TryGetValue(field.FieldName, out double size) ? size : null;
            Color color = dialog.BorderColor;
            double width = dialog.BorderWidth;
            var style = dialog.BorderStyle;
            try
            {
                SaveTempAndReload(
                    keepAnnotations: true,
                    preserveZoom: true,
                    finalizeSavedFile: path => PdfEngineIntegration.SetTextFieldBorder(
                        path, field.FieldName, width > 0 ? color : null, width, style, fontSize),
                    selectedPageAfterReload: pageIndex,
                    preserveRenderedPages: true);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                ReportEditFailure(ex);
                return;
            }
            _selectedFormFieldName = field.FieldName;
            SetStatus(string.Format(Loc("Str_St_FormFieldBorderChanged"), field.FieldName));
        }

        private void CommitFormFieldRectangle(
            int pageIndex, FormFieldInfo field, FrameworkElement element, Canvas canvas, bool resized)
        {
            if (_currentFile is null) return;
            // A text box grown past its field to show a long line moves at the field's own width.
            Rect canvasRectangle = new(
                Canvas.GetLeft(element), Canvas.GetTop(element),
                resized ? element.ActualWidth : field.Cw, element.ActualHeight);
            try
            {
                IReadOnlyList<KillerPdf.Engine.Documents.PdfPageInformation> pages =
                    PdfEngineIntegration.ReadPageInformation(_currentFile, _protection);
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
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                ReportEditFailure(ex);
                return;
            }
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
            Color? BorderColor,
            bool   IsPassword = false,   // /Tx with the Password flag (bit 14): never shown in clear
            string Description = "",     // the field's /TU user name, e.g. "Date of birth"
            string AppearanceFont = "",  // PostScript name of the embedded font the saved appearance uses
            Color? DaColor = null,       // the text color of the field's /DA, when it gives one
            bool   Underline = false,    // the field's /DS text-decoration
            bool   StrikeThrough = false,
            bool   MarkedTransparent = false,   // /MK /BG is empty: the field is clear on purpose
            double BorderWidth = 1,             // /BS /W in points
            KillerPdf.Engine.Authoring.PdfFormFieldBorderStyle BorderStyle = default)
        {
            // What a person reads and a screen reader announces: the /TU description when the
            // form has one, else the internal field name. Storage and export keep FieldName.
            public string Label => string.IsNullOrWhiteSpace(Description) ? FieldName : Description;
        }

        /// <summary>
        /// Scans the current page's /Annots for Widget subtypes and overlays interactive
        /// WPF controls on the annotation canvas so the user can fill in form fields.
        /// </summary>
        private void RenderFormFields(int pageIndex, int canvasW, int canvasH)
        {
            try { RenderFormFieldsCore(pageIndex, canvasW, canvasH); }
            // Highlighter marks show what paints beneath them, and the fields paint beneath annotations, so the marks on
            // this page take in the fields just laid (or forget the ones just removed).
            finally { HighlighterLook.Refresh(CanvasForPage(pageIndex)); }
        }

        private void RenderFormFieldsCore(int pageIndex, int canvasW, int canvasH)
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

            // Check boxes and radio buttons of one name can sit on different pages, each page with
            // its own overlay, so their marks are kept for the document and not for this page.
            if (!ReferenceEquals(_fieldMarksDocument, _doc))
            {
                _fieldMarksDocument = _doc;
                _fieldMarks = new Controls.RepeatedFieldMarks();
            }
            Controls.RepeatedFieldMarks fieldMarks = _fieldMarks;
            fieldMarks.BeginPage(pageIndex);

            bool anyField = false;
            foreach (var f in fields)
            {
                UIElement? ctrl = null;

                // Text field
                var fillRole = ClassifyFormField(f);
                // A field that already holds a digital signature keeps its signed appearance on the
                // page. A Sign box over it would hide the signature (#425).
                if (f.FieldType == "/Sig" && SignedFieldNames().Contains(f.FieldName))
                    continue;
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
                    // A field whose /MK says it is clear (an empty /BG, as recognized fields over a printed
                    // box have) shows no fill and no outline at rest; the page's own box shows where to type.
                    // A border the field gives itself shows at rest in its color, at its width on the page, and
                    // only along the bottom for an underline; a field without one keeps the 1px gray outline.
                    bool ownBorder = f.BorderColor.HasValue && f.BorderWidth > 0;
                    Brush restingBorder = ownBorder ? new SolidColorBrush(f.BorderColor!.Value)
                        : f.MarkedTransparent ? Brushes.Transparent
                        : new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
                    double borderPx = ownBorder ? Math.Max(1, f.BorderWidth * f.Scale) : 1;
                    Thickness restingThickness = ownBorder
                        && f.BorderStyle == KillerPdf.Engine.Authoring.PdfFormFieldBorderStyle.Underline
                        ? new Thickness(0, 0, 0, borderPx) : new Thickness(borderPx);
                    Brush restingFill = f.MarkedTransparent ? Brushes.Transparent
                        : new SolidColorBrush(f.BackgroundColor ?? fieldBg.Color);
                    if (f.IsPassword)
                    {
                        // A password field (flag bit 14) never shows its value: PasswordBox masks it,
                        // blocks copy and cut, and reports itself as a password to UI Automation
                        // without exposing the text (BF-U006). The real value is still saved.
                        var pb = new PasswordBox
                        {
                            Tag              = FormOverlayTag,
                            Width            = f.Cw,
                            Height           = f.Ch,
                            Password         = cur,
                            IsEnabled        = !f.IsReadOnly,
                            Background       = restingFill,
                            Foreground       = Brushes.Black,
                            CaretBrush       = Brushes.Black,
                            BorderBrush      = restingBorder,
                            BorderThickness  = restingThickness,
                            FontSize         = fontSize,
                            Padding          = new Thickness(3, 0, 3, 0),
                            VerticalContentAlignment = VerticalAlignment.Center,
                            ToolTip          = string.IsNullOrEmpty(f.Label) ? null : f.Label,
                        };
                        if (f.MaxLen > 0) pb.MaxLength = f.MaxLen;
                        string passwordKey = f.FieldName;
                        pb.GotFocus  += (_, _) => { _selectedFormFieldName = passwordKey; pb.SetResourceReference(Control.BorderBrushProperty, "HeaderLineBrush"); };
                        pb.LostFocus += (_, _) => { pb.BorderBrush = restingBorder; RecalculateFormFields(); };
                        pb.PasswordChanged += (_, _) => { _formTextValues[passwordKey] = pb.Password; MarkDirty(true); };
                        _formPasswordBoxes[passwordKey] = pb;
                        ctrl = pb;
                    }
                    else
                    {
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
                            Background       = f.IsComb ? Brushes.Transparent : restingFill,
                            Foreground       = Brushes.Black,
                            CaretBrush       = Brushes.Black,
                            Cursor           = Cursors.IBeam,
                            ForceCursor      = true,
                            SelectionBrush   = (System.Windows.Media.Brush)FindResource("HeaderLineBrush"),
                            Style            = (Style)FindResource("FormFieldTextBox"),
                            BorderBrush      = f.IsComb ? Brushes.Transparent : restingBorder,
                            BorderThickness  = f.IsComb ? new Thickness(1) : restingThickness,
                            FontSize         = fontSize,
                            Padding          = f.IsComb
                                ? new Thickness(Math.Max(0, combCellW / 2 - fontSize * 0.275), 0, 0, 0)
                                : new Thickness(3, 0, 3, 0),
                            VerticalContentAlignment = f.IsMultiLine
                                ? VerticalAlignment.Top : VerticalAlignment.Center,
                            ToolTip          = string.IsNullOrEmpty(f.Label) ? null : f.Label,
                        };
                        // The look the saved field already has, for when no style is given here.
                        if (SavedFormTextStyle(f) is { } savedStyle) _formSavedTextStyles[f.FieldName] = savedStyle;
                        else _formSavedTextStyles.Remove(f.FieldName);
                        if (!f.IsComb && !f.IsMultiLine)
                        {
                            // Centre the capitals rather than the line box, as the comb cells do, at
                            // whatever size the stepper sets.
                            var padding = new System.Windows.Data.MultiBinding
                                { Converter = new Services.TextCapCentering.PaddingConverter(3, 3) };
                            padding.Bindings.Add(new System.Windows.Data.Binding(nameof(TextBox.FontSize))
                                { RelativeSource = System.Windows.Data.RelativeSource.Self });
                            padding.Bindings.Add(new System.Windows.Data.Binding(nameof(TextBox.FontFamily))
                                { RelativeSource = System.Windows.Data.RelativeSource.Self });
                            tb.SetBinding(Control.PaddingProperty, padding);
                        }
                        // Focus also raises the per-field font-size stepper (and hides it on blur).
                        string capturedKey   = f.FieldName;
                        double capturedScale = f.Scale;
                        // One typed value goes to every field of a name, so the tightest limit holds it.
                        // Its read starts now, so it is known by the time anyone types.
                        StartReadingFormTextLimits();
                        int MaxLen() => KnownFormTextLimits()?.GetValueOrDefault(capturedKey, f.MaxLen) ?? f.MaxLen;
                        string lastAccepted = tb.Text;
                        tb.GotFocus  += (_, _) =>
                        {
                            bool wasRecalculating = _formRecalculating;
                            _formRecalculating = true;
                            try
                            {
                                tb.Text = _formTextValues.TryGetValue(capturedKey, out string? raw) ? raw : cur;
                                lastAccepted = tb.Text;
                                SetFormValueForeground(tb, FormTextBrush(capturedKey));
                            }
                            finally { _formRecalculating = wasRecalculating; }
                            _selectedFormFieldName = capturedKey; tb.SetResourceReference(Control.BorderBrushProperty, "HeaderLineBrush"); ShowFormSizeBar(tb, capturedKey, capturedScale);
                            // A value that arrived too long is shown whole, never cut, but named.
                            int maxLen = MaxLen();
                            if (Services.FormTextLimit.IsOver(tb.Text, maxLen))
                                SetStatus(string.Format(Loc("Str_St_FieldMaxLength"), maxLen));
                        };
                        tb.LostFocus += (_, _) =>
                        {
                            tb.BorderBrush = f.IsComb ? Brushes.Transparent : restingBorder;
                            if (!FormSizeBarInUse) HideFormSizeBar();
                            RecalculateFormFields();
                        };
                        tb.TextChanged += (_, _) =>
                        {
                            if (_formRecalculating) return;
                            // Typing, paste and IME composition all land here. Hold the field to the
                            // character count Save and Print enforce (BF-U007).
                            int maxLen = MaxLen();
                            if (!Services.FormTextLimit.Accepts(tb.Text, lastAccepted, maxLen))
                            {
                                int caret = Math.Min(tb.CaretIndex, lastAccepted.Length);
                                tb.Text = lastAccepted;
                                tb.CaretIndex = caret;
                                SetStatus(string.Format(Loc("Str_St_FieldMaxLength"), maxLen));
                                return;
                            }
                            lastAccepted = tb.Text;
                            _formTextValues[capturedKey] = tb.Text; MarkDirty(true);
                            // The value belongs to the name, so every box of it shows what was typed.
                            _formRecalculating = true;
                            try { fieldMarks.ShowText(capturedKey, tb.Text, tb); }
                            finally { _formRecalculating = false; }
                        };
                        fieldMarks.AddText(pageIndex, capturedKey, tb, capturedScale);
                        RegisterFormValueBox(capturedKey, tb);
                        AttachFormTextStyleClipboard(tb, capturedKey, capturedScale);
                        ctrl = f.IsComb ? new CombTextBox(tb, f.MaxLen, fontSize) : tb;
                        ApplyFormTextStyle(tb, capturedKey);
                    }
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
                        ToolTip = string.IsNullOrEmpty(f.Label) ? null : f.Label,
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
                        // The kit's ComboBox fill is the theme's field color, near black in the dark
                        // themes; the field sits on the page, so it takes the page field colors.
                        Background = new SolidColorBrush(f.BackgroundColor ?? fieldBg.Color),
                        BorderBrush = new SolidColorBrush(f.BorderColor ?? Color.FromRgb(0x88, 0x88, 0x88)),
                        Foreground = Brushes.Black,
                        FontSize  = f.DaFontPt > 0.5 && f.Scale > 0
                            ? f.DaFontPt * f.Scale
                            : Math.Min(Math.Max(10, f.Ch * 0.55), 16),
                        ToolTip   = string.IsNullOrEmpty(f.Label) ? null : f.Label,
                    };
                    string capturedKey = f.FieldName;
                    combo.SelectionChanged += (_, _) =>
                    {
                        if (combo.SelectedValue is string selectedExport)
                        {
                            _formChoiceValues[capturedKey] = selectedExport;
                            MarkDirty(true);
                            RecalculateFormFields();
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
                        Width           = f.Cw,
                        Height          = f.Ch,
                        Background      = fieldBg,
                        BorderBrush     = fieldBorder,
                        BorderThickness = new Thickness(1.5),
                        CornerRadius    = new CornerRadius(2),
                        Child           = checkMark,
                    };
                    string capturedKey = f.FieldName;
                    bool checkInitial = isChecked;
                    fieldMarks.AddCheck(pageIndex, capturedKey, checkMark);
                    ctrl = new Controls.FormToggleSurface(box, Controls.FormToggleSurface.ToggleKind.CheckBox,
                        () => _formCheckValues.TryGetValue(capturedKey, out var v) ? v : checkInitial,
                        f.IsReadOnly ? null : () =>
                        {
                            bool now = !(_formCheckValues.TryGetValue(capturedKey, out var v) ? v : checkInitial);
                            _formCheckValues[capturedKey] = now;
                            fieldMarks.ShowCheck(capturedKey, now);
                            MarkDirty(true);
                        })
                    {
                        Tag     = FormOverlayTag,
                        Width   = f.Cw,
                        Height  = f.Ch,
                        ToolTip = string.IsNullOrEmpty(f.Label) ? null : f.Label,
                    };
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

                    fieldMarks.AddRadio(pageIndex, f.FieldName, dot, f.OnValue);

                    string capturedGroup = f.FieldName;
                    string capturedOn    = f.OnValue;
                    string groupInitial  = groupSelected;
                    ctrl = new Controls.FormToggleSurface(grid, Controls.FormToggleSurface.ToggleKind.Radio,
                        () => (_formRadioValues.TryGetValue(capturedGroup, out var sel) ? sel : groupInitial) == capturedOn,
                        f.IsReadOnly ? null : () =>
                        {
                            _formRadioValues[capturedGroup] = capturedOn;
                            fieldMarks.SelectRadio(capturedGroup, capturedOn);
                            MarkDirty(true);
                        }, group: capturedGroup)
                    {
                        Tag     = FormOverlayTag,
                        Width   = f.Cw,
                        Height  = f.Ch,
                        ToolTip = string.IsNullOrEmpty(f.Label) ? null : f.Label,
                    };
                }

                if (ctrl is null) continue;
                if (!string.IsNullOrEmpty(f.Label))
                    System.Windows.Automation.AutomationProperties.SetName(ctrl,
                        f.IsRadio ? f.Label + ": " + f.OnValue[1..] : f.Label);
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

        // Every page has to be read to know a name's tightest limit, so it is read once per working
        // document, on a worker, and held with it for both panes and every tab, and only for a document
        // that has a text field on screen. Until it is known, a field holds to its own limit; a value
        // its twin cannot take keeps the twin's own and the save names it. A page edit opens a new
        // working document, which is read again.
        private static readonly Services.FormTextLimitCache FormTextLimits = new();

        private void StartReadingFormTextLimits()
        {
            if (_doc is null || FormTextLimits.Known(_doc) is not null) return;
            KillerPdf.Engine.Documents.PdfDocument engine;
            try { engine = EnsureEngineDocumentSession().Document; }
            catch (Exception error) when (error is not OutOfMemoryException) { return; }
            FormTextLimits.Read(_doc, () =>
            {
                try { return Services.FormTextLimit.SmallestByName(engine); }
                catch (Exception error) when (error is not OutOfMemoryException) { return new Dictionary<string, int>(); }
            });
        }

        private IReadOnlyDictionary<string, int>? KnownFormTextLimits() => _doc is null ? null : FormTextLimits.Known(_doc);

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
                    PdfEngineIntegration.ReadPageFormWidgets(engineDocument, pageIndex, forDisplay: true))
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
                            : null,
                        IsPassword: fieldType == "/Tx" && !isMultiLine && (flags & (1 << 13)) != 0,
                        Description: widget.Tooltip ?? string.Empty,
                        AppearanceFont: widget.AppearanceFontName,
                        DaColor: ParseDaColor(widget.DefaultAppearance),
                        Underline: widget.Underline, StrikeThrough: widget.StrikeThrough,
                        MarkedTransparent: widget.MarkedTransparent,
                        BorderWidth: widget.BorderWidth, BorderStyle: widget.BorderStyle));
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

        // The text color a /DA string sets: "r g b rg" or "gray g", the last one given. Null when none.
        private static Color? ParseDaColor(string da)
        {
            if (string.IsNullOrWhiteSpace(da)) return null;
            var t = da.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            static bool Unit(string text, out byte value)
            {
                bool ok = double.TryParse(text, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double number) && number is >= 0 and <= 1;
                value = ok ? (byte)Math.Round(number * 255) : (byte)0;
                return ok;
            }
            Color? color = null;
            for (int i = 0; i < t.Length; i++)
            {
                if (t[i] == "rg" && i >= 3 && Unit(t[i - 3], out byte r) && Unit(t[i - 2], out byte g) && Unit(t[i - 1], out byte b))
                    color = Color.FromRgb(r, g, b);
                else if (t[i] == "g" && i >= 1 && Unit(t[i - 1], out byte gray))
                    color = Color.FromRgb(gray, gray, gray);
            }
            return color;
        }

        // The look a text field's saved appearance already has: the embedded font it draws with and the
        // color of its /DA. Null for the plain black standard-font look, which the overlay leaves as is.
        private static Services.FormTextStyle? SavedFormTextStyle(FormFieldInfo f)
        {
            string? family = null;
            bool bold = false, italic = false;
            if (f.AppearanceFont.Length > 0)
            {
                Services.DetectedPdfFontStyle detected = Services.PdfFontStyle.FromPdfName(f.AppearanceFont);
                family = Shell.MainWindow.SystemFontNames.FirstOrDefault(name =>
                    string.Equals(name, detected.Family, StringComparison.OrdinalIgnoreCase));
                (bold, italic) = (detected.Bold, detected.Italic);
            }
            string? color = f.DaColor is { } c && c != Colors.Black ? Services.FormTextStyle.Hex(c) : null;
            return family is null && !bold && !italic && color is null && !f.Underline && !f.StrikeThrough
                ? null : new Services.FormTextStyle(family, bold, italic, color, f.Underline, f.StrikeThrough);
        }

        // The style a field shows: the one given here, else the one its saved appearance has.
        private Services.FormTextStyle? EffectiveFormTextStyle(string fieldName) =>
            _formTextStyles.TryGetValue(fieldName, out Services.FormTextStyle? chosen) ? chosen
            : _formSavedTextStyles.TryGetValue(fieldName, out Services.FormTextStyle? saved) ? saved : null;

        // The brush a field's value is drawn with when nothing (a formatted negative) asks for red.
        private Brush FormTextBrush(string fieldName) =>
            EffectiveFormTextStyle(fieldName)?.MediaColor is { } color ? new SolidColorBrush(color) : Brushes.Black;

        // Shows a style on one box of a field: its font, weight and slant, and its color, which a comb
        // draws on its cells because its editor's own text stays transparent.
        private void ApplyFormTextStyle(TextBox box, string fieldName)
        {
            Services.FormTextStyle? style = EffectiveFormTextStyle(fieldName);
            // A field without a font of its own shows Arial: the face its saved standard font stands for,
            // and the one the size bar names, so typed and pasted text look as they will be saved.
            box.FontFamily = new FontFamily(style?.FontFamily ?? Services.FormTextStyle.DefaultFamily);
            box.FontWeight = style?.Bold == true ? FontWeights.Bold : FontWeights.Normal;
            box.FontStyle = style?.Italic == true ? FontStyles.Italic : FontStyles.Normal;
            var decorations = new TextDecorationCollection();
            if (style?.Underline == true) decorations.Add(TextDecorations.Underline);
            if (style?.StrikeThrough == true) decorations.Add(TextDecorations.Strikethrough);
            box.TextDecorations = decorations;
            Brush brush = FormTextBrush(fieldName);
            if (box.Parent is CombTextBox comb) comb.CharacterBrush = brush;
            SetFormValueForeground(box, brush);
        }

        /// <summary>
        /// Applies all pending form values to a saved PDF as one engine revision. A field that shares
        /// its name with one that took the value and could not take it itself is remembered for the
        /// save to report.
        /// </summary>
        private void WriteFormValuesToDocument(string path)
        {
            FieldsThatKeptTheirValueRef = PdfEngineIntegration.ApplyFormValues(path, new PdfEngineIntegration.FormEdits(
                _formTextValues, _formChoiceValues, _formMultiChoiceValues, _formCheckValues,
                _formRadioValues, _formFontSizes, _formTextStyles));
        }


        internal PdfEngineIntegration.FormEdits SnapshotFormValuesForPrint() =>
            PdfPrintSource.Snapshot(new PdfEngineIntegration.FormEdits(
                _formTextValues, _formChoiceValues, _formMultiChoiceValues, _formCheckValues,
                _formRadioValues, _formFontSizes, _formTextStyles));

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

            panel.Children.Add(MakeFormSizeStep("\uE738", () => AdjustFormFontSize(-1, sizeLbl)));  // minus
            panel.Children.Add(sizeLbl);
            panel.Children.Add(MakeFormSizeStep("\uE710", () => AdjustFormFontSize(+1, sizeLbl)));  // plus
            AddFormTextStyleControls(panel, fieldName);

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
                KillerPDF.Presentation.ViewportOcclusion.SetOccludes(bar, true);
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
        private static Border MakeFormSizeStep(string glyph, Action onClick) => MakeFormSizeStep(glyph == "\uE710", onClick);

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
            SetFormFontSize(_activeFormName, _activeFormTb, scale, pt);
            sizeLbl.Text = pt.ToString("0");
        }

        // A field's font size in points, set in the box it was set in and, since the size belongs to the
        // name, shown in every other box of it at once.
        private void SetFormFontSize(string fieldName, TextBox box, double scale, double points)
        {
            points = Math.Max(4, Math.Min(96, points));
            _formFontSizes[fieldName] = points;
            box.FontSize = points * (scale > 0 ? scale : 1);
            _fieldMarks.ShowFontSize(fieldName, points, box);
            MarkDirty(true);
        }

        // Font, bold, italic, underline, strikethrough and color for the field's value: the choices the
        // Text tool's bar offers.
        // Like the size steps, none of them takes keyboard focus, so the field stays the one typed in.
        private void AddFormTextStyleControls(StackPanel panel, string fieldName)
        {
            var light = new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF2));
            Services.FormTextStyle Current() => EffectiveFormTextStyle(fieldName) ?? new Services.FormTextStyle();
            panel.Children.Add(new Border
            {
                Width = 1, Height = 14, Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF))
            });

            var fontBox = new ComboBox
            {
                Width = 130, Height = 20, FontFamily = UiKit.UiFont, FontSize = 11, MaxDropDownHeight = 320,
                Focusable = false, VerticalAlignment = VerticalAlignment.Center
            };
            if (TryFindResource("DarkComboBox") is Style comboStyle) fontBox.Style = comboStyle;
            var itemStyle = new Style(typeof(ComboBoxItem), TryFindResource("DarkComboItem") as Style);
            itemStyle.Setters.Add(new Setter(UIElement.FocusableProperty, false));
            fontBox.ItemContainerStyle = itemStyle;
            System.Windows.Automation.AutomationProperties.SetName(fontBox, Loc("Str_Bar_Font").TrimEnd(':', ' '));
            foreach (string name in Shell.MainWindow.SystemFontNames) fontBox.Items.Add(name);
            fontBox.SelectedItem = Current().FontFamily ?? Services.FormTextStyle.DefaultFamily;
            fontBox.ToolTip = fontBox.SelectedItem;
            fontBox.SelectionChanged += (_, _) =>
            {
                if (fontBox.SelectedItem is not string family) return;
                fontBox.ToolTip = family;
                ChangeFormTextStyle(fieldName, style => style with { FontFamily = family });
            };
            fontBox.DropDownClosed += (_, _) => _activeFormTb?.Focus();
            panel.Children.Add(fontBox);

            // Each glyph previews its own effect, as the Text tool's B, I, S and U toggles do.
            Border Toggle(string glyph, FontWeight weight, FontStyle slant, TextDecorationCollection? decoration,
                string tipKey, Func<Services.FormTextStyle, bool> isOn, Func<Services.FormTextStyle, Services.FormTextStyle> flip)
            {
                var toggle = new Border
                {
                    Width = 21, Height = 19, CornerRadius = new CornerRadius(4), Margin = new Thickness(4, 0, 0, 0),
                    Cursor = Cursors.Hand, ToolTip = Loc(tipKey),
                    Child = new TextBlock
                    {
                        Text = glyph, FontFamily = UiKit.UiFont, FontSize = 12, FontWeight = weight, FontStyle = slant,
                        TextDecorations = decoration, Foreground = light,
                        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                    }
                };
                void Paint() => toggle.Background = isOn(Current())
                    ? new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)) : Brushes.Transparent;
                Paint();
                toggle.MouseLeftButtonDown += (_, e) => { e.Handled = true; ChangeFormTextStyle(fieldName, flip); Paint(); };
                System.Windows.Automation.AutomationProperties.SetName(toggle, Loc(tipKey));
                return toggle;
            }
            panel.Children.Add(Toggle("B", FontWeights.Bold, FontStyles.Normal, null, "Str_Lbl_Bold",
                style => style.Bold, style => style with { Bold = !style.Bold }));
            panel.Children.Add(Toggle("I", FontWeights.Normal, FontStyles.Italic, null, "Str_Lbl_Italic",
                style => style.Italic, style => style with { Italic = !style.Italic }));
            // Strikethrough before underline, in the Text tool's order.
            panel.Children.Add(Toggle("S", FontWeights.Normal, FontStyles.Normal, TextDecorations.Strikethrough, "Str_Lbl_Strike",
                style => style.StrikeThrough, style => style with { StrikeThrough = !style.StrikeThrough }));
            panel.Children.Add(Toggle("U", FontWeights.Normal, FontStyles.Normal, TextDecorations.Underline, "Str_Lbl_Underline",
                style => style.Underline, style => style with { Underline = !style.Underline }));

            var swatch = new Border
            {
                Width = 16, Height = 16, CornerRadius = new CornerRadius(3), BorderThickness = new Thickness(1),
                BorderBrush = light, Margin = new Thickness(6, 0, 2, 0), Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center, Background = FormTextBrush(fieldName),
                ToolTip = Loc("Str_Bar_Color").TrimEnd(':', ' ')
            };
            System.Windows.Automation.AutomationProperties.SetName(swatch, Loc("Str_Bar_Color").TrimEnd(':', ' '));
            swatch.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                var dialog = new ColorPickerDialog(Window.GetWindow(this), Current().MediaColor ?? Colors.Black);
                dialog.ShowDialog();
                if (!dialog.Accepted) { _activeFormTb?.Focus(); return; }
                string color = Services.FormTextStyle.Hex(dialog.SelectedColor);
                ChangeFormTextStyle(fieldName, style => style with { TextColor = color });
                swatch.Background = FormTextBrush(fieldName);
            };
            panel.Children.Add(swatch);
        }

        // Copied field text carries its field's style and font size under this app-only format, beside the
        // plain text other programs read, the way copied annotations keep their look.
        private const string FormTextStyleClipboardFormat = "KillerPDF.FormTextStyle";
        private sealed record CopiedFormText(string Text, Services.FormTextStyle? Style, double Points);

        // Copying from a field puts its style and size on the clipboard too, and pasting that same text into
        // another field gives it the look the text had. A field has one look for its whole value, so the
        // pasted look becomes the field's. Text changed on the clipboard since (copied elsewhere) brings none.
        private void AttachFormTextStyleClipboard(TextBox box, string fieldName, double scale)
        {
            DataObject.AddCopyingHandler(box, (_, e) =>
            {
                if (box.SelectionLength == 0) return;
                // The size in points, as the size bar shows it: the one set here, else the box's own.
                double points = _formFontSizes.TryGetValue(fieldName, out double set) ? set
                    : Math.Round(box.FontSize / (scale > 0 ? scale : 1));
                e.DataObject.SetData(FormTextStyleClipboardFormat, System.Text.Json.JsonSerializer.Serialize(
                    new CopiedFormText(box.SelectedText, EffectiveFormTextStyle(fieldName), points)));
            });
            DataObject.AddPastingHandler(box, (_, e) =>
            {
                if (e.DataObject.GetData(FormTextStyleClipboardFormat) is not string json) return;
                CopiedFormText? copied;
                try { copied = System.Text.Json.JsonSerializer.Deserialize<CopiedFormText>(json); }
                catch (System.Text.Json.JsonException) { return; }
                if (copied is null || e.DataObject.GetData(DataFormats.UnicodeText) as string != copied.Text) return;
                // After the text lands, so the look follows the value it came with.
                Dispatcher.BeginInvoke(() =>
                {
                    if (copied.Points > 0) SetFormFontSize(fieldName, box, scale, copied.Points);
                    if (copied.Style is { } style) ChangeFormTextStyle(fieldName, _ => style);
                    if (ReferenceEquals(_activeFormTb, box)) ShowFormSizeBar(box, fieldName, scale);
                });
            });
        }

        // Gives the field a changed style, shows it in every box of the name and keeps typing in the field.
        private void ChangeFormTextStyle(string fieldName, Func<Services.FormTextStyle, Services.FormTextStyle> change)
        {
            _formTextStyles[fieldName] = change(EffectiveFormTextStyle(fieldName) ?? new Services.FormTextStyle());
            _fieldMarks.ForEachText(fieldName, box => ApplyFormTextStyle(box, fieldName));
            if (_activeFormTb is { } active)
            {
                ApplyFormTextStyle(active, fieldName);
                if (!active.IsKeyboardFocusWithin) active.Focus();
            }
            MarkDirty(true);
        }

        // While the pointer or keyboard is on the bar (its font list, its color button), the field it
        // belongs to may lose focus for a moment; the bar stays for the choice to land.
        private bool FormSizeBarInUse => _formSizeBar is { } bar && (bar.IsMouseOver || bar.IsKeyboardFocusWithin);

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

        // The signature fields that already hold a signature, read once per engine document.
        private KillerPdf.Engine.Documents.PdfDocument? _signedFieldsDocument;
        private HashSet<string> _signedFieldNames = new(StringComparer.Ordinal);

        private HashSet<string> SignedFieldNames()
        {
            KillerPdf.Engine.Documents.PdfDocument document = EnsureEngineDocumentSession().Document;
            if (ReferenceEquals(document, _signedFieldsDocument)) return _signedFieldNames;
            _signedFieldsDocument = document;
            try
            {
                _signedFieldNames = new HashSet<string>(KillerPdf.Engine.Signing.PdfSignatureReader.Read(document)
                    .Where(signature => signature.IsSigned)
                    .Select(signature => signature.FieldName), StringComparer.Ordinal);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"SignedFieldNames (engine): {ex}");
                _signedFieldNames = new HashSet<string>(StringComparer.Ordinal);
            }
            return _signedFieldNames;
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
