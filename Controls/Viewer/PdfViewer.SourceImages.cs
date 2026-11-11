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
    // Corner is null for a move; otherwise the dragged handle, with the opposite corner fixed.
    private (Canvas Canvas, Point Start, Rect Original, Rect Current, string? Corner)? _sourceImageDrag;
    private string? _pendingSourceImageCorner;
    // The press that just selected a picture: nothing moves yet. A drag from it draws the selection box;
    // a release leaves the picture selected.
    private (Canvas Canvas, Point Start)? _pictureSelectionPress;
    private const double MinimumSourceImageSide = 4;

    private void BeginPictureSelectionPress(Canvas canvas, Point point)
    {
        _pictureSelectionPress = (canvas, point);
        canvas.CaptureMouse();
    }

    private void EndPictureSelectionPress(bool releaseCapture)
    {
        if (_pictureSelectionPress is not { } press) return;
        _pictureSelectionPress = null;
        if (releaseCapture && ReferenceEquals(Mouse.Captured, press.Canvas)) press.Canvas.ReleaseMouseCapture();
    }

    private Rect SelectedSourceImageRect(ImageAnnotation image) =>
        new(image.Position, new Size(image.SourceWidth, image.SourceHeight));

    private void BeginSourceImageDrag(Canvas canvas, Point point)
    {
        string? corner = _pendingSourceImageCorner;
        _pendingSourceImageCorner = null;
        if (_sourceImageSelection is not { } selection || !ReferenceEquals(selection.Owner, _doc)
            || _doc is not { IsReadOnly: false } || !selection.Occurrence.CanSetBounds) return;
        Rect original = SelectedSourceImageRect(selection.Image);
        _sourceImageDrag = (canvas, point, original, original, corner);
        canvas.CaptureMouse();
    }

    private void CancelSourceImageDrag()
    {
        _pendingSourceImageCorner = null;
        EndPictureSelectionPress(releaseCapture: true);
        if (_sourceImageDrag is not { } drag) return;
        _sourceImageDrag = null;
        if (ReferenceEquals(Mouse.Captured, drag.Canvas)) drag.Canvas.ReleaseMouseCapture();
        ShowSourceImageFrame(drag.Original);
    }

    /// <summary>Places the selection border and the four corner handles around a canvas rectangle.</summary>
    private void ShowSourceImageFrame(Rect frame)
    {
        if (_selectionBorder is not null)
        {
            _selectionBorder.Width = frame.Width;
            _selectionBorder.Height = frame.Height;
            Canvas.SetLeft(_selectionBorder, frame.Left);
            Canvas.SetTop(_selectionBorder, frame.Top);
        }
        foreach (var handle in _resizeHandles)
        {
            if (handle.Tag is not string tag) continue;
            double x = tag is "NE" or "SE" ? frame.Right : frame.Left;
            double y = tag is "SW" or "SE" ? frame.Bottom : frame.Top;
            Canvas.SetLeft(handle, x - handle.Width / 2);
            Canvas.SetTop(handle, y - handle.Height / 2);
        }
    }

    /// <summary>The proportional rectangle for a corner drag: the opposite corner stays put and the
    /// picture keeps its shape, limited to the page canvas.</summary>
    private static Rect ResizedSourceImageRect(Rect original, string corner, Point pointer, double canvasWidth, double canvasHeight)
    {
        bool west = corner is "NW" or "SW", north = corner is "NW" or "NE";
        Point anchor = new(west ? original.Right : original.Left, north ? original.Bottom : original.Top);
        double roomX = west ? anchor.X : Math.Max(0, canvasWidth - anchor.X);
        double roomY = north ? anchor.Y : Math.Max(0, canvasHeight - anchor.Y);
        double scale = Math.Max(Math.Abs(pointer.X - anchor.X) / original.Width, Math.Abs(pointer.Y - anchor.Y) / original.Height);
        double minimum = MinimumSourceImageSide / Math.Min(original.Width, original.Height);
        double maximum = Math.Min(roomX / original.Width, roomY / original.Height);
        scale = Math.Max(minimum, Math.Min(scale, Math.Max(minimum, maximum)));
        double width = original.Width * scale, height = original.Height * scale;
        return new Rect(west ? anchor.X - width : anchor.X, north ? anchor.Y - height : anchor.Y, width, height);
    }

    private bool MoveSourceImageDrag(MouseEventArgs e)
    {
        if (_pictureSelectionPress is { } press)
        {
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                EndPictureSelectionPress(releaseCapture: true);
                return true;
            }
            Vector moved = e.GetPosition(press.Canvas) - press.Start;
            if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance) return true;
            // The pointer dragged away from the press that selected the picture: this is the 1.9.2 selection box.
            EndPictureSelectionPress(releaseCapture: false);
            ClearSelection();
            ClearTextSelection();
            if (_gestureCanvas is not null) _activeCanvas = _gestureCanvas;
            BeginSelectionMarquee(press.Start);
            return false;
        }
        if (_sourceImageDrag is not { } drag) return false;
        if (e.LeftButton != MouseButtonState.Pressed || _sourceImageSelection is not { } selection
            || !ReferenceEquals(selection.Owner, _doc))
        {
            CancelSourceImageDrag();
            return true;
        }
        Point pointer = e.GetPosition(drag.Canvas);
        Vector delta = pointer - drag.Start;
        if (drag.Current == drag.Original && Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return true;
        Rect next = drag.Corner is { } corner
            ? ResizedSourceImageRect(drag.Original, corner, pointer, drag.Canvas.ActualWidth, drag.Canvas.ActualHeight)
            : new Rect(new Point(
                Math.Clamp(drag.Original.X + delta.X, 0, Math.Max(0, drag.Canvas.ActualWidth - drag.Original.Width)),
                Math.Clamp(drag.Original.Y + delta.Y, 0, Math.Max(0, drag.Canvas.ActualHeight - drag.Original.Height))),
                drag.Original.Size);
        _sourceImageDrag = (drag.Canvas, drag.Start, drag.Original, next, drag.Corner);
        ShowSourceImageFrame(next);
        return true;
    }

    private bool FinishSourceImageDrag()
    {
        if (_pictureSelectionPress is not null)
        {
            // A click without a drag only selects the picture.
            EndPictureSelectionPress(releaseCapture: true);
            return true;
        }
        if (_sourceImageDrag is not { } drag) return false;
        _sourceImageDrag = null;
        if (ReferenceEquals(Mouse.Captured, drag.Canvas)) drag.Canvas.ReleaseMouseCapture();
        if (_sourceImageSelection is not { } selection || !ReferenceEquals(selection.Owner, _doc)
            || _doc is not { IsReadOnly: false } || drag.Current == drag.Original) return true;
        int page = selection.Occurrence.PageIndex;
        if (!TryPageGeometry(page, out double pw, out double ph, out double cw, out double ch, out int rotation)) return true;
        var bounds = RedactionGeometry.CanvasToPage(drag.Current, pw, ph, cw, ch, rotation);
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

    internal bool TrySelectSourceImageExt(int page, Point point) => TrySelectSourceImage(page, point, out _);

    /// <summary>Selects the topmost editable picture under a page point. wasSelected is true when that same
    /// picture was already selected before this call, or the point is on its corner handle.</summary>
    private bool TrySelectSourceImage(int page, Point point, out bool wasSelected)
    {
        wasSelected = false;
        if (_nativeDoc is not null || _doc is null || _currentTool != EditTool.Select
            || !TryPageGeometry(page, out double pw, out double ph, out double cw, out double ch, out int rotation))
            return false;
        // A left press on a corner handle of the selected picture resizes it instead of selecting again.
        _pendingSourceImageCorner = null;
        if (Mouse.LeftButton == MouseButtonState.Pressed && _sourceImageSelection is { } current
            && ReferenceEquals(current.Owner, _doc) && current.Occurrence.PageIndex == page
            && _doc is { IsReadOnly: false } && current.Occurrence.CanSetBounds)
            foreach (var handle in _resizeHandles)
            {
                double left = Canvas.GetLeft(handle), top = Canvas.GetTop(handle);
                if (handle.Tag is string corner && point.X >= left && point.X <= left + handle.Width
                    && point.Y >= top && point.Y <= top + handle.Height)
                {
                    _pendingSourceImageCorner = corner;
                    wasSelected = true;
                    return true;
                }
            }
        var location = RedactionGeometry.CanvasPointToPage(point, pw, ph, cw, ch, rotation);
        PdfDocument? source;
        try { source = _doc.OpenStrictForContentCopy(); }
        catch (Exception error) when (error is not OutOfMemoryException) { return false; }
        if (source is null) return false;
        IReadOnlyList<PdfImagePlacementSelection> images;
        // A page the viewer can display may still hold content this reader refuses; that is not a click error.
        try { images = new PdfPageContentReader(source).ReadImagePlacements(page); }
        catch (Exception error) when (error is not (OutOfMemoryException or OperationCanceledException)) { return false; }
        string? uneditable = null;
        foreach (var image in images.Reverse())
        {
            var box = image.BoundingBox;
            if (location.X < box.Left || location.X > box.Right || location.Y < box.Bottom || location.Y > box.Top) continue;
            // A picture that cannot be moved or resized stays page content; say why once no editable one is hit.
            if (!image.CanSetBounds) { uneditable ??= image.BoundsUnsupportedReason; continue; }
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
                wasSelected = _sourceImageSelection is { } held && ReferenceEquals(held.Owner, _doc)
                    && held.Occurrence.PageIndex == page && held.Occurrence.ResourceName == image.ResourceName
                    && held.Occurrence.BoundingBox == image.BoundingBox
                    && held.Occurrence.PixelWidth == image.PixelWidth && held.Occurrence.PixelHeight == image.PixelHeight;
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
                canvas.Children.Add(_selectionBorder);
                // Pictures that can be moved can also be resized from their corners, like an added image.
                if (_doc is { IsReadOnly: false })
                {
                    var brush = AccentBrush();
                    foreach (string tag in new[] { "NW", "NE", "SE", "SW" })
                    {
                        var handle = new System.Windows.Shapes.Rectangle
                        {
                            Width = 14 * inverseScale, Height = 14 * inverseScale, Fill = brush,
                            Stroke = System.Windows.Media.Brushes.White, StrokeThickness = inverseScale,
                            Cursor = tag is "NW" or "SE" ? Cursors.SizeNWSE : Cursors.SizeNESW,
                            IsHitTestVisible = true, Tag = tag
                        };
                        _resizeHandles.Add(handle);
                        canvas.Children.Add(handle);
                    }
                }
                ShowSourceImageFrame(bounds);
                return true;
            }
            catch (Exception error) when (error is NotSupportedException or InvalidOperationException or FormatException
                or KillerPdf.Engine.Filters.PdfFilterException)
            {
                // Unsupported occurrences remain page content; no raster substitute is copied.
                return false;
            }
        }
        if (uneditable is not null) SetStatus(Loc(SourceImageDiagnosis.ReasonKey(uneditable)));
        return false;
    }

    /// <summary>Places Extract Image results on a page as editable images in one Undo step. The
    /// rectangles are pixels of a page preview drawn in the page's on-screen orientation; a Cut fill
    /// goes first so it sits beneath the extracted copy.</summary>
    internal ImageAnnotation? AddExtractedImageExt(int page, int previewWidth, int previewHeight,
        Int32Rect pixels, byte[] image, byte[]? fill)
    {
        if (_doc is not { IsReadOnly: false } || previewWidth <= 0 || previewHeight <= 0
            || !TryPageGeometry(page, out _, out _, out double cw, out double ch, out _)) return null;
        double sx = cw / previewWidth, sy = ch / previewHeight;
        ImageAnnotation Make(byte[] data) => new()
        {
            PageIndex = page, Position = new Point(pixels.X * sx, pixels.Y * sy), Scale = 1,
            SourceWidth = pixels.Width * sx, SourceHeight = pixels.Height * sy,
            ImageData = Convert.ToBase64String(data)
        };
        var added = new List<ImageAnnotation>();
        if (fill is not null) added.Add(Make(fill));
        var copy = Make(image);
        added.Add(copy);
        var before = _annotations.TryGetValue(page, out var list)
            ? list.Select(CloneHistoryAnnotation).Where(c => c is not null).Cast<PageAnnotation>().ToList() : [];
        PushUndo(new UndoEntry(UndoKind.PageSnapshot, WasDirty: _isDirty,
            AnnotSnapshot: new Dictionary<int, List<PageAnnotation>> { [page] = before }));
        if (!_annotations.ContainsKey(page)) _annotations[page] = [];
        _annotations[page].AddRange(added);
        MarkDirty();
        ClearSelection();
        RenderAllAnnotations(page);
        return copy;
    }
}
