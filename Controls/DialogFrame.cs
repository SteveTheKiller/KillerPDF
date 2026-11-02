using System;
using KillerPDF.Controls;
using KillerPDF.Shell;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace KillerPDF
{
    // Chrome for modal dialog windows (KillerPDF-owned; the kit DialogChrome in Controls/DialogChrome.cs is the shared wordmark and close): Configure (borderless window setup), Frame (the rounded card +
    // title bar + grain), and BuildTitleBar (the KillerPDF wordmark + red close button).
    internal static class DialogFrame
    {
        // Keep generated dialog captions on the same close mark as the main window.
        // E711 renders noticeably smaller inside the 18x16 Win98 caption face; E8BB is
        // the shared chrome glyph used by the main title bar and fills that face correctly.
        public const string CloseGlyph = "";

        // Brush from the owner (then app) resources, with a safe fallback so the helper never throws.
        private static Brush Brush(Window? owner, string key, Brush fallback)
            => (owner?.TryFindResource(key) ?? Application.Current?.TryFindResource(key)) as Brush ?? fallback;
        private static T Value<T>(Window? owner, string key, T fallback)
            => (owner?.TryFindResource(key) ?? Application.Current?.TryFindResource(key)) is T value ? value : fallback;

        internal static Image TitleIcon(Window? owner = null)
        {
            var frames = BitmapDecoder.Create(new Uri("pack://application:,,,/Resources/kp-icon.ico"),
                BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames;
            var icon = new KillerPDF.Controls.AppIcon
            {
                Width = Value(owner, "TitleIconSize", 27.0),
                Height = Value(owner, "TitleIconSize", 27.0),
                Margin = Value(owner, "TitleIconMargin", new Thickness(0, 0, 7, 0)),
                VerticalAlignment = VerticalAlignment.Center,
                Source = frames[0]
            };
            icon.SetResourceReference(FrameworkElement.WidthProperty, "TitleIconSize");
            icon.SetResourceReference(FrameworkElement.HeightProperty, "TitleIconSize");
            icon.SetResourceReference(FrameworkElement.MarginProperty, "TitleIconMargin");
            RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.NearestNeighbor);
            return icon;
        }

        internal static ImageSource? GrainTexture(Window? owner)
        {
            for (Window? window = owner; window is not null; window = window.Owner)
                if (window is MainWindow main && main.GrainTextureSource is not null)
                    return main.GrainTextureSource;
            if (Application.Current?.TryFindResource("GrainTileBrush") is ImageBrush tile)
                return tile.ImageSource;
            return null;
        }

        // Builds the title bar.
        //   win       - the window being chromed (used for DragMove on the whole bar)
        //   owner      - supplies the themed brushes + the ChromeCloseButton style (pass the window's owner)
        //   fullTitle  - the complete title, e.g. "KillerPDF - Transform"; the "KillerPDF" part becomes the
        //                wordmark and the remainder (" - Transform") is rendered in the courier title font
        //   onClose    - invoked when the red close button is clicked (e.g. set a result then Close())
        private sealed class CaptionBorder : Border
        {
            internal Action? Refresh;
            internal static readonly DependencyProperty ClassicProperty = DependencyProperty.Register(
                "Classic", typeof(bool), typeof(CaptionBorder),
                new PropertyMetadata(false, (target, _) => ((CaptionBorder)target).Refresh?.Invoke()));
            internal bool Classic => (bool)GetValue(ClassicProperty);
        }

        public static Border BuildTitleBar(Window win, Window? owner, string? fullTitle, Action onClose)
        {
            // Transparent (not null) background so the WHOLE bar is hit-testable and acts as a drag handle.
            bool caption = Value(owner, "UseDialogCaption", false);
            var bar = new CaptionBorder
            {
                Background = Brush(owner, "DialogTitleBarBrush", Brushes.Transparent),
                SnapsToDevicePixels = true,
                UseLayoutRounding = true
            };
            bar.SetResourceReference(CaptionBorder.ClassicProperty, "UseDialogCaption");
            bar.SetResourceReference(Border.BackgroundProperty, "DialogTitleBarBrush");
            bar.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) win.DragMove(); };

            var grid = new Grid
            {
                // KillerNotes uses the shared title-bar inset for its dialog caption too.  In
                // particular, the 2px top inset keeps the 16px caption button centered in the
                // 20px classic band instead of riding against its upper edge.
                Margin = caption
                    ? Value(owner, "TitleBarPadding", new Thickness(4, 2, 0, 0))
                    : new Thickness(0)
            };
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
                Brush primary   = shadow ? Brushes.Black : Brush(owner, "ChromeTextBrush", Brushes.White);
                Brush logo      = shadow ? Brushes.Black : Brush(owner, "AccentLogo", Brushes.LimeGreen);
                Brush secondary = shadow ? Brushes.Black : Brush(owner, "ChromeTextBrush", Brushes.White);
                int kp = fullTitle?.IndexOf("KillerPDF", StringComparison.Ordinal) ?? -1;
                if (kp >= 0)
                {
                    // Killer + PDF in one TextBlock so the two sizes share a baseline (cohesive wordmark).
                    var logoTb = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
                    logoTb.Inlines.Add(new System.Windows.Documents.Run("Killer") { FontFamily = wordmark, FontWeight = FontWeights.Normal, FontSize = Value(owner, "TitleWordmarkSize", 15.0), Foreground = primary });
                    logoTb.Inlines.Add(new System.Windows.Documents.Run("PDF") { FontFamily = wordmarkPdf, FontWeight = FontWeights.Normal, FontSize = Value(owner, "TitleWordmarkBoldSize", 22.52), Foreground = logo });
                    // The qualifier ("- Transform") rides in the same TextBlock so it shares the
                    // wordmark's baseline instead of centring on its own and floating high.
                    string after = fullTitle![(kp + "KillerPDF".Length)..];
                    if (!string.IsNullOrEmpty(after))
                        logoTb.Inlines.Add(new System.Windows.Documents.Run("  " + after.TrimStart()) { FontFamily = UiKit.BrandSansFont, FontWeight = FontWeights.Normal, FontSize = Value(owner, "TitleSubtitleSize", 18.0), Foreground = secondary });
                    sp.Children.Add(logoTb);
                }
                else
                {
                    // A title without the wordmark reads as the qualifier does: Killer Sans at 18.
                    sp.Children.Add(new TextBlock { Text = fullTitle ?? "", FontFamily = UiKit.BrandSansFont, FontSize = 18, Foreground = primary, VerticalAlignment = VerticalAlignment.Center });
                }
                foreach (var text in sp.Children.OfType<TextBlock>())
                {
                    if (!shadow) text.SetResourceReference(TextBlock.ForegroundProperty, "ChromeTextBrush");
                    foreach (var run in text.Inlines.OfType<System.Windows.Documents.Run>())
                    {
                        run.SetResourceReference(System.Windows.Documents.TextElement.FontSizeProperty,
                            run.Text == "Killer" ? "TitleWordmarkSize" : run.Text == "PDF" ? "TitleWordmarkBoldSize" : "TitleSubtitleSize");
                        if (!shadow) run.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty,
                            run.Text == "PDF" ? "AccentLogo" : "ChromeTextBrush");
                    }
                }
                return sp;
            }

            // Keep one caption and close action while its decorative labels follow the live
            // plain-title/wordmark resources used by the main window and family reference.
            var plain = new TextBlock
            {
                Text = fullTitle ?? "KillerPDF", FontSize = 11, FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center
            };
            plain.SetResourceReference(TextBlock.FontFamilyProperty, "ChromeFontFamily");
            plain.SetResourceReference(TextBlock.ForegroundProperty, "ChromeTextBrush");
            plain.SetResourceReference(UIElement.VisibilityProperty, "PlainTitleVisibility");
            var title = new Grid { Margin = new Thickness(0, -18, 0, -22), VerticalAlignment = VerticalAlignment.Center };
            title.SetResourceReference(UIElement.VisibilityProperty, "WordmarkVisibility");
            {
                var shadowLayer = BuildWordmark(true);
                // The theme's wordmark shadow strength, so a light caption (0) stays clean as the main title bar does.
                shadowLayer.SetResourceReference(UIElement.OpacityProperty, "WordmarkShadowOpacity");
                shadowLayer.Effect = new BlurEffect { Radius = 2 };
                shadowLayer.RenderTransform = new TranslateTransform(0.7, 1.2);
                title.Children.Add(shadowLayer);
                var accentShadow = BuildWordmark(true);
                foreach (var text in accentShadow.Children.OfType<TextBlock>())
                    foreach (var run in text.Inlines.OfType<System.Windows.Documents.Run>())
                        if (run.Text != "PDF") run.Foreground = Brushes.Transparent;
                accentShadow.SetResourceReference(UIElement.OpacityProperty, "WordmarkAccentShadowOpacity");
                accentShadow.IsHitTestVisible = false;
                accentShadow.Effect = new BlurEffect { Radius = 3 };
                accentShadow.RenderTransform = new TranslateTransform(1, 2);
                title.Children.Add(accentShadow);
                title.Children.Add(BuildWordmark(false));
                // Same lift as the main title bar: Lacquer's deep descent makes the centred line sit low.
                if (fullTitle?.Contains("KillerPDF", StringComparison.Ordinal) == true)
                    title.RenderTransform = new TranslateTransform(0, -5);
            }
            // The app icon leads the caption, at the main title bar's size and spacing.
            var titleRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = caption ? new Thickness(0) : new Thickness(6, 0, 0, 0)
            };
            // Negative margins keep the wordmark's deep Lacquer descent out of the layout so the bar
            // never clips its drips (2px imbalance as in the main title bar); the bar sits above the
            // dialog body so the drips can hang over it.
            titleRow.Children.Add(TitleIcon(owner));
            var titleHost = new Grid { VerticalAlignment = VerticalAlignment.Center };
            titleHost.Children.Add(plain);
            titleHost.Children.Add(title);
            titleRow.Children.Add(titleHost);
            Grid.SetColumn(titleRow, 0);
            grid.Children.Add(titleRow);

            // The close glyph and its complete raised/pressed face live in ChromeCloseButton.
            // Supplying another glyph/font/background here was overriding that canonical style and
            // produced the off-centre X and the exposed title-bar pixel seen in classic dialogs.
            var close = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Right,
                // The hover face is part of the card's top-right corner. Centering a 26px
                // button in the 40px caption left a visible 7px strip above it.
                VerticalAlignment = VerticalAlignment.Top,
                Background = Brushes.Transparent,
                Cursor = Cursors.Arrow,
                FocusVisualStyle = null,
                SnapsToDevicePixels = true,
                UseLayoutRounding = true
            };
            // Dialogs use the family close (a bare X that turns red on hover, as on the About card);
            // only the classic caption themes keep the raised caption-style close.
            if (!caption && owner?.TryFindResource("OverlayCloseButton") is Style overlayClose)
            {
                close.Style = overlayClose;
                close.SetResourceReference(ContentControl.ContentProperty, "AboutCloseGlyph");
                close.VerticalAlignment = VerticalAlignment.Center;
            }
            else if (owner?.TryFindResource("ChromeCloseButton") is Style chromeClose)
            {
                close.Style = chromeClose;
            }
            else
            {
                close.Content = CloseGlyph;
                close.FontFamily = UiKit.IconFont;
                close.FontSize = 10;
                close.Width = 46; close.Height = 36;
                close.Foreground = Brush(owner, "DangerRed", Brushes.Red);
                close.Background = Brushes.Transparent;
                close.BorderThickness = new Thickness(0);
                close.Cursor = Cursors.Arrow;
            }
            close.SetResourceReference(FrameworkElement.WidthProperty, "DialogCloseWidth");
            close.SetResourceReference(FrameworkElement.HeightProperty, "DialogCloseHeight");
            close.SetResourceReference(FrameworkElement.MarginProperty, "DialogCaptionButtonsMargin");
            close.SetResourceReference(System.Windows.Automation.AutomationProperties.NameProperty, "Str_Sys_Close");
            close.SetResourceReference(FrameworkElement.ToolTipProperty, "Str_Sys_Close");
            // Resizable borderless dialogs use WindowChrome. Without this exemption its resize
            // band wins the top-right hit test, turning the close button into a resize handle.
            System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(close, true);
            // Get the click before the caption's DragMove handler starts its modal mouse loop.
            close.PreviewMouseLeftButtonDown += (_, e) => { e.Handled = true; onClose(); };
            // Keyboard and automation invoke Click without pointer preview events.
            close.Click += (_, _) => onClose();
            Grid.SetColumn(close, 1);
            grid.Children.Add(close);

            // No grain of its own: the bar is transparent over the card, which already carries it.
            bar.Child = grid;
            bar.Refresh = () =>
            {
                bool classic = bar.Classic;
                if (classic) grid.SetResourceReference(FrameworkElement.MarginProperty, "TitleBarPadding");
                else grid.Margin = new Thickness(0);
                titleRow.Margin = classic ? new Thickness(0) : new Thickness(6, 0, 0, 0);
                string closeStyle = classic ? "ChromeCloseButton" : "OverlayCloseButton";
                // An owned Window does not inherit its owner's local style dictionary.
                if (owner?.TryFindResource(closeStyle) is Style style) close.Style = style;
                close.VerticalAlignment = classic ? VerticalAlignment.Top : VerticalAlignment.Center;
                if (classic) close.ClearValue(ContentControl.ContentProperty);
                else close.SetResourceReference(ContentControl.ContentProperty, "AboutCloseGlyph");
            };
            bar.Refresh();
            Panel.SetZIndex(bar, 1);
            return bar;
        }

        // Borderless transparent window setup shared by every dialog.
        public static void Configure(Window win, Window? owner, bool resizable = false, bool fade = true)
        {
            win.Owner = owner;
            TaskbarIdentity.Track(win);
            win.WindowStyle = WindowStyle.None;
            win.AllowsTransparency = true;
            win.Background = Brushes.Transparent;
            win.ResizeMode = resizable ? ResizeMode.CanResize : ResizeMode.NoResize;
            win.WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;
            win.FontFamily = UiKit.UiFont;
            win.SetResourceReference(Control.ForegroundProperty, "TextBrush");
            win.Resources[typeof(ListBoxItem)] = Application.Current.FindResource("PickerRow");
            var listStyle = new Style(typeof(ListBox));
            listStyle.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("PaneBrush")));
            listStyle.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("TextBrush")));
            listStyle.Setters.Add(new Setter(Control.BorderBrushProperty, new DynamicResourceExtension("CardBorderBrush")));
            listStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
            win.Resources[typeof(ListBox)] = listStyle;
            TextOptions.SetTextFormattingMode(win, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(win, TextRenderingMode.Grayscale);
            if (fade) WindowFx.EnableFadeClose(win);
        }

        private static Border FrameRing(Window? owner, string brushKey, string thicknessKey, string? marginKey = null)
        {
            var ring = new Border
            {
                IsHitTestVisible = false,
                BorderBrush = Brush(owner, brushKey, Brushes.Transparent),
                BorderThickness = Value(owner, thicknessKey, new Thickness(0))
            };
            ring.SetResourceReference(Border.BorderBrushProperty, brushKey);
            ring.SetResourceReference(Border.BorderThicknessProperty, thicknessKey);
            if (marginKey != null)
                ring.SetResourceReference(FrameworkElement.MarginProperty, marginKey);
            return ring;
        }

        private sealed class NegativeStroke : System.Windows.Data.IValueConverter
        {
            public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
                => value is Thickness stroke
                    ? new Thickness(-stroke.Left, -stroke.Top, -stroke.Right, -stroke.Bottom) : new Thickness(0);
            public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
                => System.Windows.Data.Binding.DoNothing;
        }

        private static Grid WindowFrame(Window? owner)
        {
            var frame = new Grid { IsHitTestVisible = false };
            frame.Children.Add(FrameRing(owner, "WindowFrameBrush", "DialogWindowFrameThickness", "WindowFrameMargin"));
            frame.Children.Add(FrameRing(owner, "FrameInnerLightBrush", "FrameInnerLightThickness", "FrameInnerMargin"));
            frame.Children.Add(FrameRing(owner, "FrameInnerDarkBrush", "FrameInnerDarkThickness", "FrameInnerMargin"));
            frame.Children.Add(FrameRing(owner, "FrameOuterLightBrush", "FrameOuterLightThickness"));
            frame.Children.Add(FrameRing(owner, "FrameOuterDarkBrush", "FrameOuterDarkThickness"));
            return frame;
        }

        internal static UIElement WrapContent(
            Window? owner, UIElement content, Thickness? haloMargin = null)
        {
            var host = new Grid
            {
                Margin = haloMargin ?? Value(owner, "DialogHaloMargin", new Thickness(12))
            };
            if (!haloMargin.HasValue) host.SetResourceReference(FrameworkElement.MarginProperty, "DialogHaloMargin");
            var radius = Value(owner, "WindowCornerRadius", new CornerRadius(7));
            var stroke = Value(owner, "DialogFrameThickness", new Thickness(1));
            var shadowFill = Brush(owner, "WindowFrameBrush", UiKit.Brush("MenuBackgroundBrush"));
            var shadow = new Border
            {
                Background = shadowFill,
                BorderBrush = shadowFill,
                BorderThickness = stroke,
                CornerRadius = radius,
                IsHitTestVisible = false,
                Effect = UiKit.ShadowDialog()
            };
            shadow.SetResourceReference(Border.BackgroundProperty, "WindowFrameBrush");
            shadow.SetResourceReference(Border.BorderBrushProperty, "WindowFrameBrush");
            shadow.SetResourceReference(Border.BorderThicknessProperty, "DialogFrameThickness");
            shadow.SetResourceReference(Border.CornerRadiusProperty, "WindowCornerRadius");
            shadow.SetResourceReference(FrameworkElement.TagProperty, "FlyoutShadowOpacity");
            System.Windows.Data.BindingOperations.SetBinding(shadow.Effect, DropShadowEffect.OpacityProperty,
                new System.Windows.Data.Binding(nameof(FrameworkElement.Tag)) { Source = shadow });
            host.Children.Add(shadow);
            // Preserve content positions while the frame clips to its inner stroke edge.
            var layers = new Grid
            {
                Margin = new Thickness(-stroke.Left, -stroke.Top, -stroke.Right, -stroke.Bottom)
            };
            var contentSurface = new Border
            {
                Background = Brush(owner, "BackgroundBrush", UiKit.Brush("BackgroundBrush")),
                Margin = Value(owner, "DialogWindowFramePadding", new Thickness(0)),
                Child = content
            };
            contentSurface.SetResourceReference(Border.BackgroundProperty, "BackgroundBrush");
            contentSurface.SetResourceReference(FrameworkElement.MarginProperty, "DialogWindowFramePadding");
            layers.Children.Add(contentSurface);
            layers.Children.Add(WindowFrame(owner));
            var outer = new Controls.PickerBorder
            {
                Background = Brush(owner, "BackgroundBrush", UiKit.Brush("BackgroundBrush")),
                BorderBrush = Brush(owner, "DialogFrameBrush", UiKit.Brush("MenuBorderBrush")),
                BorderThickness = stroke,
                CornerRadius = radius,
                Child = layers
            };
            outer.SetResourceReference(Border.BackgroundProperty, "BackgroundBrush");
            outer.SetResourceReference(Border.BorderBrushProperty, "DialogFrameBrush");
            outer.SetResourceReference(Border.BorderThicknessProperty, "DialogFrameThickness");
            outer.SetResourceReference(Border.CornerRadiusProperty, "WindowCornerRadius");
            layers.SetBinding(FrameworkElement.MarginProperty, new System.Windows.Data.Binding(nameof(Border.BorderThickness))
                { Source = outer, Converter = new NegativeStroke() });
            host.Children.Add(outer);
            return host;
        }

        // Standard dialog: content is inset from the same five-layer frame used by KillerNotes.
        // A scale other than 1 draws the framed card at the app's scale, title bar included, and
        // grows the window with it; the halo around the card keeps its size.
        public static UIElement Frame(
            Window win, Window? owner, string title, Action onClose, UIElement body,
            Thickness? haloMargin = null, double scale = 1)
        {
            win.KeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; onClose(); } };

            var card = new Border
            {
                // Print Preview already used BackgroundBrush directly. The shared frame used
                // MenuBackgroundBrush, making every other generated window a different color.
                Background = Brush(owner, "BackgroundBrush", UiKit.Brush("BackgroundBrush")),
                CornerRadius = UiKit.RadWindow,
                Margin = Value(owner, "WindowFramePadding", new Thickness(0))
            };

            var root = new DockPanel();
            var titleBar = BuildTitleBar(win, owner, title, onClose);
            titleBar.Height = Value(owner, "DialogTitleBarHeight", 40.0);
            titleBar.SetResourceReference(FrameworkElement.HeightProperty, "DialogTitleBarHeight");
            DockPanel.SetDock(titleBar, Dock.Top);
            root.Children.Add(titleBar);
            root.Children.Add(body);

            var grid = new Grid();
            var grain = new Border { IsHitTestVisible = false };
            grain.SetResourceReference(Border.CornerRadiusProperty, "WindowCornerRadius");
            grain.SetResourceReference(Border.BackgroundProperty, "GrainTileBrush");
            grain.SetResourceReference(UIElement.OpacityProperty, "GrainOpacity");
            grid.Children.Add(grain);
            grid.Children.Add(root);
            card.Child = grid;
            var framedContent = card.Child!;
            card.Child = null;
            DialogScale.Apply(win, grid, scale);
            return WrapContent(owner, framedContent, haloMargin);
        }

        internal static void AddBevels(Grid grid, Window? owner)
        {
            grid.Children.Add(new Border { IsHitTestVisible = false, BorderBrush = Brush(owner, "BevelLightBrush", Brushes.Transparent), BorderThickness = Value(owner, "BevelLightThickness", new Thickness(0)) });
            grid.Children.Add(new Border { IsHitTestVisible = false, BorderBrush = Brush(owner, "BevelDarkBrush", Brushes.Transparent), BorderThickness = Value(owner, "BevelDarkThickness", new Thickness(0)) });
        }
    }
}
