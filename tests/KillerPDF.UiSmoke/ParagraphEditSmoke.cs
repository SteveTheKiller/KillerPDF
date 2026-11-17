using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

internal static class ParagraphEditSmoke
{
    private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    internal static int Run(Window owner, Assembly assembly, string appPath, string folder)
    {
        Type dialogType = assembly.GetType("KillerPDF.ParagraphEditDialog", true)!;
        Assembly engine = Assembly.Load("KillerPdf.Engine");
        byte[] source = Fixture();
        object document = engine.GetType("KillerPdf.Engine.Documents.PdfDocument", true)!
            .GetMethod("Open", [typeof(ReadOnlyMemory<byte>)])!.Invoke(null, [new ReadOnlyMemory<byte>(source)])!;
        var dialog = (Window)Activator.CreateInstance(dialogType, Members, null, [owner, document, 0, 1d, null], null)!;
        try
        {
            Show(dialog);
            Wait(() => Find<TextBox>(dialog, "ParagraphText").Text.Contains("Original paragraph", StringComparison.Ordinal));
            Find<TextBox>(dialog, "ParagraphLeft").Text = "299.5";
            Find<TextBox>(dialog, "ParagraphTop").Text = "299.5";
            Find<TextBox>(dialog, "ParagraphWidth").Text = "0.5";
            Find<TextBox>(dialog, "ParagraphHeight").Text = "0.5";
            Find<Thumb>(dialog, "ParagraphResize").RaiseEvent(new DragDeltaEventArgs(12, 8) { RoutedEvent = Thumb.DragDeltaEvent });
            foreach (string id in new[] { "ParagraphWidth", "ParagraphHeight" })
            {
                double extent = double.Parse(Find<TextBox>(dialog, id).Text, CultureInfo.InvariantCulture);
                if (extent <= 0 || extent > .5) throw new InvalidOperationException("Edge resize escaped the page.");
            }
            var candidates = Find<ComboBox>(dialog, "ParagraphCandidates");
            if (candidates.Items.Count != 2) throw new InvalidOperationException("The two source paragraphs were not offered separately.");
            candidates.SelectedIndex = 1; Pump();
            if (!Find<TextBox>(dialog, "ParagraphText").Text.Contains("Unchanged lower paragraph", StringComparison.Ordinal))
                throw new InvalidOperationException("Selecting another paragraph did not update the editor.");
            candidates.SelectedIndex = 0; Pump();
            Find<TextBox>(dialog, "ParagraphText").Text = "Reviewed paragraph fits inside the replacement box.";
            Find<TextBox>(dialog, "ParagraphFontSize").Text = "13";
            Find<TextBox>(dialog, "ParagraphLeading").Text = "18";
            Find<ComboBox>(dialog, "ParagraphAlignment").SelectedIndex = 1;
            Find<TextBox>(dialog, "ParagraphWidth").Text = "180";
            Find<TextBox>(dialog, "ParagraphHeight").Text = "70";
            Click(Find<Button>(dialog, "ParagraphPreview"));
            Wait(() => Find<Button>(dialog, "ParagraphApply").IsEnabled);
            Find<TextBox>(dialog, "ParagraphHeight").Text = "5";
            if (Find<Button>(dialog, "ParagraphApply").IsEnabled)
                throw new InvalidOperationException("Changing the layout left a stale preview applicable.");
            Click(Find<Button>(dialog, "ParagraphPreview"));
            Wait(() => Find<Button>(dialog, "ParagraphPreview").IsEnabled);
            if (Find<Button>(dialog, "ParagraphApply").IsEnabled)
                throw new InvalidOperationException("Overflow enabled Apply.");
            Find<TextBox>(dialog, "ParagraphHeight").Text = "70";
            double before = double.Parse(Find<TextBox>(dialog, "ParagraphWidth").Text, CultureInfo.InvariantCulture);
            Find<Thumb>(dialog, "ParagraphResize").RaiseEvent(new DragDeltaEventArgs(12, 8) { RoutedEvent = Thumb.DragDeltaEvent });
            if (double.Parse(Find<TextBox>(dialog, "ParagraphWidth").Text, CultureInfo.InvariantCulture) <= before)
                throw new InvalidOperationException("Dragging the corner did not enlarge the paragraph box.");
            Click(Find<Button>(dialog, "ParagraphPreview"));
            Wait(() => Find<Button>(dialog, "ParagraphApply").IsEnabled);
            Click(Find<Button>(dialog, "ParagraphApply"));
            object plan = dialogType.GetProperty("Result", Members)!.GetValue(dialog)
                ?? throw new InvalidOperationException("Apply did not return the reviewed paragraph plan.");
            if ((double)plan.GetType().GetProperty("FontSize")!.GetValue(plan)! != 13
                || (double)plan.GetType().GetProperty("Leading")!.GetValue(plan)! != 18
                || plan.GetType().GetProperty("Alignment")!.GetValue(plan)!.ToString() != "Center"
                || ((System.Collections.IEnumerable)plan.GetType().GetProperty("Lines")!.GetValue(plan)!).Cast<object>().Count() < 2)
                throw new InvalidOperationException("The reviewed plan lost wrapping or formatting choices.");
            byte[] preview = (byte[])plan.GetType().GetMethod("GetPreviewDocument")!.Invoke(plan, null)!;
            byte[] applied = (byte[])engine.GetType("KillerPdf.Engine.Documents.PdfParagraphEditor", true)!
                .GetMethod("Apply")!.Invoke(null, [document, plan])!;
            if (!preview.AsSpan().SequenceEqual(applied)) throw new InvalidOperationException("Apply differs from the reviewed preview document.");
            File.WriteAllBytes(Path.Combine(folder, "reviewed.pdf"), preview);
            string text = Extract(engine, preview, 0);
            if (!text.Contains("Reviewed paragraph", StringComparison.Ordinal) || text.Contains("Original paragraph", StringComparison.Ordinal)
                || !text.Contains("Unchanged lower paragraph.", StringComparison.Ordinal)
                || !Extract(engine, preview, 1).Contains("Unchanged second page.", StringComparison.Ordinal))
                throw new InvalidOperationException("The reviewed document lost replacement or unrelated text.");
            Console.WriteLine("PASS paragraph dialog selection, formatting, resize, overflow and exact preview/Apply bytes.");
            VerifyCancel(owner, dialogType, document);
            RunWorkflow(owner, assembly, engine, folder, false);
            Console.WriteLine("PASS paragraph main-window Apply, Save As, extraction, Undo and Redo.");
            RunWorkflow(owner, assembly, engine, folder, true);
            VerifyLayouts(owner, assembly, dialogType, document, folder);
            Console.WriteLine("PASS paragraph cancellation, themes, scales, locales and small-window field reachability.");
            string result = "PASS paragraph selection, resize, formatting, overflow, preview/Apply/Cancel, 14 themes at two scales, 20 locales, small-window reachability, main-window Apply/save/reopen/Undo/Redo and source-switch cancellation. WPF routed events; no native input certification. App SHA256=" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(appPath)));
            File.WriteAllText(Path.Combine(folder, "result.txt"), result);
            Console.WriteLine(result);
            return 0;
        }
        finally { dialog.Close(); (document as IDisposable)?.Dispose(); }
    }

    private static string Extract(Assembly engine, byte[] bytes, int page)
    {
        object document = engine.GetType("KillerPdf.Engine.Documents.PdfDocument", true)!
            .GetMethod("Open", [typeof(ReadOnlyMemory<byte>)])!.Invoke(null, [new ReadOnlyMemory<byte>(bytes)])!;
        try
        {
            object reader = Activator.CreateInstance(engine.GetType("KillerPdf.Engine.Documents.PdfPageContentReader", true)!, [document])!;
            object content = reader.GetType().GetMethod("Read")!.Invoke(reader, [page, CancellationToken.None])!;
            return (string)content.GetType().GetProperty("Text")!.GetValue(content)!;
        }
        finally { (document as IDisposable)?.Dispose(); }
    }

    private static void VerifyCancel(Window owner, Type type, object document)
    {
        var window = (Window)Activator.CreateInstance(type, Members, null, [owner, document, 0, 1d, null], null)!;
        try
        {
            Show(window); Find<TextBox>(window, "ParagraphText").Text = "Canceled text";
            Click(Find<Button>(window, "ParagraphPreview"));
            Click(Find<Button>(window, "ParagraphCancel")); Pump();
            if (type.GetProperty("Result", Members)!.GetValue(window) is not null)
                throw new InvalidOperationException("Cancel accepted a paragraph edit.");
        }
        finally { window.Close(); }
    }

    private static void VerifyLayouts(Window owner, Assembly assembly, Type type, object document, string folder)
    {
        Type themeType = assembly.GetType("KillerPDF.Services.Theme", true)!;
        MethodInfo themeApply = assembly.GetType("KillerPDF.Services.ThemeManager", true)!.GetMethod("Apply", Members)!;
        Type localeType = assembly.GetType("KillerPDF.Services.Locale", true)!;
        MethodInfo localeApply = assembly.GetType("KillerPDF.Services.LocaleManager", true)!.GetMethod("Apply", Members)!;
        void Layout(string name, double scale, double width, double height)
        {
            var window = (Window)Activator.CreateInstance(type, Members, null, [owner, document, 0, scale, null], null)!;
            try
            {
                Show(window); window.SizeToContent = SizeToContent.Manual; window.Width = width; window.Height = height; Pump();
                foreach (string id in new[] { "ParagraphText", "ParagraphFontSize", "ParagraphLeading", "ParagraphAlignment", "ParagraphLeft", "ParagraphTop", "ParagraphWidth", "ParagraphHeight", "ParagraphPreview", "ParagraphApply", "ParagraphCancel" })
                {
                    var control = Find<FrameworkElement>(window, id); control.BringIntoView(); Pump();
                    Rect bounds = control.TransformToAncestor(window).TransformBounds(new Rect(control.RenderSize));
                    var available = new Rect(0, 0, window.ActualWidth, window.ActualHeight); available.Inflate(1, 1);
                    if (!control.IsVisible || bounds.Width <= 0 || bounds.Height <= 0 || !available.Contains(bounds))
                        throw new InvalidOperationException(name + ": unreachable field " + id + " " + bounds);
                    for (DependencyObject? ancestor = VisualTreeHelper.GetParent(control); ancestor is not null;
                        ancestor = VisualTreeHelper.GetParent(ancestor))
                    {
                        if (ancestor is not ScrollContentPresenter presenter) continue;
                        Rect visible = presenter.TransformToAncestor(window).TransformBounds(new Rect(presenter.RenderSize));
                        visible.Inflate(1, 1);
                        if (!visible.Contains(bounds))
                            throw new InvalidOperationException(name + ": field clipped by scroll viewport " + id + " " + bounds + " viewport " + visible);
                    }
                }
                var image = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                image.Render(window); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
                using Stream file = File.Create(Path.Combine(folder, name + ".png")); png.Save(file);
            }
            finally { window.Close(); Pump(); }
        }
        foreach (string theme in Enum.GetNames(themeType))
        {
            themeApply.Invoke(null, [Enum.Parse(themeType, theme)]);
            foreach (double scale in new[] { 1d, 1.5 }) Layout(theme + "-" + scale, scale, 800, 600);
        }
        themeApply.Invoke(null, [Enum.Parse(themeType, "Dark")]); Layout("small-250", 2.5, 480, 360);
        foreach (string locale in Enum.GetNames(localeType))
        {
            localeApply.Invoke(null, [Enum.Parse(localeType, locale)]); Layout("locale-" + locale, 1.5, 640, 480);
        }
        localeApply.Invoke(null, [Enum.Parse(localeType, "EnUS")]);
    }

    private static void RunWorkflow(Window owner, Assembly assembly, Assembly engine, string folder, bool switchSource)
    {
        string source = Path.Combine(folder, switchSource ? "switch-source.pdf" : "workflow-source.pdf");
        byte[] original = Fixture(); File.WriteAllBytes(source, original);
        owner.GetType().GetMethod("OpenFromExternal", Members)!.Invoke(owner, [source]); Pump();
        string changed = "Saved paragraph replacement.";
        int stage = 0;
        Exception? failure = null;
        bool done = false;
        var watch = Stopwatch.StartNew();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(25) };
        timer.Tick += (_, _) =>
        {
            try
            {
                if (watch.Elapsed.TotalSeconds > 45) throw new TimeoutException("Paragraph workflow timed out at " + stage);
                Window? review = Application.Current.Windows.Cast<Window>().FirstOrDefault(window => window != owner
                    && window.IsVisible && window.GetType().Name == "ParagraphEditDialog");
                if (stage == 0)
                {
                    stage = 1;
                    owner.Dispatcher.BeginInvoke(() => RunSearchAction(owner, "Str_Paragraph_Title", "Alt+Shift+P"));
                    return;
                }
                if (stage == 1 && review is not null)
                {
                    if (switchSource)
                    {
                        string other = Path.Combine(folder, "switched.pdf"); File.WriteAllBytes(other, original);
                        owner.GetType().GetMethod("OpenFromExternal", Members)!.Invoke(owner, [other]);
                    }
                    Find<TextBox>(review, "ParagraphText").Text = changed;
                    Find<TextBox>(review, "ParagraphWidth").Text = "200";
                    Find<TextBox>(review, "ParagraphHeight").Text = "70";
                    stage = 2; Click(Find<Button>(review, "ParagraphPreview")); return;
                }
                if (stage == 2 && review is not null && Find<Button>(review, "ParagraphApply").IsEnabled)
                { stage = 3; Click(Find<Button>(review, "ParagraphApply")); return; }
                if (stage == 3 && review is null && owner.IsEnabled) { done = true; timer.Stop(); }
            }
            catch (Exception error)
            {
                failure = error; done = true; timer.Stop();
                foreach (Window window in Application.Current.Windows.Cast<Window>().Where(window => window != owner).ToArray()) window.Close();
            }
        };
        timer.Start();
        try
        {
            while (!done) { Pump(); Thread.Sleep(10); if (watch.Elapsed.TotalSeconds > 45) throw new TimeoutException("Paragraph workflow timed out at " + stage); }
            if (failure is not null) throw failure;
        }
        finally { timer.Stop(); }
        string saved = Path.Combine(folder, switchSource ? "switched-save.pdf" : "workflow-save.pdf");
        SaveAs(owner, saved);
        string text = Extract(engine, File.ReadAllBytes(saved), 0);
        if (switchSource)
        {
            if (text.Contains(changed, StringComparison.Ordinal) || !text.Contains("Original paragraph", StringComparison.Ordinal))
                throw new InvalidOperationException("Switching documents applied the stale paragraph edit.");
            return;
        }
        if (!text.Contains(changed, StringComparison.Ordinal) || text.Contains("Original paragraph", StringComparison.Ordinal))
            throw new InvalidOperationException("Save As did not preserve the applied paragraph.");
        Click((Button)owner.FindName("UndoBtn")); Pump();
        string undo = Path.Combine(folder, "workflow-undo.pdf"); SaveAs(owner, undo);
        text = Extract(engine, File.ReadAllBytes(undo), 0);
        if (!text.Contains("Original paragraph", StringComparison.Ordinal) || text.Contains(changed, StringComparison.Ordinal))
            throw new InvalidOperationException("Undo did not restore the original paragraph.");
        Click((Button)owner.FindName("RedoBtn")); Pump();
        string redo = Path.Combine(folder, "workflow-redo.pdf"); SaveAs(owner, redo);
        if (!Extract(engine, File.ReadAllBytes(redo), 0).Contains(changed, StringComparison.Ordinal))
            throw new InvalidOperationException("Redo did not restore the paragraph replacement.");
        owner.GetType().GetMethod("OpenFromExternal", Members)!.Invoke(owner, [redo]); Pump();
        if (!original.AsSpan().SequenceEqual(File.ReadAllBytes(source))) throw new InvalidOperationException("Paragraph editing changed the source file.");
    }

    private static void SaveAs(Window owner, string destination)
    {
        Exception? failure = null;
        var watch = Stopwatch.StartNew();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(25) };
        timer.Tick += (_, _) =>
        {
            try
            {
                if (watch.Elapsed.TotalSeconds > 30) throw new TimeoutException("Save As picker timed out.");
                Window? picker = Application.Current.Windows.Cast<Window>().FirstOrDefault(window => window.GetType().FullName == "KillerPDF.Controls.FileDialog");
                if (picker is null) return;
                Children(picker).OfType<TextBox>().Single(box => box.Name == "FileNameBox").Text = destination;
                timer.Stop(); Click(Children(picker).OfType<Button>().Single(button => button.Name == "AcceptButton"));
            }
            catch (Exception error)
            {
                failure = error; timer.Stop();
                foreach (Window window in Application.Current.Windows.Cast<Window>().Where(window => window != owner).ToArray()) window.Close();
            }
        };
        timer.Start();
        try
        {
            Click((Button)owner.FindName("SaveMenuBtn")); Pump();
            string label = (string)Application.Current.FindResource("Str_Menu_SaveAs");
            MenuItem item = PresentationSource.CurrentSources.Cast<PresentationSource>().Select(source => source.RootVisual)
                .OfType<DependencyObject>().SelectMany(Children).OfType<MenuItem>().First(item => Equals(item.Header, label));
            if (ItemsControl.ItemsControlFromItemContainer(item) is ContextMenu menu) menu.IsOpen = false;
            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Wait(() => failure is not null || File.Exists(destination));
            if (failure is not null) throw failure;
        }
        finally { timer.Stop(); }
    }

    internal static void RunSearchAction(Window owner, string key, string shortcut)
    {
        var host = (FrameworkElement)owner.FindName("ToolSearchHost");
        Click(Children(host).OfType<Button>().Single()); Pump();
        string label = (string)Application.Current.FindResource(key);
        Children(host).OfType<TextBox>().Single().Text = label; Pump();
        // The results drop down once the search box has finished growing, so wait for the row.
        Border[] Rows() => [.. PresentationSource.CurrentSources.Cast<PresentationSource>().Select(source => source.RootVisual)
            .OfType<DependencyObject>().SelectMany(Children).OfType<Border>().Where(border =>
                AutomationProperties.GetName(border) == label + " (" + shortcut + ")")];
        Wait(() => Rows().Length != 0);
        Border row = Rows().Single();
        row.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            { RoutedEvent = UIElement.MouseLeftButtonUpEvent }); Pump();
    }

    private static void Show(Window window)
    {
        window.ShowInTaskbar = false; window.ShowActivated = false;
        window.WindowStartupLocation = WindowStartupLocation.Manual; window.Left = -10000; window.Top = -10000;
        window.Show(); Pump();
    }
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static T Find<T>(DependencyObject root, string id) where T : DependencyObject =>
        Children(root).OfType<T>().Single(item => AutomationProperties.GetAutomationId(item) == id);
    private static IEnumerable<DependencyObject> Children(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (DependencyObject child in Children(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static void Wait(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            Pump(); Thread.Sleep(10);
            if (watch.Elapsed.TotalSeconds > 30) throw new TimeoutException("Paragraph review did not reach the expected state.");
        }
        Pump();
    }
    private static void Pump() => Application.Current.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    internal static byte[] Fixture(bool rotated = false)
    {
        const string first = "BT /F1 12 Tf 16 TL 40 220 Td (Original paragraph first line.) Tj T* (Original paragraph second line.) Tj ET\nBT /F1 12 Tf 40 100 Td (Unchanged lower paragraph.) Tj ET\n";
        const string second = "BT /F1 12 Tf 40 220 Td (Unchanged second page.) Tj ET\n";
        string[] objects = ["<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R 6 0 R] /Count 2 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Rotate {(rotated ? 90 : 0)} /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", $"<< /Length {first.Length} >>\nstream\n{first}endstream",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Resources << /Font << /F1 4 0 R >> >> /Contents 7 0 R >>",
            $"<< /Length {second.Length} >>\nstream\n{second}endstream"];
        var pdf = new StringBuilder("%PDF-1.7\n"); var offsets = new List<int> { 0 };
        for (int i = 0; i < objects.Length; i++) { offsets.Add(pdf.Length); pdf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n"); }
        int xref = pdf.Length; pdf.Append($"xref\n0 {offsets.Count}\n0000000000 65535 f \n");
        foreach (int offset in offsets.Skip(1)) pdf.Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        pdf.Append($"trailer\n<< /Size {offsets.Count} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
