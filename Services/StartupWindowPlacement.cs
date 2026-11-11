using System.Windows;

namespace KillerPDF.Services;

/// <summary>
/// What a main window takes from the saved window settings before it is first shown. A null size
/// or position means the window keeps the one it already has.
/// </summary>
internal readonly record struct StartupWindowPlacement(double? Width, double? Height, double? Left, double? Top, bool Maximized)
{
    internal static readonly StartupWindowPlacement None = new(null, null, null, null, false);

    /// <summary>
    /// Reads the saved size, position and state. The position is used only while part of the window
    /// would still be on the virtual desktop, so a window saved on a monitor that is gone opens centered.
    /// </summary>
    internal static StartupWindowPlacement Decide(Func<string, string?> setting, double defaultWidth, double defaultHeight,
        double virtualLeft, double virtualTop, double virtualWidth, double virtualHeight, bool tornOut)
    {
        // A torn-out window is sized and placed by the window it came from, and opens as a normal window.
        if (tornOut) return None;
        double? width = null, height = null, left = null, top = null;
        if (int.TryParse(setting("WindowWidth"), out int w) &&
            int.TryParse(setting("WindowHeight"), out int h) && w > 200 && h > 200)
        {
            width = w;
            height = h;
        }
        if (int.TryParse(setting("WindowTop"), out int savedTop) &&
            int.TryParse(setting("WindowLeft"), out int savedLeft))
        {
            double shownWidth = width ?? defaultWidth, shownHeight = height ?? defaultHeight;
            double virtualRight = virtualLeft + virtualWidth, virtualBottom = virtualTop + virtualHeight;
            bool onScreen = savedLeft + 100 < virtualRight && savedLeft + shownWidth > virtualLeft
                         && savedTop + 50 < virtualBottom && savedTop + shownHeight > virtualTop;
            if (onScreen)
            {
                left = savedLeft;
                top = savedTop;
            }
        }
        bool maximized = Enum.TryParse(setting("WindowState"), out WindowState state) && state == WindowState.Maximized;
        return new StartupWindowPlacement(width, height, left, top, maximized);
    }
}
