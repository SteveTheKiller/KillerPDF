namespace KillerPdf.Engine.Documents;

/// <summary>A recognized OCR word in top-left-origin pixel coordinates.</summary>
public sealed record PdfOcrPixelWord(
    string Text, float Confidence, int Left, int Top, int Right, int Bottom);

/// <summary>A normalized OCR result with immutable word data.</summary>
public sealed class PdfOcrResult
{
    /// <summary>Creates a result while preserving recognizer-provided page text.</summary>
    public PdfOcrResult(string text, float meanConfidence,
        IEnumerable<PdfOcrPixelWord> words)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(words);
        if (!float.IsFinite(meanConfidence) || meanConfidence is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(meanConfidence));
        PdfOcrPixelWord[] copied = words.ToArray();
        if (copied.Any(word => word is null))
            throw new ArgumentException("An OCR result word is null.", nameof(words));
        if (copied.Any(word => word.Text is null
            || !float.IsFinite(word.Confidence) || word.Confidence is < 0 or > 1
            || word.Left < 0 || word.Top < 0
            || word.Right <= word.Left || word.Bottom <= word.Top))
            throw new ArgumentException(
                "OCR result words must have text, valid confidence, and positive pixel bounds.",
                nameof(words));
        Text = text;
        MeanConfidence = meanConfidence;
        Words = Array.AsReadOnly(copied);
    }

    /// <summary>Gets the recognized page text.</summary>
    public string Text { get; }
    /// <summary>Gets mean recognition confidence.</summary>
    public float MeanConfidence { get; }
    /// <summary>Gets recognized words and their pixel bounds.</summary>
    public IReadOnlyList<PdfOcrPixelWord> Words { get; }

    /// <summary>Orders words for reading and derives consolidated text and confidence.</summary>
    public static PdfOcrResult FromWords(IEnumerable<PdfOcrPixelWord> words,
        int lineTolerance = 8)
    {
        ArgumentNullException.ThrowIfNull(words);
        if (lineTolerance < 0) throw new ArgumentOutOfRangeException(nameof(lineTolerance));
        var lines = new List<(int Top, List<PdfOcrPixelWord> Words)>();
        foreach (PdfOcrPixelWord word in words.OrderBy(word => word.Top)
            .ThenBy(word => word.Left))
        {
            int line = lines.FindIndex(candidate =>
                Math.Abs(candidate.Top - word.Top) <= lineTolerance);
            if (line < 0) lines.Add((word.Top, [word]));
            else lines[line].Words.Add(word);
        }
        foreach ((_, List<PdfOcrPixelWord> line) in lines)
            line.Sort((left, right) => left.Left.CompareTo(right.Left));
        PdfOcrPixelWord[] ordered = [.. lines.SelectMany(line => line.Words)];
        float confidence = ordered.Length == 0
            ? 0 : (float)ordered.Average(word => word.Confidence);
        return new PdfOcrResult(string.Join(Environment.NewLine,
            lines.Select(line => string.Join(' ', line.Words.Select(word => word.Text)))),
            confidence, ordered);
    }
}
