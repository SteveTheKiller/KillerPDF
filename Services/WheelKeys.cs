using System.Windows.Input;

namespace KillerPDF.Services;

/// <summary>
/// The modifier keys a WM_MOUSEWHEEL message carries. A precision touchpad pinch arrives as a wheel with MK_CONTROL set
/// while Ctrl is not held (#494), and the native page host replays the wheel into WPF after the message is gone, so the
/// message's own Ctrl and Shift flags decide; Alt and Windows, which the message does not carry, come from the keyboard.
/// </summary>
internal static class WheelKeys
{
    private const int MkShift = 0x0004, MkControl = 0x0008;

    internal static ModifierKeys FromMessage(nint wParam, ModifierKeys keyboard)
    {
        int flags = unchecked((int)(wParam.ToInt64() & 0xffff));
        var keys = keyboard & (ModifierKeys.Alt | ModifierKeys.Windows);
        if ((flags & MkControl) != 0) keys |= ModifierKeys.Control;
        if ((flags & MkShift) != 0) keys |= ModifierKeys.Shift;
        return keys;
    }
}
