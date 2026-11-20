using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KillerFamily.Accessibility;
using KillerFamily.Controls;
using FamilyTile = KillerFamily.Controls.CardTile;

// ============================================================
// TOOLBAR APPEARANCE - two independent settings
//
// The family engine. It IS KillerPDF's engine (SettingsPanel.cs SetToolbarButton and
// BuildToolbarMenu, promoted 2026-10-27): every app runs the same sizes, captions, picker and
// caption shedding, and supplies only what is its own through the app methods and hooks below.
//
// COPY THIS FILE INTO THE APP, change the namespace, and supply in Shell/ToolbarStyle.Extras.cs:
//   * ToolbarItems()       - (Button, glyph, label key) for every button on the bar. The glyph is
//                            a Segoe MDL2 string ("") or an app token its ToolbarGlyph hook
//                            draws itself.
//   * Loc(string)          - label lookup
//   * Settings.Get/Set     - persistence (string keys and values)
//   * a ContextMenu named ToolbarMenu on the toolbar Grid
// and, only where the app has them, the partial hooks further down: tooltip, drawn glyphs, menu
// gestures and extra menu rows, split buttons, caption shed order, after-restyle work.
//
// ------------------------------------------------------------
// TWO AXES, NOT ONE. The retired single five-way enum:
//
//     SmallIcons | LargeIcons | TextBeside | TextUnder | TextOnly
//
// could not express "large icons WITH text", because choosing any text option threw the size
// choice away. They were never one axis, they only looked like one. (2026-07-30)
//
//     icon size:  Small | Large
//     text:       None | Beside | Under | Only
//
// Every combination is legal. "Only" ignores the size because there is no icon to size - the
// picker GRAYS the size options rather than hiding them, so the setting stays visible and comes
// back when text moves off Only. The size axis ALONE decides the glyph; the text mode only
// decides layout.
//
// ------------------------------------------------------------
// THE NUMBERS. KillerPDF's, and not to be retuned per app - an app that picks its own comes out
// visibly smaller or tighter than the rest of the family.
//
//   glyph     small 14, large 20
//   caption   Brand Sans; beside 16, under 14 (wraps, at most 96 wide), text-only 16
//   button    icon-only:  small 36x32, large 46x42, padding 10,6
//             beside:     auto width, height 34, padding 8,5
//             under:      auto width, auto height, min height 46 (50 large), padding 6,4
//             text-only:  auto width, height 34, padding 8,5
//
// CAPTION SHEDDING. When the window is too narrow, an app's reflow calls
// ShedToolbarCaptions(fits) as its last resort: captioned buttons go icon-only one at a time, in
// ToolbarCaptionShedRank order (lowest first), until fits() says the bar fits. ApplyToolbarStyle
// puts every caption back, so widening the window restores them.
//
// THE PICKER is a fixed family card (CardBuilder, Steve 2026-11-19), three tiles across and not
// customizable: Small icons, Large icons and Text only, then No text, Text beside and Text under,
// then the app's own tiles (KillerPDF's Customize, Hide toolbar and tool menu placement) through
// AddToolbarMenuTiles. The six look tiles stay open on click, so modes can be compared without
// reopening, and show each one's shortcut when the app has one (ToolbarMenuGesture).
//
// GROUP SEPARATORS are a 1px Rectangle, not a wider margin:
//   <Rectangle Width="1" Fill="{DynamicResource CardBorderBrush}" Margin="4,6"/>
//
// ------------------------------------------------------------
// TWO THINGS THAT WILL BREAK THIS IF CHANGED CARELESSLY
//   * If the app highlights an active button by setting the BUTTON's Foreground, nothing built
//     here may set Foreground on its own TextBlocks, or the highlight stops reaching them and
//     every active button looks inactive. Drawn glyphs bind to the button's Foreground.
//   * If buttons carry state in Tag (Killendar's view buttons hold their view name), never write
//     to Tag from here.
// ============================================================
namespace KillerPDF.Shell
{
    public partial class MainWindow
    {
        private enum IconSize { Small, Large }
        private enum LabelMode { None, Beside, Under, Only }

