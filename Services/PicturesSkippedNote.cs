namespace KillerPDF.Services;

/// <summary>The status note that says how many pictures a written file left out because they could not be read.
/// Save, Save As, the exports, the macros, OCR (Make Searchable PDF and Convert Scan to Text) and Print append it to
/// their success status; each takes the count from its result (<see cref="SavedCopy"/>) or, for Print, from the burn
/// on the worker thread that ran it. The page-bake tools (Transform and Straighten) append it too. The previews append
/// nothing, because a preview is not an output. The crash-recovery copy shows it per document in the recovery dialog.</summary>
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
