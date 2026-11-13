using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using KillerPDF.Controls;
using Xunit;

namespace KillerPDF.Tests;

public sealed class PageThumbnailSpreadPanelTests
{
    [Fact]
    public void RecycledFarRowContainersRemainAttachedAndArranged() => OnSta(() =>
    {
        var list = Create(0);
        VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
        var thumbnail = new FrameworkElementFactory(typeof(Border));
        thumbnail.SetValue(FrameworkElement.HeightProperty, 40d);
        list.ItemTemplate = new DataTemplate { VisualTree = thumbnail };
        var pages = Enumerable.Range(0, 1000).Select(_ => new object()).ToArray();
        list.ItemsSource = pages;
        Layout(list, 200, 200);
        var panel = Find<PageThumbnailSpreadPanel>(list)!;
        foreach (int index in new[] { 500, 998, 0, 998 })
        {
            list.ScrollIntoView(pages[index]);
            Layout(list, 200, 200);
            var target = Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromIndex(index));
            Assert.Same(panel, VisualTreeHelper.GetParent(target));
            Assert.True(target.IsArrangeValid);
            Assert.InRange(target.TranslatePoint(new Point(), panel).Y, 0, 160);
            Assert.Same(pages[index], list.ItemContainerGenerator.ItemFromContainer(target));
        }
    });

    [Fact]
    public void DataItemsKeepIdentityAndExtendedSelectionWhileRecycling() => OnSta(() =>
    {
        var list = Create(0);
        var thumbnail = new FrameworkElementFactory(typeof(Border));
        thumbnail.SetValue(FrameworkElement.HeightProperty, 40d);
        list.ItemTemplate = new DataTemplate { VisualTree = thumbnail };
        list.SelectionMode = SelectionMode.Extended;
        var pages = new object[10000];
        for (var i = 0; i < pages.Length; i++) pages[i] = new object();
        list.ItemsSource = pages;
        Layout(list, 200, 200);
        list.SelectedItems.Add(pages[1]);
        list.SelectedItems.Add(pages[8765]);
        list.ScrollIntoView(pages[8765]);
        Layout(list, 200, 200);
        Layout(list, 120, 240);
        list.ScrollIntoView(pages[8765]);
        Layout(list, 120, 240);
        var panel = Find<PageThumbnailSpreadPanel>(list)!;
        var target = Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromIndex(8765));
        Assert.Same(pages[8765], list.ItemContainerGenerator.ItemFromContainer(target));
        Assert.Equal(60, target.TranslatePoint(new Point(), panel).X);
        Assert.Equal(2, list.SelectedItems.Count);
        Assert.True(list.SelectedItems.Contains(pages[1]));
        Assert.True(list.SelectedItems.Contains(pages[8765]));
        Assert.Equal(10000, list.Items.Count);
        Assert.InRange(panel.Children.Count, 2, 24);
    });

    // viewer BF-012: RowTop added up every measured row on each call, so a long document that had been
    // scrolled through made every layout pass cost rows x calls. The work per call is logarithmic now.
    [Fact]
    public void RowTopWorkDoesNotGrowWithTheNumberOfMeasuredRows() => OnSta(() =>
    {
        var list = Create(0);
        var thumbnail = new FrameworkElementFactory(typeof(Border));
        thumbnail.SetValue(FrameworkElement.HeightProperty, 40d);
        list.ItemTemplate = new DataTemplate { VisualTree = thumbnail };
        var pages = Enumerable.Range(0, 10000).Select(_ => new object()).ToArray();
        list.ItemsSource = pages;
        Layout(list, 200, 200);
        for (int index = 0; index < 1000; index += 8)
        {
            list.ScrollIntoView(pages[index]);
            Layout(list, 200, 200);
        }
        var panel = Find<PageThumbnailSpreadPanel>(list)!;
        panel.RowTopCalls = panel.RowTopSteps = 0;

        list.ScrollIntoView(pages[400]);
        Layout(list, 200, 200);

        Assert.True(panel.RowTopCalls > 0);
        // 5000 rows: a prefix sum over them takes at most 13 steps, and one more for the loop's own bound.
        Assert.True(panel.RowTopSteps <= panel.RowTopCalls * 14,
            $"{panel.RowTopSteps} steps for {panel.RowTopCalls} calls");
    });

    [Fact]
    public void ReplacingDocumentDiscardsPreviousRowsHeightCache() => OnSta(() =>
    {
        var list = Create(1000);
        Layout(list, 200, 200);
        list.Items.Clear();
        var replacement = new Border[1000];
        for (var i = 0; i < replacement.Length; i++) replacement[i] = new Border { Height = 100 };
        list.ItemsSource = replacement;
        Layout(list, 200, 200);
        var panel = Find<PageThumbnailSpreadPanel>(list)!;
        var fresh = Create(1000);
        foreach (Border thumbnail in fresh.Items) thumbnail.Height = 100;
        Layout(fresh, 200, 200);
        var freshPanel = Find<PageThumbnailSpreadPanel>(fresh)!;
        Assert.Equal(((IScrollInfo)freshPanel).ExtentHeight, ((IScrollInfo)panel).ExtentHeight);
        Assert.InRange(panel.Children.Count, 4, 8);
    });

    [Fact]
    public void ScrollViewerOwnsPixelMetricsAndScrollsFarItem() => OnSta(() =>
    {
        var list = Create(10000);
        var viewerFactory = new FrameworkElementFactory(typeof(ScrollViewer));
        viewerFactory.SetValue(ScrollViewer.CanContentScrollProperty, true);
        viewerFactory.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
        viewerFactory.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        viewerFactory.AppendChild(new FrameworkElementFactory(typeof(ItemsPresenter)));
        list.Template = new ControlTemplate(typeof(ListBox)) { VisualTree = viewerFactory };
        Layout(list, 200, 200);
        var panel = Find<PageThumbnailSpreadPanel>(list)!;
        var viewer = Find<ScrollViewer>(list)!;
        Assert.Same(viewer, ((IScrollInfo)panel).ScrollOwner);
        Assert.Equal(200, viewer.ViewportHeight);
        list.ScrollIntoView(list.Items[8765]);
        Layout(list, 200, 200);
        Assert.NotNull(list.ItemContainerGenerator.ContainerFromIndex(8765));
        Assert.InRange(panel.Children.Count, 2, 20);
        Assert.True(viewer.VerticalOffset > 10000);
    });

    [Fact]
    public void UnevenRowsAndOddLastItemUseActualMaximumHeight() => OnSta(() =>
    {
        var list = Create(5);
        ((Border)list.Items[0]).Height = 90;
        ((Border)list.Items[1]).Height = 20;
        ((Border)list.Items[2]).Height = 30;
        ((Border)list.Items[3]).Height = 70;
        ((Border)list.Items[4]).Height = 55;
        Layout(list, 200, 300);
        var panel = Find<PageThumbnailSpreadPanel>(list)!;
        var second = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(1);
        var third = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(2);
        var fifth = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(4);
        Assert.Equal(90, second.ActualHeight);
        Assert.Equal(90, third.TranslatePoint(new Point(), panel).Y);
        Assert.Equal(70, third.ActualHeight);
        Assert.Equal(new Point(0, 160), fifth.TranslatePoint(new Point(), panel));
        Assert.Equal(55, fifth.ActualHeight);
        Assert.Equal(215, ((IScrollInfo)panel).ExtentHeight);
        Assert.Equal(5, panel.Children.Count);
    });

    [Fact]
    public void ResizeRemeasuresBothColumnsAtNewHalfWidth() => OnSta(() =>
    {
        var list = Create(0);
        for (var i = 0; i < 5; i++) list.Items.Add(new SquareThumbnail());
        Layout(list, 200, 500);
        var panel = Find<PageThumbnailSpreadPanel>(list)!;
        Assert.Equal(300, ((IScrollInfo)panel).ExtentHeight);
        Layout(list, 120, 500);
        var second = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(1);
        var fifth = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(4);
        Assert.Equal(60, second.ActualWidth);
        Assert.Equal(60, second.ActualHeight);
        Assert.Equal(new Point(60, 0), second.TranslatePoint(new Point(), panel));
        Assert.Equal(new Point(0, 120), fifth.TranslatePoint(new Point(), panel));
        Assert.Equal(180, ((IScrollInfo)panel).ExtentHeight);
    });

    private sealed class SquareThumbnail : FrameworkElement
    {
        protected override Size MeasureOverride(Size availableSize) => new(availableSize.Width, availableSize.Width);
    }

    [Fact]
    public void RemovalAndResetKeepOriginalItemMappingAndClampExtent() => OnSta(() =>
    {
        var list = Create(1000);
        Layout(list, 200, 200);
        list.ScrollIntoView(list.Items[999]);
        Layout(list, 200, 200);
        list.Items.RemoveAt(0);
        Layout(list, 200, 200);
        list.ScrollIntoView(list.Items[998]);
        Layout(list, 200, 200);
        var last = list.ItemContainerGenerator.ContainerFromIndex(998);
        Assert.NotNull(last);
        Assert.Same(list.Items[998], list.ItemContainerGenerator.ItemFromContainer(last));
        list.Items.Clear();
        list.Items.Add(new Border { Height = 70 });
        Layout(list, 200, 200);
        var panel = Find<PageThumbnailSpreadPanel>(list)!;
        Assert.Single(panel.Children);
        Assert.Equal(0, ((IScrollInfo)panel).VerticalOffset);
        Assert.Equal(70, ((IScrollInfo)panel).ExtentHeight);
    });

    [Fact]
    public void MakeVisibleScrollsRealizedBufferItemIntoViewport() => OnSta(() =>
    {
        var list = Create(100);
        Layout(list, 200, 200);
        var panel = Find<PageThumbnailSpreadPanel>(list)!;
        var target = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(10);
        var scroll = (IScrollInfo)panel;
        scroll.MakeVisible(target, new Rect(0, 0, 100, 40));
        Layout(list, 200, 200);
        Assert.Equal(40, scroll.VerticalOffset);
        Assert.Equal(160, target.TranslatePoint(new Point(), panel).Y);
    });

    [Fact]
    public void ScrollIntoViewRealizesFarOriginalItem() => OnSta(() =>
    {
        var list = Create(10000);
        Layout(list, 200, 200);
        list.ScrollIntoView(list.Items[8765]);
        Layout(list, 200, 200);
        var panel = Find<PageThumbnailSpreadPanel>(list)!;
        var target = Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromIndex(8765));
        Assert.Same(list.Items[8765], list.ItemContainerGenerator.ItemFromContainer(target));
        var point = target.TranslatePoint(new Point(), panel);
        Assert.Equal(100, point.X);
        Assert.InRange(point.Y, 0, 160);
        Assert.InRange(panel.Children.Count, 2, 20);
    });

    [Fact]
    public void PixelScrollClampsAndRecyclesOriginalContainers() => OnSta(() =>
    {
        var list = Create(10000);
        Layout(list, 200, 200);
        var panel = Find<PageThumbnailSpreadPanel>(list)!;
        var scroll = Assert.IsAssignableFrom<IScrollInfo>(panel);
        scroll.SetVerticalOffset(double.PositiveInfinity);
        Layout(list, 200, 200);
        Assert.InRange(panel.Children.Count, 2, 20);
        Assert.NotNull(list.ItemContainerGenerator.ContainerFromIndex(9999));
        Assert.Null(list.ItemContainerGenerator.ContainerFromIndex(0));
        Assert.Equal(scroll.ExtentHeight - 200, scroll.VerticalOffset);
        scroll.SetVerticalOffset(-20);
        Layout(list, 200, 200);
        Assert.Equal(0, scroll.VerticalOffset);
        Assert.NotNull(list.ItemContainerGenerator.ContainerFromIndex(0));
    });

    [Fact]
    public void LongDocumentRealizesOnlyViewportAndBuffer() => OnSta(() =>
    {
        var list = Create(10000);
        Layout(list, 200, 200);
        var panel = Find<PageThumbnailSpreadPanel>(list)!;
        Assert.InRange(panel.Children.Count, 10, 20);
        Assert.Equal(10000, list.Items.Count);
        Assert.Null(list.ItemContainerGenerator.ContainerFromIndex(9999));
    });

    [Fact]
    public void OriginalItemsOccupyTwoEqualWidthColumns() => OnSta(() =>
    {
        var list = Create(2);
        Layout(list, 200, 200);
        var panel = Find<PageThumbnailSpreadPanel>(list)!;
        Assert.Equal(2, panel.Children.Count);
        var first = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
        var second = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(1);
        Assert.Equal(0, first.TranslatePoint(new Point(), panel).X);
        Assert.Equal(100, second.TranslatePoint(new Point(), panel).X);
        Assert.Equal(100, second.ActualWidth);
        Assert.Same(list.Items[1], list.ItemContainerGenerator.ItemFromContainer(second));
    });

    [Fact]
    public void BookCoverIsCenteredBeforeTheRemainingPairs() => OnSta(() =>
    {
        var list = Create(5);
        Layout(list, 200, 240);
        var panel = Find<PageThumbnailSpreadPanel>(list)!;
        panel.FirstPageAlone = true;
        Layout(list, 200, 240);
        var cells = Enumerable.Range(0, 5).Select(i =>
            (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(i)).ToArray();
        Assert.Equal(50, cells[0].TranslatePoint(new Point(), panel).X);
        Assert.Equal(100, cells[0].ActualWidth);
        Assert.True(cells[1].TranslatePoint(new Point(), panel).Y >= cells[0].ActualHeight);
        Assert.Equal(cells[1].TranslatePoint(new Point(), panel).Y, cells[2].TranslatePoint(new Point(), panel).Y);
        Assert.Equal(0, cells[1].TranslatePoint(new Point(), panel).X);
        Assert.Equal(100, cells[2].TranslatePoint(new Point(), panel).X);
        panel.FirstPageAlone = false;
        Layout(list, 200, 240);
        Assert.Equal(0, cells[0].TranslatePoint(new Point(), panel).X);
        Assert.Equal(cells[0].TranslatePoint(new Point(), panel).Y, cells[1].TranslatePoint(new Point(), panel).Y);
    });

    [Theory]
    [InlineData(1000, 0)]
    [InlineData(1001, 100)]
    public void BookLayoutScrollsToTheLastSpreadWithBoundedRealization(int count, double expectedX) => OnSta(() =>
    {
        var list = Create(count);
        Layout(list, 200, 200);
        var panel = Find<PageThumbnailSpreadPanel>(list)!;
        panel.FirstPageAlone = true;
        Layout(list, 200, 200);
        list.ScrollIntoView(list.Items[count - 1]);
        Layout(list, 200, 200);
        var last = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(count - 1);
        Assert.Equal(expectedX, last.TranslatePoint(new Point(), panel).X);
        Assert.InRange(last.TranslatePoint(new Point(), panel).Y, 0, 160);
        Assert.InRange(panel.Children.Count, 2, 20);
        Assert.Null(list.ItemContainerGenerator.ContainerFromIndex(0));
    });

    private static ListBox Create(int count)
    {
        var list = new ListBox { BorderThickness = new Thickness(0), Padding = new Thickness(0) };
        var presenter = new FrameworkElementFactory(typeof(ItemsPresenter));
        list.Template = new ControlTemplate(typeof(ListBox)) { VisualTree = presenter };
        list.ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(PageThumbnailSpreadPanel)));
        var style = new Style(typeof(ListBoxItem));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        style.Setters.Add(new Setter(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        list.ItemContainerStyle = style;
        for (var i = 0; i < count; i++) list.Items.Add(new Border { Height = 40 });
        return list;
    }

    private static void Layout(ListBox list, double width, double height)
    {
        list.Measure(new Size(width, height));
        list.Arrange(new Rect(0, 0, width, height));
        list.UpdateLayout();
    }

    private static T? Find<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is T match) return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            if (Find<T>(VisualTreeHelper.GetChild(parent, i)) is { } child) return child;
        return null;
    }

    private static void OnSta(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { body(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
