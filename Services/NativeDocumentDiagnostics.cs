namespace KillerPDF.Services;

/// <summary>One chapter's entry in the book diagnostics panel: a heading and its plain lines.</summary>
internal sealed record NativeDiagnosticRow(string Heading, IReadOnlyList<string> Lines);

/// <summary>Turns what a native book left out or ignored into localized rows, one per chapter.</summary>
internal static class NativeDocumentDiagnostics
{
    /// <remarks>
    /// Chapters appear in reading order and only when something was refused, omitted or ignored.
    /// The engine's reason stays as written, behind a localized "Reason" label.
    /// </remarks>
    internal static IReadOnlyList<NativeDiagnosticRow> Build(NativeWorkingDocument document, Func<string, string> loc)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(loc);
        var lines = new SortedDictionary<int, List<string>>();
        foreach (var refused in document.ChapterDiagnostics)
        {
            var chapter = Chapter(refused.SpineIndex);
            chapter.Add(string.Format(loc("Str_Epub_Diagnostics_ChapterRefused"), refused.Href));
            chapter.Add(string.Format(loc("Str_Epub_Diagnostics_Reason"), refused.Message));
        }
        foreach (var omitted in document.ContentDiagnostics)
        {
            var chapter = Chapter(omitted.SpineIndex);
            chapter.Add(string.Format(loc("Str_Epub_Diagnostics_ItemOmitted"), omitted.Href));
            chapter.Add(string.Format(loc("Str_Epub_Diagnostics_Reason"), omitted.Message));
        }
        foreach (var ignored in document.IgnoredDeclarations)
            if (ignored.Count > 0)
                Chapter(ignored.SpineIndex).Add(string.Format(loc("Str_Epub_Diagnostics_Ignored"), ignored.Count));
        return lines.Select(pair => new NativeDiagnosticRow(
            string.Format(loc("Str_Epub_Diagnostics_Chapter"), pair.Key + 1), pair.Value.AsReadOnly())).ToArray();

        List<string> Chapter(int spineIndex)
        {
            if (!lines.TryGetValue(spineIndex, out var chapter)) lines.Add(spineIndex, chapter = []);
            return chapter;
        }
    }
}
