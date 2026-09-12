using System.Windows.Input;

namespace KillerPDF
{
    // ============================================================
    // #190: user-remappable action shortcuts. The built-in handler chain in
    // Shell/KeyboardShortcuts.cs is order-sensitive by design, so remapping does NOT
    // rewire it: overrides run FIRST (MainWindow.TryRunCustomShortcut) and abandoned
    // default chords are swallowed there, which needs zero branch edits.
    //
    // String-free of WPF visuals and of App (persistence arrives as delegates), so
    // KillerPDF.Tests links this file exactly like ShortcutTable.cs. Chords are real
    // (Key, ModifierKeys) values; only the 12 core Ctrl/Delete actions below are
    // remappable - single-key tool presets, arrows, wheel and gestures stay fixed.
    // New chords must include Ctrl or Alt, so typing, tool keys and navigation can
    // never be hijacked by a remap.
    // ============================================================

    internal static class ShortcutCustomization
    {
        internal readonly record struct Chord(Key Key, ModifierKeys Mods);

        internal readonly record struct Store(
            Func<string, string?> Get,
            Action<string, string> Set,
            Action<string> Remove);

        // id, default chord, F1-overlay label key, default overlay token chord.
        internal static readonly (string Id, string LabelKey, Key Key, ModifierKeys Mods, string DefaultTokens)[] Actions =
        [
            ("Open",       "Str_KS_Open",        Key.O,      ModifierKeys.Control,                 "%ctrl%+O"),
            ("Save",       "Str_Lbl_Save",       Key.S,      ModifierKeys.Control,                 "%ctrl%+S"),
            ("SaveAs",     "Str_KS_SaveAs",      Key.S,      ModifierKeys.Control | ModifierKeys.Shift, "%ctrl%+%shift%+S"),
            ("CloseTab",   "Str_KS_CloseFile",   Key.W,      ModifierKeys.Control,                 "%ctrl%+W"),
            ("Print",      "Str_KS_Print",       Key.P,      ModifierKeys.Control,                 "%ctrl%+P"),
            ("Undo",       "Str_KS_Undo",        Key.Z,      ModifierKeys.Control,                 "%ctrl%+Z"),
            ("Redo",       "Str_Ctx_Redo",       Key.Y,      ModifierKeys.Control,                 "%ctrl%+Y"),
            ("Find",       "Str_KS_Find",        Key.F,      ModifierKeys.Control,                 "%ctrl%+F"),
            ("CopyText",   "Str_KS_CopyText",    Key.C,      ModifierKeys.Control,                 "%ctrl%+C"),
            ("Paste",      "Str_KS_Paste",       Key.V,      ModifierKeys.Control,                 "%ctrl%+V"),
            ("SelectAll",  "Str_KS_SelectAll",   Key.A,      ModifierKeys.Control,                 "%ctrl%+A"),
            ("DeleteAnnot","Str_KS_DeleteAnnot", Key.Delete, ModifierKeys.None,                    "%del%"),
        ];

        private static string KeyName(string id) => "ShortcutCustom_" + id;

        // Canonical "Ctrl+Shift+P" / "Delete" form. Modifiers always order Ctrl, Shift, Alt.
        internal static string FormatChord(Key key, ModifierKeys mods)
        {
            string s = "";
            if (mods.HasFlag(ModifierKeys.Control)) s += "Ctrl+";
            if (mods.HasFlag(ModifierKeys.Shift))   s += "Shift+";
            if (mods.HasFlag(ModifierKeys.Alt))     s += "Alt+";
            return s + (key == Key.Delete ? "Delete" : key.ToString());
        }

