namespace KillerPDF.Services
{
    /// <summary>
    /// The accent rules for each theme family, by its file name ("Dark", "98SE", "TrashPolka"): whether it takes
    /// accents, which accent its base palette bakes in, and the setting its chosen accent is saved under. The older
    /// neutral families bake Green; Trash Polka bakes Red (Blood), so its overlays cover the other seven, Green
    /// included.
    /// </summary>
    internal static class ThemeAccentRules
    {
        private static readonly HashSet<string> AccentFamilies = new(StringComparer.Ordinal)
        {
            "Dark", "Light", "Black", "98SE", "TrashPolka",
        };

        internal static bool TakesAccents(string family) => AccentFamilies.Contains(family);

        internal static string BakedAccent(string family) => family == "TrashPolka" ? "Red" : "Green";

        internal static string SettingKey(string family) => family + "Accent";
    }
}
