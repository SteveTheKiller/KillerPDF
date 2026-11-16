using System.Windows.Input;
using KillerPDF.Services;
using Xunit;
using static KillerPDF.Tests.ViewerSourcePin;

namespace KillerPDF.Tests;

// #494: a precision touchpad pinch reaches the app as WM_MOUSEWHEEL with MK_CONTROL set in wParam while Ctrl is not
// held. The native page host replays the wheel into WPF after the message has been handled, so the keyboard state is
// the wrong place to look: the message's own key flags decide whether a wheel zooms or scrolls sideways.
public sealed class WheelKeysTests
{
    private const int MkShift = 0x0004, MkControl = 0x0008;

    [Theory]
    [InlineData(MkControl, ModifierKeys.None, ModifierKeys.Control)]                       // touchpad pinch, Ctrl not held
    [InlineData(MkShift, ModifierKeys.None, ModifierKeys.Shift)]
    [InlineData(MkControl | MkShift, ModifierKeys.None, ModifierKeys.Control | ModifierKeys.Shift)]
    [InlineData(0, ModifierKeys.Control, ModifierKeys.None)]                               // Ctrl let go before the replay
    [InlineData(0, ModifierKeys.Alt, ModifierKeys.Alt)]                                    // Alt is not in the message
    [InlineData(MkControl, ModifierKeys.Alt, ModifierKeys.Control | ModifierKeys.Alt)]
    public void TheWheelMessageKeysDecideCtrlAndShift(int keyFlags, ModifierKeys keyboard, ModifierKeys expected)
    {
        // The delta lives in the high word of wParam; it must not leak into the key flags.
        nint wParam = (nint)((unchecked((short)-120) << 16) | keyFlags);
        Assert.Equal(expected, WheelKeys.FromMessage(wParam, keyboard));
    }

    // SOURCE PIN (the viewer is not compiled into this project). The bridge keeps the message's keys on the replayed
    // wheel, and the viewer's wheel handler reads them instead of the live keyboard.
    [Fact]
    public void TheNativeWheelBridgeKeepsTheMessageKeysAndTheViewerReadsThem()
    {
        string bridge = Source("Services", "Presentation", "WindowsPdfViewportHost.Input.cs");
        Assert.Contains("WheelKeys.FromMessage(wParam, Keyboard.Modifiers)", bridge);

        string zoom = Viewer("PdfViewer.Zoom.cs");
        string handler = Between(zoom, "internal void PagePreview_PreviewMouseWheel(", "Point cursorInViewport");
        Assert.Contains("ModifierKeys keys = KillerPDF.Presentation.WindowsPdfViewportHost.GetWheelModifiers(e);", handler);
        Assert.DoesNotContain("Keyboard.Modifiers", handler);
    }
}