        internal static bool TryParseChord(string? text, out Key key, out ModifierKeys mods)
        {
            key = Key.None; mods = ModifierKeys.None;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string[] parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0) return false;
            foreach (string part in parts[..^1])
            {
                if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase)
                    || part.Equals("Control", StringComparison.OrdinalIgnoreCase))
                    mods |= ModifierKeys.Control;
                else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase))
                    mods |= ModifierKeys.Shift;
                else if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase))
                    mods |= ModifierKeys.Alt;
                else return false;
            }
            string name = parts[^1];
            if (name.Equals("Del", StringComparison.OrdinalIgnoreCase)) name = "Delete";
            // Windows-key chords belong to the OS and are never valid here.
            if (name.Equals("LWin", StringComparison.OrdinalIgnoreCase)
                || name.Equals("RWin", StringComparison.OrdinalIgnoreCase))
                return false;
            if (!Enum.TryParse(name, ignoreCase: true, out Key parsed) || parsed == Key.None)
                return false;
            key = parsed;
            return true;
        }

        // Canonical "Ctrl+Shift+P" form from a captured key plus modifier flags. Returns
        // null for bare modifiers, the Windows keys, and anything unparseable, so capture
        // code can use it as the validity check.
        internal static string? Canonicalize(string keyName, bool ctrl, bool shift, bool alt)
        {
            if (string.IsNullOrWhiteSpace(keyName)) return null;
            string name = keyName.Trim();
            switch (name)
            {
                case "LeftCtrl":
                case "RightCtrl":
                case "LeftShift":
                case "RightShift":
                case "LeftAlt":
                case "RightAlt":
                case "LWin":
                case "RWin":
                case "None":
                    return null;
            }
            string chord = (ctrl ? "Ctrl+" : "") + (shift ? "Shift+" : "") + (alt ? "Alt+" : "")
                + (name == "Del" ? "Delete" : name);
            return TryParseChord(chord, out _, out _) ? chord : null;
        }

        internal static Chord EffectiveChord(Store store, string id)
        {
            var action = Actions.First(a => a.Id == id);
            string? saved = store.Get(KeyName(id));
            if (saved is not null && TryParseChord(saved, out Key key, out ModifierKeys mods))
                return new Chord(key, mods);
            return new Chord(action.Key, action.Mods);
        }

        internal static bool IsRemapped(Store store, string id)
        {
            var action = Actions.First(a => a.Id == id);
            var effective = EffectiveChord(store, id);
            return effective.Key != action.Key || effective.Mods != action.Mods;
        }

        // Overlay token chord ("%ctrl%+P") for the F1 list, so a remapped row shows its
        // real binding through the same localized FillKeyInlines path as everything else.
        internal static string EffectiveTokens(Store store, string id)
        {
            var effective = EffectiveChord(store, id);
            string s = "";
            if (effective.Mods.HasFlag(ModifierKeys.Control)) s += "%ctrl%+";
            if (effective.Mods.HasFlag(ModifierKeys.Shift))   s += "%shift%+";
            if (effective.Mods.HasFlag(ModifierKeys.Alt))     s += "%alt%+";
            return s + effective.Key switch
            {
                Key.Delete  => "%del%",
                Key.Enter   => "%enter%",
                Key.Escape  => "%esc%",
                Key.Tab     => "%tab%",
                Key.Home    => "%home%",
                Key.End     => "%end%",
                Key.PageUp  => "%pgup%",
                Key.PageDown=> "%pgdn%",
                _           => effective.Key.ToString(),
            };
        }

        // F1-list substitution: only the row carrying the action's DEFAULT token chord is
        // rewritten, so alias rows (e.g. the second Ctrl+Shift+Z redo row) keep showing.
        internal static string EffectiveKeysForRow(Store store, string labelKey, string defaultTokens)
        {
            foreach (var action in Actions)
                if (action.LabelKey == labelKey && action.DefaultTokens == defaultTokens
                    && IsRemapped(store, action.Id))
                    return EffectiveTokens(store, action.Id);
            return defaultTokens;
        }

        // True when the canonical chord is claimed: actionId names the action to run,
        // null means an abandoned default to swallow (its built-in branch must not fire).
        internal static bool TryResolve(Store store, string chord, out string? actionId)
        {
            foreach (var action in Actions)
            {
                var effective = EffectiveChord(store, action.Id);
                if (string.Equals(FormatChord(effective.Key, effective.Mods), chord, StringComparison.Ordinal))
                {
                    actionId = action.Id;
                    return true;
                }
            }
            foreach (var action in Actions)
            {
                if (IsRemapped(store, action.Id)
                    && string.Equals(FormatChord(action.Key, action.Mods), chord, StringComparison.Ordinal))
                {
                    actionId = null;
                    return true;
                }
            }
            actionId = null;
            return false;
        }

        // Validates a candidate chord. Returns false with the conflicting action's label key
        // (or "" when it clashes with a built-in binding) and true when it is free to take.
        // Backing onto the default clears the override instead of storing it.
        internal static bool TrySet(Store store, string id, Key key, ModifierKeys mods,
            out string conflictLabelKey)
        {
            conflictLabelKey = "";
            var action = Actions.First(a => a.Id == id);
            if (key == action.Key && mods == action.Mods)
            {
                store.Remove(KeyName(id));   // back to default: drop the override
                return true;
            }
            if (!mods.HasFlag(ModifierKeys.Control) && !mods.HasFlag(ModifierKeys.Alt))
                return false;   // typable keys and navigation stay unreachable, by design
            foreach (var other in Actions)
            {
                if (other.Id == id) continue;
                var effective = EffectiveChord(store, other.Id);
                if (effective.Key == key && effective.Mods == mods)
                {
                    conflictLabelKey = other.LabelKey;
                    return false;
                }
            }
            if (ReservedLabel(key, mods) is { } reserved)
            {
                conflictLabelKey = reserved;
                return false;
            }
            store.Set(KeyName(id), FormatChord(key, mods));
            return true;
        }

        internal static void ResetAll(Store store)
        {
            foreach (var action in Actions) store.Remove(KeyName(action.Id));
        }

        // Built-in chords a remap must not steal, derived from the same ShortcutTable the
        // F1 overlay and keyboard map are built from, plus the Ctrl bindings the table only
        // documents positionally. Returns the clashing row's label key, or "" when the clash
        // has no row (positional binding).
        private static string? ReservedLabel(Key key, ModifierKeys mods)
        {
            foreach (var cap in ShortcutTable.AllCapClaims())
            {
                int colon = cap.IndexOf(':');
                if (colon < 0) continue;
                ModifierKeys capMods = cap[..colon] switch
                {
                    "Ctrl" => ModifierKeys.Control,
                    "CtrlShift" => ModifierKeys.Control | ModifierKeys.Shift,
                    "Shift" => ModifierKeys.Shift,
                    "Alt" => ModifierKeys.Alt,
                    _ => ModifierKeys.None,
                };
                if (capMods != mods) continue;
                if (!TryCapKey(cap[(colon + 1)..], out Key capKey)) continue;
                if (capKey == key)
                    return ReservedRowLabel(cap);
            }
            // Ctrl bindings the table documents without caps.
            if (mods == ModifierKeys.Control && key is Key.OemPlus or Key.Add or Key.OemMinus
                or Key.Subtract or Key.D0 or Key.D1 or Key.D2 or Key.D3 or Key.OemQuestion)
                return "";
            return null;
        }

        private static bool TryCapKey(string id, out Key key)
        {
            key = id switch
            {
                "Del" => Key.Delete,
                "Menu" => Key.Apps,
                "PgUp" => Key.PageUp,
                "PgDn" => Key.PageDown,
                _ => Key.None,
            };
            if (key != Key.None) return true;
            return Enum.TryParse(id, ignoreCase: true, out key) && key != Key.None;
        }

        private static string ReservedRowLabel(string capClaim)
        {
            int colon = capClaim.IndexOf(':');
            if (colon < 0) return "";
            KbLayer layer = capClaim[..colon] switch
            {
                "Ctrl" => KbLayer.Ctrl,
                "CtrlShift" => KbLayer.CtrlShift,
                "Shift" => KbLayer.Shift,
                "Alt" => KbLayer.Alt,
                _ => KbLayer.Base,
            };
            string id = capClaim[(colon + 1)..];
            var map = ShortcutTable.BuildMap();
            if (map.TryGetValue(layer, out var ids) && ids.TryGetValue(id, out var row))
                return row.Label;
            return "";
        }
    }
}
