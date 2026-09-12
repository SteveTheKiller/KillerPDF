using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace KillerPDF
{
    // ============================================================
    // Keyboard-shortcuts overlay: the LIST view.
    //
    // The overlay card (ShortcutOverlay in MainWindow.xaml) is generated from ShortcutTable.KsAll
    // rather than hand-authored row by row, so adding or changing a shortcut is a one-line edit
    // there and can't drift out of sync with a parallel block of XAML - or with the keyboard map,
    // which is built from the same array. The two empty hosts ShortcutLeftColumn /
    // ShortcutRightColumn are filled by BuildShortcutsOverlay(), called once from the constructor.
    //
    // Keys are literal text (shown in Consolas, like a real keycap); labels are Str_* resource keys so
    // they stay localized. Everything is wired with SetResourceReference so both the theme colors and
    // the active locale keep updating live, exactly as the old DynamicResource markup did.
    // ============================================================
    public partial class MainWindow
    {
        // One row: the literal key text and the resource key for its translated description.
        private readonly record struct KsRow(string Keys, string LabelKey);

        // A titled group of rows. TitleKey is a Str_* resource key rendered as the subheader,
        // colored by the section's KsCat* theme brush - the family neon set the keyboard map
        // already uses (KillerShell's colored categories, taken as the reference).
        private sealed class KsSection
        {
            public string TitleKey = "";
            public string Cat = "";   // "" falls back to PrimaryBrush
            public KsRow[] Rows = [];
        }

        /// <summary>The list's view of the table: sections in KsGroups order, rows in declaration
        /// order. Caps are ignored here; they only matter to the keyboard map.</summary>
        private static KsSection[] KsColumn(bool right) =>
            [.. ShortcutTable.KsGroups.Where(g => g.Right == right)
                .Select(g => new KsSection
                {
                    TitleKey = g.TitleKey,
                    Cat = g.Cat,
                    Rows = [.. ShortcutTable.KsAll.Where(b => b.Cat == g.Cat).Select(b => new KsRow(b.Keys, b.LabelKey))],
                })];

        private static readonly KsSection[] KsLeftColumn  = KsColumn(right: false);
        private static readonly KsSection[] KsRightColumn = KsColumn(right: true);

        // #153: the zoom keys are spelled differently per keyboard layout - "=" is a plain keypress
        // on US but needs Shift on German, where "+" is the unshifted one instead. The bindings
        // accept whichever key TYPES the character (Services/KeyLayout.cs), so these labels have to
        // follow suit, or the overlay advertises a chord that does not work on that machine.
        // %zin% / %zout% are substituted here; everything else passes through untouched. The token
        // markers avoid braces on purpose - a brace in a char or string literal makes a plain
        // brace-balance check on this file report a false mismatch.
        private static string ResolveKeyLabel(string keys)
            => keys.IndexOf('%') < 0
                ? keys
                : keys.Replace("%zin%", Services.KeyLayout.ZoomInChar())
                      .Replace("%zout%", Services.KeyLayout.ZoomOutChar());

        // #230: the key column used to be raw English, so Shift, Delete, Home, End and the wheel
        // gestures never reached a translator. They are tokens now (ShortcutTable.KeyTokens).
        //
        // Built as Runs rather than one resolved string on purpose. The description beside it uses
        // SetResourceReference and so follows a language switch live; a string composed once in the
        // constructor would not, and the overlay is built exactly once (MainWindow ctor). Giving
        // each token its own Run with its own resource reference keeps the whole row live, and the
        // literal parts - "Ctrl+", "/", F-numbers, letters - stay plain Runs.
        //
        // %zin% / %zout% are NOT tokens here: they depend on the keyboard layout rather than the
        // locale, so ResolveKeyLabel substitutes them into the literal text first.
        private static void FillKeyInlines(TextBlock target, string keys)
        {
            target.Inlines.Clear();
            string text = ResolveKeyLabel(keys);

            int pos = 0;
            while (pos < text.Length)
            {
                int start = text.IndexOf('%', pos);
                if (start < 0) break;
                int end = text.IndexOf('%', start + 1);
                if (end < 0) break;

                string token = text.Substring(start, end - start + 1);
                string? resourceKey = ShortcutTable.KeyTokens
                    .Where(t => t.Token == token)
                    .Select(t => t.Key)
                    .FirstOrDefault();
                if (resourceKey == null) { pos = start + 1; continue; }   // unknown, leave as text

                if (start > pos) target.Inlines.Add(new System.Windows.Documents.Run(text[pos..start]));
                var run = new System.Windows.Documents.Run();
                run.SetResourceReference(System.Windows.Documents.Run.TextProperty, resourceKey);
                target.Inlines.Add(run);
                pos = end + 1;
            }
            if (pos < text.Length) target.Inlines.Add(new System.Windows.Documents.Run(text[pos..]));
        }

        // Fill the two overlay columns from the tables above. Built once from the constructor
        // and rebuilt on every open (hosts are cleared first), so remapped chords (#190) and
        // theme/language changes render fresh. The SetResourceReference calls keep every string
        // and color live across theme + language changes.
        private void BuildShortcutsOverlay()
        {
            BuildShortcutsColumn(ShortcutLeftColumn,  KsLeftColumn);
            BuildShortcutsColumn(ShortcutRightColumn, KsRightColumn);
        }

        private static void BuildShortcutsColumn(StackPanel host, KsSection[] sections)
        {
            host.Children.Clear();
            // The key column sizes to its widest entry instead of a hardcoded width, and every row
            // in this column shares that measurement so they stay aligned. Translated key names are
            // longer than English - Shift becomes Umschalt, Enter becomes Eingabe - and a fixed
            // 132px clipped them (#230). Each column host is its own scope, so the two columns size
            // independently.
            Grid.SetIsSharedSizeScope(host, true);
            for (int s = 0; s < sections.Length; s++)
            {
                var section = sections[s];

                // Section subheader: accent, semibold, 12px top gap except for the first section.
                var header = new TextBlock
                {
                    FontFamily = new FontFamily("Segoe UI, Microsoft JhengHei UI, Nirmala UI"),
                    FontSize   = 11,
                    FontWeight = FontWeights.SemiBold,
                    Margin     = new Thickness(0, s == 0 ? 0 : 12, 0, 4),
                };
                header.SetResourceReference(TextBlock.TextProperty, section.TitleKey);
                // Category color, the same KsCat* brushes the keyboard map lights its keys with,
                // so a section reads as the same color in both views (KillerShell's layout).
                // Category color remains visible on the keyboard border and bar. List headings use
                // the theme's readable text foreground so every family has consistent contrast.
                header.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
                host.Children.Add(header);

                for (int r = 0; r < section.Rows.Length; r++)
                {
                    var row  = section.Rows[r];
                    bool last = r == section.Rows.Length - 1;
                    var rowGrid = new Grid { Margin = new Thickness(0, 0, 0, last ? 0 : 4) };
                    rowGrid.ColumnDefinitions.Add(new ColumnDefinition
                    {
                        Width = GridLength.Auto,
                        SharedSizeGroup = "KsKeys",
                        MinWidth = 132,   // the old fixed width, now a floor rather than a ceiling
                    });
                    rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                    // Keep the shortcut and description on the same vertical centerline. The wider
                    // key column also leaves a deliberate gap before longer translated labels.
                    var keys = new TextBlock
                    {
                        FontFamily = new FontFamily("Consolas"),
                        FontSize   = 11,
                        Margin     = new Thickness(0, 0, 12, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    FillKeyInlines(keys, ShortcutCustomization.EffectiveKeysForRow(ShortcutStore(), row.LabelKey, row.Keys));
                    keys.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
                    Grid.SetColumn(keys, 0);
                    rowGrid.Children.Add(keys);

                    // Description: fills the rest, wraps when the window is narrow, and stays aligned
                    // with its shortcut regardless of the localized font's line metrics.
                    var label = new TextBlock
                    {
                        TextWrapping = TextWrapping.Wrap,
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    label.SetResourceReference(TextBlock.TextProperty, row.LabelKey);
                    label.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
                    label.SetResourceReference(TextBlock.FontSizeProperty, "Str_KS_FontSize");
                    Grid.SetColumn(label, 1);
                    rowGrid.Children.Add(label);

                    host.Children.Add(rowGrid);
                }
            }
        }

        // #190: custom-shortcut editor, opened from the overlay's Customize link. One row per
        // remappable action: localized name, live chord (same token rendering as the F1 list),
        // Change button. Reset All drops every override. The F1 list itself picks the new
        // bindings up because it renders through EffectiveKeysForRow and rebuilds on open.
        private void CustomizeShortcuts_Click(object sender, RoutedEventArgs e) => OpenShortcutCustomizer();

        private void OpenShortcutCustomizer()
        {
            var win = new Window
            {
                Title = Loc("Str_KS_Customize"),
                Width = 560,
                MinWidth = 460,
                SizeToContent = SizeToContent.Height,
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
            };
            DialogChrome.Configure(win, this, fade: true);

            var rows = new StackPanel { Margin = new Thickness(20, 8, 20, 4) };
            var hint = new TextBlock
            {
                Text = Loc("Str_KS_PressKeys"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 6),
            };
            hint.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            rows.Children.Add(hint);

            var chordBlocks = new Dictionary<string, TextBlock>();
            foreach (var action in ShortcutCustomization.Actions)
            {
                var row = new Grid { Margin = new Thickness(0, 5, 0, 0) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
                label.SetResourceReference(TextBlock.TextProperty, action.LabelKey);
                label.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
                Grid.SetColumn(label, 0);
                row.Children.Add(label);
                var chord = new TextBlock
                {
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 12, 0),
                };
                chord.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
                FillKeyInlines(chord, ShortcutCustomization.EffectiveTokens(ShortcutStore(), action.Id));
                Grid.SetColumn(chord, 1);
                row.Children.Add(chord);
                string capturedId = action.Id;
                var change = UiKit.Make(Loc("Str_KS_Change"), accent: false);
                change.Padding = new Thickness(14, 4, 14, 4);
                change.Click += (_, _) => CaptureShortcut(win, capturedId, () => RefreshCustomizerRows(chordBlocks));
                Grid.SetColumn(change, 2);
                row.Children.Add(change);
                chordBlocks[action.Id] = chord;
                rows.Children.Add(row);
            }

            var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
            var reset = UiKit.Make(Loc("Str_KS_ResetAll"), accent: false);
            reset.Padding = new Thickness(14, 4, 14, 4);
            reset.Click += (_, _) => { ShortcutCustomization.ResetAll(ShortcutStore()); RefreshCustomizerRows(chordBlocks); };
            btnRow.Children.Add(reset);

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(rows, 0);
            Grid.SetRow(btnRow, 1);
            root.Children.Add(rows);
            var btnHost = new Border { Child = btnRow, Padding = new Thickness(20, 4, 20, 14) };
            Grid.SetRow(btnHost, 1);
            root.Children.Add(btnHost);

            win.Content = DialogChrome.Frame(win, this, Loc("Str_KS_Customize"),
                () => { try { win.DialogResult = false; } catch { } win.Close(); }, root);
            win.ShowDialog();
        }

        private void RefreshCustomizerRows(Dictionary<string, TextBlock> chordBlocks)
        {
            foreach (var action in ShortcutCustomization.Actions)
                if (chordBlocks.TryGetValue(action.Id, out var block))
                    FillKeyInlines(block, ShortcutCustomization.EffectiveTokens(ShortcutStore(), action.Id));
        }

        // Modal key-capture for one action. Bare modifiers are ignored, bare Esc cancels,
        // anything without Ctrl/Alt is rejected (typing and navigation stay unreachable),
        // and clashes name the action already holding the chord.
        private void CaptureShortcut(Window owner, string actionId, Action refresh)
        {
            var win = new Window
            {
                Title = Loc("Str_KS_Customize"),
                Width = 420,
                SizeToContent = SizeToContent.Height,
                Owner = owner,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
            };
            DialogChrome.Configure(win, owner, fade: true);
            var msg = new TextBlock
            {
                Text = Loc("Str_KS_PressKeys"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(20, 12, 20, 4),
            };
            msg.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            var conflict = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(20, 4, 20, 12),
                Visibility = Visibility.Collapsed,
            };
            conflict.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryBrush");
            var body = new StackPanel();
            body.Children.Add(msg);
            body.Children.Add(conflict);
            win.PreviewKeyDown += (_, ev) =>
            {
                Key key = ev.Key == Key.System ? ev.SystemKey : ev.Key;
                var mods = Keyboard.Modifiers;
                if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
                    or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.None)
                    return;   // bare modifiers (and Win) never form a chord
                ev.Handled = true;
                if (key == Key.Escape && mods == ModifierKeys.None) { win.Close(); return; }
                if (!mods.HasFlag(ModifierKeys.Control) && !mods.HasFlag(ModifierKeys.Alt))
                {
                    conflict.Text = Loc("Str_KS_PressKeys");
                    conflict.Visibility = Visibility.Visible;
                    return;
                }
                if (!ShortcutCustomization.TrySet(ShortcutStore(), actionId, key, mods, out string clash))
                {
                    conflict.Text = string.Format(Loc("Str_KS_Conflict"),
                        clash.Length > 0 ? Loc(clash) : Loc("Str_KS_Builtin"));
                    conflict.Visibility = Visibility.Visible;
                    return;
                }
                refresh();
                win.Close();
            };
            win.Content = DialogChrome.Frame(win, owner, Loc("Str_KS_Customize"),
                () => { try { win.DialogResult = false; } catch { } win.Close(); }, body);
            win.ShowDialog();
        }
    }
}
