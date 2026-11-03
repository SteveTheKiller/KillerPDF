using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

// Shared title-bar chrome for floating dialogs (sketch pads, dictation bars, database pickers -
// anything that is its own Window rather than an overlay on the main one).
//
// WHY THIS FILE EXISTS. The wordmark and the close X get hand-rolled once per dialog, and the
// copies drift. In KillerNotes three of them had, independently:
//   - a wordmark + subtitle, with a 46x36 close that filled SOLID RED on hover (the OS caption
//     treatment),
//   - a wordmark and no subtitle, its title moved into the body as a heading instead,
//   - no wordmark at all, just the bare word "Dictation", and a third close-button build.
// Each carried a comment claiming to be the family rule, and they contradicted each other.
//
// The rule, settled: WORDMARK + SUBTITLE to its right, and a close button on the shared
// ChromeCloseButton style. Every dialog builds its caption from here, so the next one cannot drift
// into a fourth variant.
//
// HOST SETUP. Set the three statics once at startup, before any dialog is constructed:
//
//     DialogChrome.AppName       = "KillerNotes";
//     DialogChrome.WordmarkSplit = 6;   // "Killer" | "Notes"
//     DialogChrome.IconUri       = new Uri("pack://application:,,,/Resources/kn-icon.ico");
namespace KillerPDF.Controls
{
    internal static class DialogChrome
    {
        /// <summary>Promoted touch presses can arrive after the mouse button was released.</summary>
        public static void Drag(Window window, MouseButtonEventArgs e)
        {
            if (e.ButtonState != MouseButtonState.Pressed || Mouse.LeftButton != MouseButtonState.Pressed) return;
            try { window.DragMove(); }
            catch (InvalidOperationException) { }
        }
        /// <summary>Product name shown in the caption, e.g. "KillerNotes".</summary>
        public static string AppName { get; set; } = "";

        /// <summary>
        /// Where <see cref="AppName"/> splits into its neutral and accent runs - the family's
        /// two-run wordmark. 6 puts the break after "Killer" for every app but Killendar, whose
        /// name is one word and which sets 3 to split "The Killendar" style. 0 disables the split
        /// and draws the whole name in the neutral color.
        /// </summary>
        public static int WordmarkSplit { get; set; } = 6;

        /// <summary>Pack URI of the title-bar icon. Prefer a multi-size ICO. Null hides the icon.</summary>
        public static Uri? IconUri { get; set; }

        internal static bool SetTitleIcon(Image icon)
        {
            if (IconUri == null) return false;
            var frames = BitmapDecoder.Create(IconUri, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames;
            icon.Source = frames[0];
            RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.NearestNeighbor);
            return true;
        }

        private static object? Res(string key) => Application.Current?.TryFindResource(key);

        /// <summary>
        /// The caption content: the Punktype and Lacquer wordmark, and an icon with a plain bold
        /// title in the chrome font for a theme that declares UseDialogCaption.
        ///
        /// ChromeTextBrush and not TextBrush for the neutral half: a caption band is a dark
        /// gradient on several themes, and TextBrush is the color calibrated for the CONTENT
        /// surface (black on a light theme), which vanishes against it.
        /// </summary>
        public static UIElement Wordmark(string subtitle)
        {
            // BOTH captions are ALWAYS built, and the theme's PlainTitleVisibility and
            // WordmarkVisibility keys (the pair the main title bar binds) pick one. A dialog left
            // open through a live theme switch must not keep the logotype painted over a Win98
            // caption, or the plain title over a wordmark theme.
            var host = new Grid { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };

            // Icon + bold title, from the SAME keys the main window's caption uses, so the two
            // cannot disagree: TitleIconSize, TitleIconMargin, ChromeFontFamily, ChromeTextBrush.
            var flatRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            flatRow.SetResourceReference(UIElement.VisibilityProperty, "PlainTitleVisibility");
            if (TitleIcon() is AppIcon flatIcon) flatRow.Children.Add(flatIcon);
            var plain = new TextBlock
            {
                Text = subtitle.Length > 0 ? AppName + " - " + subtitle : AppName,
                FontSize = 11,
                FontWeight = FontWeights.Bold,   // match the main window's plain title exactly
                VerticalAlignment = VerticalAlignment.Center,
            };
            plain.SetResourceReference(TextBlock.FontFamilyProperty, "ChromeFontFamily");
            plain.SetResourceReference(TextBlock.ForegroundProperty, "ChromeTextBrush");
            flatRow.Children.Add(plain);
            host.Children.Add(flatRow);

            // The app icon leads the wordmark, as it does in the main window's title bar.
            var wf = Res("WordmarkFont") as FontFamily;
            var markRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
            };
            markRow.SetResourceReference(UIElement.VisibilityProperty, "WordmarkVisibility");
            if (TitleIcon() is AppIcon markIcon) markRow.Children.Add(markIcon);

