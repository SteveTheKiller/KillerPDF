using System.Windows;
using System.Windows.Media;

namespace KillerPDF.Controls;

/// <summary>Draws a dialog at the app's scale, like the window it opens over.</summary>
internal static class DialogScale
{
    /// <summary>Scales the dialog's framed content as one and grows the window to hold it: a fixed
    /// width, and the minimum and maximum of a dialog that sizes to its content, never past the
    /// screen's width.</summary>
    internal static void Apply(Window dialog, FrameworkElement content, double scale)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        ArgumentNullException.ThrowIfNull(content);
        if (!double.IsFinite(scale) || scale <= 0 || scale == 1) return;
        content.LayoutTransform = new ScaleTransform(scale, scale);
        double screen = SystemParameters.WorkArea.Width;
        if (double.IsFinite(dialog.Width)) dialog.Width *= scale;
        if (dialog.MinWidth > 0) dialog.MinWidth = Math.Min(dialog.MinWidth * scale, screen);
        if (double.IsFinite(dialog.MaxWidth)) dialog.MaxWidth = Math.Min(dialog.MaxWidth * scale, screen);
    }
}
