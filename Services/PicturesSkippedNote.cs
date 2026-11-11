namespace KillerPDF.Services;

/// <summary>The status note that says how many pictures a written file left out because they could not be read.
/// Every output that burns pictures appends it, so no path leaves a picture out silently.</summary>
internal static class PicturesSkippedNote
{
    /// <summary>The status with the note after it, or the status unchanged when nothing was skipped. The format is
    /// the localized Str_Save_PicturesSkipped text, which takes the count.</summary>
    internal static string Append(string status, int skipped, string format) =>
        skipped > 0 ? status + " " + string.Format(format, skipped) : status;
}