        // Large icons with the text underneath is the family default (2026-07-30). The button
        // name stays reachable in every combination through its tooltip, which is what makes the
        // icon-only settings usable at all.
        private IconSize _iconSize = IconSize.Large;
        private LabelMode _labelMode = LabelMode.Under;

        // Each choice's picker tile glyph (Segoe MDL2) sits beside its label and shortcut.
        private static readonly (IconSize Size, string LabelKey, string GestureId, string Glyph)[] IconSizes =
        {
            (IconSize.Small, "Str_Toolbar_SmallIcons", "CtrlShift:D1", "\uE80A"),
            (IconSize.Large, "Str_Toolbar_LargeIcons", "CtrlShift:D2", "\uF0E2"),
        };

        private static readonly (LabelMode Mode, string LabelKey, string GestureId, string Glyph)[] LabelModes =
        {
            (LabelMode.None,   "Str_Toolbar_TextNone",   "CtrlShift:D3", "\uE7FB"),
            (LabelMode.Beside, "Str_Toolbar_TextBeside", "CtrlShift:D4", "\uE8FD"),
            (LabelMode.Under,  "Str_Toolbar_TextUnder",  "CtrlShift:D5", "\uE8A1"),
            (LabelMode.Only,   "Str_Toolbar_TextOnly",   "CtrlShift:D6", "\uE97E"),
        };

        private static readonly FontFamily MdlFont = new FontFamily("Segoe MDL2 Assets");

        private const double GlyphSmall = 14;
        private const double GlyphLarge = 20;
        private const double CaptionBeside = 16;
        private const double CaptionUnder = 14;
        private const double CaptionUnderMaxWidth = 96;

        // ---- app hooks ----

        /// <summary>App hook: replace a toolbar button's tooltip (the label by default), for
        /// example to add the button's shortcut, or keep the one the XAML gave it.</summary>
        partial void ToolbarTooltip(Button btn, string label, ref object tip);

        /// <summary>App hook: draw a glyph the icon font cannot (set <paramref name="element"/>).
        /// Left null, the glyph string is set in Segoe MDL2 Assets.</summary>
        partial void ToolbarGlyph(string glyph, double size, ref FrameworkElement? element);

        /// <summary>App hook: the shortcut text a picker row shows. <paramref name="id"/> is the
        /// row's gesture id ("CtrlShift:D1" .. "CtrlShift:D6"); empty means no shortcut.</summary>
        partial void ToolbarMenuGesture(string id, ref string gesture);

        /// <summary>App hook: tiles the app adds as the picker card's last row (Customize, Hide). A
        /// tile that changes app state closes the card; the card is rebuilt before it opens again, so
        /// the tiles show the state they were built with.</summary>
        partial void AddToolbarMenuTiles(List<FamilyTile> tiles);

        /// <summary>App hook: split buttons as (main half, dropdown chevron, split style, plain
        /// style). In icon modes the chevron overlaps the main half for the joined look; with a
        /// caption it sits clear of the text and the main half drops its split style.</summary>
        partial void ToolbarSplitButtons(List<(Button? Main, Button? Chevron, string SplitStyle, string PlainStyle)> splits);

        /// <summary>App hook: caption shed order, lowest first. Unranked buttons are 5.</summary>
        partial void ToolbarCaptionShedRank(string labelKey, ref int rank);

        /// <summary>App hook: runs after the saved settings are read, for the app's own toolbar
        /// settings (KillerPDF's hidden toolbar).</summary>
        partial void OnToolbarStyleRestored();

        /// <summary>App hook: runs after every restyle, for button sizes tied to other controls,
        /// overflow menus, cached widths and reflow.</summary>
        partial void OnToolbarStyleApplied();

        // ---- apply ----

        /// <summary>
        /// Restores the saved settings, builds the picker and applies. Call once at startup, AFTER
        /// the locale is in place - the captions are read through Loc() and would otherwise be
        /// built in the wrong language.
        /// </summary>
        private void InitToolbarStyle()
        {
            bool haveNew = false;
            if (Settings.Get("ToolbarIconSize") is string sz && Enum.TryParse<IconSize>(sz, out var s))
            { _iconSize = s; haveNew = true; }
            if (Settings.Get("ToolbarLabels") is string lb && Enum.TryParse<LabelMode>(lb, out var l))
            { _labelMode = l; haveNew = true; }
            if (!haveNew) MigrateOldToolbarStyle();
            OnToolbarStyleRestored();

            BuildToolbarMenu();
            ApplyToolbarStyle();
        }

