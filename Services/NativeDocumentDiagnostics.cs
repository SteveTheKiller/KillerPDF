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

/// <summary>The string keys of the notice bar's message and of the diagnostics dialog's title, intro and empty note.</summary>
internal sealed record NativeNoticeWording(string Notice, string Title, string Intro, string None);

/// <summary>Turns what a native book left out or ignored into localized rows, one per chapter.</summary>
internal static class NativeDocumentDiagnostics
{
    /// <summary>
    /// The DjVu notice codes the reader is told about; each has a reason key in all locales. Included components a
    /// bundle does not carry leave shared shapes or annotations out of the pages that use them; a newer page version
    /// may not render exactly; text that is not UTF-8 searches and copies with placeholders. The rest only describe
    /// what the decoder supports (Smmr, IW44, JPEG layers and thumbnails, whose refused pages say so where they are
    /// drawn) or what the directory index lacks without changing the pages, and an indirect or unknown directory opens
    /// no pages at all, so the document never reaches the viewer.
    /// </summary>
    internal static IReadOnlyList<string> RaisingDjvuCodes { get; } =
        ["IncludedComponentUnsupported", "PageVersionUnsupported", "TextEncodingInvalid", "UnrecognizedChunkSkipped"];

    internal static bool RaisesNotice(KillerPdf.Engine.Formats.Djvu.DjvuDiagnostic notice)
        => RaisingDjvuCodes.Contains(notice.Code);

    /// <summary>The string key for a DjVu notice's reason.</summary>
    internal static string DjvuReasonKey(string code) => "Str_Djvu_Reason_" + code;

    /// <summary>The bar and dialog wording for a document format: a DjVu file is a document, an EPUB a book.</summary>
    internal static NativeNoticeWording WordingFor(KillerPdf.Engine.Documents.PdfDocumentFormat format) =>
        format == KillerPdf.Engine.Documents.PdfDocumentFormat.Djvu
            ? new("Str_Djvu_Diagnostics_Notice", "Str_Djvu_Diagnostics_Title", "Str_Djvu_Diagnostics_Intro",
                "Str_Djvu_Diagnostics_None")
            : new("Str_Epub_Diagnostics_Notice", "Str_Epub_Diagnostics_Title", "Str_Epub_Diagnostics_Intro",
                "Str_Epub_Diagnostics_None");

    // The whole document's row first, then one per page in page order, each line a localized reason shown once however
    // many notices share it (one per unknown chunk name); the copied
    // report adds the engine's English message.
    private static IReadOnlyList<NativeDiagnosticRow> BuildDjvu(NativeWorkingDocument document, Func<string, string> loc)
        => document.DjvuNotices.GroupBy(notice => notice.PageIndex).Select(group => new NativeDiagnosticRow(
                group.Key is int page ? string.Format(loc("Str_Djvu_Diagnostics_Page"), page + 1)
                    : loc("Str_Djvu_Diagnostics_Document"),
                group.Select(notice => loc(DjvuReasonKey(notice.Code))).Distinct().ToArray())
            {
                ReportLines = group.Select(notice => loc(DjvuReasonKey(notice.Code)) + " (" + notice.Message + ")").ToArray()
            }).ToArray();

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
        if (document.Format == KillerPdf.Engine.Documents.PdfDocumentFormat.Djvu) return BuildDjvu(document, loc);
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
