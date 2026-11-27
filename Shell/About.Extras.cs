using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using KillerPDF.Features;
using KillerPDF.Features.About;
using KillerPDF.Services;

namespace KillerPDF.Shell
{
    /// <summary>
    /// APP-OWNED: what KillerPDF's About card adds to the shared core (Shell/About.cs): the two-run
    /// wordmark and linked tagline, the card opening above a tool window or dialog the main window
    /// owns, the Close to system tray row, Clear all Data, and the unsaved-changes gate and document
    /// reopen the update hooks use (Features/About/AboutController.Extras.cs).
    /// </summary>
    public partial class MainWindow : IAboutTray, IAboutUpdateRestart
    {
        bool IAboutTray.CloseToTray { set => AboutCloseToTray.IsChecked = value; }

        private void AboutCloseToTray_Click(object sender, RoutedEventArgs e)
            => _about.SetCloseToTray(AboutCloseToTray.IsChecked == true);

        /// <summary>Closes the card, and the window carrying it when it opened above another window.</summary>
        private void CloseAboutOverlay() => FadeOverlayOut(AboutOverlay);

        // The overlays are mutually exclusive: the F1 overlay fades out, the static content is
        // built, and the card moves above a front tool window when one is open.
        partial void ShowingCard(ref bool handled)
        {
            if (ShortcutsOverlay.Visibility == Visibility.Visible) FadeOverlayOut(ShortcutsOverlay);
            BuildAboutStaticContent();
            if (_aboutHost is null && AboutFrontWindow() is { } front) HostAboutAbove(front);
            FadePanelIn(AboutOverlay);
            handled = true;
        }

        // KillerPDF's own short fade, which resets the overlay for the next open; About also
        // closes the window it was carried in.
        partial void OverlayFadingOut(UIElement overlay, ref bool handled)
        {
            if (ReferenceEquals(overlay, AboutOverlay) && _aboutHost is { } host) FadePanelOut(overlay, () => host.Close());
            else FadePanelOut(overlay);
            handled = true;
        }

        partial void CloseShortcutsForAbout(ref bool handled)
        {
            if (ShortcutsOverlay.Visibility == Visibility.Visible) FadeOverlayOut(ShortcutsOverlay);
            handled = true;
        }

        // ---- About above dialogs and tool windows ----------------------------------------------
        //
        // The card is a main-window overlay. A tool window such as Transform, or a dialog, is owned
        // by the main window and always sits above it, so an overlay drawn there would open behind
        // that window and leave it in front of the darkened backdrop. While one is open, the same
        // card moves into a transparent window over the main window, owned by the window in front,
        // and comes back when About closes. F12 also opens it from those windows.

        private Window? _aboutHost;
        private Panel? _aboutHome;
        private int _aboutHomeIndex;

        static MainWindow()
        {
            EventManager.RegisterClassHandler(typeof(Window), Keyboard.PreviewKeyDownEvent,
                new KeyEventHandler(OwnedWindowAboutKey));
        }

        /// <summary>F12 (or whatever key About is mapped to) pressed in a window the main window
        /// owns, directly or through another owned window, opens About above it.</summary>
        private static void OwnedWindowAboutKey(object sender, KeyEventArgs e)
        {
            if (e.Handled || sender is not Window window || window is MainWindow) return;
            MainWindow? main = null;
            for (Window? owner = window.Owner; owner is not null; owner = owner.Owner)
                if (owner is MainWindow found) { main = found; break; }
            if (main is null || ReferenceEquals(window, main._aboutHost)) return;
            var (key, _, _, swallow) = main.TranslateShortcut(e.Key, Keyboard.Modifiers, e.SystemKey);
            if (swallow || key != Key.F12) return;
            main.ShowAboutOverlay();
            e.Handled = true;
        }

        /// <summary>The window About should open above: the active window the main window owns,
        /// else the newest one still showing, else none and the card stays a main-window overlay.</summary>
        private Window? AboutFrontWindow()
        {
            Window? active = null, newest = null;
            foreach (Window window in Application.Current.Windows)
            {
                if (ReferenceEquals(window, this) || ReferenceEquals(window, _aboutHost) || !window.IsVisible) continue;
                bool ours = false;
                for (Window? owner = window.Owner; owner is not null; owner = owner.Owner)
                    if (ReferenceEquals(owner, this)) { ours = true; break; }
                if (!ours) continue;
                newest = window;
                if (window.IsActive) active = window;
            }
            return active ?? newest;
        }

