namespace KillerPDF.Services;

/// <summary>The status note that says how many pictures a written file left out because they could not be read.
/// Every output that burns pictures, a saved copy written to a temporary file included, hands its count to the caller
/// through its result (<see cref="SavedCopy"/>), and the caller appends this note to its own success status. The
/// previews, the OCR source and the crash-recovery copy are not outputs a status can describe: the preview and OCR
/// source only read the page, and the recovery copy reports it to the log.</summary>
internal static class PicturesSkippedNote
{
    /// <summary>The status with the note after it, or the status unchanged when nothing was skipped. The format is
    /// the localized Str_Save_PicturesSkipped text, which takes the count.</summary>
    internal static string Append(string status, int skipped, string format) =>
        skipped > 0 ? status + " " + string.Format(format, skipped) : status;
}

/// <summary>A saved copy written to a temporary file, with the number of pictures it left out. The count is part of the
/// result, so a caller has to take the path through this value and cannot lose the count.</summary>
internal readonly record struct SavedCopy(string Path, int SkippedPictures)
{
    /// <summary>The status with the skipped-picture note after it when this copy left pictures out.</summary>
    internal string Note(string status, string format) => PicturesSkippedNote.Append(status, SkippedPictures, format);
}