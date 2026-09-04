using System.Globalization;
using System.Text;
using System.Text.Json;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Fonts;

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
    private static readonly HashSet<string> MacroSettingNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "languages", "outputMode", "deskew", "correctOrientation",
        "removeBackground", "removeNoise", "detectPageSegments"
    };
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

    /// <summary>Reads validated OCR provider options from an OCR macro step.</summary>
    public static PdfOcrOptions FromMacroStep(PdfMacroStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        if (step.Operation != PdfMacroOperation.Ocr)
            throw new ArgumentException("The macro step is not an OCR operation.", nameof(step));
        IReadOnlyDictionary<string, string> settings = step.Settings
            ?? new Dictionary<string, string>();
        string? unknown = settings.Keys.FirstOrDefault(key => !MacroSettingNames.Contains(key));
        if (unknown is not null)
            throw new ArgumentException($"Unknown OCR macro setting '{unknown}'.", nameof(step));
        string languages = Value("languages", "en-US");
        string mode = Value("outputMode", "searchable-image");
        PdfOcrOutputMode outputMode = mode.ToLowerInvariant() switch
        {
            "searchable-image" or "searchableimage" => PdfOcrOutputMode.SearchableImage,
            "exact-image" or "exactimage" => PdfOcrOutputMode.ExactImage,
            "editable" => PdfOcrOutputMode.Editable,
            _ => throw new ArgumentException(
                "OCR outputMode must be searchable-image, exact-image, or editable.", nameof(step))
        };
        return new PdfOcrOptions(
            languages.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            outputMode,
            Boolean("deskew", true),
            Boolean("correctOrientation", true),
            Boolean("removeBackground", false),
            Boolean("removeNoise", false),
            Boolean("detectPageSegments", true));

        string Value(string name, string fallback) =>
            settings.TryGetValue(name, out string? value) ? value : fallback;

        bool Boolean(string name, bool fallback)
        {
            if (!settings.TryGetValue(name, out string? value)) return fallback;
            if (bool.TryParse(value, out bool parsed)) return parsed;
            throw new ArgumentException(
                $"OCR macro setting '{name}' must be true or false.", nameof(step));
        }
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

/// <summary>Recognition quality and review counts for one page.</summary>
public sealed record PdfOcrPageAccuracy(
    int PageIndex,
    int WordCount,
    double AverageConfidence,
    int LowConfidenceCount,
    int PendingCount,
    int CorrectedCount,
    int IgnoredCount,
    int EmptyTextCount);

/// <summary>A confidence and review summary for an OCR result.</summary>
public sealed record PdfOcrAccuracyReport
{
    /// <summary>Gets the low-confidence threshold used by the report.</summary>
    public double LowConfidenceThreshold { get; init; }
    /// <summary>Gets page summaries in page order.</summary>
    public IReadOnlyList<PdfOcrPageAccuracy> Pages { get; init; } = [];
    /// <summary>Gets the total recognized word count.</summary>
    public int WordCount => Pages.Sum(page => page.WordCount);
    /// <summary>Gets the weighted average recognition confidence.</summary>
    public double AverageConfidence => WordCount == 0 ? 0
        : Pages.Sum(page => page.AverageConfidence * page.WordCount) / WordCount;
    /// <summary>Gets whether low-confidence, pending, or empty results need attention.</summary>
    public bool HasWarnings => Pages.Any(page => page.LowConfidenceCount > 0
        || page.PendingCount > 0 || page.EmptyTextCount > 0);

    /// <summary>Exports the accuracy summary as stable JSON without recognized text.</summary>
    public string ToJson(bool indented = false) => JsonSerializer.Serialize(new
    {
        Version = 1,
        LowConfidenceThreshold,
        WordCount,
        AverageConfidence,
        HasWarnings,
        Pages
    }, new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = indented
    });

    /// <summary>Exports page-level accuracy and review counts as CSV.</summary>
    public string ToCsv()
    {
        var output = new StringBuilder(
            "Page,Words,AverageConfidence,LowConfidence,Pending,Corrected,Ignored,Empty\n");
        foreach (PdfOcrPageAccuracy page in Pages)
            output.Append(page.PageIndex + 1).Append(',')
                .Append(page.WordCount).Append(',')
                .Append(page.AverageConfidence.ToString("0.####", CultureInfo.InvariantCulture))
                .Append(',').Append(page.LowConfidenceCount)
                .Append(',').Append(page.PendingCount)
                .Append(',').Append(page.CorrectedCount)
                .Append(',').Append(page.IgnoredCount)
                .Append(',').Append(page.EmptyTextCount).Append('\n');
        return output.ToString();
    }
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

    /// <summary>Builds page and document confidence and review summaries.</summary>
    public PdfOcrAccuracyReport CreateAccuracyReport(double lowConfidenceThreshold)
    {
        if (!double.IsFinite(lowConfidenceThreshold) || lowConfidenceThreshold is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(lowConfidenceThreshold));
        PdfOcrPageAccuracy[] pages = [.. _words.GroupBy(word => word.PageIndex).Select(group =>
            new PdfOcrPageAccuracy(group.Key, group.Count(), group.Average(word => word.Confidence),
                group.Count(word => word.Confidence <= lowConfidenceThreshold),
                group.Count(word => word.Status == PdfOcrWordStatus.Pending),
                group.Count(word => word.Status == PdfOcrWordStatus.Corrected),
                group.Count(word => word.Status == PdfOcrWordStatus.Ignored),
                group.Count(word => string.IsNullOrWhiteSpace(word.Text))))];
        return new PdfOcrAccuracyReport
        {
            LowConfidenceThreshold = lowConfidenceThreshold,
            Pages = Array.AsReadOnly(pages)
        };
    }

    /// <summary>Writes reviewed words as invisible searchable text over the source pages.</summary>
    public byte[] WriteSearchableText(PdfDocument document, TrueTypeFont font)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(font);
        if (_words.Length == 0) return document.Source.ToArray();
        IReadOnlyList<PdfPageBoxInformation> pages = PdfPageBoxInformation.Read(document);
        PdfOcrWord? invalid = _words.FirstOrDefault(word => word.PageIndex >= pages.Count);
        if (invalid is not null)
            throw new ArgumentOutOfRangeException(nameof(document),
                $"OCR word '{invalid.Id}' refers to a page outside the document.");
        var editor = new PdfIncrementalPageEditor(document);
        foreach (IGrouping<int, PdfOcrWord> pageWords in _words
            .Where(word => !string.IsNullOrWhiteSpace(word.Text))
            .GroupBy(word => word.PageIndex))
        {
            PdfPageBoxBounds crop = pages[pageWords.Key].CropBox;
            var content = new PdfContentStreamBuilder();
            foreach (PdfOcrWord word in pageWords)
            {
                double fontSize = word.BoundingBox.Height;
                if (!double.IsFinite(fontSize) || fontSize <= 0)
                    throw new InvalidOperationException(
                        $"OCR word '{word.Id}' has invalid text-layer geometry.");
                double naturalWidth = font.MapText(word.Text)
                    .Sum(mapping => font.GetPdfAdvanceWidth(mapping.Glyph)) / 1000d * fontSize;
                double horizontalScale = naturalWidth > 0
                    ? word.BoundingBox.Width / naturalWidth * 100 : 100;
                if (!double.IsFinite(horizontalScale) || horizontalScale <= 0)
                    throw new InvalidOperationException(
                        $"OCR word '{word.Id}' has invalid text-layer geometry.");
                content.BeginText().SetFont(font, fontSize)
                    .SetTextRenderingMode(PdfTextRenderingMode.Invisible)
                    .SetHorizontalTextScale(horizontalScale)
                    .SetTextMatrix(1, 0, 0, 1,
                        word.BoundingBox.Left, word.BoundingBox.Bottom)
                    .ShowUnicodeText(word.Text).EndText();
            }
            editor.AppendPageContent(pageWords.Key, crop.Width, crop.Height, content);
        }
        return editor.Build();
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

    /// <summary>Accepts every pending word at or above a confidence threshold.</summary>
    public PdfOcrReview AcceptConfidentWords(double threshold)
    {
        if (!double.IsFinite(threshold) || threshold is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(threshold));
        return new PdfOcrReview(_words.Select(word =>
            word.Status == PdfOcrWordStatus.Pending && word.Confidence >= threshold
                ? word.WithText(word.Text, PdfOcrWordStatus.Ignored)
                : word));
    }

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

/// <summary>A data-safe aggregate report for an OCR page batch.</summary>
public sealed record PdfOcrBatchReport
{
    /// <summary>Creates a report from an isolated batch result prefix.</summary>
    public PdfOcrBatchReport(int totalPageCount, IEnumerable<PdfOcrBatchResult> results)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(totalPageCount);
        ArgumentNullException.ThrowIfNull(results);
        PdfOcrBatchResult[] values = results.ToArray();
        if (values.Length > totalPageCount)
            throw new ArgumentException(
                "OCR batch results cannot exceed the supplied page count.", nameof(results));
        TotalPageCount = totalPageCount;
        Results = Array.AsReadOnly(values);
    }

    /// <summary>Gets the number of supplied pages.</summary>
    public int TotalPageCount { get; }
    /// <summary>Gets completed, failed, or canceled page results.</summary>
    public IReadOnlyList<PdfOcrBatchResult> Results { get; }
    /// <summary>Gets the number of successful pages.</summary>
    public int SucceededCount => Results.Count(result => result.Succeeded);
    /// <summary>Gets the number of failed pages.</summary>
    public int FailedCount => Results.Count(result => result.Error is not null);
    /// <summary>Gets the number of canceled pages.</summary>
    public int CanceledCount => Results.Count(result => result.WasCanceled);
    /// <summary>Gets pages not reached after cancellation.</summary>
    public int UnprocessedCount => TotalPageCount - Results.Count;

    /// <summary>Exports outcomes without page buffers or recognized text.</summary>
    public string ToJson(bool indented = false) => JsonSerializer.Serialize(new
    {
        Version = 1,
        TotalPageCount,
        SucceededCount,
        FailedCount,
        CanceledCount,
        UnprocessedCount,
        Results = Results.Select(result => new
        {
            result.Input.SourceName,
            result.Input.PageIndex,
            result.Succeeded,
            result.WasCanceled,
            result.Error,
            WordCount = result.Review?.Words.Count
        })
    }, new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = indented
    });
}

/// <summary>Runs OCR page recognition with source preservation, failure isolation, and cancellation.</summary>
public static class PdfOcrBatchRunner
{
    /// <summary>Recognizes a page batch and returns aggregate, data-safe outcomes.</summary>
    public static PdfOcrBatchReport RunReport(IEnumerable<PdfOcrBatchPage> pages,
        PdfOcrOptions options,
        Func<PdfOcrBatchPage, PdfOcrOptions, CancellationToken, PdfOcrReview> recognize,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pages);
        PdfOcrBatchPage[] supplied = pages.ToArray();
        return new PdfOcrBatchReport(supplied.Length,
            Run(supplied, options, recognize, cancellationToken));
    }

    /// <summary>Recognizes a page batch and returns aggregate, data-safe outcomes.</summary>
    public static PdfOcrBatchReport RunReport(IEnumerable<PdfOcrBatchPage> pages,
        Func<PdfOcrBatchPage, CancellationToken, PdfOcrReview> recognize,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pages);
        PdfOcrBatchPage[] supplied = pages.ToArray();
        return new PdfOcrBatchReport(supplied.Length,
            Run(supplied, recognize, cancellationToken));
    }

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
