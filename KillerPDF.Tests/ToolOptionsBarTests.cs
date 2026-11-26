using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using KillerPDF.Controls;
using Xunit;

namespace KillerPDF.Tests;

// The docked tool options bar (1.9.3 redesign, section 3).
public sealed class ToolOptionsBarTests
{
    // UI2-005: focus inside the color chip's popup (or the "..." menu) is not in the bar's visual tree, and
    // the popup goes with the old bar. The rebuilt bar refocuses the button the popup opens from.
    [Theory]
    [InlineData("Color")]
    [InlineData("ToolOptionsMore")]
    public void FocusInAPopupReturnsToTheButtonThatOpensIt(string openerId)
    {
        RunSta(() =>
        {
            var opener = new Button();
            AutomationProperties.SetAutomationId(opener, openerId);
            var swatch = new Button();
            AutomationProperties.SetAutomationId(swatch, "Red");
            var popup = new System.Windows.Controls.Primitives.Popup { PlacementTarget = opener, Child = new Border { Child = swatch } };
            var bar = new Grid();
            bar.Children.Add(opener);
            bar.Children.Add(popup);

            Assert.Equal(openerId, ToolOptionsDock.FocusIdFor(swatch));
        });
    }

    [Fact]
    public void FocusOutsideAPopupKeepsItsOwnId()
    {
        RunSta(() =>
        {
            var mode = new Button();
            AutomationProperties.SetAutomationId(mode, "Highlight");

            Assert.Equal("Highlight", ToolOptionsDock.FocusIdFor(mode));
        });
    }

    // ui BF-003: the control edges came from a fixed gray, so they stayed gray in every theme. They take the
    // family's card border, which is what the Print window's checkboxes use.
    [Fact]
    public void OptionControlEdgesFollowTheThemeBorderBrush()
    {
        RunSta(() =>
        {
            var edge = new SolidColorBrush(Colors.HotPink);
            var host = new StackPanel();
            host.Resources["CardBorderBrush"] = edge;
            var swatch = (ToggleButton)ToolOptionsControls.Swatch(Colors.Red, false, "Red", () => { });
            var step = (Button)ToolOptionsControls.Step("+", "More", () => { });
            var icon = (ToggleButton)ToolOptionsControls.IconToggle(new Border(), false, "Bold", () => { });
            var check = (CheckBox)ToolOptionsControls.Check("Level", false, "Level", _ => { });
            foreach (var c in new Control[] { swatch, step, icon, check }) host.Children.Add(c);

            var chip = (Border)swatch.Content;
            step.ApplyTemplate();
            icon.ApplyTemplate();
            check.ApplyTemplate();

            Assert.Same(edge, chip.BorderBrush);
            Assert.Same(edge, ((Border)step.Template.FindName("face", step)).BorderBrush);
            Assert.Same(edge, ((Border)icon.Template.FindName("face", icon)).BorderBrush);
            Assert.Same(edge, ((Border)check.Template.FindName("box", check)).BorderBrush);
        });
    }

    // A language or theme change rebuilds the open bars from this catalog. The Measure bar is the
    // draw bar with the Measure controls in front, so it has to be rebuilt with them; it used to be
    // left out and kept the old language and accent until the tool was chosen again.
    [Fact]
    public void TheMeasureToolsBarIsRebuiltWithTheOtherDrawBars()
    {
        Assert.Equal(ToolOptionsBarKind.Draw, ToolOptionsCatalog.BarFor(EditTool.Measure));
    }

    // Measured Measure groups for the fit cases below, in row order. Color has a 54-wide chip.
    private static readonly ToolOptionsGroupSpec[] MeasureGroups =
    [
        new(ToolOptionGroup.Modes, 120), new(ToolOptionGroup.Scale, 130), new(ToolOptionGroup.Units, 118),
        new(ToolOptionGroup.PageSize, 140), new(ToolOptionGroup.Report, 60),
        new(ToolOptionGroup.Color, 270, CompactWidth: 54), new(ToolOptionGroup.Size, 150),
        new(ToolOptionGroup.Opacity, 170),
    ];

