using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using KillerPDF.Services;

namespace KillerPDF.Controls;

public partial class PdfViewer
{
    private (PdfWorkingDocument Owner, PdfImagePlacementSelection Occurrence, ImageAnnotation Image)? _sourceImageSelection;
    private (Canvas Canvas, Point Start, Point Original, Point Current)? _sourceImageDrag;

    private void BeginSourceImageDrag(Canvas canvas, Point point)
    {
        if (_sourceImageSelection is not { } selection || !ReferenceEquals(selection.Owner, _doc)
            || _doc is not { IsReadOnly: false } || !selection.Occurrence.CanSetBounds) return;
        _sourceImageDrag = (canvas, point, selection.Image.Position, selection.Image.Position);
        canvas.CaptureMouse();
    }

    private void CancelSourceImageDrag()
    {
        if (_sourceImageDrag is not { } drag) return;
        _sourceImageDrag = null;
        if (ReferenceEquals(Mouse.Captured, drag.Canvas)) drag.Canvas.ReleaseMouseCapture();
        if (_selectionBorder is not null)
        {
            Canvas.SetLeft(_selectionBorder, drag.Original.X);
            Canvas.SetTop(_selectionBorder, drag.Original.Y);
        }
    }

    private bool MoveSourceImageDrag(MouseEventArgs e)
    {
        if (_sourceImageDrag is not { } drag) return false;
        if (e.LeftButton != MouseButtonState.Pressed || _sourceImageSelection is not { } selection
            || !ReferenceEquals(selection.Owner, _doc))
        {
            CancelSourceImageDrag();
            return true;
        }
        Vector delta = e.GetPosition(drag.Canvas) - drag.Start;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return true;
        Point position = new(
            Math.Clamp(drag.Original.X + delta.X, 0, Math.Max(0, drag.Canvas.ActualWidth - selection.Image.SourceWidth)),
            Math.Clamp(drag.Original.Y + delta.Y, 0, Math.Max(0, drag.Canvas.ActualHeight - selection.Image.SourceHeight)));
        _sourceImageDrag = (drag.Canvas, drag.Start, drag.Original, position);
        if (_selectionBorder is not null)
        {
            Canvas.SetLeft(_selectionBorder, position.X);
            Canvas.SetTop(_selectionBorder, position.Y);
        }
        return true;
    }

    private bool FinishSourceImageDrag()
    {
        if (_sourceImageDrag is not { } drag) return false;
        _sourceImageDrag = null;
        if (ReferenceEquals(Mouse.Captured, drag.Canvas)) drag.Canvas.ReleaseMouseCapture();
        if (_sourceImageSelection is not { } selection || !ReferenceEquals(selection.Owner, _doc)
            || _doc is not { IsReadOnly: false } || drag.Current == drag.Original) return true;
        int page = selection.Occurrence.PageIndex;
        if (!TryPageGeometry(page, out double pw, out double ph, out double cw, out double ch, out int rotation)) return true;
        var bounds = RedactionGeometry.CanvasToPage(new Rect(drag.Current,
            new Size(selection.Image.SourceWidth, selection.Image.SourceHeight)), pw, ph, cw, ch, rotation);
        try
        {
            SaveTempAndReload(keepAnnotations: true, preserveZoom: true,
                selectedPageAfterReload: page, finalizeSavedFile: path =>
                {
                    var document = PdfDocument.Open(File.ReadAllBytes(path));
                    var expected = selection.Occurrence;
                    var current = new PdfPageContentReader(document).ReadImagePlacements(page)
                        .SingleOrDefault(image => image.ResourceName == expected.ResourceName
                            && image.BoundingBox == expected.BoundingBox
                            && image.PixelWidth == expected.PixelWidth && image.PixelHeight == expected.PixelHeight)
                        ?? throw new InvalidOperationException("The selected image occurrence is no longer available.");
                    byte[] asset = PdfImagePlacementExtractor.Extract(document, current).ToPdfBytes();
                    if (!asset.AsSpan().SequenceEqual(Convert.FromBase64String(selection.Image.ImageData)))
                        throw new InvalidOperationException("The selected image resource has changed.");
                    AtomicFileOutput.Write(path, PdfImagePlacementEditor.SetBounds(document, current,
                        new PdfContentBounds(bounds.Left, bounds.Bottom, bounds.Right, bounds.Top)));
                });
        }
        catch (Exception error) when (error is not OutOfMemoryException) { ReportEditFailure(error); }
        return true;
    }

    internal ImageAnnotation? SelectedSourceImageExt => _sourceImageSelection is { } selection
        && ReferenceEquals(selection.Owner, _doc) ? selection.Image : null;

    internal bool CanDeleteSelectedSourceImageExt => _sourceImageSelection is { } selection
        && ReferenceEquals(selection.Owner, _doc) && _doc is { IsReadOnly: false }
        && selection.Occurrence.CanRemove;

    private bool DeleteSelectedSourceImage()
    {
        if (_sourceImageSelection is not { } selection || !ReferenceEquals(selection.Owner, _doc)) return false;
        if (!CanDeleteSelectedSourceImageExt) return true;
        try
        {
            SaveTempAndReload(keepAnnotations: true, preserveZoom: true,
                selectedPageAfterReload: selection.Occurrence.PageIndex, finalizeSavedFile: path =>
                {
                    var document = PdfDocument.Open(File.ReadAllBytes(path));
                    var expected = selection.Occurrence;
                    var current = new PdfPageContentReader(document).ReadImagePlacements(expected.PageIndex)
                        .SingleOrDefault(image => image.ResourceName == expected.ResourceName
                            && image.BoundingBox == expected.BoundingBox
                            && image.PixelWidth == expected.PixelWidth && image.PixelHeight == expected.PixelHeight)
                        ?? throw new InvalidOperationException("The selected image occurrence is no longer available.");
                    byte[] asset = PdfImagePlacementExtractor.Extract(document, current).ToPdfBytes();
                    if (!asset.AsSpan().SequenceEqual(Convert.FromBase64String(selection.Image.ImageData)))
                        throw new InvalidOperationException("The selected image resource has changed.");
                    AtomicFileOutput.Write(path, PdfImagePlacementEditor.Remove(document, current));
                });
        }
        catch (Exception error) when (error is not OutOfMemoryException) { ReportEditFailure(error); }
        return true;
    }

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
        IReadOnlyList<PdfImagePlacementSelection> images;
        // A page the viewer can display may still hold content this reader refuses; that is not a click error.
        try { images = new PdfPageContentReader(source).ReadImagePlacements(page); }
        catch (Exception error) when (error is not (OutOfMemoryException or OperationCanceledException)) { return false; }
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
                _sourceImageSelection = (_doc, image, copy);
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
            catch (Exception error) when (error is NotSupportedException or InvalidOperationException or FormatException
                or KillerPdf.Engine.Filters.PdfFilterException)
            {
                // Unsupported occurrences remain page content; no raster substitute is copied.
                return false;
            }
        }
        return false;
    }
}
