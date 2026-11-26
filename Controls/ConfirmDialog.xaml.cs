using System.Windows;
using System.Windows.Input;


namespace KillerPDF.Controls
{
    // The family confirm dialog (KillerShell's, generalized): a heading, an optional detail or
    // bullet block, up to two optional checkboxes, an optional extra answer, and installer and
    // notice modes. Each app writes its own wording at the call site.
    public partial class ConfirmDialog : Window
    {
        public bool Confirmed { get; private set; }

        /// <summary>The id of the extra button pressed (<see cref="AddChoice"/>), or null.</summary>
        public string? Choice { get; private set; }

        /// <summary>State of the first optional checkbox. Meaningless unless check1Label was passed.</summary>
        public bool Check1Checked => Check1.IsChecked == true;

        /// <summary>State of the second optional checkbox. Meaningless unless check2Label was passed.</summary>
        public bool Check2Checked => Check2.IsChecked == true;

        /// <summary>Optional app hook, run once the window has a handle (KillerShell draws its theme border here).</summary>
        partial void OnDialogSourceInitialized();

        public ConfirmDialog()
        {
            InitializeComponent();
            TaskbarIdentity.Track(this);
            if (!DialogChrome.SetTitleIcon(TitleIcon))
                TitleIcon.Visibility = Visibility.Collapsed;
            ApplyAppName();
            Loaded += (_, _) => Anim.FadeIn(RootBorder);
            Loaded += (_, _) => ApplyNotice();
            SourceInitialized += (_, _) =>
            {
                // No owner means no app window to center on (setup, or a prompt before the main
                // window): center on the screen and show in the taskbar so it can't get lost.
                if (Owner == null)
                {
                    WindowStartupLocation = WindowStartupLocation.CenterScreen;
                    ShowInTaskbar = true;
                }
                OnDialogSourceInitialized();
            };
        }

        // Configurable variant. detail may contain newlines for multiple lines or a bullet list.
        // check1Label / check2Label opt in to up to two checkboxes, for a confirm that also carries
        // a choice; read them back via Check1Checked / Check2Checked after ShowDialog returns.
        // installer: centered on screen and shown in the taskbar, for setup before any app window
        // exists. notice: OK only, no Cancel. The dialog persists nothing itself.
        public ConfirmDialog(string heading, string detail, string confirmText, string? cancelText = null,
                             string? check1Label = null, bool check1Initial = false,
                             string? check2Label = null, bool check2Initial = false,
                             bool installer = false, bool notice = false, string? check2Note = null)
            : this()
        {
            if (installer)
            {
                Resources.MergedDictionaries.Add(Services.ThemeManager.CreateInstallerTheme());
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
                ShowInTaskbar = true;
            }
            if (notice) CancelButton.Visibility = Visibility.Collapsed;

            HeadingText.Text = heading;
            DetailText.Text = detail;
            DetailText.Visibility = string.IsNullOrEmpty(detail) ? Visibility.Collapsed : Visibility.Visible;
            OkButton.Content = confirmText;
            // null keeps the translated Cancel; an empty string hides it, the same as notice.
            if (cancelText != null) CancelButton.Content = cancelText;
            if (cancelText == string.Empty) CancelButton.Visibility = Visibility.Collapsed;
            // With no Cancel on screen, Esc answers the one button that is.
            if (CancelButton.Visibility != Visibility.Visible) OkButton.IsCancel = true;

            if (!string.IsNullOrEmpty(check1Label))
            {
                Check1.Content = new System.Windows.Controls.TextBlock { Text = check1Label, TextWrapping = TextWrapping.Wrap };
                Check1.IsChecked = check1Initial;
                Check1.Visibility = Visibility.Visible;
            }
            if (!string.IsNullOrEmpty(check2Label))
            {
                Check2.Content = new System.Windows.Controls.TextBlock { Text = check2Label, TextWrapping = TextWrapping.Wrap };
                Check2.IsChecked = check2Initial;
                Check2.Visibility = Visibility.Visible;
            }
            // A note under the second box says what ticking it does, so it shows before the tick.
            if (!string.IsNullOrEmpty(check2Note))
            {
                Check2Note.Text = check2Note;
                Check2Note.Visibility = Visibility.Visible;
            }
        }

