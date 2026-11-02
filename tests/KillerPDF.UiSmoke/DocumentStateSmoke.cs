using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

internal static class DocumentStateSmoke
{
    private const string Marker = "Z";

    internal static int Run(Window owner, Assembly assembly, string appPath, string folder, string scenario)
    {
        if (scenario == "--verify-stamp-isolation") return VerifyStampIsolation(owner, appPath, folder);
        if (scenario == "--verify-stamp-history") return VerifyStampIsolation(owner, appPath, folder, history: true);
        if (scenario == "--verify-stamp-tabs") return VerifyStampIsolation(owner, appPath, folder, tabs: true);
        if (scenario == "--verify-stamp-panes") return VerifyStampIsolation(owner, appPath, folder, panes: true);
        if (scenario == "--verify-stamp-tearout") return VerifyStampIsolation(owner, appPath, folder, tearOut: true);
        if (scenario == "--verify-stamp-saveall") return VerifyStampIsolation(owner, appPath, folder, saveAll: true);
        string source = Path.Combine(folder, "state-source.pdf");
        bool closeInOtherPane = scenario == "--verify-active-editor-close-b";
        string otherSource = Path.Combine(folder, "other-pane.pdf");
        byte[] original = scenario is "--verify-form-history" or "--verify-merge-pending"
            ? FormFieldEditingSmoke.Fixture("ORIGINAL") : scenario is "--verify-page-delete-failure" or "--verify-page-reorder-failure"
                ? PageDeletionSmoke.Fixture() : TableExportSmoke.Fixture();
        if (scenario is "--verify-page-transform-failure" or "--verify-page-transform-cancel" or "--verify-page-transform-save-control")
            original = TableExportSmoke.Fixture(malformedDate: true);
        if (scenario == "--verify-page-transform-form-failure")
            original = FormFieldEditingSmoke.Fixture("ORIGINAL", malformedDate: true);
        if (scenario.StartsWith("--verify-page-straighten-", StringComparison.Ordinal))
            original = TiltedScanFixture(healthy: scenario == "--verify-page-straighten-success");
        File.WriteAllBytes(source, original);
        if (closeInOtherPane) File.WriteAllBytes(otherSource, original);
        owner.GetType().GetMethod("OpenFromExternal", BindingFlags.Public | BindingFlags.Instance)!
            .Invoke(owner, [closeInOtherPane ? otherSource : source]);
        owner.WindowState = WindowState.Normal;
        owner.Left = 32; owner.Top = 32; owner.Width = 950; owner.Height = 700;
        owner.Opacity = 1; owner.ShowActivated = true;
        owner.Activate();
        Wait(() => owner.IsActive && Descendants<Canvas>(owner).Any(IsPageCanvas), "active rendered page");
        DependencyObject editingScope = owner;
        if (closeInOtherPane)
        {
            assembly.GetType("KillerPDF.App", true)!.GetMethod("SetSetting", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, ["RememberChoiceLocked", "0"]);
            Click(owner, "SplitPaneRailBtn");
            var other = (FrameworkElement)owner.FindName("ViewerB");
            Wait(() => other.IsVisible && other.ActualWidth >= 320, "active editor second pane");
            other.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = Mouse.PreviewMouseDownEvent });
            owner.GetType().GetMethod("OpenFromExternal", BindingFlags.Public | BindingFlags.Instance)!.Invoke(owner, [source]);
            Wait(() => ((TextBlock)owner.FindName("FileNameLabel")).Text == Path.GetFileName(source)
                && Descendants<Canvas>(other).Any(IsPageCanvas), "second-pane editor source");
            editingScope = other;
            Console.WriteLine("CONTROL active editor uses pane B with the combined quit preference unlocked.");
        }
        if (Dirty(owner)) throw new InvalidOperationException("The starting fixture must be clean.");

        Click(owner, "ToolTextBtn");
        // S1: the real click lands wherever the foreground window is, and the page settles its size
        // after the open's fit. The window takes the foreground and keyboard focus again and the page
        // holds still before the click, and one click that made no editor is tried once more.
        Canvas canvas = SettledPageCanvas(editingScope);
        ClickPage(owner, canvas);
        if (!WaitBriefly(() => Keyboard.FocusedElement is TextBox { AcceptsReturn: true }, TimeSpan.FromSeconds(3))
            && !Descendants<TextBox>(editingScope).Any(box => box.AcceptsReturn))
        {
            Console.WriteLine("CONTROL first page click made no editor; foreground and click retried once.");
            ClickPage(owner, SettledPageCanvas(editingScope));
        }
        Wait(() => Keyboard.FocusedElement is TextBox { AcceptsReturn: true },
            () => "active text editor (window active " + owner.IsActive + ", keyboard focus within " + owner.IsKeyboardFocusWithin
                + ", editor made " + Descendants<TextBox>(editingScope).Any(box => box.AcceptsReturn)
                + ", focused " + (Keyboard.FocusedElement?.GetType().Name ?? "nothing") + ")");
        var editor = (TextBox)Keyboard.FocusedElement;
        editor.Text = Marker;
        editor.CaretIndex = editor.Text.Length;
        Console.WriteLine("CONTROL real Text tool created a focused editable text box on a clean PDF.");

        try
        {
            if (scenario is "--verify-active-editor-close" or "--verify-active-editor-close-b")
                VerifyClose(owner, source, original, editor);
            else if (scenario == "--verify-annotation-history")
                VerifyAnnotationHistory(owner, source, editor, folder);
            else if (scenario == "--verify-form-history")
                VerifyFormHistory(owner, source, editor);
            else if (scenario == "--verify-failed-save-history")
                VerifyFailedSaveHistory(owner, source, original, editor);
            else if (scenario == "--verify-page-edit-failure")
                VerifyPageEditFailure(owner, source, original, editor, folder);
            else if (scenario == "--verify-page-delete-failure")
                VerifyPageEditFailure(owner, source, original, editor, folder, delete: true);
            else if (scenario == "--verify-page-reorder-failure")
                VerifyPageEditFailure(owner, source, original, editor, folder, reorder: true);
            else if (scenario == "--verify-page-transform-failure")
                VerifyTransformFailure(owner, source, original, editor);
            else if (scenario == "--verify-page-transform-form-failure")
                VerifyTransformFailure(owner, source, original, editor, forms: true);
            else if (scenario is "--verify-file-insertion-failure" or "--verify-file-insertion-success")
                VerifyFileInsertion(owner, source, original, editor, folder, success: scenario == "--verify-file-insertion-success");
            else if (scenario == "--verify-page-straighten-failure")
                VerifyTransformFailure(owner, source, original, editor, straighten: true);
            else if (scenario == "--verify-page-straighten-save-control")
                VerifyTransformFailure(owner, source, original, editor, straighten: true, saveOnly: true);
            else if (scenario == "--verify-page-straighten-success")
                VerifyTransformFailure(owner, source, original, editor, straighten: true, success: true);
            else if (scenario == "--verify-page-transform-cancel")
                VerifyTransformFailure(owner, source, original, editor, cancel: true);
            else if (scenario == "--verify-page-transform-save-control")
                VerifyTransformFailure(owner, source, original, editor, saveOnly: true);
            else if (scenario == "--verify-page-transform-success")
                VerifyTransformFailure(owner, source, original, editor, success: true);
            else if (scenario == "--verify-range-extract-pending")
                VerifyPendingRangeExtraction(owner, source, original, folder);
            else if (scenario == "--verify-layout-pending")
                VerifyLayoutPending(owner, source, original, editor, folder);
            else if (scenario == "--verify-merge-pending")
                VerifyLayoutPending(owner, source, original, editor, folder, merge: true);
            else if (scenario == "--verify-layouts-pending")
                foreach (string key in new[] { "Str_Production_NUp", "Str_Production_Booklet", "Str_Production_Repeat",
                    "Str_Production_CutStack", "Str_Production_Manual", "Str_Production_Poster" })
                    VerifyLayoutPending(owner, source, original, editor, folder, layoutKey: key);
            else
                VerifyHistory(owner, source, editor);

            if (closeInOtherPane && !File.ReadAllBytes(otherSource).AsSpan().SequenceEqual(original))
                throw new InvalidOperationException("Closing pane B changed the other pane's source.");
            string result = "PASS " + scenario.TrimStart('-') + ": public text/save/undo/close commands. App SHA256="
                + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(appPath)));
            File.WriteAllText(Path.Combine(folder, "result.txt"), result);
            Console.WriteLine(result);
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("Document-state stage failed: " + error);
            throw;
        }
        finally
        {
            var cleanup = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(20) };
            cleanup.Tick += (_, _) =>
            {
                foreach (Window dialog in Application.Current.Windows.Cast<Window>()
                    .Where(window => window != owner && window.IsVisible).ToArray())
                {
                    Button? discard = Descendants<Button>(dialog).FirstOrDefault(button =>
                        Equals(button.Content, Application.Current.FindResource("Str_Btn_DontSave"))
                        || Equals(button.Content, Application.Current.FindResource("Str_Btn_Quit")));
                    if (discard is not null) discard.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    else dialog.Close();
                }
            };
            cleanup.Start();
            try
            {
                if (owner.IsVisible) owner.Close();
                Wait(() => !owner.IsVisible, "owned window cleanup");
            }
            finally { cleanup.Stop(); }
        }
    }

    private static int VerifyStampIsolation(Window owner, string appPath, string folder, bool history = false, bool tabs = false, bool panes = false, bool tearOut = false, bool saveAll = false)
    {
        owner.WindowState = WindowState.Normal;
        owner.Left = 32; owner.Top = 32; owner.Width = 950; owner.Height = 700;
        owner.Opacity = 1; owner.ShowActivated = true;
        owner.Activate();
        string first = Path.Combine(folder, "stamp-first.pdf"), second = Path.Combine(folder, "stamp-second.pdf");
        File.WriteAllBytes(first, TableExportSmoke.Fixture());
        File.WriteAllBytes(second, TableExportSmoke.Fixture());
        void Open(string path)
        {
            owner.GetType().GetMethod("OpenFromExternal", BindingFlags.Public | BindingFlags.Instance)!.Invoke(owner, [path]);
            Wait(() => Descendants<TextBlock>(owner).Any(block => block.Name == "FileNameLabel"
                && block.Text == Path.GetFileName(path)), "stamp source open");
            Wait(() => ((Button)owner.FindName("ToolStampBtn")).IsEnabled
                && Descendants<Canvas>(owner).Any(IsPageCanvas), "rendered stamp-tool readiness");
        }
        Exception? failure = null;
        Window? detached = null;
        string expectedFormat = "{n}", desiredFormat = "STAMP_A";
        var handled = new HashSet<Window>();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(25) };
        timer.Tick += (_, _) =>
        {
            foreach (Window dialog in Application.Current.Windows.Cast<Window>()
                .Where(window => window != owner && window.IsVisible && window.GetType().Name == "StampWindow").ToArray())
            {
                if (!handled.Add(dialog)) continue;
                try
                {
                    Descendants<TextBox>(dialog).Single(box => box.Text == expectedFormat).Text = desiredFormat;
                    Button apply = Descendants<Button>(dialog).Single(button =>
                        System.Windows.Automation.AutomationProperties.GetAutomationId(button) == "StampApply");
                    if (!apply.IsEnabled) throw new InvalidOperationException("Stamp Apply is disabled.");
                    apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                catch (Exception error) { failure = error; dialog.Close(); }
            }
        };
        try
        {
            Open(first);
            timer.Start();
            try { Click(owner, "ToolStampBtn"); }
            finally { timer.Stop(); }
            if (failure is not null) throw failure;
            if (handled.Count != 1 || !Dirty(owner)) throw new InvalidOperationException("Stamp tool did not apply the first document's page-number format.");
            Save(owner);
            if (!ReadText(first).Contains("STAMP_A", StringComparison.Ordinal))
                throw new InvalidOperationException("The first document did not save its own stamp.");
            Console.WriteLine("CONTROL first document strictly reopened with its applied STAMP_A page number.");
            if (saveAll)
            {
                expectedFormat = "STAMP_A"; desiredFormat = "STAMP_PENDING";
                timer.Start();
                try { Click(owner, "ToolStampBtn"); }
                finally { timer.Stop(); }
                if (failure is not null) throw failure;
                if (!Dirty(owner)) throw new InvalidOperationException("The first pending stamp did not become dirty.");
            }
            if (tearOut)
            {
                byte[] savedFirst = File.ReadAllBytes(first);
                expectedFormat = "STAMP_A";
                desiredFormat = "STAMP_PENDING";
                timer.Start();
                try { Click(owner, "ToolStampBtn"); }
                finally { timer.Stop(); }
                if (failure is not null) throw failure;
                if (!Dirty(owner)) throw new InvalidOperationException("Pending stamp edit did not become dirty.");
                Open(second);
                Wait(() => Descendants<TextBlock>(owner).Any(block => block.Name == "tabLbl"
                    && block.Text.TrimStart('•', ' ') == Path.GetFileNameWithoutExtension(first)), "stamped tab label");
                TextBlock label = Descendants<TextBlock>(owner).Single(block => block.Name == "tabLbl"
                    && block.Text.TrimStart('•', ' ') == Path.GetFileNameWithoutExtension(first));
                DependencyObject tab = label;
                while (tab is not FrameworkElement { Name: "tabBd" })
                    tab = VisualTreeHelper.GetParent(tab) ?? throw new InvalidOperationException("The stamped tab frame is missing.");
                ((UIElement)tab).RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right)
                    { RoutedEvent = UIElement.MouseRightButtonUpEvent });
                Pump();
                DependencyObject[] roots = PresentationSource.CurrentSources.Cast<PresentationSource>()
                    .Select(source => source.RootVisual).OfType<DependencyObject>().ToArray();
                ContextMenu menu = roots.OfType<ContextMenu>().Concat(roots.SelectMany(Descendants<ContextMenu>))
                    .Distinct().Single(candidate => candidate.IsOpen);
                MenuItem move = menu.Items.OfType<MenuItem>().Single(item => item.InputGestureText.Contains("Ctrl", StringComparison.Ordinal)
                    && item.InputGestureText.Contains("Shift", StringComparison.Ordinal) && item.InputGestureText.EndsWith("N", StringComparison.Ordinal));
                if (!move.IsEnabled) throw new InvalidOperationException("Move to new window is disabled for the stamped document.");
                move.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                menu.IsOpen = false;
                Wait(() => Application.Current.Windows.Cast<Window>().Any(window => window != owner
                    && window.GetType() == owner.GetType() && window.IsVisible), "detached stamped document");
                detached = Application.Current.Windows.Cast<Window>().Single(window => window != owner
                    && window.GetType() == owner.GetType() && window.IsVisible);
                detached.Left = 64; detached.Top = 64; detached.Width = 950; detached.Height = 700;
                detached.Opacity = 1; detached.ShowActivated = true; detached.Activate();
                Wait(() => ((Button)detached.FindName("ToolStampBtn")).IsEnabled
                    && Descendants<Canvas>(detached).Any(IsPageCanvas), "detached stamp tool readiness");
                if (!File.ReadAllBytes(first).AsSpan().SequenceEqual(savedFirst) || !Dirty(detached))
                    throw new InvalidOperationException("Tear-out wrote the source or lost the pending stamp's dirty state.");
                expectedFormat = desiredFormat = "STAMP_PENDING";
                timer.Start();
                try { Click(detached, "ToolStampBtn"); }
                finally { timer.Stop(); }
                if (failure is not null) throw failure;
                Save(detached);
                string transferredText = ReadText(first);
                if (!transferredText.Contains("STAMP_PENDING", StringComparison.Ordinal)
                    || transferredText.Contains("STAMP_A", StringComparison.Ordinal)
                    || !File.ReadAllBytes(second).AsSpan().SequenceEqual(TableExportSmoke.Fixture()))
                    throw new InvalidOperationException("Detached Save lost its pending stamp or changed the other document.");
                const string transferResult = "PASS verify-stamp-tearout: pending stamp specification and dirty state survive Move to new window and strict Save/reopen.";
                File.WriteAllText(Path.Combine(folder, "result.txt"), transferResult);
                Console.WriteLine(transferResult);
                return 0;
            }
            if (history)
            {
                if (!((Button)owner.FindName("UndoBtn")).IsEnabled)
                    throw new InvalidOperationException("APP-003: applying a stamp produced no usable Undo action.");
                Click(owner, "UndoBtn");
                if (!Dirty(owner) || Descendants<TextBlock>(owner).Any(block => block.IsVisible && block.Text == "STAMP_A"))
                    throw new InvalidOperationException("Stamp Undo did not remove the stamp and mark the saved document dirty.");
                Click(owner, "RedoBtn");
                if (Dirty(owner) || !Descendants<TextBlock>(owner).Any(block => block.IsVisible && block.Text == "STAMP_A"))
                    throw new InvalidOperationException("Stamp Redo did not return to the saved checkpoint and visible stamp.");
                Click(owner, "UndoBtn");
                Save(owner);
                if (ReadText(first).Contains("STAMP_A", StringComparison.Ordinal))
                    throw new InvalidOperationException("Saving Stamp Undo retained the removed stamp.");
                Click(owner, "RedoBtn");
                Save(owner);
                if (!ReadText(first).Contains("STAMP_A", StringComparison.Ordinal))
                    throw new InvalidOperationException("Saving Stamp Redo lost the restored stamp.");
                string historyResult = "PASS verify-stamp-history: stamp Undo/Redo, dirty checkpoints and strictly reopened output.";
                File.WriteAllText(Path.Combine(folder, "result.txt"), historyResult);
                Console.WriteLine(historyResult);
                return 0;
            }
            if (panes)
            {
                Click(owner, "SplitPaneRailBtn");
                var other = (FrameworkElement)owner.FindName("ViewerB");
                Wait(() => other.IsVisible && other.ActualWidth >= 320, "second stamp pane");
                other.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                    { RoutedEvent = Mouse.PreviewMouseDownEvent });
                Pump();
            }
            Open(second);
            Save(owner);
            if (ReadText(second).Contains("STAMP_A", StringComparison.Ordinal))
                throw new InvalidOperationException("APP-003: saving the second document published the first document's stamp.");
            if (tabs || panes || saveAll)
            {
                expectedFormat = "{n}";
                desiredFormat = "STAMP_B";
                timer.Start();
                try { Click(owner, "ToolStampBtn"); }
                finally { timer.Stop(); }
                if (failure is not null) throw failure;
                if (saveAll)
                {
                    SaveAllOnClose(owner);
                    string firstSaved = ReadText(first), secondSaved = ReadText(second);
                    if (!firstSaved.Contains("STAMP_PENDING", StringComparison.Ordinal) || firstSaved.Contains("STAMP_B", StringComparison.Ordinal)
                        || firstSaved.Contains("STAMP_A", StringComparison.Ordinal) || !secondSaved.Contains("STAMP_B", StringComparison.Ordinal)
                        || secondSaved.Contains("STAMP_PENDING", StringComparison.Ordinal) || secondSaved.Contains("STAMP_A", StringComparison.Ordinal))
                        throw new InvalidOperationException("Save All during close mixed or lost document-owned pending stamps.");
                    const string saveAllResult = "PASS verify-stamp-saveall: Save during close writes each document's own pending stamp and closes successfully.";
                    File.WriteAllText(Path.Combine(folder, "result.txt"), saveAllResult);
                    Console.WriteLine(saveAllResult);
                    return 0;
                }
                Save(owner);
                string secondText = ReadText(second);
                if (!secondText.Contains("STAMP_B", StringComparison.Ordinal) || secondText.Contains("STAMP_A", StringComparison.Ordinal))
                    throw new InvalidOperationException("The second tab did not save only its own STAMP_B.");
                if (panes)
                {
                    var firstViewer = (FrameworkElement)owner.FindName("Viewer");
                    var secondViewer = (FrameworkElement)owner.FindName("ViewerB");
                    TextBlock previous = Descendants<TextBlock>(secondViewer).Single(block => block.IsVisible && block.Text == "STAMP_B");
                    CaptureOwnedWindow(owner, Path.Combine(folder, "panes-before-view-change.png"));
                    var single = (RadioButton)owner.FindName("ViewSingleRadio");
                    ((RadioButton)owner.FindName(single.IsChecked == true ? "ViewContinuousRadio" : "ViewSingleRadio")).IsChecked = true;
                    firstViewer.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                        { RoutedEvent = Mouse.PreviewMouseDownEvent });
                    try
                    {
                        Wait(() => Descendants<TextBlock>(secondViewer).Any(block => block.IsVisible && block.Text == "STAMP_B"
                            && !ReferenceEquals(previous, block)), "fresh unfocused-pane stamp rendering");
                    }
                    finally { CaptureOwnedWindow(owner, Path.Combine(folder, "panes-after-view-change.png")); }
                    if (Descendants<TextBlock>(secondViewer).Any(block => block.IsVisible && block.Text == "STAMP_A")
                        || !Descendants<TextBlock>(firstViewer).Any(block => block.IsVisible && block.Text == "STAMP_A")
                        || Descendants<TextBlock>(firstViewer).Any(block => block.IsVisible && block.Text == "STAMP_B"))
                        throw new InvalidOperationException("An unfocused pane rendered another document's stamp.");
                    if (((TextBlock)owner.FindName("FileNameLabel")).Text != Path.GetFileName(first))
                        throw new InvalidOperationException("Unfocused rendering changed the focused document label.");
                    Console.WriteLine("CONTROL fresh unfocused-pane render retained only its own stamp while the first pane stayed focused.");
                }
                if (!panes && !Descendants<TextBlock>(owner).Any(block => block.Name == "tabLbl"
                    && block.Text == Path.GetFileNameWithoutExtension(first)))
                    throw new InvalidOperationException("The first document's existing tab is unavailable.");
                Open(first);
                expectedFormat = desiredFormat = "STAMP_A";
                timer.Start();
                try { Click(owner, "ToolStampBtn"); }
                finally { timer.Stop(); }
                if (failure is not null) throw failure;
                Save(owner);
                string firstText = ReadText(first);
                if (!firstText.Contains("STAMP_A", StringComparison.Ordinal) || firstText.Contains("STAMP_B", StringComparison.Ordinal))
                    throw new InvalidOperationException("Returning to the first tab lost or replaced its stamp specification.");
                string tabsResult = "PASS " + (panes ? "verify-stamp-panes" : "verify-stamp-tabs")
                    + ": distinct specifications survive document switching and strict Save/reopen.";
                File.WriteAllText(Path.Combine(folder, "result.txt"), tabsResult);
                Console.WriteLine(tabsResult);
                return 0;
            }
            string result = "PASS verify-stamp-isolation: each public Save uses only its document's stamp. App SHA256="
                + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(appPath)));
            File.WriteAllText(Path.Combine(folder, "result.txt"), result);
            Console.WriteLine(result);
            return 0;
        }
        finally
        {
            timer.Stop();
            var cleanup = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(20) };
            cleanup.Tick += (_, _) =>
            {
                foreach (Window dialog in Application.Current.Windows.Cast<Window>()
                    .Where(window => window != owner && window != detached && window.IsVisible).ToArray())
                {
                    Button? discard = Descendants<Button>(dialog).FirstOrDefault(button =>
                        Equals(button.Content, Application.Current.FindResource("Str_Btn_DontSave")));
                    if (discard is not null) discard.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    else dialog.Close();
                }
            };
            cleanup.Start();
            try
            {
                if (detached?.IsVisible == true)
                {
                    detached.Close();
                    Wait(() => !detached.IsVisible, "detached window cleanup");
                }
                owner.Close();
                Wait(() => !owner.IsVisible, "stamp window cleanup");
            }
            finally { cleanup.Stop(); }
        }
    }

    private static void SaveAllOnClose(Window owner)
    {
        int savePrompts = 0;
        Exception? failure = null;
        var handled = new HashSet<Window>();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) =>
        {
            foreach (Window dialog in Application.Current.Windows.Cast<Window>().Where(window => window != owner && window.IsVisible).ToArray())
            {
                if (!handled.Add(dialog)) continue;
                Button? save = Descendants<Button>(dialog).FirstOrDefault(button => Equals(button.Content, owner.FindResource("Str_Btn_Save")));
                Button? yes = Descendants<Button>(dialog).FirstOrDefault(button => Equals(button.Content, owner.FindResource("Str_Btn_Yes")));
                if (save is not null) savePrompts++;
                Button? action = save ?? yes;
                if (action is null)
                {
                    failure = new InvalidOperationException("Unexpected dialog during Save All on close.");
                    dialog.Close();
                    continue;
                }
                _ = Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Input,
                    (Action)(() => action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent))));
            }
        };
        timer.Start();
        try
        {
            owner.Close();
            Wait(() => !owner.IsVisible || failure is not null, "Save All close completion");
            if (failure is not null) throw failure;
            if (savePrompts != 1) throw new InvalidOperationException("Save All close did not ask once for the pending documents.");
        }
        finally { timer.Stop(); }
    }

    private static void VerifyLayoutPending(Window owner, string source, byte[] original, TextBox editor, string folder, bool merge = false, string? layoutKey = null)
    {
        if (editor.IsVisible) Stroke(0x0D, 0x11);
        Wait(() => !editor.IsVisible && Dirty(owner), "pending layout annotation");
        if (!Descendants<TextBlock>(owner).Any(text => text.IsVisible && text.Text == Marker))
            throw new InvalidOperationException("The pending layout annotation is not visible.");
        string output = Path.Combine(folder, merge ? "pending-merge.pdf" : (layoutKey ?? "pending-layout") + ".pdf");
        string data = Path.Combine(folder, "pending-records.csv");
        if (merge) File.WriteAllText(data, "existing.text\r\nMERGED_RECORD\r\n");
        bool reportShown = false;
        Exception? failure = null;
        var handled = new HashSet<Window>();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(25) };
        timer.Tick += (_, _) =>
        {
            foreach (Window dialog in Application.Current.Windows.Cast<Window>().Where(window => window != owner && window.IsVisible).ToArray())
            {
                if (!handled.Add(dialog)) continue;
                try
                {
                    string type = dialog.GetType().Name;
                    if (type == "DocumentCommandDialog")
                        Descendants<Button>(dialog).Single(button => Equals(button.Content, owner.FindResource(layoutKey ?? "Str_Production_NUp")))
                            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    else if (type is "ProductionLayoutDialog" or "ProductionPreviewDialog" or "DataMergeDialog" or "ImportReviewDialog")
                    {
                        if (type == "ProductionLayoutDialog" && layoutKey == "Str_Production_Manual")
                            Descendants<TextBox>(dialog).Single(box => System.Windows.Automation.AutomationProperties.GetName(box)
                                == (string)owner.FindResource("Str_Production_Sequence")).Text = "1";
                        Descendants<Button>(dialog).Single(button => button.IsDefault).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }
                    else if (type == "FileDialog")
                    {
                        ((TextBox)dialog.FindName("FileNameBox")).Text = merge && dialog.Title.Contains(
                            (string)owner.FindResource("Str_DataMerge_SelectData"), StringComparison.Ordinal) ? data : output;
                        ((Button)dialog.FindName("AcceptButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }
                    else if (merge && type == "DocumentReportDialog") { reportShown = true; dialog.Close(); }
                    else throw new InvalidOperationException("Unexpected output dialog: " + type);
                    Console.WriteLine("CONTROL layout dialog: " + type);
                }
                catch (Exception error) { failure = error; dialog.Close(); }
            }
        };
        Click(owner, "SaveMenuBtn");
        Pump();
        var roots = PresentationSource.CurrentSources.Cast<PresentationSource>().Select(item => item.RootVisual).OfType<DependencyObject>();
        ContextMenu menu = roots.OfType<ContextMenu>().Concat(roots.SelectMany(Descendants<ContextMenu>)).Distinct().Single(item => item.IsOpen);
        MenuItem tools = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, owner.FindResource("Str_DocumentTools")));
        MenuItem production = tools.Items.OfType<MenuItem>().Single(item => Equals(item.Header,
            owner.FindResource(merge ? "Str_DocumentTools_DataMerge" : "Str_DocumentTools_ProductionLayouts")));
        menu.IsOpen = false;
        timer.Start();
        try
        {
            production.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Wait(() => (File.Exists(output) && (!merge || reportShown)) || failure is not null, "complete document output");
            if (failure is not null) throw failure;
        }
        finally { timer.Stop(); }
        if (!File.ReadAllBytes(source).AsSpan().SequenceEqual(original) || !Dirty(owner))
            throw new InvalidOperationException("Layout export changed the source or consumed pending edit state.");
        if (!ReadText(output).Contains(Marker, StringComparison.Ordinal))
            throw new InvalidOperationException("WIRE-001: " + (merge ? "direct Data Merge" : "production layout") + " output omitted the visible pending text annotation.");
        if (merge && (ReadFormValue(output, "existing.text") != "MERGED_RECORD"
            || ReadFormValue(output, "untouched.sibling") != ReadFormValue(source, "untouched.sibling")))
            throw new InvalidOperationException("Direct Data Merge lost its mapped value or changed an unmapped field.");
        Console.WriteLine("CONTROL strict production-layout output retained pending annotation and unchanged live/source state.");
        if (layoutKey is not null) Console.WriteLine("CONTROL completed layout: " + layoutKey);
    }

    private static void VerifyPageEditFailure(Window owner, string source, byte[] original, TextBox editor, string folder, bool delete = false, bool reorder = false)
    {
        Stroke(0x0D, 0x11);
        Wait(() => !editor.IsVisible && Dirty(owner), "annotation before page-edit failure");
        Save(owner);
        byte[] savedBefore = File.ReadAllBytes(source);
        if (savedBefore.AsSpan().SequenceEqual(original) || !ReadText(source).Contains(Marker, StringComparison.Ordinal) || Dirty(owner))
            throw new InvalidOperationException("Page-edit failure control did not save the initial annotation cleanly.");
        var visible = Descendants<Border>(owner).Single(border => border.IsVisible
            && Descendants<TextBlock>(border).Any(text => text.Text == Marker)
            && border.Parent is Canvas);
        double left = Canvas.GetLeft(visible), top = Canvas.GetTop(visible);
        bool undoEnabled = ((Button)owner.FindName("UndoBtn")).IsEnabled;
        string tempRoot = Path.GetFullPath(Path.Combine(folder, "data", "Temp"));
        string session = Directory.GetDirectories(tempRoot, "session-*").Single();
        var directory = new DirectoryInfo(session);
        DirectorySecurity originalSecurity = directory.GetAccessControl(AccessControlSections.Access);
        string originalSddl = originalSecurity.GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        File.WriteAllText(Path.Combine(folder, "original-temp-access.txt"), originalSddl);
        DirectorySecurity denied = directory.GetAccessControl(AccessControlSections.Access);
        denied.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!,
            FileSystemRights.CreateFiles, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Deny));
        int errors = 0;
        int escaped = 0;
        int confirmations = 0;
        var seen = new HashSet<Window>();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) =>
        {
            foreach (Window dialog in Application.Current.Windows.Cast<Window>().Where(window => window != owner && window.IsVisible).ToArray())
            {
                if (delete && dialog.GetType().Name == "DocumentValuesDialog" && seen.Add(dialog))
                {
                    if (Descendants<TextBox>(dialog).Single().Text != "1")
                        throw new InvalidOperationException("Annotated Delete did not prefill the current page.");
                    Button apply = Descendants<Button>(dialog).Single(button => button.IsDefault);
                    owner.Dispatcher.BeginInvoke(DispatcherPriority.Input,
                        (Action)(() => apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent))));
                    continue;
                }
                Button? yes = delete ? Descendants<Button>(dialog).FirstOrDefault(button =>
                    Equals(button.Content, Application.Current.FindResource("Str_Btn_Yes"))) : null;
                if (yes is not null && seen.Add(dialog))
                {
                    confirmations++;
                    owner.Dispatcher.BeginInvoke(DispatcherPriority.Input,
                        (Action)(() => yes.RaiseEvent(new RoutedEventArgs(Button.ClickEvent))));
                    continue;
                }
                Button? ok = Descendants<Button>(dialog).FirstOrDefault(button =>
                    Equals(button.Content, Application.Current.FindResource("Str_Btn_OK")));
                if (ok is null || !seen.Add(dialog)) continue;
                errors++;
                Console.WriteLine("CONTROL page-edit error: " + string.Join(" ", Descendants<TextBlock>(dialog).Select(text => text.Text)));
                ok.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
        };
        try
        {
            directory.SetAccessControl(denied);
            bool blocked = false;
            try { File.WriteAllBytes(Path.Combine(session, "write-denial-control"), [1]); }
            catch (UnauthorizedAccessException) { blocked = true; }
            if (!blocked) throw new InvalidOperationException("Synthetic temporary-write denial was not effective.");
            timer.Start();
            try
            {
                if (delete) Click(owner, "DeletePagesBtn");
                else if (reorder)
                {
                    try { Click(owner, "MovePageDownBtn"); }
                    catch (UnauthorizedAccessException error) when (error.Message.Contains(session, StringComparison.OrdinalIgnoreCase))
                    {
                        escaped++; errors++;
                        Console.WriteLine("CONTROL scoped write error escaped the public Move Page Down command.");
                    }
                }
                else RotateFromThumbnail(owner);
            }
            finally { timer.Stop(); }
        }
        finally
        {
            var restoredSecurity = new DirectorySecurity();
            restoredSecurity.SetSecurityDescriptorSddlForm(originalSddl, AccessControlSections.Access);
            directory.SetAccessControl(restoredSecurity);
            if (directory.GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access) != originalSddl)
                throw new InvalidOperationException("Synthetic temporary-directory permissions were not restored exactly.");
        }
        Pump();
        Console.WriteLine($"CONTROL failed page operation: errors={errors}, dirty={Dirty(owner)}, sourceUnchanged={File.ReadAllBytes(source).AsSpan().SequenceEqual(savedBefore)}, undoUnchanged={((Button)owner.FindName("UndoBtn")).IsEnabled == undoEnabled}, left={Canvas.GetLeft(visible)}, top={Canvas.GetTop(visible)}, previousLeft={left}, previousTop={top}.");
        if (errors != 1 || Dirty(owner) || !File.ReadAllBytes(source).AsSpan().SequenceEqual(savedBefore)
            || ((Button)owner.FindName("UndoBtn")).IsEnabled != undoEnabled
            || Math.Abs(Canvas.GetLeft(visible) - left) > 0.01 || Math.Abs(Canvas.GetTop(visible) - top) > 0.01)
            throw new InvalidOperationException("APP-004: failed page operation changed annotations, dirty state, history or original bytes.");
        if (delete && (confirmations != 1 || ((ListBox)owner.FindName("PageList")).Items.Count != 5))
            throw new InvalidOperationException("APP-004: annotated Delete did not fail after its confirmation while preserving all five pages.");
        Save(owner);
        if (!ReadText(source).Contains(Marker, StringComparison.Ordinal))
            throw new InvalidOperationException("Failed page operation lost the annotation on subsequent Save.");
        Console.WriteLine("CONTROL denied page edit retained geometry, dirty/history state and strict saved annotation.");
        if (reorder)
        {
            if (escaped != 0) throw new InvalidOperationException("APP-004: failed page reorder escaped its command error boundary after preserving Save state.");
            Click(owner, "MovePageDownBtn");
            Wait(() => Dirty(owner), "successful page reorder retry");
            Save(owner);
            if (ReadText(source).Contains(Marker, StringComparison.Ordinal) || !ReadText(source, 1).Contains(Marker, StringComparison.Ordinal)
                || !ReadText(source).Contains("PAGE_2", StringComparison.Ordinal))
                throw new InvalidOperationException("Page reorder retry misplaced its original page or annotation.");
            Click(owner, "UndoBtn"); Save(owner);
            if (!ReadText(source).Contains(Marker, StringComparison.Ordinal) || !ReadText(source).Contains("PAGE_1", StringComparison.Ordinal))
                throw new InvalidOperationException("Page reorder Undo lost its original page or annotation.");
            Click(owner, "RedoBtn"); Save(owner);
            if (!ReadText(source, 1).Contains(Marker, StringComparison.Ordinal) || !ReadText(source).Contains("PAGE_2", StringComparison.Ordinal))
                throw new InvalidOperationException("Page reorder Redo lost the moved page or annotation.");
            ((ListBox)owner.FindName("PageList")).SelectedIndex = 1;
            Click(owner, "MovePageUpBtn"); Save(owner);
            if (!ReadText(source).Contains(Marker, StringComparison.Ordinal) || !ReadText(source).Contains("PAGE_1", StringComparison.Ordinal))
                throw new InvalidOperationException("Move Page Up lost its original page or annotation.");
            Click(owner, "UndoBtn"); Save(owner);
            if (!ReadText(source, 1).Contains(Marker, StringComparison.Ordinal) || !ReadText(source).Contains("PAGE_2", StringComparison.Ordinal))
                throw new InvalidOperationException("Move Page Up Undo lost the moved page or annotation.");
            Click(owner, "RedoBtn"); Save(owner);
            if (!ReadText(source).Contains(Marker, StringComparison.Ordinal) || !ReadText(source).Contains("PAGE_1", StringComparison.Ordinal))
                throw new InvalidOperationException("Move Page Up Redo lost its original page or annotation.");
            Console.WriteLine("CONTROL Move Page Up preserves strict page ownership and Save/Undo/Redo.");
            Console.WriteLine("CONTROL failed reorder and successful retry preserve strict page ownership and Save/Undo/Redo.");
            return;
        }
        if (delete)
        {
            timer.Start();
            try { Click(owner, "DeletePagesBtn"); }
            finally { timer.Stop(); }
            Wait(() => ((ListBox)owner.FindName("PageList")).Items.Count == 4 && Dirty(owner), "annotated Delete retry");
            Save(owner);
            if (ReadText(source).Contains(Marker, StringComparison.Ordinal) || ReadText(source).Contains("PAGE_1", StringComparison.Ordinal))
                throw new InvalidOperationException("Successful Delete retained content from the removed annotated page.");
            Click(owner, "UndoBtn");
            Save(owner);
            if (((ListBox)owner.FindName("PageList")).Items.Count != 5 || !ReadText(source).Contains(Marker, StringComparison.Ordinal))
                throw new InvalidOperationException("Delete Undo lost the restored page or its annotation.");
            Click(owner, "RedoBtn");
            Save(owner);
            if (((ListBox)owner.FindName("PageList")).Items.Count != 4 || ReadText(source).Contains(Marker, StringComparison.Ordinal))
                throw new InvalidOperationException("Delete Redo restored removed annotation content.");
            Console.WriteLine("CONTROL annotated Delete failure, retry and strict Save/Undo/Redo preserved the expected page and annotation content.");
            return;
        }
        RotateFromThumbnail(owner);
        Wait(() => Dirty(owner), "successful rotation after write failure");
        Save(owner);
        if (ReadRotation(source) != 90 || !ReadText(source).Contains(Marker, StringComparison.Ordinal))
            throw new InvalidOperationException("Retry after failed rotation did not save its page turn and annotation.");
        Click(owner, "UndoBtn");
        if (!Dirty(owner)) throw new InvalidOperationException("Undo of the successful retry did not differ from the saved checkpoint.");
        Save(owner);
        if (ReadRotation(source) != 0 || !ReadText(source).Contains(Marker, StringComparison.Ordinal))
            throw new InvalidOperationException("Undo after the successful retry did not restore the original page turn and annotation.");
        Click(owner, "RedoBtn");
        Save(owner);
        if (ReadRotation(source) != 90 || !ReadText(source).Contains(Marker, StringComparison.Ordinal))
            throw new InvalidOperationException("Redo after the successful retry did not restore the saved page turn and annotation.");
        Console.WriteLine("CONTROL successful retry and Undo/Redo strictly reopened with expected rotation and annotation.");
    }

    private static void VerifyPendingRangeExtraction(Window owner, string source, byte[] original, string folder)
    {
        string destination = Path.Combine(folder, "extracted-pending-text.pdf");
        int ranges = 0, pickers = 0;
        var seen = new HashSet<Window>();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) =>
        {
            foreach (Window dialog in Application.Current.Windows.Cast<Window>().Where(window => window != owner && window.IsVisible).ToArray())
            {
                if (!seen.Add(dialog)) continue;
                if (dialog.GetType().Name == "DocumentValuesDialog")
                {
                    ranges++;
                    if (Descendants<TextBox>(dialog).Single().Text != "1")
                        throw new InvalidOperationException("Pending extraction did not prefill its selected page.");
                    Button apply = Descendants<Button>(dialog).Single(button => button.IsDefault);
                    owner.Dispatcher.BeginInvoke(DispatcherPriority.Input,
                        (Action)(() => apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent))));
                }
                else if (dialog.GetType().Name == "FileDialog")
                {
                    pickers++;
                    ((TextBox)dialog.FindName("FileNameBox")).Text = destination;
                    Button accept = (Button)dialog.FindName("AcceptButton");
                    owner.Dispatcher.BeginInvoke(DispatcherPriority.Input,
                        (Action)(() => accept.RaiseEvent(new RoutedEventArgs(Button.ClickEvent))));
                }
            }
        };
        timer.Start();
        try { Click(owner, "ExtractPagesBtn"); }
        finally { timer.Stop(); }
        if (ranges != 1 || pickers != 1 || !File.Exists(destination) || !ReadText(destination).Contains(Marker, StringComparison.Ordinal)
            || !File.ReadAllBytes(source).AsSpan().SequenceEqual(original) || !Dirty(owner))
            throw new InvalidOperationException("USER-004: typed extraction omitted pending text, changed the source or accepted a saved checkpoint.");
        Click(owner, "UndoBtn");
        if (Dirty(owner)) throw new InvalidOperationException("Extraction added history beyond the pending text edit.");
        Click(owner, "RedoBtn");
        if (!Dirty(owner) || !File.ReadAllBytes(source).AsSpan().SequenceEqual(original)
            || !ReadText(destination).Contains(Marker, StringComparison.Ordinal))
            throw new InvalidOperationException("Extraction changed the source or lost pending edit history after Undo/Redo.");
        Console.WriteLine("CONTROL typed Extract retained the active text editor in strict output while preserving original source bytes, dirty checkpoint and Undo/Redo history.");
    }

    private static byte[] TiltedScanFixture(bool healthy = false)
    {
        const int size = 400;
        var pixels = Enumerable.Repeat((byte)255, size * size).ToArray();
        for (int row = 45; row < 355; row += 24)
            for (int x = 30; x < 370; x++)
                for (int thickness = 0; thickness < 3; thickness++)
                    pixels[(row + (int)Math.Round((x - 200) * 0.05) + thickness) * size + x] = 0;
        string image = Convert.ToHexString(pixels) + ">";
        const string content = "q 200 0 0 200 0 0 cm /Scan Do Q\n";
        string[] objects = ["<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /XObject << /Scan 5 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}endstream",
            $"<< /Type /XObject /Subtype /Image /Width {size} /Height {size} /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /ASCIIHexDecode /Length {image.Length} >>\nstream\n{image}\nendstream",
            healthy ? "<< /CreationDate (D:20261026120000Z) >>" : "<< /CreationDate (NOT_A_DATE) >>"];
        var pdf = new System.Text.StringBuilder("%PDF-1.7\n");
        var offsets = new List<int> { 0 };
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        int xref = pdf.Length;
        pdf.Append($"xref\n0 {offsets.Count}\n0000000000 65535 f \n");
        foreach (int offset in offsets.Skip(1))
            pdf.Append(offset.ToString("D10", System.Globalization.CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        pdf.Append($"trailer\n<< /Size {offsets.Count} /Root 1 0 R /Info 6 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return System.Text.Encoding.ASCII.GetBytes(pdf.ToString());
    }

    private static void VerifyFileInsertion(Window owner, string source, byte[] original, TextBox editor, string folder, bool success)
    {
        string imported = Path.Combine(folder, "imported-page.pdf");
        byte[] importedBytes = TableExportSmoke.Fixture();
        File.WriteAllBytes(imported, importedBytes);
        if (!ReadText(imported).Contains("0012", StringComparison.Ordinal) || ReadText(imported).Contains(Marker, StringComparison.Ordinal))
            throw new InvalidOperationException("The imported-page positive control is invalid.");
        Stroke(0x0D, 0x11);
        Wait(() => !editor.IsVisible && Dirty(owner), "pending text before file insertion");
        var pages = (ListBox)owner.FindName("PageList");
        Wait(() => pages.Items.Count == 1 && pages.ItemContainerGenerator.ContainerFromIndex(0) is ListBoxItem { IsVisible: true }, "insertion thumbnail container");
        var first = (ListBoxItem)pages.ItemContainerGenerator.ContainerFromIndex(0);
        var marker = (FrameworkElement)owner.FindName("PageDropIndicator");
        if (marker.IsVisible) throw new InvalidOperationException("The fresh drag fixture already shows an insertion marker.");
        Point origin = pages.PointToScreen(new Point(pages.ActualWidth * .5, pages.ActualHeight - 12));
        Point target = first.PointToScreen(new Point(first.ActualWidth * .5, 4));
        DependencyObject? hit = pages.InputHitTest(new Point(pages.ActualWidth * .5, pages.ActualHeight - 12)) as DependencyObject;
        for (DependencyObject? item = hit; item is not null && item != pages; item = VisualTreeHelper.GetParent(item))
            if (item is ListBoxItem or System.Windows.Controls.Primitives.ScrollBar)
                throw new InvalidOperationException("The drag source must be empty owned sidebar space.");
        IntPtr handle = new WindowInteropHelper(owner).Handle;
        foreach (Point point in new[] { origin, target })
            if (point.X < 0 || point.Y < 0 || point.X >= SystemParameters.PrimaryScreenWidth || point.Y >= SystemParameters.PrimaryScreenHeight
                || WindowFromPoint(new NativePoint { X = (int)point.X, Y = (int)point.Y }) != handle)
                throw new InvalidOperationException("File insertion pointer target is not inside the owned window.");
        bool undo = ((Button)owner.FindName("UndoBtn")).IsEnabled;
        int writeErrors = 0, unhandled = 0;
        string tempRoot = Path.GetFullPath(Path.Combine(folder, "data", "Temp"));
        var directory = new DirectoryInfo(Directory.GetDirectories(tempRoot, "session-*").Single());
        string originalSddl = directory.GetAccessControl(AccessControlSections.Access)
            .GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        File.WriteAllText(Path.Combine(folder, "original-temp-access.txt"), originalSddl);
        DirectorySecurity denied = directory.GetAccessControl(AccessControlSections.Access);
        denied.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!,
            FileSystemRights.CreateFiles, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Deny));
        var seen = new HashSet<Window>();
        var dialogs = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(20) };
        dialogs.Tick += (_, _) =>
        {
            foreach (Window dialog in Application.Current.Windows.Cast<Window>().Where(window => window != owner && window.IsVisible).ToArray())
            {
                if (!seen.Add(dialog)) continue;
                string message = string.Join(" ", Descendants<TextBlock>(dialog).Select(text => text.Text));
                Button? ok = Descendants<Button>(dialog).FirstOrDefault(button => Equals(button.Content, owner.FindResource("Str_Btn_OK")));
                if (success || ok is null || !message.Contains("denied", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Unexpected file insertion dialog: " + message);
                writeErrors++;
                Console.WriteLine("CONTROL file insertion reported the expected temporary-write error: " + message);
                owner.Dispatcher.BeginInvoke(DispatcherPriority.Input, (Action)(() => ok.RaiseEvent(new RoutedEventArgs(Button.ClickEvent))));
            }
        };
        DispatcherUnhandledExceptionEventHandler boundary = (_, error) =>
        {
            Exception cause = error.Exception.GetBaseException();
            if (success || cause is not UnauthorizedAccessException
                || !cause.Message.Contains(directory.FullName, StringComparison.OrdinalIgnoreCase)) return;
            // Startup is suppressed in this host. Catch only the expected error at the same
            // dispatcher boundary so state can be inspected after the failing async command.
            error.Handled = true;
            unhandled++; writeErrors++;
            Console.WriteLine("CONTROL expected temporary-write error escaped the file insertion command to the dispatcher boundary.");
        };
        var watch = Stopwatch.StartNew();
        bool indicatorSeen = false;
        Exception? nativeFailure = null;
        Thread? mouseThread = null;
        DragEventHandler observe = (_, args) =>
        {
            if (args.Data.GetDataPresent(DataFormats.FileDrop) && marker.IsVisible) indicatorSeen = true;
        };
        pages.AddHandler(DragDrop.DragOverEvent, observe, handledEventsToo: true);
        Application.Current.DispatcherUnhandledException += boundary;
        dialogs.Start();
        try
        {
            if (!success)
            {
                directory.SetAccessControl(denied);
                bool blocked = false;
                try { File.WriteAllBytes(Path.Combine(directory.FullName, "write-denial-control"), [1]); }
                catch (UnauthorizedAccessException) { blocked = true; }
                if (!blocked) throw new InvalidOperationException("Synthetic temporary-write denial was not effective.");
                Console.WriteLine("CONTROL isolated temporary directory independently refuses new files.");
            }
            var data = new DataObject();
            data.SetData(DataFormats.FileDrop, new[] { imported });
            SetCursorPos((int)origin.X, (int)origin.Y);
            MouseEvent(2, 0, 0, 0, UIntPtr.Zero);
            Wait(() => (GetAsyncKeyState(1) & 0x8000) != 0 && Mouse.LeftButton == MouseButtonState.Pressed
                && GetForegroundWindow() == handle, "owned native drag source has a pressed mouse button");
            Console.WriteLine("CONTROL native and WPF mouse-button state are pressed in the owned foreground window.");
            watch.Restart();
            mouseThread = new Thread(() =>
            {
                try
                {
                    Thread.Sleep(150);
                    var point = new NativePoint { X = (int)target.X, Y = (int)target.Y };
                    if (GetForegroundWindow() != handle || WindowFromPoint(point) != handle)
                        throw new InvalidOperationException("Native drag target lost its exact owned window.");
                    if (!SetCursorPos(point.X, point.Y)) throw new InvalidOperationException("Cannot move the native drag pointer.");
                    Thread.Sleep(250);
                }
                catch (Exception error) { nativeFailure = error; }
                finally { MouseEvent(4, 0, 0, 0, UIntPtr.Zero); }
            }) { IsBackground = true, Name = "Synthetic file insertion mouse" };
            mouseThread.Start();
            DragDropEffects effect = DragDropEffects.None;
            try { effect = DragDrop.DoDragDrop(pages, data, DragDropEffects.Copy); }
            finally { MouseEvent(4, 0, 0, 0, UIntPtr.Zero); mouseThread.Join(TimeSpan.FromSeconds(2)); }
            if (nativeFailure is not null) throw new InvalidOperationException("Native drag input failed.", nativeFailure);
            Console.WriteLine($"CONTROL native drag returned {effect}; marker={indicatorSeen}; elapsedMs={watch.ElapsedMilliseconds}.");
            if (!indicatorSeen) throw new InvalidOperationException("No before-first-page insertion marker appeared during the native file drag.");
            Console.WriteLine("CONTROL public file drag reached the before-first-thumbnail insertion marker.");
            Wait(() => !marker.IsVisible, "file insertion marker dismissed after Drop");
            Wait(() => success ? pages.Items.Count == 2 : writeErrors == 1, "file insertion publication or expected write failure");
            Wait(() => !Application.Current.Windows.Cast<Window>().Any(window => window != owner && window.IsVisible), "file insertion error dismissed");
        }
        finally
        {
            dialogs.Stop(); MouseEvent(4, 0, 0, 0, UIntPtr.Zero);
            if (mouseThread?.IsAlive == true) mouseThread.Join(TimeSpan.FromSeconds(2));
            pages.RemoveHandler(DragDrop.DragOverEvent, observe);
            Application.Current.DispatcherUnhandledException -= boundary;
            if (!success)
            {
                var restored = new DirectorySecurity();
                restored.SetSecurityDescriptorSddlForm(originalSddl, AccessControlSections.Access);
                directory.SetAccessControl(restored);
            }
            if (directory.GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access) != originalSddl)
                throw new InvalidOperationException("Synthetic insertion temporary-directory permissions were not restored exactly.");
            Console.WriteLine("CONTROL isolated temporary-directory access descriptor matches its original value.");
        }
        if (!File.ReadAllBytes(source).AsSpan().SequenceEqual(original) || !File.ReadAllBytes(imported).AsSpan().SequenceEqual(importedBytes)
            || !Dirty(owner) || (!success && (pages.Items.Count != 1 || ((Button)owner.FindName("UndoBtn")).IsEnabled != undo)))
            throw new InvalidOperationException("APP-004: file insertion changed source bytes, page count or dirty/history state before successful publication.");
        Save(owner);
        int annotated = success ? 1 : 0;
        if (!ReadText(source, annotated).Contains(Marker, StringComparison.Ordinal) || (success && ReadText(source).Contains(Marker, StringComparison.Ordinal)))
            throw new InvalidOperationException("APP-004: file insertion lost or misplaced the pending annotation on subsequent Save.");
        if (unhandled != 0) throw new InvalidOperationException("APP-004: failed file insertion escaped its command error boundary.");
        Click(owner, "UndoBtn"); Save(owner);
        if (ReadText(source).Contains(Marker, StringComparison.Ordinal) != success)
            throw new InvalidOperationException("File insertion Undo did not restore the expected original text state.");
        Click(owner, "RedoBtn"); Save(owner);
        if (!ReadText(source, annotated).Contains(Marker, StringComparison.Ordinal))
            throw new InvalidOperationException("File insertion Redo did not restore the pending text on its owning page.");
        Console.WriteLine("CONTROL native file insertion preserved source ownership and strict Save/Undo/Redo annotation content.");
    }

    private static void VerifyTransformFailure(Window owner, string source, byte[] original, TextBox editor, bool cancel = false, bool saveOnly = false, bool success = false, bool straighten = false, bool forms = false)
    {
        Assembly engine = Assembly.Load("KillerPdf.Engine");
        object document = engine.GetType("KillerPdf.Engine.Documents.PdfDocument", true)!
            .GetMethod("Open", [typeof(ReadOnlyMemory<byte>)])!.Invoke(null, [new ReadOnlyMemory<byte>(original)])!;
        bool refused = false;
        try { engine.GetType("KillerPdf.Engine.Documents.PdfDocumentInformation", true)!.GetMethod("Read")!.Invoke(null, [document]); }
        catch (TargetInvocationException error) when (error.InnerException is InvalidOperationException inner
            && inner.Message.Contains("not a valid PDF date", StringComparison.Ordinal)) { refused = true; }
        if (refused == success) throw new InvalidOperationException("The synthetic Transform metadata control did not match its expected healthy or malformed state.");
        Stroke(0x0D, 0x11);
        Wait(() => !editor.IsVisible && Dirty(owner), "pending annotation before Transform failure");
        if (forms)
        {
            VerifyFields("ORIGINAL");
            TextBox field = Descendants<TextBox>(owner).Single(box => Equals(box.ToolTip, "existing.text"));
            field.Focus(); field.SelectAll(); field.SelectedText = "CHANGED";
            Pump();
            if (!Dirty(owner) || field.Text != "CHANGED") throw new InvalidOperationException("The independent pending form edit was not established.");
            Console.WriteLine("CONTROL independent pending form edit CHANGED and untouched sibling established before Transform.");
        }
        var visible = Descendants<Border>(owner).Single(border => border.IsVisible
            && Descendants<TextBlock>(border).Any(text => text.Text == Marker) && border.Parent is Canvas);
        double left = Canvas.GetLeft(visible), top = Canvas.GetTop(visible);
        bool undoEnabled = ((Button)owner.FindName("UndoBtn")).IsEnabled;
        if (!undoEnabled) throw new InvalidOperationException("The pending annotation has no Undo control.");
        Console.WriteLine("CONTROL rendered source and pending annotation are usable; public metadata reading independently "
            + (success ? "accepts the healthy document." : "refuses NOT_A_DATE."));
        int errors = 0, transforms = 0;
        var seen = new HashSet<Window>();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) =>
        {
            foreach (Window dialog in Application.Current.Windows.Cast<Window>().Where(window => window != owner && window.IsVisible).ToArray())
            {
                if (!seen.Add(dialog)) continue;
                if (dialog.GetType().Name == "TransformWindow")
                {
                    transforms++;
                    Button turn = Descendants<Button>(dialog).Single(button => Equals(button.Content, "90° ↻"));
                    Button apply = Descendants<Button>(dialog).Single(button => cancel ? button.IsCancel : button.IsDefault);
                    owner.Dispatcher.BeginInvoke(DispatcherPriority.Input, (Action)(() =>
                    {
                        turn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }));
                    continue;
                }
                Button? ok = Descendants<Button>(dialog).FirstOrDefault(button => Equals(button.Content, owner.FindResource("Str_Btn_OK")));
                if (ok is null) continue;
                string message = string.Join(" ", Descendants<TextBlock>(dialog).Select(text => text.Text));
                Console.WriteLine("CONTROL Transform notice: " + message);
                if (message.Contains("not a valid PDF date", StringComparison.Ordinal)) errors++;
                owner.Dispatcher.BeginInvoke(DispatcherPriority.Input,
                    (Action)(() => ok.RaiseEvent(new RoutedEventArgs(Button.ClickEvent))));
            }
        };
        try
        {
            if (!saveOnly)
            {
                timer.Start();
                if (straighten) ParagraphEditSmoke.RunSearchAction(owner, "Str_Pages_Straighten", "Alt+Shift+S");
                else Click(owner, "ToolRotateBtn");
                if (success) Wait(() => !visible.IsVisible, "successful Transform replaces the editable annotation layer");
                else if (!cancel) Wait(() => errors == 1, "late Transform metadata failure");
                Wait(() => !Application.Current.Windows.Cast<Window>().Any(window => window != owner && window.IsVisible), "Transform dialog dismissed");
            }
        }
        finally { timer.Stop(); }
        if (transforms != (saveOnly || straighten ? 0 : 1) || errors != (saveOnly || cancel || success ? 0 : 1) || !Dirty(owner) || !File.ReadAllBytes(source).AsSpan().SequenceEqual(original)
            || ((Button)owner.FindName("UndoBtn")).IsEnabled != undoEnabled
            || (!success && (!visible.IsVisible || Math.Abs(Canvas.GetLeft(visible) - left) > 0.01 || Math.Abs(Canvas.GetTop(visible) - top) > 0.01)))
            throw new InvalidOperationException("APP-004: late Transform failure changed pending geometry, dirty/history state or source bytes.");
        if (forms && Descendants<TextBox>(owner).Single(box => Equals(box.ToolTip, "existing.text")).Text != "CHANGED")
            throw new InvalidOperationException("APP-004: failed Transform discarded the live form value.");
        Save(owner);
        if (forms) VerifyFields("CHANGED");
        if (ReadText(source).Contains(Marker, StringComparison.Ordinal) == success || ReadRotation(source) != 0
            || (success && ReadRasterImageCount(source) == 0))
            throw new InvalidOperationException(success ? "Successful Transform did not publish raster data without an editable text overlay."
                : saveOnly ? "Metadata Save control lost the pending annotation."
                : cancel ? "Transform Cancel control lost the pending annotation."
                : "APP-004: late Transform failure lost the pending annotation on subsequent Save.");
        Click(owner, "UndoBtn"); Save(owner);
        if (forms) VerifyFields("CHANGED");
        if (ReadText(source).Contains(Marker, StringComparison.Ordinal) != success)
            throw new InvalidOperationException(success ? "Transform Undo did not restore the original annotation."
                : "Undo after failed Transform did not remove the original annotation.");
        Click(owner, "RedoBtn"); Save(owner);
        if (forms) VerifyFields("CHANGED");
        if (ReadText(source).Contains(Marker, StringComparison.Ordinal) == success
            || (success && ReadRasterImageCount(source) == 0))
            throw new InvalidOperationException(success ? "Transform Redo did not restore raster data without editable text."
                : "Redo after failed Transform lost the original annotation.");
        Console.WriteLine(success
            ? "CONTROL successful Transform published a raster page without an editable overlay; strict Save/Undo/Redo restored the expected image and text states."
            : "CONTROL " + (saveOnly ? "metadata Save without Transform" : cancel ? "Transform Cancel" : "late Transform metadata failure")
                + " preserved pending annotation, source, rotation, dirty/history and strict Save/Undo/Redo content.");

        void VerifyFields(string expected)
        {
            object saved = engine.GetType("KillerPdf.Engine.Documents.PdfDocument", true)!
                .GetMethod("Open", [typeof(ReadOnlyMemory<byte>)])!.Invoke(null, [new ReadOnlyMemory<byte>(File.ReadAllBytes(source))])!;
            object[] widgets = ((System.Collections.IEnumerable)engine.GetType("KillerPdf.Engine.Documents.PdfFormWidgetReader", true)!
                .GetMethod("ReadPage")!.Invoke(null, [saved, 0])!).Cast<object>().ToArray();
            string? Value(string name)
            {
                object widget = widgets.Single(item => Equals(item.GetType().GetProperty("FieldName")!.GetValue(item), name));
                return widget.GetType().GetProperty("Value")!.GetValue(widget) as string;
            }
            if (widgets.Length != 2 || Value("existing.text") != expected || Value("untouched.sibling") != "PRESERVED SIBLING")
                throw new InvalidOperationException("Failed Transform or annotation history replay changed independent form values.");
        }
    }

    private static int ReadRasterImageCount(string source)
    {
        Assembly engine = Assembly.Load("KillerPdf.Engine");
        object document = engine.GetType("KillerPdf.Engine.Documents.PdfDocument", true)!
            .GetMethod("Open", [typeof(ReadOnlyMemory<byte>)])!.Invoke(null, [new ReadOnlyMemory<byte>(File.ReadAllBytes(source))])!;
        object reader = Activator.CreateInstance(engine.GetType("KillerPdf.Engine.Documents.PdfPageContentReader", true)!, [document])!;
        object content = reader.GetType().GetMethod("Read", [typeof(int), typeof(CancellationToken)])!.Invoke(reader, [0, CancellationToken.None])!;
        return ((System.Collections.IEnumerable)content.GetType().GetProperty("Images")!.GetValue(content)!).Cast<object>().Count();
    }

    private static void RotateFromThumbnail(Window owner)
    {
        var pages = (ListBox)owner.FindName("PageList");
        pages.SelectedIndex = 0;
        var thumbnail = (ListBoxItem)pages.ItemContainerGenerator.ContainerFromIndex(0);
        thumbnail.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right)
            { RoutedEvent = Mouse.MouseUpEvent });
        Pump();
        var roots = PresentationSource.CurrentSources.Cast<PresentationSource>().Select(source => source.RootVisual).OfType<DependencyObject>();
        ContextMenu[] menus = roots.OfType<ContextMenu>().Concat(roots.SelectMany(Descendants<ContextMenu>)).Distinct().Where(menu => menu.IsOpen).ToArray();
        string rotationLabel = (string)owner.FindResource("Str_Ctx_RotateCWShort");
        MenuItem rotate = menus.SelectMany(menu => menu.Items.OfType<MenuItem>()).Single(item => Equals(item.Header, rotationLabel));
        try { rotate.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); }
        finally { foreach (ContextMenu menu in menus) menu.IsOpen = false; }
    }

    internal static int ReadRotation(string source)
    {
        Assembly engine = Assembly.Load("KillerPdf.Engine");
        object document = engine.GetType("KillerPdf.Engine.Documents.PdfDocument", true)!
            .GetMethod("Open", [typeof(ReadOnlyMemory<byte>)])!.Invoke(null, [new ReadOnlyMemory<byte>(File.ReadAllBytes(source))])!;
        var pages = (System.Collections.IEnumerable)engine.GetType("KillerPdf.Engine.Documents.PdfPageInformation", true)!
            .GetMethod("Read")!.Invoke(null, [document])!;
        object page = pages.Cast<object>().First();
        return (int)page.GetType().GetProperty("Rotation")!.GetValue(page)!;
    }

    private static void VerifyHistory(Window owner, string source, TextBox editor)
    {
        Stroke(0x0D, 0x11);
        Wait(() => !editor.IsVisible && Dirty(owner), "committed dirty annotation");
        Console.WriteLine("CONTROL text commit removed the editor and marked the document dirty.");
        Save(owner);
        Wait(() => !Dirty(owner), "successful clean save");
        if (!ReadText(source).Contains(Marker, StringComparison.Ordinal))
            throw new InvalidOperationException("The first successful save did not retain the typed annotation.");
        Console.WriteLine("CONTROL saved annotation strictly reopened with its marker.");

        Click(owner, "UndoBtn");
        Pump();
        if (!Dirty(owner)) throw new InvalidOperationException("APP-001: Undo after Save incorrectly reports clean content.");
        Click(owner, "RedoBtn");
        Pump();
        if (Dirty(owner)) throw new InvalidOperationException("Redo to the saved checkpoint must become clean.");
        Click(owner, "UndoBtn");
        Save(owner);
        Wait(() => !Dirty(owner), "saved undo result");
        if (ReadText(source).Contains(Marker, StringComparison.Ordinal))
            throw new InvalidOperationException("Saving the Undo result retained the removed annotation.");
    }

    private static void VerifyFailedSaveHistory(Window owner, string source, byte[] original, TextBox editor)
    {
        Stroke(0x0D, 0x11);
        Wait(() => !editor.IsVisible && Dirty(owner), "committed annotation before failed save");
        using (var locked = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            Save(owner, expectFailure: true);
        if (!Dirty(owner) || !File.ReadAllBytes(source).AsSpan().SequenceEqual(original))
            throw new InvalidOperationException("Failed Save changed the source or accepted a clean checkpoint.");
        Click(owner, "UndoBtn");
        if (Dirty(owner)) throw new InvalidOperationException("Undo after failed Save did not return to the original checkpoint.");
        Click(owner, "RedoBtn");
        if (!Dirty(owner)) throw new InvalidOperationException("Redo after failed Save lost its unsaved state.");
        Save(owner);
        if (!ReadText(source).Contains(Marker, StringComparison.Ordinal))
            throw new InvalidOperationException("Retry after releasing the destination lock lost the annotation.");
        Console.WriteLine("CONTROL failed Save preserved source/checkpoint; Undo, Redo and successful retry retained content.");
    }

    private static void VerifyFormHistory(Window owner, string source, TextBox editor)
    {
        Stroke(0x0D, 0x11);
        Wait(() => !editor.IsVisible && Dirty(owner), "committed annotation on form");
        Save(owner);
        Wait(() => !Dirty(owner), "saved form checkpoint");
        TextBox field = Descendants<TextBox>(owner).Single(box => Equals(box.ToolTip, "existing.text"));
        field.Focus();
        field.SelectAll();
        field.SelectedText = "CHANGED";
        Pump();
        if (!Dirty(owner)) throw new InvalidOperationException("The form edit did not mark the document dirty.");
        Click(owner, "UndoBtn");
        if (!Dirty(owner)) throw new InvalidOperationException("Annotation Undo hid the independent form edit.");
        Save(owner);
        Wait(() => !Dirty(owner), "saved independent form edit");
        Assembly engine = Assembly.Load("KillerPdf.Engine");
        object document = engine.GetType("KillerPdf.Engine.Documents.PdfDocument", true)!
            .GetMethod("Open", [typeof(ReadOnlyMemory<byte>)])!
            .Invoke(null, [new ReadOnlyMemory<byte>(File.ReadAllBytes(source))])!;
        object[] widgets = ((System.Collections.IEnumerable)engine.GetType("KillerPdf.Engine.Documents.PdfFormWidgetReader", true)!
            .GetMethod("ReadPage")!.Invoke(null, [document, 0])!).Cast<object>().ToArray();
        object changed = widgets.Single(widget => Equals(widget.GetType().GetProperty("FieldName")!.GetValue(widget), "existing.text"));
        if (!Equals(changed.GetType().GetProperty("Value")!.GetValue(changed), "CHANGED")
            || ReadText(source).Contains(Marker, StringComparison.Ordinal))
            throw new InvalidOperationException("Saved annotation Undo lost the independent form value or retained the removed annotation.");
        Console.WriteLine("CONTROL annotation Undo preserved the independent form edit, dirty state and strictly reopened output.");
    }

    private static void VerifyAnnotationHistory(Window owner, string source, TextBox editor, string folder)
    {
        Stroke(0x0D, 0x11);
        Wait(() => !editor.IsVisible && Dirty(owner), "committed annotation for nudge");
        Click(owner, "ToolSelectBtn");
        TextBlock MarkerBlock() => Descendants<TextBlock>(owner).Single(block => block.IsVisible && block.Text == Marker);
        double AnnotationLeft() => Canvas.GetLeft((UIElement)MarkerBlock().Parent);
        TextBlock marker = MarkerBlock();
        Point target = marker.PointToScreen(new Point(Math.Min(5, marker.ActualWidth / 2), marker.ActualHeight / 2));
        if (target.X < 0 || target.Y < 0 || target.X >= SystemParameters.PrimaryScreenWidth
            || target.Y >= SystemParameters.PrimaryScreenHeight)
            throw new InvalidOperationException("The annotation selection target is outside the visible desktop.");
        if (!SetCursorPos((int)target.X, (int)target.Y)) throw new InvalidOperationException("Cannot select annotation.");
        MouseEvent(2, 0, 0, 0, UIntPtr.Zero);
        MouseEvent(4, 0, 0, 0, UIntPtr.Zero);
        Pump();
        var selectButton = (Button)owner.FindName("ToolSelectBtn");
        if (!selectButton.Focus() || !selectButton.IsKeyboardFocused)
            throw new InvalidOperationException("Cannot focus the owned Select toolbar button for arrow input.");
        Console.WriteLine("CONTROL selected the visible annotation and focused the owned toolbar for arrow routing.");
        var capture = new RenderTargetBitmap((int)Math.Ceiling(owner.ActualWidth), (int)Math.Ceiling(owner.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        capture.Render(owner);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(capture));
        using (var output = File.Create(Path.Combine(folder, "annotation-before-nudge.png"))) png.Save(output);
        double initial = AnnotationLeft();
        var nudge = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(owner),
            Environment.TickCount, Key.Right) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        owner.RaiseEvent(nudge);
        Pump();
        Console.WriteLine($"CONTROL Right command handled={nudge.Handled}, modifiers={Keyboard.Modifiers}, canvas before={initial}, after={AnnotationLeft()}.");
        if (!nudge.Handled) throw new InvalidOperationException("The public Right command did not reach a handled application action.");
        Wait(() => AnnotationLeft() > initial + 0.5, "one-unit annotation nudge in public canvas geometry");
        Console.WriteLine("CONTROL real selection and public Right Arrow command moved the placed annotation.");
        Click(owner, "UndoBtn");
        if (Math.Abs(AnnotationLeft() - initial) > 0.01)
            throw new InvalidOperationException("The first Undo did not restore the annotation position.");
        Click(owner, "UndoBtn");
        bool markerRemains = Descendants<TextBlock>(owner).Any(block => block.IsVisible && block.Text == Marker);
        Save(owner);
        Wait(() => !Dirty(owner), "saved annotation Undo result");
        if (markerRemains || ReadText(source).Contains(Marker, StringComparison.Ordinal))
            throw new InvalidOperationException("APP-002: Add, Nudge, Undo, Undo retained the cloned annotation.");
        Click(owner, "RedoBtn");
        if (Math.Abs(AnnotationLeft() - initial) > 0.01)
            throw new InvalidOperationException("Redo add did not restore the original annotation position.");
        Click(owner, "RedoBtn");
        if (Math.Abs(AnnotationLeft() - initial - 1) > 0.01 || !Dirty(owner))
            throw new InvalidOperationException("Redo move did not restore the moved annotation and unsaved state.");
        Save(owner);
        if (!ReadText(source).Contains(Marker, StringComparison.Ordinal))
            throw new InvalidOperationException("The saved Redo result lost the restored annotation.");
        Console.WriteLine("CONTROL Redo add and move restored exact canvas positions and strictly reopened saved content.");
    }

    private static void VerifyClose(Window owner, string source, byte[] original, TextBox editor)
    {
        int prompts = 0;
        Exception? failure = null;
        var seen = new HashSet<Window>();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) =>
        {
            try
            {
                foreach (Window dialog in Application.Current.Windows.Cast<Window>()
                    .Where(window => window != owner && window.IsVisible).ToArray())
                {
                    if (!seen.Add(dialog)) continue;
                    Button? cancel = Descendants<Button>(dialog).FirstOrDefault(button =>
                        Equals(button.Content, Application.Current.FindResource("Str_Btn_CancelDlg")));
                    if (cancel is null) throw new InvalidOperationException("Close showed a dialog without an unsaved-change Cancel choice.");
                    prompts++;
                    cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
            }
            catch (Exception error) { failure = error; timer.Stop(); }
        };
        timer.Start();
        try
        {
            Stroke(0x73, 0x12);
            Wait(() => prompts != 0 || !owner.IsVisible || failure is not null, "Alt+F4 close decision");
            Pump();
            if (failure is not null) throw failure;
            if (prompts != 1 || !owner.IsVisible)
                throw new InvalidOperationException($"APP-006: active text close skipped protection: prompts={prompts}, visible={owner.IsVisible}.");
            if (!File.ReadAllBytes(source).AsSpan().SequenceEqual(original))
                throw new InvalidOperationException("Cancel changed the original source.");
            if (!editor.Text.Contains(Marker, StringComparison.Ordinal))
                throw new InvalidOperationException("Cancel discarded the active text.");
            if (!Descendants<TextBlock>(owner).Any(block => block.IsVisible && block.Text == Marker))
                throw new InvalidOperationException("Cancel did not preserve the committed text on the page.");
            Console.WriteLine("CONTROL Cancel retained the typed text visibly and preserved the source bytes.");
        }
        finally { timer.Stop(); }

        int savePrompts = 0;
        var saveSeen = new HashSet<Window>();
        var saveTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(20) };
        saveTimer.Tick += (_, _) =>
        {
            foreach (Window dialog in Application.Current.Windows.Cast<Window>()
                .Where(window => window != owner && window.IsVisible).ToArray())
            {
                Button? save = Descendants<Button>(dialog).FirstOrDefault(button =>
                    Equals(button.Content, Application.Current.FindResource("Str_Btn_Save")));
                if (save is null || !saveSeen.Add(dialog)) continue;
                savePrompts++;
                save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
        };
        saveTimer.Start();
        try
        {
            Stroke(0x73, 0x12);
            Wait(() => !owner.IsVisible, "Save during close");
            if (savePrompts != 1 || !ReadText(source).Contains(Marker, StringComparison.Ordinal))
                throw new InvalidOperationException("Save during close did not preserve the pending text in the reopened source.");
            Console.WriteLine("CONTROL Save during close retained the typed text in the strictly reopened PDF.");
        }
        finally { saveTimer.Stop(); }
    }

    internal static void Save(Window owner, bool expectFailure = false)
    {
        int confirmations = 0;
        string? saveError = null;
        var seen = new HashSet<Window>();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) =>
        {
            foreach (Window dialog in Application.Current.Windows.Cast<Window>()
                .Where(window => window != owner && window.IsVisible).ToArray())
            {
                Button? yes = Descendants<Button>(dialog).FirstOrDefault(button =>
                    Equals(button.Content, Application.Current.FindResource("Str_Btn_Yes")));
                Button? acceptError = Descendants<Button>(dialog).FirstOrDefault(button =>
                    Equals(button.Content, Application.Current.FindResource("Str_Btn_OK")));
                if ((yes is null && acceptError is null) || !seen.Add(dialog)) continue;
                if (yes is not null)
                {
                    confirmations++;
                    yes.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                else
                {
                    saveError = string.Join(" ", Descendants<TextBlock>(dialog).Select(block => block.Text));
                    acceptError!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
            }
        };
        timer.Start();
        try { Click(owner, "SaveAsBtn"); }
        finally { timer.Stop(); }
        if (confirmations != 1) throw new InvalidOperationException("Expected one overwrite confirmation for the synthetic source.");
        if (!expectFailure && saveError is not null)
            throw new InvalidOperationException("Public Save unexpectedly failed: " + saveError);
        if (expectFailure && (saveError is null || !saveError.Contains(
            (string)Application.Current.FindResource("Str_Err_SaveFailed"), StringComparison.Ordinal)))
            throw new InvalidOperationException("The locked destination did not show the expected Save failure.");
        Console.WriteLine("CONTROL confirmed overwrite through the public Save dialog.");
    }

    private static string? ReadFormValue(string source, string name)
    {
        Assembly engine = Assembly.Load("KillerPdf.Engine");
        object document = engine.GetType("KillerPdf.Engine.Documents.PdfDocument", true)!
            .GetMethod("Open", [typeof(ReadOnlyMemory<byte>)])!.Invoke(null, [new ReadOnlyMemory<byte>(File.ReadAllBytes(source))])!;
        object[] widgets = ((System.Collections.IEnumerable)engine.GetType("KillerPdf.Engine.Documents.PdfFormWidgetReader", true)!
            .GetMethod("ReadPage")!.Invoke(null, [document, 0])!).Cast<object>().ToArray();
        object widget = widgets.Single(item => Equals(item.GetType().GetProperty("FieldName")!.GetValue(item), name));
        return (string?)widget.GetType().GetProperty("Value")!.GetValue(widget);
    }

    internal static string ReadText(string source, int page = 0)
    {
        Assembly engine = Assembly.Load("KillerPdf.Engine");
        Type documentType = engine.GetType("KillerPdf.Engine.Documents.PdfDocument", true)!;
        object document = documentType.GetMethod("Open", [typeof(ReadOnlyMemory<byte>)])!
            .Invoke(null, [new ReadOnlyMemory<byte>(File.ReadAllBytes(source))])!;
        Type readerType = engine.GetType("KillerPdf.Engine.Documents.PdfPageContentReader", true)!;
        object reader = Activator.CreateInstance(readerType, [document])!;
        object content = readerType.GetMethod("Read", [typeof(int), typeof(CancellationToken)])!
            .Invoke(reader, [page, CancellationToken.None])!;
        return (string)content.GetType().GetProperty("Text")!.GetValue(content)!;
    }

    internal static void CaptureOwnedWindow(Window owner, string path)
    {
        owner.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(owner.ActualWidth), (int)Math.Ceiling(owner.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(owner);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path);
        png.Save(output);
    }

    internal static bool IsPageCanvas(Canvas canvas) => canvas.IsVisible && canvas.ActualWidth > 100
        && canvas.ActualHeight > 100 && canvas.GetType().Name == "SafeCanvas";
    internal static bool Dirty(Window owner) => ((Button)owner.FindName("SaveAsBtn")).Foreground
        is SolidColorBrush brush && brush.Color == Color.FromRgb(0xE0, 0x73, 0);
    internal static void Click(Window owner, string name)
    {
        var button = (Button)owner.FindName(name);
        if (!button.IsEnabled) throw new InvalidOperationException(name + " is disabled.");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump();
    }
    private static void Stroke(byte key, byte modifier)
    {
        if (modifier != 0) KeyEvent(modifier, 0, 0, UIntPtr.Zero);
        KeyEvent(key, 0, 0, UIntPtr.Zero);
        KeyEvent(key, 0, 2, UIntPtr.Zero);
        if (modifier != 0) KeyEvent(modifier, 0, 2, UIntPtr.Zero);
    }
    internal static void Wait(Func<bool> condition, string stage) => Wait(condition, () => stage);

    // The stage is described when the wait runs out, so a timeout says what the window was doing.
    internal static void Wait(Func<bool> condition, Func<string> stage)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            Pump();
            if (watch.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException("Timed out at " + stage());
            Thread.Sleep(10);
        }
    }

    private static bool WaitBriefly(Func<bool> condition, TimeSpan limit)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            Pump();
            if (watch.Elapsed > limit) return false;
            Thread.Sleep(10);
        }
        return true;
    }

    // The largest page canvas once its size has held for a few hundred milliseconds.
    private static Canvas SettledPageCanvas(DependencyObject scope)
    {
        Canvas Largest() => Descendants<Canvas>(scope).Where(IsPageCanvas)
            .OrderByDescending(item => item.ActualWidth * item.ActualHeight).First();
        Size last = default;
        var steady = Stopwatch.StartNew();
        Wait(() =>
        {
            Canvas current = Largest();
            var size = new Size(current.ActualWidth, current.ActualHeight);
            if (size != last) { last = size; steady.Restart(); }
            return steady.Elapsed >= TimeSpan.FromMilliseconds(300);
        }, "settled page size");
        return Largest();
    }

    private static void ClickPage(Window owner, Canvas canvas)
    {
        owner.Activate();
        Wait(() => owner.IsActive && owner.IsKeyboardFocusWithin, "window in the foreground with keyboard focus");
        Point point = canvas.PointToScreen(new Point(canvas.ActualWidth * 0.72, canvas.ActualHeight * 0.72));
        if (point.X < 0 || point.Y < 0 || point.X >= SystemParameters.PrimaryScreenWidth
            || point.Y >= SystemParameters.PrimaryScreenHeight)
            throw new InvalidOperationException("The owned page target is outside the visible desktop.");
        if (!SetCursorPos((int)point.X, (int)point.Y)) throw new InvalidOperationException("Cannot position the test pointer.");
        MouseEvent(2, 0, 0, 0, UIntPtr.Zero);
        MouseEvent(4, 0, 0, 0, UIntPtr.Zero);
    }
    internal static void Pump() => Application.Current.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    internal static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T item) yield return item;
            foreach (T nested in Descendants<T>(child)) yield return nested;
        }
    }
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetCursorPos(int x, int y);
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }
    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", EntryPoint = "mouse_event")]
    private static extern void MouseEvent(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    [DllImport("user32.dll", EntryPoint = "keybd_event")]
    private static extern void KeyEvent(byte key, byte scan, uint flags, UIntPtr extra);
}
