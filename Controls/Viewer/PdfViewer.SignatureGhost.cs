using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using KillerPDF.Services;

namespace KillerPDF.Controls
{
    // While a signature is being placed, a faint copy follows the pointer at the size it will land,
    // and the wheel resizes it. It sits on the page canvas, so page zoom applies to it as well.
    public partial class PdfViewer
    {
        private const double SignatureGhostOpacity = 0.55;
        private Canvas? _signatureGhost;
        private Canvas? _signatureGhostPage;
        private Point _signatureGhostPosition;
        private double? _signaturePlacementScale;

        internal double SignaturePlacementScaleFor(SavedSignature signature) =>
            _signaturePlacementScale ?? SignaturePlacementScale.Default(signature.Kind == SignatureKind.Initials);

        /// <summary>Forgets the ghost and its size when placement starts, ends or changes signature.</summary>
        internal void ResetSignatureGhost()
        {
            _signaturePlacementScale = null;
            RemoveSignatureGhost();
        }

        private void RemoveSignatureGhost()
        {
            if (_signatureGhost?.Parent is Panel parent) parent.Children.Remove(_signatureGhost);
            _signatureGhost = null;
            _signatureGhostPage?.ClearValue(CursorProperty);
            if (_signatureGhostPage is not null) _signatureGhostPage.MouseLeave -= SignatureGhostPage_MouseLeave;
            _signatureGhostPage = null;
        }

        private void SignatureGhostPage_MouseLeave(object sender, MouseEventArgs e) => RemoveSignatureGhost();

        private void MoveSignatureGhost(Canvas page, Point position)
        {
            if (_pendingSignature is not { } signature) return;
            _signatureGhostPosition = position;
            if (!ReferenceEquals(page, _signatureGhostPage) || _signatureGhost?.Parent is null) DrawSignatureGhost(page, signature);
            if (_signatureGhost is null) return;
            Canvas.SetLeft(_signatureGhost, position.X);
            Canvas.SetTop(_signatureGhost, position.Y);
        }

        private bool ResizeSignatureGhost(int wheelDelta)
        {
            if (_pendingSignature is not { } signature || _currentTool != EditTool.Signature) return false;
            _signaturePlacementScale = SignaturePlacementScale.Step(SignaturePlacementScaleFor(signature), wheelDelta);
            if (_signatureGhostPage is { } page)
            {
                DrawSignatureGhost(page, signature);
                if (_signatureGhost is not null)
                {
                    Canvas.SetLeft(_signatureGhost, _signatureGhostPosition.X);
                    Canvas.SetTop(_signatureGhost, _signatureGhostPosition.Y);
                }
            }
            return true;
        }

        private void DrawSignatureGhost(Canvas page, SavedSignature signature)
        {
            double? scale = _signaturePlacementScale;
            RemoveSignatureGhost();
            _signaturePlacementScale = scale;
            double size = SignaturePlacementScaleFor(signature);
            var ghost = new Canvas { IsHitTestVisible = false, Opacity = SignatureGhostOpacity };
            if (signature.ImageData is not null)
            {
                var placed = new SignatureAnnotation { ImageData = signature.ImageData };
                if (GetAnnotationBitmap(placed, signature.ImageData) is { } bitmap)
                    ghost.Children.Add(new Image
                    {
                        Source = bitmap, Stretch = Stretch.Uniform, IsHitTestVisible = false,
                        Width = signature.CanvasWidth * size, Height = signature.CanvasHeight * size
                    });
            }
            else if (signature.Vectors is { Count: > 0 })
            {
                foreach (var shape in SignatureVectorGeometry.Shapes(signature.Vectors, 0, 0, size))
                    ghost.Children.Add(shape);
            }
            else
            {
                foreach (var stroke in signature.Strokes.Where(stroke => stroke.Count >= 2))
                {
                    var line = new Polyline
                    {
                        Stroke = Brushes.Black, StrokeThickness = signature.StrokeWidth * size,
                        StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round,
                        StrokeEndLineCap = PenLineCap.Round, IsHitTestVisible = false
                    };
                    foreach (var point in stroke) line.Points.Add(new Point(point.X * size, point.Y * size));
                    ghost.Children.Add(line);
                }
            }
            Panel.SetZIndex(ghost, 1000);
            page.Children.Add(ghost);
            page.Cursor = Cursors.None;
            page.MouseLeave += SignatureGhostPage_MouseLeave;
            _signatureGhost = ghost;
            _signatureGhostPage = page;
        }
    }
}
