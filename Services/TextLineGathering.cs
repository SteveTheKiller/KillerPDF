using System.Windows;

namespace KillerPDF.Services;

/// <summary>
/// Picks the words that make up the line a Text tool click lands on. Rectangles are in the reading
/// frame, where the text reads left to right and y grows downward.
/// </summary>
internal static class TextLineGathering
{
    /// <param name="Text">The word.</param>
    /// <param name="Bounds">Where the word sits.</param>
    /// <param name="Size">The point size of its first letter, or 0 when that is not known.</param>
    internal readonly record struct Candidate(string Text, Rect Bounds, double Size);

    /// <summary>Returns the indexes of the line's words, left to right, or none when there are no words.</summary>
    internal static IReadOnlyList<int> Gather(IReadOnlyList<Candidate> words, Point click)
    {
        double clickY = click.Y;
        List<int> line = [.. Enumerable.Range(0, words.Count)
            .Where(i => clickY >= words[i].Bounds.Top - 3 && clickY <= words[i].Bounds.Bottom + 3)
            .OrderBy(i => words[i].Bounds.Left)];   // strictly left-to-right

        if (line.Count == 0 && words.Count > 0)
        {
            // Try nearest line within 20px
            int nearest = Enumerable.Range(0, words.Count)
                .OrderBy(i => Math.Abs((words[i].Bounds.Top + words[i].Bounds.Bottom) / 2 - clickY))
                .First();
            double nearMidY = (words[nearest].Bounds.Top + words[nearest].Bounds.Bottom) / 2;
            line = [.. Enumerable.Range(0, words.Count)
                .Where(i => Math.Abs((words[i].Bounds.Top + words[i].Bounds.Bottom) / 2 - nearMidY) < 5)
                .OrderBy(i => words[i].Bounds.Left)];
        }

        // A recognized word's box follows its own letters, so a word with descenders hangs below its
        // neighbors. A click on that low strip still means the row, so the row comes from the word
        // under the click: every word whose band shares at least half the smaller height with it.
        if (line.Count > 0)
        {
            Rect row = words[line[NearestAcross(words, line, click.X)]].Bounds;
            line = [.. Enumerable.Range(0, words.Count)
                .Where(i => line.Contains(i) || SharesRow(words[i].Bounds, row))
                .OrderBy(i => words[i].Bounds.Left)];
        }

        // Narrow to the contiguous run of words around the click. Words at the same Y in a
        // second column are separated by a large horizontal gap, so stop there instead of
        // merging both columns into one edit (the "weird text" / page-spanning edit).
        if (line.Count > 1)
        {
            int ci = NearestAcross(words, line, click.X);
            // A drop numeral or chapter number beside the line is tall enough to share its rows. It is not
            // part of the line's text, and lending the line its size and origin would be as wrong, so only
            // words close to the nearest word's size belong to the line.
            int anchor = line[ci];
            line = [.. line.Where(i => i == anchor || SimilarSize(words[i].Size, words[anchor].Size))];
            ci = line.IndexOf(anchor);
            double gapMax = Math.Max(words[line[ci]].Bounds.Height * 1.5, 24);   // word spacing is small; a column gap is large
            int lo = ci, hi = ci;
            while (lo > 0 && words[line[lo]].Bounds.Left - words[line[lo - 1]].Bounds.Right <= gapMax) lo--;
            while (hi < line.Count - 1 && words[line[hi + 1]].Bounds.Left - words[line[hi]].Bounds.Right <= gapMax) hi++;
            line = line.GetRange(lo, hi - lo + 1);
        }
        return line;
    }

    private static int NearestAcross(IReadOnlyList<Candidate> words, List<int> line, double x)
    {
        int nearest = 0; double bestDx = double.MaxValue;
        for (int i = 0; i < line.Count; i++)
        {
            Rect r = words[line[i]].Bounds;
            double dx = x < r.Left ? r.Left - x : x > r.Right ? x - r.Right : 0;
            if (dx < bestDx) { bestDx = dx; nearest = i; }
        }
        return nearest;
    }

    private static bool SharesRow(Rect word, Rect row)
    {
        double overlap = Math.Min(word.Bottom, row.Bottom) - Math.Max(word.Top, row.Top);
        return overlap >= Math.Min(word.Height, row.Height) * 0.5;
    }

    // Body text with a footnote marker or a larger figure stays one line; a numeral at three times the
    // body size does not. A size that is not known never excludes a word.
    private const double MaxSizeRatio = 1.8;

    private static bool SimilarSize(double size, double anchorSize) =>
        size <= 0 || anchorSize <= 0
        || (size <= anchorSize * MaxSizeRatio && anchorSize <= size * MaxSizeRatio);
}
