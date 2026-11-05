using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace KillerPDF.Shell;

public partial class MainWindow
{
    private bool _pageBoxActive, _pageBoxDragging, _pageBoxApplying, _pageBoxHooks;
    private Point _pageBoxStart;
    private ScrollContentPresenter? _pageBoxViewport;
    private object? _pageBoxSource;
    private PageThumbnailVm[] _pageBoxOriginal = [];
    private ModifierKeys _pageBoxModifiers;
    private ScrollViewer? _pageBoxScroller;
    private DispatcherTimer? _pageBoxScrollTimer;
    private readonly Dictionary<PageThumbnailVm, Rect> _pageBoxBounds = [];

    private void BeginPageBoxSelection(MouseButtonEventArgs e)
    {
        if (PageList.Items.Count == 0 || (Keyboard.Modifiers & ModifierKeys.Alt) != 0) return;
        var viewport = FindSidebarDescendant<ScrollContentPresenter>(PageList);
        if (viewport is null) return;
        if (!_pageBoxHooks)
        {
            PageList.PreviewMouseLeftButtonUp += PageBoxSelectionReleased;
            PageList.LostMouseCapture += (_, _) => { if (_pageBoxActive && Mouse.Captured != PageList) EndPageBoxSelection(cancel: true); };
            PageList.Unloaded += (_, _) => EndPageBoxSelection(cancel: true);
            Deactivated += (_, _) => EndPageBoxSelection(cancel: true);
            Closed += (_, _) => EndPageBoxSelection(cancel: true);
            _pageBoxHooks = true;
        }
        _pageBoxViewport = viewport;
        _pageBoxScroller = FindPageListScrollViewer(PageList);
        _pageBoxSource = PageList.ItemsSource;
        _pageBoxOriginal = PageList.SelectedItems.Cast<PageThumbnailVm>().ToArray();
        _pageBoxModifiers = Keyboard.Modifiers;
        _pageBoxStart = e.GetPosition(viewport);
        _pageBoxStart.Offset(_pageBoxScroller?.HorizontalOffset ?? 0, _pageBoxScroller?.VerticalOffset ?? 0);
        _pageBoxBounds.Clear();
        _pageBoxDragging = false;
        _pageBoxActive = PageList.CaptureMouse();
        if (_pageBoxActive) { PageList.Focus(); e.Handled = true; }
    }

    private bool UpdatePageBoxSelection(MouseEventArgs e)
    {
        if (!_pageBoxActive || _pageBoxViewport is not { } viewport) return false;
        e.Handled = true;
        if (!ReferenceEquals(_pageBoxSource, PageList.ItemsSource)) { EndPageBoxSelection(cancel: true); return true; }
        if (e.LeftButton != MouseButtonState.Pressed) { EndPageBoxSelection(cancel: false); return true; }
        Point point = e.GetPosition(viewport);
        Point contentPoint = point;
        contentPoint.Offset(_pageBoxScroller?.HorizontalOffset ?? 0, _pageBoxScroller?.VerticalOffset ?? 0);
        if (!_pageBoxDragging && Math.Abs(contentPoint.X - _pageBoxStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(contentPoint.Y - _pageBoxStart.Y) < SystemParameters.MinimumVerticalDragDistance) return true;
        _pageBoxDragging = true;
        RefreshPageBoxSelection(point);
        _pageBoxScrollTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(30), DispatcherPriority.Input,
            (_, _) => ScrollPageBoxSelection(), Dispatcher);
        _pageBoxScrollTimer.Start();
        return true;
    }

