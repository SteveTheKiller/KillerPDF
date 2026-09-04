using System.Text;
using KillerPdf.Engine.Objects;

namespace KillerPdf.Engine.Documents;

/// <summary>Resolves the displayed label for every page in a PDF document.</summary>
public static class PdfPageLabelReader
{
    private static readonly PdfName PageLabelsKey = Name("PageLabels");
    private static readonly PdfName StyleKey = Name("S");
    private static readonly PdfName PrefixKey = Name("P");
    private static readonly PdfName StartKey = Name("St");

    /// <summary>Reads page labels in document order, applying default decimal labels.</summary>
    public static IReadOnlyList<string> Read(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        PdfPageTree tree = PdfPageTree.Read(document);
        if (!tree.Catalog.TryGetValue(PageLabelsKey, out PdfObject? root))
            return Array.AsReadOnly(Enumerable.Range(1, tree.Pages.Count)
                .Select(number => number.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .ToArray());
        IReadOnlyList<Range> ranges = [.. PdfNumberTree.Read(document, root)
            .Select(ReadRange).OrderBy(range => range.PageIndex)];
        if (ranges.Any(range => range.PageIndex < 0 || range.PageIndex >= tree.Pages.Count))
            throw new InvalidOperationException("A page-label range starts outside the document.");
        var labels = new string[tree.Pages.Count];
        int rangeIndex = -1;
        for (int pageIndex = 0; pageIndex < labels.Length; pageIndex++)
        {
            while (rangeIndex + 1 < ranges.Count
                && ranges[rangeIndex + 1].PageIndex <= pageIndex)
                rangeIndex++;
            if (rangeIndex < 0)
            {
                labels[pageIndex] = (pageIndex + 1).ToString(
                    System.Globalization.CultureInfo.InvariantCulture);
                continue;
            }
            Range range = ranges[rangeIndex];
            long number = checked(range.StartNumber + pageIndex - range.PageIndex);
            labels[pageIndex] = range.Prefix + Format(range.Style, number);
        }
        return Array.AsReadOnly(labels);

        Range ReadRange(PdfNumberTreeEntry entry)
        {
            PdfDictionary dictionary = Resolve(entry.Value) as PdfDictionary
                ?? throw new InvalidOperationException("A page-label range is not a dictionary.");
            string? style = null;
            if (dictionary.TryGetValue(StyleKey, out PdfObject? styleValue))
                style = (Resolve(styleValue) as PdfName)?.ValueAsLatin1()
                    ?? throw new InvalidOperationException("A page-label style is not a name.");
            if (style is not (null or "D" or "R" or "r" or "A" or "a"))
                throw new InvalidOperationException("A page-label style is not supported.");
            string prefix = string.Empty;
            if (dictionary.TryGetValue(PrefixKey, out PdfObject? prefixValue))
                prefix = Resolve(prefixValue) is PdfString text
                    ? PdfUnicodeEncoding.DecodeTextString(text.Bytes.Span, "A page-label prefix")
                    : throw new InvalidOperationException("A page-label prefix is not a string.");
            long start = 1;
            if (dictionary.TryGetValue(StartKey, out PdfObject? startValue))
                start = Resolve(startValue) is PdfInteger integer && integer.Value > 0
                    ? integer.Value
                    : throw new InvalidOperationException("A page-label start must be a positive integer.");
            if (style is null && prefix.Length == 0)
                throw new InvalidOperationException("A page-label range has neither a style nor a prefix.");
            return new Range(entry.Key, style, prefix, start);
        }

        PdfObject Resolve(PdfObject value)
        {
            var visited = new HashSet<(int, int)>();
            for (int depth = 0; value is PdfIndirectReference reference; depth++)
            {
                if (depth >= 32 || !visited.Add((reference.ObjectNumber, reference.Generation)))
                    throw new InvalidOperationException("A page-label value has an invalid reference chain.");
                value = document.Resolve(reference);
            }
            return value;
        }
    }

    private static string Format(string? style, long number) => style switch
    {
        null => string.Empty,
        "D" => number.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "R" => Roman(number),
        "r" => Roman(number).ToLowerInvariant(),
        "A" => Letters(number, 'A'),
        "a" => Letters(number, 'a'),
        _ => throw new InvalidOperationException("A page-label style is not supported.")
    };

    private static string Roman(long number)
    {
        (int Value, string Text)[] parts =
        [
            (1000, "M"), (900, "CM"), (500, "D"), (400, "CD"),
            (100, "C"), (90, "XC"), (50, "L"), (40, "XL"),
            (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I")
        ];
        var output = new StringBuilder();
        foreach ((int value, string text) in parts)
            while (number >= value) { output.Append(text); number -= value; }
        return output.ToString();
    }

    private static string Letters(long number, char first)
    {
        long zeroBased = number - 1;
        int count = checked((int)(zeroBased / 26 + 1));
        char letter = checked((char)(first + zeroBased % 26));
        return new string(letter, count);
    }

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
    private sealed record Range(long PageIndex, string? Style, string Prefix, long StartNumber);
}
