using System.Globalization;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;

// Each process owns its settings and window. Reflection only initializes the isolated host.
internal static class Program
{
    private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic
        | BindingFlags.Instance | BindingFlags.Static;
    private static string Folder = "";

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if ((args.Length != 8 && args.Length != 10) || args[^1] != "--startup-smoke")
                throw new ArgumentException("APP_DLL APP_XAML NEW_OUTPUT_DIRECTORY SIDE SCALE WIDTH THEME [LOCALE open|collapsed] --startup-smoke");
            string appPath = Path.GetFullPath(args[0]);
            Folder = Path.GetFullPath(args[2]);
            if (Directory.Exists(Folder)) throw new IOException("Use a fresh output directory.");
            Directory.CreateDirectory(Path.Combine(Folder, "data"));
            string temporary = Path.Combine(Folder, "temp");
            Directory.CreateDirectory(temporary);
            Environment.SetEnvironmentVariable("TEMP", temporary);
            Environment.SetEnvironmentVariable("TMP", temporary);
            File.WriteAllText(Path.Combine(Folder, "data", "settings.json"), JsonSerializer.Serialize(
                new Dictionary<string, string> { ["SidebarSide"] = args[3], ["AppScale"] = args[4],
                    ["Theme"] = args[6], ["Locale"] = args.Length == 10 ? args[7] : "EnUS" }));
            Application app = Bootstrap(appPath, Path.GetFullPath(args[1]));
            Assembly assembly = Application.ResourceAssembly;
            var window = (Window)Activator.CreateInstance(assembly.GetType("KillerPDF.Shell.MainWindow", true)!)!;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -10000; window.Top = -10000; window.ShowInTaskbar = false;
            window.ShowActivated = false; window.Width = double.Parse(args[5], CultureInfo.InvariantCulture);
            window.Height = 800; window.Show(); Pump(500);
            string fixture = CreateFixture();
            window.GetType().GetMethod("OpenFromExternal", BindingFlags.Public | BindingFlags.Instance)!
                .Invoke(window, [fixture]);
            Pump(550);
            var header = (FrameworkElement)window.FindName("SidebarHeaderStack");
            bool open = args.Length == 10 && args[8] == "open";
            if ((header.Visibility == Visibility.Visible) != open)
            {
                ((Button)window.FindName("SidebarToggleBtn")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Pump(350);
            }
            window.UpdateLayout(); Pump(200);
            var rail = (FrameworkElement)window.FindName("SidebarToggleStrip");
            var outer = (FrameworkElement)window.FindName("SidebarOuterGrid");
            if (Math.Abs(outer.LayoutTransform.Value.M11 - double.Parse(args[4], CultureInfo.InvariantCulture)) > 0.001
                || ((args[3] is "Top" or "Bottom") != (rail.ActualWidth > rail.ActualHeight)))
                throw new InvalidOperationException("Requested startup orientation or app scale was not applied.");
            var stack = (StackPanel)window.FindName("SidebarRailStack");
            var buttons = stack.Children.OfType<Button>().ToArray();
            var before = buttons.Select(button => Describe(button, rail)).ToArray();
            Save(window, "initial.png");
            var scroll = Descendants<ScrollViewer>(rail).FirstOrDefault(view => IsAncestor(view, stack));
            bool needsOverflow = before.Any(item => item.Outside);
            var next = Descendants<Button>(rail).FirstOrDefault(button => button.Name == "RailOverflowNext");
            var previous = Descendants<Button>(rail).FirstOrDefault(button => button.Name == "RailOverflowPrevious");
            File.WriteAllText(Path.Combine(Folder, "initial.json"), JsonSerializer.Serialize(
                new { Side = args[3], Scale = args[4], Width = window.ActualWidth,
                    Rail = new[] { rail.ActualWidth, rail.ActualHeight }, Buttons = before },
                new JsonSerializerOptions { WriteIndented = true }));
            if (!open && before.All(item => item.Outside))
                throw new InvalidOperationException("Collapsed rail initially hides every original command.");
            if (needsOverflow && (scroll is null || next?.IsVisible != true || previous?.IsVisible != true))
                throw new InvalidOperationException("BF-L001: rail commands are clipped without visible overflow navigation: "
                    + string.Join(", ", before.Where(item => item.Outside).Select(item => item.Name)));
            if (scroll is not null && scroll.ScrollableWidth > 0.5)
            {
                if (next?.IsVisible != true || previous?.IsVisible != true)
                    throw new InvalidOperationException("Overflow controls must be visible while commands need scrolling.");
                double offset = scroll.HorizontalOffset;
                next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(100);
                if (scroll.HorizontalOffset <= offset)
                    throw new InvalidOperationException("Next overflow button did not reveal more commands.");
                foreach (Button button in buttons)
                {
                    if (button.IsEnabled && !button.Focus())
                        throw new InvalidOperationException("Cannot focus enabled rail command " + button.Name);
                    if (!button.IsEnabled) button.BringIntoView();
                    Pump(40);
                    Rect bounds = button.TransformToAncestor(scroll).TransformBounds(new Rect(button.RenderSize));
                    if (bounds.Left < -0.5 || bounds.Right > scroll.ActualWidth + 0.5)
                        throw new InvalidOperationException("Cannot reveal original rail command " + button.Name);
                }
                Save(window, "last-command.png");
                double previousOffset = scroll.HorizontalOffset;
                previous.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(100);
                if (previousOffset > 0.5 && scroll.HorizontalOffset >= previousOffset - 0.5)
                    throw new InvalidOperationException("Previous overflow button did not reveal earlier commands.");
            }
            if (open)
            {
                Reveal((Control)window.FindName("PageJumpBox"), rail);
                var outline = (Button)window.FindName("SidebarOutlinesTab");
                outline.BringIntoView(); Pump(40);
                outline.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(250);
                Reveal((Control)window.FindName("OutlineExpandCollapseAllButton"), rail);
                Save(window, "outline-actions.png");
                if (args[3] == "Top" && args[4] == "2.5" && args[6] == "Dark")
                {
                    window.Height = 600; Pump(250);
                    Reveal((Control)window.FindName("OutlineExpandCollapseAllButton"), rail);
                    Save(window, "short-window.png");
                    window.Height = 1200; Pump(250);
                    Reveal((Control)window.FindName("OutlineExpandCollapseAllButton"), rail);
                    window.Height = 800; Pump(250);
                    var logo = (UIElement)window.FindName("LogoBar");
                    foreach (int direction in new[] { -120, 120 })
                    {
                        for (int step = 0; step < 90; step++)
                            logo.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, direction)
                                { RoutedEvent = Mouse.MouseWheelEvent });
                        Pump(300);
                        Reveal((Control)window.FindName("OutlineExpandCollapseAllButton"), rail);
                    }
                    Console.WriteLine("PASS real resize and logo-wheel scale changes keep header on screen");
                }
                var comments = (Button)window.FindName("SidebarCommentsTab");
                comments.BringIntoView(); Pump(40);
                comments.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(250);
                Reveal((Control)window.FindName("SidebarPanelEditButton"), rail);
                Save(window, "panel-edit.png");
            }
            foreach (string name in new[] { "LangButton", "ThemeButton" })
            {
                var button = (Button)window.FindName(name);
                button.BringIntoView(); Pump(50);
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(170);
                var menu = (ContextMenu)window.FindName(name == "LangButton" ? "LangMenu" : "ThemeMenu");
                if (!menu.IsOpen)
                    throw new InvalidOperationException("The kit menu did not open from its rail button: " + name);
                if (name == "ThemeButton") Console.WriteLine("PASS theme flyout inset " + CheckThemeMenuInset(menu));
                else Console.WriteLine("PASS language flyout " + CheckLanguageMenu(menu));
                menu.IsOpen = false; Pump(50);
            }
            if (!buttons.SequenceEqual(stack.Children.OfType<Button>()))
                throw new InvalidOperationException("Overflow must retain original button instances and order.");
            if (args[4] == "2.5" && args[6] == "Dark")
            {
                ((Button)window.FindName("FullScreenRailBtn")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Pump(700);
                var content = (Grid)window.FindName("MainContentGrid");
                int row = args[3] == "Top" ? 0 : 1;
                double extent = content.RowDefinitions[row].ActualHeight;
                window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window),
                    Environment.TickCount, Key.F11) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                Pump(700);
                if (extent > 0.5) throw new InvalidOperationException("Fullscreen retained sidebar track height " + extent);
                if (!rail.IsVisible) throw new InvalidOperationException("Fullscreen exit did not restore the rail.");
                Console.WriteLine("PASS fullscreen removes and restores the strip track");
            }
            if (args[3] == "Top" && args[4] == "1" && args[5] == "1000")
            {
                foreach (string side in new[] { "Left", "Bottom", "Right", "Top" })
                {
                    rail.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right)
                        { RoutedEvent = UIElement.MouseRightButtonUpEvent });
                    Pump(80);
                    ContextMenu menu = PresentationSource.CurrentSources.Cast<PresentationSource>()
                        .Where(source => source.RootVisual is not null)
                        .SelectMany(source => Descendants<ContextMenu>(source.RootVisual))
                        .Single(candidate => candidate.IsOpen);
                    string caption = (string)window.FindResource("Str_Sidebar_" + side);
                    MenuItem item = menu.Items.OfType<MenuItem>().Single(candidate => Equals(candidate.Header, caption));
                    item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    menu.IsOpen = false; Pump(230);
                    if (!buttons.SequenceEqual(stack.Children.OfType<Button>())
                        || (side is "Top" or "Bottom") != (stack.Orientation == Orientation.Horizontal))
                        throw new InvalidOperationException("Side transition changed original command instances or orientation.");
                    Console.WriteLine("PASS context-menu transition to " + side);
                }
            }
            File.WriteAllText(Path.Combine(Folder, "result.txt"), "PASS rail commands are reachable");
            Console.WriteLine("PASS " + args[3] + " scale=" + args[4] + " width=" + args[5]);
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL " + ex);
            if (Directory.Exists(Folder)) File.WriteAllText(Path.Combine(Folder, "result.txt"), "FAIL " + ex);
            Environment.Exit(1);
        }
        return 1;
    }

    // Family rule: the theme rows, the accent pills, the first row and Film Grain sit 8px from the
    // card's outer edge on every side. Layout rounding may land an edge one device pixel off at a
    // fractional scale; anything more is a real layout error.
    private static string CheckThemeMenuInset(ContextMenu menu)
    {
        menu.UpdateLayout();
        var card = Descendants<FrameworkElement>(menu).First(element => element.Margin == new Thickness(22, 18, 22, 26));
        Rect Bounds(FrameworkElement element) => element.TransformToAncestor(card).TransformBounds(new Rect(element.RenderSize));
        var parts = new List<Rect>(); int pills = 0;
        foreach (var control in Descendants<System.Windows.Controls.Primitives.ToggleButton>(menu))
        {
            if (!control.IsVisible || control.ActualWidth == 0) continue;
            if (control.Template?.FindName("bd", control) is FrameworkElement hover) parts.Add(Bounds(hover));
            else if (control is RadioButton { GroupName: "ThemeAccentGroup" }) { parts.Add(Bounds(control)); pills++; }
        }
        double width = card.ActualWidth, height = card.ActualHeight;
        double left = parts.Min(r => r.Left), top = parts.Min(r => r.Top);
        double right = width - parts.Max(r => r.Right), bottom = height - parts.Max(r => r.Bottom);
        double pixel = 1 / VisualTreeHelper.GetDpi(card).DpiScaleX;
        string measured = $"L{left:0.##} T{top:0.##} R{right:0.##} B{bottom:0.##} at {1 / pixel:0.##}x, {pills} pills";
        if (pills == 0 || new[] { left, top, right, bottom }.Any(gap => Math.Abs(gap - 8) > pixel + 0.01))
            throw new InvalidOperationException("The theme flyout's rows and pills must sit 8px from every card edge: " + measured);
        // Every theme radio and the Film Grain box centers on its label, from the rendered pixels.
        foreach (var element in Descendants<FrameworkElement>(menu).Prepend(menu))
            element.BeginAnimation(UIElement.OpacityProperty, null);
        menu.Opacity = 1; card.Opacity = 1; menu.UpdateLayout();
        SaveBitmap(RenderBitmap(menu), "theme-menu.png");
        double worst = RingAlignment(menu, Descendants<System.Windows.Controls.Primitives.ToggleButton>(menu), out int rows);
        return measured + $", rings within {worst:0.#}px over {rows} rows";
    }

    // Measured from the rendered pixels, not layout: every ring's ink centers on its label's first
    // capital within half a pixel, the divider has the same gap to the hover box on each side, and
    // the hover rows sit 8px from every card edge. Saves lang-look.png and lang-gaps.png.
    private static string CheckLanguageMenu(ContextMenu menu)
    {
        // The fade-in is still running this soon after opening; render the finished state.
        foreach (var element in Descendants<FrameworkElement>(menu).Prepend(menu))
            element.BeginAnimation(UIElement.OpacityProperty, null);
        var card = Descendants<FrameworkElement>(menu).First(element => element.Margin == new Thickness(22, 18, 22, 26));
        menu.Opacity = 1; card.Opacity = 1;
        menu.UpdateLayout();
        var radios = Descendants<RadioButton>(menu).Where(radio => radio.GroupName == "LangGroup" && radio.IsVisible).ToList();
        if (radios.Count == 0) throw new InvalidOperationException("The language flyout has no rows.");
        Rect In(FrameworkElement element, FrameworkElement root) =>
            element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));
        Border HoverBox(RadioButton radio) => (Border)radio.Template.FindName("bd", radio);
        double split = radios.Select(radio => In(radio, menu).Left).Distinct().Average();
        var left = radios.Where(radio => In(radio, menu).Left < split).ToList();
        var right = radios.Where(radio => In(radio, menu).Left >= split).ToList();

        // How it looks with the pointer on two rows, for a person to check.
        var hover = (Brush)menu.FindResource("RowHoverBrush");
        HoverBox(left[^1]).Background = hover; HoverBox(right[0]).Background = hover;
        menu.UpdateLayout(); SaveBitmap(RenderBitmap(menu), "lang-look.png");
        HoverBox(left[^1]).ClearValue(Border.BackgroundProperty); HoverBox(right[0]).ClearValue(Border.BackgroundProperty);

        double worst = RingAlignment(menu, radios, out int rows);
        int width = RenderBitmap(menu).PixelWidth;

        // Divider: paint the line green alone to find it, then every hover box magenta, on one row's midline.
        var divider = Descendants<Border>(menu).First(border => border.Width == 1 && border.ActualHeight > 20);
        Brush lineBrush = divider.Background; Brush? mask = divider.OpacityMask; double opacity = divider.Opacity;
        divider.Background = new SolidColorBrush(Color.FromRgb(0, 255, 0)); divider.OpacityMask = null; divider.Opacity = 1;
        menu.UpdateLayout();
        int y = (int)(In(left[0], menu).Top + In(left[0], menu).Height / 2);
        byte[] line = Pixels(RenderBitmap(menu), out _);
        var magenta = new SolidColorBrush(Color.FromRgb(255, 0, 255));
        radios.ForEach(radio => HoverBox(radio).Background = magenta); menu.UpdateLayout();
        var gapsBitmap = RenderBitmap(menu); SaveBitmap(gapsBitmap, "lang-gaps.png");
        byte[] boxes = Pixels(gapsBitmap, out _);
        radios.ForEach(radio => HoverBox(radio).ClearValue(Border.BackgroundProperty));
        divider.Background = lineBrush; divider.OpacityMask = mask; divider.Opacity = opacity;
        bool Green(byte[] p, int x) { int i = (y * width + x) * 4; return p[i + 1] > 200 && p[i + 2] < 80 && p[i] < 80; }
        bool Magenta(byte[] p, int x) { int i = (y * width + x) * 4; return p[i + 2] > 200 && p[i] > 200 && p[i + 1] < 80; }
        int lineLeft = Enumerable.Range(0, width).First(x => Green(line, x));
        int lineRight = Enumerable.Range(0, width).Last(x => Green(line, x));
        int leftEnd = Enumerable.Range(0, lineLeft).Last(x => Magenta(boxes, x));
        int rightStart = Enumerable.Range(lineRight + 1, width - lineRight - 1).First(x => Magenta(boxes, x));
        int gapLeft = lineLeft - leftEnd - 1, gapRight = rightStart - lineRight - 1;
        if (gapLeft != gapRight || gapLeft < 6)
            throw new InvalidOperationException($"The language divider gaps are {gapLeft}px left and {gapRight}px right.");

        // Outer edges: every hover box against the card.
        var hoverBounds = radios.Select(radio => In(HoverBox(radio), card)).ToList();
        double l = hoverBounds.Min(r => r.Left), t = hoverBounds.Min(r => r.Top);
        double rr = card.ActualWidth - hoverBounds.Max(r => r.Right), b = card.ActualHeight - hoverBounds.Max(r => r.Bottom);
        double pixel = 1 / VisualTreeHelper.GetDpi(card).DpiScaleX;
        string measured = $"rings within {worst:0.#}px over {rows} rows, divider {gapLeft}/{gapRight}px, edges L{l:0.##} T{t:0.##} R{rr:0.##} B{b:0.##}";
        if (new[] { l, t, rr, b }.Any(gap => Math.Abs(gap - 8) > pixel + 0.01))
            throw new InvalidOperationException("The language flyout's rows must sit 8px from every card edge: " + measured);
        return measured;
    }

    // Ring or check square against its label's first capital, from ink: diff a full render with one that
    // hides the indicators and one that hides the labels. Throws past half a pixel; returns the worst row.
    private static double RingAlignment(FrameworkElement menu, IEnumerable<System.Windows.Controls.Primitives.ToggleButton> controls, out int rows)
    {
        Rect In(FrameworkElement element) => element.TransformToAncestor(menu).TransformBounds(new Rect(element.RenderSize));
        // "ring" in the kit templates; MainWindow's own ThemeCheck calls its square "box".
        FrameworkElement? Indicator(System.Windows.Controls.Primitives.ToggleButton control) =>
            (control.Template?.FindName("ring", control) ?? control.Template?.FindName("box", control)) as FrameworkElement;
        var list = controls.Where(control => control.IsVisible && Indicator(control) is not null).ToList();
        var rings = list.Select(control => (FrameworkElement)Indicator(control)!.Parent).ToList();
        var labels = list.Select(control => Descendants<ContentPresenter>(control).First(cp => cp.Content == control.Content)).ToList();
        menu.UpdateLayout(); byte[] full = Pixels(RenderBitmap(menu), out int width);
        rings.ForEach(ring => ring.Opacity = 0); menu.UpdateLayout(); byte[] noRing = Pixels(RenderBitmap(menu), out _);
        rings.ForEach(ring => ring.Opacity = 1); labels.ForEach(label => label.Opacity = 0);
        menu.UpdateLayout(); byte[] noLabel = Pixels(RenderBitmap(menu), out _);
        labels.ForEach(label => label.Opacity = 1); menu.UpdateLayout();
        double worst = 0; rows = 0;
        for (int index = 0; index < list.Count; index++)
        {
            // The label slot, not the control: a check box's first text is its own tick glyph.
            var text = Descendants<TextBlock>(labels[index]).FirstOrDefault(block => block.Text.Length > 0);
            // Only plain capitals with nothing above or below give a clean cap-height box.
            if (text is null || "ABDEFHIKLMNPRSTUVWXZ".IndexOf(text.Text[0]) < 0) continue;
            var glyph = new FormattedText(text.Text[..1], CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize, Brushes.Black, 1.0);
            Rect name = In(text), ringBox = In(rings[index]);
            double? ring = InkCenter(full, noRing, width, new Rect(ringBox.Left - 1, ringBox.Top - 3, ringBox.Width + 2, ringBox.Height + 6));
            double? cap = InkCenter(full, noLabel, width, new Rect(name.Left, name.Top - 4, glyph.WidthIncludingTrailingWhitespace, name.Height + 8));
            if (ring is null || cap is null) throw new InvalidOperationException("No ink found for the row " + text.Text);
            double off = Math.Abs(ring.Value - cap.Value);
            if (off > 0.5)
                throw new InvalidOperationException($"The {list[index].GetType().Name} \"{text.Text}\" sits {off:0.#}px off its label's capitals.");
            worst = Math.Max(worst, off); rows++;
        }
        if (rows == 0) throw new InvalidOperationException("No row with a plain capital to measure.");
        return worst;
    }

    // The vertical center of the pixels that differ between two renders inside a box.
    private static double? InkCenter(byte[] a, byte[] b, int width, Rect box)
    {
        int height = a.Length / 4 / width;
        int x0 = Math.Max(0, (int)Math.Floor(box.Left)), x1 = Math.Min(width - 1, (int)Math.Ceiling(box.Right));
        int y0 = Math.Max(0, (int)Math.Floor(box.Top)), y1 = Math.Min(height - 1, (int)Math.Ceiling(box.Bottom));
        double top = double.NaN, bottom = double.NaN;
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                int i = (y * width + x) * 4;
                if (Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2]) <= 90) continue;
                if (double.IsNaN(top)) top = y;
                bottom = y + 1; break;
            }
        return double.IsNaN(top) ? null : (top + bottom) / 2;
    }

    private static RenderTargetBitmap RenderBitmap(FrameworkElement element)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element); bitmap.Freeze();
        return bitmap;
    }

    private static byte[] Pixels(BitmapSource bitmap, out int width)
    {
        width = bitmap.PixelWidth;
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return pixels;
    }

    private static void SaveBitmap(BitmapSource bitmap, string name)
    {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(Folder, name)); encoder.Save(stream);
    }

    private static void Reveal(Control control, FrameworkElement rail)
    {
        Console.WriteLine(control.Name + ": " + control.Visibility + " visible=" + control.IsVisible
            + " enabled=" + control.IsEnabled + " focusable=" + control.Focusable + " size=" + control.RenderSize);
        if (!control.IsVisible || !control.IsEnabled || control.ActualWidth < 1 || control.ActualHeight < 1)
            throw new InvalidOperationException("Cannot focus populated header action " + control.Name);
        if (control.Focusable && !control.Focus())
            throw new InvalidOperationException("Cannot focus populated header action " + control.Name);
        if (!control.Focusable) control.BringIntoView();
        Pump(60);
        Window window = Window.GetWindow(control);
        Rect visibleBounds = control.TransformToAncestor(window).TransformBounds(new Rect(control.RenderSize));
        Console.WriteLine(control.Name + " in window: " + visibleBounds);
        if (visibleBounds.Left < -0.5 || visibleBounds.Right > window.ActualWidth + 0.5
            || visibleBounds.Top < -0.5 || visibleBounds.Bottom > window.ActualHeight + 0.5)
            throw new InvalidOperationException("Populated header action lies outside the window: " + control.Name);
        for (DependencyObject? current = VisualTreeHelper.GetParent(control); current is not null;
             current = VisualTreeHelper.GetParent(current))
        {
            if (current is FrameworkElement ancestor && (ancestor.ClipToBounds || ReferenceEquals(ancestor, rail)))
            {
                Rect bounds = control.TransformToAncestor(ancestor).TransformBounds(new Rect(control.RenderSize));
                Console.WriteLine(control.Name + " in " + ancestor.Name + ": " + bounds + " / " + ancestor.ActualWidth);
                if (bounds.Left < -0.5 || bounds.Right > ancestor.ActualWidth + 0.5)
                    throw new InvalidOperationException("Populated header action is clipped: " + control.Name);
            }
            if (ReferenceEquals(current, rail)) break;
        }
    }

    private static string CreateFixture()
    {
        const string contents = "BT /F1 12 Tf 72 720 Td (RAIL FIXTURE) Tj ET";
        string[] objects = [
            "<< /Type /Catalog /Pages 2 0 R /Outlines 6 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            $"<< /Length {contents.Length} >>\nstream\n{contents}\nendstream",
            "<< /Type /Outlines /First 7 0 R /Last 7 0 R /Count 1 >>",
            "<< /Title (Rail chapter) /Parent 6 0 R /Dest [3 0 R /Fit] /First 8 0 R /Last 8 0 R /Count 1 >>",
            "<< /Title (Rail bookmark) /Parent 7 0 R /Dest [3 0 R /Fit] >>"
        ];
        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        foreach (string item in objects)
        {
            offsets.Add(pdf.Length);
            pdf.Append($"{offsets.Count} 0 obj\n{item}\nendobj\n");
        }
        int xref = pdf.Length;
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) pdf.Append(offset.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        string path = Path.Combine(Folder, "fixture.pdf");
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes(pdf.ToString()));
        return path;
    }

    private static Application Bootstrap(string appPath, string appXaml)
    {
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            string path = Path.Combine(Path.GetDirectoryName(appPath)!, name.Name + ".dll");
            return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
        };
        Assembly assembly = Assembly.LoadFrom(appPath);
        typeof(Application).GetField("_resourceAssembly", Members)!.SetValue(null, assembly);
        assembly.GetType("KillerPDF.Services.AppDataPaths", true)!
            .GetProperty("StartupSmokeRoot", Members)!.SetValue(null, Path.Combine(Folder, "data"));
        Type appType = assembly.GetType("KillerPDF.App", true)!;
        var host = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("RailSmokeHost"), AssemblyBuilderAccess.Run)
            .DefineDynamicModule("Host").DefineType("IsolatedApplication", TypeAttributes.Public, appType);
        host.DefineDefaultConstructor(MethodAttributes.Public);
        var startup = host.DefineMethod("OnStartup", MethodAttributes.Family | MethodAttributes.Virtual
            | MethodAttributes.HideBySig, typeof(void), [typeof(StartupEventArgs)]);
        startup.GetILGenerator().Emit(OpCodes.Ret);
        host.DefineMethodOverride(startup, appType.GetMethod("OnStartup", Members)!);
        var app = (Application)Activator.CreateInstance(host.CreateType()!)!;
        var settings = JsonSerializer.Deserialize<Dictionary<string, string>>(
            File.ReadAllText(Path.Combine(Folder, "data", "settings.json")))!;
        foreach (var setting in settings)
            appType.GetMethod("SetSetting", Members)!.Invoke(null, [setting.Key, setting.Value]);
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        app.DispatcherUnhandledException += (_, e) => { Console.Error.WriteLine(e.Exception); Environment.Exit(1); };
        var deadline = new DispatcherTimer { Interval = TimeSpan.FromSeconds(35) };
        deadline.Tick += (_, _) => { Console.Error.WriteLine("FAIL rail smoke deadline"); Environment.Exit(1); };
        deadline.Start();
        XDocument source = XDocument.Load(appXaml);
        XElement resources = source.Descendants().First(element => element.Name.LocalName == "ResourceDictionary");
        foreach (var declaration in source.Root!.Attributes().Where(attribute => attribute.IsNamespaceDeclaration))
            resources.SetAttributeValue(declaration.Name, declaration.Value);
        string xaml = resources.ToString();
        // Whole attribute values: "clr-namespace:KillerPDF" is also the start of KillerPDF.Controls.
        foreach (string name in new[] { "KillerPDF", "KillerPDF.Controls", "KillerFamily.Layout" })
            xaml = xaml.Replace("\"clr-namespace:" + name + "\"", "\"clr-namespace:" + name + ";assembly=KillerPDF\"", StringComparison.Ordinal);
        app.Resources = (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(xaml,
            new System.Windows.Markup.ParserContext { BaseUri = new Uri("pack://application:,,,/KillerPDF;component/") });
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        // App startup order: the kit theme hooks (settings, KillerPDF's own keys) before the services.
        assembly.GetType("KillerPDF.Services.AppTheme", true)!.GetMethod("Configure",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)!.Invoke(null, null);
        foreach (string service in new[] { "ThemeManager", "LocaleManager", "FilmGrain" })
            assembly.GetType("KillerPDF.Services." + service, true)!.GetMethod("Initialize", Members)!.Invoke(null, null);
        string locale = assembly.GetType("KillerPDF.Services.LocaleManager", true)!
            .GetProperty("Current", Members)!.GetValue(null)!.ToString()!;
        if (locale != settings["Locale"])
            throw new InvalidOperationException("Requested locale was not applied: " + locale);
        Console.WriteLine("Locale=" + locale + ", TEMP=" + Path.GetTempPath());
        string smokeRoot = (string)appType.GetProperty("SmokeDataRoot", Members)!.GetValue(null)!;
        if (!smokeRoot.StartsWith(Path.Combine(Folder, "temp") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Smoke data escaped the owned scratch folder.");
        Console.WriteLine("SmokeDataRoot=" + smokeRoot);
        return app;
    }

    private static bool IsAncestor(DependencyObject ancestor, DependencyObject element)
    {
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
            if (ReferenceEquals(current, ancestor)) return true;
        return false;
    }

    private sealed record ButtonBounds(string Name, bool Outside, double Left, double Right);

    private static ButtonBounds Describe(Button button, FrameworkElement rail)
    {
        Rect bounds = button.TransformToAncestor(rail).TransformBounds(new Rect(button.RenderSize));
        return new(button.Name, bounds.Left < -0.5 || bounds.Right > rail.ActualWidth + 0.5
            || bounds.Top < -0.5 || bounds.Bottom > rail.ActualHeight + 0.5, bounds.Left, bounds.Right);
    }

    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }

    private static void Save(FrameworkElement element, string name)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(Folder, name)); encoder.Save(stream);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T value) yield return value;
            foreach (T descendant in Descendants<T>(child)) yield return descendant;
        }
    }
}