    // Spec 3.4: Opacity, Size, Page size, Report, Units, Color; modes and Scale never go.
    [Fact]
    public void TheMeasureBarShedsInTheSpecsPriorityOrder()
    {
        Assert.Equal(["opacity", "size", "pagesize", "report", "units", "color"],
            ToolOptionsCatalog.ShedOrder(EditTool.Measure));
    }

    // 16 padding + 1158 of groups + 7 separators of 17 = 1293, so a 1293 pane holds everything as is.
    [Fact]
    public void AWidePaneKeepsEveryGroupInOneRow()
    {
        var fit = ToolOptionsLayout.Assign(1293, MeasureGroups, ToolOptionsCatalog.ShedOrder(EditTool.Measure));

        Assert.Equal(MeasureGroups.Select(g => g.Id), fit.Inline);
        Assert.Empty(fit.Overflow);
        Assert.False(fit.Compact);
    }

    // At 900 the chip saves 216 (1077). Opacity leaves and "..." arrives: 1077 - 187 + 34 = 924, still
    // over. Size leaves too: 924 - 167 = 757, which fits. The menu lists them in row order.
    [Fact]
    public void ANarrowPaneCompactsColorFirstThenShedsInPriorityOrder()
    {
        var fit = ToolOptionsLayout.Assign(900, MeasureGroups, ToolOptionsCatalog.ShedOrder(EditTool.Measure));

        Assert.True(fit.Compact);
        Assert.Equal(["modes", "scale", "units", "pagesize", "report", "color"], fit.Inline);
        Assert.Equal(["size", "opacity"], fit.Overflow);
    }

    // At 300 every sheddable group goes, leaving modes + Scale + "..." = 16 + 120 + 17 + 130 + 34 = 317.
    // Still over, so the never-shed groups go from the right, keeping the first: 16 + 120 + 34 = 170.
    [Fact]
    public void AVeryNarrowPaneKeepsOnlyTheFirstGroupAndTheMenu()
    {
        var fit = ToolOptionsLayout.Assign(300, MeasureGroups, ToolOptionsCatalog.ShedOrder(EditTool.Measure));

        Assert.Equal(["modes"], fit.Inline);
        Assert.Equal(["scale", "units", "pagesize", "report", "color", "size", "opacity"], fit.Overflow);
    }

    // The replacement layer grid is three rows tall, so it lives in the menu even when the row has room.
    [Fact]
    public void AMenuOnlyGroupGoesToTheMenuAtAnyWidth()
    {
        ToolOptionsGroupSpec[] groups = [new(ToolOptionGroup.Font, 200), new(ToolOptionGroup.Layers, 120, OverflowOnly: true)];

        var fit = ToolOptionsLayout.Assign(2000, groups, ToolOptionsCatalog.ShedOrder(EditTool.Text));

        Assert.Equal(["font"], fit.Inline);
        Assert.Equal(["layers"], fit.Overflow);
    }

    // Every tool with a bar has a shed order taken from spec 3.4 (Crop and Redact are not in the spec:
    // their Apply and the readouts never go).
    [Theory]
    [InlineData(EditTool.Text, new[] { "blur", "spacing", "fillopacity", "opacity", "paragraph", "fill" })]
    [InlineData(EditTool.Highlight, new[] { "eraser", "opacity" })]
    [InlineData(EditTool.Shape, new[] { "opacity", "size", "fill" })]
    [InlineData(EditTool.Draw, new[] { "opacity", "size" })]
    [InlineData(EditTool.Line, new[] { "opacity", "size" })]
    [InlineData(EditTool.Underline, new[] { "opacity" })]
    [InlineData(EditTool.Strikethrough, new[] { "opacity" })]
    [InlineData(EditTool.Crop, new[] { "pages", "units" })]
    [InlineData(EditTool.Redact, new[] { "properties", "marksearch", "markpages", "find", "clear" })]
    public void EveryToolShedsInItsOwnOrder(EditTool tool, string[] expected)
    {
        Assert.Equal(expected, ToolOptionsCatalog.ShedOrder(tool));
    }