        /// <summary>
        /// Reads the retired single "ToolbarStyle" setting and splits it across the two new keys,
        /// so an existing install keeps the bar it was left on instead of silently resetting.
        /// Only runs when the new keys are absent.
        /// </summary>
        private void MigrateOldToolbarStyle()
        {
            switch (Settings.Get("ToolbarStyle"))
            {
                case "SmallIcons": _iconSize = IconSize.Small; _labelMode = LabelMode.None; break;
                case "LargeIcons": _iconSize = IconSize.Large; _labelMode = LabelMode.None; break;
                // The old text modes never said what size the icon was, so they take the default.
                case "TextBeside": _labelMode = LabelMode.Beside; break;
                case "TextUnder":  _labelMode = LabelMode.Under;  break;
                case "TextOnly":   _labelMode = LabelMode.Only;   break;
            }
        }

        private void SetIconSize(IconSize size)
        {
            _iconSize = size;
            Settings.Set("ToolbarIconSize", size.ToString());
            ApplyToolbarStyle();
        }

        private void SetLabelMode(LabelMode mode)
        {
            _labelMode = mode;
            Settings.Set("ToolbarLabels", mode.ToString());
            ApplyToolbarStyle();
        }

        /// <summary>Rebuilds every toolbar button with its caption on, then the split chrome, the
        /// picker checks and the app's after-restyle work (which runs any reflow).</summary>
        private void ApplyToolbarStyle()
        {
            foreach (var (btn, glyph, key) in ToolbarItems())
            {
                var label = Loc(key);
                SetToolbarButton(btn, glyph, key, withLabel: true);
                object tip = label;
                ToolbarTooltip(btn, label, ref tip);
                btn.ToolTip = tip;
            }

            ApplyToolbarSplitChrome(_labelMode != LabelMode.None);
            SyncToolbarMenuChecks();
            OnToolbarStyleApplied();
        }

        /// <summary>
        /// One button's content and box for the current settings. withLabel=false forces icon-only
        /// (caption shedding). Never touches Foreground, Background or Tag.
        /// </summary>
        private void SetToolbarButton(Button btn, string glyph, string key, bool withLabel)
        {
            var mode = withLabel || _labelMode == LabelMode.Only ? _labelMode : LabelMode.None;
            btn.FontSize = _iconSize == IconSize.Large ? GlyphLarge : GlyphSmall;
            btn.Content = BuildToolbarContent(_iconSize, mode, glyph, Loc(key));
            SizeToolbarButton(btn, _iconSize, mode);
        }

        /// <summary>The family button box for a combination.</summary>
        private static void SizeToolbarButton(Button btn, IconSize size, LabelMode mode)
        {
            bool large = size == IconSize.Large;
            switch (mode)
            {
                case LabelMode.Only:
                case LabelMode.Beside:
                    btn.Width = double.NaN; btn.MinWidth = 0; btn.Height = 34; btn.MinHeight = 0;
                    btn.Padding = new Thickness(8, 5, 8, 5);
                    break;
                case LabelMode.Under:
                    // Auto height so a wrapped caption grows the button instead of clipping.
                    btn.Width = double.NaN; btn.MinWidth = 0; btn.Height = double.NaN;
                    btn.MinHeight = large ? 50 : 46;
                    btn.Padding = new Thickness(6, 4, 6, 4);
                    break;
                default:   // None - icon only, the fixed box
                    btn.Width = large ? 46 : 36;
                    btn.MinWidth = 0;
                    btn.Height = large ? 42 : 32; btn.MinHeight = 0;
                    btn.Padding = new Thickness(10, 6, 10, 6);
                    break;
            }
        }

