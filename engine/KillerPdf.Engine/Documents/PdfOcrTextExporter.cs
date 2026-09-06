using System.Text;

namespace KillerPdf.Engine.Documents;

/// <summary>Supported text formats for page-oriented OCR export.</summary>
public enum PdfOcrTextFormat
{
    /// <summary>Plain text with visible page dividers.</summary>
    PlainText,
    /// <summary>Markdown with a heading for each page.</summary>
    Markdown
}

/// <summary>Formats recognized page text without depending on a desktop UI.</summary>
public static class PdfOcrTextExporter
{
    /// <summary>Formats recognized pages in document order.</summary>
    public static string Format(IReadOnlyList<string> pages,
        PdfOcrTextFormat format, string? newline = null)
    {
        ArgumentNullException.ThrowIfNull(pages);
        if (!Enum.IsDefined(format)) throw new ArgumentOutOfRangeException(nameof(format));
        newline ??= Environment.NewLine;
        if (newline.Length == 0) throw new ArgumentException(
            "The OCR export newline cannot be empty.", nameof(newline));

        var output = new StringBuilder();
        for (int index = 0; index < pages.Count; index++)
        {
            string text = pages[index] ?? throw new ArgumentException(
                "OCR page text cannot be null.", nameof(pages));
            text = text.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n').TrimEnd('\n')
                .Replace("\n", newline, StringComparison.Ordinal);
            if (format == PdfOcrTextFormat.Markdown)
                output.Append("## Page ").Append(index + 1).Append(newline).Append(newline);
            else
                output.Append("----- Page ").Append(index + 1).Append(" -----").Append(newline);
            output.Append(text).Append(newline).Append(newline);
        }
        return output.ToString();
    }
}