        /// <summary>Moves the card into a borderless transparent window that covers the main
        /// window, owned by <paramref name="front"/> so it sits above it.</summary>
        private void HostAboutAbove(Window front)
        {
            if (_aboutHost is not null || AboutOverlay.Parent is not Panel home) return;
            _aboutHome = home;
            _aboutHomeIndex = home.Children.IndexOf(AboutOverlay);
            home.Children.Remove(AboutOverlay);

            // Cover the main window's frame, or the front window when the main one is minimized.
            FrameworkElement area = WindowState == WindowState.Minimized ? front : RootBorder;
            var source = PresentationSource.FromVisual(area);
            Point topLeft = area.PointToScreen(new Point(0, 0));
            if (source?.CompositionTarget is { } target) topLeft = target.TransformFromDevice.Transform(topLeft);
            double width = area.ActualWidth, height = area.ActualHeight;
            double radius = ReferenceEquals(area, RootBorder) ? RootBorder.CornerRadius.TopLeft : 0;

            var frame = new Grid { Clip = new RectangleGeometry(new Rect(0, 0, width, height), radius, radius) };
            frame.Children.Add(AboutOverlay);
            var host = new Window
            {
                Owner = front,
                Title = Title,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = topLeft.X,
                Top = topLeft.Y,
                Width = width,
                Height = height,
                FontFamily = FontFamily,
                Content = frame
            };
            // The card's styles and brushes live in the main window's resources.
            host.Resources.MergedDictionaries.Add(Resources);
            host.PreviewKeyDown += (_, e) =>
            {
                var (key, _, _, swallow) = TranslateShortcut(e.Key, Keyboard.Modifiers, e.SystemKey);
                if (!swallow && (key == Key.Escape || key == Key.F12))
                {
                    CloseAboutOverlay();
                    e.Handled = true;
                }
            };
            host.Closed += (_, _) => ReturnAboutHome(front);
            _aboutHost = host;
            host.Show();
        }

        /// <summary>Puts the card back in the main window after the host window closes.</summary>
        private void ReturnAboutHome(Window front)
        {
            if (AboutOverlay.Parent is Panel frame) frame.Children.Remove(AboutOverlay);
            if (_aboutHome is { } home)
                home.Children.Insert(Math.Clamp(_aboutHomeIndex, 0, home.Children.Count), AboutOverlay);
            AboutOverlay.BeginAnimation(UIElement.OpacityProperty, null);
            AboutOverlay.Opacity = 1;
            AboutOverlay.Visibility = Visibility.Collapsed;
            _aboutHost = null;
            _aboutHome = null;
            if (front.IsVisible) front.Activate();
        }

        // ---- Update restart -------------------------------------------------------------------

        bool IAboutUpdateRestart.IsDirty => Application.Current.Windows.OfType<MainWindow>().Any(window =>
            window.Viewer.IsDirtyRef || window.ViewerB.IsDirtyRef
                || window.AllSessions().Any(session => session.IsDirty));

                IUpdateRestartHandoff? IAboutUpdateRestart.PrepareUpdateRestart() => PrepareUpdateRestart();

        /// <summary>Also reached by the UI smoke run, which drives a real handoff.</summary>
        internal IUpdateRestartHandoff? PrepareUpdateRestart() => App.PrepareUpdateRestart(this);

        internal string? UpdateRestartActiveFile => _originalFile ?? _currentFile;
        internal bool UpdateRestartEditingEnabled { get => RootBorder.IsEnabled; set => RootBorder.IsEnabled = value; }

