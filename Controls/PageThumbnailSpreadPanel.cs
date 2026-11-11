using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace KillerPDF.Controls;

internal sealed class PageThumbnailSpreadPanel : VirtualizingPanel, IScrollInfo
{
    private const double EstimatedRowHeight = 160;
    private readonly Dictionary<int, double> _rowHeights = new();
    private double[] _tree = [0];
    private readonly Dictionary<UIElement, int> _indices = new();
    private double _width;
    private int? _bringIndex;

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        base.OnItemsChanged(sender, args);
        ClearRowHeights();
        _bringIndex = null;
        var owner = ItemsControl.GetItemsOwner(this);
        _indices.Clear();
        for (var i = InternalChildren.Count - 1; i >= 0; i--)
        {
            var child = InternalChildren[i];
            var index = owner?.ItemContainerGenerator.IndexFromContainer(child) ?? -1;
            if (index < 0) RemoveInternalChildRange(i, 1);
            else _indices[child] = index;
        }
        InvalidateMeasure();
    }

    /// <summary>Counted work for tests: calls to RowTop and the steps they took.</summary>
    internal long RowTopCalls, RowTopSteps;

    private int RowCount => ((ItemsControl.GetItemsOwner(this)?.Items.Count ?? 0) + 1) / 2;

    private double RowTop(int row)
    {
        // Unvisited pages contribute an estimate without creating their containers.
        double top = row * EstimatedRowHeight;
        RowTopCalls++;
        // A prefix sum over the measured rows' differences from the estimate (a Fenwick tree), so a
        // call costs about log2(rows) steps however many rows have been measured.
        for (int i = Math.Min(row, _tree.Length - 1); i > 0; i -= i & -i)
        {
            RowTopSteps++;
            top += _tree[i];
        }
        return top;
    }

    private void SetRowHeight(int row, double height)
    {
        int rows = RowCount;
        if (_tree.Length != rows + 1) RebuildTree(rows);
        double delta = height - (_rowHeights.TryGetValue(row, out double known) ? known : EstimatedRowHeight);
        _rowHeights[row] = height;
        if (delta == 0) return;
        for (int i = row + 1; i < _tree.Length; i += i & -i) _tree[i] += delta;
    }

    private void ClearRowHeights()
    {
        _rowHeights.Clear();
        Array.Clear(_tree);
    }

    private void RebuildTree(int rows)
    {
        _tree = new double[rows + 1];
        foreach (var pair in _rowHeights)
            if (pair.Key < rows)
                for (int i = pair.Key + 1; i < _tree.Length; i += i & -i) _tree[i] += pair.Value - EstimatedRowHeight;
    }

    private int RowAt(double offset)
    {
        var low = 0;
        var high = Math.Max(0, RowCount - 1);
        while (low < high)
        {
            var middle = (low + high + 1) / 2;
            if (RowTop(middle) <= offset) low = middle; else high = middle - 1;
        }
        return low;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _ = InternalChildren;
        var count = ItemsControl.GetItemsOwner(this)?.Items.Count ?? 0;
        if (_width != availableSize.Width)
        {
            ClearRowHeights();
            _width = availableSize.Width;
        }
        ViewportWidth = double.IsFinite(availableSize.Width) ? availableSize.Width : Math.Max(1, ActualWidth);
        ViewportHeight = double.IsFinite(availableSize.Height) ? availableSize.Height : Math.Max(1, ActualHeight);
        var atEnd = VerticalOffset > 0 && VerticalOffset >= Math.Max(0, ExtentHeight - ViewportHeight);
        var generator = ItemContainerGenerator;
        var retained = new HashSet<UIElement>();
        ExtentHeight = RowTop(RowCount);
        if (_bringIndex is { } target) VerticalOffset = RowTop(target / 2);
        VerticalOffset = Math.Clamp(VerticalOffset, 0, Math.Max(0, ExtentHeight - ViewportHeight));
        int first;
        do
        {
            first = Math.Max(0, RowAt(VerticalOffset) - 1);
            var lastVisible = RowAt(VerticalOffset + ViewportHeight);
            for (var row = first; row * 2 < count && (RowTop(row) <= VerticalOffset + ViewportHeight || row <= lastVisible + 1); row++)
            {
                double height = 0;
                for (var i = row * 2; i < Math.Min(count, row * 2 + 2); i++)
                {
                    var position = generator.GeneratorPositionFromIndex(i);
                    using (generator.StartAt(position, GeneratorDirection.Forward, true))
                    {
                        var child = (UIElement)generator.GenerateNext(out var fresh);
                        if (fresh || !InternalChildren.Contains(child))
                        {
                            var insert = position.Offset == 0 ? position.Index : position.Index + 1;
                            InsertInternalChild(insert, child);
                            generator.PrepareItemContainer(child);
                        }
                        _indices[child] = i;
                        retained.Add(child);
                        child.Measure(new Size(ViewportWidth / 2, double.PositiveInfinity));
                        height = Math.Max(height, child.DesiredSize.Height);
                    }
                }
                SetRowHeight(row, Math.Max(1, height));
            }
            ExtentHeight = RowTop(RowCount);
            VerticalOffset = _bringIndex is { } requested
                ? Math.Clamp(RowTop(requested / 2), 0, Math.Max(0, ExtentHeight - ViewportHeight))
                : atEnd ? Math.Max(0, ExtentHeight - ViewportHeight) : Math.Clamp(VerticalOffset, 0, Math.Max(0, ExtentHeight - ViewportHeight));
        } while (first != Math.Max(0, RowAt(VerticalOffset) - 1));
        _bringIndex = null;
        var finalFirst = Math.Max(0, RowAt(VerticalOffset) - 1);
        var finalLast = Math.Min(RowCount - 1, RowAt(VerticalOffset + ViewportHeight) + 1);
        for (var i = InternalChildren.Count - 1; i >= 0; i--)
        {
            var child = InternalChildren[i];
            if (retained.Contains(child) && _indices[child] / 2 >= finalFirst && _indices[child] / 2 <= finalLast) continue;
            var position = generator.GeneratorPositionFromIndex(_indices[child]);
            if (generator is IRecyclingItemContainerGenerator recycling) recycling.Recycle(position, 1);
            else generator.Remove(position, 1);
            _indices.Remove(child);
            RemoveInternalChildRange(i, 1);
        }
        ScrollOwner?.InvalidateScrollInfo();
        return new Size(ViewportWidth, Math.Min(ViewportHeight, ExtentHeight));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (UIElement child in InternalChildren)
        {
            var index = _indices[child];
            var y = RowTop(index / 2) - VerticalOffset;
            child.Arrange(new Rect(index % 2 * finalSize.Width / 2, y, finalSize.Width / 2, _rowHeights[index / 2]));
        }
        return finalSize;
    }

    public bool CanHorizontallyScroll { get; set; }
    public bool CanVerticallyScroll { get; set; }
    public double ExtentWidth => ViewportWidth;
    public double ExtentHeight { get; private set; }
    public double ViewportWidth { get; private set; }
    public double ViewportHeight { get; private set; }
    public double HorizontalOffset => 0;
    public double VerticalOffset { get; private set; }
    public ScrollViewer? ScrollOwner { get; set; }
    public void SetHorizontalOffset(double offset) { }
    public void SetVerticalOffset(double offset)
    {
        if (double.IsNaN(offset)) return;
        var clamped = Math.Clamp(offset, 0, Math.Max(0, ExtentHeight - ViewportHeight));
        if (clamped == VerticalOffset) return;
        VerticalOffset = clamped;
        InvalidateMeasure();
        ScrollOwner?.InvalidateScrollInfo();
    }
    public void LineUp() => SetVerticalOffset(VerticalOffset - 16);
    public void LineDown() => SetVerticalOffset(VerticalOffset + 16);
    public void PageUp() => SetVerticalOffset(VerticalOffset - ViewportHeight);
    public void PageDown() => SetVerticalOffset(VerticalOffset + ViewportHeight);
    public void MouseWheelUp() => SetVerticalOffset(VerticalOffset - 48);
    public void MouseWheelDown() => SetVerticalOffset(VerticalOffset + 48);
    public void LineLeft() { }
    public void LineRight() { }
    public void PageLeft() { }
    public void PageRight() { }
    public void MouseWheelLeft() { }
    public void MouseWheelRight() { }
    public Rect MakeVisible(System.Windows.Media.Visual visual, Rect rectangle)
    {
        if (visual is null || rectangle.IsEmpty || !IsAncestorOf(visual)) return Rect.Empty;
        var bounds = visual.TransformToAncestor(this).TransformBounds(rectangle);
        var top = bounds.Top + VerticalOffset;
        var bottom = bounds.Bottom + VerticalOffset;
        if (bounds.Height > ViewportHeight || top < VerticalOffset) SetVerticalOffset(top);
        else if (bottom > VerticalOffset + ViewportHeight) SetVerticalOffset(bottom - ViewportHeight);
        bounds.Y = top - VerticalOffset;
        bounds.Intersect(new Rect(0, 0, ViewportWidth, ViewportHeight));
        return bounds;
    }

    protected override void BringIndexIntoView(int index)
    {
        var count = ItemsControl.GetItemsOwner(this)?.Items.Count ?? 0;
        if (index < 0 || index >= count) return;
        _bringIndex = index;
        InvalidateMeasure();
    }
}
