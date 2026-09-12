using System.Text.Json;

namespace KillerPDF
{
    // ============================================================
    // #340: named presets for fillable text fields - dimensions plus visual
    // settings, reusable across documents. Stored as one JSON settings value;
    // persistence arrives as delegates so the logic links into KillerPDF.Tests
    // exactly like ShortcutCustomization.cs. Colors are "#RRGGBB" strings so the
    // record stays UI-free; conversion helpers live beside it.
    // ============================================================

    internal sealed record FieldPreset(
        string Name,
        double WidthPt,
        double HeightPt,
        double FontSizePt,
        string TextHex,
        string FillHex,
        string BorderHex,
        double BorderWidth);

    internal static class FormFieldPresets
    {
        internal readonly record struct Store(
            Func<string, string?> Get,
            Action<string, string> Set,
            Action<string> Remove);

        private const string SettingKey = "FormFieldPresets";

        internal static List<FieldPreset> Load(Store store)
        {
            try
            {
                string? raw = store.Get(SettingKey);
                if (string.IsNullOrWhiteSpace(raw)) return [];
                var list = JsonSerializer.Deserialize<List<FieldPreset>>(raw!);
                return list?.Where(IsValid).ToList() ?? [];
            }
            catch { return []; }   // corrupt JSON resets to empty, never crashes
        }

        internal static void Save(Store store, List<FieldPreset> presets)
        {
            if (presets.Count == 0) { store.Remove(SettingKey); return; }
            store.Set(SettingKey, JsonSerializer.Serialize(presets));
        }

        internal static bool IsValid(FieldPreset preset) =>
            !string.IsNullOrWhiteSpace(preset.Name)
            && preset.WidthPt >= 3 && preset.WidthPt <= 14400
            && preset.HeightPt >= 3 && preset.HeightPt <= 14400
            && preset.FontSizePt >= 0 && preset.FontSizePt <= 96
            && preset.BorderWidth >= 0 && preset.BorderWidth <= 12
            && IsHex(preset.TextHex) && IsHex(preset.FillHex) && IsHex(preset.BorderHex);

        private static bool IsHex(string value) =>
            value.Length == 7 && value[0] == '#'
            && value.Skip(1).All(c => Uri.IsHexDigit(c));

        internal static string UniqueName(List<FieldPreset> presets, string stem)
        {
            if (presets.All(p => p.Name != stem)) return stem;
            int n = 2;
            while (presets.Any(p => p.Name == $"{stem} ({n})")) n++;
            return $"{stem} ({n})";
        }

        internal static void Move(List<FieldPreset> presets, int from, int to)
        {
            if (from < 0 || from >= presets.Count || to < 0 || to >= presets.Count || from == to)
                return;
            var item = presets[from];
            presets.RemoveAt(from);
            presets.Insert(to, item);
        }

        internal static bool Rename(List<FieldPreset> presets, string oldName, string newName)
        {
            newName = newName.Trim();
            if (newName.Length == 0 || presets.Any(p => p.Name == newName)) return false;
            int index = presets.FindIndex(p => p.Name == oldName);
            if (index < 0) return false;
            presets[index] = presets[index] with { Name = newName };
            return true;
        }

        internal static bool Remove(List<FieldPreset> presets, string name) =>
            presets.RemoveAll(p => p.Name == name) > 0;
    }
}
