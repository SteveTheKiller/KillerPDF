using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

internal static class JapaneseSelectionSmoke
{
    private const string Expected = "「今の時間帯は予約のみです」等で並";
    private const string SampleHash = "F8A583B46B47612417BC72C677EB6BC01961E76C69BB810D89EC598D439E7C18";
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);

    internal static int Run(Window owner, string appPath, string folder)
    {
        if (!string.Equals(Environment.MachineName, "SN01", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Japanese native clipboard acceptance requires the owned SN01 UI VM.");
        string sample = Path.Combine(Path.GetDirectoryName(folder)!, "issue465-japanese.pdf");
        byte[] bytes = File.ReadAllBytes(sample);
        if (Convert.ToHexString(SHA256.HashData(bytes)) != SampleHash)
            throw new InvalidOperationException("The Japanese sample differs from the retained reporter attachment.");
        string copy = Path.Combine(folder, "issue465-japanese.pdf");
        File.WriteAllBytes(copy, bytes);
        string stage = "visible window readiness";
        bool documentText = false, rangeText = false, clipboardText = false, clipboardRestored = false;
        int rectangleCount = 0;
        DataObject? previous = null;
        bool capturedClipboard = false;
        try
        {
            Rect work = SystemParameters.WorkArea;
            owner.WindowState = WindowState.Normal;
            owner.Left = work.Left + 24; owner.Top = work.Top + 24;
            owner.Width = Math.Min(1100, work.Width - 48); owner.Height = Math.Min(760, work.Height - 48);
            owner.Opacity = 1; owner.ShowActivated = true;
            ShortcutHintsSmoke.Focus(owner);
            stage = "public sample open";
            owner.GetType().GetMethod("OpenFromExternal", BindingFlags.Public | BindingFlags.Instance)!.Invoke(owner, [copy]);
            var viewer = (FrameworkElement)owner.FindName("Viewer");
            ITextProvider? provider = null;
            FrameworkElement? surface = null;
            stage = "public UIA document text readiness";
            Wait(() =>
            {
                surface = Children(viewer).OfType<FrameworkElement>()
                    .FirstOrDefault(element => element.GetType().Name == "DocumentSurface");
                provider = surface is null ? null : UIElementAutomationPeer.CreatePeerForElement(surface)
                    ?.GetPattern(PatternInterface.Text) as ITextProvider;
                documentText = provider?.DocumentRange.GetText(-1).Contains(Expected, StringComparison.Ordinal) == true;
                return documentText;
            }, stage);
            stage = "public UIA phrase range";
            ITextRangeProvider range = provider!.DocumentRange.FindText(Expected, backward: false, ignoreCase: false)
                ?? throw new InvalidOperationException("The public reading provider did not find the reporter's exact phrase.");
            rangeText = range.GetText(-1) == Expected;
            if (!rangeText) throw new InvalidOperationException("The public reading range does not contain the exact expected phrase.");
            range.ScrollIntoView(true);
            Pump();
            double[] rectangles = range.GetBoundingRectangles();
            if (rectangles.Length == 0 || rectangles.Length % 4 != 0
                || rectangles.Any(value => !double.IsFinite(value)))
                throw new InvalidOperationException("The Japanese reading range has no valid screen rectangles.");
            for (int index = 0; index < rectangles.Length; index += 4)
                if (rectangles[index + 2] <= 0 || rectangles[index + 3] <= 0)
                    throw new InvalidOperationException("A Japanese reading rectangle has no positive area.");
            rectangleCount = rectangles.Length / 4;
            stage = "clipboard preservation";
            previous = SnapshotClipboard();
            capturedClipboard = true;
            stage = "public UIA selection";
            surface!.Focus();
            Keyboard.Focus(surface);
            range.Select();
            Pump();
            if (!provider.GetSelection().Any(selection => selection.GetText(-1) == Expected))
                throw new InvalidOperationException("The public UIA selection does not match the expected phrase.");
            // A fresh sentinel proves the subsequent native key route actually writes the clipboard.
            Clipboard.SetText("KillerPDF Japanese native copy acceptance sentinel");
            stage = "native Ctrl+C";
            Copy(owner);
            Wait(() =>
            {
                try { return clipboardText = Clipboard.ContainsText() && Clipboard.GetText() == Expected; }
                catch (ExternalException) { return false; }
            }, "native clipboard equals the exact Japanese phrase");
            if (!File.ReadAllBytes(sample).AsSpan().SequenceEqual(bytes)
                || !File.ReadAllBytes(copy).AsSpan().SequenceEqual(bytes))
                throw new InvalidOperationException("Japanese selection modified the reporter's PDF.");
            stage = "passed";
            return 0;
        }
        finally
        {
            try
            {
                if (capturedClipboard)
                {
                    if (previous is null) Clipboard.Clear(); else Clipboard.SetDataObject(previous, copy: true);
                    clipboardRestored = true;
                }
            }
            finally
            {
                File.WriteAllText(Path.Combine(folder, "japanese-selection-result.json"), JsonSerializer.Serialize(new
                {
                    Passed = stage == "passed" && clipboardRestored,
                    Stage = stage, PublicDocumentContainsPhrase = documentText, PublicRangeEqualsPhrase = rangeText,
                    BoundingRectangleCount = rectangleCount, NativeClipboardEqualsPhrase = clipboardText,
                    PreviousClipboardRestored = clipboardRestored, SampleSha256 = SampleHash,
                    AppSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(appPath)))
                }, new JsonSerializerOptions { WriteIndented = true }));
                if (stage == "passed" && clipboardRestored)
                    Console.WriteLine("PASS GH-465 actual reporter PDF: public UIA phrase, selection and screen bounds, native Ctrl+C with exact clipboard text, and prior clipboard restoration.");
            }
        }
    }

    private static void Copy(Window owner)
    {
        ShortcutHintsSmoke.Focus(owner);
        IntPtr handle = new WindowInteropHelper(owner).Handle;
        try
        {
            Guard();
            keybd_event(0x11, 0, 0, UIntPtr.Zero);
            Guard();
            keybd_event(0x43, 0, 0, UIntPtr.Zero);
        }
        finally
        {
            keybd_event(0x43, 0, 2, UIntPtr.Zero);
            keybd_event(0x11, 0, 2, UIntPtr.Zero);
        }
        void Guard()
        {
            if (!owner.IsVisible || owner.Opacity < .99 || GetForegroundWindow() != handle)
                throw new InvalidOperationException("The exact owned Japanese document window is not the native keyboard target.");
        }
    }

    private static DataObject? SnapshotClipboard()
    {
        IDataObject? current = Clipboard.GetDataObject();
        if (current is null) return null;
        var saved = new DataObject();
        foreach (string format in current.GetFormats(autoConvert: false))
        {
            object? value = current.GetData(format, autoConvert: false);
            if (value is null) continue;
            if (value is byte[] data) value = data.ToArray();
            else if (value is MemoryStream stream) value = new MemoryStream(stream.ToArray());
            saved.SetData(format, value, autoConvert: false);
        }
        return saved;
    }

    private static IEnumerable<DependencyObject> Children(DependencyObject root)
    {
        yield return root;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (DependencyObject child in Children(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
    private static void Pump() => Application.Current.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static void Wait(Func<bool> condition, string stage)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException(stage);
            Pump(); Thread.Sleep(15);
        }
    }
}
