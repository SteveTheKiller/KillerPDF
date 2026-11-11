using System.Linq;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using KillerPDF.Controls;
using Xunit;

namespace KillerPDF.Tests;

// The page windows' shared layout (redesign spec 2.2 and 5): two fixed columns filled row-major,
// keyboard section headers, one footer row, a preview that never sets the window's height, and a
// page fitted with 16 of margin. Nothing here is shown.
public sealed class PageWindowLayoutTests
{
    // UI2-002: Custom 150% of an 816 x 1056 DIP page on a 768 x 1008 printable area is 1224 x 1584,
    // clipped by the sheet; it used to be shrunk to fit. Actual size of the page is 816 x 1056.
    [Theory]
    [InlineData(1224, 1584)]
    [InlineData(816, 1056)]
    public void APageLargerThanThePrintableAreaKeepsItsScaledSize(double width, double height)
    {
        var box = PageWindowLayout.PrintedImageBox(width, height, 768, 1008);

        Assert.Equal((width, height), box);
    }

    // The hairline snap stays for a page within 1.5 of the area on each axis.
    [Theory]
    [InlineData(767, 1007)]
    [InlineData(768, 1008)]
    [InlineData(769.4, 1009.4)]
    public void APageWithinAPixelAndAHalfOfTheAreaSnapsToItPlusABleedPixel(double width, double height)
    {
        Assert.Equal((769.0, 1009.0), PageWindowLayout.PrintedImageBox(width, height, 768, 1008));
    }

    [Fact]
    public void APageSmallerThanTheAreaIsLeftAlone()
    {
        Assert.Equal((700.0, 900.0), PageWindowLayout.PrintedImageBox(700, 900, 768, 1008));
    }

    // ui BF-005: the two-up label reads "Pages 1-5" for pages 1 and 5, which says there are five.
    [Theory]
    [InlineData(3, 4, "Str_PagesOf")]
    [InlineData(1, 5, "Str_Print_PagesAndOf")]
    [InlineData(4, 3, "Str_Print_PagesAndOf")]
    [InlineData(7, 7, "Str_Print_PagesAndOf")]
    public void TheTwoPageLabelSaysARangeOnlyForPagesInARow(int first, int second, string key)
    {
        Assert.Equal(key, PageWindowLayout.PrintPagePairLabelKey(first, second));
    }

    // Print fixed this for gradient themes; Transform and Stamp still cast every lookup.
    [Fact]
    public void AGradientThemeBrushIsUsedAsItIsInsteadOfThrowing()
    {
        var gradient = new LinearGradientBrush(Colors.Black, Colors.White, 90);

        Assert.Same(gradient, PageWindowLayout.ThemeBrush(gradient));
        Assert.Same(Brushes.Transparent, PageWindowLayout.ThemeBrush(null));
        Assert.Same(Brushes.Transparent, PageWindowLayout.ThemeBrush("not a brush"));
    }

    // Where a raw color is needed (a tint drawn from the accent), a gradient gives its first stop,
    // as Print's RColor does, and anything else the fallback.
    [Fact]
    public void AThemeColorComesFromASolidBrushOrAGradientsFirstStop()
    {
        var gradient = new LinearGradientBrush(Color.FromRgb(0x38, 0x75, 0x52), Color.FromRgb(0x10, 0x4E, 0x2B), 90);

        Assert.Equal(Color.FromRgb(0x22, 0xBB, 0x56), PageWindowLayout.ThemeColor(new SolidColorBrush(Color.FromRgb(0x22, 0xBB, 0x56)), Colors.Gray));
        Assert.Equal(Color.FromRgb(0x38, 0x75, 0x52), PageWindowLayout.ThemeColor(gradient, Colors.Gray));
        Assert.Equal(Colors.Gray, PageWindowLayout.ThemeColor(null, Colors.Gray));
    }

    // Letter page in a 500 x 700 frame: 468 x 668 is left inside the 16 margins, and the width
    // binds first (468 / 612 = 0.7647 against 668 / 792 = 0.8434), so 468 x 605.65.
    [Fact]
    public void ThePageFillsTheFrameLessSixteenOnEverySide()
    {
        Size page = PageWindowLayout.FitPage(new Size(500, 700), new Size(612, 792), new Size(612, 792));

        Assert.Equal(468, page.Width, 2);
        Assert.Equal(605.65, page.Height, 2);
    }

    // Scaled to 50%, the page keeps the original's fit factor, so it is drawn half as big:
    // 306 x 0.7647 = 234, 396 x 0.7647 = 302.82.
    [Fact]
    public void AHalfSizePageIsDrawnAtHalfTheFittedSize()
    {
        Size page = PageWindowLayout.FitPage(new Size(500, 700), new Size(612, 792), new Size(306, 396));

        Assert.Equal(234, page.Width, 2);
        Assert.Equal(302.82, page.Height, 2);
    }

    // Turned a quarter, the page would be 792 x 0.7647 = 605.65 wide, past the 468 available,
    // so it shrinks by 468 / 605.65 = 0.7727 to 468 x 361.64.
    [Fact]
    public void ATurnedPageIsClampedInsideTheFrame()
    {
        Size page = PageWindowLayout.FitPage(new Size(500, 700), new Size(612, 792), new Size(792, 612));

        Assert.Equal(468, page.Width, 2);
        Assert.Equal(361.64, page.Height, 2);
    }

