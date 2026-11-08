using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace KillerPDF.Presentation;

internal partial class WindowsPdfViewportHost
{
    bool editorMouseBridge;
    List<(uint Message, nint Keys, nint Position)>? pendingEditorMouse;
    bool translatingAccelerator;

    sealed class NativeWheelEventArgs(int timestamp, int delta, Point screenPosition, FrameworkElement pageRoot)
        : MouseWheelEventArgs(Mouse.PrimaryDevice, timestamp, delta)
    {
        internal Point ScreenPosition { get; } = screenPosition;
        internal FrameworkElement PageRoot { get; } = pageRoot;
    }

    internal static Point GetWheelPosition(MouseWheelEventArgs args, FrameworkElement relativeTo)
        => args is NativeWheelEventArgs native
            ? relativeTo.PointFromScreen(native.ScreenPosition)
            : args.GetPosition(relativeTo);

    internal static Action? CaptureWheelAnchor(MouseWheelEventArgs args, ScrollViewer viewport)
    {
        if (args is not NativeWheelEventArgs native) return null;
        FrameworkElement page = native.PageRoot;
        var source = PresentationSource.FromVisual(page);
        Point local = page.PointFromScreen(native.ScreenPosition);
        return () =>
        {
            if (!page.IsVisible || PresentationSource.FromVisual(page) != source) return;
            Point actual = viewport.PointFromScreen(page.PointToScreen(local));
            Point intended = viewport.PointFromScreen(native.ScreenPosition);
            viewport.ScrollToHorizontalOffset(viewport.HorizontalOffset + actual.X - intended.X);
            viewport.ScrollToVerticalOffset(viewport.VerticalOffset + actual.Y - intended.Y);
        };
    }

    protected override bool TranslateAcceleratorCore(ref MSG message, ModifierKeys modifiers)
    {
        if (translatingAccelerator || Overlay is not IKeyboardInputSink sink || !sink.HasFocusWithin()) return false;
        translatingAccelerator = true;
        try { return sink.TranslateAccelerator(ref message, modifiers); }
        finally { translatingAccelerator = false; }
    }

    // Tab moves into the overlay's first or last focusable editor, and mnemonics reach it.
    protected override bool TabIntoCore(TraversalRequest request) =>
        Overlay is IKeyboardInputSink sink && sink.TabInto(request);

    protected override bool OnMnemonicCore(ref MSG message, ModifierKeys modifiers) =>
        Overlay is IKeyboardInputSink sink && sink.OnMnemonic(ref message, modifiers);

    protected override IKeyboardInputSite RegisterKeyboardInputSinkCore(IKeyboardInputSink sink) =>
        new OverlayKeyboardSite(this, sink);

    /// <summary>Returns Tab out of the overlay to the window that owns this host.</summary>
    internal sealed class OverlayKeyboardSite(HwndHost host, IKeyboardInputSink sink) : IKeyboardInputSite
    {
        public IKeyboardInputSink Sink => sink;

        public void Unregister() { }

        public bool OnNoMoreTabStops(TraversalRequest request)
        {
            if (((IKeyboardInputSink)host).KeyboardInputSite is { } site && site.OnNoMoreTabStops(request)) return true;
            // Without a hosting site, continue WPF traversal from the host element itself.
            return host.MoveFocus(request);
        }
    }

