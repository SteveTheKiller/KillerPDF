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

    /// <summary>Gets the word edit count divided by the expected word count.</summary>
    public double WordErrorRate => ErrorRate(
        WordEditCount, ExpectedWordCount, RecognizedWordCount);

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
