using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KillerPDF.Services;

namespace KillerPDF.Shell
{
    /// <summary>
    /// The What's new card: once after an update, the new version's changelog notes in a themed
    /// overlay. The markup (WhatsNewOverlay and its named parts) is the README snippet each app
    /// pastes beside its About overlay; the notes come from the embedded CHANGELOG.md through
    /// Services/ReleaseNotes.cs. Only the card's own words are translated, the notes stay English.
    ///
    /// The app calls <see cref="ShowWhatsNewAfterUpdate"/> once its startup prompts are done (the
    /// unlock prompt, recovery offers). It runs once per process, stays quiet on a fresh install,
    /// and records the version only when the card actually shows or there is nothing to show.
    /// </summary>
    public partial class MainWindow
    {
        internal const string WhatsNewSetting = "WhatsNewSeen";

        /// <summary>"0" turns the card off; anything else, or nothing saved, leaves it on.</summary>
        internal const string WhatsNewEnabledSetting = "ShowWhatsNew";

        private bool _whatsNewChecked;

        private static bool WhatsNewEnabled => ThemeManager.GetSetting(WhatsNewEnabledSetting) != "0";

        /// <summary>The card's own box and the About card's box are one setting; both follow it.</summary>
        private void SyncWhatsNewChecks()
        {
            bool on = WhatsNewEnabled;
            WhatsNewShowCheck.IsChecked = on;
            AboutWhatsNewCheck.IsChecked = on;
        }

        private void SetWhatsNewEnabled(bool on)
        {
            ThemeManager.SetSetting(WhatsNewEnabledSetting, on ? "1" : "0");
            WhatsNewRemembered();
            SyncWhatsNewChecks();
        }

        private void WhatsNewShowCheck_Click(object sender, RoutedEventArgs e) => SetWhatsNewEnabled(WhatsNewShowCheck.IsChecked == true);
        private void AboutWhatsNewCheck_Click(object sender, RoutedEventArgs e) => SetWhatsNewEnabled(AboutWhatsNewCheck.IsChecked == true);

        /// <summary>App hook: skip the card this run (demo, capture and self-test modes, task windows).
        /// A skipped run records nothing, so the card still shows on the next normal start.</summary>
        partial void SkipWhatsNew(ref bool skip);

        /// <summary>App hook: the seen version or the on/off choice was just written. An app whose
        /// settings only save on close (KillerBench) flushes them here.</summary>
        partial void WhatsNewRemembered();

        /// <summary>Shows the card when this version is newer than the last one the user saw.</summary>
        private void ShowWhatsNewAfterUpdate()
        {
            if (_whatsNewChecked) return;
            SyncWhatsNewChecks();
            bool skip = false;
            SkipWhatsNew(ref skip);
            if (skip) return;
            _whatsNewChecked = true;

            string current = ReleaseNotes.Normalize(AppInfo.Version);
            if (current.Length == 0) return;
            string? seen = ThemeManager.GetSetting(WhatsNewSetting);
            // A fresh install has nothing new to report; start counting from here.
            if (string.IsNullOrWhiteSpace(seen) && LocaleManager.IsFirstRun) { RememberWhatsNew(current); return; }
            if (!ReleaseNotes.IsNewer(current, seen)) return;
            // Turned off: the version still counts as seen, so turning it back on later never
            // brings up an old changelog.
            if (!WhatsNewEnabled) { RememberWhatsNew(current); return; }

            ReleaseNotes? notes = ReleaseNotes.LoadEmbedded(typeof(MainWindow).Assembly, current);
            if (notes != null) ShowWhatsNew(notes);
            RememberWhatsNew(current);
        }

        private void RememberWhatsNew(string version)
        {
            ThemeManager.SetSetting(WhatsNewSetting, version);
            WhatsNewRemembered();
        }

        private void ShowWhatsNew(ReleaseNotes notes)
        {
            WhatsNewTitle.Text = string.Format(CultureInfo.CurrentCulture, WhatsNewText("Str_WhatsNew_Title","What's new in {0}"), notes.Version);
            WhatsNewList.Children.Clear();
            if (notes.Summary.Length > 0)
                WhatsNewList.Children.Add(NoteText(notes.Summary, "TextBrush", new Thickness(0, 0, 0, 10)));
            bool first = notes.Summary.Length == 0;
            foreach (ReleaseNotes.Section section in notes.Sections)
            {
                string? key = ReleaseNotes.HeadingKey(section.Heading);
                string heading = key == null ? section.Heading : WhatsNewText(key, section.Heading);
                if (heading.Length > 0)
                {
                    var h = NoteText(heading.ToUpper(CultureInfo.CurrentUICulture), "MutedTextBrush", new Thickness(0, first ? 0 : 8, 0, 4));
                    h.FontSize = 10;
                    h.FontWeight = FontWeights.SemiBold;
                    WhatsNewList.Children.Add(h);
                }
                first = false;
                foreach (string item in section.Items)
                {
                    var row = new Grid { Margin = new Thickness(0, 0, 0, 5) };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
                    row.ColumnDefinitions.Add(new ColumnDefinition());
                    row.Children.Add(NoteText("•", "PrimaryBrush", new Thickness(0)));
                    var line = NoteText(item, "TextBrush", new Thickness(0));
                    Grid.SetColumn(line, 1);
                    row.Children.Add(line);
                    WhatsNewList.Children.Add(row);
                }
            }
            WhatsNewScroller.ScrollToTop();
            ShowingWhatsNew();
            FadeOverlayIn(WhatsNewOverlay);
        }

        /// <summary>App hook: fill the card's header (the wordmark, the grain brush) and step other
        /// overlays aside just before the card fades in.</summary>
        partial void ShowingWhatsNew();

        private TextBlock NoteText(string text, string brushKey, Thickness margin)
        {
            var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12, LineHeight = 17, Margin = margin };
            t.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
            t.SetResourceReference(TextBlock.FontFamilyProperty, "UiFont");
            return t;
        }

        private string WhatsNewText(string key, string fallback) => TryFindResource(key) as string ?? fallback;

        /// <summary>For the app's key handler: true while the card shows, so the app's shortcuts
        /// stay off behind it. Esc closes it; Tab, Space and the scroll keys reach the card.</summary>
        private bool WhatsNewOwnsKey(KeyEventArgs e)
        {
            if (WhatsNewOverlay.Visibility != Visibility.Visible) return false;
            if (e.Key == Key.Escape) { CloseWhatsNew(); e.Handled = true; }
            return true;
        }

        private void CloseWhatsNew() => FadeOverlayOut(WhatsNewOverlay);

        // Click the dim backdrop to dismiss; a click on the card itself is swallowed.
        private void WhatsNewOverlay_Click(object sender, MouseButtonEventArgs e) => CloseWhatsNew();
        private void WhatsNewCard_Click(object sender, MouseButtonEventArgs e) => e.Handled = true;
        private void WhatsNewClose_Click(object sender, RoutedEventArgs e) => CloseWhatsNew();
    }
}