    // Rows: [47 | 30] = 47, [60 spanning] = 60, [20 | collapsed] = 20, [40] = 40, so 167 tall. The
    // right column starts at 206 + 12 = 218, a spanning cell is 424 wide, and the collapsed cell
    // keeps its slot so the cell after it starts a new row.
    [Fact]
    public void CellsFillTwoColumnsLeftThenRightRowByRow()
    {
        RunSta(() =>
        {
            var cells = new[] { Cell(47), Cell(30), Cell(60), Cell(20), Cell(50), Cell(40) };
            TwoColumnPanel.SetSpan(cells[2], true);
            cells[4].Visibility = Visibility.Collapsed;
            var panel = new TwoColumnPanel { Width = 424 };
            foreach (var cell in cells) panel.Children.Add(cell);

            panel.Measure(new Size(424, double.PositiveInfinity));
            panel.Arrange(new Rect(panel.DesiredSize));

            Assert.Equal(new Size(424, 167), panel.DesiredSize);
            Assert.Equal(new Rect(0, 0, 206, 47), Slot(cells[0]));
            Assert.Equal(new Rect(218, 0, 206, 30), Slot(cells[1]));
            Assert.Equal(new Rect(0, 47, 424, 60), Slot(cells[2]));
            Assert.Equal(new Rect(0, 107, 206, 20), Slot(cells[3]));
            Assert.Equal(new Rect(0, 127, 206, 40), Slot(cells[5]));
        });
    }

    // The narrow fallback is one 256 column, every cell its own row: 47 + 30 + 60 + 20 + 0 + 40.
    [Fact]
    public void OneColumnStacksEveryCell()
    {
        RunSta(() =>
        {
            var cells = new[] { Cell(47), Cell(30), Cell(60), Cell(20), Cell(50), Cell(40) };
            TwoColumnPanel.SetSpan(cells[2], true);
            cells[4].Visibility = Visibility.Collapsed;
            var host = new Border();
            TwoColumnPanel.SetColumns(host, 1);
            var panel = new TwoColumnPanel();
            foreach (var cell in cells) panel.Children.Add(cell);
            host.Child = panel;

            host.Measure(new Size(256, double.PositiveInfinity));
            host.Arrange(new Rect(host.DesiredSize));

            Assert.Equal(new Size(256, 197), panel.DesiredSize);
            Assert.Equal(new Rect(0, 47, 256, 30), Slot(cells[1]));
            Assert.Equal(new Rect(0, 157, 256, 40), Slot(cells[5]));
        });
    }

    // Spec 5: section headers are real buttons that Tab reaches and that report ExpandCollapse.
    [Fact]
    public void ASectionHeaderIsAKeyboardButtonThatExpandsAndCollapses()
    {
        RunSta(() =>
        {
            var body = new Border { Height = 40 };
            PageWindowLayout.Section("ROTATE", body, out FrameworkElement header);

            var button = Assert.IsAssignableFrom<ToggleButton>(header);
            Assert.True(button.Focusable);
            Assert.True(button.IsTabStop);
            Assert.Equal("ROTATE", AutomationProperties.GetName(button));
            Assert.Equal(Visibility.Visible, body.Visibility);

            var peer = UIElementAutomationPeer.CreatePeerForElement(button);
            var expand = Assert.IsAssignableFrom<IExpandCollapseProvider>(peer.GetPattern(PatternInterface.ExpandCollapse));
            Assert.Equal(ExpandCollapseState.Expanded, expand.ExpandCollapseState);

            expand.Collapse();
            Assert.Equal(Visibility.Collapsed, body.Visibility);
            Assert.Equal(ExpandCollapseState.Collapsed, expand.ExpandCollapseState);
            button.IsChecked = true;
            Assert.Equal(Visibility.Visible, body.Visibility);
        });
    }

    // ui BF-004: a screen reader is told when a section opens or closes, not only when it is asked.
    [Fact]
    public void ASectionHeaderAnnouncesEveryExpandAndCollapse()
    {
        RunSta(() =>
        {
            PageWindowLayout.Section("ROTATE", new Border(), out FrameworkElement header);
            var button = (ToggleButton)header;
            var changes = new List<(ExpandCollapseState Old, ExpandCollapseState New)>();
            ((SectionHeader)button).ExpandCollapseChanged += (o, n) => changes.Add((o, n));

            button.IsChecked = false;
            button.IsChecked = true;

            Assert.Equal(
            [
                (ExpandCollapseState.Expanded, ExpandCollapseState.Collapsed),
                (ExpandCollapseState.Collapsed, ExpandCollapseState.Expanded),
            ], changes);
        });
    }

