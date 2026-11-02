using System.Windows;
using System.Windows.Media;

namespace KillerPDF.Controls;

/// <summary>Draws a dialog at the app's scale, like the window it opens over.</summary>
internal static class DialogScale
{
    /// <summary>Scales the dialog's framed content as one and grows the window to hold it.</summary>
    internal static void Apply(Window dialog, FrameworkElement content, double scale)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        ArgumentNullException.ThrowIfNull(content);
        if (scale == 1) return;
        content.LayoutTransform = new ScaleTransform(scale, scale);
        dialog.Width *= scale;
    }
}
