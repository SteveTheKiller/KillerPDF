using System.Windows;

namespace KillerPDF.Shell;

/// <summary>
/// A small second glyph drawn at the corner of a Document tools tile's icon, such as the pen that marks an edit
/// tool. The tile shows the thing being edited (layers, a portfolio, attachments) with the pen on it, so the
/// edit tools no longer all show the same bare pen. Empty means no badge.
/// </summary>
public static class ToolBadge
{
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.RegisterAttached(
        "Glyph", typeof(string), typeof(ToolBadge), new PropertyMetadata(string.Empty));

    public static string GetGlyph(DependencyObject element) => (string)element.GetValue(GlyphProperty);

    public static void SetGlyph(DependencyObject element, string value) => element.SetValue(GlyphProperty, value);
}
