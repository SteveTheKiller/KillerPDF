using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace KillerPDF;

/// <summary>
/// The previous and next page buttons on the page strip under a page window's preview (Transform, Color correction,
/// Stamp, Print preview, Separation preview). One builder so every strip has the same button: a 32 x 26 face with a
/// 16 px Segoe MDL2 chevron centered on its ink, that fills with the accent on hover and press like the family's
/// OutlineButton. No shadow. The caller enables and disables it at the first and last page.
/// </summary>
internal static class PageNavButtons
{
    private const int ChevronLeft = 0xE76B;
    private const int ChevronRight = 0xE76C;
    private const double GlyphSize = 16;

    /// <summary>The previous page button, named "Previous page" for its tooltip and screen readers.</summary>
    internal static Button Previous(Action onClick) => Make("Str_Kb_PrevPage", ChevronLeft, onClick);

    /// <summary>The next page button, named "Next page" for its tooltip and screen readers.</summary>
    internal static Button Next(Action onClick) => Make("Str_Kb_NextPage", ChevronRight, onClick);

    private static Button Make(string labelKey, int glyph, Action onClick)
    {
        string name = Application.Current?.TryFindResource(labelKey) as string ?? labelKey;
        var button = new PageNavButton
        {
            Content = Chevron(glyph),
            ToolTip = name,
            Width = 32,
            Height = KillerPDF.Controls.PageWindowLayout.ControlHeight,
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            FocusVisualStyle = null
        };
        // The glyph is a private-use icon character, so name the button for a screen reader; the label key is the
        // automation id, so tests find it in any language.
        System.Windows.Automation.AutomationProperties.SetName(button, name);
        System.Windows.Automation.AutomationProperties.SetAutomationId(button, labelKey);
        button.Click += (_, _) => onClick();
        return button;
    }

    // An MDL2 glyph fills its em square top to bottom but its ink sits a third of a pixel off the square's middle
    // across, toward the point. A square text box the size of the font centers it up and down; the shift measured
    // from the glyph's own outline centers it across, whatever the font size.
    private static TextBlock Chevron(int codePoint)
    {
        string text = char.ConvertFromUtf32(codePoint);
        var glyph = new TextBlock
        {
            Text = text,
            FontSize = GlyphSize,
            Width = GlyphSize,
            Height = GlyphSize,
            LineHeight = GlyphSize,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        glyph.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        try
        {
            var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(UiKit.IconFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                GlyphSize, Brushes.Black, 1.0);
            Rect ink = formatted.BuildGeometry(new Point(0, 0)).Bounds;
            if (!ink.IsEmpty)
                glyph.RenderTransform = new TranslateTransform(
                    formatted.WidthIncludingTrailingWhitespace / 2 - (ink.Left + ink.Right) / 2,
                    formatted.Height / 2 - (ink.Top + ink.Bottom) / 2);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // Without the outline the glyph stays on its em square, a fraction of a pixel off.
        }
        return glyph;
    }

    /// <summary>
    /// The family's OutlineButton template (the 98SE bevel comes with it) over a quiet chip face: the pane tier at
    /// rest with the pane's edge, the accent fill on hover and press with the text color derived for that fill.
    /// </summary>
    private sealed class PageNavButton : Button
    {
        internal PageNavButton()
        {
            SetResourceReference(StyleProperty, "OutlineButton");
            SetResourceReference(BackgroundProperty, "ChipFaceBrush");
            SetResourceReference(BorderBrushProperty, "ChipEdgeBrush");
            ApplyForeground();
        }

        protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);
            if (e.Property == IsMouseOverProperty || e.Property == IsPressedProperty || e.Property == IsEnabledProperty)
                ApplyForeground();
        }

        // The template's hover trigger sets the accent fill on its inner border; the text follows here, because a
        // style's trigger cannot override a local foreground.
        private void ApplyForeground() =>
            SetResourceReference(ForegroundProperty,
                IsEnabled && (IsMouseOver || IsPressed) ? "OutlineHoverTextBrush" : "TextBrush");
    }
}