        /// <summary>A one-button notice, OK only.</summary>
        internal static void ShowNotice(string message, Window? owner = null)
        {
            var dialog = new ConfirmDialog(message, string.Empty,
                Application.Current.TryFindResource("Str_Sys_Close") as string ?? "Close", notice: true);
            if (owner != null) dialog.Owner = owner;
            dialog.ShowDialog();
        }

        /// <summary>Pre-ticked and locked, with an explanatory note underneath. Used when the
        /// choice is already made for the user, for example an app already installed machine-wide,
        /// where unticking the all-users box would leave two copies behind. The note replaces
        /// any check2Note.</summary>
        public void LockCheck2(string? note = null)
        {
            Check2.IsChecked = true;
            Check2.IsEnabled = false;
            Check2.Visibility = Visibility.Visible;
            if (!string.IsNullOrEmpty(note))
            {
                Check2Note.Text = note;
                Check2Note.Visibility = Visibility.Visible;
            }
        }

        /// <summary>An extra answer between Cancel and OK, styled like Cancel. Pressing it closes
        /// the dialog unconfirmed with <see cref="Choice"/> set to <paramref name="id"/>.</summary>
        public void AddChoice(string id, string label)
        {
            var button = new System.Windows.Controls.Button
            {
                Content = label,
                Style = (Style)FindResource("SurfaceButton"),
                Height = 28,
                MinWidth = 80,
                Padding = new Thickness(12, 2, 12, 2),
                Margin = new Thickness(0, 0, 8, 0),
            };
            button.Click += (_, _) => { Choice = id; Confirmed = false; Close(); };
            ButtonRow.Children.Insert(ButtonRow.Children.IndexOf(OkButton), button);
        }

        /// <summary>Enter and Esc both answer Cancel, for a prompt whose OK replaces something.</summary>
        public void DefaultToCancel()
        {
            OkButton.IsDefault = false;
            CancelButton.IsDefault = true;
            CancelButton.IsCancel = true;
            Loaded += (_, _) => CancelButton.Focus();
        }

        // The wordmark and plain caption carry the app's own name: "Killer" plus the accent word
        // for a KillerX app, "The " plus the name otherwise (The Killendar).
        private void ApplyAppName()
        {
            string name = Services.AppInfo.DisplayName;
            bool killer = name.StartsWith("Killer") && name.Length > "Killer".Length;
            string lead = killer ? "Killer" : "The ";
            string accent = killer ? name.Substring("Killer".Length) : name;
            foreach (var run in new[] { WordLead0, WordLead1, WordLead2 }) run.Text = lead;
            foreach (var run in new[] { WordAccent0, WordAccent1, WordAccent2 }) run.Text = accent;
            PlainTitle.Text = name;
            Title = name;
        }

