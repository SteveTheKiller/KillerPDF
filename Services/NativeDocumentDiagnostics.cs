using System.Text;
using KillerPdf.Engine.Formats.Epub;

namespace KillerPDF.Services;

/// <summary>
/// One chapter's entry in the book diagnostics panel: a heading, its localized lines, and the same
/// lines with the engine's English diagnosis for the copied report.
/// </summary>
internal sealed record NativeDiagnosticRow(string Heading, IReadOnlyList<string> Lines)
{
    internal IReadOnlyList<string> ReportLines { get; init; } = Lines;
}

/// <summary>Turns what a native book left out or ignored into localized rows, one per chapter.</summary>
internal static class NativeDocumentDiagnostics
{
    /// <summary>The string key for an engine omission reason; every reason has one in all locales.</summary>
    internal static string ReasonKey(EpubOmissionReason reason) =>
        "Str_Epub_Reason_" + (Enum.IsDefined(reason) ? reason : EpubOmissionReason.Other);

    /// <summary>The text Copy report puts on the clipboard, keeping the engine's English diagnosis for support.</summary>
    internal static string Report(IReadOnlyList<NativeDiagnosticRow> rows)
    {
        var report = new StringBuilder();
        foreach (var row in rows)
        {
            report.AppendLine(row.Heading);
            foreach (string line in row.ReportLines) report.Append("  ").AppendLine(line);
        }
        return report.ToString();
    }

    /// <summary>
    /// The note above the rows while the book is still being paginated, naming how far it has come; null once every
    /// page is known. The rows cover only the chapters paginated so far.
    /// </summary>
    internal static string? CountingNote(NativeWorkingDocument document, Func<string, string> loc)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(loc);
        if (!document.IsPaginating) return null;
        var (paginated, total) = document.PaginationProgress;
        return string.Format(loc("Str_Epub_Diagnostics_Counting"), paginated, total);
    }

    /// <remarks>
    /// Chapters appear in reading order and only when something was refused, omitted or ignored.
    /// The panel shows a localized reason; the report adds the engine's English message after it.
    /// </remarks>
    internal static IReadOnlyList<NativeDiagnosticRow> Build(NativeWorkingDocument document, Func<string, string> loc)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(loc);
        var lines = new SortedDictionary<int, (List<string> Shown, List<string> Report)>();
        foreach (var refused in document.ChapterDiagnostics)
            Add(refused, "Str_Epub_Diagnostics_ChapterRefused");
        foreach (var omitted in document.ContentDiagnostics)
            Add(omitted, "Str_Epub_Diagnostics_ItemOmitted");
        foreach (var ignored in document.IgnoredDeclarations)
            if (ignored.Count > 0)
            {
                string line = string.Format(loc("Str_Epub_Diagnostics_Ignored"), ignored.Count);
                var chapter = Chapter(ignored.SpineIndex);
                chapter.Shown.Add(line);
                chapter.Report.Add(line);
            }
        // Rows follow the pages: a non-linear chapter's row comes where the reader appended it.
        var order = document.ReadingOrder.Select((spine, position) => (spine, position)).ToDictionary(p => p.spine, p => p.position);
        return lines.OrderBy(pair => order.TryGetValue(pair.Key, out int position) ? position : int.MaxValue)
            .ThenBy(pair => pair.Key).Select(pair => new NativeDiagnosticRow(
            string.Format(loc("Str_Epub_Diagnostics_Chapter"), pair.Key + 1), pair.Value.Shown.AsReadOnly())
        { ReportLines = pair.Value.Report.AsReadOnly() }).ToArray();

        void Add(EpubChapterDiagnostic diagnostic, string whatKey)
        {
            var chapter = Chapter(diagnostic.SpineIndex);
            string what = string.Format(loc(whatKey), diagnostic.Href);
            string reason = string.Format(loc("Str_Epub_Diagnostics_Reason"), loc(ReasonKey(diagnostic.Reason)));
            // The one omitted image or link target, when the engine names it, follows its reason.
            if (!string.IsNullOrEmpty(diagnostic.ItemHref)) reason += " (" + diagnostic.ItemHref + ")";
            chapter.Shown.Add(what);
            chapter.Shown.Add(reason);
            chapter.Report.Add(what);
            chapter.Report.Add(reason + " (" + diagnostic.Message + ")");
        }

        (List<string> Shown, List<string> Report) Chapter(int spineIndex)
        {
            if (!lines.TryGetValue(spineIndex, out var chapter)) lines.Add(spineIndex, chapter = ([], []));
            return chapter;
        }
    }
}
