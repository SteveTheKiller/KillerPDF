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
        Text = text;
        MeanConfidence = meanConfidence;
        Words = Array.AsReadOnly(words.ToArray());
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
        PdfOcrPixelWord[] ordered = [.. words.OrderBy(word => word.Top)
            .ThenBy(word => word.Left)];
        Array.Sort(ordered, (left, right) =>
        {
            if (Math.Abs(left.Top - right.Top) < lineTolerance)
                return left.Left.CompareTo(right.Left);
            int top = left.Top.CompareTo(right.Top);
            return top != 0 ? top : left.Left.CompareTo(right.Left);
        });
        float confidence = ordered.Length == 0
            ? 0 : (float)ordered.Average(word => word.Confidence);
        return new PdfOcrResult(string.Join(Environment.NewLine,
            ordered.Select(word => word.Text)), confidence, ordered);
    }
}