        /// <summary>A toolbar glyph at a size: the app's drawing when it has one, else the icon font.</summary>
        private FrameworkElement MakeToolbarGlyph(string glyph, double size)
        {
            FrameworkElement? drawn = null;
            ToolbarGlyph(glyph, size, ref drawn);
            return drawn ?? new Glyph
            {
                Text = glyph,
                FontFamily = MdlFont,
                FontSize = size,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }

        private static TextBlock ToolbarCaption(string label, double size)
        {
            var text = new TextBlock { Text = label, FontSize = size, VerticalAlignment = VerticalAlignment.Center };
            text.SetResourceReference(TextBlock.FontFamilyProperty, "BrandSansFont");
            return text;
        }

        /// <summary>
        /// The button's content. An icon is sized by <paramref name="size"/> ALONE - see the header
        /// on why the text mode must not influence it.
        /// </summary>
        private object BuildToolbarContent(IconSize size, LabelMode mode, string glyph, string label)
        {
            double glyphSize = size == IconSize.Large ? GlyphLarge : GlyphSmall;

            if (mode == LabelMode.Only) return ToolbarCaption(label, CaptionBeside);

            var icon = MakeToolbarGlyph(glyph, glyphSize);
            if (mode == LabelMode.None) return icon;

            if (mode == LabelMode.Beside)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                var text = ToolbarCaption(label, CaptionBeside);
                text.Margin = new Thickness(7, 0, 0, 0);
                row.Children.Add(icon);
                row.Children.Add(text);
                return row;
            }

            var col = new StackPanel { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Center };
            var under = ToolbarCaption(label, CaptionUnder);
            under.Margin = new Thickness(0, 2, 0, 0);
            under.MaxWidth = CaptionUnderMaxWidth;
            under.HorizontalAlignment = HorizontalAlignment.Center;
            under.TextAlignment = TextAlignment.Center;
            under.TextWrapping = TextWrapping.Wrap;
            col.Children.Add(icon);
            col.Children.Add(under);
            return col;
        }

        private void ApplyToolbarSplitChrome(bool textMode)
        {
            var splits = new List<(Button? Main, Button? Chevron, string SplitStyle, string PlainStyle)>();
            ToolbarSplitButtons(splits);
            if (splits.Count == 0) return;
            var chevMargin = textMode ? new Thickness(1, 0, 0, 0) : new Thickness(-6, 0, 0, 0);
            foreach (var (main, chevron, split, plain) in splits)
            {
                if (chevron != null) chevron.Margin = chevMargin;
                if (main != null && TryFindResource(textMode ? plain : split) is Style style) main.Style = style;
            }
        }

        // ---- caption shedding ----

        private int CaptionShedRank(string labelKey)
        {
            int rank = 5;
            ToolbarCaptionShedRank(labelKey, ref rank);
            return rank;
        }

        /// <summary>
        /// The reflow's last resort: drop captions to icon-only, lowest rank first, until
        /// <paramref name="fits"/> is true. Does nothing outside Beside and Under. Returns true when
        /// the bar fits afterwards.
        /// </summary>
        private bool ShedToolbarCaptions(Func<bool> fits)
        {
            if (fits()) return true;
            if (_labelMode is not (LabelMode.Beside or LabelMode.Under)) return false;
            var order = new List<(Button Btn, string Glyph, string Key)>(ToolbarItems());
            order.Sort((a, b) => CaptionShedRank(a.Key).CompareTo(CaptionShedRank(b.Key)));
            foreach (var (btn, glyph, key) in order)
            {
                if (!btn.IsVisible) continue;   // already in the overflow menu
                SetToolbarButton(btn, glyph, key, withLabel: false);
                if (fits()) return true;
            }
            return false;
        }

        /// <summary>Puts every caption back without the rest of a restyle, for a reflow pass that
        /// starts fully expanded.</summary>
        private void RestoreToolbarCaptions()
        {
            if (_labelMode is not (LabelMode.Beside or LabelMode.Under)) return;
            foreach (var (btn, glyph, key) in ToolbarItems())
                SetToolbarButton(btn, glyph, key, withLabel: true);
        }

        // ---- the right-click picker ----

        // The six look tiles on the open card, with whether each is on and usable, so a click repaints the
        // card in place instead of rebuilding it under the pointer.
        private readonly List<(Button Tile, Func<bool> On, Func<bool> Enabled)> _toolbarMenuTiles = new();

