using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

internal static class ToolbarAvailabilitySmoke
{
    private static readonly string[] DocumentButtons = [
        "CloseFileBtn", "SaveAsBtn", "SaveMenuBtn", "OcrBtn", "OcrMenuBtn", "FlattenBtn", "PrintBtn",
        "ComparePdfBtn", "MergeBtn", "ToolPanBtn", "ToolSelectBtn", "ToolTextBtn", "ToolHighlightBtn", "ToolUnderlineBtn",
        "ToolShapeBtn", "ToolDrawBtn", "ToolImageBtn", "ToolSignatureBtn", "ToolCropBtn", "ToolRotateBtn",
        "ToolColorCorrectionBtn", "ToolMeasureBtn", "ToolRedactBtn", "ToolStampBtn", "ToolFormFieldBtn", "SearchBtn"];

    internal static int Run(Window owner, string appPath, string folder)
    {
        Empty(owner);
        foreach (Key key in new[] { Key.P, Key.T, Key.G })
            owner.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(owner),
                Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        Pump();
        if (Equals(Button(owner, "ToolPanBtn").Tag, "selected")
            || Equals(Button(owner, "ToolTextBtn").Tag, "selected")
            || Equals(Button(owner, "ToolSignatureBtn").Tag, "selected"))
            throw new InvalidOperationException("A bare tool shortcut activated a document tool without a PDF.");

        byte[] fixture = Fixture();
        string source = Path.Combine(folder, "toolbar-three-pages.pdf");
        File.WriteAllBytes(source, fixture);
        Open(owner, source);
        Loaded(owner);
        History(owner, undo: false, redo: false, clear: false);
        var pages = (ListBox)owner.FindName("PageList");
        // Opening finishes in the background and can select the current page again, so clear the
        // selection until it stays clear for a moment.
        var unselect = Stopwatch.StartNew();
        do { pages.UnselectAll(); Pump(); Thread.Sleep(200); Pump(); }
        while (pages.SelectedItems.Count != 0 && unselect.Elapsed.TotalSeconds < 10);
        if (pages.SelectedItems.Count != 0) throw new InvalidOperationException("The page selection would not stay clear after opening.");
        Enabled(owner, false, "ExtractPagesBtn", "DeletePagesBtn", "MovePageUpBtn", "MovePageDownBtn");
        pages.SelectedIndex = 0; Pump();
        Enabled(owner, true, "ExtractPagesBtn", "DeletePagesBtn", "MovePageDownBtn");
        Enabled(owner, false, "MovePageUpBtn");
        pages.SelectAll(); Pump();
        Enabled(owner, false, "DeletePagesBtn");
        pages.UnselectAll(); pages.SelectedIndex = 2; Pump();
        Enabled(owner, true, "MovePageUpBtn");
        Enabled(owner, false, "MovePageDownBtn");
        pages.SelectedIndex = 0; Pump();

        var viewer = (FrameworkElement)owner.FindName("Viewer");
        Click(Button(owner, "ToolPanBtn"));
        if (!Equals(Button(owner, "ToolPanBtn").Tag, "selected") || Equals(Button(owner, "ToolSelectBtn").Tag, "selected"))
            throw new InvalidOperationException("ToolPanBtn did not select Pan mode.");
        Click(Button(owner, "ToolSelectBtn"));
        if (Equals(Button(owner, "ToolPanBtn").Tag, "selected") || !Equals(Button(owner, "ToolSelectBtn").Tag, "selected"))
            throw new InvalidOperationException("ToolSelectBtn did not restore Select mode.");
        Click(Button(owner, "ToolTextBtn"));
        Canvas page = Children(viewer).OfType<Canvas>().First(canvas => Equals(canvas.Tag, 0) && canvas.Width > 0);
        viewer.GetType().GetMethod("PlaceTextAtPosition", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(viewer, [new Point(page.Width * .2, page.Height * .5), 0]);
        TextBox editor = Children(viewer).OfType<TextBox>().Single(box => box.AcceptsReturn && Equals(box.Tag, 0));
        editor.Text = "TOOLBAR HISTORY"; Pump();
        History(owner, undo: true, redo: false, clear: true);
        Click(Button(owner, "ToolSelectBtn"));
        Click(Button(owner, "ClearAllBtn"));
        History(owner, undo: true, redo: false, clear: false);
        Click(Button(owner, "UndoBtn"));
        History(owner, undo: true, redo: true, clear: true);
        Click(Button(owner, "RedoBtn"));
        History(owner, undo: true, redo: false, clear: false);

        string second = Path.Combine(folder, "toolbar-clean-tab.pdf");
        File.WriteAllBytes(second, fixture);
        Open(owner, second);
        History(owner, undo: false, redo: false, clear: false);
        Close(owner);
        History(owner, undo: true, redo: false, clear: false);

        Click(Button(owner, "SplitPaneRailBtn"));
        var other = (FrameworkElement)owner.FindName("ViewerB");
        FocusPane(other); Empty(owner);
        FocusPane(viewer); Loaded(owner);
        History(owner, undo: true, redo: false, clear: false);
        Click(Button(owner, "SplitPaneRailBtn"));
        Close(owner); Empty(owner);
        if (!File.ReadAllBytes(source).AsSpan().SequenceEqual(fixture))
            throw new InvalidOperationException("Toolbar actions changed the source PDF.");
        VerifyDirectTextAvailability(owner, folder, fixture);
        VerifyReadingAfterReplacement(owner, source);
        string result = "PASS empty/open/close toolbar state, split arrows, overflow parity, page boundaries, pending text, unchanged direct-text history, Undo/Redo/Clear All, tab and pane changes. Offscreen public WPF workflow only. App SHA256="
            + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(appPath)));
        File.WriteAllText(Path.Combine(folder, "result.txt"), result);
        Console.WriteLine(result);
        return 0;
    }

    private static void VerifyDirectTextAvailability(Window owner, string folder, byte[] fixture)
    {
        string source = Path.Combine(folder, "toolbar-direct-text.pdf");
        File.WriteAllBytes(source, fixture);
        Open(owner, source);
        var viewer = (FrameworkElement)owner.FindName("Viewer");
        TextBox BeginExistingText()
        {
            Click(Button(owner, "ToolTextBtn"));
            Canvas page = Children(viewer).OfType<Canvas>().First(canvas => Equals(canvas.Tag, 0) && canvas.Width > 0);
            viewer.GetType().GetMethod("BeginTextEditAtPosition", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(viewer, [new Point(page.Width * .18, page.Height * .15), 0]);
            Pump();
            TextBox editor = Children(viewer).OfType<TextBox>().Single(box => box.AcceptsReturn && Equals(box.Tag, 0));
            if (editor.Text != "ORIGINAL BODY TEXT")
                throw new InvalidOperationException("The page-text editor did not contain the source line.");
            return editor;
        }

        Console.WriteLine("CHECK unchanged direct-text editor with empty history.");
        TextBox editor = BeginExistingText();
        History(owner, undo: false, redo: false, clear: false);
        editor.Text = "CHANGED BODY TEXT"; Pump();
        History(owner, undo: true, redo: false, clear: false);
        editor.Text = "ORIGINAL BODY TEXT"; Pump();
        History(owner, undo: false, redo: false, clear: false);
        Click(Button(owner, "ToolSelectBtn"));

        Click(Button(owner, "ToolTextBtn"));
        Canvas canvas = Children(viewer).OfType<Canvas>().First(candidate => Equals(candidate.Tag, 0) && candidate.Width > 0);
        viewer.GetType().GetMethod("PlaceTextAtPosition", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(viewer, [new Point(canvas.Width * .2, canvas.Height * .5), 0]);
        Children(viewer).OfType<TextBox>().Single(box => box.AcceptsReturn && Equals(box.Tag, 0)).Text = "REDO MARKER";
        Click(Button(owner, "ToolSelectBtn"));
        Click(Button(owner, "UndoBtn"));
        History(owner, undo: false, redo: true, clear: false);
        editor = BeginExistingText();
        History(owner, undo: false, redo: true, clear: false);
        editor.Text = "CHANGED BODY TEXT"; Pump();
        History(owner, undo: true, redo: false, clear: false);
        editor.Text = "ORIGINAL BODY TEXT"; Pump();
        History(owner, undo: false, redo: true, clear: false);
        Click(Button(owner, "ToolSelectBtn"));
        Click(Button(owner, "RedoBtn"));
        History(owner, undo: true, redo: false, clear: true);
        Close(owner); Empty(owner);
        if (!File.ReadAllBytes(source).AsSpan().SequenceEqual(fixture))
            throw new InvalidOperationException("Direct-text availability checks changed the source PDF.");
    }

    private static void VerifyReadingAfterReplacement(Window owner, string source)
    {
        Open(owner, source);
        var viewer = (FrameworkElement)owner.FindName("Viewer");
        Click(Button(owner, "ToolPanBtn"));
        if (!Equals(Button(owner, "ToolPanBtn").Tag, "selected") || Equals(Button(owner, "ToolSelectBtn").Tag, "selected"))
            throw new InvalidOperationException("ToolPanBtn did not select Pan mode.");
        Click(Button(owner, "ToolSelectBtn"));
        if (Equals(Button(owner, "ToolPanBtn").Tag, "selected") || !Equals(Button(owner, "ToolSelectBtn").Tag, "selected"))
            throw new InvalidOperationException("ToolSelectBtn did not restore Select mode.");
        Click(Button(owner, "ToolTextBtn"));
        Canvas page = Children(viewer).OfType<Canvas>().First(canvas => Equals(canvas.Tag, 0) && canvas.Width > 0);
        viewer.GetType().GetMethod("PlaceTextAtPosition", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(viewer, [new Point(page.Width * .2, page.Height * .5), 0]);
        TextBox editor = Children(viewer).OfType<TextBox>().Single(box => box.AcceptsReturn && Equals(box.Tag, 0));
        editor.Text = "CACHE BEFORE";
        Click(Button(owner, "ToolSelectBtn"));
        FrameworkElement surface = Children(viewer).OfType<FrameworkElement>()
            .Single(element => element.GetType().Name == "DocumentSurface");
        var text = (System.Windows.Automation.Provider.ITextProvider)
            System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(surface)
                .GetPattern(System.Windows.Automation.Peers.PatternInterface.Text);
        void Expect(string expected, string absent)
        {
            string actual = text.DocumentRange.GetText(-1);
            if (!actual.Contains(expected, StringComparison.Ordinal) || actual.Contains(absent, StringComparison.Ordinal))
                throw new InvalidOperationException("Reading provider expected " + expected + ", got: " + actual);
        }
        Expect("CACHE BEFORE", "CACHE AFTER");
        Click(Button(owner, "SearchBtn"));
        Click(Children(owner).OfType<Button>().Single(button => button.ToolTip is string tip && tip.StartsWith("Toggle Replace", StringComparison.Ordinal)));
        TextBox replacement = Children(owner).OfType<TextBox>().Single(box => Equals(box.ToolTip, Application.Current.FindResource("Str_Replace_WithTT")));
        Grid grid = (Grid)replacement.Parent;
        TextBox find = grid.Children.OfType<TextBox>().Single(box => box != replacement);
        find.Text = "CACHE BEFORE";
        replacement.Text = "CACHE AFTER";
        Click(Children(owner).OfType<Button>().Single(button => Equals(button.Content, Application.Current.FindResource("Str_Replace_All"))));
        Expect("CACHE AFTER", "CACHE BEFORE");
        ScrollViewer readingScroll = Children(viewer).OfType<ScrollViewer>().Single(control => control.Name == "PagePreviewPanel");
        readingScroll.ScrollToTop(); owner.UpdateLayout(); Pump();
        var body = text.DocumentRange.FindText("BODY", false, false)!;
        double[] bounds = body.GetBoundingRectangles();
        if (bounds.Length < 4) throw new InvalidOperationException("Body text has no screen rectangle.");
        var hit = text.RangeFromPoint(new Point(bounds[0] + 1, bounds[1] + bounds[3] / 2));
        hit.ExpandToEnclosingUnit(System.Windows.Automation.Text.TextUnit.Word);
        if (!hit.GetText(-1).StartsWith("BODY", StringComparison.Ordinal))
            throw new InvalidOperationException("RangeFromPoint missed BODY: " + hit.GetText(-1));
        Click(Button(owner, "UndoBtn"));
        Expect("CACHE BEFORE", "CACHE AFTER");
        Click(Button(owner, "RedoBtn"));
        Expect("CACHE AFTER", "CACHE BEFORE");
        double oldHeight = owner.Height;
        owner.Height = 350;
        ((ComboBox)owner.FindName("ZoomBox")).SelectedIndex = 7;
        owner.UpdateLayout(); Pump();
        ScrollViewer scroll = Children(viewer).OfType<ScrollViewer>().Single(control => control.Name == "PagePreviewPanel");
        scroll.ScrollToBottom(); owner.UpdateLayout(); Pump();
        if (text.GetVisibleRanges().Any(range => range.GetText(-1).Contains("ORIGINAL BODY TEXT", StringComparison.Ordinal)))
            throw new InvalidOperationException("GetVisibleRanges includes the scrolled-out body line.");
        if (body.GetBoundingRectangles().Length != 0)
            throw new InvalidOperationException("Scrolled-out text still has screen rectangles.");
        scroll.ScrollToTop(); owner.UpdateLayout(); Pump();
        // The page comes back into view once the viewer has caught up with the scroll, which on a
        // slow machine can take longer than one idle pass.
        var settle = Stopwatch.StartNew();
        while (!text.GetVisibleRanges().Any(range => range.GetText(-1).Contains("BODY", StringComparison.Ordinal)))
        {
            if (settle.Elapsed.TotalSeconds > 10)
                throw new InvalidOperationException("GetVisibleRanges omitted the visible body line. Offset "
                    + scroll.VerticalOffset.ToString(CultureInfo.InvariantCulture) + " of "
                    + scroll.ScrollableHeight.ToString(CultureInfo.InvariantCulture) + ", ranges: ["
                    + string.Join(" | ", text.GetVisibleRanges().Select(range => range.GetText(80))) + "]; " + ViewerState(viewer));
            Pump(); Thread.Sleep(10);
        }
        owner.Height = oldHeight;
        Close(owner);
        Console.WriteLine("PASS reading provider refresh after same-count replacement, Undo and Redo.");
    }

    private static void Empty(Window owner)
    {
        Pump();
        foreach (string name in new[] { "ViewContinuousRadio", "ViewSingleRadio", "ViewTwoPageRadio", "ViewGridRadio",
                     "DocInvertBtn", "ZoomOutBtn", "ZoomBox", "ZoomInBtn" })
            if (((UIElement)owner.FindName(name)).IsEnabled)
                throw new InvalidOperationException(name + " remains active without a PDF.");
        bool? singleBefore = ((RadioButton)owner.FindName("ViewSingleRadio")).IsChecked;
        owner.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(owner),
            Environment.TickCount, Key.F6) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        Pump();
        if (((RadioButton)owner.FindName("ViewSingleRadio")).IsChecked != singleBefore)
            throw new InvalidOperationException("F6 changes the view without a PDF.");
        // The Save menu waits for a PDF too: Document tools (Macros, certificates) have their own sidebar panel.
        Enabled(owner, false, DocumentButtons);
        Enabled(owner, false, "UndoBtn", "RedoBtn", "ClearAllBtn", "ExtractPagesBtn", "DeletePagesBtn", "MovePageUpBtn", "MovePageDownBtn");
        Enabled(owner, true, "NewBlankBtn", "OpenFileBtn", "OpenRecentBtn", "SidebarDocumentToolsTab");
        if (Button(owner, "SaveAsBtn").Opacity >= 1 || Button(owner, "MiText").Opacity >= 1)
            throw new InvalidOperationException("Disabled toolbar or overflow action was not dimmed.");
        Overflow(owner);
    }

    private static void Loaded(Window owner)
    {
        Pump(); Enabled(owner, true, DocumentButtons); Overflow(owner);
        foreach (string name in new[] { "ViewContinuousRadio", "ViewSingleRadio", "ViewTwoPageRadio", "ViewGridRadio",
                     "DocInvertBtn", "ZoomOutBtn", "ZoomBox", "ZoomInBtn" })
            if (!((UIElement)owner.FindName(name)).IsEnabled)
                throw new InvalidOperationException(name + " remains inactive with a PDF.");
    }
    private static void History(Window owner, bool undo, bool redo, bool clear)
    {
        Pump(); Enabled(owner, undo, "UndoBtn"); Enabled(owner, redo, "RedoBtn"); Enabled(owner, clear, "ClearAllBtn"); Overflow(owner);
    }

    private static void Overflow(Window owner)
    {
        foreach ((string main, string more) in new[] {
            ("ToolTextBtn", "MiText"), ("ToolSignatureBtn", "MiSignature"), ("MergeBtn", "MiMerge"),
            ("UndoBtn", "MiUndo"), ("RedoBtn", "MiRedo"), ("ClearAllBtn", "MiClear"),
            ("DeletePagesBtn", "MiDelete"), ("MovePageUpBtn", "MiMoveUp"), ("MovePageDownBtn", "MiMoveDown") })
            if (Button(owner, main).IsEnabled != Button(owner, more).IsEnabled)
                throw new InvalidOperationException(more + " does not match its toolbar action.");
    }

    private static void Enabled(Window owner, bool expected, params string[] names)
    {
        foreach (string name in names)
            if (Button(owner, name).IsEnabled != expected)
                throw new InvalidOperationException(name + ": expected IsEnabled=" + expected);
    }

    private static Button Button(Window owner, string name) => (Button)owner.FindName(name);
    private static void Click(Button button) { button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); Pump(); }
    private static void FocusPane(FrameworkElement viewer)
    {
        viewer.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            { RoutedEvent = Mouse.PreviewMouseDownEvent });
        Pump();
    }
    private static void Open(Window owner, string path)
    {
        owner.GetType().GetMethod("OpenFromExternal", BindingFlags.Public | BindingFlags.Instance)!.Invoke(owner, [path]);
        Pump();
    }
    private static void Close(Window owner)
    {
        var elapsed = Stopwatch.StartNew();
        Exception? failure = null;
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(25) };
        timer.Tick += (_, _) =>
        {
            foreach (Window dialog in Application.Current.Windows.Cast<Window>().Where(window => window != owner && window.IsVisible).ToArray())
            {
                string discard = (string)Application.Current.FindResource("Str_Btn_DontSave");
                Button? button = Children(dialog).OfType<Button>().FirstOrDefault(candidate => Equals(candidate.Content, discard));
                if (button is not null) Click(button);
                else { failure = new InvalidOperationException("Unexpected close prompt."); dialog.Close(); }
            }
            if (elapsed.Elapsed.TotalSeconds > 20)
            {
                failure = new TimeoutException("Close did not complete.");
                foreach (Window dialog in Application.Current.Windows.Cast<Window>().Where(window => window != owner).ToArray()) dialog.Close();
            }
        };
        timer.Start();
        try { Click(Button(owner, "CloseFileBtn")); if (failure is not null) throw failure; }
        finally { timer.Stop(); }
    }
    // The pages the viewer holds and where they sit, for a reading-provider failure message.
    private static string ViewerState(FrameworkElement viewer)
    {
        const BindingFlags Fields = BindingFlags.NonPublic | BindingFlags.Instance;
        Type type = viewer.GetType();
        var dims = (System.Collections.IDictionary)type.GetField("_renderDims", Fields)!.GetValue(viewer)!;
        var pages = (System.Collections.IDictionary)type.GetProperty("_pages", Fields)!.GetValue(viewer)!;
        var parts = new List<string> { "renderDims=[" + string.Join(",", dims.Keys.Cast<object>()) + "]" };
        foreach (System.Collections.DictionaryEntry entry in pages)
        {
            var canvas = (FrameworkElement)entry.Value!;
            bool shown = PresentationSource.FromVisual(canvas) is not null;
            parts.Add($"page {entry.Key} visible={canvas.IsVisible} inTree={shown} size={canvas.ActualWidth}x{canvas.ActualHeight}"
                + (shown ? " at " + canvas.PointToScreen(new Point()) : ""));
        }
        // The scroll area the reading provider clips page 1 to: the nearest one above its canvas.
        if (pages.Contains(0) && pages[0] is DependencyObject first)
            for (DependencyObject? node = first; node is not null; node = VisualTreeHelper.GetParent(node))
                if (node is ScrollContentPresenter clip)
                {
                    string owner = (clip.TemplatedParent as FrameworkElement)?.Name ?? "?";
                    parts.Add($"page 0 clip ({owner}) at " + clip.PointToScreen(new Point()) + " size " + clip.ActualWidth + "x" + clip.ActualHeight);
                    break;
                }
        ScrollViewer preview = Children(viewer).OfType<ScrollViewer>().Single(control => control.Name == "PagePreviewPanel");
        parts.Add($"PagePreviewPanel offset {preview.HorizontalOffset},{preview.VerticalOffset} viewport {preview.ViewportWidth}x{preview.ViewportHeight}"
            + $" extent {preview.ExtentWidth}x{preview.ExtentHeight} at " + preview.PointToScreen(new Point()));
        Window window = Window.GetWindow(viewer);
        parts.Add($"window {window.Left},{window.Top} {window.ActualWidth}x{window.ActualHeight}");
        if (dims.Contains(0)) parts.Add("renderDims[0]=" + dims[0]);
        parts.Add("rotation=" + type.GetMethod("EffectiveRotation", Fields)?.Invoke(viewer, [0]));
        // The text geometry the provider reads for page 1, and the visible character runs it finds.
        object textRuns = type.GetField("_textRuns", Fields)!.GetValue(viewer)!;
        object? runs = textRuns.GetType().GetMethod("GetPage", BindingFlags.Public | BindingFlags.Instance)!
            .Invoke(textRuns, [type.GetField("_currentFile", Fields)!.GetValue(viewer)!, 0]);
        if (runs is null) parts.Add("runs[0]=null");
        else
        {
            Type runsType = runs.GetType();
            var chars = (System.Collections.IList)runsType.GetField("Chars")!.GetValue(runs)!;
            var lines = (System.Collections.IList)runsType.GetField("Lines")!.GetValue(runs)!;
            string Field(object item, string name) => Convert.ToString(item.GetType().GetField(name)!.GetValue(item), CultureInfo.InvariantCulture)!;
            parts.Add($"runs[0] pdf {Field(runs, "PdfWidth")}x{Field(runs, "PdfHeight")} chars={chars.Count} lines={lines.Count}"
                + (chars.Count > 0 ? $" first '{Field(chars[0]!, "Value")}' x {Field(chars[0]!, "Left")}-{Field(chars[0]!, "Right")} line {Field(chars[0]!, "Line")}" : "")
                + (lines.Count > 0 ? $" line0 y {Field(lines[0]!, "Bottom")}-{Field(lines[0]!, "Top")}" : ""));
        }
        FrameworkElement surface = Children(viewer).OfType<FrameworkElement>().Single(element => element.GetType().Name == "DocumentSurface");
        object? source = surface.GetType().GetProperty("Source", Fields)!.GetValue(surface);
        if (source is not null)
            parts.Add("visibleCarets=[" + string.Join(",", ((System.Collections.IEnumerable)source.GetType()
                .GetMethod("VisibleCaretRanges", BindingFlags.Public | BindingFlags.Instance)!.Invoke(source, null)!).Cast<object>()) + "]");
        parts.Add("file=" + type.GetField("_currentFile", Fields)!.GetValue(viewer));
        return string.Join("; ", parts);
    }

    private static void Pump() => Application.Current.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static IEnumerable<DependencyObject> Children(DependencyObject root)
    {
        yield return root;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (DependencyObject child in Children(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
    private static byte[] Fixture()
    {
        const string content = "BT /F1 12 Tf 30 250 Td (ORIGINAL BODY TEXT) Tj ET\n";
        string[] objects = ["<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R 4 0 R 5 0 R] /Count 3 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 300] /Resources << /Font << /F1 6 0 R >> >> /Contents 7 0 R >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 300] /Resources << >> >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 300] /Resources << >> >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            $"<< /Length {content.Length} >>\nstream\n{content}endstream"];
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int> { 0 };
        for (int index = 0; index < objects.Length; index++)
        {
            offsets.Add(pdf.Length);
            pdf.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        int xref = pdf.Length;
        pdf.Append($"xref\n0 {offsets.Count}\n0000000000 65535 f \n");
        foreach (int offset in offsets.Skip(1)) pdf.Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        pdf.Append($"trailer\n<< /Size {offsets.Count} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
