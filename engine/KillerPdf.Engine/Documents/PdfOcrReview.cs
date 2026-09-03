using System.Globalization;
using System.Text;

namespace KillerPdf.Engine.Documents;

/// <summary>The review state of one recognized word.</summary>
public enum PdfOcrWordStatus
{
    /// <summary>The recognition result has not been reviewed.</summary>
    Pending,
    /// <summary>The recognized text was corrected.</summary>
    Corrected,
    /// <summary>The recognition result was deliberately accepted without correction.</summary>
    Ignored
}

/// <summary>A recognized word with stable page order, geometry, confidence, and review state.</summary>
public sealed record PdfOcrWord
{
    /// <summary>Creates a recognized word.</summary>
    public PdfOcrWord(string id, int pageIndex, int sequence, string originalText, string text,
        PdfContentBounds boundingBox, double confidence, string? language = null,
        PdfOcrWordStatus status = PdfOcrWordStatus.Pending)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("A word ID is required.", nameof(id));
        if (pageIndex < 0) throw new ArgumentOutOfRangeException(nameof(pageIndex));
        if (sequence < 0) throw new ArgumentOutOfRangeException(nameof(sequence));
        ArgumentNullException.ThrowIfNull(originalText);
        ArgumentNullException.ThrowIfNull(text);
        if (!double.IsFinite(confidence) || confidence is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(confidence));
        if (!Enum.IsDefined(status)) throw new ArgumentOutOfRangeException(nameof(status));
        Id = id;
        PageIndex = pageIndex;
        Sequence = sequence;
        OriginalText = originalText;
        Text = text;
        BoundingBox = boundingBox;
        Confidence = confidence;
        Language = language;
        Status = status;
    }

    /// <summary>Gets the stable recognition ID.</summary>
    public string Id { get; }
    /// <summary>Gets the zero-based page index.</summary>
    public int PageIndex { get; }
    /// <summary>Gets the reading-order position on the page.</summary>
    public int Sequence { get; }
    /// <summary>Gets the original recognized text.</summary>
    public string OriginalText { get; }
    /// <summary>Gets the current reviewed text.</summary>
    public string Text { get; }
    /// <summary>Gets the word bounds in PDF points.</summary>
    public PdfContentBounds BoundingBox { get; }
    /// <summary>Gets recognition confidence from zero through one.</summary>
    public double Confidence { get; }
    /// <summary>Gets the recognition language when known.</summary>
    public string? Language { get; }
    /// <summary>Gets the review state.</summary>
    public PdfOcrWordStatus Status { get; }

    internal PdfOcrWord WithText(string text, PdfOcrWordStatus status) =>
        new(Id, PageIndex, Sequence, OriginalText, text, BoundingBox, Confidence, Language, status);
}

/// <summary>An immutable, reviewable OCR result.</summary>
public sealed class PdfOcrReview
{
    private readonly PdfOcrWord[] _words;

    /// <summary>Creates a review from recognized words.</summary>
    public PdfOcrReview(IEnumerable<PdfOcrWord> words)
    {
        ArgumentNullException.ThrowIfNull(words);
        _words = words.OrderBy(word => word.PageIndex).ThenBy(word => word.Sequence).ToArray();
        if (_words.Select(word => word.Id).Distinct(StringComparer.Ordinal).Count() != _words.Length)
            throw new ArgumentException("OCR word IDs must be unique.", nameof(words));
        Words = Array.AsReadOnly(_words);
    }

    /// <summary>Gets words in page reading order.</summary>
    public IReadOnlyList<PdfOcrWord> Words { get; }

    /// <summary>Gets pending words at or below the supplied confidence threshold.</summary>
    public IReadOnlyList<PdfOcrWord> GetLowConfidenceWords(double threshold)
    {
        if (!double.IsFinite(threshold) || threshold is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(threshold));
        return Array.AsReadOnly(_words
            .Where(word => word.Status == PdfOcrWordStatus.Pending && word.Confidence <= threshold)
            .ToArray());
    }

    /// <summary>Returns a new review with one word corrected.</summary>
    public PdfOcrReview Correct(string id, string replacement)
    {
        if (string.IsNullOrWhiteSpace(replacement))
            throw new ArgumentException("Replacement text is required.", nameof(replacement));
        return Change(id, word => word.WithText(replacement, PdfOcrWordStatus.Corrected));
    }

    /// <summary>Returns a new review with one word accepted without correction.</summary>
    public PdfOcrReview Ignore(string id) =>
        Change(id, word => word.WithText(word.Text, PdfOcrWordStatus.Ignored));

