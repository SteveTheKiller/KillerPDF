using System.Globalization;
using System.Text;
using System.Text.Json;

namespace KillerPdf.Engine.Documents;

/// <summary>The PDF output produced from reviewed OCR results.</summary>
public enum PdfOcrOutputMode
{
    /// <summary>Preserve the source image and add searchable text.</summary>
    SearchableImage,
    /// <summary>Preserve the exact source image and place hidden text over it.</summary>
    ExactImage,
    /// <summary>Create editable page content from recognition results.</summary>
    Editable
}

/// <summary>Validated recognition and preprocessing settings supplied to an OCR provider.</summary>
public sealed record PdfOcrOptions
{
    private readonly string[] _languages;

    /// <summary>Creates OCR settings for one or more recognition languages.</summary>
    public PdfOcrOptions(IEnumerable<string> languages,
        PdfOcrOutputMode outputMode = PdfOcrOutputMode.SearchableImage,
        bool deskew = true, bool correctOrientation = true,
        bool removeBackground = false, bool removeNoise = false,
        bool detectPageSegments = true)
    {
        ArgumentNullException.ThrowIfNull(languages);
        _languages = languages.Select(language => language?.Trim())
            .Where(language => !string.IsNullOrEmpty(language))
            .Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (_languages.Length == 0)
            throw new ArgumentException(
                "At least one OCR recognition language is required.", nameof(languages));
        if (_languages.Any(language => language.Length > 35
            || language.Any(character => !char.IsAsciiLetterOrDigit(character)
                && character is not ('-' or '_'))))
            throw new ArgumentException(
                "OCR language names may contain only letters, numbers, hyphens, and underscores.",
                nameof(languages));
        if (!Enum.IsDefined(outputMode))
            throw new ArgumentOutOfRangeException(nameof(outputMode));
        Languages = Array.AsReadOnly(_languages);
        OutputMode = outputMode;
        Deskew = deskew;
        CorrectOrientation = correctOrientation;
        RemoveBackground = removeBackground;
        RemoveNoise = removeNoise;
        DetectPageSegments = detectPageSegments;
    }

    /// <summary>Gets recognition languages in provider priority order.</summary>
    public IReadOnlyList<string> Languages { get; }
    /// <summary>Gets the requested PDF output mode.</summary>
    public PdfOcrOutputMode OutputMode { get; }
    /// <summary>Gets whether tilted page images should be straightened.</summary>
    public bool Deskew { get; }
    /// <summary>Gets whether page orientation should be detected and corrected.</summary>
    public bool CorrectOrientation { get; }
    /// <summary>Gets whether background shading should be removed.</summary>
    public bool RemoveBackground { get; }
    /// <summary>Gets whether image noise should be removed.</summary>
    public bool RemoveNoise { get; }
    /// <summary>Gets whether the provider should detect columns and other page segments.</summary>
    public bool DetectPageSegments { get; }
}

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

    /// <summary>Exports the complete recognition and review state as stable JSON.</summary>
    public string ExportJson() => JsonSerializer.Serialize(new PdfOcrReviewFile(1, _words));

    /// <summary>Restores a previously exported recognition and review state.</summary>
    public static PdfOcrReview ImportJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        PdfOcrReviewFile file = JsonSerializer.Deserialize<PdfOcrReviewFile>(json)
            ?? throw new JsonException("The OCR review file is empty.");
        if (file.Version != 1)
            throw new NotSupportedException($"OCR review file version {file.Version} is not supported.");
        return new PdfOcrReview(file.Words
            ?? throw new JsonException("The OCR review file has no words."));
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

    private sealed record PdfOcrReviewFile(int Version, PdfOcrWord[] Words);
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
    /// <summary>Recognizes each page independently with shared provider options.</summary>
    public static IReadOnlyList<PdfOcrBatchResult> Run(IEnumerable<PdfOcrBatchPage> pages,
        PdfOcrOptions options,
        Func<PdfOcrBatchPage, PdfOcrOptions, CancellationToken, PdfOcrReview> recognize,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(recognize);
        return Run(pages, (page, token) => recognize(page, options, token), cancellationToken);
    }

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