    private void RefreshPageBoxSelection(Point point)
    {
        if (_pageBoxViewport is not { } viewport) return;
        double x = _pageBoxScroller?.HorizontalOffset ?? 0, y = _pageBoxScroller?.VerticalOffset ?? 0;
        point = new Point(Math.Clamp(point.X, 0, viewport.ActualWidth), Math.Clamp(point.Y, 0, viewport.ActualHeight));
        Rect region = new(_pageBoxStart, new Point(point.X + x, point.Y + y));
        Rect visible = region;
        visible.Offset(-x, -y);
        visible.Intersect(new Rect(0, 0, viewport.ActualWidth, viewport.ActualHeight));
        if (visible.IsEmpty) { PageSelectionBox.Visibility = Visibility.Collapsed; return; }
        Point origin = viewport.TranslatePoint(visible.TopLeft, PageSelectionCanvas);
        Canvas.SetLeft(PageSelectionBox, origin.X); Canvas.SetTop(PageSelectionBox, origin.Y);
        PageSelectionBox.Width = visible.Width; PageSelectionBox.Height = visible.Height;
        PageSelectionBox.Visibility = Visibility.Visible;
        if (FindSidebarDescendant<VirtualizingPanel>(PageList) is { } panel)
        {
            foreach (var item in panel.Children.OfType<ListBoxItem>())
            {
                if (PageList.ItemContainerGenerator.ItemFromContainer(item) is not PageThumbnailVm page) continue;
                Rect bounds = new(item.TranslatePoint(new Point(), viewport), item.RenderSize);
                if (!bounds.IntersectsWith(new Rect(0, 0, viewport.ActualWidth, viewport.ActualHeight))) continue;
                bounds.Offset(x, y);
                _pageBoxBounds[page] = bounds;
            }
        }
        var selected = _pageBoxBounds.Where(entry => region.IntersectsWith(entry.Value)).Select(entry => entry.Key).ToHashSet();
        if ((_pageBoxModifiers & ModifierKeys.Shift) != 0) selected.UnionWith(_pageBoxOriginal);
        else if ((_pageBoxModifiers & ModifierKeys.Control) != 0) selected.SymmetricExceptWith(_pageBoxOriginal);
        ApplyPageBoxSelection(selected);
    }

    private void ScrollPageBoxSelection()
    {
        if (!_pageBoxActive || _pageBoxViewport is not { } viewport || _pageBoxScroller is not { } scroller) return;
        if (!ReferenceEquals(_pageBoxSource, PageList.ItemsSource)) { EndPageBoxSelection(cancel: true); return; }
        if (Mouse.LeftButton != MouseButtonState.Pressed) { EndPageBoxSelection(cancel: false); return; }
        Point point = Mouse.GetPosition(viewport);
        double along = _sidebarStripLayout ? point.X : point.Y;
        double length = _sidebarStripLayout ? viewport.ActualWidth : viewport.ActualHeight;
        double edge = Math.Min(48, length / 3);
        double velocity = along < edge ? -24 * Math.Clamp((edge - along) / edge, 0, 1)
            : along > length - edge ? 24 * Math.Clamp((along - length + edge) / edge, 0, 1) : 0;
        if (velocity != 0)
        {
            if (_sidebarStripLayout) scroller.ScrollToHorizontalOffset(scroller.HorizontalOffset + velocity);
            else scroller.ScrollToVerticalOffset(scroller.VerticalOffset + velocity);
            PageList.UpdateLayout();
        }
        RefreshPageBoxSelection(point);
    }

    private void ApplyPageBoxSelection(HashSet<PageThumbnailVm> selected)
    {
        _pageBoxApplying = true;
        try
        {
            foreach (PageThumbnailVm item in PageList.SelectedItems.Cast<PageThumbnailVm>().ToArray())
                if (!selected.Contains(item)) PageList.SelectedItems.Remove(item);
            foreach (PageThumbnailVm item in selected.OrderBy(item => item.PageIndex))
                if (!PageList.SelectedItems.Contains(item)) PageList.SelectedItems.Add(item);
        }
        finally { _pageBoxApplying = false; }
    }

    private void PageBoxSelectionReleased(object sender, MouseButtonEventArgs e)
    {
        if (!_pageBoxActive) return;
        e.Handled = true;
        EndPageBoxSelection(cancel: false);
    }

    private void EndPageBoxSelection(bool cancel)
    {
        if (!_pageBoxActive) return;
        bool sameSource = ReferenceEquals(_pageBoxSource, PageList.ItemsSource);
        if (sameSource && cancel) ApplyPageBoxSelection(_pageBoxOriginal.ToHashSet());
        if (sameSource && !cancel && !_pageBoxDragging && _pageBoxModifiers == ModifierKeys.None) ApplyPageBoxSelection([]);
        _pageBoxActive = false;
        _pageBoxDragging = false;
        _pageBoxScrollTimer?.Stop();
        PageSelectionBox.Visibility = Visibility.Collapsed;
        if (Mouse.Captured == PageList) PageList.ReleaseMouseCapture();
        _pageBoxViewport = null;
        _pageBoxScroller = null;
        _pageBoxBounds.Clear();
        _pageBoxSource = null;
        _pageBoxOriginal = [];
        if (!cancel && sameSource && PageList.SelectedIndex >= 0
            && ActiveViewer.CurrentPageIndex != PageList.SelectedIndex)
        {
            _pageBoxApplying = true;
            try
            {
                ActiveViewer.PageListSelectionChangedExt(PageList,
                    new SelectionChangedEventArgs(Selector.SelectionChangedEvent, Array.Empty<object>(), (IList)PageList.SelectedItems));
            }
            finally { _pageBoxApplying = false; }
        }
    }
}