    // Spec 3.2: the document starts below the docked row, so nothing is drawn over the page. A 34-tall
    // bar in an 800 x 600 pane leaves the document 566 tall, starting at 34.
    [Fact]
    public void TheDocumentStartsBelowTheDockedBar()
    {
        RunSta(() =>
        {
            var document = new Border();
            var bar = new Border { Height = 34 };
            var dock = new ToolOptionsDock { Document = document, Bar = bar };

            dock.Measure(new Size(800, 600));
            dock.Arrange(new Rect(0, 0, 800, 600));

            Point top = document.TranslatePoint(new Point(0, 0), dock);
            Assert.Equal(34, bar.ActualHeight);
            Assert.Equal(34, top.Y);
            Assert.Equal(566, document.ActualHeight);
            Assert.Equal(0, bar.TranslatePoint(new Point(0, 0), dock).Y);
        });
    }

    // A tool without options takes the row away entirely: the document fills the pane from the top.
    [Fact]
    public void WithoutABarTheDocumentFillsThePane()
    {
        RunSta(() =>
        {
            var document = new Border();
            var dock = new ToolOptionsDock { Document = document, Bar = new Border { Height = 34 } };
            dock.Bar = null;

            dock.Measure(new Size(800, 600));
            dock.Arrange(new Rect(0, 0, 800, 600));

            Assert.Equal(0, document.TranslatePoint(new Point(0, 0), dock).Y);
            Assert.Equal(600, document.ActualHeight);
        });
    }

    // Spec 5: the bar's swatches, toggles, checks and steppers were mouse-only Borders that Tab never
    // reached. Each is a real control now: a Tab stop with a name, a hit area of at least 24 x 24, and
    // the action on Click, which is what Space and Enter raise.
    public static TheoryData<string> PressableControls => ["swatch", "more", "toggle", "step"];

    private static System.Windows.FrameworkElement Build(string kind, Action click) => kind switch
    {
        "swatch" => ToolOptionsControls.Swatch(System.Windows.Media.Colors.Red, false, "Color #FF0000", click),
        "more" => ToolOptionsControls.MoreColors("More colors...", click),
        "toggle" => ToolOptionsControls.IconToggle(new Border(), false, "Distance", click),
        _ => ToolOptionsControls.Step("+", "Larger", click),
    };

    [Theory]
    [MemberData(nameof(PressableControls))]
    public void EveryPressableBarControlIsATabStopThatSpaceAndEnterReach(string kind)
    {
        RunSta(() =>
        {
            int clicks = 0;
            var element = Build(kind, () => clicks++);

            var control = Assert.IsAssignableFrom<System.Windows.Controls.Primitives.ButtonBase>(element);
            Assert.True(control.Focusable && control.IsTabStop);
            Assert.False(string.IsNullOrWhiteSpace(System.Windows.Automation.AutomationProperties.GetName(control)));
            control.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Assert.True(control.DesiredSize.Width >= 24 && control.DesiredSize.Height >= 24,
                $"{kind} hit area is {control.DesiredSize}");

            control.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.Equal(1, clicks);
        });
    }

    // The check (Level, Eraser, Fill) is a CheckBox whose label is part of its name; Space toggles it.
    [Fact]
    public void TheBarCheckIsANamedCheckBoxThatToggles()
    {
        RunSta(() =>
        {
            bool? last = null;
            var element = ToolOptionsControls.Check("Eraser", false, "Eraser", value => last = value);

            var check = Assert.IsType<CheckBox>(element);
            Assert.True(check.Focusable && check.IsTabStop);
            Assert.Equal("Eraser", System.Windows.Automation.AutomationProperties.GetName(check));
            check.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Assert.True(check.DesiredSize.Height >= 24, $"check row is {check.DesiredSize}");

            var toggle = (System.Windows.Automation.Provider.IToggleProvider)
                new System.Windows.Automation.Peers.CheckBoxAutomationPeer(check)
                    .GetPattern(System.Windows.Automation.Peers.PatternInterface.Toggle);
            toggle.Toggle();
            Assert.True(last);
        });
    }

    // Swatches and mode buttons report whether they are the current choice.
    [Fact]
    public void TheSelectedSwatchAndToggleReportTheirCheckedState()
    {
        RunSta(() =>
        {
            var swatch = Assert.IsAssignableFrom<System.Windows.Controls.Primitives.ToggleButton>(
                ToolOptionsControls.Swatch(System.Windows.Media.Colors.Red, true, "Color #FF0000", () => { }));
            var toggle = Assert.IsAssignableFrom<System.Windows.Controls.Primitives.ToggleButton>(
                ToolOptionsControls.IconToggle(new Border(), true, "Distance", () => { }));
            Assert.True(swatch.IsChecked);
            Assert.True(toggle.IsChecked);
        });
    }

