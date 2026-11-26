using System.Runtime.InteropServices;
using KillerPDF.PreviewHandler;
using Xunit;

namespace KillerPDF.Tests;

// #410: prevhost.exe is only system DPI aware. Outlook hands the preview its reading pane bounds in
// physical pixels, so on a monitor whose scaling differs from the primary one a system-aware preview
// window lands in the wrong place. The preview's own UI thread has to run per-monitor aware.
public sealed partial class PreviewUiThreadDpiTests
{
    private static readonly IntPtr PerMonitorAwareV2 = new(-4);

    [Fact]
    public void ThePreviewThreadRunsPerMonitorDpiAware()
    {
        IntPtr context = PreviewUiThread.Invoke(GetThreadDpiAwarenessContext);
        Assert.True(AreDpiAwarenessContextsEqual(context, PerMonitorAwareV2),
            "The preview UI thread must be per-monitor DPI aware (v2).");
    }

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetThreadDpiAwarenessContext();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AreDpiAwarenessContextsEqual(IntPtr first, IntPtr second);
}
