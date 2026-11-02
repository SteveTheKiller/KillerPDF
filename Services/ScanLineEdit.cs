using System.Windows;
using KillerPdf.Engine.Documents;

namespace KillerPDF.Services;

/// <summary>What an edit of a scanned line changes: the words' area, the slightly larger area to
/// cover so no scanned edge shows, and the text set over it.</summary>
internal sealed record ScanLineEditPlan(Rect Covered, Rect Cover, string Text);

/// <summary>Limits an edit of a scanned line to the words it changes, so the rest stay as scanned.</summary>
internal static class ScanLineEdit
{
    /// <summary>
    /// Plans the edit of a line from its words and their boxes, or returns null when nothing changed.
    /// Replacement text too wide for the gap before the next word takes over the rest of the line,
    /// because setting it there would run over scanned words.
    /// </summary>
    internal static ScanLineEditPlan? Plan(IReadOnlyList<string> words, IReadOnlyList<Rect> boxes,
        string typed, Func<string, double> measure)
    {
        ArgumentNullException.ThrowIfNull(words);
        ArgumentNullException.ThrowIfNull(boxes);
        ArgumentNullException.ThrowIfNull(measure);
        if (typed.Contains('\n') || typed.Contains('\r'))
            return Planned(boxes, 0, boxes.Count - 1, typed);
        if (ScanEditSpan.Diff(words, typed) is not { } span) return null;
        int next = span.Last + 1;
        if (next < boxes.Count && measure(span.Replacement) > boxes[next].Left - boxes[span.First].Left)
        {
            string[] edited = typed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            return Planned(boxes, span.First, boxes.Count - 1, string.Join(' ', edited[span.First..]));
        }
        return Planned(boxes, span.First, span.Last, span.Replacement);
    }

    // The cover reaches past the words so no scanned edge shows, but never into a neighbor that stays.
    private const double CoverMargin = 3;

    private static ScanLineEditPlan Planned(IReadOnlyList<Rect> boxes, int first, int last, string text)
    {
        Rect covered = Span(boxes, first, last);
        Rect cover = covered;
        cover.Inflate(CoverMargin, CoverMargin);
        double left = first > 0 ? Math.Min(covered.Left, Math.Max(cover.Left, boxes[first - 1].Right)) : cover.Left;
        double right = last + 1 < boxes.Count ? Math.Max(covered.Right, Math.Min(cover.Right, boxes[last + 1].Left)) : cover.Right;
        cover = new Rect(left, cover.Top, right - left, cover.Height);
        return new ScanLineEditPlan(covered, cover, text);
    }

    /// <summary>A word's box on the canvas as a word on the page bitmap.</summary>
    internal static PdfOcrPixelWord ToPixelWord(string text, Rect box, double pixelsX, double pixelsY) =>
        new(text, 1, (int)Math.Floor(box.Left * pixelsX), (int)Math.Floor(box.Top * pixelsY),
            (int)Math.Ceiling(box.Right * pixelsX), (int)Math.Ceiling(box.Bottom * pixelsY));

    private static Rect Span(IReadOnlyList<Rect> boxes, int first, int last)
    {
        Rect covered = boxes[first];
        for (int index = first + 1; index <= last; index++) covered.Union(boxes[index]);
        return covered;
    }
}