        /// <summary>
        /// The picker card: Small icons, Large icons and Text only (what a button shows), then No text,
        /// Text beside and Text under (where its caption goes), then the app's tiles. The look tiles
        /// keep the card open so modes can be compared without reopening. The toolbar Grid needs
        /// Background="Transparent" - not null - or it gets no mouse events and the card never opens.
        /// </summary>
        private void BuildToolbarMenu()
        {
            ToolbarMenu.Items.Clear();
            _toolbarMenuTiles.Clear();
            ToolbarMenu.SetResourceReference(FrameworkElement.StyleProperty, "CardMenu");
            ToolbarMenu.Items.Add(CardBuilder.Header(Loc("Str_Toolbar_Header")));

            var looks = new List<(FamilyTile Tile, Func<bool> On, Func<bool> Enabled)>();
            FamilyTile Look(string glyph, string key, string gestureId, Func<bool> on, Func<bool> enabled, Action set)
            {
                // The kit flips a stay-open tile as it is clicked; the setting changes after that, and its
                // restyle repaints every look tile from the real settings (SyncToolbarMenuChecks).
                var tile = new FamilyTile(glyph, Loc(key), NullIfEmpty(MenuGesture(gestureId)),
                    () => Dispatcher.BeginInvoke(set), Enabled: enabled())
                { Checked = on(), KeepsOpen = true };
                looks.Add((tile, on, enabled));
                return tile;
            }
            static string? NullIfEmpty(string text) => text.Length == 0 ? null : text;

            var shows = new List<FamilyTile>();
            foreach (var (size, key, gestureId, glyph) in IconSizes)
            {
                var s = size;
                shows.Add(Look(glyph, key, gestureId, () => _iconSize == s && _labelMode != LabelMode.Only,
                    // Text-only has no icon to size. Gray the choice rather than hiding it.
                    () => _labelMode != LabelMode.Only, () => SetIconSize(s)));
            }
            var captions = new List<FamilyTile>();
            foreach (var (mode, key, gestureId, glyph) in LabelModes)
            {
                var m = mode;
                var tile = Look(glyph, key, gestureId, () => _labelMode == m, () => true, () => SetLabelMode(m));
                (m == LabelMode.Only ? shows : captions).Add(tile);
            }
            var app = new List<FamilyTile>();
            AddToolbarMenuTiles(app);

            var grid = CardBuilder.TileGroups(ToolbarMenu, Loc("Str_Toolbar_Header"), new[] { shows, captions, app });
            ToolbarMenu.Items.Add(CardBuilder.Slot(grid, new Thickness(0)));
            var buttons = LogicalDescendants(grid).OfType<Button>().ToList();
            foreach (var (tile, on, enabled) in looks)
                if (buttons.FirstOrDefault(b => System.Windows.Automation.AutomationProperties.GetName(b) == tile.Caption) is { } button)
                    _toolbarMenuTiles.Add((button, on, enabled));
        }

        private static IEnumerable<DependencyObject> LogicalDescendants(DependencyObject root)
        {
            yield return root;
            foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
                foreach (var d in LogicalDescendants(child)) yield return d;
        }

        private string MenuGesture(string id)
        {
            string gesture = "";
            ToolbarMenuGesture(id, ref gesture);
            return gesture;
        }

        /// <summary>Brings the picker card in step with the settings: repaints the look tiles while it is
        /// open, and rebuilds it while it is closed, so the app's tiles show their current state too.</summary>
        private void SyncToolbarMenuChecks()
        {
            if (ToolbarMenu is null) return;
            if (!ToolbarMenu.IsOpen) { BuildToolbarMenu(); return; }
            foreach (var (tile, on, enabled) in _toolbarMenuTiles)
            {
                tile.IsEnabled = enabled();
                CardBuilder.ShowTileState(tile, on());
            }
        }

        /// <summary>Rebuild captions and the menu after a live language change.</summary>
        private void RelocalizeToolbar()
        {
            BuildToolbarMenu();
            ApplyToolbarStyle();
        }
    }
}
