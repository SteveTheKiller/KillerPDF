using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

public static class TextAnnotationReopenSmoke
{
    private const string WholeText = "First line\nSecond line";

    public static int Run(Window owner, string appPath, string appXaml, string folder, bool verifySave = false)
    {
        string stage = "starting";
        string? failedAt = null;
        Exception? failure = null;
        var events = new List<object>();
        var hashes = new Dictionary<string, string>();
        var modals = new HashSet<Window>();
        string sourcePath = Path.Combine(folder, "ordinary-multiline.pdf");
        byte[] original = [];
        const string editedText = "First line\nEdited line";
        string savedPath = Path.Combine(folder, "ordinary-multiline-edited.pdf");
        byte[] savedBytes = [];
        bool saveDialogExpected = false, saveDialogAccepted = false, originalSessionClosed = false;
        bool pickerBusy = false;
        Rect priorPickerBounds = Rect.Empty;
        int pickerStable = 0;
        double totalSeconds = verifySave ? 180 : 100;
        nint hwnd = 0;
        bool preserved = false, closed = false;
        int assertions = 0;
        Application? app = Application.Current;
        var overall = Stopwatch.StartNew();
        var timer = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(50) };
        DispatcherUnhandledExceptionEventHandler dispatcherFailure = (_, e) => { Fail(e.Exception); e.Handled = true; };
        timer.Tick += (_, _) =>
        {
            if (overall.Elapsed > TimeSpan.FromSeconds(totalSeconds)) Fail(new TimeoutException("Overall deadline expired."));
            if (app is null) return;
            foreach (Window window in app.Windows.Cast<Window>().Where(w => w != owner && w.IsVisible).ToArray())
            {
                if (verifySave && saveDialogExpected && window.Owner == owner
                    && window.GetType().FullName == "KillerPDF.Controls.FileDialog"
                    && Equals(window.Title, app.TryFindResource("Str_Dlg_SavePdfAs")))
                {
                    if (pickerBusy || saveDialogAccepted) continue;
                    pickerBusy = true;
                    try
                    {
                        window.UpdateLayout();
                        if (Named<TextBox>(window, "FileNameBox") is { IsVisible: true, IsEnabled: true } nameBox
                            && Named<Button>(window, "AcceptButton") is { IsVisible: true, IsEnabled: true } accept)
                        {
                            nint dialogHwnd = new WindowInteropHelper(window).Handle;
                            if (dialogHwnd == 0) continue;
                            if (GetForegroundWindow() != dialogHwnd) SetForegroundWindow(dialogHwnd);
                            if (GetForegroundWindow() != dialogHwnd || !SystemParameters.WorkArea.Contains(Bounds(nameBox))
                                || !SystemParameters.WorkArea.Contains(Bounds(accept))) { pickerStable = 0; continue; }
                            Rect pickerBounds = Bounds(window);
                            pickerStable = pickerBounds == priorPickerBounds ? pickerStable + 1 : 0;
                            priorPickerBounds = pickerBounds;
                            if (pickerStable < 3) continue;
                            Point center = accept.PointToScreen(new Point(accept.ActualWidth / 2, accept.ActualHeight / 2));
                            if (GetAncestor(WindowFromPoint(new((int)Math.Round(center.X), (int)Math.Round(center.Y))), 2) != dialogHwnd)
                            { pickerStable = 0; continue; }
                            if (new TextBoxAutomationPeer(nameBox).GetPattern(PatternInterface.Value) is not IValueProvider value
                                || new ButtonAutomationPeer(accept).GetPattern(PatternInterface.Invoke) is not IInvokeProvider invoke)
                                throw new InvalidOperationException("Expected Save As public control patterns absent.");
                            value.SetValue(savedPath);
                            saveDialogAccepted = true;
                            events.Add(new { stage, expectedPicker = window.GetType().FullName, window.Title, savedPath, dialogHandle = dialogHwnd.ToInt64() });
                            invoke.Invoke();
                        }
                    }
                    catch (Exception error) { Fail(error); try { window.Close(); } catch { } }
                    finally { pickerBusy = false; }
                    continue;
                }
                if (modals.Add(window)) events.Add(new { stage, unexpectedWindow = window.GetType().FullName, window.Title });
                Fail(new InvalidOperationException("Unexpected modal at " + stage));
                try { window.Close(); } catch (Exception error) { Fail(error); }
            }
        };
        Directory.CreateDirectory(folder);
        try
        {
            Stage("vm-guard");
            if (!new[] { "SN01", "SN02" }.Contains(Environment.MachineName, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("Physical input requires an exclusively owned SN01 or SN02.");
            if (app is null) throw new InvalidOperationException("Isolated application absent.");
            app.DispatcherUnhandledException += dispatcherFailure;
            timer.Start();
            Stage("binary-identities");
            hashes["app"] = Hash(File.ReadAllBytes(appPath));
            hashes["appXaml"] = Hash(File.ReadAllBytes(appXaml));
            hashes["driver"] = Hash(File.ReadAllBytes(typeof(TextAnnotationReopenSmoke).Assembly.Location));
            Assembly engine = Assembly.Load("KillerPdf.Engine");
            hashes["engine"] = SelectedAssemblyIdentity.HashLoaded(engine, appPath);
            hashes["loadedApp"] = SelectedAssemblyIdentity.HashLoaded(owner.GetType().Assembly, appPath);
            Stage("public-fixture-construction");
            original = Fixture(engine, owner.GetType().Assembly);
            hashes["source"] = Hash(original);
            File.WriteAllBytes(sourcePath, original);
            if (!Blue(Solid(true)) || Blue(Solid(false))) throw new InvalidOperationException("Independent blue detector controls failed.");
            owner.WindowState = WindowState.Normal;
            owner.Width = Math.Min(1200, SystemParameters.WorkArea.Width - 48);
            owner.Height = Math.Min(850, SystemParameters.WorkArea.Height - 48);
            if (!owner.IsVisible) owner.Show();
            owner.Activate();
            hwnd = new WindowInteropHelper(owner).Handle;
            if (!SetForegroundWindow(hwnd)) throw new InvalidOperationException("Owned window cannot be foregrounded.");
            Stage("external-open");
            Call(owner, "OpenFromExternal", sourcePath);
            Stage("single-view-control-readiness");
            Wait(() => Named<RadioButton>(owner, "ViewSingleRadio") is { IsVisible: true, IsEnabled: true });
            RadioButton single = Named<RadioButton>(owner, "ViewSingleRadio");
            if (new RadioButtonAutomationPeer(single).GetPattern(PatternInterface.SelectionItem) is not ISelectionItemProvider selection)
                throw new InvalidOperationException("Single view selection provider absent.");
            selection.Select();
            Stage("loaded-page-readiness");
            Rect previous = Rect.Empty;
            int stable = 0;
            Wait(() =>
            {
                owner.UpdateLayout();
                ListBox pages = Named<ListBox>(owner, "PageList");
                ScrollViewer viewport = Named<ScrollViewer>(owner, "PagePreviewPanel");
                if (Named<TextBlock>(owner, "FileNameLabel").Text != Path.GetFileName(sourcePath)
                    || pages.Items.Count != 1 || pages.SelectedIndex != 0 || single.IsChecked != true
                    || !viewport.IsVisible || viewport.ActualWidth < 100 || viewport.ActualHeight < 100) return false;
                Rect bounds = Bounds(viewport);
                stable = bounds == previous ? stable + 1 : 0;
                previous = bounds;
                return stable >= 20;
            });
            Stage("rendered-page-control");
            Rect visibleViewport = Rect.Intersect(Bounds(owner), Bounds(Named<ScrollViewer>(owner, "PagePreviewPanel")));
            Wait(() => Blue(Capture(visibleViewport, null)));
            Capture(visibleViewport, "opened-viewport");
            Stage("select-tool-provider");
            Wait(() => Named<Button>(owner, "ToolSelectBtn") is { IsVisible: true, IsEnabled: true });
            Button selectButton = Named<Button>(owner, "ToolSelectBtn");
            if (new ButtonAutomationPeer(selectButton).GetPattern(PatternInterface.Invoke) is not IInvokeProvider selectTool)
                throw new InvalidOperationException("Select tool invocation provider absent.");
            selectTool.Invoke();
            Stage("restored-multiline-overlay");
            // Allow asynchronous overlay presentation to settle before declaring product absence.
            Poll(() => Visible<TextBlock>().Any(t => Normalize(t.Text) == WholeText), TimeSpan.FromSeconds(8));
            TextBlock[] restored = Visible<TextBlock>().Where(t => Normalize(t.Text) == WholeText).ToArray();
            Product(restored.Length == 1, "Opening the saved ordinary text annotation must restore exactly one full multiline overlay.");
            TextBlock label = restored[0];
            Rect labelBounds = Bounds(label);
            Product(visibleViewport.Contains(labelBounds), "The restored whole text overlay must be inside the visible viewport.");
            events.Add(new { stage, label = label.Text, labelType = label.GetType().FullName, bounds = labelBounds.ToString() });
            Stage("restored-box-geometry");
            Image pageImage = Descendants<Image>(Named<ScrollViewer>(owner, "PagePreviewPanel"))
                .Single(image => image.IsVisible && image.Source is BitmapSource && image.ActualWidth > 100 && image.ActualHeight > 100);
            if (VisualTreeHelper.GetParent(label) is not Border textBoxFrame)
                throw new InvalidOperationException("Restored text label has no public box frame.");
            Rect pageBounds = Bounds(pageImage), boxBounds = Bounds(textBoxFrame);
            Rect expectedBox = new(pageBounds.Left + pageBounds.Width * .2, pageBounds.Top + pageBounds.Height * .2,
                pageBounds.Width * .6, pageBounds.Height / 6);
            events.Add(new { stage, page = pageBounds.ToString(), box = boxBounds.ToString(), expected = expectedBox.ToString() });
            Product(Math.Abs(boxBounds.Left - expectedBox.Left) <= 2 && Math.Abs(boxBounds.Top - expectedBox.Top) <= 2
                && Math.Abs(boxBounds.Right - expectedBox.Right) <= 2 && Math.Abs(boxBounds.Bottom - expectedBox.Bottom) <= 2,
                "Reopened text must retain the source box position and size in the visible page.");
            Stage("text-tool-provider");
            Wait(() => Named<Button>(owner, "ToolTextBtn") is { IsVisible: true, IsEnabled: true });
            if (new ButtonAutomationPeer(Named<Button>(owner, "ToolTextBtn")).GetPattern(PatternInterface.Invoke) is not IInvokeProvider textTool)
                throw new InvalidOperationException("Text tool invocation provider absent.");
            textTool.Invoke();
            Rect priorTextBounds = Rect.Empty;
            int textStable = 0;
            Wait(() =>
            {
                TextBlock? current = Visible<TextBlock>().SingleOrDefault(t => Normalize(t.Text) == WholeText);
                if (current is null) return false;
                Rect currentBounds = Bounds(current);
                textStable = currentBounds == priorTextBounds ? textStable + 1 : 0;
                priorTextBounds = currentBounds;
                label = current;
                return textStable >= 12;
            });
            visibleViewport = Rect.Intersect(Bounds(owner), Bounds(Named<ScrollViewer>(owner, "PagePreviewPanel")));
            Stage("actual-owned-edit-click");
            Point target = label.PointToScreen(new Point(Math.Min(12, label.ActualWidth / 2), Math.Min(10, label.ActualHeight / 2)));
            int x = (int)Math.Round(target.X), y = (int)Math.Round(target.Y);
            VerifyPoint(x, y);
            nint at = WindowFromPoint(new NativePoint(x, y));
            HwndSource source = HwndSource.FromHwnd(at) ?? throw new InvalidOperationException("Click target has no public WPF presentation source.");
            if (source.RootVisual is not UIElement root) throw new InvalidOperationException("Click presentation root is not interactive.");
            DependencyObject? hit = root.InputHitTest(root.PointFromScreen(new Point(x, y))) as DependencyObject;
            bool onLabelBranch = false;
            for (DependencyObject? node = label; node is Visual; node = VisualTreeHelper.GetParent(node))
                if (ReferenceEquals(node, hit) && node is not Window) onLabelBranch = true;
            if (!onLabelBranch || hit is ButtonBase || hit is TextBox)
                throw new InvalidOperationException("Exact rounded click pixel did not hit the noninteractive restored overlay branch.");
            if (!SetCursorPos(x, y)) throw new System.ComponentModel.Win32Exception();
            VerifyPoint(x, y);
            INPUT[] input = [Mouse(0x0002), Mouse(0x0004)];
            if (SendInput(2, input, Marshal.SizeOf<INPUT>()) != 2) throw new System.ComponentModel.Win32Exception();
            Stage("native-whole-text-editor");
            Poll(() => Visible<TextBox>().Any(t => t.AcceptsReturn && !t.IsReadOnly && Normalize(t.Text) == WholeText), TimeSpan.FromSeconds(8));
            TextBox[] editors = Visible<TextBox>().Where(t => t.AcceptsReturn && !t.IsReadOnly && Normalize(t.Text) == WholeText).ToArray();
            Product(editors.Length == 1, "The Text tool must open one editable native TextBox containing both complete lines.");
            Product(visibleViewport.Contains(Bounds(editors[0])), "The native editor must be wholly visible.");
            Capture(Bounds(owner), "whole-text-editor-window");
            events.Add(new { stage, text = editors[0].Text, editors[0].AcceptsReturn, editors[0].IsReadOnly, editors[0].IsKeyboardFocusWithin });
            Stage("complete-first-vertical-tracer");
            if (verifySave)
            {
                Stage("physical-whole-text-edit");
                if (File.Exists(savedPath)) throw new InvalidOperationException("Owned Save As output already exists; use a fresh run folder.");
                PhysicalClick(editors[0], requireEditor: true);
                Wait(() => editors[0].IsKeyboardFocusWithin);
                Shortcut(0x41, shift: false);
                foreach (char character in editedText)
                {
                    CheckInputOwner();
                    Send(character == '\n' ? [Key(0x0D), Key(0x0D, 2)] : [Unicode(character), Unicode(character, 6)]);
                }
                Wait(() => Normalize(editors[0].Text) == editedText);
                Product(!editors[0].Text.Contains("Second", StringComparison.Ordinal), "Physical editor replacement must remove the old term.");
                Stage("select-tool-commit-edited-text");
                InvokeButton("ToolSelectBtn");
                Wait(() => !Visible<TextBox>().Any(t => t.AcceptsReturn && !t.IsReadOnly)
                    && Visible<TextBlock>().Count(t => Normalize(t.Text) == editedText) == 1);
                Stage("actual-save-as-shortcut-and-picker");
                saveDialogExpected = true;
                Shortcut(0x53, shift: true);
                Wait(() => saveDialogAccepted && !app.Windows.Cast<Window>().Any(w => w.IsVisible && w.GetType().FullName == "KillerPDF.Controls.FileDialog")
                    && File.Exists(savedPath) && Named<TextBlock>(owner, "FileNameLabel").Text == Path.GetFileName(savedPath));
                saveDialogExpected = false;
                savedBytes = File.ReadAllBytes(savedPath);
                hashes["saved"] = Hash(savedBytes);
                Stage("saved-public-pdf-model-and-appearance");
                object savedDocument = Call(engine.GetType("KillerPdf.Engine.Documents.PdfDocument", true)!, "Open", new ReadOnlyMemory<byte>(savedBytes));
                object[] savedRecords = ((IEnumerable)Call(owner.GetType().Assembly.GetType("KillerPDF.Services.PdfOverlayAnnotations", true)!, "Read", savedDocument)).Cast<object>().ToArray();
                events.Add(new { stage, savedRecordCount = savedRecords.Length, logicalText = savedRecords.Select(record =>
                    Get(record, "Annotation").GetType().GetProperty("Content", BindingFlags.Public | BindingFlags.Instance)?.GetValue(Get(record, "Annotation"))).ToArray() });
                Product(savedRecords.Length == 1 && Get(Get(savedRecords[0], "Annotation"), "Content") is string savedText
                    && Normalize(savedText) == editedText,
                    "Saved PDF must contain exactly one validated full edited overlay representation.");
                object[] savedComments = ((IEnumerable)Call(engine.GetType("KillerPdf.Engine.Documents.PdfCommentReader", true)!, "Read", savedDocument, true)).Cast<object>().ToArray();
                Product(savedComments.Count(c => Equals(Get(c, "Subtype"), "FreeText") && Get(c, "Contents") is string contents && Normalize(contents) == editedText) == 1
                    && !savedComments.Any(c => (Get(c, "Contents") as string)?.Contains("Second", StringComparison.Ordinal) == true),
                    "Current saved annotations must contain the edited logical text once and omit the old term.");
                object renderer = New(engine.GetType("KillerPdf.Engine.Rendering.PdfPageRenderer", true)!, savedDocument);
                object renderOptions = New(engine.GetType("KillerPdf.Engine.Rendering.PdfRenderOptions", true)!, 400, 600);
                object rendered = Call(renderer, "Render", 0, renderOptions);
                Product(!((IEnumerable)Get(rendered, "Diagnostics")).Cast<object>().Any(), "Saved appearance rendering must have no compatibility diagnostics.");
                byte[] renderedPixels = ((ReadOnlyMemory<byte>)Get(rendered, "Pixels")).ToArray();
                Product(Blue(BitmapSource.Create(400, 600, 96, 96, PixelFormats.Bgra32, null, renderedPixels, 1600)),
                    "The saved full-page appearance must retain the blue text drawing above the original white annotation.");
                var renderedEncoder = new PngBitmapEncoder();
                renderedEncoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(400, 600, 96, 96, PixelFormats.Bgra32, null, renderedPixels, 1600)));
                using (var renderedFile = File.Create(Path.Combine(folder, "saved-engine-appearance.png"))) renderedEncoder.Save(renderedFile);
                Stage("saved-public-application-search");
                Type search = owner.GetType().Assembly.GetType("KillerPDF.Services.SearchService", true)!;
                object editedSearch = Call(search, "Search", savedPath, "Edited", false, false);
                object oldSearch = Call(search, "Search", savedPath, "Second", false, false);
                Product((int)Get(editedSearch, "TotalHits") == 1 && (int)Get(oldSearch, "TotalHits") == 0
                    && !((IEnumerable)Get(editedSearch, "UnreadablePages")).Cast<object>().Any()
                    && !((IEnumerable)Get(oldSearch, "UnreadablePages")).Cast<object>().Any(),
                    "Public saved-file application search must find Edited once, omit Second and report no unreadable pages.");
                events.Add(new { stage, editedHits = Get(editedSearch, "TotalHits"), oldHits = Get(oldSearch, "TotalHits"), savedHash = hashes["saved"] });
                Stage("close-saved-source-session");
                if (app.ShutdownMode != ShutdownMode.OnExplicitShutdown) throw new InvalidOperationException("Fresh-owner acceptance requires the isolated explicit-shutdown host.");
                Type ownerType = owner.GetType();
                double oldLeft = owner.Left, oldTop = owner.Top, oldWidth = owner.Width, oldHeight = owner.Height;
                owner.Close();
                Wait(() => !IsWindow(hwnd));
                originalSessionClosed = true;
                Stage("fresh-owner-saved-external-open");
                owner = (Window)New(ownerType);
                owner.WindowStartupLocation = WindowStartupLocation.Manual;
                owner.Left = oldLeft; owner.Top = oldTop; owner.Width = oldWidth; owner.Height = oldHeight;
                owner.ShowInTaskbar = false; owner.Show(); owner.Activate();
                hwnd = new WindowInteropHelper(owner).Handle;
                if (!SetForegroundWindow(hwnd)) throw new InvalidOperationException("Fresh saved-file owner cannot be foregrounded.");
                Call(owner, "OpenFromExternal", savedPath);
                Wait(() => Named<RadioButton>(owner, "ViewSingleRadio") is { IsVisible: true, IsEnabled: true });
                if (new RadioButtonAutomationPeer(Named<RadioButton>(owner, "ViewSingleRadio")).GetPattern(PatternInterface.SelectionItem) is not ISelectionItemProvider reopenedSingle)
                    throw new InvalidOperationException("Fresh saved-file Single provider absent.");
                reopenedSingle.Select();
                Wait(() => Named<TextBlock>(owner, "FileNameLabel").Text == Path.GetFileName(savedPath)
                    && Named<ListBox>(owner, "PageList").Items.Count == 1 && Named<ListBox>(owner, "PageList").SelectedIndex == 0);
                Stage("fresh-owner-restored-edited-overlay");
                Poll(() => Visible<TextBlock>().Count(t => Normalize(t.Text) == editedText) == 1, TimeSpan.FromSeconds(8));
                Product(Visible<TextBlock>().Count(t => Normalize(t.Text) == editedText) == 1,
                    "The fresh saved-file owner must restore exactly one complete edited multiline overlay.");
                Stage("fresh-owner-text-tool-and-edit-click");
                InvokeButton("ToolTextBtn");
                TextBlock? savedLabel = null; Rect priorSaved = Rect.Empty; int savedStable = 0;
                Wait(() => { savedLabel = Visible<TextBlock>().SingleOrDefault(t => Normalize(t.Text) == editedText);
                    if (savedLabel is null) return false; Rect next = Bounds(savedLabel);
                    savedStable = next == priorSaved ? savedStable + 1 : 0; priorSaved = next; return savedStable >= 12; });
                PhysicalClick(savedLabel!, requireEditor: false);
                Stage("fresh-owner-whole-edited-native-editor");
                Poll(() => Visible<TextBox>().Any(t => t.AcceptsReturn && !t.IsReadOnly && Normalize(t.Text) == editedText), TimeSpan.FromSeconds(8));
                TextBox[] reopenedEditors = Visible<TextBox>().Where(t => t.AcceptsReturn && !t.IsReadOnly).ToArray();
                Product(reopenedEditors.Length == 1 && Normalize(reopenedEditors[0].Text) == editedText
                    && !reopenedEditors[0].Text.Contains("Second", StringComparison.Ordinal),
                    "A fresh owner must reopen one editable native box containing exactly both edited lines and no old term.");
                Rect savedViewport = Rect.Intersect(Bounds(owner), Bounds(Named<ScrollViewer>(owner, "PagePreviewPanel")));
                Product(savedViewport.Contains(Bounds(reopenedEditors[0])), "The fresh saved-file native editor must be wholly visible.");
                Capture(Bounds(owner), "saved-reopened-editor-window");
                Product(File.ReadAllBytes(sourcePath).AsSpan().SequenceEqual(original), "Save As must preserve the original source bytes.");
                Product(File.ReadAllBytes(savedPath).AsSpan().SequenceEqual(savedBytes), "Fresh reopening must preserve the saved output bytes.");
                File.WriteAllText(Path.Combine(folder, "save-acceptance-receipt.json"), JsonSerializer.Serialize(new
                    { stage, originalSessionClosed, savedPath, text = reopenedEditors[0].Text, hashes, assertions, events }, new JsonSerializerOptions { WriteIndented = true }));
                Stage("complete-save-reopen-acceptance");
            }
        }
        catch (Exception error)
        {
            Fail(error);
            if (hwnd != 0 && owner.IsVisible)
                try { Capture(Bounds(owner), "failure-window"); } catch (Exception captureError) { events.Add(new { captureError = captureError.ToString() }); }
        }
        finally
        {
            if (verifySave && hwnd != 0) try { ReleaseModifiers(); } catch (Exception error) { Fail(error); }
            timer.Stop();
            try
            {
                preserved = original.Length > 0 && File.Exists(sourcePath) && File.ReadAllBytes(sourcePath).AsSpan().SequenceEqual(original);
                if (original.Length > 0 && !preserved) Fail(new InvalidOperationException("Fixture source changed or disappeared."));
                if (savedBytes.Length > 0 && (!File.Exists(savedPath) || !File.ReadAllBytes(savedPath).AsSpan().SequenceEqual(savedBytes)))
                    Fail(new InvalidOperationException("Saved output changed or disappeared after acceptance."));
                foreach (Window modal in modals.Where(w => w.IsVisible).ToArray()) modal.Close();
                var discard = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(50) };
                discard.Tick += (_, _) =>
                {
                    string? label = app?.TryFindResource("Str_Btn_DontSave") as string;
                    if (label is null) return;
                    foreach (Window dialog in app?.Windows.Cast<Window>().Where(w => w != owner && w.Owner == owner && w.IsVisible).ToArray() ?? [])
                        foreach (Button button in Descendants<Button>(dialog).Where(b => b.IsVisible && b.IsEnabled
                            && string.Equals(b.Content as string, label, StringComparison.Ordinal)))
                            if (new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke) is IInvokeProvider invoke) invoke.Invoke();
                };
                discard.Start();
                try { if (hwnd != 0 && IsWindow(hwnd)) owner.Close(); }
                finally { discard.Stop(); }
                var closeDeadline = Stopwatch.StartNew();
                while (hwnd != 0 && IsWindow(hwnd) && closeDeadline.Elapsed < TimeSpan.FromSeconds(10)) { Pump(); Thread.Sleep(25); }
                closed = hwnd == 0 || !IsWindow(hwnd);
                if (!closed) Fail(new InvalidOperationException("Owned acceptance window did not close."));
            }
            catch (Exception error) { Fail(new InvalidOperationException("Acceptance teardown failed.", error)); }
            if (app is not null) app.DispatcherUnhandledException -= dispatcherFailure;
            File.WriteAllText(Path.Combine(folder, "result.json"), JsonSerializer.Serialize(new
            {
                passed = failure is null && preserved && closed,
                verdict = failure is null ? verifySave ? "GREEN_TEXT_SAVE_REOPEN" : "GREEN_TEXT_REOPEN_TRACER" : failure is ProductAssertionException ? verifySave ? "RED_TEXT_SAVE_REOPEN" : "RED_TEXT_REOPEN_TRACER" : "SETUP_FAILURE",
                stage, failedAt, failure = failure?.ToString(), assertions, preserved, closed, hashes, events,
                machine = Environment.MachineName, processId = Environment.ProcessId, ownerHandle = hwnd.ToInt64(), sourcePath,
                verifySave, savedPath, originalSessionClosed,
                scope = verifySave ? "Existing geometry and reopen tracer plus physical Unicode edit, Select commit, actual Save As shortcut/custom picker, public saved PDF model/appearance, public saved-file SearchService.Search, close original owner, fresh owner reopen and complete edited native TextBox. Search UI is not exercised."
                    : "External-open, Single provider, owned on-screen pixels, restored full text overlay, Text tool provider, physical edit click and public native TextBox. Editing and Save As are not covered."
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        return failure is null && preserved && closed ? 0 : 1;

        IEnumerable<T> Visible<T>() where T : FrameworkElement => Roots(hwnd).SelectMany(Descendants<T>)
            .Distinct().Where(t => t.IsVisible && t.IsEnabled && t.ActualWidth > 1 && t.ActualHeight > 1);
        void Wait(Func<bool> ready) { if (!Poll(ready, TimeSpan.FromSeconds(20))) throw new TimeoutException("Readiness/locator deadline expired at " + stage); }
        bool Poll(Func<bool> ready, TimeSpan timeout)
        {
            var age = Stopwatch.StartNew();
            do { Pump(); if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
                if (overall.Elapsed > TimeSpan.FromSeconds(totalSeconds)) throw new TimeoutException("Overall deadline expired at " + stage);
                if (ready()) return true; Thread.Sleep(25); } while (age.Elapsed < timeout);
            return false;
        }
        void Product(bool ok, string message) { assertions++; if (!ok) throw new ProductAssertionException(message); }
        void Stage(string value) { stage = value; File.WriteAllText(Path.Combine(folder, "stage.txt"), value); }
        void Fail(Exception error)
        {
            if (failure is not null) return;
            failedAt = stage; failure = error;
            File.WriteAllText(Path.Combine(folder, "first-failure.json"), JsonSerializer.Serialize(new
                { stage, failure = error.ToString(), assertions, hashes, events }, new JsonSerializerOptions { WriteIndented = true }));
        }
        void VerifyPoint(int x, int y)
        {
            if (GetForegroundWindow() != hwnd
                || !Bounds(owner).Contains(new Point(x, y)) || GetAncestor(WindowFromPoint(new(x, y)), 2) != hwnd)
                throw new InvalidOperationException("Physical pixel is outside the foreground owned window or obscured.");
        }
        void InvokeButton(string name)
        {
            Wait(() => Named<Button>(owner, name) is { IsVisible: true, IsEnabled: true });
            if (new ButtonAutomationPeer(Named<Button>(owner, name)).GetPattern(PatternInterface.Invoke) is not IInvokeProvider invoke)
                throw new InvalidOperationException("Public toolbar provider absent: " + name);
            invoke.Invoke();
        }
        void CheckInputOwner()
        {
            if (GetForegroundWindow() != hwnd || !owner.IsVisible || !owner.IsEnabled)
                throw new InvalidOperationException("Physical keyboard input lost the owned foreground window.");
        }
        void Shortcut(ushort key, bool shift)
        {
            CheckInputOwner();
            try { Send(shift ? [Key(0x11), Key(0x10), Key(key), Key(key, 2), Key(0x10, 2), Key(0x11, 2)]
                : [Key(0x11), Key(key), Key(key, 2), Key(0x11, 2)]); }
            finally { ReleaseModifiers(); }
        }
        void PhysicalClick(FrameworkElement element, bool requireEditor)
        {
            Point point = element.PointToScreen(new Point(Math.Min(12, element.ActualWidth / 2), Math.Min(10, element.ActualHeight / 2)));
            int px = (int)Math.Round(point.X), py = (int)Math.Round(point.Y); VerifyPoint(px, py);
            HwndSource clickSource = HwndSource.FromHwnd(WindowFromPoint(new(px, py)))
                ?? throw new InvalidOperationException("Physical edit pixel has no owned public WPF source.");
            if (clickSource.RootVisual is not UIElement clickRoot) throw new InvalidOperationException("Physical edit source has no input root.");
            DependencyObject? actual = clickRoot.InputHitTest(clickRoot.PointFromScreen(new Point(px, py))) as DependencyObject;
            bool related = false;
            if (requireEditor)
            {
                for (DependencyObject? node = actual; node is Visual; node = VisualTreeHelper.GetParent(node))
                    if (ReferenceEquals(node, element)) related = true;
            }
            else
            {
                for (DependencyObject? node = element; node is Visual; node = VisualTreeHelper.GetParent(node))
                    if (ReferenceEquals(node, actual) && node is not Window) related = true;
            }
            if (!related || actual is ButtonBase) throw new InvalidOperationException("Exact physical edit pixel hit an unexpected control.");
            if (!SetCursorPos(px, py)) throw new System.ComponentModel.Win32Exception();
            VerifyPoint(px, py); Send([Mouse(0x0002), Mouse(0x0004)]);
        }
        BitmapSource Capture(Rect bounds, string? name)
        {
            int left = (int)Math.Ceiling(bounds.Left), top = (int)Math.Ceiling(bounds.Top);
            int width = (int)Math.Floor(bounds.Right) - left, height = (int)Math.Floor(bounds.Bottom) - top;
            if (width < 40 || height < 40 || width > 4096 || height > 4096) throw new InvalidOperationException("Invalid owned capture bounds.");
            foreach (NativePoint point in new[] { new NativePoint(left + 16, top + 16), new(left + width - 17, top + 16), new(left + 16, top + height - 17), new(left + width - 17, top + height - 17), new(left + width / 2, top + height / 2) }) VerifyPoint(point.X, point.Y);
            nint screen = 0, dc = 0, bitmap = 0, old = 0;
            try
            {
                screen = GetDC(0); dc = CreateCompatibleDC(screen); bitmap = CreateCompatibleBitmap(screen, width, height); old = SelectObject(dc, bitmap);
                if (screen == 0 || dc == 0 || bitmap == 0 || old == 0 || old == -1 || !BitBlt(dc, 0, 0, width, height, screen, left, top, 0x00CC0020)) throw new System.ComponentModel.Win32Exception();
                SelectObject(dc, old); old = 0;
                var info = new BitmapInfo { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 };
                byte[] pixels = new byte[width * height * 4];
                if (GetDIBits(dc, bitmap, 0, (uint)height, pixels, ref info, 0) != height) throw new System.ComponentModel.Win32Exception();
                for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
                BitmapSource result = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
                if (name is not null) { var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(result)); using var file = File.Create(Path.Combine(folder, name + ".png")); encoder.Save(file); }
                return result;
            }
            finally { if (old != 0 && old != -1) SelectObject(dc, old); if (bitmap != 0) DeleteObject(bitmap); if (dc != 0) DeleteDC(dc); if (screen != 0) ReleaseDC(0, screen); }
        }
    }

    private static byte[] Fixture(Assembly engine, Assembly app)
    {
        Type E(string name) => engine.GetType(name, true)!;
        object Open(byte[] bytes) => Call(E("KillerPdf.Engine.Documents.PdfDocument"), "Open", new ReadOnlyMemory<byte>(bytes));
        object builder = New(E("KillerPdf.Engine.Authoring.PdfDocumentBuilder"));
        Call(builder, "AddBlankPage", 400d, 600d);
        object doc = Open((byte[])Call(builder, "Build"));
        Type editorType = E("KillerPdf.Engine.Editing.PdfIncrementalAnnotationEditor");
        object original = New(editorType, doc);
        object white = New(E("KillerPdf.Engine.Authoring.PdfRgbColor"), 1d, 1d, 1d);
        Call(original, "AddRectangle", 0, 20d, 20d, 360d, 560d, white, white);
        doc = Open((byte[])Call(original, "Build"));
        object content = New(E("KillerPdf.Engine.Authoring.PdfContentStreamBuilder"));
        Call(content, "SetFillRgb", 0d, 0d, 1d);
        Call(content, "BeginText");
        object helvetica = Enum.Parse(E("KillerPdf.Engine.Authoring.PdfStandardFont"), "Helvetica");
        Call(content, "SetFont", helvetica, 20d);
        Call(content, "SetTextMatrix", 1d, 0d, 0d, 1d, 2d, 74d);
        Call(content, "ShowLatin1Text", "First line");
        Call(content, "SetTextMatrix", 1d, 0d, 0d, 1d, 2d, 46d);
        Call(content, "ShowLatin1Text", "Second line"); Call(content, "EndText");
        object appearance = New(E("KillerPdf.Engine.Authoring.PdfFormXObject"), 240d, 100d, content);
        object editor = New(editorType, doc);
        Call(editor, "AddFreeText", 0, 80d, 380d, 240d, 100d, WholeText, appearance);
        doc = Open((byte[])Call(editor, "Build"));
        object[] comments = ((IEnumerable)Call(E("KillerPdf.Engine.Documents.PdfCommentReader"), "Read", doc, true)).Cast<object>().ToArray();
        object tail = comments.Single(c => Equals(Get(c, "Subtype"), "FreeText"));
        object model = New(app.GetType("KillerPDF.TextAnnotation", true)!);
        foreach (var pair in new Dictionary<string, object> { ["Position"] = new Point(80, 120), ["Content"] = WholeText,
            ["Width"] = 240d, ["Height"] = 100d, ["FontSize"] = 20d, ["FontName"] = "Arial", ["ColorB"] = (byte)255 })
            model.GetType().GetProperty(pair.Key, BindingFlags.Public | BindingFlags.Instance)!.SetValue(model, pair.Value);
        Type recordType = app.GetType("KillerPDF.Services.OverlayWriteRecord", true)!;
        object record = New(recordType, model, 400, 600, 0, Get(tail, "AnnotationIndex"), Get(tail, "ObjectNumber"));
        Array records = Array.CreateInstance(recordType, 1); records.SetValue(record, 0);
        return (byte[])Call(app.GetType("KillerPDF.Services.PdfOverlayAnnotations", true)!, "Attach", doc, records);
    }
    private static object Get(object value, string name) => value.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)!.GetValue(value)!;
    private static object New(Type type, params object[] args) => Invoke(type.GetConstructors(), null, args);
    private static object Call(object target, string method, params object[] args) => Invoke((target is Type type ? type : target.GetType())
        .GetMethods(BindingFlags.Public | (target is Type ? BindingFlags.Static : BindingFlags.Instance)).Where(m => m.Name == method), target is Type ? null : target, args);
    private static object Invoke(IEnumerable<MethodBase> methods, object? target, object[] args)
    {
        MethodBase match = methods.Single(m => { ParameterInfo[] p = m.GetParameters(); return p.Length >= args.Length
            && p.Skip(args.Length).All(v => v.HasDefaultValue) && args.Select((a, i) => a is null ? !p[i].ParameterType.IsValueType : p[i].ParameterType.IsInstanceOfType(a)).All(v => v); });
        object?[] supplied = match.GetParameters().Select((p, i) => i < args.Length ? args[i] : p.DefaultValue).ToArray();
        try { return (match is ConstructorInfo ctor ? ctor.Invoke(supplied) : match.Invoke(target, supplied))!; }
        catch (TargetInvocationException error) when (error.InnerException is not null) { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }
    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static T Named<T>(DependencyObject root, string name) where T : FrameworkElement => (root as FrameworkElement)?.FindName(name) as T
        ?? Descendants<T>(root).SingleOrDefault(e => e.Name == name) ?? throw new InvalidOperationException("Required control absent: " + name);
    private static Rect Bounds(FrameworkElement element) => new(element.PointToScreen(new Point()), element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight)));
    private static IEnumerable<DependencyObject> Roots(nint owner) => PresentationSource.CurrentSources.Cast<PresentationSource>()
        .OfType<HwndSource>().Where(s => s.RootVisual is not null && GetAncestor(s.Handle, 2) == owner).Select(s => (DependencyObject)s.RootVisual);
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T self) yield return self;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (T child in Descendants<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static void Pump() { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame); }
    private static BitmapSource Solid(bool blue)
    {
        byte[] data = new byte[64 * 64 * 4]; for (int i = 0; i < data.Length; i += 4) { data[i] = 255; data[i + 1] = data[i + 2] = blue ? (byte)0 : (byte)255; data[i + 3] = 255; }
        return BitmapSource.Create(64, 64, 96, 96, PixelFormats.Bgra32, null, data, 256);
    }
    private static bool Blue(BitmapSource image)
    {
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0); int stride = converted.PixelWidth * 4, hits = 0;
        byte[] pixels = new byte[stride * converted.PixelHeight]; converted.CopyPixels(pixels, stride, 0);
        for (int i = 0; i < pixels.Length; i += 4) if (pixels[i] > 170 && pixels[i + 1] < 85 && pixels[i + 2] < 85) hits++;
        return hits >= 40;
    }
    private sealed class ProductAssertionException(string message) : Exception(message);
    [StructLayout(LayoutKind.Sequential)] private readonly record struct NativePoint(int X, int Y);
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo { public uint Size; public int Width, Height; public ushort Planes, BitCount; public uint Compression, SizeImage; public int XPels, YPels; public uint Used, Important, Colors; }
    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int X, Y; public uint MouseData, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { public ushort VirtualKey, Scan; public uint Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Explicit, Size = 40)] private struct INPUT { [FieldOffset(0)] public uint Type; [FieldOffset(8)] public MOUSEINPUT Mouse; [FieldOffset(8)] public KEYBDINPUT Keyboard; }
    private static INPUT Mouse(uint flags) => new() { Type = 0, Mouse = new() { Flags = flags } };
    private static INPUT Key(ushort key, uint flags = 0) => new() { Type = 1, Keyboard = new() { VirtualKey = key, Flags = flags } };
    private static INPUT Unicode(char character, uint flags = 4) => new() { Type = 1, Keyboard = new() { Scan = character, Flags = flags } };
    private static void Send(INPUT[] input) { if (SendInput((uint)input.Length, input, Marshal.SizeOf<INPUT>()) != input.Length) throw new System.ComponentModel.Win32Exception(); }
    private static void ReleaseModifiers() => Send([Key(0x10, 2), Key(0x11, 2), Key(0x12, 2)]);
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, INPUT[] input, int size);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleBitmap(nint dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint item);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(nint target, int x, int y, int width, int height, nint source, int sourceX, int sourceY, uint operation);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(nint dc, nint bitmap, uint start, uint count, byte[] pixels, ref BitmapInfo info, uint usage);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint item);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
}
