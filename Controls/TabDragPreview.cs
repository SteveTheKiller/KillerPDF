#nullable enable
using System.Windows;
using System.Windows.Media;

namespace KillerFamily.Windowing
{
    /// <summary>Displays the real tab without maintaining a second tab template.</summary>
    public sealed class TabDragPreview : FrameworkElement
    {
        private VisualBrush? _brush;

        public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
            nameof(Source), typeof(FrameworkElement), typeof(TabDragPreview),
            new FrameworkPropertyMetadata(null,
                FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender,
                SourceChanged));

        public FrameworkElement? Source
        {
            get => (FrameworkElement?)GetValue(SourceProperty);
            set => SetValue(SourceProperty, value);
        }

        private static void SourceChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
        {
            var preview = (TabDragPreview)sender;
            preview._brush = e.NewValue is FrameworkElement source
                ? new VisualBrush(source)
                {
                    ViewboxUnits = BrushMappingMode.Absolute,
                    Viewbox = new Rect(source.RenderSize),
                    Stretch = Stretch.Fill
                }
                : null;
        }

        protected override Size MeasureOverride(Size availableSize)
            => Source?.RenderSize ?? new Size();

        protected override void OnRender(DrawingContext drawingContext)
        {
            base.OnRender(drawingContext);
            if (_brush != null && RenderSize.Width > 0 && RenderSize.Height > 0)
                drawingContext.DrawRectangle(_brush, null, new Rect(RenderSize));
        }
    }
}
