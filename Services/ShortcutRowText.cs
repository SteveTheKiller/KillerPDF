using System.Linq;

namespace KillerPDF
{
    /// <summary>
    /// The key text a shortcut surface prints for a command, written with the overlay's key-name
    /// tokens so it reads in the user's language. Free of WPF like ShortcutTable, so the tests can
    /// link it and check that the list follows a remap.
    /// </summary>
    internal static class ShortcutRowText
    {
        /// <summary>A cap written with the overlay's key-name tokens.</summary>
        internal static string CapKeys(KsCap cap)
        {
            string prefix = cap.Layer switch
            {
                KbLayer.Ctrl => "%ctrl%+",
                KbLayer.CtrlShift => "%ctrl%+%shift%+",
                KbLayer.Shift => "%shift%+",
                KbLayer.Alt => "%alt%+",
                KbLayer.AltShift => "%alt%+%shift%+",
                _ => ""
            };
            string key = cap.Id switch
            {
                "Del" => "%del%",
                "Enter" => "%enter%",
                "Esc" => "%esc%",
                "Menu" => "%menu%",
                "Home" => "%home%",
                "End" => "%end%",
                "PgUp" => "%pgup%",
                "PgDn" => "%pgdn%",
                "Tab" => "%tab%",
                "Equals" => "%zin%",
                "Minus" => "%zout%",
                "Slash" => "?",
                "Left" => "←",
                "Up" => "↑",
                "Right" => "→",
                "Down" => "↓",
                var digit when digit.Length == 2 && digit[0] == 'D' && char.IsDigit(digit[1]) => digit[1..],
                var other => other
            };
            return prefix + key;
        }

        private const string DocumentToolsChord = "%ctrl%+%shift%+D";

        /// <summary>
        /// The overlay row's key text: the table's own unless one of its keys was moved. A moved
        /// key is swapped into the row's own text where it sits, so "F11 / Esc" and "L (or U)" keep
        /// their shape; a row that cannot be edited that way lists each of its keys as it is now.
        /// The Document tools letter rows follow the chord that opens the menu.
        /// </summary>
        internal static string RowKeys(string keys, KsCap[] caps, ShortcutRemap remap)
        {
            KsCap chord = ShortcutTable.Cap("CtrlShift:D");
            if (keys.StartsWith(DocumentToolsChord + ", ", StringComparison.Ordinal) && remap.IsCustomized(chord))
                return CapKeys(remap.Effective(chord)) + keys[DocumentToolsChord.Length..];
            if (!caps.Any(remap.IsCustomized)) return keys;

            string[] parts = keys.Split(" / ");
            if (parts.Length == caps.Length && parts.Zip(caps).All(pair => pair.First == CapKeys(pair.Second)))
                return string.Join(" / ", caps.Select(cap => CapKeys(remap.Effective(cap))));

            var edits = new List<(int Start, int Length, string Text)>();
            foreach (KsCap cap in caps.Where(remap.IsCustomized))
            {
                int start = IndexOfKey(keys, CapKeys(cap));
                if (start < 0) return string.Join(" / ", caps.Select(c => CapKeys(remap.Effective(c))));
                edits.Add((start, CapKeys(cap).Length, CapKeys(remap.Effective(cap))));
            }
            string result = keys;
            foreach (var (start, length, text) in edits.OrderByDescending(edit => edit.Start))
                result = result[..start] + text + result[(start + length)..];
            return result;
        }

        // Where a key's text stands on its own in a row: not the tail of another chord ("+O" in
        // "%shift%+O") and not the head of a longer key ("F1" in "F10").
        private static int IndexOfKey(string keys, string key)
        {
            for (int at = keys.IndexOf(key, StringComparison.Ordinal); at >= 0;
                 at = keys.IndexOf(key, at + 1, StringComparison.Ordinal))
            {
                bool startsClean = at == 0 || !(char.IsLetterOrDigit(keys[at - 1]) || keys[at - 1] is '%' or '+');
                int end = at + key.Length;
                bool endsClean = end == keys.Length || !(char.IsLetterOrDigit(keys[end]) || keys[end] is '%' or '-');
                if (startsClean && endsClean) return at;
            }
            return -1;
        }
    }
}
