using System;
using KillerFamily.Windowing;
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
        // The title row's left inset on the card themes. 2 puts the icon's ink 3 in from the card's edge,
        // 3 below its top (Steve 2026-11-26; 6 left it 7 in).
        internal const double TitleRowInset = 2;

        // Keep generated dialog captions on the same close mark as the main window.
        // E711 renders noticeably smaller inside the 18x16 Win98 caption face; E8BB is
        // the shared chrome glyph used by the main title bar and fills that face correctly.
        public const string CloseGlyph = "";

        // Brush from the owner (then app) resources, with a safe fallback so the helper never throws.
        private static Brush Brush(Window? owner, string key, Brush fallback)
            => (owner?.TryFindResource(key) ?? Application.Current?.TryFindResource(key)) as Brush ?? fallback;
        private static T Value<T>(Window? owner, string key, T fallback)
            => (owner?.TryFindResource(key) ?? Application.Current?.TryFindResource(key)) is T value ? value : fallback;
        // #506: an ownerless dialog (uninstall, setup) still takes the app's close styles. Without
        // the app fallback its close button lost its style and drew as a bare red box.
        private static Style? CloseStyle(Window? owner, string key)
            => (owner?.TryFindResource(key) ?? Application.Current?.TryFindResource(key)) as Style;

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

        //   accessory  - optional control placed just left of the close button (Print's 1 / 2 / Book
        //                switch); it stays clickable inside the WindowChrome resize band
        public static Border BuildTitleBar(Window win, Window? owner, string? fullTitle, Action onClose,
            FrameworkElement? accessory = null)
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
            bar.MouseLeftButtonDown += (_, e) =>
            {
                if (e.ButtonState != MouseButtonState.Pressed) return;
                // A double-click maximizes or restores a resizable dialog, as on the main window.
                if (e.ClickCount == 2 && win.ResizeMode == ResizeMode.CanResize)
                {
                    e.Handled = true;
                    win.WindowState = win.WindowState == WindowState.Maximized
                        ? WindowState.Normal : WindowState.Maximized;
                    return;
                }
                win.DragMove();
            };

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
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            if (accessory is not null)
            {
                accessory.VerticalAlignment = VerticalAlignment.Center;
                accessory.Margin = new Thickness(0, 0, 8, 0);
                System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(accessory, true);
                Grid.SetColumn(accessory, 1);
                grid.Children.Add(accessory);
            }

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
                    var plainTitle = new TextBlock { Text = fullTitle ?? "", FontFamily = UiKit.BrandSansFont, FontSize = 18, Foreground = primary, VerticalAlignment = VerticalAlignment.Center };
                    // Drawn like the main window's file name, from the same resources.
                    plainTitle.SetResourceReference(TextBlock.FontFamilyProperty, "FileNameFont");
                    plainTitle.SetResourceReference(TextBlock.FontSizeProperty, "FileNameFontSize");
                    sp.Children.Add(plainTitle);
                }
                foreach (var text in sp.Children.OfType<TextBlock>())
                {
                    if (!shadow) text.SetResourceReference(TextBlock.ForegroundProperty, text.Inlines.Count == 0 ? "FilenameBrush" : "ChromeTextBrush");
                    foreach (var run in text.Inlines.OfType<System.Windows.Documents.Run>())
                    {
                        bool wordmarkRun = run.Text is "Killer" or "PDF";
                        run.SetResourceReference(System.Windows.Documents.TextElement.FontSizeProperty,
                            run.Text == "Killer" ? "TitleWordmarkSize" : run.Text == "PDF" ? "TitleWordmarkBoldSize" : "FileNameFontSize");
                        // The words after the wordmark take the main window's file name face, size and color,
                        // so a dialog's title reads like the title bar it came from.
                        if (!wordmarkRun) run.SetResourceReference(System.Windows.Documents.TextElement.FontFamilyProperty, "FileNameFont");
                        if (!shadow) run.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty,
                            run.Text == "PDF" ? "AccentLogo" : wordmarkRun ? "ChromeTextBrush" : "FilenameBrush");
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
            // The main title bar's lift for the plain caption (0,-1 on 98SE). The classic row sits
            // under TitleBarPadding's 2px top inset, which put Microsoft Sans Serif a pixel below
            // the band's middle, exactly as on the main window before it took this margin.
            plain.SetResourceReference(FrameworkElement.MarginProperty, "TitleTextMargin");
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
                var crisp = BuildWordmark(false);
                // The main window's file name draws ClearType in Ideal formatting. A dialog is a layered
                // window that Configure sets to grayscale, which drew the same font and size visibly
                // thinner. ClearTypeHint lets this opaque caption keep ClearType on a layered window.
                TextOptions.SetTextFormattingMode(crisp, TextFormattingMode.Ideal);
                TextOptions.SetTextRenderingMode(crisp, TextRenderingMode.ClearType);
                RenderOptions.SetClearTypeHint(crisp, ClearTypeHint.Enabled);
                title.Children.Add(crisp);
                // Same lift as the main title bar: Lacquer's deep descent makes the centred line sit low.
                if (fullTitle?.Contains("KillerPDF", StringComparison.Ordinal) == true)
                    title.RenderTransform = new TranslateTransform(0, -5);
            }
            // The app icon leads the caption, at the main title bar's size and spacing.
            var titleRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = caption ? new Thickness(0) : new Thickness(TitleRowInset, 0, 0, 0)
            };
            // Negative margins keep the wordmark's deep Lacquer descent out of the layout so the bar
            // never clips its drips (2px imbalance as in the main title bar); the bar sits above the
            // dialog body so the drips can hang over it.
            var rowIcon = TitleIcon(owner);
            // The dialog icon sat 2px high against the wordmark compared with the main title bar
            // (Steve 2026-11-25), so the wordmark themes drop it 2px. Classic captions keep it centered.
            if (!caption) rowIcon.RenderTransform = new TranslateTransform(0, 2);
            titleRow.Children.Add(rowIcon);
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
            if (!caption && CloseStyle(owner, "OverlayCloseButton") is Style overlayClose)
            {
                close.Style = overlayClose;
                close.SetResourceReference(ContentControl.ContentProperty, "AboutCloseGlyph");
                close.VerticalAlignment = VerticalAlignment.Center;
            }
            else if (CloseStyle(owner, "ChromeCloseButton") is Style chromeClose)
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
            Grid.SetColumn(close, 2);
            grid.Children.Add(close);

            // No grain of its own: the bar is transparent over the card, which already carries it.
            bar.Child = grid;
            bar.Refresh = () =>
            {
                bool classic = bar.Classic;
                if (classic) grid.SetResourceReference(FrameworkElement.MarginProperty, "TitleBarPadding");
                else grid.Margin = new Thickness(0);
                titleRow.Margin = classic ? new Thickness(0) : new Thickness(TitleRowInset, 0, 0, 0);
                string closeStyle = classic ? "ChromeCloseButton" : "OverlayCloseButton";
                // An owned Window does not inherit its owner's local style dictionary.
                if (CloseStyle(owner, closeStyle) is Style style) close.Style = style;
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
            if (!PaneCentered.TryGetValue(win, out _))
            {
                PaneCentered.Add(win, new object());
                win.SourceInitialized += (_, _) => CenterOnOwnerPane(win);
            }
            // A dialog that declares itself resizable really is: edges, a maximize that respects
            // the taskbar, and never smaller than the size it opened at.
            if (resizable) EnableEdgeResize(win);
        }

        // ---- Page windows: resizable, sized from their options, remembered per type ----------
        //
        // KillerUI gives these borderless dialogs no resize edges: WindowStyle.None with
        // AllowsTransparency ignores ResizeMode.CanResize. WindowChrome restores the edges. The
        // card sits inside a 12 DIP transparent halo for its shadow, so the resize band matches
        // the halo and the drag zone reaches the card's visible edge.
        private const double ResizeBand = 12;

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Window, object> Resizable = new();
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Window, object> PageWindows = new();
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Window, object> DeclaredMinimums = new();
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Window, object> PaneCentered = new();

        /// <summary>
        /// Keeps the MinWidth and MinHeight a resizable dialog declares instead of raising them to the size it
        /// opens at. For a dialog that opens wider than its layout needs (one sized from the app scale, whose
        /// content scrolls), so it can still be made smaller on a small screen.
        /// </summary>
        internal static void KeepDeclaredMinimum(Window win) => DeclaredMinimums.AddOrUpdate(win, new object());

        internal static void EnableEdgeResize(Window win)
        {
            win.ResizeMode = ResizeMode.CanResize;
            if (Resizable.TryGetValue(win, out _)) return;
            Resizable.Add(win, new object());
            System.Windows.Shell.WindowChrome.SetWindowChrome(win, new System.Windows.Shell.WindowChrome
            {
                ResizeBorderThickness = new Thickness(ResizeBand),
                CaptionHeight         = 0,
                GlassFrameThickness   = new Thickness(0),
                CornerRadius          = new CornerRadius(0),
                UseAeroCaptionButtons = false
            });
            win.SourceInitialized += (_, _) =>
            {
                if (PresentationSource.FromVisual(win) is System.Windows.Interop.HwndSource source)
                    source.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
                    {
                        if (msg == WM_GETMINMAXINFO) { ClampMaximized(win, hwnd, lParam); handled = true; }
                        return IntPtr.Zero;
                    });
                ApplyWindowState(win);
            };
            win.StateChanged += (_, _) => ApplyWindowState(win);
            // The opening size is the smallest the layout was designed for. Page windows set
            // their own minimum from their options instead.
            win.Loaded += (_, _) =>
            {
                if (PageWindows.TryGetValue(win, out _) || DeclaredMinimums.TryGetValue(win, out _)) return;
                Size min = PageWindowSizing.OpeningMinimum(new Size(win.ActualWidth, win.ActualHeight),
                    new Size(win.MinWidth, win.MinHeight),
                    new Size(SystemParameters.WorkArea.Width, SystemParameters.WorkArea.Height));
                win.MinWidth = min.Width;
                win.MinHeight = min.Height;
            };
        }

        /// <summary>
        /// Opts a page window (Print, Transform, Color correction, Stamp) into the shared size
        /// rules. Call after Content is set to DialogFrame.Frame's result. Before the window is
        /// shown, its framed content is measured with <paramref name="preview"/> collapsed: that
        /// height (options, footer, title bar and frame) is the window's height and MinHeight, and
        /// MinWidth adds a one-page preview floor. A placement saved for <paramref name="windowType"/>
        /// ("Print", "Transform", "ColorCorrection", "Stamp") is restored and raised to that
        /// minimum; otherwise the window opens centered on its owner, <paramref name="fittedExtraWidth"/>
        /// wider than the minimum (read after a layout pass at the minimum size, so it can look at
        /// the preview pane). Either way it is then fitted to the monitor's work area, and the
        /// placement is saved on close. Double-clicking the title bar maximizes it.
        /// </summary>
        internal static void EnablePageSizing(Window win, string windowType, UIElement preview,
            double scale = 1, double previewFloor = PageWindowSizing.PreviewFloor,
            Func<double>? fittedExtraWidth = null)
        {
            PageWindows.AddOrUpdate(win, new object());
            EnableEdgeResize(win);
            win.SourceInitialized += (_, _) =>
            {
                if (win.Content is not FrameworkElement content) return;
                var shown = preview.Visibility;
                preview.Visibility = Visibility.Collapsed;
                content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Size optionsOnly = content.DesiredSize;
                preview.Visibility = shown;

                Size min = PageWindowSizing.Minimum(optionsOnly, previewFloor, scale);
                win.SizeToContent = SizeToContent.Manual;
                win.MinWidth = min.Width;
                win.MinHeight = min.Height;
                win.Width = min.Width;
                win.Height = min.Height;

                bool restored = PageWindowSizing.Restore(win, windowType, App.GetSetting);
                if (!restored)
                {
                    if (fittedExtraWidth is not null)
                    {
                        content.Measure(min);
                        content.Arrange(new Rect(min));
                        double extra = fittedExtraWidth();
                        if (double.IsFinite(extra) && extra > 0) win.Width = Math.Ceiling(min.Width + extra);
                    }
                    CenterOnOwner(win);
                    WorkAreaFit.Fit(win, keepMargin: true);
                    // A window the work area shrank is centered on its owner again, then kept on screen.
                    CenterOnOwner(win);
                }
                // A page window always opens centered on the app window. The saved placement
                // keeps its size and maximized state, but its position is not replayed: a left
                // edge remembered from one open (or another monitor) put Transform at the far
                // left of the app on every later open.
                else if (win.WindowState == WindowState.Normal) CenterOnOwner(win);
                WorkAreaFit.Fit(win, keepMargin: !restored);
                // A placement restored as maximized drops the halo before the first frame.
                ApplyWindowState(win);
            };
            win.Closing += (_, _) => PageWindowSizing.Save(win, windowType, App.SetSetting);
        }

        // Maximized, the card fills the work area: no shadow halo and no resize band around it.
        private static void ApplyWindowState(Window win)
        {
            bool max = win.WindowState == WindowState.Maximized;
            if (win.Content is FrameworkElement host)
            {
                if (max) host.Margin = new Thickness(0);
                else host.SetResourceReference(FrameworkElement.MarginProperty, "DialogHaloMargin");
            }
            if (System.Windows.Shell.WindowChrome.GetWindowChrome(win) is { } chrome)
                chrome.ResizeBorderThickness = new Thickness(max ? 0 : ResizeBand);
        }

        // WindowStartupLocation.CenterOwner is resolved by WPF after this point, and WorkAreaFit
        // needs a real position to pick the monitor, so the centering is done here.
        private static void CenterOnOwner(Window win)
        {
            win.WindowStartupLocation = WindowStartupLocation.Manual;
            Rect area;
            if (win.Owner is { } owner && PresentationSource.FromVisual(owner) is System.Windows.Interop.HwndSource os
                && os.CompositionTarget is { } target
                && GetWindowRect(os.Handle, out RECT r) && r.right > r.left)
            {
                Point a = target.TransformFromDevice.Transform(new Point(r.left, r.top));
                Point b = target.TransformFromDevice.Transform(new Point(r.right, r.bottom));
                area = new Rect(a, b);
            }
            else area = SystemParameters.WorkArea;
            Point corner = PageWindowSizing.CenteredIn(area, new Size(win.Width, win.Height));
            win.Left = corner.X;
            win.Top = corner.Y;
        }

        // ---- Small dialogs over the pane they came from -------------------------------------
        //
        // With the document area split, a dialog opened from one pane opens centered on THAT pane
        // (the pane that has focus: a click, a wheel or keyboard focus moves it before any
        // command runs), the way the signatures picker opens inside its pane. Page windows
        // (EnablePageSizing) and any dialog bigger than the pane keep centering on the app
        // window. Unsplit, or owned by anything but the main window, WPF's CenterOwner stays.
        private static void CenterOnOwnerPane(Window win)
        {
            if (PageWindows.TryGetValue(win, out _)) return;
            if (win.WindowStartupLocation != WindowStartupLocation.CenterOwner) return;
            if (win.Owner is not KillerPDF.Shell.MainWindow main || main.ActivePaneScreenArea() is not Rect pane) return;

            Size size = OpeningSize(win);
            if (!PageWindowSizing.FitsOnPane(pane, size)) return;
            Point corner = PageWindowSizing.CenteredIn(pane, size);
            win.WindowStartupLocation = WindowStartupLocation.Manual;
            win.Left = corner.X;
            win.Top = corner.Y;
            WorkAreaFit.Fit(win, keepMargin: false);
        }

        // The size a dialog is about to open at. A size-to-content dialog has no Width or Height
        // yet at SourceInitialized, so its content is measured the way WPF will size it.
        private static Size OpeningSize(Window win)
        {
            double width = win.Width, height = win.Height;
            if ((double.IsNaN(width) || double.IsNaN(height)) && win.Content is UIElement content)
            {
                content.Measure(new Size(double.IsNaN(width) ? double.PositiveInfinity : width,
                                         double.IsNaN(height) ? double.PositiveInfinity : height));
                if (double.IsNaN(width)) width = Math.Max(content.DesiredSize.Width, win.MinWidth);
                if (double.IsNaN(height)) height = Math.Max(content.DesiredSize.Height, win.MinHeight);
            }
            return new Size(width, height);
        }

        private const int WM_GETMINMAXINFO = 0x0024;
        private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

        // A WindowStyle.None window maximizes over the taskbar unless the maximize size is clamped
        // to the monitor's work area. Handling the message takes it away from WPF, which is how WPF
        // enforces MinWidth and MinHeight on a drag, so the minimum track size is set here too.
        private static void ClampMaximized(Window win, IntPtr hwnd, IntPtr lParam)
        {
            var mmi = System.Runtime.InteropServices.Marshal.PtrToStructure<MINMAXINFO>(lParam);
            IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (monitor != IntPtr.Zero)
            {
                var info = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(MONITORINFO)) };
                if (GetMonitorInfo(monitor, ref info))
                {
                    RECT mon = info.rcMonitor, work = info.rcWork;
                    mmi.ptMaxPosition.x = Math.Abs(work.left - mon.left);
                    mmi.ptMaxPosition.y = Math.Abs(work.top - mon.top);
                    mmi.ptMaxSize.x = Math.Abs(work.right - work.left);
                    mmi.ptMaxSize.y = Math.Abs(work.bottom - work.top);
                    mmi.ptMaxTrackSize.x = Math.Max(mmi.ptMaxTrackSize.x, mmi.ptMaxSize.x);
                    mmi.ptMaxTrackSize.y = Math.Max(mmi.ptMaxTrackSize.y, mmi.ptMaxSize.y);
                }
            }
            var src = PresentationSource.FromVisual(win);
            double sx = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            double sy = src?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
            if (win.MinWidth > 0) mmi.ptMinTrackSize.x = (int)Math.Ceiling(win.MinWidth * sx);
            if (win.MinHeight > 0) mmi.ptMinTrackSize.y = (int)Math.Ceiling(win.MinHeight * sy);
            System.Runtime.InteropServices.Marshal.StructureToPtr(mmi, lParam, true);
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct POINT { public int x, y; }
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct RECT { public int left, top, right, bottom; }
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct MINMAXINFO { public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize; }
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
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
            Thickness? haloMargin = null, double scale = 1, FrameworkElement? titleAccessory = null)
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
            var titleBar = BuildTitleBar(win, owner, title, onClose, titleAccessory);
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
