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

internal static class AccessibilityReviewSmoke
{
    internal static void ConfigureOffscreen(Assembly assembly)
    {
        foreach (Type type in assembly.GetTypes().Where(type => typeof(Window).IsAssignableFrom(type)))
        {
            Window.ShowActivatedProperty.OverrideMetadata(type, new FrameworkPropertyMetadata(false));
            Window.VisibilityProperty.OverrideMetadata(type, new FrameworkPropertyMetadata(Visibility.Collapsed,
                (_, args) =>
                {
                    if (args.NewValue is not Visibility.Visible) return;
                    var window = (Window)_;
                    window.ShowActivated = false; window.ShowInTaskbar = false;
                    // A window that restored a saved position before its first show (the app's startup
                    // placement) keeps it; parking it here would hide the saved place and let the
                    // work-area fit drag the window to the corner. Only unplaced windows are parked.
                    bool placed = window.WindowStartupLocation == WindowStartupLocation.Manual
                        && !double.IsNaN(window.Left) && !double.IsNaN(window.Top);
                    if (placed) return;
                    window.WindowStartupLocation = WindowStartupLocation.Manual;
                    window.Left = -10000; window.Top = -10000;
                }));
        }
    }

    internal static int Run(Window owner, Assembly assembly, string appPath, string folder)
    {
        const string missingLanguage = "MissingDocumentLanguage";
        Assembly engine = Assembly.Load("KillerPdf.Engine");
        byte[] original = ParagraphEditSmoke.Fixture();
        if (!FindingCodes(engine, original).Contains(missingLanguage))
            throw new InvalidOperationException("The fixture must report a missing document language before review.");
        string source = Path.Combine(folder, "language-source.pdf");
        File.WriteAllBytes(source, original);
        Open(owner, source);
        int stage = 0;
        Exception? failure = null;
        bool done = false;
        var watch = Stopwatch.StartNew();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(25) };
        timer.Tick += (_, _) =>
        {
            try
            {
                if (watch.Elapsed.TotalSeconds > 40) throw new TimeoutException("Accessibility review timed out at stage " + stage);
                Window? dialog = Application.Current.Windows.Cast<Window>().FirstOrDefault(window => window != owner
                    && window.IsVisible && window.GetType().FullName == "KillerPDF.AccessibilityReviewDialog");
                if (stage == 0)
                {
                    stage = 1;
                    owner.Dispatcher.BeginInvoke(() => RunSearchAction(owner));
                    return;
                }
                if (stage == 1 && dialog is not null)
                {
                    ListBox findings =Find<ListBox>(dialog, "A11yFindings");
                    findings.SelectedItem = findings.Items.Cast<ListBoxItem>().Single(item => Equals(item.Tag, missingLanguage));
                    Find<TextBox>(dialog, "A11yInput").Text = "en-US";
                    stage = 2;
                    return;
                }
                if (stage == 2 && dialog is not null && Find<Button>(dialog, "A11yApply").IsEnabled)
                {
                    stage = 3;
                    Click(Find<Button>(dialog, "A11yApply"));
                    return;
                }
                if (stage == 3 && dialog is not null)
                {
                    if (Find<ListBox>(dialog, "A11yFindings").Items.Cast<ListBoxItem>().Any(item => Equals(item.Tag, missingLanguage))) return;
                    stage = 4;
                    Click(Find<Button>(dialog, "A11yClose"));
                    return;
                }
                if (stage == 4 && dialog is null && owner.IsEnabled) { done = true; timer.Stop(); }
            }
            catch (Exception error) { failure = error; done = true; timer.Stop(); CloseTransient(owner); }
        };
        timer.Start();
        try
        {
            Wait(() => done, "language review");
            if (failure is not null) throw failure;
        }
        finally { timer.Stop(); CloseTransient(owner); }
        string saved = Path.Combine(folder, "language-saved.pdf");
        SaveAs(owner, saved);
        if (FindingCodes(engine, File.ReadAllBytes(saved)).Contains(missingLanguage))
            throw new InvalidOperationException("Saved PDF still reports the missing document language.");
        if (!File.ReadAllBytes(source).AsSpan().SequenceEqual(original))
            throw new InvalidOperationException("Review changed the source file before Save As.");
        Open(owner, saved);
        Console.WriteLine("PASS language review and saved-byte inspection.");
        RunDescriptions(owner, engine, folder);
        VerifyHostFailure(owner, assembly);
        VerifyHostFailure(owner, assembly, undoFailure: true);
        RunSafety(owner, engine, folder, "close");
        RunSafety(owner, engine, folder, "invalid-language");
        RunSafety(owner, engine, folder, "dialog-undo");
        RunSafety(owner, engine, folder, "source-changed");
        RunSafety(owner, engine, folder, "source-switched");
        VerifyCloseDuringApply(owner, assembly);
        VerifyManualTargets(owner, engine, folder);
        VerifySignatureGuard(owner, engine, folder);
        VerifyReadOnly(owner, assembly);
        VerifyLayouts(owner, assembly, folder);
        string result = "PASS accessibility review: language/figure/link/field fixes; public host Save As/reopen/Undo/Redo; dialog Undo, validation, close, stale-source, manual and read-only guards; Apply/Undo host failures; 14 themes, 20 locales, 70/100/150/250 percent scales, 480x360 reachability and decoded nonblank PNGs. WPF routed events; no native input or screen-reader certification. App SHA256="
            + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(appPath)));
        File.WriteAllText(Path.Combine(folder, "result.txt"), result);
        Console.WriteLine(result);
        return 0;
    }

    private static void VerifyHostFailure(Window owner, Assembly assembly, bool undoFailure = false)
    {
        byte[] original = ParagraphEditSmoke.Fixture();
        byte[] hostBytes = original;
        int applied = 0, undone = 0;
        Func<byte[], byte[]?> apply = bytes =>
        {
            applied++; hostBytes = bytes;
            if (!undoFailure) throw new IOException("Owned host snapshot failure after mutation.");
            return bytes;
        };
        Func<byte[]?> undo = () =>
        {
            undone++; hostBytes = original;
            if (undoFailure) throw new IOException("Owned Undo snapshot failure after mutation.");
            return original;
        };
        var dialog = (Window)Activator.CreateInstance(assembly.GetType("KillerPDF.AccessibilityReviewDialog", true)!,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null,
            [owner, original, 1d, true, apply, undo, (Func<bool>)(() => true), null, null], null)!;
        try
        {
            dialog.Show(); Pump();
            ListBox findings =Find<ListBox>(dialog, "A11yFindings");
            findings.SelectedItem = findings.Items.Cast<ListBoxItem>().Single(item => Equals(item.Tag, "MissingDocumentLanguage"));
            Find<TextBox>(dialog, "A11yInput").Text = "en-US";
            Click(Find<Button>(dialog, "A11yApply"));
            if (undoFailure)
            {
                Wait(() => !findings.Items.Cast<ListBoxItem>().Any(item => Equals(item.Tag, "MissingDocumentLanguage")), "before failing Undo");
                Click(Find<Button>(dialog, "A11yUndo"));
            }
            Wait(() => Find<TextBlock>(dialog, "A11yStatus").Text.Length != 0, "host snapshot failure");
            if (applied != 1 || undone != (undoFailure ? 1 : 0) || hostBytes.AsSpan().SequenceEqual(original) != undoFailure)
                throw new InvalidOperationException("The owned host/storage boundary did not mutate before failing.");
            foreach (string id in new[] { "A11yApply", "A11yUndo", "A11yRecheck" })
                if (Find<Button>(dialog, id).IsEnabled)
                    throw new InvalidOperationException("Host failure left stale review usable: " + id);
            Find<TextBox>(dialog, "A11yInput").Text = "fr-FR";
            foreach (string id in new[] { "A11yApply", "A11yUndo", "A11yRecheck" }) Click(Find<Button>(dialog, id));
            Pump();
            if (applied != 1 || undone != (undoFailure ? 1 : 0) || Find<TextBox>(dialog, "A11yInput").IsEnabled)
                throw new InvalidOperationException("Stale review called the host again after mutation failure.");
        }
        finally { dialog.Close(); Wait(() => !dialog.IsVisible, "closing failed host review"); }
        Console.WriteLine(undoFailure ? "PASS Undo host/storage failure invalidates the review." : "PASS host/storage mutation failure invalidates the review.");
    }

    private static void VerifyCloseDuringApply(Window owner, Assembly assembly)
    {
        int applied = 0;
        Func<byte[], byte[]?> apply = bytes => { applied++; return bytes; };
        var dialog = (Window)Activator.CreateInstance(assembly.GetType("KillerPDF.AccessibilityReviewDialog", true)!,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null,
            [owner, ParagraphEditSmoke.Fixture(), 1d, true, apply, (Func<byte[]?>)(() => null), (Func<bool>)(() => true), null, null], null)!;
        try
        {
            dialog.Show(); Pump();
            Find<TextBox>(dialog, "A11yInput").Text = "en-US";
            Click(Find<Button>(dialog, "A11yApply"));
            if (applied != 0) throw new InvalidOperationException("Apply completed before the pending-close test could close the window.");
            Click(Find<Button>(dialog, "A11yClose"));
            Wait(() => !dialog.IsVisible, "closing pending Apply");
            if (applied != 0) throw new InvalidOperationException("Closing during pending Apply still mutated the host.");
        }
        finally { dialog.Close(); Pump(); }
        Console.WriteLine("PASS closing during pending Apply cancels host mutation.");
    }

    private static void VerifyLayouts(Window owner, Assembly assembly, string folder)
    {
        string captures = Path.Combine(folder, "captures"); Directory.CreateDirectory(captures);
        Type type = assembly.GetType("KillerPDF.AccessibilityReviewDialog", true)!;
        void Layout(string name, double scale, double width, double height)
        {
            var window = (Window)Activator.CreateInstance(type, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null, [owner, DescriptionFixture(), scale, true, (Func<byte[], byte[]?>)(bytes => bytes),
                    (Func<byte[]?>)(() => null), (Func<bool>)(() => true), null, null], null)!;
            try
            {
                window.Opacity = 1; window.Show(); window.SizeToContent = SizeToContent.Manual;
                window.Width = width; window.Height = height; Pump();
                Wait(() => Find<Image>(window, "A11yPreview").Source is not null, "rendered page preview");
                Find<TextBox>(window, "A11yInput").Text = "A blue square in the chart";
                Capture(window, Path.Combine(captures, name + ".png"));
                foreach (string id in new[] { "A11yFindings", "A11yInput", "A11yApply", "A11yUndo", "A11yRecheck", "A11yReadingOrder", "A11yTableHeader", "A11yCopy", "A11yClose" })
                {
                    FrameworkElement control = Find<FrameworkElement>(window, id); control.BringIntoView(); Pump();
                    Rect bounds = control.TransformToAncestor(window).TransformBounds(new Rect(control.RenderSize));
                    var available = new Rect(0, 0, window.ActualWidth, window.ActualHeight); available.Inflate(1, 1);
                    if (!control.IsVisible || bounds.Width <= 0 || bounds.Height <= 0 || !available.Contains(bounds))
                        throw new InvalidOperationException(name + ": unreachable control " + id + " " + bounds);
                    for (DependencyObject? ancestor = VisualTreeHelper.GetParent(control); ancestor is not null; ancestor = VisualTreeHelper.GetParent(ancestor))
                    {
                        if (ancestor is not ScrollContentPresenter presenter) continue;
                        Rect visible = presenter.TransformToAncestor(window).TransformBounds(new Rect(presenter.RenderSize)); visible.Inflate(1, 1);
                        if (!visible.Contains(bounds)) throw new InvalidOperationException(name + ": control clipped by scroll viewport " + id + " " + bounds + " viewport " + visible);
                    }
                }
                Capture(window, Path.Combine(captures, name + "-actions.png"));
                Console.WriteLine("PASS rendered reachability and decoded PNGs: " + name);
            }
            catch { Capture(window, Path.Combine(captures, name + "-failure.png")); throw; }
            finally { window.Close(); Wait(() => !window.IsVisible, "closing layout case"); }
        }
        Type themeType = assembly.GetType("KillerPDF.Services.Theme", true)!;
        MethodInfo applyTheme = assembly.GetType("KillerPDF.Services.ThemeManager", true)!
            .GetMethod("Apply", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        Type localeType = assembly.GetType("KillerPDF.Services.Locale", true)!;
        MethodInfo applyLocale = assembly.GetType("KillerPDF.Services.LocaleManager", true)!
            .GetMethod("Apply", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        string[] themes = Enum.GetNames(themeType), locales = Enum.GetNames(localeType);
        if (themes.Length != 14 || locales.Length != 20) throw new InvalidOperationException("Expected 14 themes and 20 locales.");
        try
        {
            foreach (string theme in themes)
            {
                applyTheme.Invoke(null, [Enum.Parse(themeType, theme)]); Layout(theme + "-100", 1, 800, 600);
            }
            applyTheme.Invoke(null, [Enum.Parse(themeType, "Dark")]);
            Layout("Dark-70", .7, 640, 480);
            Layout("Dark-150", 1.5, 640, 480);
            Layout("small-250", 2.5, 480, 360);
            foreach (string locale in locales)
            {
                applyLocale.Invoke(null, [Enum.Parse(localeType, locale)]); Layout("locale-" + locale, 1.5, 640, 480);
            }
        }
        finally
        {
            applyLocale.Invoke(null, [Enum.Parse(localeType, "EnUS")]);
            applyTheme.Invoke(null, [Enum.Parse(themeType, "Dark")]);
        }
        Console.WriteLine("PASS 14 themes, 20 locales, 70/100/150/250 percent scales and small-window reachability.");
    }

    private static void Capture(Window window, string path)
    {
        window.UpdateLayout();
        var rendered = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        rendered.Render(window); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(rendered));
        using (Stream file = File.Create(path)) png.Save(file);
        BitmapSource decoded = BitmapDecoder.Create(new Uri(path), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        var converted = new FormatConvertedBitmap(decoded, PixelFormats.Bgra32, null, 0);
        byte[] pixels = new byte[converted.PixelWidth * converted.PixelHeight * 4]; converted.CopyPixels(pixels, converted.PixelWidth * 4, 0);
        int visible = 0; var colors = new HashSet<uint>();
        for (int i = 0; i < pixels.Length; i += 4) { if (pixels[i + 3] > 0) visible++; if (colors.Count < 64) colors.Add(BitConverter.ToUInt32(pixels, i)); }
        if (visible < converted.PixelWidth * converted.PixelHeight / 3 || colors.Count < 16)
            throw new InvalidOperationException("Rendered PNG is blank or nearly transparent: " + path);
    }

    private static void VerifyReadOnly(Window owner, Assembly assembly)
    {
        int mutations = 0;
        var dialog = (Window)Activator.CreateInstance(assembly.GetType("KillerPDF.AccessibilityReviewDialog", true)!,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null,
            [owner, ParagraphEditSmoke.Fixture(), 1d, false,
                (Func<byte[], byte[]?>)(bytes => { mutations++; return bytes; }),
                (Func<byte[]?>)(() => { mutations++; return null; }), (Func<bool>)(() => true), null, null], null)!;
        try
        {
            dialog.Show(); Pump();
            foreach (string id in new[] { "A11yApply", "A11yUndo", "A11yReadingOrder", "A11yTableHeader" })
            {
                if (Find<Button>(dialog, id).IsEnabled) throw new InvalidOperationException("Read-only capability left a mutation action enabled: " + id);
                Click(Find<Button>(dialog, id));
            }
            Click(Find<Button>(dialog, "A11yRecheck")); Pump();
            if (mutations != 0 || Find<TextBox>(dialog, "A11yInput").IsEnabled
                || Find<TextBlock>(dialog, "A11yStatus").Text != (string)Application.Current.FindResource("Str_A11y_ReadOnly"))
                throw new InvalidOperationException("Read-only capability did not preserve inspection without mutation.");
        }
        finally { dialog.Close(); Wait(() => !dialog.IsVisible, "closing read-only review"); }
        Console.WriteLine("PASS read-only capability boundary blocks mutation and retains recheck (no encrypted-file interoperability claim).");
    }

    private static void VerifySignatureGuard(Window owner, Assembly engine, string folder)
    {
        byte[] original = BuildFixture([
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [4 0 R] >>",
            "<< /Type /Annot /Subtype /Widget /FT /Sig /T (OwnedSignature) /TU (Owned signature marker) /Rect [10 10 80 30] /V 5 0 R >>",
            "<< /Type /Sig /Filter /Adobe.PPKLite /SubFilter /adbe.pkcs7.detached /ByteRange [0 1 2 1] /Contents <0102> >>"]);
        object document = engine.GetType("KillerPdf.Engine.Documents.PdfDocument", true)!
            .GetMethod("Open", [typeof(ReadOnlyMemory<byte>)])!.Invoke(null, [new ReadOnlyMemory<byte>(original)])!;
        try
        {
            object[] signatures = ((IEnumerable)engine.GetType("KillerPdf.Engine.Signing.PdfSignatureReader", true)!
                .GetMethod("Read", [document.GetType()])!.Invoke(null, [document])!).Cast<object>().ToArray();
            if (signatures.Length != 1 || !(bool)signatures[0].GetType().GetProperty("IsSigned")!.GetValue(signatures[0])!)
                throw new InvalidOperationException("Signature-bearing fixture does not reach the host signature guard.");
        }
        finally { (document as IDisposable)?.Dispose(); }
        string source = Path.Combine(folder, "signature-marker-source.pdf"); File.WriteAllBytes(source, original); Open(owner, source);
        DriveReview(owner, dialog =>
        {
            foreach (string id in new[] { "A11yApply", "A11yUndo", "A11yReadingOrder", "A11yTableHeader" })
                if (Find<Button>(dialog, id).IsEnabled) throw new InvalidOperationException("Signature guard left a mutation action enabled: " + id);
            if (Find<TextBox>(dialog, "A11yInput").IsEnabled
                || Find<TextBlock>(dialog, "A11yStatus").Text != (string)Application.Current.FindResource("Str_A11y_ReadOnly"))
                throw new InvalidOperationException("Signature-bearing review was not read-only.");
            Find<TextBox>(dialog, "A11yInput").Text = "en-US"; Click(Find<Button>(dialog, "A11yApply"));
        });
        string saved = Path.Combine(folder, "signature-marker-saved.pdf"); SaveAs(owner, saved);
        if (!FindingCodes(engine, File.ReadAllBytes(saved)).Contains("MissingDocumentLanguage") || !File.ReadAllBytes(source).AsSpan().SequenceEqual(original))
            throw new InvalidOperationException("Signature-bearing review changed the document.");
        Open(owner, saved);
        Console.WriteLine("PASS real host signature-presence guard (synthetic marker; no cryptographic signature validation claimed).");
    }

    private static void VerifyManualTargets(Window owner, Assembly engine, string folder)
    {
        byte[] original = BuildFixture([
            "<< /Type /Catalog /Pages 2 0 R /Lang (en-US) /MarkInfo << /Marked true >> /StructTreeRoot << /Type /StructTreeRoot /K 4 2 R >> /AcroForm << /Fields [5 0 R] >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [<< /Type /Annot /Subtype /Link /Rect [10 10 50 20] /A << /S /URI /URI (https://direct.test) >> >> << /Type /Annot /Subtype /Widget /Rect [10 40 100 60] /Parent 5 0 R >>] >>",
            "<< /Type /StructElem /S /Figure /Pg 3 0 R /K 0 >>",
            "<< /FT /Tx /T (direct.field) /V (Unchanged direct field) >>"], nonzeroObject: 4);
        string[] expected = ["MissingFigureAlternateDescription", "MissingLinkDescription", "MissingFormFieldDescription"];
        if (!FindingCodes(engine, original).SetEquals(expected)) throw new InvalidOperationException("Manual fixture does not contain the expected unsupported targets.");
        string source = Path.Combine(folder, "manual-source.pdf"); File.WriteAllBytes(source, original); Open(owner, source);
        DriveReview(owner, dialog =>
        {
            ListBox findings =Find<ListBox>(dialog, "A11yFindings");
            foreach (string code in expected)
            {
                findings.SelectedItem = findings.Items.Cast<ListBoxItem>().Single(item => Equals(item.Tag, code)); Pump();
                string manual = (string)Application.Current.FindResource("Str_A11y_Manual");
                if (Find<TextBox>(dialog, "A11yInput").IsEnabled || Find<Button>(dialog, "A11yApply").IsEnabled
                    || !Children(dialog).OfType<TextBlock>().Any(block => block.Text.Contains(manual, StringComparison.Ordinal)))
                    throw new InvalidOperationException("Unsupported target was not presented as manual review: " + code);
                Find<TextBox>(dialog, "A11yInput").Text = "Must stay unchanged"; Click(Find<Button>(dialog, "A11yApply"));
            }
        });
        string saved = Path.Combine(folder, "manual-saved.pdf"); SaveAs(owner, saved);
        if (!FindingCodes(engine, File.ReadAllBytes(saved)).SetEquals(expected) || !File.ReadAllBytes(source).AsSpan().SequenceEqual(original))
            throw new InvalidOperationException("Manual review changed an unsupported target or source.");
        Open(owner, saved);
        Console.WriteLine("PASS direct link/widget and nonzero-generation figure remain manual and unchanged.");
    }

    private static byte[] BuildFixture(string[] objects, int nonzeroObject = -1)
    {
        var pdf = new StringBuilder("%PDF-1.7\n"); var offsets = new List<int> { 0 };
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(pdf.Length); int generation = i + 1 == nonzeroObject ? 2 : 0;
            pdf.Append($"{i + 1} {generation} obj\n{objects[i]}\nendobj\n");
        }
        int xref = pdf.Length; pdf.Append($"xref\n0 {offsets.Count}\n0000000000 65535 f \n");
        for (int i = 1; i < offsets.Count; i++)
            pdf.Append(offsets[i].ToString("D10", System.Globalization.CultureInfo.InvariantCulture))
                .Append(i == nonzeroObject ? " 00002 n \n" : " 00000 n \n");
        pdf.Append($"trailer\n<< /Size {offsets.Count} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }

    private static void DriveReview(Window owner, Action<Window> action)
    {
        int stage = 0; bool done = false;
        Exception? failure = null; Window? review = null;
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(25) };
        timer.Tick += (_, _) =>
        {
            try
            {
                if (stage == 0) { stage = 1; owner.Dispatcher.BeginInvoke(() => RunSearchAction(owner)); return; }
                review = Application.Current.Windows.Cast<Window>().FirstOrDefault(window => window != owner
                    && window.IsVisible && window.GetType().FullName == "KillerPDF.AccessibilityReviewDialog");
                if (review is null) return;
                timer.Stop(); action(review); Click(Find<Button>(review, "A11yClose")); done = true;
            }
            catch (Exception error) { failure = error; done = true; timer.Stop(); CloseTransient(owner); }
        };
        timer.Start();
        try
        {
            Wait(() => done && (failure is not null || review?.IsVisible != true && owner.IsEnabled), "public review workflow");
            if (failure is not null) throw failure;
        }
        finally { timer.Stop(); CloseTransient(owner); }
    }

    private static void RunSafety(Window owner, Assembly engine, string folder, string mode)
    {
        byte[] original = ParagraphEditSmoke.Fixture();
        byte[] expectedSource = original;
        string source = Path.Combine(folder, "safety-" + mode + "-source.pdf"); File.WriteAllBytes(source, original); Open(owner, source);
        DriveReview(owner, dialog =>
        {
            var findings = Find<ListBox>(dialog, "A11yFindings");
            findings.SelectedItem = findings.Items.Cast<ListBoxItem>().Single(item => Equals(item.Tag, "MissingDocumentLanguage"));
            Find<TextBox>(dialog, "A11yInput").Text = "en-US";
            if (mode == "invalid-language")
            {
                Find<TextBox>(dialog, "A11yInput").Text = "not_a_language";
                Click(Find<Button>(dialog, "A11yApply"));
                Wait(() => Find<Button>(dialog, "A11yApply").IsEnabled && Find<TextBlock>(dialog, "A11yStatus").Text.Length != 0, "invalid language rejected");
                if (!findings.Items.Cast<ListBoxItem>().Any(item => Equals(item.Tag, "MissingDocumentLanguage"))
                    || Find<Button>(dialog, "A11yUndo").IsEnabled)
                    throw new InvalidOperationException("Invalid language changed review findings or created an Undo entry.");
                Find<TextBox>(dialog, "A11yInput").Text = "en-US";
                if (!Find<Button>(dialog, "A11yApply").IsEnabled) throw new InvalidOperationException("Validation failure prevented correction.");
            }
            else if (mode == "dialog-undo")
            {
                if (Find<Button>(dialog, "A11yUndo").IsEnabled) throw new InvalidOperationException("Dialog Undo enabled before its own fix.");
                Click(Find<Button>(dialog, "A11yApply"));
                Wait(() => !findings.Items.Cast<ListBoxItem>().Any(item => Equals(item.Tag, "MissingDocumentLanguage")), "dialog fix before Undo");
                Click(Find<Button>(dialog, "A11yUndo"));
                Wait(() => findings.Items.Cast<ListBoxItem>().Any(item => Equals(item.Tag, "MissingDocumentLanguage")), "dialog Undo restores finding");
                if (Find<Button>(dialog, "A11yUndo").IsEnabled) throw new InvalidOperationException("Dialog Undo escaped its own fix history.");
            }
            else if (mode == "source-changed")
            {
                // KillerPDF reviews its own complete copy of the document, so another program
                // changing the user's file meanwhile does not make the review stale. The fix applies
                // to KillerPDF's copy and leaves the user's file as the other program wrote it.
                if (!File.ReadAllBytes(source).AsSpan().SequenceEqual(original)) throw new InvalidOperationException("Owned stale-source fixture changed unexpectedly.");
                expectedSource = original.Concat(Encoding.ASCII.GetBytes("\n% owned external change\n")).ToArray();
                File.WriteAllBytes(source, expectedSource);
                Click(Find<Button>(dialog, "A11yApply"));
                Wait(() => !findings.Items.Cast<ListBoxItem>().Any(item => Equals(item.Tag, "MissingDocumentLanguage")), "fix applied after an outside change");
            }
            else if (mode == "source-switched")
            {
                string replacement = Path.Combine(folder, "replacement-source.pdf");
                File.WriteAllBytes(replacement, DescriptionFixture()); Open(owner, replacement);
                Click(Find<Button>(dialog, "A11yApply"));
                string expected = (string)Application.Current.FindResource("Str_A11y_Changed");
                Wait(() => Find<TextBlock>(dialog, "A11yStatus").Text == expected, "external source change rejected");
                if (Find<Button>(dialog, "A11yApply").IsEnabled || Find<Button>(dialog, "A11yUndo").IsEnabled)
                    throw new InvalidOperationException("A changed source left mutation controls enabled.");
            }
            else if (mode != "close") throw new InvalidOperationException("Unknown safety case: " + mode);
        });
        string saved = Path.Combine(folder, "safety-" + mode + "-saved.pdf"); SaveAs(owner, saved);
        HashSet<string> savedFindings = FindingCodes(engine, File.ReadAllBytes(saved));
        if (savedFindings.Contains("MissingDocumentLanguage") == (mode is "source-switched" or "source-changed"))
            throw new InvalidOperationException("Safety case changed the saved document: " + mode);
        if (mode == "source-switched" && (!savedFindings.SetEquals(["MissingFigureAlternateDescription", "MissingLinkDescription", "MissingFormFieldDescription"])
            || !File.ReadAllBytes(Path.Combine(folder, "replacement-source.pdf")).AsSpan().SequenceEqual(DescriptionFixture())))
            throw new InvalidOperationException("Stale review changed the replacement document.");
        if (!File.ReadAllBytes(source).AsSpan().SequenceEqual(expectedSource)) throw new InvalidOperationException("Safety case changed source bytes: " + mode);
        Open(owner, saved);
        Console.WriteLine("PASS public review safety case: " + mode);
    }

    private static void RunDescriptions(Window owner, Assembly engine, string folder)
    {
        string[] codes = ["MissingFigureAlternateDescription", "MissingLinkDescription", "MissingFormFieldDescription"];
        string[] descriptions = ["A blue square in the chart", "Customer portal", "Customer name"];
        byte[] original = DescriptionFixture();
        if (!FindingCodes(engine, original).SetEquals(codes))
            throw new InvalidOperationException("Description fixture must expose exactly one figure, link and field finding.");
        string source = Path.Combine(folder, "descriptions-source.pdf"); File.WriteAllBytes(source, original); Open(owner, source);
        int target = 0, stage = 0;
        bool done = false;
        Exception? failure = null;
        var watch = Stopwatch.StartNew();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(25) };
        timer.Tick += (_, _) =>
        {
            try
            {
                if (watch.Elapsed.TotalSeconds > 40) throw new TimeoutException("Description review timed out at " + stage + ":" + target);
                Window? dialog = Application.Current.Windows.Cast<Window>().FirstOrDefault(window => window != owner
                    && window.IsVisible && window.GetType().FullName == "KillerPDF.AccessibilityReviewDialog");
                if (stage == 0) { stage = 1; owner.Dispatcher.BeginInvoke(() => RunSearchAction(owner)); return; }
                if (stage == 1 && dialog is not null)
                {
                    ListBox findings =Find<ListBox>(dialog, "A11yFindings");
                    findings.SelectedItem = findings.Items.Cast<ListBoxItem>().Single(item => Equals(item.Tag, codes[target]));
                    if (!Find<TextBox>(dialog, "A11yInput").IsEnabled)
                        throw new InvalidOperationException("Supported description finding is not editable: " + codes[target]);
                    Find<TextBox>(dialog, "A11yInput").Text = descriptions[target]; stage = 2; return;
                }
                if (stage == 2 && dialog is not null && Find<Image>(dialog, "A11yPreview").Source is not null)
                {
                    var bitmap = (System.Windows.Media.Imaging.BitmapSource)Find<Image>(dialog, "A11yPreview").Source;
                    if (bitmap.PixelWidth != 800 || bitmap.PixelHeight != 900)
                        throw new InvalidOperationException("Crop and rotation preview did not produce the expected 800x900 page.");
                    if (target > 0)
                    {
                        var highlight = Find<System.Windows.Shapes.Rectangle>(dialog, "A11yHighlight");
                        Rect expected = target == 1 ? new Rect(50, 50, 100, 300) : new Rect(250, 100, 100, 450);
                        Rect actual = new(Canvas.GetLeft(highlight), Canvas.GetTop(highlight), highlight.Width, highlight.Height);
                        if (!highlight.IsVisible || Math.Abs(actual.X - expected.X) > .01 || Math.Abs(actual.Y - expected.Y) > .01
                            || Math.Abs(actual.Width - expected.Width) > .01 || Math.Abs(actual.Height - expected.Height) > .01)
                            throw new InvalidOperationException("Selected annotation highlight differs from literal expected bounds: " + actual + " expected " + expected);
                    }
                    if (!Find<Button>(dialog, "A11yApply").IsEnabled) throw new InvalidOperationException("Description did not enable Apply.");
                    stage = 3; Click(Find<Button>(dialog, "A11yApply")); return;
                }
                if (stage == 3 && dialog is not null)
                {
                    if (Find<ListBox>(dialog, "A11yFindings").Items.Cast<ListBoxItem>().Any(item => Equals(item.Tag, codes[target]))) return;
                    if (++target < codes.Length) { stage = 1; return; }
                    stage = 4; Click(Find<Button>(dialog, "A11yClose")); return;
                }
                if (stage == 4 && dialog is null && owner.IsEnabled) { done = true; timer.Stop(); }
            }
            catch (Exception error) { failure = error; done = true; timer.Stop(); CloseTransient(owner); }
        };
        timer.Start();
        try { Wait(() => done, "description review"); if (failure is not null) throw failure; }
        finally { timer.Stop(); CloseTransient(owner); }
        string saved = Path.Combine(folder, "descriptions-saved.pdf"); SaveAs(owner, saved);
        byte[] edited = File.ReadAllBytes(saved);
        if (FindingCodes(engine, edited).Count != 0) throw new InvalidOperationException("Saved description repairs left accessibility findings.");
        object document = engine.GetType("KillerPdf.Engine.Documents.PdfDocument", true)!
            .GetMethod("Open", [typeof(ReadOnlyMemory<byte>)])!.Invoke(null, [new ReadOnlyMemory<byte>(edited)])!;
        try
        {
            object[] Read(string type) => ((IEnumerable)engine.GetType("KillerPdf.Engine.Documents." + type, true)!
                .GetMethod("ReadPage", [document.GetType(), typeof(int)])!.Invoke(null, [document, 0])!).Cast<object>().ToArray();
            string? Value(object item, string property) => item.GetType().GetProperty(property)!.GetValue(item)?.ToString();
            object[] links = Read("PdfLinkReader"), widgets = Read("PdfFormWidgetReader");
            if (links.Length != 2 || Value(links[0], "Description") != "Customer portal" || Value(links[1], "Description") != "Preserved link"
                || Value(links[0], "Uri") != "https://customer.test" || Value(links[1], "Uri") != "https://preserved.test")
                throw new InvalidOperationException("Selected link repair changed a destination or sibling description.");
            if (widgets.Length != 2 || Value(widgets[0], "Tooltip") != "Customer name" || Value(widgets[1], "Tooltip") != "Preserved field tooltip"
                || Value(widgets[0], "Value") != "Unchanged field value" || Value(widgets[1], "Value") != "Preserved sibling value")
                throw new InvalidOperationException("Selected field repair changed a value or sibling description.");
            object reader = Activator.CreateInstance(engine.GetType("KillerPdf.Engine.Documents.PdfPageContentReader", true)!, [document])!;
            object content = reader.GetType().GetMethod("Read")!.Invoke(reader, [0, CancellationToken.None])!;
            if (!Value(content, "Text")!.Contains("Unrelated page text", StringComparison.Ordinal))
                throw new InvalidOperationException("Description repair changed unrelated page content.");
        }
        finally { (document as IDisposable)?.Dispose(); }
        if (!File.ReadAllBytes(source).AsSpan().SequenceEqual(original)) throw new InvalidOperationException("Description workflow changed the source PDF.");
        Click((Button)owner.FindName("UndoBtn")); Pump();
        string undone = Path.Combine(folder, "description-undo.pdf"); SaveAs(owner, undone);
        if (!FindingCodes(engine, File.ReadAllBytes(undone)).SetEquals(["MissingFormFieldDescription"]))
            throw new InvalidOperationException("Normal Undo did not restore only the most recent field description finding.");
        Click((Button)owner.FindName("RedoBtn")); Pump();
        string redone = Path.Combine(folder, "description-redo.pdf"); SaveAs(owner, redone);
        if (FindingCodes(engine, File.ReadAllBytes(redone)).Count != 0)
            throw new InvalidOperationException("Normal Redo did not restore the field description repair.");
        Open(owner, redone);
        Console.WriteLine("PASS normal host Undo/Redo, Save As and reopen.");
        Console.WriteLine("PASS selected figure, indirect link and field descriptions, rotated/cropped preview, saved-byte inspection and sibling preservation.");
    }

    private static byte[] DescriptionFixture()
    {
        const string content = "/Figure << /MCID 0 >> BDC 0 0 1 rg 30 110 30 30 re f EMC\nBT /F1 10 Tf 25 150 Td (Unrelated page text) Tj ET\n";
        string[] objects = [
            "<< /Type /Catalog /Pages 2 0 R /Lang (en-US) /MarkInfo << /Marked true >> /StructTreeRoot 6 0 R /AcroForm << /Fields [11 0 R 12 0 R] /DA (/F1 10 Tf 0 g) /DR << /Font << /F1 4 0 R >> >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /CropBox [10 20 190 180] /Rotate 90 /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R /Annots [9 0 R 10 0 R 11 0 R 12 0 R] >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            $"<< /Length {content.Length} >>\nstream\n{content}endstream",
            "<< /Type /StructTreeRoot /K 7 0 R >>",
            "<< /Type /StructElem /S /Document /P 6 0 R /K 8 0 R >>",
            "<< /Type /StructElem /S /Figure /P 7 0 R /Pg 3 0 R /K 0 >>",
            "<< /Type /Annot /Subtype /Link /Rect [20 30 80 50] /A << /S /URI /URI (https://customer.test) >> >>",
            "<< /Type /Annot /Subtype /Link /Rect [100 30 160 50] /Contents (Preserved link) /A << /S /URI /URI (https://preserved.test) >> >>",
            "<< /Type /Annot /Subtype /Widget /P 3 0 R /FT /Tx /T (customer.name) /V (Unchanged field value) /Rect [30 70 120 90] /F 4 >>",
            "<< /Type /Annot /Subtype /Widget /P 3 0 R /FT /Tx /T (preserved.field) /TU (Preserved field tooltip) /V (Preserved sibling value) /Rect [30 100 120 120] /F 4 >>"];
        var pdf = new StringBuilder("%PDF-1.7\n"); var offsets = new List<int> { 0 };
        for (int i = 0; i < objects.Length; i++) { offsets.Add(pdf.Length); pdf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n"); }
        int xref = pdf.Length; pdf.Append($"xref\n0 {offsets.Count}\n0000000000 65535 f \n");
        foreach (int offset in offsets.Skip(1)) pdf.Append(offset.ToString("D10", System.Globalization.CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        pdf.Append($"trailer\n<< /Size {offsets.Count} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }

    private static HashSet<string> FindingCodes(Assembly engine, byte[] bytes)
    {
        Type type = engine.GetType("KillerPdf.Engine.Documents.PdfDocument", true)!;
        object document = type.GetMethod("Open", [typeof(ReadOnlyMemory<byte>)])!.Invoke(null, [new ReadOnlyMemory<byte>(bytes)])!;
        try
        {
            object report = engine.GetType("KillerPdf.Engine.Diagnostics.PdfAccessibilityInspector", true)!
                .GetMethod("Inspect")!.Invoke(null, [document])!;
            return ((IEnumerable)report.GetType().GetProperty("Findings")!.GetValue(report)!).Cast<object>()
                .Select(finding => finding.GetType().GetProperty("Code")!.GetValue(finding)!.ToString()!).ToHashSet(StringComparer.Ordinal);
        }
        finally { (document as IDisposable)?.Dispose(); }
    }

    private static void RunSearchAction(Window owner)
    {
        var host = (FrameworkElement)owner.FindName("ToolSearchHost");
        Click(Children(host).OfType<Button>().Single()); Pump();
        string label = (string)Application.Current.FindResource("Str_DocumentTools_Accessibility");
        Children(host).OfType<TextBox>().Single().Text = label; Pump();
        // The results drop down once the search box has finished growing, so wait for the row.
        Border[] Rows() => [.. PresentationSource.CurrentSources.Cast<PresentationSource>().Select(source => source.RootVisual)
            .OfType<DependencyObject>().SelectMany(Children).OfType<Border>().Where(border =>
                AutomationProperties.GetName(border) == label || AutomationProperties.GetName(border).StartsWith(label + " (", StringComparison.Ordinal))];
        Wait(() => Rows().Length != 0, "showing the search result");
        Border row = Rows().Single();
        row.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            { RoutedEvent = UIElement.MouseLeftButtonUpEvent }); Pump();
    }

    private static void SaveAs(Window owner, string destination)
    {
        Exception? failure = null;
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(25) };
        timer.Tick += (_, _) =>
        {
            try
            {
                Window? picker = Application.Current.Windows.Cast<Window>().FirstOrDefault(window => window.GetType().FullName == "KillerPDF.Controls.FileDialog");
                if (picker is null) return;
                Children(picker).OfType<TextBox>().Single(box => box.Name == "FileNameBox").Text = destination;
                timer.Stop(); Click(Children(picker).OfType<Button>().Single(button => button.Name == "AcceptButton"));
            }
            catch (Exception error) { failure = error; timer.Stop(); CloseTransient(owner); }
        };
        timer.Start();
        try
        {
            Click((Button)owner.FindName("SaveMenuBtn")); Pump();
            string label = (string)Application.Current.FindResource("Str_Menu_SaveAs");
            MenuItem item = PresentationSource.CurrentSources.Cast<PresentationSource>().Select(source => source.RootVisual)
                .OfType<DependencyObject>().SelectMany(Children).OfType<MenuItem>().Single(item => Equals(item.Header, label));
            if (ItemsControl.ItemsControlFromItemContainer(item) is ContextMenu menu) menu.IsOpen = false;
            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Wait(() => failure is not null || File.Exists(destination), "Save As");
            if (failure is not null) throw failure;
        }
        finally { timer.Stop(); }
    }

    private static void Open(Window owner, string path)
    {
        owner.GetType().GetMethod("OpenFromExternal", BindingFlags.Public | BindingFlags.Instance)!.Invoke(owner, [path]);
        Pump();
    }
    private static void CloseTransient(Window owner)
    {
        foreach (Window window in Application.Current.Windows.Cast<Window>().Where(window => window != owner).ToArray()) window.Close();
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
    private static void Pump() => Application.Current.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static void Wait(Func<bool> condition, string stage)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            Pump(); Thread.Sleep(10);
            if (watch.Elapsed.TotalSeconds > 45) throw new TimeoutException("Accessibility workflow did not finish " + stage);
        }
        Pump();
    }
}
