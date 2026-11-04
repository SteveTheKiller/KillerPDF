using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace KillerPDF.Presentation;

internal partial class WindowsPdfViewportHost
{
    bool editorMouseBridge;

    bool BridgeEditorMouse(uint message, nint wParam, nint lParam)
    {
        if (Overlay is not { } overlay) return false;
        nint viewport = Viewport, overlayHandle = overlay.Handle;
        var screen = new PointI { X = unchecked((short)(lParam.ToInt64() & 0xffff)), Y = unchecked((short)((lParam.ToInt64() >> 16) & 0xffff)) };
        if (!ClientToScreen(viewport, ref screen)) return false;
        TextBox? editor = null;
        if (!editorMouseBridge)
        {
            if (message != 0x201 || WindowFromPoint(screen) != viewport || !OverlayRoot.IsVisible) return false;
            Point point = OverlayRoot.PointFromScreen(new Point(screen.X, screen.Y));
            for (DependencyObject? hit = OverlayRoot.InputHitTest(point) as DependencyObject;
                hit is not null; hit = VisualTreeHelper.GetParent(hit))
                if (hit is TextBox textBox) { editor = textBox; break; }
            if (editor is null || !editor.IsEnabled || editor.IsReadOnly) return false;
            editorMouseBridge = true;
        }
        var client = screen;
        if (!ScreenToClient(overlayHandle, ref client)) { editorMouseBridge = false; return false; }
        nint packed = (nint)((client.X & 0xffff) | ((client.Y & 0xffff) << 16));
        if (message == 0x201)
        {
            TextBox target = editor!;
            // Focus after returning from the native mouse input stack.
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                if (!ReferenceEquals(Overlay, overlay) || Viewport != viewport || !target.IsVisible
                    || !target.IsEnabled || target.IsReadOnly || PresentationSource.FromVisual(target) != overlay)
                { editorMouseBridge = false; return; }
                bool focused = target.Focus(), captured = target.CaptureMouse();
                if (!focused || !captured || GetFocus() != overlayHandle || GetCapture() != overlayHandle
                    || !PostMessage(overlayHandle, 0x200, wParam, packed)
                    || !PostMessage(overlayHandle, message, wParam, packed))
                {
                    editorMouseBridge = false;
                    target.ReleaseMouseCapture();
                    if (GetCapture() == overlayHandle) ReleaseCapture();
                }
            }));
        }
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
    [DllImport("user32.dll")] static extern nint GetCapture();
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
}
