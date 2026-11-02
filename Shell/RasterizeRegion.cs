using System.Windows;
using KillerPdf.Engine.Documents;
using KillerPDF.Services;

namespace KillerPDF.Shell
{
    // Rasterize Region (#369): turn one box on a page into an image and leave the rest of the
    // page as it is, for content that prints or displays wrongly. One undo step.
    public partial class MainWindow
    {
        private void RasterizeRegion(int pageIndex)
        {
            if (_doc is null || _currentFile is null) { SetStatus(Loc("Str_Msg_OpenFirst")); return; }
            if (pageIndex < 0 || pageIndex >= _doc.PageCount) return;
            if (!EnsureEditable(continueAfterUnlock: false)) return;
            CommitActiveTextBox();
            string file = _currentFile;
            PdfPageInformation page = PdfEngineIntegration.ReadPageInformation(file)[pageIndex];
            // The page as it shows in the viewer: its effective rotation, the app's when it has one.
            int rotation = ((_pageRotations.TryGetValue(pageIndex, out int turned) ? turned : page.Rotation) % 360 + 360) % 360;
            if (RegionPreview(file, pageIndex, page.Rotation, rotation) is not { } preview) return;
            PdfContentBounds ToPage(Rect box)
            {
                var (left, bottom, right, top) = RedactionGeometry.CanvasToPage(box, page.Width, page.Height,
                    preview.PixelWidth, preview.PixelHeight, rotation);
                return new PdfContentBounds(
                    Math.Clamp(Math.Min(left, right), 0, page.Width), Math.Clamp(Math.Min(bottom, top), 0, page.Height),
                    Math.Clamp(Math.Max(left, right), 0, page.Width), Math.Clamp(Math.Max(bottom, top), 0, page.Height));
            }

            var dialog = new RasterizeRegionDialog(this, preview, box =>
            {
                PdfPartialRasterizationPlan plan = PdfEngineIntegration.PlanPartialRasterization(file, pageIndex, ToPage(box), 72);
                return string.Format(Loc("Str_Raster_Affects"), plan.TextRuns.Count, plan.Images.Count,
                    plan.Paths.Count + plan.Shadings.Count);
            });
            dialog.ShowDialog();
            if (!dialog.Confirmed || dialog.Region.IsEmpty) return;

            PdfContentBounds region = ToPage(dialog.Region);
            double dpi = dialog.Dpi;
            try
            {
                UndoEntry? documentUndo = CaptureDocumentUndo();
                SaveTempAndReload(
                    keepAnnotations: true,
                    finalizeSavedFile: path => PdfEngineIntegration.RasterizeRegion(path, pageIndex, region, dpi),
                    selectedPageAfterReload: pageIndex,
                    documentUndo: documentUndo);
                SetStatus(string.Format(Loc("Str_Raster_Done"), pageIndex + 1));
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, string.Format(Loc("Str_Raster_Failed"), ex.Message), "KillerPDF",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // The base page without annotations or form fields, since only the page itself is
        // rasterized. The renderer applies the file's /Rotate; the rest of the turn comes here.
        private static System.Windows.Media.Imaging.BitmapSource? RegionPreview(
            string file, int pageIndex, int fileRotation, int rotation)
        {
            try
            {
                using var session = PdfPageRenderSession.OpenEngineFirst(file, 1200, 1200);
                PdfRenderedPage rendered = session.RenderBasePage(pageIndex);
                (byte[] pixels, int width, int height) = (rendered.Pixels, rendered.Width, rendered.Height);
                (pixels, width, height) = BitmapHelpers.RotateBitmap(pixels, width, height, rotation - fileRotation);
                var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(width, height, 96, 96,
                    System.Windows.Media.PixelFormats.Bgra32, null, pixels, width * 4);
                bitmap.Freeze();
                return bitmap;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return null;
            }
        }
    }
}
