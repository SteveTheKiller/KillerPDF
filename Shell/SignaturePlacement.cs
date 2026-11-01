using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using KillerPDF.Controls;

namespace KillerPDF.Shell;

public partial class MainWindow
{
    private const string SignatureDragFormat = "KillerPDF.SignaturePlacement";
    private sealed record SignatureDragOrigin(PdfViewer Viewer, object Document, SavedSignature Signature, Point Press);
    private SignatureDragOrigin? _signatureDragOrigin;
    private bool _signatureDragRunning;
    private Border? _signaturePlacementHint;

    private void InitializeSignaturePlacement(PdfViewer viewer)
    {
        viewer.AllowDrop = true;
        viewer.PreviewMouseMove += (_, e) =>
        {
            if (_signatureDragRunning || _signatureDragOrigin is not { } origin
                || !ReferenceEquals(origin.Viewer, viewer)) return;
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                _signatureDragOrigin = null;
                return;
            }
            if (!SignatureDragIsCurrent(origin)) { _signatureDragOrigin = null; return; }
            Point position = e.GetPosition(viewer);
            if (Math.Abs(position.X - origin.Press.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(position.Y - origin.Press.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            e.Handled = true;
            _signatureDragRunning = true;
            try
            {
                DragDrop.DoDragDrop(viewer, new DataObject(SignatureDragFormat, origin.Signature), DragDropEffects.Copy);
            }
            finally
            {
                _signatureDragRunning = false;
                _signatureDragOrigin = null;
            }
        };
        viewer.AddHandler(UIElement.PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler((_, _) =>
        {
            if (!_signatureDragRunning) _signatureDragOrigin = null;
        }), true);
        viewer.PreviewDragOver += (sender, e) =>
        {
            if (!e.Data.GetDataPresent(SignatureDragFormat)) return;
            e.Handled = true;
            e.Effects = SignatureDropIsCurrent(viewer, e)
                && viewer.TryGetSignatureDropTargetExt(e, out _, out _, out _)
                ? DragDropEffects.Copy : DragDropEffects.None;
        };
        viewer.PreviewDrop += (_, e) =>
        {
            if (!e.Data.GetDataPresent(SignatureDragFormat)) return;
            e.Handled = true;
            e.Effects = DragDropEffects.None;
            if (!SignatureDropIsCurrent(viewer, e)
                || !viewer.TryGetSignatureDropTargetExt(e, out Canvas? canvas, out int page, out Point position)) return;
            _activeCanvas = canvas!;
            PlaceSignature(position, page);
            e.Effects = DragDropEffects.Copy;
        };
    }

    private bool SignatureDragIsCurrent(SignatureDragOrigin origin) =>
        ReferenceEquals(origin.Viewer, ActiveViewer) && ReferenceEquals(origin.Document, _doc)
        && ReferenceEquals(origin.Signature, _pendingSignature) && _currentTool == EditTool.Signature;

    private bool SignatureDropIsCurrent(PdfViewer viewer, DragEventArgs e) =>
        _signatureDragOrigin is { } origin && ReferenceEquals(viewer, origin.Viewer)
        && SignatureDragIsCurrent(origin) && ReferenceEquals(e.Data.GetData(SignatureDragFormat), origin.Signature);

    private void ArmSignaturePickerDrag(SavedSignature signature, MouseButtonEventArgs e)
    {
        if (_doc is not null)
            _signatureDragOrigin = new(ActiveViewer, _doc, signature, e.GetPosition(ActiveViewer));
        e.Handled = true;
    }

    private void UpdateSignaturePlacementHint()
    {
        if (_signaturePlacementHint?.Parent is Panel prior) prior.Children.Remove(_signaturePlacementHint);
        _signaturePlacementHint = null;
        if (_pendingSignature is not { } signature || _doc is null || PagePreviewPanel.Parent is not Grid grid) return;
        var text = new TextBlock { FontFamily = UiKit.UiFont, TextWrapping = TextWrapping.Wrap, MaxWidth = 360 };
        text.SetResourceReference(TextBlock.TextProperty,
            signature.Kind == SignatureKind.Initials ? "Str_St_PlaceInitials" : "Str_St_PlaceSignature");
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        var hint = new Border
        {
            Child = text, Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(12),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
            BorderThickness = new Thickness(1), IsHitTestVisible = false
        };
        hint.SetResourceReference(Border.BackgroundProperty, "MenuBackgroundBrush");
        hint.SetResourceReference(Border.BorderBrushProperty, "MenuBorderBrush");
        hint.SetResourceReference(Border.CornerRadiusProperty, "FlyoutCornerRadius");
        AutomationProperties.SetAutomationId(hint, "SignaturePlacementHint");
        Panel.SetZIndex(hint, 190);
        grid.Children.Add(hint);
        _signaturePlacementHint = hint;
    }
}