        // The family setup dialog's notice ring: an amber "!" on a confirmation and the accent "i" on a
        // notice with a single button. The amber darkens on a light card so it keeps its contrast.
        private void ApplyNotice()
        {
            FitHeading();
            FitCheckLabels();
            bool confirmation = CancelButton.Visibility == Visibility.Visible;
            System.Windows.Media.Brush brush;
            if (confirmation)
            {
                var card = TryFindResource("BackgroundBrush") as System.Windows.Media.SolidColorBrush;
                bool light = card != null && card.Color.R * 0.299 + card.Color.G * 0.587 + card.Color.B * 0.114 > 140;
                brush = new System.Windows.Media.SolidColorBrush(light
                    ? System.Windows.Media.Color.FromRgb(176, 112, 0)
                    : System.Windows.Media.Color.FromRgb(255, 190, 80));
            }
            else
            {
                brush = TryFindResource("PrimaryBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Gray;
            }
            NoticeGlyph.Text = confirmation ? "!" : "i";
            NoticeGlyph.Foreground = brush;
            NoticeRing.BorderBrush = brush;
        }

        // Long checkbox labels wrap inside the dialog body instead of running past its edge. The
        // dialog sizes to its content, so the body's cap is the limit, not its current width: a
        // checkbox that is itself the widest row would otherwise wrap its own label.
        private void FitCheckLabels()
        {
            foreach (var box in new[] { Check1, Check2 })
                if (box.Content is System.Windows.Controls.TextBlock label && box.Parent is FrameworkElement body)
                {
                    double room = double.IsInfinity(body.MaxWidth) ? body.ActualWidth : body.MaxWidth;
                    if (room > 0) label.MaxWidth = System.Math.Max(80, room - box.Margin.Left - box.Margin.Right - 40);
                }
        }

        // A heading is one short line. Text after its first line break moves into the detail, and
        // a first line too long for a heading stays body-sized instead of shouting.
        private void FitHeading()
        {
            string text = HeadingText.Text ?? "";
            int lineBreak = text.IndexOf('\n');
            if (lineBreak >= 0)
            {
                string rest = text.Substring(lineBreak + 1).Trim();
                HeadingText.Text = text.Substring(0, lineBreak).TrimEnd('\r');
                if (rest.Length > 0)
                {
                    string existing = DetailText.Visibility == Visibility.Visible ? DetailText.Text : "";
                    DetailText.Text = string.IsNullOrEmpty(existing) ? rest : rest + "\n\n" + existing;
                    DetailText.Visibility = Visibility.Visible;
                }
            }
            string line = HeadingText.Text ?? "";
            if (line.Length > 90)
            {
                HeadingText.ClearValue(System.Windows.Controls.TextBlock.FontFamilyProperty);
                HeadingText.ClearValue(System.Windows.Media.TextOptions.TextFormattingModeProperty);
                HeadingText.FontSize = 13;
            }
            else if (NamesAFile(line) || !LacquerCovers(line))
            {
                // A file name is never hand-lettered, and a line Lacquer cannot fully draw (Chinese,
                // Cyrillic) goes whole into the interface font instead of mixing two faces.
                // The 18 px size and the 4 px lift are tuned for Lacquer's small, low-sitting letters;
                // in the interface font they make a line that is too big and rides above the ring.
                // 14 px with the first line centred on the 28 px ring.
                HeadingText.ClearValue(System.Windows.Controls.TextBlock.FontFamilyProperty);
                HeadingText.ClearValue(System.Windows.Media.TextOptions.TextFormattingModeProperty);
                HeadingText.FontSize = 14;
                HeadingText.Margin = new Thickness(0, 4, 0, 4);
            }
        }

        // A word ending in a dot and a short extension, like "report.pdf" or "notes.txt".
        private static readonly System.Text.RegularExpressions.Regex FileNamePattern =
            new(@"\S\.[A-Za-z][A-Za-z0-9]{0,7}(?=$|[\s?？!！,，。:：;；""”'’)）»])");

        internal static bool NamesAFile(string text) => FileNamePattern.IsMatch(text);

        private bool LacquerCovers(string text)
        {
            if (TryFindResource("WordmarkAccentFont") is not System.Windows.Media.FontFamily lacquer
                || !new System.Windows.Media.Typeface(lacquer, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal)
                    .TryGetGlyphTypeface(out System.Windows.Media.GlyphTypeface face)) return false;
            foreach (char c in text)
                if (!char.IsWhiteSpace(c) && !face.CharacterToGlyphMap.ContainsKey(c)) return false;
            return true;
        }

        private void OK_Click(object sender, RoutedEventArgs e)
        {
            Confirmed = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            Confirmed = false;
            Close();
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            try { DragMove(); }
            catch (System.InvalidOperationException) { }   // the button was already up
        }
    }
}
