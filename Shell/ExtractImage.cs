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

        private static (PdfRenderedFrame Frame, System.Windows.Media.Imaging.BitmapSource Preview) ExtractImageSource(
            string file, OpenedProtection protection, int pageIndex, PdfPageInformation page, int rotation)
        {
            var (frame, pixels) = PageExtractionSource.Render(file, protection, pageIndex, page, rotation);
            var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(frame.Width, frame.Height, 96, 96,
                System.Windows.Media.PixelFormats.Bgra32, null, pixels, frame.Width * 4);
            bitmap.Freeze();
            return (frame, bitmap);
        }
    }
}
