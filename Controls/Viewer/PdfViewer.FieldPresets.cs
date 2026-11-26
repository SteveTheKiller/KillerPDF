using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KillerPdf.Engine.Authoring;
using KillerPDF.Features;
using KillerPDF.Services;

namespace KillerPDF.Controls
{
    // Fillable text field presets (#340): save a field's size and look, insert it again anywhere,
    // in this document or any other. The presets live in the user's app data.
    public partial class PdfViewer
    {
        internal static TextFieldPresetStore FieldPresets { get; } = new();

        /// <summary>The saved presets, or null after telling the user they could not be read.</summary>
        internal PdfTextFieldPresetCollection? LoadFieldPresets(bool quiet = false)
        {
            try { return FieldPresets.Load(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException
                or System.Text.Json.JsonException or NotSupportedException or ArgumentException)
            {
                if (!quiet) ShowFieldPresetError(error);
                return null;
            }
        }

        private void ShowFieldPresetError(Exception error) =>
            KillerDialog.Show(Window.GetWindow(this), Loc("Str_FieldPreset_Failed") + "\n" + error.Message,
                "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);

        /// <summary>Opens the preset list. With <paramref name="pending"/>, the user names that new
        /// preset; otherwise they rename, reorder or delete the saved ones.</summary>
        internal void OpenFieldPresets(PdfTextFieldPreset? pending = null)
        {
            if (LoadFieldPresets() is not PdfTextFieldPresetCollection presets) return;
            var dialog = new TextFieldPresetsDialog(Window.GetWindow(this), presets, pending);
            dialog.ShowDialog();
            if (!dialog.Saved) return;
            try { FieldPresets.Save(dialog.Result); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                ShowFieldPresetError(error);
                return;
            }
            if (dialog.SavedName is string name)
                SetStatus(string.Format(Loc("Str_FieldPreset_Saved"), name));
        }

        // A text field's own size, font size, colors and border, read back from the document.
        private void SaveFieldAsPreset(FormFieldInfo field)
        {
            if (_currentFile is null) return;
            double? fontSize = _formFontSizes.TryGetValue(field.FieldName, out double size) ? size : null;
            PdfTextFieldPreset captured;
            try
            {
                captured = PdfEngineIntegration.ReadTextFieldPreset(_currentFile, field.FieldName,
                    field.FieldName, fontSize);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException
                or KeyNotFoundException or ArgumentException or FormatException or InvalidOperationException)
            {
                ShowFieldPresetError(error);
                return;
            }
            OpenFieldPresets(captured);
        }

        // Right-click on a form field, with any tool: the object card for that kind of field. A text field gets its fill
        // color, Clear and Copy Value, then its presets; a check box or list gets Clear; every field copies its name.
        // Delete sits in the header while the Form Field tool is on, the tool that moves and resizes fields.
        private void ShowFormFieldCard(FormFieldInfo field, int pageIndex, FrameworkElement element)
        {
            if (Host is not { } host) return;
            string value = FormFieldValueText(field, element);
            var copyName = new ViewerCardTile(GlyphText(0xE8EC), Loc("Str_Ctx_CopyFieldName"), null,
                () => CopyFieldText(field.FieldName), true);
            var copyValue = new ViewerCardTile(GlyphText(0xE8C8), Loc("Str_Ctx_CopyValue"), null,
                () => CopyFieldText(value), value.Length > 0);
            var clear = new ViewerCardTile(GlyphText(0xE894), Loc("Str_Ctx_ClearValue"), null,
                () => ClearFormFieldValue(field, element), !field.IsReadOnly && FormFieldHasValue(field, element));
            var rows = new List<IReadOnlyList<ViewerCardTile>>();
            if (field.FieldType == "/Tx")
            {
                bool inFile = _currentFile is not null;
                var fill = new ViewerCardTile(GlyphText(0xE790), Loc("Str_FieldPreset_FillColor"), null,
                    () => OpenFormFieldColorPicker(field, pageIndex), inFile);
                // A password field never hands its value to the clipboard.
                rows.Add(field.IsPassword ? [fill, clear, copyName] : [fill, clear, copyValue]);
                var presets = new List<ViewerCardTile>
                {
                    new(GlyphText(0xE74E), Loc("Str_FieldPreset_Save"), null, () => SaveFieldAsPreset(field), inFile),
                    new(GlyphText(0xE8FD), Loc("Str_FieldPreset_Manage"), null, () => OpenFieldPresets(), true),
                };
                if (!field.IsPassword) presets.Add(copyName);
                rows.Add(presets);
                // #536: a Z through the space nobody wrote in, on a multiline or empty field. A spare tile, so it
                // waits in the card's Hidden drawer until someone puts it on the card.
                if (!field.IsPassword && !field.IsComb && (field.IsMultiLine || value.Length == 0))
                {
                    IReadOnlyList<Point>? z = inFile && !field.IsReadOnly ? FieldZMarkPoints(field, element) : null;
                    rows.Add([new ViewerCardTile(GlyphText(0xED63), Loc("Str_Ctx_FillUnusedSpace"), null,
                        () => FillUnusedSpace(field, pageIndex, z!), z is not null) { SpareId = "fillUnusedSpace" }]);
                }
            }
            else if (field.IsCheckBox) rows.Add([clear, copyName]);
            else if (field.IsMultiSelectChoice) rows.Add([clear, copyValue, copyName]);
            else if (field.FieldType == "/Sig") rows.Add([copyName]);
            else rows.Add([copyValue, copyName]);

            Action? delete = _currentTool == EditTool.FormField && field.ObjNum > 0 && _currentFile is not null
                ? () => { _selectedFormFieldName = field.FieldName; DeleteSelectedFormField(); }
                : null;
            string meta = field.Label + " · " + string.Format(Loc("Str_PageLabel"), pageIndex + 1);
            host.ShowObjectCard(this, new ViewerObjectCard(pageIndex, FormFieldKindLabel(field), meta, rows)
                { Delete = delete, CardKey = "FormField", LockedGlyphs = [""] }, element);
        }

        // Where the Z goes: the field's box as it sits on the page now, under its last line of text.
        private IReadOnlyList<Point>? FieldZMarkPoints(FormFieldInfo field, FrameworkElement element)
        {
            double left = Canvas.GetLeft(element), top = Canvas.GetTop(element);
            if (!double.IsFinite(left) || !double.IsFinite(top)) return null;
            var box = new Rect(left, top, field.Cw, element.ActualHeight > 0 ? element.ActualHeight : field.Ch);
            double used = 0;
            if (element is TextBox text && text.Text.TrimEnd().Length is int length and > 0)
            {
                // The bottom of the last character that shows ink; trailing blank lines are unused space too.
                Rect last = text.GetRectFromCharacterIndex(length - 1, true);
                if (last.IsEmpty) return null;
                used = last.Bottom;
            }
            return FieldZMark.Points(box, used, field.Scale);
        }

        // Fill unused space (#536): the Z goes on as an ordinary black ink mark, so it moves, resizes, undoes and
        // flattens like any drawing, and the field becomes read-only so nothing gets written under it later.
        private void FillUnusedSpace(FormFieldInfo field, int pageIndex, IReadOnlyList<Point> points)
        {
            if (_currentFile is null || points.Count < 2) return;
            var z = new InkAnnotation
            {
                PageIndex = pageIndex,
                StrokeWidth = FieldZMark.StrokePoints * field.Scale,
                SquareEnds = true,
            };
            z.SetColor(Colors.Black);
            z.Points.AddRange(points);
            // The lock and the Z are one edit. The document undo the reload captures holds the page before both,
            // so the Z joins the annotations only once the lock is published and pushes no entry of its own: one
            // Ctrl+Z takes off the Z and unlocks the field, and a failed lock leaves no Z behind.
            try
            {
                SaveTempAndReload(
                    keepAnnotations: true,
                    preserveZoom: true,
                    finalizeSavedFile: path => PdfEngineIntegration.SetFormFieldReadOnly(path, field.FieldName),
                    selectedPageAfterReload: pageIndex,
                    preserveRenderedPages: true,
                    onPublished: () =>
                    {
                        if (!_annotations.TryGetValue(pageIndex, out var marks)) _annotations[pageIndex] = marks = [];
                        marks.Add(z);
                    });
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                ReportEditFailure(ex);
                return;
            }
            RenderAllAnnotations(pageIndex);
            SetStatus(string.Format(Loc("Str_St_FieldUnusedSpaceFilled"), field.Label));
        }

        private string FormFieldKindLabel(FormFieldInfo field) => field switch
        {
            { FieldType: "/Tx" } => Loc("Str_Field_Text"),
            { IsCheckBox: true } => Loc("Str_Field_CheckBox"),
            { IsRadio: true } => Loc("Str_Field_Radio"),
            { FieldType: "/Ch" } => Loc("Str_Field_Choice"),
            { FieldType: "/Sig" } => Loc("Str_Field_Signature"),
            _ => Loc("Str_Field_Button"),
        };

        // What the field holds now, as text: typed text, the chosen option or options, or the chosen radio button.
        private string FormFieldValueText(FormFieldInfo field, FrameworkElement element)
        {
            if (field.FieldType == "/Tx")
                return field.IsPassword ? ""
                    : _formValueBoxes.TryGetValue(field.FieldName, out TextBox? box) ? box.Text
                    : _formTextValues.TryGetValue(field.FieldName, out string? raw) ? raw : field.CurrentValue;
            if (element is ComboBox combo) return combo.SelectedItem is FormChoiceItem chosen ? chosen.DisplayValue : "";
            if (element is ListBox list)
                return string.Join(Environment.NewLine, list.SelectedItems.OfType<FormChoiceItem>().Select(item => item.DisplayValue));
            if (field.IsRadio)
            {
                string on = _formRadioValues.TryGetValue(field.FieldName, out string? picked) ? picked : field.CurrentValue;
                return on is "" or "/Off" or "Off" ? "" : on.TrimStart('/');
            }
            return "";
        }

        private bool FormFieldHasValue(FormFieldInfo field, FrameworkElement element) => field switch
        {
            { FieldType: "/Tx", IsPassword: true } => _formPasswordBoxes.TryGetValue(field.FieldName, out PasswordBox? secret) && secret.Password.Length > 0,
            { FieldType: "/Tx" } => _formValueBoxes.TryGetValue(field.FieldName, out TextBox? box) && box.Text.Length > 0,
            { IsCheckBox: true } => element is FormToggleSurface toggle && toggle.IsOn,
            _ => element is ListBox list && list.SelectedItems.Count > 0,
        };

        // Clear goes through the field's own control, so the value, the dirty mark and every box of the name follow.
        private void ClearFormFieldValue(FormFieldInfo field, FrameworkElement element)
        {
            if (field.FieldType == "/Tx")
            {
                if (field.IsPassword) { if (_formPasswordBoxes.TryGetValue(field.FieldName, out PasswordBox? secret)) secret.Clear(); }
                else if (_formValueBoxes.TryGetValue(field.FieldName, out TextBox? box)) box.Clear();
                RecalculateFormFields();
            }
            else if (field.IsCheckBox && element is FormToggleSurface toggle && toggle.IsOn) toggle.Activate();
            else if (element is ListBox list) list.UnselectAll();
        }

        private void CopyFieldText(string text)
        {
            if (text.Length == 0) return;
            TrySetClipboard(text);
            SetStatus(Loc("Str_St_Copied"));
        }

        /// <summary>
        /// Inserts a field from a preset with its top left corner at a canvas point, kept on the
        /// page. The preset's size is the size the field shows at, whatever the page's rotation.
        /// </summary>
        internal void PlaceTextFieldPreset(Point position, int pageIndex, PdfTextFieldPreset preset)
        {
            if (_currentFile is null) return;
            Canvas canvas = CanvasForPage(pageIndex);
            string? fieldName = null;
            try
            {
                IReadOnlyList<KillerPdf.Engine.Documents.PdfPageInformation> pages =
                    PdfEngineIntegration.ReadPageInformation(_currentFile, _protection);
                if ((uint)pageIndex >= (uint)pages.Count) return;
                KillerPdf.Engine.Documents.PdfPageInformation page = pages[pageIndex];
                int rotation = _pageRotations.TryGetValue(pageIndex, out int storedRotation)
                    ? ((storedRotation % 360) + 360) % 360
                    : page.Rotation;
                double canvasWidth = Math.Max(1, canvas.ActualWidth);
                double canvasHeight = Math.Max(1, canvas.ActualHeight);
                double visualWidth = rotation is 90 or 270 ? page.Height : page.Width;
                double scale = canvasWidth / Math.Max(1, visualWidth);
                double width = Math.Min(preset.Width * scale, canvasWidth);
                double height = Math.Min(preset.Height * scale, canvasHeight);
                var canvasRect = new Rect(
                    Math.Clamp(position.X, 0, canvasWidth - width),
                    Math.Clamp(position.Y, 0, canvasHeight - height),
                    width, height);
                (double x1, double y1, double x2, double y2) = CanvasToPdfRect(
                    canvasRect, page.Width, page.Height, canvasWidth, canvasHeight, rotation);

                string tooltipFormat = Loc("Str_FormField_AnswerTooltip");
                SaveTempAndReload(
                    keepAnnotations: true,
                    preserveZoom: true,
                    finalizeSavedFile: path => fieldName = PdfEngineIntegration.AddTextField(
                        path, pageIndex, x1, y1, x2 - x1, y2 - y1, preset, tooltipFormat),
                    selectedPageAfterReload: pageIndex,
                    preserveRenderedPages: true);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                ReportEditFailure(ex);
                return;
            }
            SetStatus(fieldName is null
                ? Loc("Str_St_FormFieldCreateFailed")
                : string.Format(Loc("Str_St_FormFieldCreated"), fieldName));
        }
    }
}
