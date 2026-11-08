using System.Windows;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Rendering;
using KillerPDF.Services;

namespace KillerPDF.Shell
{
    // Extract Image: turn chosen pixels of a page into an editable image annotation. Copy leaves the
    // page as it is; Cut also covers the area with a fill image beneath the copy. One Undo step.
    public partial class MainWindow
    {
        // Pages render at 150 dpi for selection, held to about four million pixels so a large page
        // stays inside the selection's working budget.
        private const double ExtractImageDpi = 150;
        private const double ExtractImageMaximumPixels = 4_000_000;

        internal void ExtractImage(int pageIndex)
        {
            if (_doc is null || _currentFile is null) { SetStatus(Loc("Str_Msg_OpenFirst")); return; }
            if (pageIndex < 0 || pageIndex >= _doc.PageCount) return;
            if (!EnsureEditable(continueAfterUnlock: false)) return;
            // The raster path never goes through original-picture extraction, which is what checks
            // copying for a copied picture, so the file's copy permission is checked here.
            if (!_protection.Access.MayCopy)
            {
                KillerDialog.Show(this, Loc("Str_ExtractImage_NotAllowed"), "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            CommitActiveTextBox();
            string file = _currentFile;
            PdfPageInformation page = PdfEngineIntegration.ReadPageInformation(file, _protection)[pageIndex];
            int rotation = ((_pageRotations.TryGetValue(pageIndex, out int turned) ? turned : page.Rotation) % 360 + 360) % 360;
            (PdfRenderedFrame Frame, System.Windows.Media.Imaging.BitmapSource Preview) source;
            try { source = ExtractImageSource(file, _protection, pageIndex, page, rotation); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                KillerDialog.Show(this, string.Format(Loc("Str_ExtractImage_Failed"), ex.Message), "KillerPDF",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            var dialog = new ExtractImageDialog(this, source.Frame, source.Preview);
            dialog.ShowDialog();
            // Cancel, or closing the window, leaves the document exactly as it was.
            if (!dialog.Confirmed || dialog.Selection is not { } selection) return;
            try
            {
                var extracted = PdfRasterExtraction.Extract(source.Frame, selection, dialog.FeatherRadius);
                byte[]? fill = dialog.Cut
                    ? PdfRasterExtraction.CreateFill(selection, extracted, dialog.FillColor.R, dialog.FillColor.G, dialog.FillColor.B).ToPdfBytes()
                    : null;
                var placed = ActiveViewer.AddExtractedImageExt(pageIndex, source.Frame.Width, source.Frame.Height,
                    new Int32Rect(extracted.Left, extracted.Top, extracted.Width, extracted.Height), extracted.Image.ToPdfBytes(), fill);
                if (placed is null) return;
                SetStatus(string.Format(Loc("Str_ExtractImage_Done"), pageIndex + 1));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                KillerDialog.Show(this, string.Format(Loc("Str_ExtractImage_Failed"), ex.Message), "KillerPDF",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // The base page from the engine's own sRGB render, without annotations, form fields, monitor
        // correction or proofing, turned to the orientation the viewer shows.
        internal static (PdfRenderedFrame Frame, System.Windows.Media.Imaging.BitmapSource Preview) ExtractImageSource(
            string file, OpenedProtection protection, int pageIndex, PdfPageInformation page, int rotation)
        {
            {
                double scale = ExtractImageDpi / 72;
                double area = page.Width * page.Height * scale * scale;
                if (area > ExtractImageMaximumPixels) scale *= Math.Sqrt(ExtractImageMaximumPixels / area);
                using var session = PdfPageRenderSession.OpenEngineFirst(file, scale, protection);
                var rendered = session.RenderBasePage(pageIndex);
                (byte[] pixels, int width, int height) = (rendered.Pixels.ToArray(), rendered.Width, rendered.Height);
                (pixels, width, height) = BitmapHelpers.RotateBitmap(pixels, width, height, rotation - page.Rotation);
                var floats = new float[width * height * 4];
                for (int i = 0; i < width * height; i++)
                {
                    floats[i * 4] = pixels[i * 4 + 2] / 255f;
                    floats[i * 4 + 1] = pixels[i * 4 + 1] / 255f;
                    floats[i * 4 + 2] = pixels[i * 4] / 255f;
                    floats[i * 4 + 3] = pixels[i * 4 + 3] / 255f;
                }
                var frame = new PdfRenderedFrame(width, height, floats, PdfFrameColorEncoding.SrgbD65);
                var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(width, height, 96, 96,
                    System.Windows.Media.PixelFormats.Bgra32, null, pixels, width * 4);
                bitmap.Freeze();
                return (frame, bitmap);
            }
        }
    }
}
