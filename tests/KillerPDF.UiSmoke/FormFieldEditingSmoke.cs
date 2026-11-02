using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

internal static class FormFieldEditingSmoke
{
    private const string Original = "OLD FIELD TEXT";
    private const string Sibling = "PRESERVED SIBLING";

    internal static int Run(Window owner, string appPath, string folder)
    {
        VerifyToggleKeyboard(owner, folder);
        VerifyPasswordKeyboard(owner, folder);
        foreach (bool saveAs in new[] { true, false })
        {
            string mode = saveAs ? "save-as" : "save";
            string source = Path.Combine(folder, mode + "-source.pdf");
            string destination = saveAs ? Path.Combine(folder, mode + "-cleared.pdf") : source;
            File.WriteAllBytes(source, Fixture(Original));
            Open(owner, source);
            TextBox field = Field(owner, "existing.text");
            if (field.Text != Original) throw new InvalidOperationException("Fixture text did not reach the live editor.");
            field.Focus();
            field.SelectAll();
            field.SelectedText = "";
            Pump();
            if (field.Text.Length != 0) throw new InvalidOperationException("Live editor did not clear.");
            Save(owner, destination, saveAs);
            Verify(destination, "", folder, mode);
            if (saveAs && !File.ReadAllBytes(source).AsSpan().SequenceEqual(Fixture(Original)))
                throw new InvalidOperationException("Save As changed the source fixture.");
            Reopen(owner, destination);
            if (Field(owner, "existing.text").Text != "" || Field(owner, "untouched.sibling").Text != Sibling)
                throw new InvalidOperationException("Reopened live editor retained old text or changed the sibling.");
            Capture(owner, Path.Combine(folder, mode + "-reopened.png"));
            Console.WriteLine("PASS live field clear, " + mode + ", public values, rendered appearance and reopened editors.");
        }
        foreach (bool unlisted in new[] { false, true })
        {
            string deleteMode = unlisted ? "unlisted-delete" : "delete";
            string deleteSource = Path.Combine(folder, deleteMode + "-source.pdf");
            File.WriteAllBytes(deleteSource, Fixture(Original, unlisted));
            Open(owner, deleteSource);
            TextBox selected = Field(owner, "existing.text");
            selected.Focus(); Pump();
            if (!selected.IsFocused) throw new InvalidOperationException("Deletion fixture did not focus the real form editor.");
            owner.Focus(); Pump();
            var delete = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(owner), Environment.TickCount, Key.Delete)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            string? applicationError = null;
            var errorTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(25) };
            errorTimer.Tick += (_, _) =>
            {
                foreach (Window message in Application.Current.Windows.Cast<Window>().Where(window => window != owner && window.IsVisible).ToArray())
                {
                    applicationError = string.Join(" ", Children(message).OfType<TextBlock>().Select(block => block.Text));
                    Capture(message, Path.Combine(folder, deleteMode + "-error.png"));
                    Button? accept = Children(message).OfType<Button>().FirstOrDefault(button => button.IsDefault);
                    if (accept is not null) Click(accept); else message.Close();
                }
            };
            errorTimer.Start();
            try
            {
                owner.RaiseEvent(delete);
                Wait(() => applicationError is not null || !Children(owner).OfType<TextBox>().Any(box => Equals(box.ToolTip, "existing.text")), "Delete removes selected field");
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("FAIL " + deleteMode + ": " + error);
                Capture(owner, Path.Combine(folder, deleteMode + "-failure.png"));
                throw;
            }
            finally { errorTimer.Stop(); }
            if (applicationError is not null) throw new InvalidOperationException(deleteMode + ": " + applicationError);
            if (!delete.Handled) throw new InvalidOperationException("Delete shortcut was not handled.");
            if (Field(owner, "untouched.sibling").Text != Sibling) throw new InvalidOperationException("Delete changed the sibling.");
            Click((Button)owner.FindName("UndoBtn"));
            Wait(() => Children(owner).OfType<TextBox>().Any(box => Equals(box.ToolTip, "existing.text")), "Undo restores field");
            if (Field(owner, "existing.text").Text != Original) throw new InvalidOperationException("Undo lost the field value.");
            Click((Button)owner.FindName("RedoBtn"));
            Wait(() => !Children(owner).OfType<TextBox>().Any(box => Equals(box.ToolTip, "existing.text")), "Redo removes field");
            string deleted = Path.Combine(folder, deleteMode + "-saved.pdf");
            Save(owner, deleted, true);
            Verify(deleted, null, folder, deleteMode);
            Reopen(owner, deleted);
            if (Children(owner).OfType<TextBox>().Any(box => Equals(box.ToolTip, "existing.text"))
                || Field(owner, "untouched.sibling").Text != Sibling)
                throw new InvalidOperationException("Deleted field returned after reopen or sibling changed.");
            Capture(owner, Path.Combine(folder, deleteMode + "-reopened.png"));
            Console.WriteLine("PASS " + deleteMode + ": actual field focus, routed Delete shortcut, Undo/Redo, Save As, public values and close/reopen.");
        }
        string result = "PASS checkbox Space/Enter and radio arrows through focused controls; focused password D/Y input, Delete editing and unconsumed Tab; existing text field clearing through live WPF TextBox, Save and Save As, saved public widget values, saved appearance pixels, close/reopen and untouched sibling; listed and unlisted widgets: actual field focus, routed Delete shortcut, Undo/Redo, saved deletion and close/reopen. Offscreen WPF routed events; no native input or physical DPI coverage. App SHA256="
            + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(appPath)));
        File.WriteAllText(Path.Combine(folder, "result.txt"), result);
        Console.WriteLine(result);
        return 0;
    }

    private static void VerifyToggleKeyboard(Window owner, string folder)
    {
        string source = Path.Combine(folder, "toggle-keyboard-source.pdf");
        File.WriteAllBytes(source, Fixture("", toggles: true));
        Open(owner, source);
        var controls = Children(owner).OfType<FrameworkElement>()
            .Where(element => element.GetType().Name == "FormToggleSurface")
            .Select(element => (Element: element, Peer: UIElementAutomationPeer.CreatePeerForElement(element))).ToArray();
        var checkbox = controls.Single(item => item.Peer.GetAutomationControlType() == AutomationControlType.CheckBox);
        var radios = controls.Where(item => item.Peer.GetAutomationControlType() == AutomationControlType.RadioButton)
            .OrderBy(item => Canvas.GetLeft(item.Element)).ToArray();
        if (radios.Length != 2) throw new InvalidOperationException("Radio keyboard fixture did not expose both actual controls.");
        var failures = new List<string>();

        void Press(FrameworkElement control, Key key)
        {
            control.Focus(); Pump();
            if (!control.IsFocused) throw new InvalidOperationException("Toggle fixture did not focus the actual control.");
            var input = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(owner), Environment.TickCount, key)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            control.RaiseEvent(input);
            if (input.Handled) failures.Add(control.ToolTip + " " + key + " was consumed before the control.");
            else
            {
                input.RoutedEvent = Keyboard.KeyDownEvent;
                control.RaiseEvent(input);
            }
            var release = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(owner), Environment.TickCount, key)
                { RoutedEvent = Keyboard.PreviewKeyUpEvent };
            control.RaiseEvent(release);
            if (!release.Handled) { release.RoutedEvent = Keyboard.KeyUpEvent; control.RaiseEvent(release); }
            Pump();
        }

        var toggle = (IToggleProvider)checkbox.Peer.GetPattern(PatternInterface.Toggle);
        foreach (Key key in new[] { Key.Space, Key.Enter })
        {
            ToggleState before = toggle.ToggleState;
            Press(checkbox.Element, key);
            if (toggle.ToggleState == before) failures.Add("Checkbox " + key + " did not change its value.");
        }
        var first = (ISelectionItemProvider)radios[0].Peer.GetPattern(PatternInterface.SelectionItem);
        var second = (ISelectionItemProvider)radios[1].Peer.GetPattern(PatternInterface.SelectionItem);
        foreach (Key key in new[] { Key.Left, Key.Right, Key.Up, Key.Down })
        {
            first.Select();
            Press(radios[0].Element, key);
            if (first.IsSelected || !second.IsSelected || !radios[1].Element.IsFocused)
                failures.Add("Radio " + key + " did not move focus and exclusive selection.");
        }
        if (failures.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, failures));
        Console.WriteLine("PASS checkbox Space/Enter and radio arrows through actual focused controls and public automation state.");
        Reopen(owner, source);
    }

    private static void VerifyPasswordKeyboard(Window owner, string folder)
    {
        string source = Path.Combine(folder, "password-keyboard-source.pdf");
        File.WriteAllBytes(source, Fixture("", password: true));
        Open(owner, source);
        PasswordBox? found = null;
        Wait(() => (found = Children(owner).OfType<PasswordBox>().SingleOrDefault(box => Equals(box.ToolTip, "existing.text"))) is not null,
            "password field");
        PasswordBox password = found!;
        password.Focus(); Pump();
        if (!password.IsFocused) throw new InvalidOperationException("Password fixture did not focus the real form editor.");

        KeyEventArgs Preview(Key key)
        {
            var input = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(owner), Environment.TickCount, key)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            password.RaiseEvent(input); Pump();
            if (input.Handled) throw new InvalidOperationException("Focused password " + key + " was consumed by an application shortcut.");
            return input;
        }

        foreach (var (key, text) in new[] { (Key.D, "d"), (Key.Y, "y") })
        {
            KeyEventArgs input = Preview(key);
            input.RoutedEvent = Keyboard.KeyDownEvent;
            password.RaiseEvent(input);
            TextCompositionManager.StartComposition(new TextComposition(InputManager.Current, password, text));
            Pump();
        }
        if (password.Password != "dy") throw new InvalidOperationException("Password typing did not reach the focused editor.");

        password.SelectAll();
        KeyEventArgs delete = Preview(Key.Delete);
        delete.RoutedEvent = Keyboard.KeyDownEvent;
        password.RaiseEvent(delete); Pump();
        if (password.Password.Length != 0 || !Children(owner).Contains(password))
            throw new InvalidOperationException("Password Delete must edit the value and preserve the field.");
        Preview(Key.Tab);
        if (Field(owner, "untouched.sibling").Text != Sibling)
            throw new InvalidOperationException("Password keyboard input changed the sibling field.");
        Console.WriteLine("PASS focused password routed D/Y input, Delete editing and unconsumed Tab.");
        Reopen(owner, source);
    }

    private static void Verify(string path, string? expected, string folder, string name)
    {
        Assembly engine = Assembly.Load("KillerPdf.Engine");
        Type documentType = engine.GetType("KillerPdf.Engine.Documents.PdfDocument", true)!;
        object OpenBytes(byte[] bytes) => documentType.GetMethod("Open", [typeof(ReadOnlyMemory<byte>)])!
            .Invoke(null, [new ReadOnlyMemory<byte>(bytes)])!;
        object document = OpenBytes(File.ReadAllBytes(path));
        object reference = OpenBytes(Fixture(expected ?? ""));
        try
        {
            object[] widgets = ((IEnumerable)engine.GetType("KillerPdf.Engine.Documents.PdfFormWidgetReader", true)!
                .GetMethod("ReadPage")!.Invoke(null, [document, 0])!).Cast<object>().ToArray();
            var values = widgets.ToDictionary(widget => (string)widget.GetType().GetProperty("FieldName")!.GetValue(widget)!,
                widget => (string)widget.GetType().GetProperty("Value")!.GetValue(widget)!);
            if (widgets.Length != (expected is null ? 1 : 2) || values["untouched.sibling"] != Sibling
                || (expected is null ? values.ContainsKey("existing.text") : values["existing.text"] != expected))
                throw new InvalidOperationException("Saved public widget values are incorrect.");
            byte[] Pixels(object doc)
            {
                Type rendererType = engine.GetType("KillerPdf.Engine.Rendering.PdfPageRenderer", true)!;
                object renderer = rendererType.GetConstructors().Single(ctor => ctor.GetParameters().Length == 2).Invoke([doc, null]);
                object options = Activator.CreateInstance(engine.GetType("KillerPdf.Engine.Rendering.PdfRenderOptions", true)!,
                    [400, 400, false, true, true, 512L * 1024 * 1024])!;
                object rendered = rendererType.GetMethod("Render")!.Invoke(renderer, [0, options, CancellationToken.None])!;
                return ((ReadOnlyMemory<byte>)rendered.GetType().GetProperty("Pixels")!.GetValue(rendered)!).ToArray();
            }
            byte[] actual = Pixels(document), empty = Pixels(reference);
            SavePng(actual, 400, 400, Path.Combine(folder, name + "-saved-appearance.png"));
            // Compare the field interior against an independently authored blank appearance.
            for (int y = 104; y < 144; y++)
            for (int x = 44; x < 356; x++)
            {
                int offset = (y * 400 + x) * 4;
                if (!actual.AsSpan(offset, 4).SequenceEqual(empty.AsSpan(offset, 4)))
                    throw new InvalidOperationException($"Saved cleared field differs from the blank reference at ({x}, {y}).");
            }
        }
        finally { (document as IDisposable)?.Dispose(); (reference as IDisposable)?.Dispose(); }
    }

    private static void Save(Window owner, string destination, bool saveAs)
    {
        Exception? failure = null;
        var handled = new HashSet<Window>();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(25) };
        timer.Tick += (_, _) =>
        {
            try
            {
                foreach (Window window in Application.Current.Windows.Cast<Window>().Where(window => window != owner && window.IsVisible).ToArray())
                {
                    if (!handled.Add(window)) continue;
                    if (window.GetType().FullName == "KillerPDF.Controls.FileDialog")
                    {
                        Children(window).OfType<TextBox>().Single(box => box.Name == "FileNameBox").Text = destination;
                        Click(Children(window).OfType<Button>().Single(button => button.Name == "AcceptButton"));
                    }
                    else
                    {
                        string yes = (string)Application.Current.FindResource("Str_Btn_Yes");
                        Button? confirm = Children(window).OfType<Button>().FirstOrDefault(button => Equals(button.Content, yes));
                        if (confirm is null)
                            throw new InvalidOperationException("Unexpected application message: " + string.Join(" ", Children(window).OfType<TextBlock>().Select(block => block.Text)));
                        Click(confirm);
                    }
                }
            }
            catch (Exception error)
            {
                failure = error; timer.Stop();
                foreach (Window window in Application.Current.Windows.Cast<Window>().Where(window => window != owner).ToArray()) window.Close();
            }
        };
        byte[] before = File.Exists(destination) ? File.ReadAllBytes(destination) : [];
        timer.Start();
        try
        {
            if (saveAs)
            {
                Click((Button)owner.FindName("SaveMenuBtn")); Pump();
                string label = (string)Application.Current.FindResource("Str_Menu_SaveAs");
                MenuItem item = PresentationSource.CurrentSources.Cast<PresentationSource>().Select(source => source.RootVisual)
                    .OfType<DependencyObject>().SelectMany(Children).OfType<MenuItem>().Single(item => Equals(item.Header, label));
                if (ItemsControl.ItemsControlFromItemContainer(item) is ContextMenu menu) menu.IsOpen = false;
                item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            }
            else Click((Button)owner.FindName("SaveAsBtn"));
            Wait(() => failure is not null || File.Exists(destination) && !File.ReadAllBytes(destination).AsSpan().SequenceEqual(before), "save output");
            if (failure is not null) throw failure;
        }
        finally { timer.Stop(); }
    }

    // The document already open has a sibling field too, and a new tab keeps the old overlays until its
    // own page renders, so the wait is for a sibling that was not on screen before the open (S2).
    private static void Open(Window owner, string path)
    {
        var before = Children(owner).OfType<TextBox>().Where(box => Equals(box.ToolTip, "untouched.sibling")).ToHashSet();
        owner.GetType().GetMethod("OpenFromExternal", BindingFlags.Public | BindingFlags.Instance)!.Invoke(owner, [path]);
        Wait(() => Children(owner).OfType<TextBox>().Any(box => Equals(box.ToolTip, "untouched.sibling")
            && box.IsVisible && !before.Contains(box)), "form overlays of the opened file");
    }
    private static void Reopen(Window owner, string path)
    {
        TextBox old = Field(owner, "untouched.sibling");
        Exception? failure = null;
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(25) };
        timer.Tick += (_, _) =>
        {
            foreach (Window message in Application.Current.Windows.Cast<Window>().Where(window => window != owner && window.IsVisible).ToArray())
            {
                string discard = (string)Application.Current.FindResource("Str_Btn_DontSave");
                Button? button = Children(message).OfType<Button>().FirstOrDefault(button => Equals(button.Content, discard));
                if (button is not null) Click(button);
                else
                {
                    failure = new InvalidOperationException("Unexpected close message: " + string.Join(" ", Children(message).OfType<TextBlock>().Select(block => block.Text)));
                    message.Close();
                }
            }
        };
        timer.Start();
        try { Click((Button)owner.FindName("CloseFileBtn")); Pump(); }
        finally { timer.Stop(); }
        if (failure is not null) throw failure;
        Open(owner, path);
        if (ReferenceEquals(old, Field(owner, "untouched.sibling")))
            throw new InvalidOperationException("Reopen reused the existing live form controls.");
    }
    private static TextBox Field(Window owner, string name) => Children(owner).OfType<TextBox>().Single(box => Equals(box.ToolTip, name));
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static IEnumerable<DependencyObject> Children(DependencyObject root)
    {
        yield return root;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (DependencyObject child in Children(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
    private static void Pump() => Application.Current.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static void Wait(Func<bool> condition, string stage)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            Pump(); Thread.Sleep(10);
            if (watch.Elapsed.TotalSeconds > 40) throw new TimeoutException("Form editing timed out: " + stage);
        }
        Pump();
    }
    private static void Capture(Window window, string path)
    {
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using Stream file = File.Create(path); encoder.Save(file);
    }
    private static void SavePng(byte[] pixels, int width, int height, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4)));
        using Stream file = File.Create(path); encoder.Save(file);
    }
    internal static byte[] Fixture(string value, bool unlisted = false, bool password = false, bool toggles = false,
        string? formatScript = null, string? validationScript = null, string? calculationScript = null, string? siblingValue = null,
        bool malformedDate = false)
    {
        static string Appearance(string text) => $"q 1 1 1 rg 0 0 160 24 re f 0 g BT /Helv 12 Tf 4 8 Td ({text}) Tj ET Q\n";
        siblingValue ??= Sibling;
        string first = Appearance(value), second = Appearance(siblingValue);
        string Action(string trigger, string? script) => script is null ? "" : "/" + trigger + " << /S /JavaScript /JS ("
            + script.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)") + ") >> ";
        string actions = Action("F", formatScript) + Action("V", validationScript) + Action("C", calculationScript);
        string format = actions.Length == 0 ? "" : "/AA << " + actions + ">>";
        string extraFields = toggles ? " 9 0 R 10 0 R" : "", extraWidgets = toggles ? " 9 0 R 11 0 R 12 0 R" : "";
        List<string> objects = [
            unlisted ? "<< /Type /Catalog /Pages 2 0 R >>" : $"<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R 6 0 R{extraFields}] /DA (/Helv 12 Tf 0 g) /DR << /Font << /Helv 4 0 R >> >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << >> /Annots [5 0 R 6 0 R{extraWidgets}] >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            $"<< /Type /Annot /Subtype /Widget /FT /Tx /T (existing.text) /Ff {(password ? 8192 : 0)} /V ({value}) /DV ({Original}) /Rect [20 126 180 150] /P 3 0 R /F 4 /DA (/Helv 12 Tf 0 g) /AP << /N 7 0 R >> {format} >>",
            $"<< /Type /Annot /Subtype /Widget /FT /Tx /T (untouched.sibling) /V ({siblingValue}) /Rect [20 70 180 94] /P 3 0 R /F 4 /DA (/Helv 12 Tf 0 g) /AP << /N 8 0 R >> >>",
            $"<< /Type /XObject /Subtype /Form /BBox [0 0 160 24] /Resources << /Font << /Helv 4 0 R >> >> /Length {first.Length} >>\nstream\n{first}endstream",
            $"<< /Type /XObject /Subtype /Form /BBox [0 0 160 24] /Resources << /Font << /Helv 4 0 R >> >> /Length {second.Length} >>\nstream\n{second}endstream"];
        if (toggles)
        {
            string off = "q 1 1 1 rg 0 0 14 14 re f Q\n", on = "q 0 g 2 2 10 10 re f Q\n";
            objects.AddRange([
                "<< /Type /Annot /Subtype /Widget /FT /Btn /T (keyboard.check) /V /Off /AS /Off /Rect [20 38 34 52] /P 3 0 R /F 4 /AP << /N << /Off 13 0 R /Yes 14 0 R >> >> >>",
                "<< /FT /Btn /Ff 32768 /T (keyboard.radio) /V /A /Kids [11 0 R 12 0 R] >>",
                "<< /Type /Annot /Subtype /Widget /Parent 10 0 R /AS /A /Rect [70 38 84 52] /P 3 0 R /F 4 /AP << /N << /Off 13 0 R /A 14 0 R >> >> >>",
                "<< /Type /Annot /Subtype /Widget /Parent 10 0 R /AS /Off /Rect [110 38 124 52] /P 3 0 R /F 4 /AP << /N << /Off 13 0 R /B 14 0 R >> >> >>",
                $"<< /Type /XObject /Subtype /Form /BBox [0 0 14 14] /Length {off.Length} >>\nstream\n{off}endstream",
                $"<< /Type /XObject /Subtype /Form /BBox [0 0 14 14] /Length {on.Length} >>\nstream\n{on}endstream"]);
        }
        string info = "";
        if (malformedDate)
        {
            objects.Add("<< /CreationDate (NOT_A_DATE) >>");
            info = " /Info " + objects.Count + " 0 R";
        }
        var pdf = new StringBuilder("%PDF-1.7\n"); var offsets = new List<int> { 0 };
        for (int index = 0; index < objects.Count; index++) { offsets.Add(pdf.Length); pdf.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n"); }
        int xref = pdf.Length; pdf.Append($"xref\n0 {offsets.Count}\n0000000000 65535 f \n");
        foreach (int offset in offsets.Skip(1)) pdf.Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        pdf.Append($"trailer\n<< /Size {offsets.Count} /Root 1 0 R{info} >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
