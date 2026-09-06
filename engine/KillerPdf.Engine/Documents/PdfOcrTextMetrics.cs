using System.Text;

namespace KillerPdf.Engine.Documents;

/// <summary>Character and word error measurements for OCR output.</summary>
public sealed record PdfOcrTextMetrics(
    int ExpectedCharacterCount,
    int RecognizedCharacterCount,
    int CharacterEditCount,
    int ExpectedWordCount,
    int RecognizedWordCount,
    int WordEditCount)
{
    private const long MaximumComparisonCells = 100_000_000;

    /// <summary>Gets the character edit count divided by the expected character count.</summary>
    public double CharacterErrorRate => ErrorRate(
        CharacterEditCount, ExpectedCharacterCount, RecognizedCharacterCount);

    /// <summary>Gets character accuracy clamped from zero through one.</summary>
    public double CharacterAccuracy => Math.Max(0, 1 - CharacterErrorRate);

    /// <summary>Gets the word edit count divided by the expected word count.</summary>
    public double WordErrorRate => ErrorRate(
        WordEditCount, ExpectedWordCount, RecognizedWordCount);

    /// <summary>Gets word accuracy clamped from zero through one.</summary>
    public double WordAccuracy => Math.Max(0, 1 - WordErrorRate);

    /// <summary>Compares expected text with OCR output after normalizing whitespace.</summary>
    public static PdfOcrTextMetrics Compare(string expected, string recognized)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(recognized);
        string normalizedExpected = NormalizeWhitespace(expected);
        string normalizedRecognized = NormalizeWhitespace(recognized);
        Rune[] expectedCharacters = [.. normalizedExpected.EnumerateRunes()];
        Rune[] recognizedCharacters = [.. normalizedRecognized.EnumerateRunes()];
        string[] expectedWords = Words(normalizedExpected);
        string[] recognizedWords = Words(normalizedRecognized);
        return new PdfOcrTextMetrics(
            expectedCharacters.Length,
            recognizedCharacters.Length,
            EditDistance(expectedCharacters, recognizedCharacters),
            expectedWords.Length,
            recognizedWords.Length,
            EditDistance(expectedWords, recognizedWords));
    }

    /// <summary>Measures recognition independently of page reading order using word-box matches.</summary>
    public static PdfOcrTextMetrics CompareMatchedWords(
        IReadOnlyList<PdfOcrPixelWord> expected,
        IReadOnlyList<PdfOcrPixelWord> recognized,
        IReadOnlyList<PdfOcrWordBoxMatch> matches)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(recognized);
        ArgumentNullException.ThrowIfNull(matches);
        var matchedExpected = new HashSet<int>();
        var matchedRecognized = new HashSet<int>();
        int expectedCharacters = 0, recognizedCharacters = 0;
        int characterEdits = 0, expectedWords = 0, recognizedWords = 0, wordEdits = 0;
        foreach (PdfOcrWordBoxMatch match in matches)
        {
            if ((uint)match.ExpectedIndex >= (uint)expected.Count
                || (uint)match.RecognizedIndex >= (uint)recognized.Count
                || !matchedExpected.Add(match.ExpectedIndex)
                || !matchedRecognized.Add(match.RecognizedIndex))
                throw new ArgumentException(
                    "OCR word-box matches contain invalid or duplicate indexes.",
                    nameof(matches));
            PdfOcrTextMetrics comparison = Compare(
                expected[match.ExpectedIndex].Text,
                recognized[match.RecognizedIndex].Text);
            expectedCharacters = checked(expectedCharacters
                + comparison.ExpectedCharacterCount);
            recognizedCharacters = checked(recognizedCharacters
                + comparison.RecognizedCharacterCount);
            characterEdits = checked(characterEdits + comparison.CharacterEditCount);
            expectedWords = checked(expectedWords + comparison.ExpectedWordCount);
            recognizedWords = checked(recognizedWords + comparison.RecognizedWordCount);
            wordEdits = checked(wordEdits + comparison.WordEditCount);
        }
        for (int index = 0; index < expected.Count; index++)
        {
            if (matchedExpected.Contains(index)) continue;
            PdfOcrTextMetrics missing = Compare(expected[index].Text, string.Empty);
            expectedCharacters = checked(expectedCharacters + missing.ExpectedCharacterCount);
            characterEdits = checked(characterEdits + missing.CharacterEditCount);
            expectedWords = checked(expectedWords + missing.ExpectedWordCount);
            wordEdits = checked(wordEdits + missing.WordEditCount);
        }
        for (int index = 0; index < recognized.Count; index++)
        {
            if (matchedRecognized.Contains(index)) continue;
            PdfOcrTextMetrics extra = Compare(string.Empty, recognized[index].Text);
            recognizedCharacters = checked(recognizedCharacters
                + extra.RecognizedCharacterCount);
            characterEdits = checked(characterEdits + extra.CharacterEditCount);
            recognizedWords = checked(recognizedWords + extra.RecognizedWordCount);
            wordEdits = checked(wordEdits + extra.WordEditCount);
        }
        return new PdfOcrTextMetrics(expectedCharacters, recognizedCharacters,
            characterEdits, expectedWords, recognizedWords, wordEdits);
    }

    private static double ErrorRate(int edits, int expected, int recognized) =>
        expected == 0 ? recognized == 0 ? 0 : 1 : edits / (double)expected;

    private static string NormalizeWhitespace(string text)
    {
        var result = new StringBuilder(text.Length);
        bool pendingSpace = false;
        foreach (Rune rune in text.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                pendingSpace = result.Length > 0;
                continue;
            }
            if (pendingSpace) result.Append(' ');
            result.Append(rune);
            pendingSpace = false;
        }
        return result.ToString();
    }

    private static string[] Words(string text) => text.Length == 0
        ? [] : text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static int EditDistance<T>(ReadOnlySpan<T> expected, ReadOnlySpan<T> recognized)
        where T : IEquatable<T>
    {
        if ((long)expected.Length * recognized.Length > MaximumComparisonCells)
            throw new ArgumentException(
                "The OCR text comparison exceeds the work limit.");
        if (expected.Length < recognized.Length)
            return EditDistance(recognized, expected);
        int[] previous = new int[recognized.Length + 1];
        int[] current = new int[recognized.Length + 1];
        for (int index = 0; index < previous.Length; index++) previous[index] = index;
        for (int expectedIndex = 1; expectedIndex <= expected.Length; expectedIndex++)
        {
            current[0] = expectedIndex;
            for (int recognizedIndex = 1;
                recognizedIndex <= recognized.Length; recognizedIndex++)
            {
                int substitution = previous[recognizedIndex - 1]
                    + (expected[expectedIndex - 1].Equals(
                        recognized[recognizedIndex - 1]) ? 0 : 1);
                current[recognizedIndex] = Math.Min(substitution,
                    Math.Min(previous[recognizedIndex] + 1,
                        current[recognizedIndex - 1] + 1));
            }
            (previous, current) = (current, previous);
        }
        return previous[recognized.Length];
    }
}
