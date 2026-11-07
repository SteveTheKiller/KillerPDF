using System.Windows;
using System.Windows.Controls;
using KillerPdf.Engine.Documents;
using KillerPDF.Services;

namespace KillerPDF.Controls;

public partial class PdfViewer
{
    private (PdfWorkingDocument Owner, ImageAnnotation Image)? _sourceImageSelection;

    internal ImageAnnotation? SelectedSourceImageExt => _sourceImageSelection is { } selection
        && ReferenceEquals(selection.Owner, _doc) ? selection.Image : null;

    internal bool TrySelectSourceImageExt(int page, Point point)
    {
        if (_nativeDoc is not null || _doc is null || _currentTool != EditTool.Select
            || !TryPageGeometry(page, out double pw, out double ph, out double cw, out double ch, out int rotation))
            return false;
        var location = RedactionGeometry.CanvasPointToPage(point, pw, ph, cw, ch, rotation);
        PdfDocument? source;
        try { source = _doc.OpenStrictForContentCopy(); }
        catch (Exception error) when (error is not OutOfMemoryException) { return false; }
        if (source is null) return false;
        var images = new PdfPageContentReader(source).ReadImagePlacements(page);
        foreach (var image in images.Reverse())
        {
            var box = image.BoundingBox;
            if (!image.CanSetBounds || location.X < box.Left || location.X > box.Right
                || location.Y < box.Bottom || location.Y > box.Top) continue;
            try
            {
                // Extraction enforces copying restrictions and retains the original resource graph.
                var asset = PdfImagePlacementExtractor.Extract(source, image);
                var bounds = RedactionGeometry.PageToCanvas(box.Left, box.Bottom, box.Right, box.Top,
                    pw, ph, cw, ch, rotation);
                var copy = new ImageAnnotation
                {
                    PageIndex = page, Position = bounds.TopLeft, Scale = 1,
                    SourceWidth = bounds.Width, SourceHeight = bounds.Height,
                    ImageData = Convert.ToBase64String(asset.ToPdfBytes())
                };
                ClearSelection();
                ClearTextSelection();
                _sourceImageSelection = (_doc, copy);
                var canvas = CanvasForPage(page);
                double inverseScale = canvas.LayoutTransform is System.Windows.Media.ScaleTransform scale
                    && scale.ScaleX > .0001 ? 1 / scale.ScaleX : 1;
                _selectionBorder = new Border
                {
                    BorderBrush = AccentBrush(), BorderThickness = new Thickness(2 * inverseScale),
                    Width = bounds.Width, Height = bounds.Height, IsHitTestVisible = false
                };
                Canvas.SetLeft(_selectionBorder, bounds.Left);
                Canvas.SetTop(_selectionBorder, bounds.Top);
                canvas.Children.Add(_selectionBorder);
                return true;
            }
            catch (Exception error) when (error is NotSupportedException or InvalidOperationException or FormatException)
            {
                // Unsupported occurrences remain page content; no raster substitute is copied.
                return false;
            }
        }
        return false;
    }
}
