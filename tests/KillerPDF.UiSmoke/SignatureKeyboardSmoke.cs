using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

internal static class SignatureKeyboardSmoke
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    private static Window? _keyboardOwner;

    internal static int Run(Window owner, string appPath, string folder)
    {
        if (!new[] { "SN01", "SN02" }.Contains(Environment.MachineName))
            throw new InvalidOperationException("Signature keyboard acceptance requires an owned lab VM.");
        Directory.CreateDirectory(folder);
        string source = Path.Combine(folder, "signature-keyboard.pdf");
        File.WriteAllBytes(source, Fixture());
        owner.Left = 30; owner.Top = 30; owner.Width = 1100; owner.Height = 800;
        owner.ShowActivated = true;
        owner.ShowInTaskbar = true;
        owner.BeginAnimation(UIElement.OpacityProperty, null);
        owner.Opacity = 1;
        owner.Show();
        Call(owner, "OpenFromExternal", source);
        Pump();
        ShortcutHintsSmoke.Focus(owner);
        _keyboardOwner = owner;
        Type signatureType = owner.GetType().Assembly.GetType("KillerPDF.SavedSignature", true)!;
        object signature = Activator.CreateInstance(signatureType)!;
        signatureType.GetProperty("Name")!.SetValue(signature, "KEYBOARD SIGNATURE");
        object initials = Activator.CreateInstance(signatureType)!;
        signatureType.GetProperty("Name")!.SetValue(initials, "KEYBOARD INITIALS");
        signatureType.GetProperty("Kind")!.SetValue(initials, Enum.ToObject(signatureType.GetProperty("Kind")!.PropertyType, 1));
        foreach (object saved in new[] { signature, initials })
        {
            var strokes = (IList)signatureType.GetProperty("Strokes")!.GetValue(saved)!;
            var stroke = (IList)Activator.CreateInstance(strokes.GetType().GetGenericArguments()[0])!;
            Type pointType = owner.GetType().Assembly.GetType("KillerPDF.SerializablePoint", true)!;
            foreach (double x in new[] { 20d, 120d, 200d })
            {
                object point = Activator.CreateInstance(pointType)!;
                pointType.GetProperty("X")!.SetValue(point, x);
                pointType.GetProperty("Y")!.SetValue(point, 30d);
                stroke.Add(point);
            }
            strokes.Add(stroke);
            Call(Get(owner, "_signatureStore")!, "Add", saved);
        }

        var trigger = (Button)owner.FindName("ToolSignatureBtn");
        if (trigger is null) throw new InvalidOperationException("The signature tool was not found.");
        Console.WriteLine("READINESS signature toolbar: focusable=" + trigger.Focusable + ", visible=" + trigger.IsVisible);
        owner.Focus();
        Require(Get(owner, "_doc") is not null, "The signature shortcut needs an open document.");
        Press(0x47);
        Pump();
        Console.WriteLine("READINESS signature picker: active=" + owner.IsActive + ", opacity=" + owner.Opacity
            + ", popupVisible=" + Popup(owner)?.IsVisible + ", popupEnabled=" + Popup(owner)?.IsEnabled
            + ", focused=" + Keyboard.FocusedElement?.GetType().Name);
        Wait(() => Popup(owner)?.IsKeyboardFocusWithin == true, "picker keyboard focus");
        Button choice = Choice(owner, signature);
        var peer = UIElementAutomationPeer.CreatePeerForElement(choice);
        Require(peer.GetAutomationControlType() == AutomationControlType.Button
            && peer.GetName().Contains("KEYBOARD SIGNATURE", StringComparison.Ordinal)
            && peer.GetPattern(PatternInterface.Invoke) is IInvokeProvider, "saved signature semantics");
        TabTo(choice);
        Press(0x0D);
        Wait(() => Popup(owner) is null, "Enter activation");
        Require(ReferenceEquals(signature, Get(owner, "_pendingSignature")), "Enter arms the selected signature");
        Require(Get(owner, "_signatureDragOrigin") is null, "keyboard selection does not start a mouse drag");
        Require(!choice.IsKeyboardFocusWithin, "activation restores focus outside the removed picker");

        Call(owner, "ShowSignaturePopup");
        Wait(() => Popup(owner)?.IsKeyboardFocusWithin == true, "reopened picker focus");
        TabTo(Choice(owner, initials));
        Press(0x20);
        Wait(() => Popup(owner) is null, "Space activation");
        Require(ReferenceEquals(initials, Get(owner, "_pendingSignature")), "Space arms initials");
        Require(!(bool)Get(owner, "_spaceHeld")!, "Space is not consumed by page panning");

        Call(owner, "ShowSignaturePopup");
        Wait(() => Popup(owner)?.IsKeyboardFocusWithin == true, "Escape picker focus");
        Press(0x1B);
        Wait(() => Popup(owner) is null, "Escape closes the picker");

        // Enter the same callback used by a document signature-field overlay, then choose with native keys.
        Call(owner, "FillSignField", false, 5, 0, 40d, 40d, 200d, 70d);
        Wait(() => Popup(owner)?.IsKeyboardFocusWithin == true, "waiting signature field picker");
        TabTo(Choice(owner, signature));
        Press(0x0D);
        Wait(() => Popup(owner) is null, "guided field Enter activation");
        var signedFields = (IDictionary)Get(owner, "_signedFields")!;
        Require(signedFields.Contains(5) && Get(owner, "_pendingSignature") is null, "guided selection fills once and clears placement");

        Call(owner, "ShowSignaturePopup");
        Wait(() => Popup(owner)?.IsKeyboardFocusWithin == true, "delete picker focus");
        string id = (string)signatureType.GetProperty("Id")!.GetValue(initials)!;
        Button delete = Tree(Popup(owner)!).OfType<Button>().Single(button => AutomationProperties.GetAutomationId(button) == "DeleteSignature_" + id);
        Require(AutomationProperties.GetName(delete).Contains("KEYBOARD INITIALS", StringComparison.Ordinal), "delete control name");
        TabTo(delete);
        Press(0x20);
        Wait(() => Popup(owner) is { IsKeyboardFocusWithin: true }
            && !Tree(Popup(owner)!).OfType<Button>().Any(button => AutomationProperties.GetAutomationId(button) == "SavedSignature_" + id), "delete refresh and focus");
        Press(0x1B);

        VerifyOrdinaryExternalOpenAndInactiveOwner(owner, folder);

        File.WriteAllText(Path.Combine(folder, "signature-keyboard-result.json"), JsonSerializer.Serialize(new
        {
            appSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(appPath))),
            passed = new[] { "named button peer", "Tab", "Enter", "Space", "Escape", "guided callback", "delete", "focus restoration",
                "ordinary public external-open document focus", "native PageDown/PageUp", "inactive owner preserves second-window editor focus" },
            remaining = new[] { "physical drag and select-then-click in SignaturePlacementSmoke", "screen-reader spoken output", "rendered focus across themes" }
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("PASS signature native keyboard, semantics, guided callback, delete and focus checks.");
        return 0;
    }

    private static void VerifyOrdinaryExternalOpenAndInactiveOwner(Window owner, string folder)
    {
        string ordinary = Path.Combine(folder, "ordinary-focus-control.pdf");
        string inactive = Path.Combine(folder, "inactive-owner-focus-control.pdf");
        byte[] fivePages = PageDeletionSmoke.Fixture();
        File.WriteAllBytes(ordinary, fivePages);
        File.WriteAllBytes(inactive, fivePages);
        ShortcutHintsSmoke.Focus(owner);
        Press(0x75);
        owner.Focus();
        Call(owner, "OpenFromExternal", ordinary);
        var viewer = (FrameworkElement)owner.FindName("Viewer");
        var ordinaryRender = (Task)Get(viewer, "PrimaryRenderTask")!;
        WaitForRenderedDocument(owner, ordinary, ordinaryRender);
        var pageList = (ListBox)owner.FindName("PageList");
        var pagePreview = Tree(viewer).OfType<ScrollViewer>().Single(scroll => scroll.Name == "PagePreviewPanel");
        Wait(() => pagePreview.IsKeyboardFocused, "ordinary external-open initial document focus");
        Require(pageList.SelectedIndex == 0, "The ordinary external-open control did not start on page one.");
        Press(0x22);
        Wait(() => pageList.SelectedIndex == 1, "native PageDown after ordinary external open");
        Press(0x21);
        Wait(() => pageList.SelectedIndex == 0, "native PageUp after ordinary external open");

        var editor = new TextBox { Text = "OWNED SECOND WINDOW", Margin = new Thickness(12) };
        var other = new Window { Owner = owner, Title = "Owned focus control", Content = editor,
            Width = 340, Height = 140, Left = 130, Top = 130, WindowStartupLocation = WindowStartupLocation.Manual,
            ShowActivated = true, ShowInTaskbar = true, Opacity = 1 };
        try
        {
            ShortcutHintsSmoke.Focus(owner);
            owner.Focus();
            Call(owner, "OpenFromExternal", inactive);
            var inactiveRender = (Task)Get(viewer, "PrimaryRenderTask")!;
            other.Show();
            ShortcutHintsSmoke.Focus(other);
            Require(editor.Focus() && editor.IsKeyboardFocused, "The owned second-window editor did not receive focus.");
            WaitForRenderedDocument(owner, inactive, inactiveRender);
            Require(other.IsActive && editor.IsKeyboardFocused
                && GetForegroundWindow() == new System.Windows.Interop.WindowInteropHelper(other).Handle,
                "Initial document rendering took focus from the owned second-window editor.");
        }
        finally
        {
            other.Close();
            ShortcutHintsSmoke.Focus(owner);
        }
        Console.WriteLine("PASS public external-open focus, native page navigation and inactive-owner focus controls.");
    }

    private static void WaitForRenderedDocument(Window owner, string path, Task initialRaster)
    {
        Wait(() => initialRaster.IsCompleted, "actual initial page raster task");
        initialRaster.GetAwaiter().GetResult();
        Wait(() => ((TextBlock)owner.FindName("FileNameLabel")).Text == Path.GetFileName(path)
            && ((ListBox)owner.FindName("PageList")).Items.Count == 5
            && Tree((DependencyObject)owner.FindName("Viewer")).OfType<Image>().Any(image => image.IsVisible
                && image.Source is System.Windows.Media.Imaging.BitmapSource { PixelWidth: > 20, PixelHeight: > 20 }),
            "public document title, five pages and rendered page image");
        // Let the initial-render continuation and its layout work complete after raster publication.
        var frame = new DispatcherFrame();
        owner.Dispatcher.BeginInvoke(DispatcherPriority.SystemIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        Pump();
    }

    private static void TabTo(Button target)
    {
        for (int i = 0; i < 30 && !target.IsKeyboardFocused; i++) Press(0x09);
        Require(target.IsKeyboardFocused, "Tab reaches " + AutomationProperties.GetAutomationId(target));
    }
    private static Button Choice(Window owner, object signature) => Tree(Popup(owner)!).OfType<Button>()
        .Single(button => AutomationProperties.GetAutomationId(button) == "SavedSignature_" + signature.GetType().GetProperty("Id")!.GetValue(signature));
    private static Border? Popup(Window owner) => Get(owner, "_signaturePopup") as Border;
    private static object? Get(object target, string name)
    {
        if (target.GetType().GetField(name, Members) is { } field) return field.GetValue(target);
        var property = target.GetType().GetProperty(name, Members)
            ?? throw new MissingMemberException(target.GetType().FullName, name);
        return property.GetValue(target);
    }
    private static object? Call(object target, string method, params object?[] values) => target.GetType().GetMethod(method, Members)!.Invoke(target, values);
    private static IEnumerable<DependencyObject> Tree(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Tree(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static void Press(byte key)
    {
        Require(_keyboardOwner is { IsActive: true, IsVisible: true, Opacity: >= 0.99 }
            && GetForegroundWindow() == new System.Windows.Interop.WindowInteropHelper(_keyboardOwner).Handle,
            "The owned visible signature window lost native foreground readiness; no key was sent.");
        keybd_event(key, 0, 0, UIntPtr.Zero);
        try { Pump(); }
        finally { keybd_event(key, 0, 2, UIntPtr.Zero); }
        Pump();
    }
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }
    private static void Wait(Func<bool> ready, string stage)
    {
        var watch = Stopwatch.StartNew();
        while (!ready()) { if (watch.Elapsed.TotalSeconds > 10) throw new TimeoutException(stage); Pump(); }
    }
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static byte[] Fixture()
    {
        string[] bodies = ["<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R] >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << >> /Contents 4 0 R /Annots [5 0 R] >>",
            "<< /Length 0 >>\nstream\n\nendstream", "<< /Type /Annot /Subtype /Widget /FT /Sig /T (KeyboardSignature) /Rect [40 682 240 752] /P 3 0 R >>"];
        var text = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int> { 0 };
        for (int i = 0; i < bodies.Length; i++) { offsets.Add(Encoding.ASCII.GetByteCount(text.ToString())); text.Append($"{i + 1} 0 obj\n{bodies[i]}\nendobj\n"); }
        int xref = Encoding.ASCII.GetByteCount(text.ToString());
        text.Append($"xref\n0 {bodies.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets.Skip(1)) text.Append(offset.ToString("D10") + " 00000 n \n");
        text.Append($"trailer\n<< /Size {bodies.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(text.ToString());
    }
}
