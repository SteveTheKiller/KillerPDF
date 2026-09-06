using System.Text;

namespace KillerPdf.Engine.Documents;

/// <summary>One visual OCR candidate for sequence decoding.</summary>
public readonly record struct PdfOcrLanguageCandidate(string Label, double Score);

/// <summary>A bounded character-transition model for deterministic OCR sequence decoding.</summary>
public sealed class PdfOcrLanguageModel
{
    private static readonly byte[] Magic = "KPLM1\0"u8.ToArray();
    private const int MaximumCharacters = 10_000_000;
    private const int MaximumLabels = 65_536;
    internal const int MaximumModelBytes = 64 * 1024 * 1024;
    private const int MaximumSequenceLength = 4_096;
    private const int MaximumCandidatesPerPosition = 64;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly Dictionary<string, int> _labels;
    private readonly Dictionary<(int Previous, int Current), int> _counts;
    private readonly int[] _totals;

    private PdfOcrLanguageModel(Dictionary<string, int> labels,
        Dictionary<(int Previous, int Current), int> counts, int[] totals)
    {
        _labels = labels;
        _counts = counts;
        _totals = totals;
    }

    /// <summary>Trains a deterministic character-transition model from text.</summary>
    public static PdfOcrLanguageModel Train(IEnumerable<string> texts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(texts);
        var labels = new SortedSet<string>(StringComparer.Ordinal) { string.Empty };
        var observed = new Dictionary<(string Previous, string Current), int>();
        int characterCount = 0;
        foreach (string text in texts)
        {
            ArgumentNullException.ThrowIfNull(text);
            cancellationToken.ThrowIfCancellationRequested();
            string previous = string.Empty;
            foreach (Rune rune in text.EnumerateRunes())
            {
                if ((characterCount & 0x3FFF) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                if (Rune.IsControl(rune) || Rune.IsWhiteSpace(rune))
                {
                    previous = string.Empty;
                    continue;
                }
                if (++characterCount > MaximumCharacters)
                    throw new ArgumentException(
                        "The OCR language-model training text exceeds the character limit.",
                        nameof(texts));
                string label = rune.ToString();
                labels.Add(label);
                var transition = (previous, label);
                observed[transition] = checked(observed.GetValueOrDefault(transition) + 1);
                previous = label;
            }
        }
        if (observed.Count == 0)
            throw new ArgumentException(
                "At least one OCR language-model training sequence is required.", nameof(texts));
        if (labels.Count > MaximumLabels)
            throw new ArgumentException(
                "The OCR language-model training text has too many labels.", nameof(texts));

        string[] ordered = [.. labels];
        var indexes = ordered.Select((label, index) => (label, index))
            .ToDictionary(item => item.label, item => item.index, StringComparer.Ordinal);
        var counts = new Dictionary<(int Previous, int Current), int>();
        var totals = new int[ordered.Length];
        foreach (((string previous, string current), int count) in observed)
        {
            int previousIndex = indexes[previous];
            int currentIndex = indexes[current];
            counts.Add((previousIndex, currentIndex), count);
            totals[previousIndex] = checked(totals[previousIndex] + count);
        }
        return new PdfOcrLanguageModel(indexes, counts, totals);
    }

    /// <summary>Combines compatible character-transition models deterministically.</summary>
    public static PdfOcrLanguageModel Combine(IEnumerable<PdfOcrLanguageModel> models)
    {
        ArgumentNullException.ThrowIfNull(models);
        PdfOcrLanguageModel[] supplied = models.ToArray();
        if (supplied.Length is < 1 or > 16 || supplied.Any(model => model is null))
            throw new ArgumentException(
                "One through sixteen OCR language models are required.", nameof(models));
        var labels = new SortedSet<string>(StringComparer.Ordinal) { string.Empty };
        var observed = new Dictionary<(string Previous, string Current), int>();
        int totalCount = 0;
        foreach (PdfOcrLanguageModel model in supplied)
        {
            string[] modelLabels = [.. model._labels
                .OrderBy(item => item.Value).Select(item => item.Key)];
            foreach (string label in modelLabels) labels.Add(label);
            foreach (((int previous, int current), int count) in model._counts)
            {
                totalCount = checked(totalCount + count);
                if (totalCount > MaximumCharacters)
                    throw new ArgumentException(
                        "The combined OCR language models exceed the character limit.",
                        nameof(models));
                var transition = (modelLabels[previous], modelLabels[current]);
                observed[transition] = checked(
                    observed.GetValueOrDefault(transition) + count);
            }
        }
        if (labels.Count > MaximumLabels)
            throw new ArgumentException(
                "The combined OCR language models have too many labels.", nameof(models));
        string[] ordered = [.. labels];
        var indexes = ordered.Select((label, index) => (label, index))
            .ToDictionary(item => item.label, item => item.index, StringComparer.Ordinal);
        var counts = new Dictionary<(int Previous, int Current), int>(observed.Count);
        var totals = new int[ordered.Length];
        foreach (((string previous, string current), int count) in observed)
        {
            int previousIndex = indexes[previous];
            int currentIndex = indexes[current];
            counts.Add((previousIndex, currentIndex), count);
            totals[previousIndex] = checked(totals[previousIndex] + count);
        }
        return new PdfOcrLanguageModel(indexes, counts, totals);
    }

    /// <summary>Saves the model in a deterministic bounded binary format.</summary>
    public byte[] Save()
    {
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, Utf8, leaveOpen: true);
        writer.Write(Magic);
        string[] labels = [.. _labels.OrderBy(item => item.Value).Select(item => item.Key)];
        writer.Write(labels.Length);
        foreach (string label in labels)
        {
            byte[] encoded = Utf8.GetBytes(label);
            writer.Write(encoded.Length);
            writer.Write(encoded);
        }
        (int Previous, int Current, int Count)[] transitions = [.. _counts
            .OrderBy(item => item.Key.Previous)
            .ThenBy(item => item.Key.Current)
            .Select(item => (item.Key.Previous, item.Key.Current, item.Value))];
        writer.Write(transitions.Length);
        foreach ((int previous, int current, int count) in transitions)
        {
            writer.Write(previous);
            writer.Write(current);
            writer.Write(count);
        }
        writer.Flush();
        if (output.Length > MaximumModelBytes)
            throw new InvalidOperationException("The OCR language model exceeds the size limit.");
        return output.ToArray();
    }

