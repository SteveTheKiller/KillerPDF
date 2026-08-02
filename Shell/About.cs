using System.Diagnostics;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using KillerPDF.Features;

namespace KillerPDF
{
    /// <summary>
    /// The About overlay's window half: the IAboutHost implementation that maps the controller's
    /// values onto the named XAML elements, the inline construction the card needs, and the click
    /// handlers. All the logic - signature, hashing, update check, self-update - lives in
    /// <see cref="AboutController"/>.
    ///
    /// NOTE: this stays "namespace KillerPDF" rather than KillerPDF.Shell, because it is a partial
    /// of MainWindow and every partial of a class must share one namespace. It moves to
    /// KillerPDF.Shell when MainWindow itself does.
    /// </summary>
    public partial class MainWindow : IAboutHost
    {
        private AboutController? _aboutController;
        private AboutController About => _aboutController ??= new AboutController(this);

        private void VersionLabel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            ShowAboutOverlay();
        }

        private void ShowAboutOverlay() => About.Show();

        // ---- IShellServices ------------------------------------------------------------------

        Window IShellServices.Window => this;

        // MainWindow's own Loc and SetStatus are private, and a private member cannot implement an
        // interface. Forwarding explicitly satisfies IShellServices without widening either of them
        // to public, which would be a change nobody asked for.
        string IShellServices.Loc(string key) => Loc(key);
        void IShellServices.SetStatus(string text) => SetStatus(text);

        // ---- IAboutHost ----------------------------------------------------------------------

        string IAboutHost.Publisher   { set => AboutPublisherBlock.Text  = value; }
        string IAboutHost.Thumbprint  { set => AboutThumbprintBlock.Text = value; }
        string IAboutHost.Sha256      { set => AboutSha256Block.Text     = value; }
        string IAboutHost.ReleaseDate { set => AboutReleaseDateBlock.Text = value; }
        string IAboutHost.UpdateText  { set => AboutUpdateText.Text      = value; }

        bool IAboutHost.UpdateVisible
        {
            set => AboutUpdateButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        }

        bool IAboutHost.UpdateEnabled { set => AboutUpdateButton.IsEnabled = value; }

        bool IAboutHost.IsDirty => _isDirty;

        string? IAboutHost.FileToReopen => _originalFile ?? _currentFile;

        /// <summary>Version line, as a hyperlink through to that release tag.</summary>
        void IAboutHost.SetVersion(string version)
        {
            AboutVersionBlock.Inlines.Clear();
            AboutVersionBlock.Inlines.Add(AccentLink($"v{version}", () => About.OpenReleaseNotes()));
        }

        /// <summary>The AKA line. Null hides it entirely.</summary>
        void IAboutHost.SetAlias(string? alias)
        {
            AboutAkaBlock.Visibility = alias is null ? Visibility.Collapsed : Visibility.Visible;
            if (alias is null) return;

            AboutAkaBlock.Inlines.Clear();
            AboutAkaBlock.Inlines.Add(new Run("AKA ") { Foreground = Res("MutedTextBrush") });
            var hl = AccentLink(alias, () => AboutController.OpenUrl("https://thekiller.net"));
            hl.ToolTip = "thekiller.net";
            AboutAkaBlock.Inlines.Add(hl);
        }

        /// <summary>Dismisses any other full-window overlay, then fades the card in. The overlays
        /// are mutually exclusive rather than stacking on top of one another.</summary>
        void IAboutHost.ShowCard()
        {
            if (ShortcutOverlay.Visibility == Visibility.Visible) FadeOverlayOut(ShortcutOverlay);

            BuildAboutStaticContent();
            FadeOverlayIn(AboutOverlay);
        }

        // ---- Card content that never varies with the signature or the update state ------------

        private void BuildAboutStaticContent()
        {
            // Reuse the main window's film-grain texture on the About card.
            if (GrainBrush?.ImageSource != null) AboutGrainBrush.ImageSource = GrainBrush.ImageSource;

            BuildAboutWordmark();
            BuildAboutTagline();
        }

        /// <summary>"Killer" in the text colour, "PDF" in the brand green, the pair clickable.</summary>
        private void BuildAboutWordmark()
        {
            AboutLogoBlock.Inlines.Clear();
            var hl = new Hyperlink { TextDecorations = null };
            hl.Inlines.Add(new Run("Killer")
            {
                FontSize   = 21,
                FontWeight = FontWeights.Normal,
                Foreground = Res("TextBrush")
            });
            hl.Inlines.Add(new Run("PDF")
            {
                FontFamily = UiKit.WordmarkFontPdf,
                FontSize   = 27.3,
                Foreground = Res("AccentLogo")
            });
            hl.Click += (_, _) => AboutController.OpenUrl("https://killerpdf.net");
            AboutLogoBlock.Inlines.Add(hl);
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
                AccentLink("Killer Tools", () => AboutController.OpenUrl("https://killertools.net")));
            AddText(suf);
        }

        // ---- Small helpers -------------------------------------------------------------------

        private Brush Res(string key) => (Brush)FindResource(key);

        /// <summary>An accent-coloured hyperlink with no underline - the family's one treatment for
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

        // ---- Handlers ------------------------------------------------------------------------

        // Click the dim backdrop to dismiss; a click on the card itself is swallowed.
        private void AboutOverlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
            => FadeOverlayOut(AboutOverlay);

        private void AboutOverlayCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
            => e.Handled = true;

        private void AboutOverlayClose_Click(object sender, RoutedEventArgs e)
            => FadeOverlayOut(AboutOverlay);

        private void AboutUpdateButton_Click(object sender, RoutedEventArgs e) => About.Update();

        /// <summary>
        /// "Clear all Data" footer link: wipes settings, downloaded OCR language packs, and temp
        /// files after an explicit confirmation. Destructive, so it always warns first; the user's
        /// PDFs are untouched.
        /// </summary>
        private void AboutClearData_Click(object sender, RoutedEventArgs e)
        {
            var res = KillerDialog.Show(this,
                "This will delete all saved settings, downloaded OCR language packs, and temporary files.\n\n" +
                "Your PDF files are not affected. Continue?",
                "Clear all Data", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (res != MessageBoxResult.Yes) return;

            App.ClearAllData();
            SetStatus(Loc("Str_St_DataCleared"));
            KillerDialog.Show(this,
                "Settings, language packs, and temp files were cleared.\n\n" +
                "Restart KillerPDF to finish clearing any files still in use this session.",
                "Clear all Data", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
