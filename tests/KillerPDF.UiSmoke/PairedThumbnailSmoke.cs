using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

internal static class PairedThumbnailSmoke
{
    internal static int Run(Window owner, string appPath, string appXaml, string folder)
    {
        // This guard precedes activation, opening documents, or injecting input.
        if (!new[] { "SN01", "SN02" }.Contains(Environment.MachineName, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("Paired thumbnail acceptance requires an owned SN01 or SN02 UI VM.");
        Application app = Application.Current ?? throw new InvalidOperationException("Isolated application absent.");
        nint root = new WindowInteropHelper(owner).Handle;
        string stage = "fixture";
        string? failedAt = null;
        Exception? failure = null;
        bool closed = false, rightDown = false, leftDown = false;
        int assertionCount = 0;
        ContextMenu? openedMenu = null;
        var events = new List<object>();
        var hashes = new Dictionary<string, string>();
        string source = Path.Combine(folder, "paired-thumbnail-five-pages.pdf");
        byte[] fixture = FivePages();
        string? longSource = null;
        byte[]? longFixture = null;
        string? paneSource = null;
        byte[]? paneFixture = null;
        object[] originalItems = [];
        ListBox? originalList = null;
        var overall = Stopwatch.StartNew();
        owner.Closed += (_, _) => closed = true;
        DispatcherUnhandledExceptionEventHandler onFailure = (_, e) => { Record(e.Exception); e.Handled = true; };
        app.DispatcherUnhandledException += onFailure;
        try
        {
            hashes["app"] = Hash(appPath);
            hashes["appXaml"] = Hash(appXaml);
            hashes["driver"] = Hash(typeof(PairedThumbnailSmoke).Assembly.Location);
            hashes["engine"] = SelectedAssemblyIdentity.HashLoaded(Assembly.Load("KillerPdf.Engine"), appPath);
            File.WriteAllBytes(source, fixture);
            Stage("public-open");
            owner.Activate();
            SetForegroundWindow(root);
            MethodInfo open = owner.GetType().GetMethod("OpenFromExternal", BindingFlags.Public | BindingFlags.Instance,
                null, [typeof(string)], null) ?? throw new MissingMethodException("Public MainWindow.OpenFromExternal(string)");
            open.Invoke(owner, [source]);
            Wait(() => owner.FindName("PageList") is ListBox list && list.IsVisible && list.IsEnabled && list.Items.Count == 5
                && owner.FindName("FileNameLabel") is TextBlock label && label.Text == Path.GetFileName(source));
            originalList = (ListBox)owner.FindName("PageList");
            originalItems = originalList.Items.Cast<object>().ToArray();

            Stage("public-two-page-mode");
            Wait(() => owner.FindName("ViewTwoPageRadio") is RadioButton { IsVisible: true, IsEnabled: true });
            var mode = (RadioButton)owner.FindName("ViewTwoPageRadio");
            Pattern<ISelectionItemProvider>(new RadioButtonAutomationPeer(mode), PatternInterface.SelectionItem).Select();
            Wait(() => mode.IsChecked == true);
            Wait(() => Descendants(owner).OfType<Panel>().Any(panel => panel.Name == "PageContentPanel" && panel.IsVisible)
                && Descendants(owner).OfType<Panel>().Any(panel => panel.Name == "ContinuousPanel" && panel.Visibility == Visibility.Collapsed));
            events.Add(new { stage, panels = Descendants(owner).OfType<Panel>()
                .Where(panel => panel.Name is "PageContentPanel" or "ContinuousPanel")
                .Select(panel => new { panel.Name, type = panel.GetType().FullName, panel.IsVisible, visibility = panel.Visibility.ToString() }).ToArray() });
            Stable();
            Stage("one-column-baseline");
            Rect[] baseline = Rectangles();
            Require(Enumerable.Range(1, 3).All(i => Near(baseline[i].Left, baseline[0].Left)
                && baseline[i].Top >= baseline[i - 1].Bottom - 1), "Default sidebar must have one column before opting in.");
            CheckItems();
            events.Add(new { stage, rectangles = baseline.Select(RectData).ToArray(), itemCount = originalList.Items.Count });

            Stage("guarded-sidebar-context-menu");
            Wait(() => owner.FindName("SidebarPanelTitle") is TextBlock { IsVisible: true, IsEnabled: true } title
                && title.ActualWidth > 8 && title.ActualHeight > 4);
            var title = (TextBlock)owner.FindName("SidebarPanelTitle");
            Point screen = title.PointToScreen(new Point(title.ActualWidth / 2, title.ActualHeight / 2));
            var pixel = new NativePoint { X = (int)Math.Round(screen.X), Y = (int)Math.Round(screen.Y) };
            Point exact = new(pixel.X, pixel.Y);
            ValidateTitle(title, exact, pixel);
            if (!SetCursorPos(pixel.X, pixel.Y)) throw new System.ComponentModel.Win32Exception();
            Pump();
            ValidateTitle(title, exact, pixel);
            try
            {
                SendMouse(0x0008); rightDown = true;
                Pump();
                SendMouse(0x0010); rightDown = false;
                Pump();
            }
            finally { if (rightDown) { SendMouse(0x0010); rightDown = false; } }
            Wait(() => PopupNodes().OfType<ContextMenu>().Any(m => m.IsOpen && m.IsVisible), 3000);
            openedMenu = PopupNodes().OfType<ContextMenu>().Single(m => m.IsOpen && m.IsVisible);
            Stage("find-paired-thumbnail-option");
            // One dispatcher idle turn completes the opened menu's layout. Absence is now a bounded RED.
            openedMenu.UpdateLayout(); Pump();
            MenuItem? option = PairedOption(openedMenu);
            if (option is null)
                throw new MissingOptionException("Opened the real owned sidebar menu after public open, Two Page selection, and one-column baseline; TwoPageThumbnailsMenuItem is absent.");
            Require(option.IsVisible && option.IsEnabled && option.IsCheckable,
                "Two-page thumbnail option must be a visible, enabled, checkable menu item.");
            Require(!option.IsChecked, "Two-page thumbnails must default OFF.");
            Stage("public-option-activation");
            Pattern<IInvokeProvider>(new MenuItemAutomationPeer(option), PatternInterface.Invoke).Invoke();
            Wait(() => !openedMenu.IsOpen);
            Stable();
            Stage("paired-container-geometry");
            Rect[] paired = Rectangles();
            CheckItems();
            Require(Near(paired[0].Top, paired[1].Top) && paired[1].Left >= paired[0].Right - 1,
                "Pages 1 and 2 must share a row with page 2 to the right.");
            Require(Near(paired[2].Top, paired[3].Top) && paired[3].Left >= paired[2].Right - 1
                && paired[2].Top >= Math.Max(paired[0].Bottom, paired[1].Bottom) - 1,
                "Pages 3 and 4 must share the next row in page order.");
            Require(Near(paired[0].Left, paired[2].Left) && Near(paired[1].Left, paired[3].Left),
                "Both paired rows must align in the same two columns.");
            events.Add(new { stage, rectangles = paired.Select(RectData).ToArray(), itemCount = originalList.Items.Count,
                independentItemsPreserved = true, originalListPreserved = true });
            CaptureSidebar("paired-sidebar");
            Stage("book-cover-centered");
            var toggleBook = owner.GetType().GetMethod("ToggleBookMode", BindingFlags.Instance | BindingFlags.NonPublic)!;
            toggleBook.Invoke(owner, null);
            Stable();
            Rect[] book = Rectangles();
            Require(Near(book[0].Left, paired[0].Left + paired[0].Width / 2)
                && book[1].Top >= book[0].Bottom - 1 && Near(book[1].Top, book[2].Top)
                && Near(book[1].Left, paired[0].Left) && Near(book[2].Left, paired[1].Left),
                "Book layout centers page 1 alone before the page 2 and 3 spread.");
            CaptureSidebar("book-cover-centered");
            toggleBook.Invoke(owner, null);
            Stable();
            Require(Near(Rectangles()[0].Top, Rectangles()[1].Top), "Turning Book layout off restores page 1 and 2 as a pair.");
            Stage("marquee-both-columns");
            var first = (ListBoxItem)originalList.ItemContainerGenerator.ContainerFromIndex(0);
            var fourth = (ListBoxItem)originalList.ItemContainerGenerator.ContainerFromIndex(3);
            Point from = first.PointToScreen(new Point(2, 2));
            Point to = fourth.PointToScreen(new Point(fourth.ActualWidth - 2, fourth.ActualHeight - 2));
            ValidatePagePoint(from);
            IInputElement? startHit = owner.InputHitTest(owner.PointFromScreen(from));
            if (startHit is not Border || startHit is FrameworkElement { Name: "ThumbBorder" })
                throw new InvalidOperationException("Marquee start must hit the noninteractive item gutter border.");
            if (!SetCursorPos((int)Math.Round(from.X), (int)Math.Round(from.Y))) throw new System.ComponentModel.Win32Exception();
            try
            {
                SendMouse(2); leftDown = true; Pump();
                for (int step = 1; step <= 8; step++)
                {
                    Point point = from + (to - from) * (step / 8d);
                    ValidatePagePoint(point);
                    if (!SetCursorPos((int)Math.Round(point.X), (int)Math.Round(point.Y))) throw new System.ComponentModel.Win32Exception();
                    Pump(); Thread.Sleep(20); Pump();
                }
                if (owner.FindName("PageSelectionBox") is not FrameworkElement { IsVisible: true, ActualWidth: > 2, ActualHeight: > 2 })
                    throw new InvalidOperationException("Actual marquee gesture did not create the visible selection box.");
                events.Add(new { stage, selected = originalList.SelectedItems.Cast<object>()
                    .Select(item => Array.IndexOf(originalItems, item)).Order().ToArray() });
                CaptureSidebar("paired-sidebar-marquee");
                Require(originalList.SelectedItems.Count == 4 && originalItems.Take(4).All(originalList.SelectedItems.Contains),
                    "Marquee across the first two paired rows must select exactly original pages 1 through 4.");
            }
            finally { if (leftDown) { SendMouse(4); leftDown = false; Pump(); } }

            Stage("same-row-drag-setup");
            if (owner.FindName("PageDropIndicator") is not FrameworkElement { IsVisible: false })
                throw new InvalidOperationException("Insertion-marker absence control failed before the native drag.");
            Wait(() => Enumerable.Range(0, 5).All(i => Thumbnail(i)?.Source is BitmapSource));
            string[] before = ColorOrder();
            if (!before.SequenceEqual(new[] { "red", "green", "blue", "orange", "magenta" }))
                throw new InvalidOperationException("Initial public thumbnail colors do not identify the five source pages.");
            Image firstImage = Thumbnail(0)!;
            Image secondImage = Thumbnail(1)!;
            Point dragStart = Rounded(firstImage.PointToScreen(new Point(firstImage.ActualWidth * .5, firstImage.ActualHeight * .3)));
            Point dragEnd = Rounded(secondImage.PointToScreen(new Point(secondImage.ActualWidth * .8, secondImage.ActualHeight * .3)));
            ValidateImagePoint(firstImage, dragStart);
            ValidateImagePoint(secondImage, dragEnd);
            Move(dragStart); Pump(); ValidateImagePoint(firstImage, dragStart);
            try { SendMouse(2); leftDown = true; Pump(); }
            finally { if (leftDown) { SendMouse(4); leftDown = false; Pump(); } }
            if (originalList.SelectedItems.Count != 1 || !ReferenceEquals(originalList.SelectedItem, originalItems[0]))
                throw new InvalidOperationException("Physical first-thumbnail click did not collapse selection to only the first page.");
            events.Add(new { stage, before, selectedOriginalIndex = 0, dragStart, dragEnd });

            Stage("same-row-native-drag");
            var second = (ListBoxItem)originalList.ItemContainerGenerator.ContainerFromIndex(1);
            Rect secondCell = new(second.TranslatePoint(new Point(), originalList), new Size(second.ActualWidth, second.ActualHeight));
            Rect? markerAtDestination = null;
            int markerSamples = 0;
            using var released = new ManualResetEventSlim();
            Exception? inputFailure = null;
            Point destinationInList = originalList.PointFromScreen(dragEnd);
            DragEventHandler observeMarker = (_, e) =>
            {
                if ((e.GetPosition(originalList) - destinationInList).Length <= 2
                    && owner.FindName("PageDropIndicator") is FrameworkElement { IsVisible: true, ActualWidth: > 0, ActualHeight: > 0 } marker)
                {
                    markerAtDestination = new Rect(marker.TranslatePoint(new Point(), originalList), new Size(marker.ActualWidth, marker.ActualHeight));
                    markerSamples++;
                    if (markerSamples == 2) CaptureSidebar("paired-sidebar-drag-marker");
                }
            };
            Point[] moves = Enumerable.Range(1, 10).Select(step => Rounded(dragStart + (dragEnd - dragStart) * (step / 10d))).ToArray();
            foreach (Point point in moves) ValidatePagePoint(point);
            uint ownedProcess = (uint)Environment.ProcessId;
            // Native OLE dragging can block the timer callback that initiated it.
            // WPF observations stay on the UI thread; only bounded native input runs here.
            var inputThread = new Thread(() =>
            {
                try
                {
                    foreach (Point point in moves.Concat(Enumerable.Repeat(dragEnd, 8)))
                    {
                        Thread.Sleep(40);
                        nint hit = WindowFromPoint(new NativePoint { X = (int)point.X, Y = (int)point.Y });
                        if (GetForegroundWindow() != root || GetWindowThreadProcessId(hit, out uint process) == 0 || process != ownedProcess)
                            throw new InvalidOperationException("Native drag left the prevalidated owned foreground surface.");
                        if (!SetCursorPos((int)point.X, (int)point.Y)) throw new System.ComponentModel.Win32Exception();
                    }
                }
                catch (Exception error) { inputFailure = error; }
                finally { SendMouse(4); released.Set(); }
            }) { IsBackground = true };
            originalList.AddHandler(DragDrop.DragOverEvent, observeMarker, true);
            try
            {
                Move(dragStart); ValidateImagePoint(firstImage, dragStart);
                SendMouse(2); leftDown = true;
                inputThread.Start();
                Wait(() => released.IsSet, 6000);
            }
            finally
            {
                if (inputThread.IsAlive && !inputThread.Join(2000)) throw new TimeoutException("Native input thread did not stop.");
                originalList.RemoveHandler(DragDrop.DragOverEvent, observeMarker);
                if (leftDown) { SendMouse(4); leftDown = false; Pump(); }
            }
            if (inputFailure is not null) throw new InvalidOperationException("Native drag input failed.", inputFailure);
            if (markerAtDestination is null || markerSamples < 2)
                throw new InvalidOperationException("Native drag did not expose a stable public insertion marker at the destination.");
            Stage("same-row-drop-result");
            Stable();
            Wait(() => Enumerable.Range(0, 5).All(i => Thumbnail(i)?.Source is BitmapSource));
            string[] after = ColorOrder();
            Rect actualMarker = markerAtDestination.Value;
            bool markerMatches = actualMarker.Height > actualMarker.Width * 3
                && Math.Abs(actualMarker.Left + actualMarker.Width / 2 - secondCell.Right) <= 5
                && Math.Abs(actualMarker.Top - secondCell.Top) <= 5
                && Math.Abs(actualMarker.Bottom - secondCell.Bottom) <= 5;
            bool orderMatches = after.SequenceEqual(new[] { "green", "red", "blue", "orange", "magenta" });
            events.Add(new { stage, after, expected = new[] { "green", "red", "blue", "orange", "magenta" }, markerMatches, orderMatches });
            CaptureSidebar("paired-sidebar-after-drop");
            assertionCount++;
            if (!markerMatches || !orderMatches)
                throw new ReorderException("Dragging first page after second must show a vertical marker at the second cell's end and produce green, red, blue, orange, magenta. Marker matches: "
                    + markerMatches + "; actual order: " + string.Join(", ", after));
            foreach ((string name, bool pairedExpected) in new[]
                {
                    ("ViewContinuousRadio", false), ("ViewTwoPageContinuousRadio", true),
                    ("ViewSingleRadio", false), ("ViewTwoPageRadio", true)
                })
            {
                Stage("mode-sidebar-" + name);
                Wait(() => owner.FindName(name) is RadioButton { IsVisible: true, IsEnabled: true });
                var radio = (RadioButton)owner.FindName(name);
                var viewport = Descendants(owner).OfType<ScrollViewer>()
                    .Single(view => view.Name == "PagePreviewPanel" && view.IsVisible);
                Pattern<ISelectionItemProvider>(new RadioButtonAutomationPeer(radio), PatternInterface.SelectionItem).Select();
                Wait(() => radio.IsChecked == true);
                Wait(() => viewport.Opacity < .99);
                Wait(() => viewport.Opacity >= .99);
                bool continuousExpected = name is "ViewContinuousRadio" or "ViewTwoPageContinuousRadio";
                Wait(() => Descendants(owner).OfType<Panel>().Any(panel => panel.Name == "ContinuousPanel"
                    && panel.IsVisible == continuousExpected)
                    && Descendants(owner).OfType<Panel>().Any(panel => panel.Name == "PageContentPanel"
                        && panel.IsVisible != continuousExpected));
                Stable();
                Rect[] current = Rectangles();
                bool isPair = Near(current[0].Top, current[1].Top) && current[1].Left >= current[0].Right - 1
                    && Near(current[2].Top, current[3].Top);
                bool isColumn = Enumerable.Range(1, 3).All(i => Near(current[i].Left, current[0].Left)
                    && current[i].Top >= current[i - 1].Bottom - 1);
                events.Add(new { stage, pairedExpected, isPair, isColumn, rectangles = current.Select(RectData).ToArray() });
                Require(pairedExpected ? isPair : isColumn, "Sidebar must follow eligibility while preserving its paired preference: " + name);
                Require(ReferenceEquals(owner.FindName("PageList"), originalList) && originalList.Items.Count == 5
                    && ColorOrder().SequenceEqual(new[] { "green", "red", "blue", "orange", "magenta" }),
                    "Switching view modes must preserve the original list and reordered pages.");
            }
            foreach (string side in new[] { "Right", "Top", "Bottom", "Left" })
            {
                Stage("sidebar-side-" + side);
                OpenSidebarMenu();
                string caption = (string)app.FindResource("Str_Sidebar_" + side);
                var sidebarRow = Descendants(openedMenu!).OfType<MenuItem>().Single(item => item.Name == "SidebarMenuItem");
                sidebarRow.IsSubmenuOpen = true;
                openedMenu!.UpdateLayout(); Pump();
                var sideOption = sidebarRow.Items.OfType<MenuItem>().Single(item => Equals(item.Header, caption));
                Pattern<IInvokeProvider>(new MenuItemAutomationPeer(sideOption), PatternInterface.Invoke).Invoke();
                Wait(() => !openedMenu!.IsOpen);
                Stable();
                Rect[] current = Rectangles();
                bool strip = side is "Top" or "Bottom";
                bool correctGeometry = strip
                    ? Enumerable.Range(1, 3).All(i => Near(current[i].Top, current[0].Top)
                        && current[i].Left >= current[i - 1].Right - 1)
                    : Near(current[0].Top, current[1].Top) && current[1].Left >= current[0].Right - 1
                        && Near(current[2].Top, current[3].Top) && current[2].Top > current[0].Top;
                events.Add(new { stage, strip, rectangles = current.Select(RectData).ToArray() });
                Require(correctGeometry, "Paired sidebars must retain two columns, and top/bottom sidebars must retain their horizontal strip: " + side);
                Require(ColorOrder().SequenceEqual(new[] { "green", "red", "blue", "orange", "magenta" }),
                    "Moving the sidebar must preserve page order.");
                OpenSidebarMenu();
                var preference = PairedOption(openedMenu!) ?? throw new MissingOptionException("TwoPageThumbnailsMenuItem is absent from the Sidebar flyout.");
                Require(preference.IsChecked && preference.IsEnabled != strip,
                    "Paired preference must remain checked and be disabled only in a horizontal strip.");
                openedMenu!.IsOpen = false;
                Pump();
            }
            Stage("paired-keyboard-focus");
            Image keyStart = Thumbnail(0)!;
            Point keyPoint = Rounded(keyStart.PointToScreen(new Point(keyStart.ActualWidth / 2, keyStart.ActualHeight / 2)));
            ValidateImagePoint(keyStart, keyPoint);
            Move(keyPoint);
            try { SendMouse(2); leftDown = true; Pump(); }
            finally { if (leftDown) { SendMouse(4); leftDown = false; Pump(); } }
            Wait(() => originalList.SelectedIndex == 0);
            var firstCell = (ListBoxItem)originalList.ItemContainerGenerator.ContainerFromIndex(0);
            Require(firstCell.Focus() && firstCell.IsKeyboardFocusWithin,
                "The first paired thumbnail must accept keyboard focus.");
            foreach ((ushort key, int expectedIndex) in new (ushort, int)[]
                { (0x27, 2), (0x27, 4), (0x25, 2), (0x25, 0) })
            {
                Stage("paired-keyboard-" + key);
                if (!Owned(root) || GetForegroundWindow() != root || !originalList.IsKeyboardFocusWithin
                    || Keyboard.Modifiers != ModifierKeys.None)
                    throw new InvalidOperationException("Keyboard input requires unmodified focus within the owned thumbnail list.");
                SendKey(key);
                var keyWatch = Stopwatch.StartNew();
                while (originalList.SelectedIndex != expectedIndex && keyWatch.ElapsedMilliseconds < 1000) Pump();
                events.Add(new { stage, expectedIndex, actualIndex = originalList.SelectedIndex, originalList.IsKeyboardFocusWithin });
                Require(originalList.SelectedIndex == expectedIndex && originalList.SelectedItems.Count == 1,
                    "Focused thumbnails must preserve the established Left/Right document spread navigation.");
            }
            Stage("long-document-public-open");
            longFixture = FivePages(1000);
            longSource = Path.Combine(folder, "paired-thousand-pages.pdf");
            File.WriteAllBytes(longSource, longFixture);
            open.Invoke(owner, [longSource]);
            Wait(() => originalList.Items.Count == 1000
                && owner.FindName("FileNameLabel") is TextBlock label && label.Text == Path.GetFileName(longSource));
            Stable();
            Stage("long-document-last-row");
            originalList.ScrollIntoView(originalList.Items[998]);
            Wait(() => Enumerable.Range(998, 2).All(i =>
                originalList.ItemContainerGenerator.ContainerFromIndex(i) is ListBoxItem { IsVisible: true, ActualWidth: > 1, ActualHeight: > 1 }
                && Thumbnail(i)?.Source is BitmapSource));
            originalList.UpdateLayout();
            var lastLeft = (ListBoxItem)originalList.ItemContainerGenerator.ContainerFromIndex(998);
            var lastRight = (ListBoxItem)originalList.ItemContainerGenerator.ContainerFromIndex(999);
            Rect leftBounds = new(lastLeft.TranslatePoint(new Point(), originalList), new Size(lastLeft.ActualWidth, lastLeft.ActualHeight));
            Rect rightBounds = new(lastRight.TranslatePoint(new Point(), originalList), new Size(lastRight.ActualWidth, lastRight.ActualHeight));
            int realized = Enumerable.Range(0, 1000).Count(i => originalList.ItemContainerGenerator.ContainerFromIndex(i) is not null);
            events.Add(new { stage, realized, leftBounds = RectData(leftBounds), rightBounds = RectData(rightBounds) });
            Require(Near(leftBounds.Top, rightBounds.Top) && rightBounds.Left >= leftBounds.Right - 1
                && leftBounds.Top >= 0 && leftBounds.Bottom <= originalList.ActualHeight + 1,
                "The last paired row of a thousand-page document must be visible after ScrollIntoView.");
            Require(realized <= 40, "Long paired sidebars must keep container realization bounded.");
            Require(originalList.ItemContainerGenerator.IndexFromContainer(lastLeft) == 998
                && originalList.ItemContainerGenerator.IndexFromContainer(lastRight) == 999
                && ReferenceEquals(lastLeft.DataContext, originalList.Items[998])
                && ReferenceEquals(lastRight.DataContext, originalList.Items[999]),
                "Virtualized last-row containers must retain their original page identities.");
            Require(ColorAt(998) == "orange" && ColorAt(999) == "magenta",
                "The virtualized last row must show its own page content rather than recycled earlier thumbnails.");
            Stage("paired-split-open");
            Wait(() => owner.FindName("SplitPaneRailBtn") is Button { IsVisible: true, IsEnabled: true });
            var split = (Button)owner.FindName("SplitPaneRailBtn");
            Pattern<IInvokeProvider>(new ButtonAutomationPeer(split), PatternInterface.Invoke).Invoke();
            var paneA = (FrameworkElement)owner.FindName("Viewer");
            var paneB = (FrameworkElement)owner.FindName("ViewerB");
            Wait(() => paneA.IsVisible && paneB.IsVisible && paneA.ActualWidth >= 320 && paneB.ActualWidth >= 320);
            FocusOwnedPane(paneB);
            paneFixture = FivePages();
            paneSource = Path.Combine(folder, "paired-other-pane.pdf");
            File.WriteAllBytes(paneSource, paneFixture);
            open.Invoke(owner, [paneSource]);
            Wait(() => originalList.Items.Count == 5
                && owner.FindName("FileNameLabel") is TextBlock label && label.Text == Path.GetFileName(paneSource));
            SelectPaneMode(paneB, "ViewSingleRadio");
            CheckPaneSidebar(paneSource, 5, "ViewSingleRadio", false);
            FocusOwnedPane(paneA);
            CheckPaneSidebar(longSource, 1000, "ViewTwoPageRadio", true);
            FocusOwnedPane(paneB);
            CheckPaneSidebar(paneSource, 5, "ViewSingleRadio", false);
            SelectPaneMode(paneB, "ViewTwoPageContinuousRadio");
            CheckPaneSidebar(paneSource, 5, "ViewTwoPageContinuousRadio", true);
            FocusOwnedPane(paneA);
            CheckPaneSidebar(longSource, 1000, "ViewTwoPageRadio", true);
            FocusOwnedPane(paneB);
            CheckPaneSidebar(paneSource, 5, "ViewTwoPageContinuousRadio", true);
        }
        catch (Exception error)
        {
            Record(error);
            if (originalList is not null)
            {
                var scroll = Descendants(originalList).OfType<ScrollViewer>().FirstOrDefault();
                var ancestors = new List<object>();
                for (DependencyObject? node = originalList; node is FrameworkElement element; node = VisualTreeHelper.GetParent(node))
                    ancestors.Add(new { type = element.GetType().Name, element.Name, element.IsVisible,
                        visibility = element.Visibility.ToString(), element.ActualWidth, element.ActualHeight });
                events.Add(new { diagnostic = stage, originalList.SelectedIndex, originalList.IsVisible,
                    listVisibility = originalList.Visibility.ToString(), ancestors,
                    pagesTag = (owner.FindName("SidebarPagesTab") as FrameworkElement)?.Tag,
                    count = originalList.Items.Count,
                    realized = Enumerable.Range(0, originalList.Items.Count)
                        .Count(index => originalList.ItemContainerGenerator.ContainerFromIndex(index) is not null),
                    scroll = scroll is null ? null : new { scroll.CanContentScroll, scroll.VerticalOffset, scroll.ExtentHeight, scroll.ViewportHeight },
                    cells = new[] { 0, 998, 999 }.Where(index => index < originalList.Items.Count).Select(index =>
                    {
                        var item = originalList.ItemContainerGenerator.ContainerFromIndex(index) as ListBoxItem;
                        return new { index, realized = item is not null, item?.IsVisible, item?.ActualWidth, item?.ActualHeight,
                            parent = item is null ? null : VisualTreeHelper.GetParent(item)?.GetType().Name,
                            bounds = item is null ? null : RectData(new Rect(item.TranslatePoint(new Point(), originalList),
                                new Size(item.ActualWidth, item.ActualHeight))), thumbnail = Thumbnail(index)?.Source is not null };
                    }).ToArray() });
            }
        }
        finally
        {
            if (failure is not null) File.WriteAllText(Path.Combine(folder, "failure.txt"), failure.ToString());
            try { if (rightDown) { SendMouse(0x0010); rightDown = false; } }
            catch (Exception error) { Record(error); }
            try { if (leftDown) { SendMouse(4); leftDown = false; } }
            catch (Exception error) { Record(error); }
            try
            {
                if (openedMenu is not null && openedMenu.IsOpen && PopupNodes().Contains(openedMenu)) openedMenu.IsOpen = false;
                CloseOwned();
            }
            catch (Exception error) { Record(error); }
            app.DispatcherUnhandledException -= onFailure;
            bool sourcepreserved = File.Exists(source) && File.ReadAllBytes(source).AsSpan().SequenceEqual(fixture);
            if (longSource is not null && longFixture is not null)
                sourcepreserved &= File.Exists(longSource) && File.ReadAllBytes(longSource).AsSpan().SequenceEqual(longFixture);
            if (paneSource is not null && paneFixture is not null)
                sourcepreserved &= File.Exists(paneSource) && File.ReadAllBytes(paneSource).AsSpan().SequenceEqual(paneFixture);
            if (!sourcepreserved) Record(new InvalidOperationException("Synthetic source bytes changed."));
            bool destroyed = !IsWindow(root);
            if (!closed || !destroyed) Record(new InvalidOperationException("Owned window did not close and destroy its handle."));
            File.WriteAllText(Path.Combine(folder, "result.json"), JsonSerializer.Serialize(new
            {
                passed = failure is null,
                verdict = failure is null ? "GREEN_PAIRED_THUMBNAILS" : failure is MissingOptionException
                    ? "RED_MISSING_PAIRED_THUMBNAILS" : failure is ReorderException ? "RED_PAIRED_SAME_ROW_REORDER"
                    : failure is GeometryException ? "RED_PAIRED_THUMBNAILS" : "SETUP_FAILURE",
                stage, failedAt, error = failure?.ToString(), assertionCount, sourcepreserved, closed,
                ownedHandleDestroyed = destroyed, ownedHandle = root.ToInt64(), machine = Environment.MachineName,
                sourceSha256 = Convert.ToHexString(SHA256.HashData(fixture)), hashes, events,
                scope = "Public sidebar option, independent item geometry, marquee, and one native first-page-after-second drag with public marker bounds and thumbnail color order."
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        return failure is null ? 0 : 1;

        void Stage(string value) { stage = value; File.WriteAllText(Path.Combine(folder, "stage.txt"), value); }
        void SelectPaneMode(FrameworkElement pane, string name)
        {
            Stage("paired-pane-select-" + name);
            var radio = (RadioButton)owner.FindName(name);
            Wait(() => radio.IsVisible && radio.IsEnabled);
            if (radio.IsChecked == true) return;
            var viewport = Descendants(pane).OfType<ScrollViewer>().Single(view => view.Name == "PagePreviewPanel");
            Pattern<ISelectionItemProvider>(new RadioButtonAutomationPeer(radio), PatternInterface.SelectionItem).Select();
            Wait(() => radio.IsChecked == true);
            Wait(() => viewport.Opacity < .99);
            Wait(() => viewport.Opacity >= .99);
        }
        void CheckPaneSidebar(string path, int count, string modeName, bool paired)
        {
            Stage("paired-pane-check-" + Path.GetFileName(path) + "-" + modeName);
            Wait(() => originalList!.Items.Count == count
                && owner.FindName("FileNameLabel") is TextBlock label && label.Text == Path.GetFileName(path));
            originalList!.ScrollIntoView(originalList.Items[0]);
            Stable();
            Rect[] current = Rectangles();
            bool geometry = paired
                ? Near(current[0].Top, current[1].Top) && current[1].Left >= current[0].Right - 1
                : Enumerable.Range(1, 3).All(i => Near(current[i].Left, current[0].Left)
                    && current[i].Top >= current[i - 1].Bottom - 1);
            events.Add(new { stage, count, paired, mode = modeName, geometry, rectangles = current.Select(RectData).ToArray() });
            Require(owner.FindName(modeName) is RadioButton { IsChecked: true } && geometry,
                "Pane focus must restore its own view-mode control and sidebar layout.");
            Require(ColorOrder().SequenceEqual(new[] { "red", "green", "blue", "orange", "magenta" }),
                "Pane focus must restore that document's original page thumbnails.");
        }
        void FocusOwnedPane(FrameworkElement pane)
        {
            Stage("paired-pane-focus-" + pane.Name);
            var settling = Stopwatch.StartNew();
            string previous = ""; int repeats = 0;
            Wait(() =>
            {
                string current = JsonSerializer.Serialize(new { pane.ActualWidth, pane.ActualHeight, screen = pane.PointToScreen(new Point()) });
                if (current != previous) { previous = current; repeats = 0; settling.Restart(); }
                else repeats++;
                return repeats > 8 && settling.ElapsedMilliseconds >= 150;
            });
            Point local = PageNavigationFocusProbe.NeutralPaneFocusPoint(owner, pane);
            Point point = Rounded(owner.PointToScreen(local));
            var pixel = new NativePoint { X = (int)point.X, Y = (int)point.Y };
            var border = owner.InputHitTest(owner.PointFromScreen(point)) as Border;
            void Guard()
            {
                if (!Owned(root) || GetForegroundWindow() != root || !Owned(WindowFromPoint(pixel))
                    || !SystemParameters.WorkArea.Contains(point) || border is not { Name: "PaneBorder" }
                    || !pane.IsAncestorOf(border) || !ReferenceEquals(owner.InputHitTest(owner.PointFromScreen(point)), border))
                    throw new InvalidOperationException("Pane focus pixel must hit the owned noninteractive neutral pane border.");
            }
            Guard();
            if (!SetCursorPos(pixel.X, pixel.Y)) throw new System.ComponentModel.Win32Exception();
            Pump(); Guard();
            try { SendMouse(2); leftDown = true; Pump(); }
            finally { if (leftDown) { SendMouse(4); leftDown = false; Pump(); } }
        }
        void OpenSidebarMenu()
        {
            Wait(() => owner.FindName("SidebarPanelTitle") is TextBlock { IsVisible: true, IsEnabled: true, ActualWidth: > 8, ActualHeight: > 4 });
            var target = (TextBlock)owner.FindName("SidebarPanelTitle");
            Point point = Rounded(target.PointToScreen(new Point(target.ActualWidth / 2, target.ActualHeight / 2)));
            var pixel = new NativePoint { X = (int)point.X, Y = (int)point.Y };
            ValidateTitle(target, point, pixel);
            if (!SetCursorPos(pixel.X, pixel.Y)) throw new System.ComponentModel.Win32Exception();
            Pump();
            ValidateTitle(target, point, pixel);
            try
            {
                SendMouse(0x0008); rightDown = true; Pump();
                SendMouse(0x0010); rightDown = false; Pump();
            }
            finally { if (rightDown) { SendMouse(0x0010); rightDown = false; } }
            Wait(() => PopupNodes().OfType<ContextMenu>().Any(menu => menu.IsOpen && menu.IsVisible), 3000);
            openedMenu = PopupNodes().OfType<ContextMenu>().Single(menu => menu.IsOpen && menu.IsVisible);
            openedMenu.UpdateLayout();
            Pump();
        }
        void Record(Exception error) { if (failure is null) { failure = error; failedAt = stage; } }
        void Require(bool condition, string message)
        {
            assertionCount++;
            if (!condition) throw new GeometryException(message);
        }
        void CheckItems()
        {
            Require(ReferenceEquals(owner.FindName("PageList"), originalList) && originalList!.Items.Count == 5,
                "The original PageList and all five independent page items must remain.");
            for (int i = 0; i < 5; i++)
                Require(ReferenceEquals(originalList!.Items[i], originalItems[i]), "Original page item order changed at index " + i);
            for (int i = 0; i < 4; i++)
            {
                var container = originalList!.ItemContainerGenerator.ContainerFromIndex(i);
                Require(container is ListBoxItem && originalList.ItemContainerGenerator.IndexFromContainer(container) == i
                    && ReferenceEquals(originalList.ItemContainerGenerator.ItemFromContainer(container), originalItems[i]),
                    "Public container mapping changed at index " + i);
            }
        }
        Rect[] Rectangles() => Enumerable.Range(0, 4).Select(i =>
        {
            if (originalList!.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem { IsVisible: true } item
                || item.ActualWidth <= 0 || item.ActualHeight <= 0)
                throw new InvalidOperationException("Visible generated ListBoxItem absent at index " + i);
            return new Rect(item.TranslatePoint(new Point(), originalList), new Size(item.ActualWidth, item.ActualHeight));
        }).ToArray();
        void Stable()
        {
            string previous = ""; int repeats = 0;
            var settled = Stopwatch.StartNew();
            var sampling = Stopwatch.StartNew();
            long lastSample = -15;
            Wait(() =>
            {
                if (sampling.ElapsedMilliseconds - lastSample < 15) return false;
                lastSample = sampling.ElapsedMilliseconds;
                owner.UpdateLayout();
                if (Enumerable.Range(0, 4).Any(i => originalList!.ItemContainerGenerator.ContainerFromIndex(i)
                    is not ListBoxItem { IsVisible: true, ActualWidth: > 0, ActualHeight: > 0 } item
                    || !Descendants(item).OfType<Image>().Any(image => image.IsVisible && image.Source is not null
                        && image.ActualWidth > 1 && image.ActualHeight > 1))) return false;
                string current = JsonSerializer.Serialize(Rectangles().Select(RectData));
                if (current != previous) { repeats = 0; settled.Restart(); }
                else repeats++;
                previous = current;
                return repeats >= 8 && settled.ElapsedMilliseconds >= 150;
            });
        }
        void Wait(Func<bool> ready, int timeout = 12000, bool teardown = false)
        {
            var watch = Stopwatch.StartNew();
            while (!ready())
            {
                if (!teardown && failure is not null) throw new InvalidOperationException("Dispatcher failed at " + stage, failure);
                if (!teardown && (closed || overall.ElapsedMilliseconds > 45000)) throw new TimeoutException("Tracer deadline at " + stage);
                if (watch.ElapsedMilliseconds > timeout) throw new TimeoutException("Readiness deadline at " + stage);
                Pump();
            }
        }
        bool Owned(nint handle) => handle != 0 && IsWindow(handle)
            && GetWindowThreadProcessId(handle, out uint process) != 0 && process == Environment.ProcessId
            && (GetAncestor(handle, 2) == root || GetWindow(GetAncestor(handle, 2), 4) == root);
        DependencyObject[] PopupNodes() => PresentationSource.CurrentSources.OfType<HwndSource>()
            .Where(s => s.Handle != root && Owned(s.Handle) && s.RootVisual is not null)
            .SelectMany(s => Descendants(s.RootVisual!)).ToArray();
        void ValidateTitle(TextBlock target, Point exact, NativePoint pixel)
        {
            if (!Owned(root) || GetForegroundWindow() != root || !Owned(WindowFromPoint(pixel))
                || !SystemParameters.WorkArea.Contains(exact) || !target.IsVisible || !target.IsEnabled)
                throw new InvalidOperationException("Sidebar input endpoint is not visible, foreground, in the work area, and owned.");
            IInputElement? hit = owner.InputHitTest(owner.PointFromScreen(exact));
            if (!ReferenceEquals(hit, target))
                throw new InvalidOperationException("Rounded sidebar input pixel did not hit the exact noninteractive title: " + hit?.GetType().FullName);
            events.Add(new { stage, exact.X, exact.Y, hitType = hit.GetType().FullName, target = target.Name });
        }
        void ValidatePagePoint(Point point)
        {
            var pixel = new NativePoint { X = (int)Math.Round(point.X), Y = (int)Math.Round(point.Y) };
            Point exact = new(pixel.X, pixel.Y);
            if (!Owned(root) || GetForegroundWindow() != root || !Owned(WindowFromPoint(pixel))
                || !SystemParameters.WorkArea.Contains(exact)
                || !new Rect(originalList!.PointToScreen(new Point()), originalList.PointToScreen(new Point(originalList.ActualWidth, originalList.ActualHeight))).Contains(exact))
                throw new InvalidOperationException("Sidebar pointer or capture endpoint is not visible and owned.");
        }
        Image? Thumbnail(int index) => originalList!.ItemContainerGenerator.ContainerFromIndex(index) is ListBoxItem item
            ? Descendants(item).OfType<Image>().FirstOrDefault(image => image.IsVisible && image.Source is not null && image.ActualWidth > 1 && image.ActualHeight > 1)
            : null;
        string[] ColorOrder() => Enumerable.Range(0, 5).Select(ColorAt).ToArray();
        string ColorAt(int index)
        {
            if (Thumbnail(index)?.Source is not BitmapSource bitmap)
                throw new InvalidOperationException("Public bitmap thumbnail absent at index " + index);
            var bgra = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
            byte[] pixel = new byte[4];
            bgra.CopyPixels(new Int32Rect(bgra.PixelWidth / 2, bgra.PixelHeight / 2, 1, 1), pixel, 4, 0);
            (string Name, int R, int G, int B)[] colors = [("red", 255, 0, 0), ("green", 0, 255, 0), ("blue", 0, 0, 255), ("orange", 255, 128, 0), ("magenta", 255, 0, 255)];
            var matches = colors.Where(color => Math.Abs(pixel[2] - color.R) <= 35 && Math.Abs(pixel[1] - color.G) <= 35 && Math.Abs(pixel[0] - color.B) <= 35).ToArray();
            if (matches.Length != 1) throw new InvalidOperationException("Unidentified public thumbnail center at index " + index + ": " + Convert.ToHexString(pixel));
            return matches[0].Name;
        }
        void ValidateImagePoint(Image image, Point point)
        {
            ValidatePagePoint(point);
            if (!image.IsVisible || !image.IsEnabled || !ReferenceEquals(owner.InputHitTest(owner.PointFromScreen(point)), image))
                throw new InvalidOperationException("Rounded native input pixel did not hit the exact public thumbnail image.");
        }
        void Move(Point point)
        {
            ValidatePagePoint(point);
            if (!SetCursorPos((int)point.X, (int)point.Y)) throw new System.ComponentModel.Win32Exception();
        }
        void CaptureSidebar(string label)
        {
            Point top = originalList!.PointToScreen(new Point(2, 2));
            Point bottom = originalList.PointToScreen(new Point(originalList.ActualWidth - 2, Math.Min(originalList.ActualHeight - 2, 420)));
            ValidatePagePoint(top); ValidatePagePoint(bottom);
            int width = (int)Math.Floor(bottom.X - top.X), height = (int)Math.Floor(bottom.Y - top.Y);
            if (width <= 0 || height <= 0) throw new InvalidOperationException("Empty sidebar capture.");
            nint screen = GetDC(0), memory = 0, bitmap = 0, previous = 0;
            try
            {
                memory = CreateCompatibleDC(screen); bitmap = CreateCompatibleBitmap(screen, width, height);
                previous = SelectObject(memory, bitmap);
                if (screen == 0 || memory == 0 || bitmap == 0 || previous == 0 || previous == -1
                    || !BitBlt(memory, 0, 0, width, height, screen, (int)Math.Round(top.X), (int)Math.Round(top.Y), 0x40CC0020))
                    throw new System.ComponentModel.Win32Exception();
                BitmapSource pixels = Imaging.CreateBitmapSourceFromHBitmap(bitmap, 0, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(pixels));
                string path = Path.Combine(folder, label + ".png"); using (var output = File.Create(path)) png.Save(output);
                events.Add(new { capture = label, sha256 = Hash(path), pixels.PixelWidth, pixels.PixelHeight });
            }
            finally
            {
                if (previous != 0 && previous != -1 && memory != 0) SelectObject(memory, previous);
                if (bitmap != 0) DeleteObject(bitmap); if (memory != 0) DeleteDC(memory); if (screen != 0) ReleaseDC(0, screen);
            }
        }
        void CloseOwned()
        {
            if (closed && !IsWindow(root)) return;
            var timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(25) };
            timer.Tick += (_, _) =>
            {
                string? discard = app.TryFindResource("Str_Btn_DontSave") as string;
                foreach (Window dialog in app.Windows.Cast<Window>().Where(w => w != owner && w.Owner == owner && w.IsVisible).ToArray())
                {
                    Button? button = Descendants(dialog).OfType<Button>().FirstOrDefault(b => b.IsVisible && b.IsEnabled && Equals(b.Content, discard));
                    if (discard is not null && button is not null)
                        Pattern<IInvokeProvider>(new ButtonAutomationPeer(button), PatternInterface.Invoke).Invoke();
                }
            };
            timer.Start();
            try { owner.Dispatcher.BeginInvoke(new Action(owner.Close)); Wait(() => closed && !IsWindow(root), 5000, teardown: true); }
            finally { timer.Stop(); }
        }
    }

    private sealed class MissingOptionException(string message) : Exception(message);
    private sealed class GeometryException(string message) : Exception(message);
    private sealed class ReorderException(string message) : Exception(message);
    private static Point Rounded(Point point) => new(Math.Round(point.X), Math.Round(point.Y));
    private static T Pattern<T>(AutomationPeer peer, PatternInterface pattern) where T : class => peer.GetPattern(pattern) as T
        ?? throw new InvalidOperationException("Public automation pattern absent: " + typeof(T).Name);
    private static bool Near(double a, double b) => Math.Abs(a - b) <= 1;
    private static object RectData(Rect r) => new { r.X, r.Y, r.Width, r.Height };
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    // The sidebar options sit in the Sidebar row's flyout: open it the way a user does, then find the toggle.
    private static MenuItem? PairedOption(ContextMenu menu)
    {
        var sidebar = Descendants(menu).OfType<MenuItem>().SingleOrDefault(item => item.Name == "SidebarMenuItem");
        if (sidebar is null) return null;
        sidebar.IsSubmenuOpen = true;
        menu.UpdateLayout(); Pump();
        return sidebar.Items.OfType<MenuItem>().SingleOrDefault(item => item.Name == "TwoPageThumbnailsMenuItem");
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject node)
    {
        yield return node;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(node, i))) yield return child;
    }
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    private static byte[] FivePages(int count = 5)
    {
        var objects = new List<string> { "<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Kids [{string.Join(" ", Enumerable.Range(3, count).Select(i => i + " 0 R"))}] /Count {count} >>" };
        // Landscape pages keep the first four public containers visible in the baseline sidebar.
        for (int i = 0; i < count; i++) objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 100] /Resources << >> /Contents {3 + count + i} 0 R >>");
        string[] colors = ["1 0 0", "0 1 0", "0 0 1", "1 0.5 0", "1 0 1"];
        for (int i = 0; i < count; i++)
        {
            string content = colors[i % colors.Length] + " rg 0 0 300 100 re f\n";
            objects.Add($"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}endstream");
        }
        var pdf = new StringBuilder("%PDF-1.7\n"); var offsets = new List<int>();
        foreach (string item in objects)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(pdf.ToString()));
            pdf.Append($"{offsets.Count} 0 obj\n{item}\nendobj\n");
        }
        int xref = Encoding.ASCII.GetByteCount(pdf.ToString());
        pdf.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) pdf.Append($"{offset:D10} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }
    private static void SendMouse(uint flags)
    {
        Input[] inputs = [new() { Type = 0, Data = new InputUnion { Mouse = new MouseInput { Flags = flags } } }];
        if (SendInput(1, inputs, Marshal.SizeOf<Input>()) != 1) throw new System.ComponentModel.Win32Exception();
    }
    private static void SendKey(ushort key)
    {
        Input[] down = [new() { Type = 1, Data = new InputUnion { Key = new KeyInput { Virtual = key } } }];
        Input[] up = [new() { Type = 1, Data = new InputUnion { Key = new KeyInput { Virtual = key, Flags = 2 } } }];
        try
        {
            if (SendInput(1, down, Marshal.SizeOf<Input>()) != 1) throw new System.ComponentModel.Win32Exception();
            Pump();
        }
        finally
        {
            if (SendInput(1, up, Marshal.SizeOf<Input>()) != 1) throw new System.ComponentModel.Win32Exception();
            Pump();
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public MouseInput Mouse; [FieldOffset(0)] public KeyInput Key; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyInput { public ushort Virtual, Scan; public uint Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, Input[] input, int size);
    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleBitmap(nint dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint value);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(nint target, int x, int y, int width, int height, nint source, int sx, int sy, uint flags);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
}