    /// <summary>Loads a bounded deterministic OCR language model.</summary>
    public static PdfOcrLanguageModel Load(ReadOnlyMemory<byte> source)
    {
        if (source.Length > MaximumModelBytes)
            throw new InvalidDataException("The OCR language model exceeds the size limit.");
        try
        {
            using var input = new MemoryStream(source.ToArray(), writable: false);
            using var reader = new BinaryReader(input, Utf8, leaveOpen: true);
            if (!reader.ReadBytes(Magic.Length).AsSpan().SequenceEqual(Magic))
                throw new InvalidDataException("The OCR language model header is invalid.");
            int labelCount = reader.ReadInt32();
            if (labelCount is <= 1 or > MaximumLabels)
                throw new InvalidDataException("The OCR language model label count is invalid.");
            var labels = new Dictionary<string, int>(labelCount, StringComparer.Ordinal);
            for (int index = 0; index < labelCount; index++)
            {
                int length = reader.ReadInt32();
                if (length is < 0 or > 64)
                    throw new InvalidDataException("An OCR language-model label is invalid.");
                string label = Utf8.GetString(reader.ReadBytes(length));
                if ((index == 0 && label.Length != 0)
                    || (index > 0 && label.Length == 0) || !labels.TryAdd(label, index))
                    throw new InvalidDataException("An OCR language-model label is invalid.");
            }
            int transitionCount = reader.ReadInt32();
            if (transitionCount is <= 0 or > MaximumCharacters)
                throw new InvalidDataException(
                    "The OCR language model transition count is invalid.");
            var counts = new Dictionary<(int Previous, int Current), int>(transitionCount);
            var totals = new int[labelCount];
            for (int index = 0; index < transitionCount; index++)
            {
                int previous = reader.ReadInt32();
                int current = reader.ReadInt32();
                int count = reader.ReadInt32();
                if ((uint)previous >= labelCount || current <= 0 || current >= labelCount
                    || count <= 0 || !counts.TryAdd((previous, current), count))
                    throw new InvalidDataException(
                        "An OCR language-model transition is invalid.");
                totals[previous] = checked(totals[previous] + count);
            }
            if (input.Position != input.Length)
                throw new InvalidDataException("The OCR language model has trailing data.");
            return new PdfOcrLanguageModel(labels, counts, totals);
        }
        catch (Exception error) when (error is EndOfStreamException
            or DecoderFallbackException or OverflowException)
        {
            throw new InvalidDataException("The OCR language model is truncated or invalid.", error);
        }
    }

