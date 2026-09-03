using System.Globalization;
using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Syntax;

namespace KillerPdf.Engine.Documents;

/// <summary>High-level descriptive and structural information read from a PDF document.</summary>
public sealed record PdfDocumentInformation
{
    /// <summary>Gets the document title.</summary>
    public string? Title { get; init; }
    /// <summary>Gets the document author.</summary>
    public string? Author { get; init; }
    /// <summary>Gets the document subject.</summary>
    public string? Subject { get; init; }
    /// <summary>Gets document-search keywords.</summary>
    public string? Keywords { get; init; }
    /// <summary>Gets the application that created the original content.</summary>
    public string? Creator { get; init; }
    /// <summary>Gets the application that produced the PDF.</summary>
    public string? Producer { get; init; }
    /// <summary>Gets the document's primary natural language.</summary>
    public string? Language { get; init; }
    /// <summary>Gets the document creation date.</summary>
    public DateTimeOffset? CreationDate { get; init; }
    /// <summary>Gets the most recent document modification date.</summary>
    public DateTimeOffset? ModificationDate { get; init; }
    /// <summary>Gets the document trapping status.</summary>
    public PdfTrappedStatus? Trapped { get; init; }
    /// <summary>Gets the header version.</summary>
    public required PdfVersion Version { get; init; }
    /// <summary>Gets the number of leaf pages in the page tree.</summary>
    public required int PageCount { get; init; }
    /// <summary>Gets the presentation settings applied when the document opens.</summary>
    public required PdfInitialView InitialView { get; init; }

    /// <summary>Reads document information from an open PDF.</summary>
    public static PdfDocumentInformation Read(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        PdfDictionary? info = null;
        if (document.Trailer.TryGetValue(new PdfName("Info"u8), out PdfObject? value))
        {
            value = Resolve(document, value);
            info = value as PdfDictionary
                ?? throw new InvalidOperationException("The trailer /Info value is not a dictionary.");
        }

        PdfPageTree pageTree = PdfPageTree.Read(document);
        return new PdfDocumentInformation
        {
            Title = Text(info, "Title"),
            Author = Text(info, "Author"),
            Subject = Text(info, "Subject"),
            Keywords = Text(info, "Keywords"),
            Creator = Text(info, "Creator"),
            Producer = Text(info, "Producer"),
            Language = CatalogText(document, "Lang"),
            CreationDate = Date(info, "CreationDate"),
            ModificationDate = Date(info, "ModDate"),
            Trapped = ReadTrapped(info),
            Version = document.Header.Version,
            PageCount = pageTree.Pages.Count,
            InitialView = PdfInitialView.Read(document, pageTree)
        };
    }

    private static string? CatalogText(PdfDocument document, string key)
    {
        PdfDictionary catalog = PdfPageTree.Read(document).Catalog;
        if (!catalog.TryGetValue(new PdfName(Encoding.ASCII.GetBytes(key)), out PdfObject? value))
            return null;
        value = Resolve(document, value);
        return value is PdfString text
            ? PdfUnicodeEncoding.DecodeTextString(text.Bytes.Span, $"The catalog /{key} value")
            : throw new InvalidOperationException($"The catalog /{key} value is not a string.");
    }

    private static DateTimeOffset? Date(PdfDictionary? info, string key)
    {
        string? value = Text(info, key);
        if (value is null) return null;
        try
        {
            if (!value.StartsWith("D:", StringComparison.Ordinal) || value.Length < 6)
                throw new FormatException();
            string digits = new([.. value.Skip(2).TakeWhile(char.IsAsciiDigit)]);
            if (digits.Length is not (4 or 6 or 8 or 10 or 12 or 14)) throw new FormatException();
            int Part(int offset, int length, int fallback) => digits.Length >= offset + length
                ? int.Parse(digits.AsSpan(offset, length), CultureInfo.InvariantCulture) : fallback;
            int year = Part(0, 4, 1), month = Part(4, 2, 1), day = Part(6, 2, 1);
            int hour = Part(8, 2, 0), minute = Part(10, 2, 0), second = Part(12, 2, 0);
            string suffix = value[(2 + digits.Length)..];
            TimeSpan offset = TimeSpan.Zero;
            if (suffix.Length > 0 && suffix != "Z")
            {
                char sign = suffix[0];
                if (sign is not ('+' or '-')) throw new FormatException();
                string compact = suffix[1..].Replace("'", string.Empty, StringComparison.Ordinal);
                if (compact.Length != 4 || !int.TryParse(compact[..2], out int oh)
                    || !int.TryParse(compact[2..], out int om)) throw new FormatException();
                offset = new TimeSpan(oh, om, 0) * (sign == '-' ? -1 : 1);
            }
            return new DateTimeOffset(year, month, day, hour, minute, second, offset);
        }
        catch (Exception error) when (error is FormatException or ArgumentOutOfRangeException)
        {
            throw new InvalidOperationException($"The /{key} document information value is not a valid PDF date.", error);
        }
    }

    private static PdfTrappedStatus? ReadTrapped(PdfDictionary? info)
    {
        if (info is null || !info.TryGetValue(new PdfName("Trapped"u8), out PdfObject? value)) return null;
        return value is PdfName name && Enum.TryParse(name.ValueAsLatin1(), out PdfTrappedStatus result)
            ? result
            : throw new InvalidOperationException("The /Trapped document information value is not defined.");
    }

    private static PdfObject Resolve(PdfDocument document, PdfObject value)
    {
        var visited = new HashSet<(int, int)>();
        while (value is PdfIndirectReference reference)
        {
            if (!visited.Add((reference.ObjectNumber, reference.Generation)))
                throw new InvalidOperationException("The document information reference contains a cycle.");
            value = document.Resolve(reference);
        }
        return value;
    }

    private static string? Text(PdfDictionary? info, string key)
    {
        if (info is null || !info.TryGetValue(new PdfName(System.Text.Encoding.ASCII.GetBytes(key)), out PdfObject? value))
            return null;
        return value is PdfString text
            ? PdfUnicodeEncoding.DecodeTextString(text.Bytes.Span, $"The /{key} document information value")
            : throw new InvalidOperationException($"The /{key} document information value is not a string.");
    }
}
