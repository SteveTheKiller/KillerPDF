using KillerPdf.Engine.Rendering;
using System.Runtime.InteropServices;
using System.Windows;

namespace KillerPDF.Presentation;

internal partial class WindowsPdfViewportHost
{
    nint detailWindow;
    IPdfFramePresenter? detailPresenter;
    Rect detailBounds;

    internal IPdfFramePresenter PrepareDetail(Rect normalizedBounds)
    {
        VerifyAccess();
        ClearDetail();
        if (Viewport == 0 || normalizedBounds.IsEmpty || normalizedBounds.Width <= 0 || normalizedBounds.Height <= 0)
            throw new InvalidOperationException("The detail viewport is unavailable.");
        detailBounds = normalizedBounds;
        // A disabled child leaves page input with its parent while retaining its own frame surface.
        detailWindow = CreateWindowEx(0, className, "PDF detail", 0x48000000, 0, 0, 1, 1, Viewport, 0, 0, 0);
        if (detailWindow == 0) throw new System.ComponentModel.Win32Exception();
        try
        {
            PositionDetail();
            return detailPresenter = presenterFactory(detailWindow);
        }
        catch { ClearDetail(); throw; }
    }

    internal void ShowDetail()
    {
        VerifyAccess();
        if (detailWindow != 0) ShowWindow(detailWindow, 4);
    }

    void PositionDetail()
    {
        if (detailWindow == 0) return;
        var size = PixelSize;
        int left = (int)Math.Round(detailBounds.Left * size.Width);
        int top = (int)Math.Round(detailBounds.Top * size.Height);
        int right = (int)Math.Round(detailBounds.Right * size.Width);
        int bottom = (int)Math.Round(detailBounds.Bottom * size.Height);
        if (!SetWindowPos(detailWindow, 0, left, top, Math.Max(1, right - left), Math.Max(1, bottom - top), 0x14))
            throw new System.ComponentModel.Win32Exception();
        if (detailPresenter is WindowsPdfFramePresenter native) native.RefreshViewport();
    }

    internal void ClearDetail()
    {
        VerifyAccess();
        try { detailPresenter?.Dispose(); }
        finally
        {
            detailPresenter = null;
            if (detailWindow != 0) DestroyWindow(detailWindow);
            detailWindow = 0;
        }
    }

    [DllImport("user32.dll")] static extern bool ShowWindow(nint window, int command);
}
