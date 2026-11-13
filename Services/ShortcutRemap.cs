using System.Linq;

namespace KillerPDF
{
    /// <summary>A command the user can move to another key: the cap it ships on, its label and
    /// the overlay section it is listed under.</summary>
    internal readonly record struct ShortcutCommand(KsCap Original, string LabelKey, string Cat);

    /// <summary>
    /// Custom shortcuts (#190). A command keeps its built-in binding in ShortcutTable; this table
    /// only records which other key now stands in for it. The window's key handler translates a
    /// pressed custom key back to the built-in one before dispatching, so every handler keeps
    /// matching the keys it always did, and the key a command moved away from stops doing it.
    ///
    /// Works in the keyboard map's terms (layer plus cap id) so the list and the map can show the
    /// keys the user chose. Free of WPF like ShortcutTable, so the tests can link it.
    /// </summary>
    internal sealed class ShortcutRemap
    {
        private readonly Dictionary<string, KsCap> _customByOriginal = new(StringComparer.Ordinal);
        private readonly Dictionary<string, KsCap> _originalByCustom = new(StringComparer.Ordinal);

        internal static string Name(KsCap cap) => cap.Layer + ":" + cap.Id;

        private static string Name(KbLayer layer, string id) => layer + ":" + id;

        /// <summary>Keys a shortcut may sit on: letters, the number row, the function keys and
        /// Delete and Insert. Navigation, Enter, Escape and the punctuation keys whose meaning
        /// changes with the keyboard layout stay where they are.</summary>
        internal static bool IsAssignable(string id) =>
            id.Length == 1 && id[0] is >= 'A' and <= 'Z'
            || id.Length == 2 && id[0] == 'D' && char.IsDigit(id[1])
            || id is "Del" or "Ins"
            || id.Length is 2 or 3 && id[0] == 'F' && int.TryParse(id[1..], out int f) && f is >= 1 and <= 12;

        // F2 renames in the outline and the toggle chord has to stay reachable, so neither moves.
        // F1 and F12 always open the shortcuts list and About, so bare F1 and F12 never move either.
        // The find box reads Alt+C and Alt+W itself, so they are listed but stay where they are.
        internal static bool IsRemappable(KsCap cap) =>
            IsAssignable(cap.Id) && cap.Id != "F2" && !(cap.Layer == KbLayer.CtrlShift && cap.Id == "K")
            && !(cap.Layer == KbLayer.Base && cap.Id is "F1" or "F12")
            && !(cap.Layer == KbLayer.Alt && cap.Id is "C" or "W");

        /// <summary>Every command that can be moved, once each, in the table's order.</summary>
        internal static IReadOnlyList<ShortcutCommand> Commands { get; } =
        [
            .. ShortcutTable.KsAll
                .SelectMany(binding => binding.Caps.Select(cap =>
                    new ShortcutCommand(cap, cap.LabelKey.Length > 0 ? cap.LabelKey : binding.LabelKey, binding.Cat)))
                .Where(command => IsRemappable(command.Original))
                .GroupBy(command => Name(command.Original))
                .Select(group => group.First())
        ];

        /// <summary>The key a command answers to now.</summary>
        internal KsCap Effective(KsCap original) =>
            _customByOriginal.TryGetValue(Name(original), out KsCap custom) ? custom : original;

        internal bool IsCustomized(KsCap original) => _customByOriginal.ContainsKey(Name(original));

        internal int Count => _customByOriginal.Count;

        /// <summary>
        /// The built-in key a pressed key stands for. A custom key returns the command's own key;
        /// a built-in key whose command moved elsewhere returns null, so it does nothing; any other
        /// key passes through unchanged.
        /// </summary>
        internal (KbLayer Layer, string Id)? Translate(KbLayer layer, string id)
        {
            string name = Name(layer, id);
            if (_originalByCustom.TryGetValue(name, out KsCap original)) return (original.Layer, original.Id);
            if (_customByOriginal.ContainsKey(name)) return null;
            return (layer, id);
        }

        /// <summary>
        /// Moves <paramref name="original"/>'s command to <paramref name="custom"/>. Returns null
        /// when that worked, or the label key of the command already on that key, which is left
        /// as it was. Moving a command back to its own key clears the customization.
        /// </summary>
        internal string? Assign(KsCap original, KsCap custom)
        {
            if (!IsRemappable(original) || !IsAssignable(custom.Id))
                return null;
            string target = Name(custom);
            foreach (KsBinding binding in ShortcutTable.KsAll)
                foreach (KsCap cap in binding.Caps)
                {
                    if (Name(cap) == Name(original)) continue;
                    if (Name(Effective(cap)) == target)
                        return cap.LabelKey.Length > 0 ? cap.LabelKey : binding.LabelKey;
                }
            if (Name(custom) == Name(original)) Reset(original);
            else _customByOriginal[Name(original)] = custom with { LabelKey = "" };
            Reindex();
            return null;
        }

        internal void Reset(KsCap original)
        {
            _customByOriginal.Remove(Name(original));
            Reindex();
        }

        internal void ResetAll()
        {
            _customByOriginal.Clear();
            _originalByCustom.Clear();
        }

        /// <summary>"Ctrl:F=Alt:K;F3=F4" style text for the settings file.</summary>
        internal string Serialize() => string.Join(";", _customByOriginal
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => pair.Key + "=" + Name(pair.Value)));

        /// <summary>Reads saved customizations, dropping any entry that is malformed, names a
        /// command that cannot move, or would land on a key something else already uses.</summary>
        internal static ShortcutRemap Parse(string? text)
        {
            var remap = new ShortcutRemap();
            if (string.IsNullOrWhiteSpace(text)) return remap;
            foreach (string entry in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string[] parts = entry.Split('=');
                if (parts.Length != 2 || ParseCap(parts[0]) is not KsCap original
                    || ParseCap(parts[1]) is not KsCap custom) continue;
                if (!Commands.Any(command => Name(command.Original) == Name(original))) continue;
                remap.Assign(original, custom);
            }
            return remap;
        }

        internal static KsCap? ParseCap(string text)
        {
            int colon = text.IndexOf(':');
            if (colon <= 0 || !Enum.TryParse(text[..colon], out KbLayer layer)
                || !Enum.IsDefined(layer)) return null;
            string id = text[(colon + 1)..];
            return IsAssignable(id) ? new KsCap(layer, id, "") : null;
        }

        private void Reindex()
        {
            _originalByCustom.Clear();
            foreach ((string original, KsCap custom) in _customByOriginal)
                if (ParseCap(original) is KsCap cap) _originalByCustom[Name(custom)] = cap;
        }
    }
}
