using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

internal static class GuidedTasksSmoke
{
    internal static int Run(Window owner, Assembly assembly, string appPath, string folder)
    {
        const string edited = "LIVE EMAIL VALUE";
        string source = Path.Combine(folder, "email-source.pdf");
        string output = Path.Combine(folder, "email-output.pdf");
        byte[] original = Fixture();
        File.WriteAllBytes(source, original);
        owner.GetType().GetMethod("OpenFromExternal", BindingFlags.Public | BindingFlags.Instance)!.Invoke(owner, [source]);
        Wait(() => Children(owner).OfType<TextBox>().Any(box => Equals(box.ToolTip, "guided.value")), "live form editor");
        TextBox field = Children(owner).OfType<TextBox>().Single(box => Equals(box.ToolTip, "guided.value"));
        field.Focus(); field.SelectAll(); field.SelectedText = edited; owner.Focus(); Pump();
        int stage = 0;
        Exception? failure = null;
        var watch = Stopwatch.StartNew();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(25) };
        timer.Tick += (_, _) =>
        {
            try
            {
                if (watch.Elapsed.TotalSeconds > 45) throw new TimeoutException("Guided workflow timed out at stage " + stage);
                Window? dialog = Application.Current.Windows.Cast<Window>().FirstOrDefault(window => window.IsVisible && window.GetType().Name == "GuidedTasksDialog");
                if (stage == 0)
                {
                    stage = 1;
                    owner.Dispatcher.BeginInvoke(() =>
                    {
                        try { OpenGuidedMenu(owner); }
                        catch (Exception error) { failure = error; stage = -1; }
                    });
                }
                else if (stage == 1 && dialog is not null)
                {
                    if (Find<ComboBox>(dialog, "GuidedTaskChoice").SelectedIndex != 0)
                        throw new InvalidOperationException("Guided tasks must default to email preparation.");
                    stage = 2;
                    owner.Dispatcher.BeginInvoke(() => Click(Find<Button>(dialog, "GuidedBrowse")));
                }
                else if (stage == 2)
                {
                    Window? picker = Application.Current.Windows.Cast<Window>().FirstOrDefault(window => window.IsVisible && window.GetType().FullName == "KillerPDF.Controls.FileDialog");
                    if (picker is null) return;
                    Children(picker).OfType<TextBox>().Single(box => box.Name == "FileNameBox").Text = output;
                    stage = 3; owner.Dispatcher.BeginInvoke(() => Click(Children(picker).OfType<Button>().Single(button => button.Name == "AcceptButton")));
                }
                else if (stage == 3 && dialog is not null && Find<Button>(dialog, "GuidedRun").IsEnabled)
                {
                    if (Find<TextBox>(dialog, "GuidedOutput").Text != output) throw new InvalidOperationException("Chosen destination did not reach the plan.");
                    stage = 4; owner.Dispatcher.BeginInvoke(() => Click(Find<Button>(dialog, "GuidedRun")));
                }
                else if (stage == 4 && File.Exists(output))
                {
                    if (dialog is not null) Click(Find<Button>(dialog, "GuidedCancel"));
                    stage = 5; timer.Stop();
                }
            }
            catch (Exception error) { failure = error; stage = -1; timer.Stop(); CloseTransient(owner); }
        };
        timer.Start();
        try
        {
            Wait(() => stage == 5 || failure is not null, "email workflow");
            if (failure is not null) throw failure;
            if (!File.ReadAllBytes(source).AsSpan().SequenceEqual(original)) throw new InvalidOperationException("Guided task changed source bytes.");
            if (!ReferenceEquals(field, Children(owner).OfType<TextBox>().Single(box => Equals(box.ToolTip, "guided.value"))) || field.Text != edited)
                throw new InvalidOperationException("Guided task replaced the current document or lost live edits.");
            Assembly engine = Assembly.Load("KillerPdf.Engine");
            Type documentType = engine.GetType("KillerPdf.Engine.Documents.PdfDocument", true)!;
            object saved = documentType.GetMethod("Open", [typeof(ReadOnlyMemory<byte>)])!.Invoke(null, [new ReadOnlyMemory<byte>(File.ReadAllBytes(output))])!;
            object[] widgets = ((IEnumerable)engine.GetType("KillerPdf.Engine.Documents.PdfFormWidgetReader", true)!.GetMethod("ReadPage")!.Invoke(null, [saved, 0])!).Cast<object>().ToArray();
            object widget = widgets.Single(item => Equals(item.GetType().GetProperty("FieldName")!.GetValue(item), "guided.value"));
            if (!Equals(widget.GetType().GetProperty("Value")!.GetValue(widget), edited)) throw new InvalidOperationException("Saved PDF lost live form edits.");
            RunCopyTask(owner, 1, Path.Combine(folder, "scan-output.pdf"));
            RunCopyTask(owner, 2, Path.Combine(folder, "privacy-output.pdf"));
            RunCopyTask(owner, 3, Path.Combine(folder, "form-output.pdf"));
            RunCopyTask(owner, 2, Path.Combine(folder, "live-edit-rejected.pdf"), "live-edit");
            RunCopyTask(owner, 2, Path.Combine(folder, "cancel-review.pdf"), "cancel-review");
            RunCopyTask(owner, 0, Path.Combine(folder, "cancel-picker.pdf"), "cancel-picker");
            RunCopyTask(owner, 1, Path.Combine(folder, "ocr-output.pdf"), "ocr");
            VerifyMacros(owner, engine, folder);
            VerifyDiscovery(owner);
            string existing = Path.Combine(folder, "existing-output.pdf"); File.WriteAllBytes(existing, original);
            RunCopyTask(owner, 0, existing, "retry");
            RunCopyTask(owner, 2, Path.Combine(folder, "source-switch-rejected.pdf"), "source-switch");
            string cropped = Path.Combine(folder, "cropped-source.pdf"); byte[] cropBytes = Fixture(true); File.WriteAllBytes(cropped, cropBytes);
            TextBox previous = Children(owner).OfType<TextBox>().Single(box => Equals(box.ToolTip, "guided.value"));
            owner.GetType().GetMethod("OpenFromExternal", BindingFlags.Public | BindingFlags.Instance)!.Invoke(owner, [cropped]);
            Wait(() => Children(owner).OfType<TextBox>().Any(box => Equals(box.ToolTip, "guided.value") && !ReferenceEquals(box, previous)), "cropped source editor");
            RunCopyTask(owner, 3, Path.Combine(folder, "cropped-form-output.pdf"), "crop");
            if (!File.ReadAllBytes(cropped).AsSpan().SequenceEqual(cropBytes)) throw new InvalidOperationException("Cropped form journey changed source bytes.");
            VerifyLayouts(owner, assembly, folder);
            if (!File.ReadAllBytes(source).AsSpan().SequenceEqual(original)) throw new InvalidOperationException("A later guided workflow changed source bytes.");
            if (Directory.EnumerateFiles(Path.Combine(folder, "data"), "*reduceclean*", SearchOption.AllDirectories).Any())
                throw new InvalidOperationException("Guided snapshots left reduceclean intermediates in owned app data.");
            string result = "PASS four real menu-to-picker task workflows, public saved readers, reviewed privacy removal and form name, source/current document preservation, live-edit rejection, review/picker cancellation, serialized email/scan macros, 14 themes, current locales, 70/100/150/250 percent scales and small-window rendered reachability. Offscreen WPF routed events; app SHA256=" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(appPath)));
            File.WriteAllText(Path.Combine(folder, "result.txt"), result); Console.WriteLine(result); return 0;
        }
        catch (Exception error) { File.WriteAllText(Path.Combine(folder, "failure.txt"), "stage=" + stage + "\napp SHA256=" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(appPath))) + "\n" + error); throw; }
        finally { timer.Stop(); CloseTransient(owner); }
    }

    private static void RunCopyTask(Window owner, int choice, string output, string? safety = null)
    {
        int stage = 0;
        Exception? failure = null;
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(25) };
        var watch = Stopwatch.StartNew();
        var handled = new HashSet<Window>();
        bool reviewedPrivacy = false;
        bool reviewedForm = false;
        bool retried = false;
        bool ocrCanceled = false;
        byte[]? existingBytes = safety == "retry" ? File.ReadAllBytes(output) : null;
        FileStream? outputLock = safety == "retry" ? new FileStream(output, FileMode.Open, FileAccess.Read, FileShare.Read) : null;
        timer.Tick += (_, _) =>
        {
            try
            {
                if (watch.Elapsed.TotalSeconds > 40) throw new TimeoutException("Copy task " + choice + " timed out at stage " + stage);
                Window? dialog = Application.Current.Windows.Cast<Window>().FirstOrDefault(window => window.IsVisible && window.GetType().Name == "GuidedTasksDialog");
                if (stage == 0)
                {
                    stage = 1; owner.Dispatcher.BeginInvoke(() => OpenGuidedMenu(owner));
                }
                else if (stage == 1 && dialog is not null)
                {
                    Find<ComboBox>(dialog, "GuidedTaskChoice").SelectedIndex = choice;
                    if (choice == 1)
                    {
                        CheckBox ocr = Find<CheckBox>(dialog, "GuidedOcr");
                        if (ocr.IsChecked != true) throw new InvalidOperationException("Scan task must offer OCR by default.");
                        if (safety != "ocr") ocr.IsChecked = false;
                    }
                    stage = 2; owner.Dispatcher.BeginInvoke(() => Click(Find<Button>(dialog, "GuidedBrowse")));
                }
                else if (stage == 2)
                {
                    Window? picker = Application.Current.Windows.Cast<Window>().FirstOrDefault(window => window.IsVisible && window.GetType().FullName == "KillerPDF.Controls.FileDialog");
                    if (picker is null) return;
                    if (safety == "cancel-picker")
                    {
                        stage = 6; owner.Dispatcher.BeginInvoke(() => Click(Children(picker).OfType<Button>().Single(button => Equals(button.Content, Application.Current.FindResource("Str_Btn_Cancel"))))); return;
                    }
                    Children(picker).OfType<TextBox>().Single(box => box.Name == "FileNameBox").Text = output;
                    stage = 3; owner.Dispatcher.BeginInvoke(() => Click(Children(picker).OfType<Button>().Single(button => button.Name == "AcceptButton")));
                }
                else if (stage == 3 && dialog is not null && Find<Button>(dialog, "GuidedRun").IsEnabled)
                { stage = 4; owner.Dispatcher.BeginInvoke(() => Click(Find<Button>(dialog, "GuidedRun"))); }
                else if (stage == 4 && dialog is not null)
                {
                    Window? modelPrompt = Application.Current.Windows.Cast<Window>().FirstOrDefault(window => window != owner && window != dialog && window.IsVisible
                        && Children(window).OfType<Button>().Any(button => Equals(button.Content, Application.Current.FindResource("Str_Btn_Cancel"))));
                    if (safety == "ocr" && modelPrompt is not null && handled.Add(modelPrompt))
                    {
                        ocrCanceled = true;
                        owner.Dispatcher.BeginInvoke(() => Click(Children(modelPrompt).OfType<Button>().Single(button => Equals(button.Content, Application.Current.FindResource("Str_Btn_Cancel"))))); return;
                    }
                    Window? form = Application.Current.Windows.Cast<Window>().FirstOrDefault(window => window.IsVisible && window.GetType().Name == "FormRecognitionReviewDialog");
                    if (form is not null && handled.Add(form))
                    {
                        TextBox[] names = Children(form).OfType<TextBox>().ToArray();
                        if (names.Length == 0) throw new InvalidOperationException("Form recognition did not propose the fixture rectangle.");
                        names[0].Text = "guided.recognized";
                        if (safety == "crop")
                        {
                            form.Opacity = 1; Pump();
                            Canvas preview = Children(form).OfType<Canvas>().Single();
                            var outline = preview.Children.OfType<System.Windows.Shapes.Rectangle>().Single();
                            if (Math.Abs(Canvas.GetLeft(outline) - preview.Width * .05) > 1 || Math.Abs(Canvas.GetTop(outline) - preview.Height * (610d / 650)) > 1)
                                throw new InvalidOperationException("Cropped form proposal preview is misplaced.");
                            Capture(form, Path.Combine(Path.GetDirectoryName(output)!, "cropped-form-review.png"));
                        }
                        reviewedForm = true; owner.Dispatcher.BeginInvoke(() => Click(Children(form).OfType<Button>().Single(button => button.IsDefault))); return;
                    }
                    Window? review = Application.Current.Windows.Cast<Window>().FirstOrDefault(window => window.IsVisible
                        && window.GetType().Name is "RedactionFindDialog" or "RedactionFinishDialog");
                    if (review is not null && handled.Add(review))
                    {
                        if (review.GetType().Name == "RedactionFindDialog")
                        {
                            if (safety == "cancel-review")
                            {
                                owner.Dispatcher.BeginInvoke(() => Click(Children(review).OfType<Button>().Single(button => button.IsCancel))); return;
                            }
                            if (safety == "live-edit")
                            {
                                TextBox live = Children(owner).OfType<TextBox>().Single(box => Equals(box.ToolTip, "guided.value"));
                                live.SelectAll(); live.SelectedText = "EDIT WHILE REVIEW OPEN"; Pump();
                            }
                            if (safety == "source-switch")
                            {
                                string replacement = Path.Combine(Path.GetDirectoryName(output)!, "replacement-source.pdf");
                                File.WriteAllBytes(replacement, Fixture());
                                owner.GetType().GetMethod("OpenFromExternal", BindingFlags.Public | BindingFlags.Instance)!.Invoke(owner, [replacement]);
                            }
                            Children(review).OfType<TextBox>().Single(box => box.AcceptsReturn).Text = "PRIVATE VALUE";
                            Click(Children(review).OfType<Button>().Single(button => Equals(button.Content, Application.Current.FindResource("Str_RedactFind_Search"))));
                            Button mark = Children(review).OfType<Button>().Single(button => Equals(button.Content, Application.Current.FindResource("Str_RedactFind_Mark")));
                            if (!mark.IsEnabled) throw new InvalidOperationException("Privacy search did not find the known private page text.");
                            reviewedPrivacy = true; owner.Dispatcher.BeginInvoke(() => Click(mark));
                        }
                        else owner.Dispatcher.BeginInvoke(() => Click(Children(review).OfType<Button>().Single(button => button.IsDefault)));
                        return;
                    }
                    Window? confirm = Application.Current.Windows.Cast<Window>().FirstOrDefault(window => window != owner && window != dialog && window.IsVisible
                        && Children(window).OfType<Button>().Any(button => Equals(button.Content, Application.Current.FindResource("Str_Btn_Yes"))));
                    if ((choice == 2 || safety == "retry") && confirm is not null && handled.Add(confirm))
                    {
                        owner.Dispatcher.BeginInvoke(() => Click(Children(confirm).OfType<Button>().Single(button => Equals(button.Content, Application.Current.FindResource("Str_Btn_Yes")))));
                        return;
                    }
                    if (Find<Button>(dialog, "GuidedRun").IsEnabled && Find<Button>(dialog, "GuidedOpen").Visibility != Visibility.Visible)
                    {
                        if (safety == "ocr" && ocrCanceled) { Click(Find<Button>(dialog, "GuidedCancel")); stage = 5; timer.Stop(); return; }
                        if (safety == "retry" && !retried)
                        {
                            if (!File.ReadAllBytes(output).AsSpan().SequenceEqual(existingBytes)) throw new InvalidOperationException("Failed task changed existing destination.");
                            if (!Equals(Find<Button>(dialog, "GuidedRun").Content, Application.Current.FindResource("Str_Guided_Retry"))) throw new InvalidOperationException("Failed output did not offer Retry.");
                            outputLock!.Dispose(); outputLock = null; retried = true;
                            owner.Dispatcher.BeginInvoke(() => Click(Find<Button>(dialog, "GuidedRun"))); return;
                        }
                        if (safety is "live-edit" or "cancel-review" or "source-switch") { Click(Find<Button>(dialog, "GuidedCancel")); stage = 5; timer.Stop(); return; }
                        throw new InvalidOperationException("Task " + choice + " failed: " + Find<TextBlock>(dialog, "GuidedStatus").Text);
                    }
                    if (!File.Exists(output) || Find<Button>(dialog, "GuidedOpen").Visibility != Visibility.Visible) return;
                    if (safety is "live-edit" or "source-switch") throw new InvalidOperationException("Guided privacy task saved stale output after " + safety + " during review.");
                    Click(Find<Button>(dialog, "GuidedCancel")); stage = 5; timer.Stop();
                }
                else if (stage == 6 && dialog is not null && dialog.IsEnabled)
                {
                    if (Find<Button>(dialog, "GuidedRun").IsEnabled || Find<TextBox>(dialog, "GuidedOutput").Text.Length != 0)
                        throw new InvalidOperationException("Canceled picker enabled Run or retained a destination.");
                    Click(Find<Button>(dialog, "GuidedCancel")); stage = 5; timer.Stop();
                }
            }
            catch (Exception error) { failure = error; stage = -1; timer.Stop(); CloseTransient(owner); }
        };
        timer.Start();
        try
        {
            Wait(() => stage == 5 || failure is not null, "copy task " + choice);
            if (failure is not null) throw failure;
            if (safety is "live-edit" or "cancel-review" or "cancel-picker" or "source-switch" || ocrCanceled)
            {
                if (File.Exists(output)) throw new InvalidOperationException("Stale live-edit task created output.");
                Console.WriteLine("PASS " + safety + " without output."); return;
            }
            Assembly engine = Assembly.Load("KillerPdf.Engine");
            object saved = engine.GetType("KillerPdf.Engine.Documents.PdfDocument", true)!.GetMethod("Open", [typeof(ReadOnlyMemory<byte>)])!.Invoke(null, [new ReadOnlyMemory<byte>(File.ReadAllBytes(output))])!;
            object[] pages = ((IEnumerable)engine.GetType("KillerPdf.Engine.Documents.PdfPageInformation", true)!.GetMethod("Read")!.Invoke(null, [saved])!).Cast<object>().ToArray();
            if (pages.Length != 1) throw new InvalidOperationException("Task output did not reopen as one page.");
            if (choice == 2 && !reviewedPrivacy) throw new InvalidOperationException("Privacy output bypassed the real search review.");
            if (choice == 2)
            {
                Type readerType = engine.GetType("KillerPdf.Engine.Documents.PdfPageContentReader", true)!;
                object reader = Activator.CreateInstance(readerType, [saved])!;
                object page = readerType.GetMethod("Read")!.Invoke(reader, [0, CancellationToken.None])!;
                string text = (string)page.GetType().GetProperty("Text")!.GetValue(page)!;
                if (text.Contains("PRIVATE VALUE", StringComparison.Ordinal) || !text.Contains("Name", StringComparison.Ordinal))
                    throw new InvalidOperationException("Privacy output retained private text or removed unrelated page text.");
            }
            if (choice == 3)
            {
                if (!reviewedForm) throw new InvalidOperationException("Form output bypassed proposal review.");
                object[] widgets = ((IEnumerable)engine.GetType("KillerPdf.Engine.Documents.PdfFormWidgetReader", true)!.GetMethod("ReadPage")!.Invoke(null, [saved, 0])!).Cast<object>().ToArray();
                if (!widgets.Any(widget => Equals(widget.GetType().GetProperty("FieldName")!.GetValue(widget), "guided.recognized")))
                    throw new InvalidOperationException("Saved PDF lost the reviewed form field name.");
                if (safety == "crop")
                {
                    object widget = widgets.Single(widget => Equals(widget.GetType().GetProperty("FieldName")!.GetValue(widget), "guided.recognized"));
                    foreach (var coordinate in new[] { ("Left", 120d), ("Bottom", 70d), ("Right", 280d), ("Top", 90d) })
                        if (Math.Abs((double)widget.GetType().GetProperty(coordinate.Item1)!.GetValue(widget)! - coordinate.Item2) > .01)
                            throw new InvalidOperationException("Cropped form saved widget has incorrect " + coordinate.Item1);
                }
            }
            if (safety == "retry" && (!retried || File.ReadAllBytes(output).AsSpan().SequenceEqual(existingBytes))) throw new InvalidOperationException("Retry did not replace the preserved destination with completed output.");
            Console.WriteLine("PASS real copy task " + choice + " menu, picker, save and public reopen.");
        }
        finally { outputLock?.Dispose(); timer.Stop(); CloseTransient(owner); }
    }

    private static void WithGuided(Window owner, Action<Window> action)
    {
        bool opened = false, done = false;
        Exception? failure = null;
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) =>
        {
            if (done) return;
            if (!opened) { opened = true; owner.Dispatcher.BeginInvoke(() => OpenGuidedMenu(owner)); return; }
            Window? dialog = Application.Current.Windows.Cast<Window>().FirstOrDefault(window => window.IsVisible && window.GetType().Name == "GuidedTasksDialog");
            if (dialog is null) return;
            timer.Stop();
            try { action(dialog); Click(Find<Button>(dialog, "GuidedCancel")); }
            catch (Exception error) { failure = error; CloseTransient(owner); }
            done = true;
        };
        timer.Start();
        try { Wait(() => done, "guided inspection"); if (failure is not null) throw failure; }
        finally { timer.Stop(); CloseTransient(owner); }
    }

    private static void VerifyMacros(Window owner, Assembly engine, string folder)
    {
        WithGuided(owner, dialog =>
        {
            Click(Find<Button>(dialog, "GuidedSaveMacro"));
            Click(Find<Button>(dialog, "GuidedSaveMacro"));
            Find<ComboBox>(dialog, "GuidedTaskChoice").SelectedIndex = 1;
            Click(Find<Button>(dialog, "GuidedSaveMacro"));
            foreach (int index in new[] { 2, 3 })
            {
                Find<ComboBox>(dialog, "GuidedTaskChoice").SelectedIndex = index;
                if (Find<Button>(dialog, "GuidedSaveMacro").IsEnabled) throw new InvalidOperationException("Review task exposed unsupported macro save.");
            }
        });
        Type type = engine.GetType("KillerPdf.Engine.Documents.PdfMacro", true)!;
        object[] macros = Directory.GetFiles(Path.Combine(folder, "data", "macros"), "*.json").Select(file =>
            type.GetMethod("FromJson")!.Invoke(null, [File.ReadAllText(file)])!).ToArray();
        string[] names = macros.Select(macro => (string)type.GetProperty("Name")!.GetValue(macro)!).ToArray();
        string[] steps = macros.Select(macro => string.Join(",", ((IEnumerable)type.GetProperty("Steps")!.GetValue(macro)!).Cast<object>()
            .Select(step => step.GetType().GetProperty("Operation")!.GetValue(step)!.ToString()))).ToArray();
        if (macros.Length != 3 || names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != 3
            || steps.Count(step => step == "Optimize,Save") != 2 || steps.Count(step => step == "Ocr,Optimize,Save") != 1)
            throw new InvalidOperationException("Saved macros did not round-trip distinct names and supported operations.");
        Console.WriteLine("PASS actual Save as macro buttons and public macro serialization.");
    }

    private static void VerifyLayouts(Window owner, Assembly assembly, string folder)
    {
        string captures = Path.Combine(folder, "captures"); Directory.CreateDirectory(captures);
        Type themeType = assembly.GetType("KillerPDF.Services.Theme", true)!;
        Type localeType = assembly.GetType("KillerPDF.Services.Locale", true)!;
        MethodInfo theme = assembly.GetType("KillerPDF.Services.ThemeManager", true)!.GetMethod("Apply", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        MethodInfo locale = assembly.GetType("KillerPDF.Services.LocaleManager", true)!.GetMethod("Apply", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        double scale = 1;
        void SetScale(double target)
        {
            FrameworkElement logo = (FrameworkElement)owner.FindName("LogoBar");
            int steps = (int)Math.Round((target - scale) / .02);
            for (int i = 0; i < Math.Abs(steps); i++) logo.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, steps > 0 ? 120 : -120) { RoutedEvent = UIElement.MouseWheelEvent });
            scale = target; Pump();
        }
        void Layout(string name, double width, double height)
        {
            WithGuided(owner, dialog =>
            {
                dialog.Opacity = 1; dialog.Width = width; dialog.Height = height; Pump();
                Capture(dialog, Path.Combine(captures, name + ".png"));
                FrameworkElement task = Find<FrameworkElement>(dialog, "GuidedTaskChoice"); task.Focus(); Pump();
                if (!task.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)))
                    throw new InvalidOperationException(name + ": Tab traversal from task selector failed.");
                foreach (int choice in new[] { 0, 1, 2, 3 })
                {
                    Find<ComboBox>(dialog, "GuidedTaskChoice").SelectedIndex = choice; Pump();
                    string[] ids = ["GuidedTaskChoice", "GuidedOutput", "GuidedBrowse", "GuidedRun", "GuidedCancel", "GuidedSaveMacro",
                        choice == 0 ? "GuidedFlatten" : choice == 1 ? "GuidedOcr" : choice == 2 ? "GuidedSearch" : "GuidedTaskChoice"];
                    foreach (string id in ids)
                    {
                        FrameworkElement control = Find<FrameworkElement>(dialog, id); control.BringIntoView(); Pump();
                        Rect bounds = control.TransformToAncestor(dialog).TransformBounds(new Rect(control.RenderSize));
                        var available = new Rect(0, 0, dialog.ActualWidth, dialog.ActualHeight); available.Inflate(1, 1);
                        if (!control.IsVisible || bounds.Width <= 0 || bounds.Height <= 0 || !available.Contains(bounds))
                            throw new InvalidOperationException(name + ": unreachable " + id + " " + bounds);
                        for (DependencyObject? ancestor = VisualTreeHelper.GetParent(control); ancestor is not null; ancestor = VisualTreeHelper.GetParent(ancestor))
                        {
                            if (ancestor is not ScrollContentPresenter presenter) continue;
                            Rect viewport = presenter.TransformToAncestor(dialog).TransformBounds(new Rect(presenter.RenderSize)); viewport.Inflate(1, 1);
                            if (!viewport.Contains(bounds)) throw new InvalidOperationException(name + ": clipped " + id + " " + bounds + " viewport=" + viewport);
                        }
                    }
                }
                Capture(dialog, Path.Combine(captures, name + "-actions.png"));
            });
            Console.WriteLine("PASS rendered reachability " + name);
        }
        try
        {
            WithGuided(owner, dialog =>
            {
                dialog.Opacity = 1; Pump(); Capture(dialog, Path.Combine(captures, "default.png"));
                File.WriteAllText(Path.Combine(captures, "default-size.txt"), dialog.ActualWidth + "x" + dialog.ActualHeight);
            });
            foreach (string name in Enum.GetNames(themeType)) { theme.Invoke(null, [Enum.Parse(themeType, name)]); Layout(name + "-100", 620, 480); }
            theme.Invoke(null, [Enum.Parse(themeType, "Dark")]);
            foreach (double value in new[] { .7, 1.5, 2.5 }) { SetScale(value); Layout("scale-" + value, value == 2.5 ? 480 : 640, value == 2.5 ? 360 : 480); }
            SetScale(1.5);
            foreach (string name in Enum.GetNames(localeType)) { locale.Invoke(null, [Enum.Parse(localeType, name)]); Layout("locale-" + name, 640, 480); }
        }
        finally { SetScale(1); locale.Invoke(null, [Enum.Parse(localeType, "EnUS")]); theme.Invoke(null, [Enum.Parse(themeType, "Dark")]); }
    }

    private static void VerifyDiscovery(Window owner)
    {
        owner.Focus(); Pump();
        owner.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(owner), Environment.TickCount, Key.F1) { RoutedEvent = Keyboard.PreviewKeyDownEvent }); Pump();
        FrameworkElement overlay = (FrameworkElement)owner.FindName("ShortcutsOverlay");
        string label = (string)Application.Current.FindResource("Str_Guided_Title");
        if (!overlay.IsVisible || !Children(overlay).OfType<TextBlock>().Any(block => block.Text.Contains(label, StringComparison.Ordinal)))
            throw new InvalidOperationException("F1 shortcuts did not expose Guided tasks.");
        owner.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(owner), Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent }); Pump();
        FrameworkElement search = (FrameworkElement)owner.FindName("ToolSearchHost");
        Click(Children(search).OfType<Button>().Single()); Pump();
        Children(search).OfType<TextBox>().Single().Text = label; Pump();
        // The results drop down once the search box has finished growing, so wait for the row.
        Border[] Rows() => [.. PresentationSource.CurrentSources.Cast<PresentationSource>().Select(source => source.RootVisual).OfType<DependencyObject>().SelectMany(Children).OfType<Border>()
            .Where(border => AutomationProperties.GetName(border) == label || AutomationProperties.GetName(border).StartsWith(label + " (", StringComparison.Ordinal))];
        Wait(() => Rows().Length != 0, "search result");
        Border row = Rows().Single();
        bool done = false; Exception? failure = null;
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) =>
        {
            Window? dialog = Application.Current.Windows.Cast<Window>().FirstOrDefault(window => window.IsVisible && window.GetType().Name == "GuidedTasksDialog");
            if (dialog is null) return;
            timer.Stop(); try { Click(Find<Button>(dialog, "GuidedCancel")); } catch (Exception error) { failure = error; } done = true;
        };
        timer.Start();
        try
        {
            row.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
            Wait(() => done, "tool search opens guide"); if (failure is not null) throw failure;
        }
        finally { timer.Stop(); CloseTransient(owner); }
        Console.WriteLine("PASS F1 Guided tasks entry and actual tool search opens guide.");
    }

    private static void Capture(Window window, string path)
    {
        window.UpdateLayout();
        var rendered = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        rendered.Render(window); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(rendered));
        using (Stream stream = File.Create(path)) png.Save(stream);
        BitmapSource decoded = BitmapDecoder.Create(new Uri(path), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        var converted = new FormatConvertedBitmap(decoded, PixelFormats.Bgra32, null, 0);
        byte[] pixels = new byte[converted.PixelWidth * converted.PixelHeight * 4]; converted.CopyPixels(pixels, converted.PixelWidth * 4, 0);
        int visible = 0; var colors = new HashSet<uint>();
        for (int i = 0; i < pixels.Length; i += 4) { if (pixels[i + 3] > 0) visible++; if (colors.Count < 64) colors.Add(BitConverter.ToUInt32(pixels, i)); }
        if (visible < converted.PixelWidth * converted.PixelHeight / 3 || colors.Count < 16) throw new InvalidOperationException("Blank rendered dialog " + path);
    }

    private static void OpenGuidedMenu(Window owner)
    {
        // The Guided tasks row of the Document tools panel, run by its letter while the panel has the keyboard.
        Button entry = DocumentStateSmoke.DocumentToolRow(owner, "Str_Guided_Title");
        string tip = entry.ToolTip as string ?? "";
        if (!tip.Contains("X", StringComparison.Ordinal) || !tip.Contains("D", StringComparison.Ordinal))
            throw new InvalidOperationException("The Guided tasks row does not advertise its Document tools chord.");
        if (!entry.Focus()) throw new InvalidOperationException("The Guided tasks row did not take the keyboard.");
        var chord = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(entry), Environment.TickCount, Key.X) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        entry.RaiseEvent(chord);
        if (!chord.Handled) throw new InvalidOperationException("Document tools X chord was not handled.");
    }
    private static T Find<T>(Window window, string id) where T : DependencyObject => Children(window).OfType<T>().Single(item => AutomationProperties.GetAutomationId(item) == id);
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void CloseTransient(Window owner) { foreach (Window window in Application.Current.Windows.Cast<Window>().Where(window => window != owner).ToArray()) window.Close(); }
    private static void Pump() => Application.Current.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static void Wait(Func<bool> condition, string stage)
    {
        var watch = Stopwatch.StartNew();
        while (!condition()) { Pump(); Thread.Sleep(10); if (watch.Elapsed.TotalSeconds > 50) throw new TimeoutException("Guided tasks timed out: " + stage); }
        Pump();
    }
    private static IEnumerable<DependencyObject> Children(DependencyObject root)
    {
        yield return root;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++) foreach (DependencyObject child in Children(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
    private static byte[] Fixture(bool cropped = false)
    {
        string content = cropped ? "BT /Helv 12 Tf 120 150 Td (PRIVATE VALUE) Tj ET\nBT /Helv 12 Tf 120 98 Td (Name) Tj ET\n120 70 160 20 re S\n"
            : "BT /Helv 12 Tf 20 100 Td (PRIVATE VALUE) Tj ET\nBT /Helv 12 Tf 20 48 Td (Name) Tj ET\n20 20 160 20 re S\n";
        string pageBox = cropped ? "/MediaBox [0 0 600 800] /CropBox [100 50 500 700]" : "/MediaBox [0 0 200 200]";
        string fieldBox = cropped ? "[120 176 280 200]" : "[20 126 180 150]";
        string[] objects = ["<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] /DA (/Helv 12 Tf 0 g) /DR << /Font << /Helv 5 0 R >> >> >> >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", $"<< /Type /Page /Parent 2 0 R {pageBox} /Resources << /Font << /Helv 5 0 R >> >> /Annots [4 0 R] /Contents 6 0 R >>", $"<< /Type /Annot /Subtype /Widget /FT /Tx /T (guided.value) /V (ORIGINAL VALUE) /Rect {fieldBox} /P 3 0 R /F 4 /DA (/Helv 12 Tf 0 g) >>", "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", $"<< /Length {content.Length} >>\nstream\n{content}endstream"];
        var pdf = new StringBuilder("%PDF-1.7\n"); var offsets = new List<int>();
        for (int index = 0; index < objects.Length; index++) { offsets.Add(pdf.Length); pdf.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n"); }
        int xref = pdf.Length; pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) pdf.Append(offset.ToString("D10", System.Globalization.CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n"); return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