    private static ToolOptionsBar MeasureBar(Dictionary<string, FrameworkElement> contents)
    {
        var bar = new ToolOptionsBar(EditTool.Measure, "Measure options", "More settings");
        foreach (var g in MeasureGroups)
        {
            var content = new Border { Width = g.Width, Height = 24 };
            contents[g.Id] = content;
            bar.Add(g.Id, content, g.CompactWidth is double w ? new Button { Width = w, Height = 24 } : null);
        }
        return bar;
    }

    // The real row lays out the fit above: one line, 27 tall, the shed groups in the "..." menu, and
    // every control still in the row lying fully inside it, one pixel below the top and above the border.
    [Fact]
    public void TheRowShedsIntoItsMenuAndNeverWrapsOrClips()
    {
        RunSta(() =>
        {
            var contents = new Dictionary<string, FrameworkElement>();
            var bar = MeasureBar(contents);

            bar.Measure(new Size(900, double.PositiveInfinity));
            bar.Arrange(new Rect(0, 0, 900, bar.DesiredSize.Height));
            bar.UpdateLayout();

            Assert.Equal(27, bar.ActualHeight);
            Assert.Equal(["modes", "scale", "units", "pagesize", "report", "color"], bar.InlineIds);
            Assert.Equal(["size", "opacity"], bar.OverflowIds);
            Assert.True(bar.IsCompact);
            Assert.Equal(Visibility.Visible, bar.OverflowButton.Visibility);
            foreach (string id in bar.InlineIds.Where(i => i != "color"))
            {
                Point at = contents[id].TranslatePoint(new Point(0, 0), bar);
                Assert.True(at.X >= 8 && at.X + contents[id].ActualWidth <= 900 - 8, $"{id} at {at.X}");
                Assert.True(at.Y >= 0 && at.Y + contents[id].ActualHeight <= 27, $"{id} at y {at.Y}");
            }
            Point more = bar.OverflowButton.TranslatePoint(new Point(0, 0), bar);
            Assert.Equal(1, more.Y);
            Assert.Equal(1, 27 - 1 - (more.Y + bar.OverflowButton.ActualHeight));
        });
    }

    // The "..." button is a named Tab stop, and a pane wide enough for everything has no menu at all.
    [Fact]
    public void TheMenuButtonIsANamedTabStopShownOnlyWhileSomethingIsShed()
    {
        RunSta(() =>
        {
            var bar = MeasureBar([]);
            bar.Measure(new Size(1440, double.PositiveInfinity));
            bar.Arrange(new Rect(0, 0, 1440, bar.DesiredSize.Height));

            Assert.Empty(bar.OverflowIds);
            Assert.Equal(Visibility.Collapsed, bar.OverflowButton.Visibility);
            Assert.True(bar.OverflowButton.Focusable && bar.OverflowButton.IsTabStop);
            Assert.Equal("More settings", System.Windows.Automation.AutomationProperties.GetName(bar.OverflowButton));
        });
    }

    // The Scale text, the page size and the Redact count change in place. A row that keeps the old
    // frozen width would clip the right end on a narrow pane, so a group that grew asks for a re-fit:
    // page size 140 -> 400 makes 1293 into 1553, the chip saves 216 (1337), Opacity leaves for the
    // menu (1337 - 187 + 34 = 1184), which fits.
    [Fact]
    public void AGroupThatGrowsInPlaceRefitsTheRow()
    {
        RunSta(() =>
        {
            var contents = new Dictionary<string, FrameworkElement>();
            var bar = MeasureBar(contents);
            bar.Measure(new Size(1293, double.PositiveInfinity));
            bar.Arrange(new Rect(0, 0, 1293, 34));
            Assert.Empty(bar.OverflowIds);

            contents["pagesize"].Width = 400;
            bar.Remeasure();
            bar.Measure(new Size(1293, double.PositiveInfinity));
            bar.Arrange(new Rect(0, 0, 1293, 34));

            Assert.True(bar.IsCompact);
            Assert.Equal(["opacity"], bar.OverflowIds);
        });
    }

