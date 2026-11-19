namespace KillerPDF.Controls;

/// <summary>#506: the Film Grain tooltip with its chord in the active language's key names,
/// "Filmkorn (Alt+Umschalt+G)" in German. Kept apart from ThemePicker so tests can link it.</summary>
internal static class FilmGrainTip
{
    internal static string Text(Func<string, string?> loc)
        => (loc("Str_Theme_FilmGrain") ?? "Film Grain")
           + " (" + (loc("Str_Key_Alt") ?? "Alt") + "+" + (loc("Str_Key_Shift") ?? "Shift") + "+G)";
}
