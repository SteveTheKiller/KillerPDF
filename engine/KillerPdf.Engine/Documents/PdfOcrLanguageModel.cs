using System.Buffers.Binary;
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
        string[] labels = [.. _labels.OrderBy(item => item.Value).Select(item => item.Key)];
        (int Previous, int Current, int Count)[] transitions = [.. _counts
            .OrderBy(item => item.Key.Previous)
            .ThenBy(item => item.Key.Current)
            .Select(item => (item.Key.Previous, item.Key.Current, item.Value))];
        long length = Magic.Length + sizeof(int) * 2L
            + labels.Sum(label => sizeof(int) + (long)Utf8.GetByteCount(label))
            + transitions.Length * sizeof(int) * 3L;
        if (length > MaximumModelBytes)
            throw new InvalidOperationException("The OCR language model exceeds the size limit.");
        var output = new byte[checked((int)length)];
        Span<byte> destination = output;
        Magic.CopyTo(destination);
        int position = Magic.Length;
        WriteInt32(destination, ref position, labels.Length);
        foreach (string label in labels)
        {
            int bytes = Utf8.GetByteCount(label);
            WriteInt32(destination, ref position, bytes);
            position += Utf8.GetBytes(label, destination[position..]);
        }
        WriteInt32(destination, ref position, transitions.Length);
        foreach ((int previous, int current, int count) in transitions)
        {
            WriteInt32(destination, ref position, previous);
            WriteInt32(destination, ref position, current);
            WriteInt32(destination, ref position, count);
        }
        return output;
    }

    /// <summary>Loads a bounded deterministic OCR language model.</summary>
    public static PdfOcrLanguageModel Load(ReadOnlyMemory<byte> source)
    {
        if (source.Length > MaximumModelBytes)
            throw new InvalidDataException("The OCR language model exceeds the size limit.");
        try
        {
            ReadOnlySpan<byte> input = source.Span;
            int position = 0;
            if (!ReadBytes(input, ref position, Magic.Length).SequenceEqual(Magic))
                throw new InvalidDataException("The OCR language model header is invalid.");
            int labelCount = ReadInt32(input, ref position);
            if (labelCount is <= 1 or > MaximumLabels)
                throw new InvalidDataException("The OCR language model label count is invalid.");
            var labels = new Dictionary<string, int>(labelCount, StringComparer.Ordinal);
            for (int index = 0; index < labelCount; index++)
            {
                int length = ReadInt32(input, ref position);
                if (length is < 0 or > 64)
                    throw new InvalidDataException("An OCR language-model label is invalid.");
                string label = Utf8.GetString(ReadBytes(input, ref position, length));
                if ((index == 0 && label.Length != 0)
                    || (index > 0 && label.Length == 0) || !labels.TryAdd(label, index))
                    throw new InvalidDataException("An OCR language-model label is invalid.");
            }
            int transitionCount = ReadInt32(input, ref position);
            if (transitionCount is <= 0 or > MaximumCharacters)
                throw new InvalidDataException(
                    "The OCR language model transition count is invalid.");
            var counts = new Dictionary<(int Previous, int Current), int>(transitionCount);
            var totals = new int[labelCount];
            for (int index = 0; index < transitionCount; index++)
            {
                int previous = ReadInt32(input, ref position);
                int current = ReadInt32(input, ref position);
                int count = ReadInt32(input, ref position);
                if ((uint)previous >= labelCount || current <= 0 || current >= labelCount
                    || count <= 0 || !counts.TryAdd((previous, current), count))
                    throw new InvalidDataException(
                        "An OCR language-model transition is invalid.");
                totals[previous] = checked(totals[previous] + count);
            }
            if (position != input.Length)
                throw new InvalidDataException("The OCR language model has trailing data.");
            return new PdfOcrLanguageModel(labels, counts, totals);
        }
        catch (Exception error) when (error is EndOfStreamException
            or DecoderFallbackException or OverflowException)
        {
            throw new InvalidDataException("The OCR language model is truncated or invalid.", error);
        }
    }

    private static int ReadInt32(ReadOnlySpan<byte> source, ref int position)
    {
        ReadOnlySpan<byte> value = ReadBytes(source, ref position, sizeof(int));
        return BinaryPrimitives.ReadInt32LittleEndian(value);
    }

    private static void WriteInt32(Span<byte> destination, ref int position, int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(destination[position..], value);
        position += sizeof(int);
    }

    private static ReadOnlySpan<byte> ReadBytes(ReadOnlySpan<byte> source,
        ref int position, int length)
    {
        if (length < 0 || position > source.Length - length)
            throw new EndOfStreamException();
        ReadOnlySpan<byte> value = source.Slice(position, length);
        position += length;
        return value;
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
            [string.Empty] = new Path(0, string.Empty, null, 0)
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
                    var proposed = new Path(
                        score, candidate.Label, path, path.Length + 1);
                    if (!next.TryGetValue(candidate.Label, out Path? best)
                        || score > best.Score
                        || score == best.Score && ComparePaths(proposed, best) < 0)
                        next[candidate.Label] = proposed;
                }
            }
            paths = next;
        }
        Path selected = paths.Values.First();
        foreach (Path candidate in paths.Values.Skip(1))
            if (candidate.Score > selected.Score
                || candidate.Score == selected.Score
                && ComparePaths(candidate, selected) < 0)
                selected = candidate;
        return Array.AsReadOnly(ToLabels(selected));
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

    private static int ComparePaths(Path left, Path right)
    {
        string[] leftLabels = ToLabels(left);
        string[] rightLabels = ToLabels(right);
        for (int index = 0; index < Math.Min(leftLabels.Length, rightLabels.Length); index++)
        {
            int comparison = string.CompareOrdinal(leftLabels[index], rightLabels[index]);
            if (comparison != 0) return comparison;
        }
        return leftLabels.Length.CompareTo(rightLabels.Length);
    }

    private static string[] ToLabels(Path path)
    {
        var labels = new string[path.Length];
        for (int index = labels.Length - 1; index >= 0; index--)
        {
            labels[index] = path.Label;
            path = path.Previous!;
        }
        return labels;
    }

    private sealed record Path(
        double Score, string Label, Path? Previous, int Length);
}