    bool BridgeWheel(nint wParam, nint lParam)
    {
        if (Overlay is not { } overlay || !OverlayRoot.IsVisible) return false;
        var screen = new PointI { X = unchecked((short)(lParam.ToInt64() & 0xffff)), Y = unchecked((short)((lParam.ToInt64() >> 16) & 0xffff)) };
        if (WindowFromPoint(screen) != Viewport) return false;
        Point point = OverlayRoot.PointFromScreen(new Point(screen.X, screen.Y));
        DependencyObject? hit = OverlayRoot.InputHitTest(point) as DependencyObject;
        while (hit is not null && hit is not UIElement) hit = LogicalTreeHelper.GetParent(hit);
        UIElement target = hit as UIElement ?? OverlayRoot;
        int delta = unchecked((short)((wParam.ToInt64() >> 16) & 0xffff));
        int timestamp = Environment.TickCount;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (!ReferenceEquals(Overlay, overlay) || !target.IsVisible || !target.IsEnabled
                || PresentationSource.FromVisual(target) != overlay) return;
            // The native page does not update WPF's mouse device position. Keep the message's
            // screen point so consumers can anchor the gesture across presentation sources.
            var preview = new NativeWheelEventArgs(timestamp, delta, new Point(screen.X, screen.Y), OverlayRoot)
                { RoutedEvent = Mouse.PreviewMouseWheelEvent, Source = target };
            target.RaiseEvent(preview);
            if (!preview.Handled)
                target.RaiseEvent(new NativeWheelEventArgs(timestamp, delta, new Point(screen.X, screen.Y), OverlayRoot)
                    { RoutedEvent = Mouse.MouseWheelEvent, Source = target });
        }));
        return true;
    }

    bool BridgeEditorMouse(uint message, nint wParam, nint lParam)
    {
        if (Overlay is not { } overlay) return false;
        nint viewport = Viewport, overlayHandle = overlay.Handle;
        var screen = new PointI { X = unchecked((short)(lParam.ToInt64() & 0xffff)), Y = unchecked((short)((lParam.ToInt64() >> 16) & 0xffff)) };
        if (!ClientToScreen(viewport, ref screen)) return false;
        UIElement? editor = null;
        if (!editorMouseBridge)
        {
            if (message != 0x201 || WindowFromPoint(screen) != viewport || !OverlayRoot.IsVisible) return false;
            Point point = OverlayRoot.PointFromScreen(new Point(screen.X, screen.Y));
            DependencyObject? inputTarget = OverlayRoot.InputHitTest(point) as DependencyObject;
            for (DependencyObject? hit = inputTarget;
                hit is not null; hit = VisualTreeHelper.GetParent(hit))
            {
                if (hit is TextBox textBox) { editor = textBox; break; }
                // Preserve the hit child so canvas capture does not swallow its drag-start event.
                if (hit is Canvas canvas && !ReferenceEquals(canvas, OverlayRoot))
                { editor = inputTarget as UIElement ?? canvas; break; }
            }
            if (editor is null || !editor.IsEnabled || editor is TextBox { IsReadOnly: true }) return false;
            editorMouseBridge = true;
        }
        var client = screen;
        if (!ScreenToClient(overlayHandle, ref client)) { editorMouseBridge = false; return false; }
        nint packed = (nint)((client.X & 0xffff) | ((client.Y & 0xffff) << 16));
        if (message == 0x201 && editor is not null)
        {
            UIElement target = editor;
            var messages = new List<(uint Message, nint Keys, nint Position)>
                { (0x200, wParam, packed), (message, wParam, packed) };
            pendingEditorMouse = messages;
            // Focus after returning from the native mouse input stack.
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                if (!ReferenceEquals(pendingEditorMouse, messages)) return;
                if (!ReferenceEquals(Overlay, overlay) || Viewport != viewport || !target.IsVisible
                    || !target.IsEnabled || target is TextBox { IsReadOnly: true } || PresentationSource.FromVisual(target) != overlay)
                { pendingEditorMouse = null; editorMouseBridge = false; return; }
                bool focused;
                if (target is TextBox) focused = target.Focus();
                else { SetFocus(overlayHandle); focused = GetFocus() == overlayHandle; }
                bool captured = Mouse.Capture(target, target is TextBox ? CaptureMode.Element : CaptureMode.SubTree);
                pendingEditorMouse = null;
                if (!focused || !captured || GetFocus() != overlayHandle || GetCapture() != overlayHandle)
                {
                    editorMouseBridge = false;
                    target.ReleaseMouseCapture();
                    if (GetCapture() == overlayHandle) ReleaseCapture();
                    return;
                }
                Point textPoint = default;
                if (target is TextBox textTarget)
                {
                    textPoint = textTarget.PointFromScreen(new Point(screen.X, screen.Y));
                    int character = textTarget.GetCharacterIndexFromPoint(textPoint, true);
                    if (character >= 0)
                    {
                        Rect leading = textTarget.GetRectFromCharacterIndex(character, false);
                        Rect trailing = textTarget.GetRectFromCharacterIndex(character, true);
                        if (EdgeDistance(trailing) < EdgeDistance(leading)) character++;
                        textTarget.Select(Math.Min(character, textTarget.Text.Length), 0);
                    }
                }
                foreach (var input in messages)
                    if (!PostMessage(overlayHandle, input.Message, input.Keys, input.Position))
                    {
                        editorMouseBridge = false;
                        target.ReleaseMouseCapture();
                        if (GetCapture() == overlayHandle) ReleaseCapture();
                        return;
                    }

                double EdgeDistance(Rect edge)
                {
                    if (edge.IsEmpty) return double.PositiveInfinity;
                    double dx = textPoint.X - edge.X;
                    double dy = textPoint.Y - Math.Clamp(textPoint.Y, edge.Top, edge.Bottom);
                    return dx * dx + dy * dy;
                }
            }));
        }
        else if (pendingEditorMouse is { } messages) messages.Add((message, wParam, packed));
        else if (!PostMessage(overlayHandle, message, wParam, packed)) editorMouseBridge = false;
        return true;
    }

    void ReleaseEditorMouse(nint hwnd, int message)
    {
        if (message != 0x202 || !editorMouseBridge) return;
        editorMouseBridge = false;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (Overlay?.Handle == hwnd && Mouse.Captured is null && GetCapture() == hwnd) ReleaseCapture();
        }));
    }

    [StructLayout(LayoutKind.Sequential)] struct PointI { public int X, Y; }
    [DllImport("user32.dll")] static extern bool ClientToScreen(nint window, ref PointI point);
    [DllImport("user32.dll")] static extern bool ScreenToClient(nint window, ref PointI point);
    [DllImport("user32.dll")] static extern nint WindowFromPoint(PointI point);
    [DllImport("user32.dll")] static extern nint GetFocus();
    [DllImport("user32.dll")] static extern nint SetFocus(nint window);
    [DllImport("user32.dll")] static extern nint GetCapture();
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
}
