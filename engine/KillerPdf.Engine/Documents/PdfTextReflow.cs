using System.Globalization;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Parsing;

namespace KillerPdf.Engine.Documents;

/// <summary>One line produced by font-metric-aware text reflow.</summary>
public sealed record PdfReflowLine(string Text, double Width);

/// <summary>Plans and authors wrapped Unicode text using embedded-font metrics.</summary>
public static class PdfTextReflow
{
    private const int MaximumTextLength = 1_000_000;

    /// <summary>Wraps text to a maximum width while retaining explicit paragraph breaks.</summary>
    public static IReadOnlyList<PdfReflowLine> Wrap(
        string text, TrueTypeFont font, double fontSize, double maximumWidth)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(font);
        if (text.Length > MaximumTextLength)
            throw new ArgumentException("Text reflow input exceeds the size limit.", nameof(text));
        if (!double.IsFinite(fontSize) || fontSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(fontSize));
        if (!double.IsFinite(maximumWidth) || maximumWidth <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumWidth));

        var lines = new List<PdfReflowLine>();
        string normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        foreach (string paragraph in normalized.Split('\n'))
        {
            string[] words = Words(paragraph);
            if (words.Length == 0)
            {
                lines.Add(new PdfReflowLine(string.Empty, 0));
                continue;
            }

            string current = string.Empty;
            double currentWidth = 0;
            double spaceWidth = Width(" ", font, fontSize);
            foreach (string word in words)
            {
                double wordWidth = Width(word, font, fontSize);
                if (current.Length > 0 && currentWidth + spaceWidth + wordWidth <= maximumWidth)
                {
                    current += " " + word;
                    currentWidth += spaceWidth + wordWidth;
                    continue;
                }
                if (current.Length > 0)
                    lines.Add(new PdfReflowLine(current, currentWidth));
                if (wordWidth <= maximumWidth)
                {
                    current = word;
                    currentWidth = wordWidth;
                    continue;
                }

                IReadOnlyList<PdfReflowLine> fragments = BreakWord(
                    word, font, fontSize, maximumWidth);
                for (int index = 0; index + 1 < fragments.Count; index++)
                    lines.Add(fragments[index]);
                PdfReflowLine last = fragments[^1];
                current = last.Text;
                currentWidth = last.Width;
            }
            lines.Add(new PdfReflowLine(current, currentWidth));
        }
        return Array.AsReadOnly(lines.ToArray());
    }

    /// <summary>Creates positioned, wrapped Unicode content with a safely embedded font.</summary>
    public static PdfContentStreamBuilder CreateUnicodeContent(
        string text, TrueTypeFont font, double fontSize, double maximumWidth,
        double x, double y, double leading)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y))
            throw new ArgumentOutOfRangeException(nameof(x));
        if (!double.IsFinite(leading) || leading <= 0)
            throw new ArgumentOutOfRangeException(nameof(leading));
        IReadOnlyList<PdfReflowLine> lines = Wrap(text, font, fontSize, maximumWidth);
        var content = new PdfContentStreamBuilder()
            .BeginText().SetFont(font, fontSize).SetTextLeading(leading).MoveText(x, y);
        for (int index = 0; index < lines.Count; index++)
        {
            if (index > 0) content.MoveToNextTextLine();
            if (lines[index].Text.Length > 0) content.ShowUnicodeText(lines[index].Text);
        }
        return content.EndText();
    }

    /// <summary>
    /// Replaces one top-level text object with wrapped Unicode text in an untagged document.
    /// </summary>
    public static byte[] ReplaceTextObject(
        PdfDocument document, int pageIndex, int textObjectIndex,
        string text, TrueTypeFont font, double fontSize, double maximumWidth,
        double x, double y, double leading)
    {
        ArgumentNullException.ThrowIfNull(document);
        var reader = new PdfPageContentReader(document);
        PdfPageContent page = reader.Read(pageIndex);
        IReadOnlyList<PdfContentInstruction> rewritten =
            PdfContentTransformation.RemoveTextObjects(
                reader.ReadInstructions(pageIndex), [textObjectIndex]);
        PdfContentStreamBuilder replacement = CreateUnicodeContent(
            text, font, fontSize, maximumWidth, x, y, leading);
        return new PdfIncrementalPageEditor(document)
            .SetPageContentAndPruneResources(pageIndex, rewritten)
            .AppendPageContent(pageIndex, page.Width, page.Height, replacement)
            .Build();
    }

    private static IReadOnlyList<PdfReflowLine> BreakWord(
        string word, TrueTypeFont font, double fontSize, double maximumWidth)
    {
        var result = new List<PdfReflowLine>();
        var current = new System.Text.StringBuilder();
        double currentWidth = 0;
        TextElementEnumerator elements = StringInfo.GetTextElementEnumerator(word);
        while (elements.MoveNext())
        {
            string element = elements.GetTextElement();
            double elementWidth = Width(element, font, fontSize);
            if (current.Length > 0 && currentWidth + elementWidth > maximumWidth)
            {
                result.Add(new PdfReflowLine(current.ToString(), currentWidth));
                current.Clear();
                currentWidth = 0;
            }
            current.Append(element);
            currentWidth += elementWidth;
        }
        if (current.Length > 0) result.Add(new PdfReflowLine(current.ToString(), currentWidth));
        return Array.AsReadOnly(result.ToArray());
    }

    private static double Width(string text, TrueTypeFont font, double fontSize) =>
        font.MapText(text).Sum(mapping =>
            font.GetPdfAdvanceWidth(mapping.Glyph) * fontSize / 1000d);

    private static string[] Words(string paragraph)
    {
        var words = new List<string>();
        int start = -1;
        for (int index = 0; index < paragraph.Length; index++)
        {
            if (char.IsWhiteSpace(paragraph[index]))
            {
                if (start >= 0)
                {
                    words.Add(paragraph[start..index]);
                    start = -1;
                }
            }
            else if (start < 0) start = index;
        }
        if (start >= 0) words.Add(paragraph[start..]);
        return words.ToArray();
    }
}
