using System.Runtime.InteropServices;

namespace KillerPDF.Services;

/// <summary>
/// #513: Shift+Enter on PDFs selected in File Explorer opens them together in a new window. Explorer
/// starts one launch per file and passes no keyboard state, so each launch reads Shift itself as
/// early as it can and marks the path it forwards to the running window.
/// </summary>
internal static partial class ShiftLaunch
{
    private const string Marker = "::KILLERPDF_NEW_WINDOW::";
    private const int VkShift = 0x10;

    /// <summary>
    /// How long after a Shift launch starts KillerPDF the rest of that selection still joins its
    /// window, so one Shift+Enter with KillerPDF closed does not make two windows.
    /// </summary>
    internal static readonly TimeSpan JoinPeriod = TimeSpan.FromSeconds(10);

    /// <summary>Whether Shift is down right now, read from the keyboard rather than this thread's queue.</summary>
    internal static bool ShiftHeld() => (GetAsyncKeyState(VkShift) & 0x8000) != 0;

    internal static string Mark(string path) => Marker + path;

    internal static bool TryUnmark(string message, out string path)
    {
        path = "";
        if (!message.StartsWith(Marker, StringComparison.Ordinal) || message.Length == Marker.Length) return false;
        path = message[Marker.Length..];
        return true;
    }

    /// <summary>
    /// A marked launch opens a new window, unless KillerPDF itself was just started by a Shift launch:
    /// then the window it opened is the new window, and the rest of the selection joins it.
    /// </summary>
    internal static bool OpensNewWindow(bool primaryStartedWithShift, TimeSpan sinceStart) =>
        !primaryStartedWithShift || sinceStart >= JoinPeriod;

    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int key);
}
