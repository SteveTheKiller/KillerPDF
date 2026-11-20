namespace KillerPDF.Services
{
    // #508: the shortcut card's size limit follows the main window. A fixed 920 by 620 card left
    // German descriptions wrapping and the list scrolling even on a large window.
    internal static class ShortcutCardSize
    {
        internal const double MinWidth = 920, MinHeight = 620, MaxWidth = 1240, MaxHeight = 900;
        private const double Share = 0.85;

        internal static (double Width, double Height) Caps(double viewportWidth, double viewportHeight)
            => (Math.Clamp(Math.Round(viewportWidth * Share), MinWidth, MaxWidth),
                Math.Clamp(Math.Round(viewportHeight * Share), MinHeight, MaxHeight));
    }
}