    // The window's shortcuts tunnel first. Space, Enter and the arrows on a focused row control are
    // that control's (press a toggle, move a slider), not panning, cropping or a page turn; letters,
    // chords and Tab still go to the window.
    [Fact]
    public void KeysOnARowControlBelongToThatControl()
    {
        RunSta(() =>
        {
            var bar = new ToolOptionsBar(EditTool.Draw, "Draw options", "More settings");
            var slider = new Slider { Width = 80 };
            var button = new Button { Width = 24, Height = 24 };
            bar.Add("size", slider);
            bar.Add("color", button);
            bar.Measure(new Size(800, double.PositiveInfinity));
            bar.Arrange(new Rect(0, 0, 800, 34));

            Assert.True(ToolOptionsBar.OwnsKey(slider, System.Windows.Input.Key.Left, System.Windows.Input.ModifierKeys.None));
            Assert.True(ToolOptionsBar.OwnsKey(button, System.Windows.Input.Key.Space, System.Windows.Input.ModifierKeys.None));
            Assert.True(ToolOptionsBar.OwnsKey(button, System.Windows.Input.Key.Enter, System.Windows.Input.ModifierKeys.None));
            Assert.False(ToolOptionsBar.OwnsKey(button, System.Windows.Input.Key.T, System.Windows.Input.ModifierKeys.None));
            Assert.False(ToolOptionsBar.OwnsKey(button, System.Windows.Input.Key.Left, System.Windows.Input.ModifierKeys.Control));
            Assert.False(ToolOptionsBar.OwnsKey(new Border(), System.Windows.Input.Key.Space, System.Windows.Input.ModifierKeys.None));
        });
    }

    // Key events in the "..." menu tunnel through the main window first (the popup's logical parent
    // is the row). Esc there closes the menu; it must not reach the window, where it drops the tool.
    // In the row itself Esc stays the window's.
    [Fact]
    public void EscapeInTheMenuBelongsToTheMenu()
    {
        RunSta(() =>
        {
            var bar = new ToolOptionsBar(EditTool.Draw, "Draw options", "More settings");
            var inRow = new Button { Width = 200, Height = 24 };
            var shed = new Button { Width = 300, Height = 24 };
            bar.Add("color", inRow);
            bar.Add("opacity", shed);
            bar.Measure(new Size(300, double.PositiveInfinity));
            bar.Arrange(new Rect(0, 0, 300, 34));
            Assert.Equal(["opacity"], bar.OverflowIds);

            Assert.True(ToolOptionsBar.OwnsKey(shed, System.Windows.Input.Key.Escape, System.Windows.Input.ModifierKeys.None));
            Assert.False(ToolOptionsBar.OwnsKey(inRow, System.Windows.Input.Key.Escape, System.Windows.Input.ModifierKeys.None));
        });
    }

    // A screen reader names a quick color by its color name in the app's language, not its hex value.
    // The nine default swatches (UiKit.DefaultSwatches) each have a name; any other color has none.
    [Theory]
    [InlineData(0xE0, 0x3C, 0x3C, "Str_Color_Red")]
    [InlineData(0xE8, 0x7A, 0x1E, "Str_Color_Orange")]
    [InlineData(0xF2, 0xC0, 0x1E, "Str_Color_Yellow")]
    [InlineData(0x2E, 0xA5, 0x4C, "Str_Color_Green")]
    [InlineData(0x2E, 0x86, 0xDE, "Str_Color_Blue")]
    [InlineData(0x8E, 0x5B, 0xD6, "Str_Color_Purple")]
    [InlineData(0xE0, 0x4A, 0x9A, "Str_Color_Pink")]
    [InlineData(0x00, 0x00, 0x00, "Str_Color_Black")]
    [InlineData(0xFF, 0xFF, 0xFF, "Str_Color_White")]
    [InlineData(0x12, 0x34, 0x56, null)]
    public void QuickColorsAreNamedByTheirColorName(byte r, byte g, byte b, string? key)
    {
        Assert.Equal(key, ToolOptionsControls.ColorNameKey(System.Windows.Media.Color.FromRgb(r, g, b)));
    }

    private static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { error = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }
}
