using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using KillerPDF.Services;

namespace KillerPDF.Controls
{
    public partial class PdfViewer
    {
        private void BeginFormFieldDrag(Point position)
        {
            ClearSelection();
            _isDrawing = true;
            _drawStart = position;
            var preview = new Rectangle
            {
                Width = 0,
                Height = 0,
                Fill = new SolidColorBrush(Color.FromArgb(210, 255, 255, 255)),
                Stroke = new SolidColorBrush(Color.FromRgb(220, 32, 32)),
                StrokeThickness = 2,
                StrokeDashArray = [4, 2],
                IsHitTestVisible = false
            };
            Canvas.SetLeft(preview, position.X);
            Canvas.SetTop(preview, position.Y);
            Panel.SetZIndex(preview, 20);
            _activeCanvas.Children.Add(preview);
            _activePreview = preview;
            _activeCanvas.CaptureMouse();
            SetStatus(Loc("Str_St_FormFieldDrag"));
        }

        private void CommitFormFieldDrag(int pageIndex, Rectangle preview)
        {
            Rect canvasRect = new(
                Canvas.GetLeft(preview), Canvas.GetTop(preview),
                preview.Width, preview.Height);
            _activeCanvas?.Children.Remove(preview);
            if (_currentFile is null || _activeCanvas is null)
                return;

            IReadOnlyList<KillerPdf.Engine.Documents.PdfPageInformation> pages =
                PdfEngineIntegration.ReadPageInformation(_currentFile);
            if ((uint)pageIndex >= (uint)pages.Count)
                return;
            KillerPdf.Engine.Documents.PdfPageInformation page = pages[pageIndex];
            int rotation = _pageRotations.TryGetValue(pageIndex, out int storedRotation)
                ? ((storedRotation % 360) + 360) % 360
                : page.Rotation;
            double canvasWidth = Math.Max(1, _activeCanvas.ActualWidth);
            double canvasHeight = Math.Max(1, _activeCanvas.ActualHeight);

            // #340: click-to-place an armed preset centered on the click (a real drag wins
            // instead and discards the armed preset). One shot either way.
            var preset = _pendingFieldPreset;
            _pendingFieldPreset = null;
            bool fromPreset = false;
            KillerPdf.Engine.Authoring.PdfRgbColor? presetText = null, presetFill = null, presetBorder = null;
            double presetBorderW = 1, presetFont = 0;
            if (preset is not null && canvasRect.Width >= 12 && canvasRect.Height >= 12)
            {
                // Manual drag with a preset armed: fully manual, preset discarded.
            }
            else if (preset is not null)
            {
                double presetCanvasW = preset.WidthPt * canvasWidth / Math.Max(1, page.Width);
                double presetCanvasH = preset.HeightPt * canvasHeight / Math.Max(1, page.Height);
                canvasRect = new Rect(
                    _drawStart.X - presetCanvasW / 2, _drawStart.Y - presetCanvasH / 2,
                    presetCanvasW, presetCanvasH);
                fromPreset = true;
                presetText = RgbFromHex(preset.TextHex);
                presetFill = RgbFromHex(preset.FillHex);
                presetBorder = RgbFromHex(preset.BorderHex);
                presetBorderW = preset.BorderWidth;
                presetFont = preset.FontSizePt;
            }
            else if (canvasRect.Width < 12 || canvasRect.Height < 12)
            {
                SetStatus(Loc("Str_St_FormFieldCanceled"));
                return;
            }
            (double x1, double y1, double x2, double y2) = CanvasToPdfRect(
                canvasRect, page.Width, page.Height, canvasWidth, canvasHeight, rotation);

            string? fieldName = null;
            SaveTempAndReload(
                keepAnnotations: true,
                preserveZoom: true,
                finalizeSavedFile: path => fieldName = fromPreset
                    ? PdfEngineIntegration.AddTextField(path, pageIndex, x1, y1, x2 - x1, y2 - y1,
                        presetText, presetFill, presetBorder, presetBorderW,
                        presetFont > 0 ? presetFont : null)
                    : PdfEngineIntegration.AddTextField(
                        path, pageIndex, x1, y1, x2 - x1, y2 - y1),
                selectedPageAfterReload: pageIndex,
                preserveRenderedPages: true);
            SetStatus(fieldName is null
                ? Loc("Str_St_FormFieldCreateFailed")
                : string.Format(Loc("Str_St_FormFieldCreated"), fieldName));
        }
    }
}