            // Main title bar geometry: the negative margins keep Lacquer's deep descent out of
            // the layout so the caption band never clips the drips, and the 5px lift matches the
            // main window's wordmark.
            var mark = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, -18, 0, -22),
                RenderTransform = new TranslateTransform(0, -5),
            };

            // Shadow copy first, offset a pixel and blurred, so the mark lifts off the band.
            var shadowInk = new SolidColorBrush(Color.FromArgb(0xD8, 0, 0, 0));
            var shadow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(1, 2, 0, 0) };
            shadow.SetResourceReference(UIElement.OpacityProperty, "WordmarkShadowOpacity");
            shadow.Effect = new BlurEffect { Radius = 3 };
            shadow.Children.Add(Text(wf, subtitle, "", "", "", shadowInk));
            mark.Children.Add(shadow);
            // Light/Yellow: the accent half is yellow on a light band, so it gets the same dark
            // halo the main window's wordmark has. WordmarkAccentShadowOpacity is 0 everywhere else.
            var accentShadow = Text(wf, subtitle, "", "", "", Brushes.Transparent);
            if (accentShadow.Inlines.ElementAtOrDefault(1) is Run accentRun) accentRun.Foreground = shadowInk;
            accentShadow.Margin = new Thickness(1, 2, 0, 0);
            accentShadow.IsHitTestVisible = false;
            accentShadow.Effect = new BlurEffect { Radius = 3 };
            accentShadow.SetResourceReference(UIElement.OpacityProperty, "WordmarkAccentShadowOpacity");
            mark.Children.Add(accentShadow);

            // The subtitle takes ChromeTextBrush too, NOT MutedTextBrush. Muted is a color mixed
            // against the CONTENT surface; on a theme that paints a real caption band it lands
            // dark-on-dark and the subtitle disappears while the app name beside it stays perfectly
            // readable. Chrome text is the band's own color by definition. The subtitle still reads
            // as secondary because it is lighter weight and a size down.
            mark.Children.Add(Text(wf, subtitle, "ChromeTextBrush", "AccentLogo", "ChromeTextBrush"));
            markRow.Children.Add(mark);
            host.Children.Add(markRow);
            return host;
        }

        /// <summary>The app icon at the main title bar's size and spacing, or null without an IconUri.</summary>
        private static AppIcon? TitleIcon()
        {
            if (IconUri == null) return null;
            var icon = new AppIcon
            {
                VerticalAlignment = VerticalAlignment.Center,
            };
            icon.SetResourceReference(FrameworkElement.WidthProperty, "TitleIconSize");
            icon.SetResourceReference(FrameworkElement.HeightProperty, "TitleIconSize");
            icon.SetResourceReference(FrameworkElement.MarginProperty, "TitleIconMargin");
            SetTitleIcon(icon);
            return icon;
        }

        private static TextBlock Text(FontFamily? wf, string subtitle,
                                      string neutralKey, string accentKey, string subKey, Brush? flat = null)
        {
            // Sizes come from the SAME two keys the main window's wordmark uses. Hardcoding them
            // here is what made a dialog caption fail to match the main window on every theme that
            // shows the wordmark.
            var tb = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
            tb.SetResourceReference(TextBlock.FontSizeProperty, "TitleWordmarkSize");
            if (wf != null) tb.FontFamily = wf;

            int cut = WordmarkSplit > 0 && WordmarkSplit < AppName.Length ? WordmarkSplit : AppName.Length;
            var neutral = new Run(AppName.Substring(0, cut));
            var accent = new Run(AppName.Substring(cut));
            accent.SetResourceReference(TextElement.FontFamilyProperty, "WordmarkAccentFont");
            accent.SetResourceReference(TextElement.FontSizeProperty, "TitleWordmarkBoldSize");
            var sub = new Run(subtitle.Length > 0 ? "  " + subtitle : "");
            sub.SetResourceReference(TextElement.FontFamilyProperty, "BrandSansFont");
            sub.SetResourceReference(TextElement.FontSizeProperty, "TitleSubtitleSize");

            if (flat != null) { neutral.Foreground = flat; accent.Foreground = flat; sub.Foreground = flat; }
            else
            {
                neutral.SetResourceReference(TextElement.ForegroundProperty, neutralKey);
                accent.SetResourceReference(TextElement.ForegroundProperty, accentKey);
                sub.SetResourceReference(TextElement.ForegroundProperty, subKey);
            }

            tb.Inlines.Add(neutral);
            tb.Inlines.Add(accent);
            tb.Inlines.Add(sub);
            return tb;
        }

        /// <summary>
        /// The family dialog close, in code: the exact equivalent of the OverlayCloseButton markup
        /// ConfirmDialog, FileDialog, About and the F1 overlay use, on the AboutClose* box and
        /// vertically centered in the caption band.
        ///
        /// Only the GLYPH reddens on hover, never a filled red block: a filled block is the Windows
        /// CAPTION treatment (ChromeCloseButton, the main window's own X) and reads wrong on a
        /// window floating over the app. On a flat-caption theme (98SE) the same style draws the
        /// Win98 caption X on its beveled face, from the AboutClose* tokens ThemeManager derives.
        ///
        /// PreviewMouseLeftButtonDown, NOT Click: the caption band under this handles ButtonDown to
        /// drag the window, DragMove() runs a modal loop, and the matching ButtonUp - and therefore
        /// Click - never arrives, which is how a close X ends up doing nothing. Tunnelling gets it
        /// first, and marking it handled stops a drag starting on a click aimed at the X. Click is
        /// still wired as the keyboard and automation path (Space or Enter on the focused X, UI
        /// Automation Invoke); the handled pointer press never raises it, so a mouse click closes
        /// once.
        /// </summary>
        public static FrameworkElement CloseGlyph(string tooltip, Action onClose)
        {
            var glyph = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Background = Brushes.Transparent,          // Transparent, not null: null is not hit-testable
                Cursor = Cursors.Hand,
                ToolTip = tooltip,
            };
            System.Windows.Automation.AutomationProperties.SetName(glyph, tooltip);
            if (Application.Current?.TryFindResource("OverlayCloseButton") is Style s) glyph.Style = s;
            glyph.SetResourceReference(ContentControl.ContentProperty, "AboutCloseGlyph");
            glyph.SetResourceReference(FrameworkElement.WidthProperty, "AboutCloseWidth");
            glyph.SetResourceReference(FrameworkElement.HeightProperty, "AboutCloseHeight");
            glyph.PreviewMouseLeftButtonDown += (_, e) => { e.Handled = true; onClose(); };
            glyph.Click += (_, _) => onClose();
            return glyph;
        }
    }
}
