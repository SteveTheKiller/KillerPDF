using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace KillerPDF
{
    // Chrome for modal dialog windows: Configure (borderless window setup), Frame (the rounded card +
    // title bar + grain), and BuildTitleBar (the KillerPDF wordmark + red close button).
    internal static class DialogChrome
    {
        public const string CloseGlyph = ""; // Segoe MDL2 ChromeClose

        // Brush from the owner (then app) resources, with a safe fallback so the helper never throws.
        private static Brush Brush(Window? owner, string key, Brush fallback)
            => (owner?.TryFindResource(key) ?? Application.Current?.TryFindResource(key)) as Brush ?? fallback;

        // Builds the title bar.
        //   win       - the window being chromed (used for DragMove on the whole bar)
        //   owner      - supplies the themed brushes + the ChromeCloseButton style (pass the window's owner)
        //   fullTitle  - the complete title, e.g. "KillerPDF - Transform"; the "KillerPDF" part becomes the
        //                wordmark and the remainder (" - Transform") is rendered in the courier title font
        //   onClose    - invoked when the red close button is clicked (e.g. set a result then Close())
        public static Border BuildTitleBar(Window win, Window? owner, string? fullTitle, Action onClose)
        {
            // Transparent (not null) background so the WHOLE bar is hit-testable and acts as a drag handle.
            var bar = new Border { Background = Brushes.Transparent };
            bar.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) win.DragMove(); };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var wordmark = UiKit.WordmarkFont;
            var wordmarkPdf = UiKit.WordmarkFontPdf;

            // Build the wordmark row. A DropShadowEffect applied directly to text rasterizes it and
            // disables ClearType, which reads as blurry. So we LAYER it instead: a blurred black duplicate
            // sits behind a crisp, effect-free copy - soft shadow, sharp text. `shadow` paints the duplicate.
            StackPanel BuildWordmark(bool shadow)
            {
                var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                Brush primary   = shadow ? Brushes.Black : Brush(owner, "TextBrush", Brushes.White);
                Brush logo      = shadow ? Brushes.Black : Brush(owner, "AccentLogo", Brushes.LimeGreen);
                Brush secondary = shadow ? Brushes.Black : Brush(owner, "MutedTextBrush", Brushes.Gray);
                int kp = fullTitle?.IndexOf("KillerPDF", StringComparison.Ordinal) ?? -1;
                if (kp >= 0)
                {
                    // Killer + PDF in one TextBlock so the two sizes share a baseline (cohesive wordmark).
                    var logoTb = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
                    logoTb.Inlines.Add(new System.Windows.Documents.Run("Killer") { FontFamily = wordmark, FontWeight = FontWeights.Normal, FontSize = 16, Foreground = primary });
                    logoTb.Inlines.Add(new System.Windows.Documents.Run("PDF") { FontFamily = wordmarkPdf, FontWeight = FontWeights.Bold, FontSize = 20.8, Foreground = logo });
                    sp.Children.Add(logoTb);
                    string after = fullTitle![(kp + "KillerPDF".Length)..];
                    if (!string.IsNullOrEmpty(after))
                        sp.Children.Add(new TextBlock { Text = after, FontFamily = UiKit.MonoFont, FontSize = 14, Foreground = secondary, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 1, 0, 0) });
                }
                else
                {
                    sp.Children.Add(new TextBlock { Text = fullTitle ?? "", FontFamily = UiKit.MonoFont, FontSize = 14, Foreground = primary, VerticalAlignment = VerticalAlignment.Center });
                }
                return sp;
            }

            var title = new Grid { Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            var shadowLayer = BuildWordmark(true);
            shadowLayer.Opacity = 0.5;
            shadowLayer.Effect = new BlurEffect { Radius = 2 };
            shadowLayer.RenderTransform = new TranslateTransform(0.7, 1.2);
            title.Children.Add(shadowLayer);
            title.Children.Add(BuildWordmark(false));
            Grid.SetColumn(title, 0);
            grid.Children.Add(title);

            // Full red rounded-corner close button (ChromeCloseButton), matching the main window chrome.
            var close = new Button { Content = CloseGlyph };
            if (owner?.TryFindResource("ChromeCloseButton") is Style chromeClose)
            {
                close.Style = chromeClose;
            }
            else
            {
                close.FontFamily = UiKit.IconFont;
                close.FontSize = 10;
                close.Width = 46; close.Height = 36;
                close.Foreground = Brush(owner, "DangerRed", Brushes.Red);
                close.Background = Brushes.Transparent;
                close.BorderThickness = new Thickness(0);
                close.Cursor = Cursors.Hand;
            }
            close.Click += (_, _2) => onClose();
            Grid.SetColumn(close, 1);
            grid.Children.Add(close);

            bar.Child = grid;
            return bar;
        }

        // Borderless transparent window setup shared by every dialog.
        public static void Configure(Window win, Window? owner, bool resizable = false, bool fade = true)
        {
            win.Owner = owner;
            win.WindowStyle = WindowStyle.None;
            win.AllowsTransparency = true;
            win.Background = Brushes.Transparent;
            win.ResizeMode = resizable ? ResizeMode.CanResize : ResizeMode.NoResize;
            win.WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;
            win.FontFamily = UiKit.UiFont;
            TextOptions.SetTextFormattingMode(win, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(win, TextRenderingMode.Grayscale);
            if (fade) WindowFx.EnableFadeClose(win);
        }

        // Standard dialog card: rounded themed border + shadow, the title bar on top, film grain, Esc-to-close.
        public static Border Frame(Window win, Window? owner, string title, Action onClose, UIElement body)
        {
            win.KeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; onClose(); } };

            var card = new Border
            {
                Background = UiKit.Brush("MenuBackgroundBrush"),
                BorderBrush = UiKit.Brush("MenuBorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = UiKit.RadWindow,
                Margin = new Thickness(12),
                Effect = UiKit.ShadowDialog()
            };

            var root = new DockPanel();
            var titleBar = BuildTitleBar(win, owner, title, onClose);
            titleBar.Height = 40;
            DockPanel.SetDock(titleBar, Dock.Top);
            root.Children.Add(titleBar);
            root.Children.Add(body);

            var grain = (owner as MainWindow)?.GrainTexture;
            if (grain != null)
            {
                var grid = new Grid();
                double op = Application.Current?.Resources["GrainOpacity"] is double go ? go : 0.05;
                grid.Children.Add(new Border
                {
                    CornerRadius = UiKit.RadWindow, IsHitTestVisible = false, Opacity = op,
                    Background = new ImageBrush(grain) { TileMode = TileMode.Tile, ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 256, 256), Stretch = Stretch.None }
                });
                grid.Children.Add(root);
                card.Child = grid;
            }
            else card.Child = root;
            return card;
        }
    }
}
