using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

internal static class SidebarActionsSmoke
{
    internal static int Run(Window owner, string folder)
    {
        string source = Path.Combine(folder, "source.pdf");
        File.WriteAllBytes(source, TableExportSmoke.Fixture());
        owner.GetType().GetMethod("OpenFromExternal", BindingFlags.Public | BindingFlags.Instance)!.Invoke(owner, [source]);
        Pump();
        Button Find(string name) => Children(owner).OfType<Button>().Single(button => button.Name == name);
        Button pages = Find("SidebarPagesTab");
        pages.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(); CloseMenus();
        string? original = pages.Tag as string;
        pages.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
        // Clicking the open panel's icon offers the four sidebar positions instead of closing it.
        // Since c8399f5 the four sides are nested under the Sidebar row, so open its submenu and count them there.
        ContextMenu? clickMenu = Menus().SingleOrDefault();
        int positions = 0;
        if (clickMenu?.Items.OfType<MenuItem>().SingleOrDefault(item => item.Name == "SidebarMenuItem") is { } sidebarRow)
        {
            sidebarRow.IsSubmenuOpen = true;
            clickMenu.UpdateLayout(); Pump();
            positions = sidebarRow.Items.OfType<MenuItem>().Count(item => item.IsCheckable);
        }
        CloseMenus();
        if (original != "on" || !Equals(pages.Tag, "on")) throw new InvalidOperationException("Pages click closed its panel.");
        if (positions != 4) throw new InvalidOperationException("Pages click did not offer the four sidebar positions.");
        foreach (string buttonName in new[] { "SidebarPagesTab", "SidebarCommentsTab", "SidebarSignaturesTab", "SplitPaneRailBtn" })
        {
            Button button = Find(buttonName);
            button.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right)
                { RoutedEvent = Mouse.MouseUpEvent });
            Pump();
            ContextMenu menu = Menus().Single();
            try
            {
                if (menu.Placement != PlacementMode.MousePoint) throw new InvalidOperationException("Rail context menu is not at cursor.");
                if (!Equals(pages.Tag, "on")) throw new InvalidOperationException("Right-click changed the active sidebar panel.");
                if (buttonName == "SidebarCommentsTab")
                    foreach (var (key, shortcut) in new[] { ("Str_Measure_Distance", "Alt+Shift+1"),
                        ("Str_Measure_Perimeter", "Alt+Shift+2"), ("Str_Measure_Area", "Alt+Shift+3"),
                        ("Str_Measure_Angle", "Alt+Shift+4") })
                    {
                        var item = menu.Items.OfType<MenuItem>().SingleOrDefault(item => Equals(item.Header, Application.Current.FindResource(key)));
                        if (item?.InputGestureText != shortcut) throw new InvalidOperationException("Missing measurement action or shortcut: " + key);
                    }
                if (buttonName == "SidebarSignaturesTab" && !menu.Items.OfType<MenuItem>().Any(item =>
                    Equals(item.Header, Application.Current.FindResource("Str_Lbl_DigitalSig")) && item.InputGestureText == "Ctrl+Shift+G"))
                    throw new InvalidOperationException("Signatures rail menu lacks signing and its shortcut.");
                if (buttonName == "SidebarPagesTab")
                {
                    // The page menu's own view items are checkable too, so match the four sides by name.
                    // They now sit in the Sidebar row's flyout, so open it first.
                    MenuItem railSidebarRow = menu.Items.OfType<MenuItem>().Single(item => item.Name == "SidebarMenuItem");
                    railSidebarRow.IsSubmenuOpen = true;
                    menu.UpdateLayout(); Pump();
                    MenuItem[] sides = [.. railSidebarRow.Items.OfType<MenuItem>().Where(item => new[] { "Left", "Right", "Top", "Bottom" }
                        .Any(side => Equals(item.Header, Application.Current.FindResource("Str_Sidebar_" + side))))];
                    if (sides.Length != 4 || sides.Count(item => item.IsChecked) != 1)
                        throw new InvalidOperationException("Pages rail menu lacks the four sidebar positions.");
                }
            }
            finally { CloseMenus(); }
        }
        Button split = Find("SplitPaneRailBtn");
        if (split.TranslatePoint(new Point(), owner).Y >= pages.TranslatePoint(new Point(), owner).Y)
            throw new InvalidOperationException("Split pane is not first in the app rail group.");
        Find("ShortcutHelpRailBtn").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
        var overlay = (FrameworkElement)owner.FindName("ShortcutsOverlay");
        if (!overlay.IsVisible) throw new InvalidOperationException("Shortcut overlay did not open.");
        foreach (var (key, shortcut) in new[] { ("Str_Lbl_DigitalSig", "Ctrl+Shift+G"),
            ("Str_TableExport_Title", "Alt+Shift+T"), ("Str_Measure_Distance", "Alt+Shift+1") })
        {
            TextBlock label = Children(overlay).OfType<TextBlock>().Single(block => block.Text == (string)Application.Current.FindResource(key));
            string[] rowText = Children((DependencyObject)label.Parent).OfType<TextBlock>()
                .Select(block => new System.Windows.Documents.TextRange(block.ContentStart, block.ContentEnd).Text).ToArray();
            if (!rowText.Contains(shortcut))
                throw new InvalidOperationException("Shortcut overlay is missing " + shortcut + ": " + string.Join(" | ", rowText));
        }
        Find("ShortcutHelpRailBtn").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
        RunSearchAction(owner, "Str_Measure_Area", "Alt+Shift+3");
        // Since 998e7e1 the measure types are toggles in the docked options row, checked for the active type.
        ToggleButton area = Children(owner).OfType<ToggleButton>().Single(toggle =>
            AutomationProperties.GetAutomationId(toggle) == "MeasureModeArea");
        if (!area.IsVisible || area.IsChecked != true
            || AutomationProperties.GetName(area) != (string)Application.Current.FindResource("Str_Measure_TT_Area"))
            throw new InvalidOperationException("Area shortcut did not select the Area tool.");
        bool signingOpened = false;
        var closeSigning = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        closeSigning.Tick += (_, _) =>
        {
            Window? signing = Application.Current.Windows.Cast<Window>().FirstOrDefault(window =>
                window != owner && window.IsVisible);
            if (signing is null) return;
            signingOpened = signing.GetType().Name == "SignDocumentDialog";
            closeSigning.Stop(); signing.Close();
        };
        closeSigning.Start();
        try { RunSearchAction(owner, "Str_Lbl_DigitalSig", "Ctrl+Shift+G"); }
        finally { closeSigning.Stop(); }
        if (!signingOpened || !Equals(Find("SidebarSignaturesTab").Tag, "on"))
            throw new InvalidOperationException("Signing shortcut did not open signing in the Signatures panel.");
        File.WriteAllText(Path.Combine(folder, "result.txt"),
            "PASS Pages stays open, rail right-click preserves the panel, cursor menus include measurements and signing shortcuts, split pane is first.");
        Console.WriteLine("PASS sidebar actions.");
        return 0;
    }

    private static void RunSearchAction(Window owner, string key, string shortcut)
    {
        var host = (FrameworkElement)owner.FindName("ToolSearchHost");
        Children(host).OfType<Button>().Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
        string label = (string)Application.Current.FindResource(key);
        Children(host).OfType<TextBox>().Single().Text = label; Pump();
        // The results drop down once the search box has finished growing, so wait for the row.
        Border[] Rows() => [.. PresentationSource.CurrentSources.Cast<PresentationSource>().Select(source => source.RootVisual)
            .OfType<DependencyObject>().SelectMany(Children).OfType<Border>().Where(border =>
                AutomationProperties.GetName(border) == label + " (" + shortcut + ")")];
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (Rows().Length == 0)
        {
            if (watch.Elapsed.TotalSeconds > 10) throw new TimeoutException("The search result did not appear: " + label);
            Pump(); Thread.Sleep(10);
        }
        Border row = Rows().Single();
        row.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            { RoutedEvent = UIElement.MouseLeftButtonUpEvent }); Pump();
    }

    private static ContextMenu[] Menus() => PresentationSource.CurrentSources.Cast<PresentationSource>()
        .Select(source => source.RootVisual).OfType<DependencyObject>().SelectMany(Children).OfType<ContextMenu>().Where(menu => menu.IsOpen).ToArray();
    private static void CloseMenus() { foreach (ContextMenu menu in Menus()) menu.IsOpen = false; Pump(); }
    private static void Pump() => Application.Current.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static IEnumerable<DependencyObject> Children(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (DependencyObject child in Children(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
}
