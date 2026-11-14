using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using static DocumentStateSmoke;

internal static class PageDeletionSmoke
{
    internal static int Run(Window owner, string folder, string scenario = "last")
    {
        try { return RunCore(owner, folder, scenario); }
        catch (Exception error) { Console.Error.WriteLine("Page deletion stage failed: " + error); throw; }
        finally
        {
            var seen = new HashSet<Window>();
            var cleanup = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(20) };
            cleanup.Tick += (_, _) =>
            {
                foreach (Window dialog in Application.Current.Windows.Cast<Window>().Where(window => window != owner && window.IsVisible).ToArray())
                {
                    if (!seen.Add(dialog)) continue;
                    Button? discard = Descendants<Button>(dialog).FirstOrDefault(button =>
                        Equals(button.Content, owner.FindResource("Str_Btn_DontSave")) || Equals(button.Content, owner.FindResource("Str_Btn_Quit")));
                    if (discard is not null) owner.Dispatcher.BeginInvoke(DispatcherPriority.Input,
                        (Action)(() => discard.RaiseEvent(new RoutedEventArgs(Button.ClickEvent))));
                    else dialog.Close();
                }
            };
            cleanup.Start();
            try { if (owner.IsVisible) owner.Close(); Wait(() => !owner.IsVisible, "owned deletion-window cleanup"); }
            finally { cleanup.Stop(); }
        }
    }

    private static int RunCore(Window owner, string folder, string scenario)
    {
        string[] options = scenario.Split(':');
        scenario = options[0];
        string mode = options.Length > 1 ? options[1] : "ViewSingleRadio";
        bool secondPane = options.Length > 2 && options[2] == "pane-b";
        (int[] Removed, int Active, int Target, int SurvivingOriginal, string Labels) expected = scenario switch
        {
            "first" => ([0], 0, 0, 1, "PAGE_2|PAGE_3|PAGE_4|PAGE_5"),
            "middle" => ([2], 2, 2, 3, "PAGE_1|PAGE_2|PAGE_4|PAGE_5"),
            "tail-block" => ([3, 4], 3, 2, 2, "PAGE_1|PAGE_2|PAGE_3"),
            "disjoint" => ([0, 2, 4], 0, 0, 1, "PAGE_2|PAGE_4"),
            "write-failure" => ([0, 2, 4], 0, 0, 1, "PAGE_2|PAGE_4"),
            "typed-range" => ([1, 3], 1, 1, 2, "PAGE_1|PAGE_3|PAGE_5"),
            "extract-range" => ([1, 3], 1, 1, 2, "PAGE_1|PAGE_3|PAGE_5"),
            "range-reversed" => ([1, 2, 3], 1, 1, 4, "PAGE_1|PAGE_5"),
            "range-empty" or "range-outside" or "range-cancel" or "range-switch" => ([1, 3], 1, 1, 1, "PAGE_1|PAGE_2|PAGE_3|PAGE_4|PAGE_5"),
            "all" => ([0, 1, 2, 3, 4], 0, 0, 0, "PAGE_1|PAGE_2|PAGE_3|PAGE_4|PAGE_5"),
            "last" => ([4], 4, 3, 3, "PAGE_1|PAGE_2|PAGE_3|PAGE_4"),
            "cancel" => ([4], 4, 4, 4, "PAGE_1|PAGE_2|PAGE_3|PAGE_4|PAGE_5"),
            _ => throw new ArgumentException("Unknown deletion case: " + scenario)
        };
        (byte R, byte G, byte B)[] colors = [(255, 0, 0), (0, 255, 0), (0, 0, 255), (255, 255, 0), (255, 0, 255)];
        string source = Path.Combine(folder, "delete-last-page.pdf");
        byte[] bytes = Fixture();
        File.WriteAllBytes(source, bytes);
        string otherSource = Path.Combine(folder, "untouched-pane-a.pdf");
        Rect work = SystemParameters.WorkArea;
        owner.Left = work.Left + 8; owner.Top = work.Top + 8;
        owner.Width = Math.Min(950, work.Width - 16); owner.Height = Math.Min(700, work.Height - 16);
        owner.Opacity = 1; owner.Activate();
        var pane = (FrameworkElement)owner.FindName("Viewer");
        var paneA = pane;
        using var focusTrace = options.Length > 1 ? new FocusTrace(owner) : null;
        if (secondPane)
        {
            File.WriteAllBytes(otherSource, bytes);
            owner.GetType().GetMethod("OpenFromExternal", BindingFlags.Public | BindingFlags.Instance)!.Invoke(owner, [otherSource]);
            Wait(() => PageNavigationModesSmoke.ColorPresent(paneA, 255, 0, 0), "untouched pane A source");
            ShortcutHintsSmoke.Key(owner, 0x79);
            pane = (FrameworkElement)owner.FindName("ViewerB");
            Wait(() => pane.IsVisible && pane.ActualWidth > 100, "deletion pane B");
            ShortcutHintsSmoke.ClickAt(owner, PageNavigationFocusProbe.NeutralPaneFocusPoint(owner, pane), expectedPane: pane);
        }
        string switchedSource = Path.Combine(folder, "range-switched-source.pdf");
        byte[] switchedBytes = TableExportSmoke.Fixture();
        if (scenario == "range-switch") File.WriteAllBytes(switchedSource, switchedBytes);
        if (Read(source) != (5, "PAGE_1|PAGE_2|PAGE_3|PAGE_4|PAGE_5")) throw new InvalidOperationException("Public five-page fixture control failed.");
        owner.GetType().GetMethod("OpenFromExternal", BindingFlags.Public | BindingFlags.Instance)!.Invoke(owner, [source]);
        var pages = (ListBox)owner.FindName("PageList");
        var jump = (TextBox)owner.FindName("PageJumpBox");
        Wait(() => owner.IsActive && pages.Items.Count == 5, "five-page sidebar");
        ((RadioButton)owner.FindName("ViewSingleRadio")).IsChecked = true;
        bool PageColor(byte red, byte green, byte blue)
        {
            if (options.Length > 1) return PageNavigationModesSmoke.ColorPresent(pane, red, green, blue);
            ScrollViewer? viewport = Descendants<ScrollViewer>(owner).SingleOrDefault(view => view.Name == "PagePreviewPanel" && view.IsVisible);
            if (viewport is null) return false;
            foreach (Image image in Descendants<Image>(viewport).Where(image => image.IsVisible))
            {
                if (image.Source is not BitmapSource bitmap || bitmap.PixelWidth < 20 || bitmap.PixelHeight < 20) continue;
                BitmapSource sample = bitmap.Format == PixelFormats.Bgra32 || bitmap.Format == PixelFormats.Pbgra32
                    ? bitmap : new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
                byte[] pixel = new byte[4];
                sample.CopyPixels(new Int32Rect(sample.PixelWidth / 2, sample.PixelHeight / 4, 1, 1), pixel, 4, 0);
                if (Math.Abs(pixel[2] - red) < 8 && Math.Abs(pixel[1] - green) < 8 && Math.Abs(pixel[0] - blue) < 8) return true;
            }
            return false;
        }
        Wait(() => pages.SelectedIndex == 0 && PageColor(255, 0, 0), "first-page rendered presence control");
        if (PageColor(255, 0, 255)) throw new InvalidOperationException("Page-color absence control confused the first and final page.");
        ((RadioButton)owner.FindName(mode)).IsChecked = true;
        long modeStart = Environment.TickCount64;
        Wait(() => ((RadioButton)owner.FindName(mode)).IsChecked == true
            && Descendants<ScrollViewer>(pane).Any(view => view.Name == "PagePreviewPanel" && view.IsVisible && view.Opacity > .99)
            && Environment.TickCount64 - modeStart >= 200, "requested deletion view mode settled");
        pages.SelectedIndex = expected.Active;
        Wait(() => pages.SelectedIndex == expected.Active && jump.Text == (expected.Active + 1).ToString(CultureInfo.InvariantCulture)
            && PageColor(colors[expected.Active].R, colors[expected.Active].G, colors[expected.Active].B), "displayed target page");
        foreach (int index in expected.Removed.Where(index => index != expected.Active)) pages.SelectedItems.Add(pages.Items[index]);
        Wait(() => pages.SelectedItems.Cast<object>().Select(item => pages.Items.IndexOf(item)).Order().SequenceEqual(expected.Removed.Order())
            && jump.Text == (expected.Active + 1).ToString(CultureInfo.InvariantCulture)
            && PageColor(colors[expected.Active].R, colors[expected.Active].G, colors[expected.Active].B)
            && ((Button)owner.FindName("DeletePagesBtn")).IsEnabled, "rendered target, exact selected set and Delete availability");
        Console.WriteLine($"CONTROL case={scenario}, mode={mode}, pane={pane.Name}, active={expected.Active}, selected={string.Join(',', expected.Removed)}; initial rendered color controls passed.");
        int confirmations = 0;
        int rangePrompts = 0;
        bool typedRange = scenario is "typed-range" or "extract-range" || scenario.StartsWith("range-", StringComparison.Ordinal);
        bool rejectedRange = scenario is "range-empty" or "range-outside";
        string extracted = Path.Combine(folder, "extracted-range.pdf");
        int failures = 0;
        bool denyWrite = scenario == "write-failure";
        DirectoryInfo? sessionDirectory = null;
        string? originalSddl = null;
        if (denyWrite)
        {
            string tempRoot = Path.Combine(folder, "data", "Temp");
            sessionDirectory = new DirectoryInfo(Directory.GetDirectories(tempRoot, "session-*").Single());
            originalSddl = sessionDirectory.GetAccessControl(AccessControlSections.Access)
                .GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        }
        void RestoreAccess()
        {
            if (sessionDirectory is null || originalSddl is null) return;
            var restored = new DirectorySecurity();
            restored.SetSecurityDescriptorSddlForm(originalSddl, AccessControlSections.Access);
            sessionDirectory.SetAccessControl(restored);
            if (sessionDirectory.GetAccessControl(AccessControlSections.Access)
                    .GetSecurityDescriptorSddlForm(AccessControlSections.Access) != originalSddl)
                throw new InvalidOperationException("Delete failure fixture did not restore its exact temporary-directory permissions.");
        }
        var seen = new HashSet<Window>();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) =>
        {
            foreach (Window dialog in Application.Current.Windows.Cast<Window>().Where(window => window != owner && window.IsVisible).ToArray())
            {
                if (scenario == "extract-range" && dialog.GetType().Name == "FileDialog")
                {
                    if (!seen.Add(dialog)) continue;
                    if (rangePrompts == 0) { dialog.Close(); continue; }
                    ((TextBox)dialog.FindName("FileNameBox")).Text = extracted;
                    Button accept = (Button)dialog.FindName("AcceptButton");
                    owner.Dispatcher.BeginInvoke(DispatcherPriority.Input,
                        (Action)(() => accept.RaiseEvent(new RoutedEventArgs(Button.ClickEvent))));
                    continue;
                }
                string dialogType = dialog.GetType().Name;
                // Extract Pages' typed page list is one of several boxes in its window; Delete has only the one.
                TextBox? range = dialogType == "SplitDialog"
                    ? Descendants<TextBox>(dialog).SingleOrDefault(box => System.Windows.Automation.AutomationProperties.GetAutomationId(box) == "SplitPageList")
                    : dialogType == "DeletePagesDialog" ? Descendants<TextBox>(dialog).SingleOrDefault() : null;
                if (range is not null && seen.Add(dialog))
                {
                    rangePrompts++;
                    string expectedPrefill = string.Join(',', expected.Removed.Select(index => index + 1));
                    if (range.Text != expectedPrefill) throw new InvalidOperationException("The page range prompt did not prefill the selected pages.");
                    if (typedRange) range.Text = scenario switch
                    {
                        "range-reversed" => "4-2,2",
                        "range-empty" => "",
                        "range-outside" => "6",
                        _ => "4,2,2"
                    };
                    if (scenario == "range-switch")
                        owner.GetType().GetMethod("OpenFromExternal", BindingFlags.Public | BindingFlags.Instance)!
                            .Invoke(owner, [switchedSource]);
                    // The one-step Delete dialog is also the confirmation, and it refuses an unusable range in place.
                    bool refused = dialogType == "DeletePagesDialog" && (rejectedRange || scenario == "all");
                    if (refused)
                    {
                        string problem = (string)owner.FindResource(scenario == "all" ? "Str_Dlg_KeepOnePage" : "Str_InvalidRange");
                        if (Descendants<Button>(dialog).Single(button => button.IsDefault).IsEnabled
                            || !Descendants<TextBlock>(dialog).Any(text => text.IsVisible && text.Text == problem))
                            throw new InvalidOperationException("The Delete dialog did not refuse the range in place.");
                        failures++;
                        Console.WriteLine("CONTROL Delete refused in the dialog: " + problem);
                    }
                    else if (dialogType == "DeletePagesDialog" && scenario is not ("range-cancel" or "range-switch"))
                    {
                        confirmations++;
                        if (denyWrite)
                        {
                            DirectorySecurity denied = sessionDirectory!.GetAccessControl(AccessControlSections.Access);
                            denied.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!,
                                FileSystemRights.CreateFiles, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Deny));
                            sessionDirectory!.SetAccessControl(denied);
                        }
                    }
                    bool dismiss = refused || scenario is "range-cancel" or "cancel";
                    Button apply = Descendants<Button>(dialog).Single(button => dismiss ? button.IsCancel : button.IsDefault);
                    owner.Dispatcher.BeginInvoke(DispatcherPriority.Input,
                        (Action)(() => apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent))));
                    continue;
                }
                Button? ok = Descendants<Button>(dialog).FirstOrDefault(button => Equals(button.Content, owner.FindResource("Str_Btn_OK")));
                if (denyWrite && ok is not null && seen.Add(dialog))
                {
                    failures++;
                    Console.WriteLine("CONTROL Delete failure: " + string.Join(" ", Descendants<TextBlock>(dialog).Select(text => text.Text)));
                    owner.Dispatcher.BeginInvoke(DispatcherPriority.Input,
                        (Action)(() => ok.RaiseEvent(new RoutedEventArgs(Button.ClickEvent))));
                    continue;
                }
            }
        };
        timer.Start();
        try { Click(owner, scenario == "extract-range" ? "ExtractPagesBtn" : "DeletePagesBtn"); }
        finally { timer.Stop(); RestoreAccess(); }
        Console.WriteLine($"CONTROL page action returned: ranges={rangePrompts}, confirmations={confirmations}, errors={failures}, file={((TextBlock)owner.FindName("FileNameLabel")).Text}, count={pages.Items.Count}, selected={pages.SelectedIndex}, counter={jump.Text}, dirty={Dirty(owner)}.");
        if (typedRange && rangePrompts != 1)
            throw new InvalidOperationException("USER-004: " + (scenario == "extract-range" ? "Extract" : "Delete") + " did not offer a typed page range before confirmation.");
        if (scenario == "range-switch")
        {
            Wait(() => pages.Items.Count == 1 && ((TextBlock)owner.FindName("FileNameLabel")).Text == Path.GetFileName(switchedSource), "replacement document after range dialog");
            if (confirmations != 0 || failures != 0 || Dirty(owner) || ((Button)owner.FindName("UndoBtn")).IsEnabled
                || !File.ReadAllBytes(source).AsSpan().SequenceEqual(bytes)
                || !File.ReadAllBytes(switchedSource).AsSpan().SequenceEqual(switchedBytes))
                throw new InvalidOperationException("USER-004: an old range confirmation changed a replacement document.");
            const string switchedResult = "PASS verify-delete-nearest: switching documents during range entry prevents deletion and preserves both source files and clean history.";
            File.WriteAllText(Path.Combine(folder, "result.txt"), switchedResult); Console.WriteLine(switchedResult);
            return 0;
        }
        if (rejectedRange || scenario == "range-cancel")
        {
            if (confirmations != 0 || failures != (rejectedRange ? 1 : 0) || pages.Items.Count != 5
                || pages.SelectedIndex != expected.Active || !PageColor(0, 255, 0) || Dirty(owner)
                || ((Button)owner.FindName("UndoBtn")).IsEnabled || !File.ReadAllBytes(source).AsSpan().SequenceEqual(bytes))
                throw new InvalidOperationException("USER-004: rejected or canceled range changed document state or reached deletion confirmation.");
            string refusedRange = "PASS verify-delete-nearest: " + scenario + " preserves the document, visible page, source and clean history without deletion confirmation.";
            File.WriteAllText(Path.Combine(folder, "result.txt"), refusedRange); Console.WriteLine(refusedRange);
            return 0;
        }
        if (scenario == "extract-range")
        {
            if (!File.Exists(extracted) || Read(extracted) != (2, "PAGE_2|PAGE_4") || pages.Items.Count != 5
                || Dirty(owner) || ((Button)owner.FindName("UndoBtn")).IsEnabled
                || !File.ReadAllBytes(source).AsSpan().SequenceEqual(bytes))
                throw new InvalidOperationException("USER-004: typed extraction lost page order or changed source content/history.");
            const string extractedResult = "PASS verify-delete-nearest: typed Extract normalizes duplicate and out-of-order input into source order and preserves the original document and history.";
            File.WriteAllText(Path.Combine(folder, "result.txt"), extractedResult); Console.WriteLine(extractedResult);
            return 0;
        }
        if (scenario == "all")
        {
            Console.WriteLine($"CONTROL all-page Delete: confirmations={confirmations}, notices={failures}, pages={pages.Items.Count}, dirty={Dirty(owner)}.");
            if (pages.Items.Count != 5 || pages.SelectedIndex != expected.Active || Dirty(owner)
                || ((Button)owner.FindName("UndoBtn")).IsEnabled || !PageColor(255, 0, 0)
                || !File.ReadAllBytes(source).AsSpan().SequenceEqual(bytes)
                || Read(source) != (5, "PAGE_1|PAGE_2|PAGE_3|PAGE_4|PAGE_5"))
                throw new InvalidOperationException("USER-004: deleting every page discarded the usable document or changed its clean history.");
            const string refused = "PASS verify-delete-nearest: all-page Delete preserves the usable document, rendered page, original bytes and clean history.";
            File.WriteAllText(Path.Combine(folder, "result.txt"), refused); Console.WriteLine(refused);
            return 0;
        }
        if (denyWrite)
        {
            if (confirmations != 1 || failures != 1 || pages.Items.Count != 5 || pages.SelectedIndex != expected.Active
                || Dirty(owner) || ((Button)owner.FindName("UndoBtn")).IsEnabled
                || !PageColor(255, 0, 0) || !File.ReadAllBytes(source).AsSpan().SequenceEqual(bytes)
                || Read(source) != (5, "PAGE_1|PAGE_2|PAGE_3|PAGE_4|PAGE_5"))
                throw new InvalidOperationException("APP-004: failed Delete changed the usable page set, selection, source, dirty state or history.");
            Console.WriteLine("CONTROL confirmed Delete failed once and preserved all pages, rendered selection, source bytes and clean history; exact permissions restored.");
            denyWrite = false;
            confirmations = 0;
            timer.Start();
            try { Click(owner, "DeletePagesBtn"); }
            finally { timer.Stop(); }
        }
        int remaining = expected.Labels.Split('|').Length;
        try { Wait(() => pages.Items.Count == remaining && pages.SelectedIndex >= 0, "completed page deletion"); }
        catch
        {
            Console.WriteLine($"CONTROL incomplete deletion: file={((TextBlock)owner.FindName("FileNameLabel")).Text}, count={pages.Items.Count}, selected={pages.SelectedIndex}, counter={jump.Text}, dirty={Dirty(owner)}, expectedCount={remaining}, sourceUnchanged={File.ReadAllBytes(source).AsSpan().SequenceEqual(bytes)}.");
            try
            {
                owner.UpdateLayout();
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(owner.ActualWidth), (int)Math.Ceiling(owner.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(owner);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var output = File.Create(Path.Combine(folder, "delete-incomplete.png")); encoder.Save(output);
            }
            catch (Exception evidenceError) { Console.Error.WriteLine("Deletion evidence capture also failed: " + evidenceError); }
            throw;
        }
        Console.WriteLine($"CONTROL deletion: confirmations={confirmations}, selectedIndex={pages.SelectedIndex}, pageCounter={jump.Text}, remaining={pages.Items.Count}.");
        if (confirmations != 1) throw new InvalidOperationException("Expected one real Delete confirmation.");
        if (!File.ReadAllBytes(source).AsSpan().SequenceEqual(bytes)) throw new InvalidOperationException("Delete changed the source before Save.");
        var survivorColor = colors[expected.SurvivingOriginal];
        Console.WriteLine($"CONTROL immediate survivor bitmap={PageColor(survivorColor.R, survivorColor.G, survivorColor.B)}, expected original page={expected.SurvivingOriginal + 1}.");
        Wait(() => pages.SelectedIndex == expected.Target && jump.Text == (expected.Target + 1).ToString(CultureInfo.InvariantCulture)
            && PageColor(survivorColor.R, survivorColor.G, survivorColor.B), "USER-006 rendered nearest survivor in " + scenario);
        if (pages.SelectedIndex != expected.Target || jump.Text != (expected.Target + 1).ToString(CultureInfo.InvariantCulture)
            || !PageColor(survivorColor.R, survivorColor.G, survivorColor.B))
            throw new InvalidOperationException("USER-006: deletion jumped away from the nearest surviving page in " + scenario + ".");
        if (scenario == "cancel")
        {
            if (Dirty(owner) || ((Button)owner.FindName("UndoBtn")).IsEnabled
                || Read(source) != (5, "PAGE_1|PAGE_2|PAGE_3|PAGE_4|PAGE_5"))
                throw new InvalidOperationException("Cancel changed document content or history.");
            const string canceled = "PASS verify-delete-nearest: Cancel preserves all five pages, rendered page5, source bytes and clean history.";
            File.WriteAllText(Path.Combine(folder, "result.txt"), canceled); Console.WriteLine(canceled);
            return 0;
        }
        Save(owner);
        if (Read(source) != (remaining, expected.Labels)) throw new InvalidOperationException("Strict saved deletion did not retain the expected surviving pages.");
        Wait(() => ((Button)owner.FindName("UndoBtn")).IsEnabled, "Delete Undo availability");
        Click(owner, "UndoBtn");
        Wait(() => pages.Items.Count == 5, "Delete Undo page set");
        Save(owner);
        if (Read(source) != (5, "PAGE_1|PAGE_2|PAGE_3|PAGE_4|PAGE_5")) throw new InvalidOperationException("Delete Undo did not restore the original page set.");
        Wait(() => ((Button)owner.FindName("RedoBtn")).IsEnabled, "Delete Redo availability");
        Click(owner, "RedoBtn");
        Wait(() => pages.Items.Count == remaining, "Delete Redo page set");
        Save(owner);
        if (Read(source) != (remaining, expected.Labels)) throw new InvalidOperationException("Delete Redo did not restore the surviving page set.");
        if (options.Length > 1 && ((RadioButton)owner.FindName(mode)).IsChecked != true)
            throw new InvalidOperationException("Delete or history replay changed the requested view mode.");
        if (secondPane)
        {
            ShortcutHintsSmoke.ClickAt(owner, PageNavigationFocusProbe.NeutralPaneFocusPoint(owner, paneA), expectedPane: paneA);
            Wait(() => pages.Items.Count == 5 && jump.Text == "1" && !Dirty(owner)
                && PageNavigationModesSmoke.ColorPresent(paneA, 255, 0, 0), "pane A unchanged after pane B Delete and history replay");
            if (!File.ReadAllBytes(otherSource).AsSpan().SequenceEqual(bytes)
                || ((Button)owner.FindName("UndoBtn")).IsEnabled)
                throw new InvalidOperationException("Pane B deletion changed pane A source or history.");
            ShortcutHintsSmoke.ClickAt(owner, PageNavigationFocusProbe.NeutralPaneFocusPoint(owner, pane), expectedPane: pane);
            Wait(() => pages.Items.Count == remaining && !Dirty(owner)
                && ((RadioButton)owner.FindName(mode)).IsChecked == true, "pane B saved state restored after focus switch");
            Console.WriteLine("CONTROL pane B Delete, Save, Undo and Redo preserved independent pane A source, visible page, clean state and history.");
        }
        string result = "PASS verify-delete-nearest: " + scenario + " retains the nearest survivor; strict Save, Undo and Redo preserve every expected page label.";
        File.WriteAllText(Path.Combine(folder, "result.txt"), result);
        Console.WriteLine(result);
        return 0;
    }

    private sealed class FocusTrace : IDisposable
    {
        private readonly Window _owner;
        private readonly KeyboardFocusChangedEventHandler _handler;
        internal FocusTrace(Window owner)
        {
            _owner = owner;
            _handler = (_, args) =>
            {
                string Describe(IInputElement? value)
                {
                    var path = new List<string>();
                    for (DependencyObject? node = value as DependencyObject; node is not null; node = node is Visual
                        ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
                    {
                        if (node is FrameworkElement { Name.Length: > 0 } element) path.Add(element.GetType().Name + ":" + element.Name);
                        if (ReferenceEquals(node, owner)) break;
                    }
                    return string.Join('>', path);
                }
                Console.WriteLine($"CONTROL deletion keyboard focus: old={Describe(args.OldFocus)}, new={Describe(args.NewFocus)}, file={((TextBlock)owner.FindName("FileNameLabel")).Text}.");
            };
            owner.AddHandler(Keyboard.GotKeyboardFocusEvent, _handler, handledEventsToo: true);
        }
        public void Dispose() => _owner.RemoveHandler(Keyboard.GotKeyboardFocusEvent, _handler);
    }

    internal static (int Count, string Labels) Read(string path)
    {
        Assembly engine = Assembly.Load("KillerPdf.Engine");
        object document = engine.GetType("KillerPdf.Engine.Documents.PdfDocument", true)!
            .GetMethod("Open", [typeof(ReadOnlyMemory<byte>)])!.Invoke(null, [new ReadOnlyMemory<byte>(File.ReadAllBytes(path))])!;
        int count = ((System.Collections.IEnumerable)engine.GetType("KillerPdf.Engine.Documents.PdfPageInformation", true)!
            .GetMethod("Read")!.Invoke(null, [document])!).Cast<object>().Count();
        object reader = Activator.CreateInstance(engine.GetType("KillerPdf.Engine.Documents.PdfPageContentReader", true)!, [document])!;
        var labels = new List<string>();
        for (int page = 0; page < count; page++)
        {
            object content = reader.GetType().GetMethod("Read", [typeof(int), typeof(CancellationToken)])!.Invoke(reader, [page, CancellationToken.None])!;
            labels.Add(((string)content.GetType().GetProperty("Text")!.GetValue(content)!).Trim());
        }
        return (count, string.Join('|', labels));
    }

    internal static byte[] Fixture(int pageCount = 5)
    {
        if (pageCount is < 1 or > 5) throw new ArgumentOutOfRangeException(nameof(pageCount));
        List<string> objects = ["<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [" + string.Join(" ", Enumerable.Range(0, pageCount).Select(page => $"{4 + page * 2} 0 R")) + $"] /Count {pageCount} >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"];
        for (int page = 0; page < pageCount; page++)
        {
            string[] colors = ["1 0 0", "0 1 0", "0 0 1", "1 1 0", "1 0 1"];
            string content = $"{colors[page]} rg 0 0 200 200 re f 0 g BT /F1 20 Tf 20 100 Td (PAGE_{page + 1}) Tj ET\n";
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F1 3 0 R >> >> /Contents {5 + page * 2} 0 R >>");
            objects.Add($"<< /Length {content.Length} >>\nstream\n{content}endstream");
        }
        var pdf = new StringBuilder("%PDF-1.7\n");
        List<int> offsets = [0];
        for (int index = 0; index < objects.Count; index++) { offsets.Add(pdf.Length); pdf.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n"); }
        int xref = pdf.Length;
        pdf.Append($"xref\n0 {offsets.Count}\n0000000000 65535 f \n");
        foreach (int offset in offsets.Skip(1)) pdf.Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        pdf.Append($"trailer\n<< /Size {offsets.Count} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
