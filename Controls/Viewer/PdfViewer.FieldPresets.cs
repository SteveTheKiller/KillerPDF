using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using KillerPdf.Engine.Authoring;
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

        // Right-click on a text field with the Form Field tool: its fill color, or keep it as a preset.
        private void ShowFormFieldMenu(FormFieldInfo field, int pageIndex, UIElement target)
        {
            var menu = MakeThemedMenu();
            menu.Items.Add(MakeMenuItem(Loc("Str_FieldPreset_FillColor"),
                (_, _) => OpenFormFieldColorPicker(field, pageIndex), glyph: ""));
            menu.Items.Add(new Separator());
            menu.Items.Add(MakeMenuItem(Loc("Str_FieldPreset_Save"), (_, _) => SaveFieldAsPreset(field), glyph: ""));
            menu.Items.Add(MakeMenuItem(Loc("Str_FieldPreset_Manage"), (_, _) => OpenFieldPresets()));
            menu.PlacementTarget = target;
            menu.Placement = PlacementMode.MousePoint;
            menu.IsOpen = true;
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

                SaveTempAndReload(
                    keepAnnotations: true,
                    preserveZoom: true,
                    finalizeSavedFile: path => fieldName = PdfEngineIntegration.AddTextField(
                        path, pageIndex, x1, y1, x2 - x1, y2 - y1, preset),
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
