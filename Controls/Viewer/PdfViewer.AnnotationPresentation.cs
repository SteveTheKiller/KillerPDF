using System.Windows;
using System.Windows.Controls;
using KillerPDF.Presentation;

namespace KillerPDF.Controls;

public partial class PdfViewer
{
    private WindowsPdfAnnotationHost? _annotationPresentation;
    private Grid? _annotationPresentationLayer;

    private void AttachCropBarPresentation()
    {
        if (_primaryViewport is null || _cropConfirmBar is not { } bar
            || PagePreviewPanel.Parent is not Grid area) return;
        if (_annotationPresentation is null)
        {
            _annotationPresentationLayer = new Grid { Width = area.ActualWidth, Height = area.ActualHeight };
            _annotationPresentation = new WindowsPdfAnnotationHost(_annotationPresentationLayer);
            Panel.SetZIndex(_annotationPresentation, 100);
            area.Children.Add(_annotationPresentation);
        }
        if (ReferenceEquals(bar.Parent, _annotationPresentationLayer)) return;
        (bar.Parent as Panel)?.Children.Remove(bar);
        _annotationPresentationLayer!.Children.Add(bar);
    }

    private void ReleaseEmptyAnnotationPresentation()
    {
        if (_annotationPresentationLayer?.Children.Count == 0) ReleaseAnnotationPresentation();
    }

    private void ReleaseAnnotationPresentation()
    {
        if (_annotationPresentation is not { } host) return;
        var area = host.Parent as Panel;
        if (_annotationPresentationLayer is { } layer)
            foreach (UIElement child in layer.Children.Cast<UIElement>().ToArray())
            {
                layer.Children.Remove(child);
                area?.Children.Add(child);
            }
        area?.Children.Remove(host);
        host.Dispose();
        _annotationPresentation = null;
        _annotationPresentationLayer = null;
    }
}