    /// <summary>Chooses the highest-scoring label sequence with transition context.</summary>
    public IReadOnlyList<string> Decode(
        IReadOnlyList<IReadOnlyList<PdfOcrLanguageCandidate>> positions,
        double languageWeight = 1)
    {
        ArgumentNullException.ThrowIfNull(positions);
        if (!double.IsFinite(languageWeight) || languageWeight < 0)
            throw new ArgumentOutOfRangeException(nameof(languageWeight));
        if (positions.Count == 0) return Array.Empty<string>();
        if (positions.Count > MaximumSequenceLength)
            throw new ArgumentException(
                "The OCR language-model sequence exceeds the position limit.",
                nameof(positions));

        var paths = new Dictionary<string, Path>(StringComparer.Ordinal)
        {
            [string.Empty] = new Path(0, [])
        };
        foreach (IReadOnlyList<PdfOcrLanguageCandidate> supplied in positions)
        {
            if (supplied is null || supplied.Count == 0
                || supplied.Count > MaximumCandidatesPerPosition)
                throw new ArgumentException(
                    "Every OCR language-model position requires a bounded candidate set.",
                    nameof(positions));
            var next = new Dictionary<string, Path>(StringComparer.Ordinal);
            foreach (PdfOcrLanguageCandidate candidate in supplied)
            {
                if (string.IsNullOrEmpty(candidate.Label) || !double.IsFinite(candidate.Score))
                    throw new ArgumentException(
                        "OCR language-model candidates require labels and finite scores.",
                        nameof(positions));
                foreach ((string previous, Path path) in paths)
                {
                    double score = path.Score + candidate.Score
                        + languageWeight * TransitionScore(previous, candidate.Label);
                    if (!next.TryGetValue(candidate.Label, out Path? best)
                        || score > best.Score)
                        next[candidate.Label] = new Path(score,
                            [.. path.Labels, candidate.Label]);
                }
            }
            paths = next;
        }
        return Array.AsReadOnly(paths.Values
            .OrderByDescending(path => path.Score)
            .ThenBy(path => string.Concat(path.Labels), StringComparer.Ordinal)
            .First().Labels);
    }

    private double TransitionScore(string previous, string current)
    {
        int vocabulary = Math.Max(1, _labels.Count - 1);
        if (!_labels.TryGetValue(previous, out int previousIndex)) previousIndex = 0;
        if (!_labels.TryGetValue(current, out int currentIndex))
            return -Math.Log(_totals[previousIndex] + vocabulary);
        int count = _counts.GetValueOrDefault((previousIndex, currentIndex));
        return Math.Log((count + 1d) / (_totals[previousIndex] + vocabulary));
    }

    private sealed record Path(double Score, string[] Labels);
}