    /// <summary>Returns a new review with every exact text match corrected.</summary>
    public PdfOcrReview ReplaceAll(string text, string replacement,
        StringComparison comparison = StringComparison.Ordinal)
    {
        if (string.IsNullOrEmpty(text)) throw new ArgumentException("Text is required.", nameof(text));
        if (string.IsNullOrWhiteSpace(replacement))
            throw new ArgumentException("Replacement text is required.", nameof(replacement));
        return new PdfOcrReview(_words.Select(word => string.Equals(word.Text, text, comparison)
            ? word.WithText(replacement, PdfOcrWordStatus.Corrected)
            : word));
    }

    /// <summary>Exports reviewed text with one line per page.</summary>
    public string ExportText() => string.Join(Environment.NewLine, _words
        .GroupBy(word => word.PageIndex)
        .Select(page => string.Join(' ', page.Select(word => word.Text))));

    /// <summary>Exports a stable CSV review report.</summary>
    public string ExportReport()
    {
        var output = new StringBuilder("Page,Word,Original,Current,Confidence,Language,Status\n");
        foreach (PdfOcrWord word in _words)
        {
            output.Append(word.PageIndex + 1).Append(',').Append(word.Sequence).Append(',')
                .Append(Csv(word.OriginalText)).Append(',').Append(Csv(word.Text)).Append(',')
                .Append(word.Confidence.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
                .Append(Csv(word.Language ?? string.Empty)).Append(',').Append(word.Status).Append('\n');
        }
        return output.ToString();
    }

    private PdfOcrReview Change(string id, Func<PdfOcrWord, PdfOcrWord> change)
    {
        ArgumentNullException.ThrowIfNull(id);
        int index = Array.FindIndex(_words, word => string.Equals(word.Id, id, StringComparison.Ordinal));
        if (index < 0) throw new KeyNotFoundException($"OCR word '{id}' was not found.");
        PdfOcrWord[] changed = (PdfOcrWord[])_words.Clone();
        changed[index] = change(changed[index]);
        return new PdfOcrReview(changed);
    }

    private static string Csv(string value) =>
        value.IndexOfAny([',', '"', '\r', '\n']) < 0 ? value : $"\"{value.Replace("\"", "\"\"")}\"";
}

/// <summary>One source page supplied to an OCR provider.</summary>
public sealed record PdfOcrBatchPage
{
    /// <summary>Creates a validated OCR page input.</summary>
    public PdfOcrBatchPage(string sourceName, int pageIndex, ReadOnlyMemory<byte> source)
    {
        if (string.IsNullOrWhiteSpace(sourceName))
            throw new ArgumentException("A source name is required.", nameof(sourceName));
        if (pageIndex < 0) throw new ArgumentOutOfRangeException(nameof(pageIndex));
        SourceName = sourceName;
        PageIndex = pageIndex;
        Source = source.ToArray();
    }

    /// <summary>Gets the source file name.</summary>
    public string SourceName { get; }
    /// <summary>Gets the zero-based source page index.</summary>
    public int PageIndex { get; }
    /// <summary>Gets an isolated copy of the source page buffer.</summary>
    public ReadOnlyMemory<byte> Source { get; }
}

/// <summary>The isolated OCR result for one source page.</summary>
public sealed record PdfOcrBatchResult(PdfOcrBatchPage Input, PdfOcrReview? Review,
    string? Error, bool WasCanceled)
{
    /// <summary>Gets whether recognition completed.</summary>
    public bool Succeeded => Review is not null && Error is null && !WasCanceled;
}

/// <summary>Runs OCR page recognition with source preservation, failure isolation, and cancellation.</summary>
public static class PdfOcrBatchRunner
{
    /// <summary>Recognizes each page independently.</summary>
    public static IReadOnlyList<PdfOcrBatchResult> Run(IEnumerable<PdfOcrBatchPage> pages,
        Func<PdfOcrBatchPage, CancellationToken, PdfOcrReview> recognize,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(recognize);
        var results = new List<PdfOcrBatchResult>();
        foreach (PdfOcrBatchPage suppliedPage in pages)
        {
            if (cancellationToken.IsCancellationRequested) break;
            var page = new PdfOcrBatchPage(suppliedPage.SourceName, suppliedPage.PageIndex,
                suppliedPage.Source);
            try
            {
                PdfOcrReview review = recognize(page, cancellationToken)
                    ?? throw new InvalidOperationException("The OCR provider returned no review result.");
                results.Add(new PdfOcrBatchResult(page, review, null, false));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                results.Add(new PdfOcrBatchResult(page, null, null, true));
                break;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException
                and not StackOverflowException and not AccessViolationException)
            {
                results.Add(new PdfOcrBatchResult(page, null, exception.Message, false));
            }
        }
        return Array.AsReadOnly(results.ToArray());
    }
}