        internal bool TryCaptureUpdateRestart(out long revision, out string[] files)
        {
            revision = SessionEndingEditRevision;
            files = [];
            if (_busyCts is not null || _externalOpenInFlight != 0 || _pendingExternalPath is not null
                || OwnedWindows.Cast<Window>().Any(window => window.IsVisible)
                || Viewer.IsDirtyRef || ViewerB.IsDirtyRef) return false;
            var paths = new List<string>();
            foreach (var session in AllSessions())
            {
                if (session.IsDirty || session.LoadPending) return false;
                if (session.Doc is null && string.IsNullOrEmpty(session.CurrentFile)
                    && string.IsNullOrEmpty(session.DeferredPath)) continue;
                string? path = session.OriginalFile ?? session.DeferredPath;
                if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path)) return false;
                path = System.IO.Path.GetFullPath(path);
                if (App.IsCleanupOwnedPath(path)) return false;
                paths.Add(path);
            }
            files = paths.ToArray();
            return true;
        }

        // ---- Card content that never varies with the signature or the update state ------------

        private void BuildAboutStaticContent()
        {
            // The family film-grain texture on the About card, repainted with the theme.
            Controls.GrainTexture.Track(AboutGrainBrush);

            BuildWordmark(AboutLogoBlock, AboutLogoShadowBlock);
            BuildAboutTagline();
        }

        // The What's new card (Shell/WhatsNew.cs) carries the About card's grain; its header is the title bar's.
        partial void ShowingWhatsNew()
        {
            if (ShortcutsOverlay.Visibility == Visibility.Visible) FadeOverlayOut(ShortcutsOverlay);
            Controls.GrainTexture.Track(WhatsNewGrainBrush);
            ClipWhatsNewNotes();
        }

        private void WhatsNewNotesClip_SizeChanged(object sender, SizeChangedEventArgs e) => ClipWhatsNewNotes();

        /// <summary>A Border's CornerRadius never clips its children, and the notes' ScrollViewer
        /// clips square, so lines scrolling under the top and bottom edges painted over the rounded
        /// corners. The clip follows the pane's inner edge: the theme's corner less the 1px border.</summary>
        private void ClipWhatsNewNotes()
        {
            double width = WhatsNewNotesClip.ActualWidth, height = WhatsNewNotesClip.ActualHeight;
            if (width <= 0 || height <= 0) return;
            double corner = TryFindResource("ControlCornerRadius") is CornerRadius r ? r.TopLeft : 0;
            double radius = Math.Max(0, corner - 1);
            WhatsNewNotesClip.Clip = new RectangleGeometry(new Rect(0, 0, width, height), radius, radius);
        }

        /// <summary>"Killer" in the text color, "PDF" in the brand green, the pair clickable. The
        /// About card and the What's new card carry the same wordmark.</summary>
        private void BuildWordmark(TextBlock logo, TextBlock shadow)
        {
            logo.Inlines.Clear();
            var hl = new Hyperlink { TextDecorations = null };
            hl.Inlines.Add(new Run("Killer")
            {
                FontFamily = UiKit.WordmarkFont,
                FontSize   = 16.43,
                FontWeight = FontWeights.Normal,
                Foreground = Res("TextBrush")
            });
            hl.Inlines.Add(new Run("PDF")
            {
                FontFamily = UiKit.WordmarkFontPdf,
                FontSize   = 31.83,
                FontWeight = FontWeights.Normal,
                Foreground = Res("AccentLogo")
            });
            hl.Click += (_, _) => WebLink.Open("https://killerpdf.net");
            logo.Inlines.Add(hl);

            // The shadow copy mirrors the real runs exactly (sizes, weights, fonts), so the blur
            // sits directly behind each letter instead of smearing above the word - a flat
            // single-size copy had different line metrics than the two-run wordmark.
            shadow.Inlines.Clear();
            var shadowBrush = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromArgb(0xB0, 0, 0, 0));
            shadowBrush.Freeze();
            shadow.Inlines.Add(new Run("Killer")
            {
                FontFamily = UiKit.WordmarkFont,
                FontSize   = 16.43,
                FontWeight = FontWeights.Normal,
                Foreground = shadowBrush
            });
            shadow.Inlines.Add(new Run("PDF")
            {
                FontFamily = UiKit.WordmarkFontPdf,
                FontSize   = 31.83,
                FontWeight = FontWeights.Normal,
                Foreground = shadowBrush
            });
        }

        /// <summary>
        /// Localized tagline. {0} is the (untranslated) brand, so splitting on the placeholder keeps
        /// "Killer Tools" a styled, clickable link while the rest translates and the brand can sit
        /// anywhere in the sentence the language needs it. A "\n" in the localized string marks the
        /// line break; the second line carries the license and the brand.
        /// </summary>
        private void BuildAboutTagline()
        {
            AboutTaglineBlock.Inlines.Clear();
            var dim  = Res("MutedTextBrush");
            var text = Loc("Str_Tagline");

            int brand  = text.IndexOf("{0}", System.StringComparison.Ordinal);
            string pre = brand >= 0 ? text[..brand]        : text;
            string suf = brand >= 0 ? text[(brand + 3)..]  : "";

            void AddText(string s)
            {
                var lines = s.Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    if (i > 0) AboutTaglineBlock.Inlines.Add(new LineBreak());
                    AboutTaglineBlock.Inlines.Add(new Run(lines[i]) { Foreground = dim });
                }
            }

            AddText(pre);
            AboutTaglineBlock.Inlines.Add(
                AccentLink("Killer Tools", () => WebLink.Open("https://killertools.net")));
            AddText(suf);
        }

        private void AboutHeader_SizeChanged(object sender, SizeChangedEventArgs e) => SizeAboutIcon();

        /// <summary>
        /// The About icon spans the wordmark and tagline: its bottom on the tagline's last row, its
        /// top 4px below the top of "PDF". A wider icon narrows the text, which can wrap to more
        /// lines and ask for a bigger icon again, so the size steps up from zero to the first size
        /// that fits and stops there. It waits for the info panel's real width, because the card's
        /// first layout pass has none and wraps the tagline one word per line.
        /// The 15px gap runs from the drawn page to the text: kp-icon's artwork leaves 12.5% of its
        /// square empty on the right at every frame size, so the margin gives that much back.
        /// </summary>
        private void SizeAboutIcon()
        {
            double width = AboutInfoGrid.ActualWidth;
            if (width <= 0) return;
            const double gap = 15, inset = 4, artRightPad = 0.125;
            static double IconMargin(double s) => gap - System.Math.Round(s * artRightPad);
            double size = 0;
            for (int i = 0; i < 12; i++)
            {
                AboutHeaderText.Measure(new Size(System.Math.Max(0, width - IconMargin(size) - size), double.PositiveInfinity));
                double next = System.Math.Max(0, AboutHeaderText.DesiredSize.Height - inset);
                bool settled = System.Math.Abs(next - size) < 0.5;
                size = next;
                if (settled) break;
            }
            AboutHeaderText.InvalidateMeasure();
            if (System.Math.Abs(AboutIcon.Width - size) < 0.5) return;
            AboutIcon.Width = size;
            AboutIcon.Height = size;
            AboutIcon.Margin = new Thickness(0, 0, IconMargin(size), 0);
        }

        // ---- Small helpers -------------------------------------------------------------------

        private Brush Res(string key) => (Brush)FindResource(key);

        /// <summary>An accent-colored hyperlink with no underline - the family's one treatment for
        /// "this is clickable" on a card.</summary>
        private Hyperlink AccentLink(string text, System.Action onClick)
        {
            var hl = new Hyperlink(new Run(text))
            {
                Foreground      = Res("PrimaryBrush"),
                TextDecorations = null
            };
            hl.Click += (_, _) => onClick();
            return hl;
        }

        /// <summary>
        /// "Clear all Data" footer link: wipes settings, downloaded OCR language packs, and temp
        /// files after an explicit confirmation. Destructive, so it always warns first; the user's
        /// PDFs are untouched.
        /// </summary>
        private void AboutClearData_Click(object sender, RoutedEventArgs e)
        {
            Window owner = _aboutHost ?? this;
            var res = KillerDialog.Show(owner,
                Loc("Str_ClearDataConfirm"),
                Loc("Str_ClearAllData"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (res != MessageBoxResult.Yes) return;

            if (!App.ClearAllData())
            {
                KillerDialog.Show(owner, Loc("Str_Watch_Busy"), Loc("Str_ClearAllData"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            SetStatus(Loc("Str_St_DataCleared"));
            KillerDialog.Show(owner,
                Loc("Str_ClearDataDone"),
                Loc("Str_ClearAllData"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
