using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace KillerPDF.Shell
{
    // Custom shortcuts (#190): the user's key choices, the translation the key handler runs every
    // press through, and the Customize button in the shortcuts overlay.
    public partial class MainWindow
    {
        private const string ShortcutRemapSetting = "ShortcutRemap";

        // One map for the whole app: every window translates keys with it and refreshes when it moves.
        private static readonly ShortcutRemapStore s_shortcutRemaps = new(
            () => App.GetSetting(ShortcutRemapSetting), text => App.SetSetting(ShortcutRemapSetting, text));

        private ShortcutRemap ShortcutRemaps => s_shortcutRemaps.Current;

        private void FollowShortcutRemapChanges()
        {
            s_shortcutRemaps.Changed += RefreshShortcutViews;
            Closed += (_, _) => s_shortcutRemaps.Changed -= RefreshShortcutViews;
        }

        /// <summary>
        /// The key the dispatcher should see for a press. A custom key becomes the command's
        /// built-in key, with the modifiers that key needs; a key whose command moved away is
        /// swallowed. Alt chords and F10 arrive as system keys, so they are handed on that way.
        /// </summary>
        private (Key Key, ModifierKeys Modifiers, Key SystemKey, bool Swallow) TranslateShortcut(
            Key key, ModifierKeys modifiers, Key systemKey)
        {
            if (ShortcutRemaps.Count == 0) return (key, modifiers, systemKey, false);
            Key actual = key == Key.System ? systemKey : key;
            if (CapIdForKey(actual) is not string id || LayerFor(modifiers) is not KbLayer layer)
                return (key, modifiers, systemKey, false);
            if (ShortcutRemaps.Translate(layer, id) is not (KbLayer newLayer, string newId))
                return (key, modifiers, systemKey, true);
            if (newLayer == layer && newId == id) return (key, modifiers, systemKey, false);
            Key target = KeyForCapId(newId);
            ModifierKeys newModifiers = ModifiersFor(newLayer);
            bool system = newModifiers.HasFlag(ModifierKeys.Alt) || target == Key.F10;
            return (system ? Key.System : target, newModifiers, system ? target : Key.None, false);
        }

        /// <summary>The keyboard map's id for a key a shortcut may use, or null.</summary>
        internal static string? CapIdForKey(Key key) => key switch
        {
            >= Key.A and <= Key.Z => key.ToString(),
            >= Key.D0 and <= Key.D9 => key.ToString(),
            >= Key.F1 and <= Key.F12 => key.ToString(),
            Key.Delete => "Del",
            Key.Insert => "Ins",
            _ => null
        };

        private static Key KeyForCapId(string id) => id switch
        {
            "Del" => Key.Delete,
            "Ins" => Key.Insert,
            _ => Enum.Parse<Key>(id)
        };

        /// <summary>The keyboard map layer for a modifier combination, or null for one the map has
        /// no layer for (Ctrl+Alt and the like).</summary>
        internal static KbLayer? LayerFor(ModifierKeys modifiers) => modifiers switch
        {
            ModifierKeys.None => KbLayer.Base,
            ModifierKeys.Control => KbLayer.Ctrl,
            ModifierKeys.Control | ModifierKeys.Shift => KbLayer.CtrlShift,
            ModifierKeys.Shift => KbLayer.Shift,
            ModifierKeys.Alt => KbLayer.Alt,
            ModifierKeys.Alt | ModifierKeys.Shift => KbLayer.AltShift,
            _ => null
        };

        private static ModifierKeys ModifiersFor(KbLayer layer) => layer switch
        {
            KbLayer.Ctrl => ModifierKeys.Control,
            KbLayer.CtrlShift => ModifierKeys.Control | ModifierKeys.Shift,
            KbLayer.Shift => ModifierKeys.Shift,
            KbLayer.Alt => ModifierKeys.Alt,
            KbLayer.AltShift => ModifierKeys.Alt | ModifierKeys.Shift,
            _ => ModifierKeys.None
        };

        /// <summary>The overlay row's key text: the table's own unless one of its keys was moved,
        /// then each of its keys as they are now.</summary>
        private string ShortcutRowKeys(KsRow row) => ShortcutRowText.RowKeys(row.Keys, row.Caps, ShortcutRemaps);

        // The Customize button sits in the overlay's footer, opposite the on/off switch.
        private void AddShortcutCustomizeButton()
        {
            if (KeyboardShortcutsCheck.Parent is not Grid footer
                || footer.Children.OfType<Button>().Any(button => Equals(button.Tag, "customize"))) return;
            var customize = UiKit.Make(Loc("Str_KS_Customize"), accent: false);
            customize.Tag = "customize";
            customize.VerticalAlignment = VerticalAlignment.Center;
            customize.Click += (_, _) => OpenShortcutCustomizer();
            Grid.SetColumn(customize, 1);
            footer.Children.Add(customize);
        }

        private void OpenShortcutCustomizer()
        {
            var dialog = new ShortcutCustomizeDialog(this, ShortcutRemaps);
            dialog.ShowDialog();
            if (!dialog.Saved) return;
            s_shortcutRemaps.Save(dialog.Result);
        }

        // The list, the map and the tooltips show the keys as they are now.
        private void RefreshShortcutViews()
        {
            KbMap = ShortcutTable.BuildMap(ShortcutRemaps.Effective);
            BuildShortcutsColumn(ShortcutLeftColumn, KsColumn(right: false));
            BuildShortcutsColumn(ShortcutRightColumn, KsColumn(right: true));
            if (_kbBuilt && ShortcutKeyboardHost.Visibility == Visibility.Visible) BuildKeyboardView();
            else _kbBuilt = false;
            BuildToolbarMenu();
            ApplyShortcutTooltips();
        }
    }
}