    // One row (spec 2.2): Reset all on the left as a button Tab reaches, then Cancel and the
    // primary 8 apart, the primary's right edge 16 in from the 456 column (440).
    [Fact]
    public void TheFooterIsOneRowWithResetAllThenCancelThenApply()
    {
        RunSta(() =>
        {
            int resets = 0;
            var link = PageWindowLayout.LinkButton("Reset all", () => resets++);
            var cancel = new Button { Width = 80, Height = 28 };
            var apply = new Button { Width = 90, Height = 28 };
            var footer = new Border { Width = 456, Child = PageWindowLayout.Footer(link, cancel, apply) };

            footer.Measure(new Size(456, double.PositiveInfinity));
            footer.Arrange(new Rect(0, 0, 456, footer.DesiredSize.Height));

            var linkButton = Assert.IsAssignableFrom<Button>(link);
            Assert.True(linkButton.Focusable);
            linkButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal(1, resets);
            Rect l = Bounds(link, footer), c = Bounds(cancel, footer), a = Bounds(apply, footer);
            Assert.Equal(c.Top + c.Height / 2, l.Top + l.Height / 2, 0);
            Assert.Equal(c.Top, a.Top);
            Assert.True(l.Right <= c.Left);
            Assert.Equal(8, a.Left - c.Right, 3);
            Assert.Equal(440, a.Right, 3);
        });
    }

    // Spec 2.3: the window is as tall as the open options plus the footer. A preview that would
    // like 2000 never adds to that, and the options column keeps its 456 width.
    [Fact]
    public void TheBodyIsAsTallAsTheOptionsAndFooterWhateverThePreviewWants()
    {
        RunSta(() =>
        {
            var options = new Border { Width = 424, Height = 500 };
            var footer = new Border { Height = 52 };
            var preview = new Border { Width = 1500, Height = 2000 };
            var body = PageWindowLayout.Body(options, footer, preview, out Border column);

            body.Measure(new Size(1200, double.PositiveInfinity));
            body.Arrange(new Rect(0, 0, 1200, body.DesiredSize.Height));

            Assert.Equal(552, body.DesiredSize.Height, 3);
            Assert.Equal(456, column.ActualWidth, 3);
            Assert.Equal(744, Bounds(column, body).Left, 3);
        });
    }

    // Spec 2.2, default width: the preview widens until the page fills the frame's height. A 266 x 600
    // frame leaves 568 of height inside the 16 margins; a Letter page that tall is 568 x 612 / 792 =
    // 438.91 wide, so the frame needs 438.91 + 32 = 470.91, which is 204.91 more than it has.
    [Fact]
    public void TheDefaultWidthGrowsUntilThePageFillsTheFrameHeight()
    {
        Assert.Equal(204.91, PageWindowLayout.FittedExtraWidth(new Size(266, 600), new Size(612, 792)), 2);
    }

    // A frame already wide enough for the full-height page asks for nothing more.
    [Fact]
    public void AWideEnoughFrameNeedsNoExtraWidth()
    {
        Assert.Equal(0, PageWindowLayout.FittedExtraWidth(new Size(800, 600), new Size(612, 792)));
    }

    // Spec 5: Tab runs through the options, then the footer, then the preview's nav arrows, on either
    // side. Tab follows the visual tree when no TabIndex is set, so that is the order checked.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TabGoesThroughTheOptionsThenTheFooterThenThePreview(bool optionsLeft)
    {
        RunSta(() =>
        {
            var option = new Button { Content = "option" };
            var footerButton = new Button { Content = "footer" };
            var navArrow = new Button { Content = "nav" };
            var body = PageWindowLayout.Body(new StackPanel { Children = { option } }, new StackPanel { Children = { footerButton } },
                new StackPanel { Children = { navArrow } }, out _, optionsLeft: optionsLeft);
            body.Measure(new Size(1200, 800));
            body.Arrange(new Rect(0, 0, 1200, 800));

            var order = VisualOrder<Button>(body).Select(b => (string)b.Content).ToArray();

            Assert.Equal(new[] { "option", "footer", "nav" }, order);
        });
    }

    // Print keeps its options on the left on purpose; the preview then takes the right.
    [Fact]
    public void PrintsOptionsSitOnTheLeftWithThePreviewOnTheRight()
    {
        RunSta(() =>
        {
            var options = new Border { Width = 424, Height = 500 };
            var preview = new Border { Height = 2000 };
            var body = PageWindowLayout.Body(options, new Border { Height = 52 }, preview, out Border column, optionsLeft: true);

            body.Measure(new Size(1200, double.PositiveInfinity));
            body.Arrange(new Rect(0, 0, 1200, body.DesiredSize.Height));

            Assert.Equal(552, body.DesiredSize.Height, 3);
            Assert.Equal(0, Bounds(column, body).Left, 3);
            Assert.Equal(456, Bounds(preview, body).Left, 3);
            Assert.Equal(744, preview.ActualWidth, 3);
        });
    }

    private static IEnumerable<T> VisualOrder<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in VisualOrder<T>(child)) yield return nested;
        }
    }

    private static Border Cell(double height) => new() { Height = height };

    private static Rect Slot(FrameworkElement element)
        => new(element.TranslatePoint(new Point(), (UIElement)element.Parent), element.RenderSize);

    private static Rect Bounds(FrameworkElement element, Visual root)
        => element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
