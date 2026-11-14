#nullable enable
using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Navigation;

namespace KillerFamily.Controls
{
    /// <summary>
    /// The right end of every app's footer: the version label, which opens the app's About card, and the
    /// copyright with its link to thekiller.net. The spacing lives here so no app can drift from it: 12 px
    /// after the version and 25 px after the copyright, which ends the text 8 px before the corner grip's
    /// 13 px canvas, set 4 px in from the edge (steve-rules: the same 8 px in every app). The cell margin, its padding and
    /// the 98SE bevel come from the theme (FooterCellMargin, FooterCornerPadding, FooterBevel*). The app sets
    /// <see cref="VersionText"/>'s text and handles <see cref="VersionClick"/>.
    /// </summary>
    public sealed class FooterCorner : Grid
    {
        /// <summary>Space between the version label and the copyright.</summary>
        public const double VersionGap = 12;

        /// <summary>Space after the copyright: 8 px of it is clear of the 17 px the corner grip takes.</summary>
        public const double CornerGap = 25;

        /// <summary>The version label. The app sets its text, such as "v1.9.3".</summary>
        public TextBlock VersionText { get; }

        /// <summary>The copyright line with its link.</summary>
        public TextBlock Copyright { get; }

        /// <summary>The version label was clicked; the app opens its About card.</summary>
        public event MouseButtonEventHandler? VersionClick;

        public FooterCorner()
        {
            HorizontalAlignment = HorizontalAlignment.Right;
            SetResourceReference(MarginProperty, "FooterCellMargin");

            // The 98SE sunken cell; both borders are 0 thick on the other themes.
            var dark = new Border { IsHitTestVisible = false };
            dark.SetResourceReference(Border.BorderBrushProperty, "FooterBevelDarkBrush");
            dark.SetResourceReference(Border.BorderThicknessProperty, "FooterCellLightThickness");
            Panel.SetZIndex(dark, 2);
            var light = new Border { IsHitTestVisible = false };
            light.SetResourceReference(Border.BorderBrushProperty, "FooterBevelLightBrush");
            light.SetResourceReference(Border.BorderThicknessProperty, "FooterCellDarkThickness");
            Panel.SetZIndex(light, 2);
            Children.Add(dark);
            Children.Add(light);

            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            row.SetResourceReference(MarginProperty, "FooterCornerPadding");

            var divider = new Border { Width = 1, Height = 14, Margin = new Thickness(5, 0, 8, 0), IsHitTestVisible = false };
            divider.SetResourceReference(Border.BackgroundProperty, "CardBorderBrush");
            divider.SetResourceReference(VisibilityProperty, "FooterVersionDividerVisibility");
            row.Children.Add(divider);

            VersionText = Text(VersionGap);
            VersionText.Cursor = Cursors.Hand;
            VersionText.SetResourceReference(ToolTipProperty, "Str_TT_About");
            VersionText.MouseLeftButtonDown += (sender, e) => VersionClick?.Invoke(sender, e);
            row.Children.Add(VersionText);

            Copyright = Text(CornerGap);
            var year = new Run("© 2026 ");
            year.SetResourceReference(TextElement.ForegroundProperty, "TextFooter");
            var link = new Hyperlink(new Run("Steve the Killer")) { NavigateUri = new Uri("https://thekiller.net"), TextDecorations = null };
            link.SetResourceReference(TextElement.ForegroundProperty, "TextFooter");
            link.RequestNavigate += Open;
            Copyright.Inlines.Add(year);
            Copyright.Inlines.Add(link);
            row.Children.Add(Copyright);

            Children.Add(row);
        }

        private static TextBlock Text(double gap)
        {
            var text = new TextBlock { Opacity = 0.75, FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, gap, 0) };
            TextOptions.SetTextFormattingMode(text, TextFormattingMode.Ideal);
            text.SetResourceReference(TextBlock.ForegroundProperty, "TextFooter");
            text.SetResourceReference(TextBlock.FontFamilyProperty, "BrandSansFont");
            return text;
        }

        // No registered handler or a failed shell launch must not take the window down.
        private static void Open(object sender, RequestNavigateEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            e.Handled = true;
        }
    }
}
