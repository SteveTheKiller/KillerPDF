using KillerPdf.Engine.Documents;

namespace KillerPDF.Services;

/// <summary>Readable saved annotation descriptions appended after the page body.</summary>
internal static class ReadingAnnotationText
{
    public static string Describe(PdfCommentInfo comment, Func<PageTextRuns?> textRuns,
        (double Left, double Bottom) cropOrigin, Func<string, string> localize)
    {
        if (!string.IsNullOrWhiteSpace(comment.Contents))
            return string.IsNullOrWhiteSpace(comment.Author)
                ? string.Format(localize("Str_Read_Comment"), comment.Contents.Trim())
                : string.Format(localize("Str_Read_CommentBy"), comment.Author.Trim(), comment.Contents.Trim());
        switch (comment.Subtype)
        {
            case "Highlight" or "Underline" or "StrikeOut" or "Squiggly":
                string marked = MarkedText(comment.Bounds, textRuns, cropOrigin);
                return marked.Length == 0 ? localize("Str_Read_MarkedNoText")
                    : string.Format(localize("Str_Read_MarkedText"), marked);
            case "Stamp":
                return localize("Str_Read_Stamp");
            default:
                return localize("Str_Read_Drawing");
        }
    }

    private static string MarkedText(PdfContentBounds? bounds, Func<PageTextRuns?> textRuns,
        (double Left, double Bottom) cropOrigin)
    {
        if (bounds is not { } b || textRuns() is not { } runs) return string.Empty;
        var (left, bottom) = cropOrigin;
        double x1 = b.Left - left, x2 = b.Right - left, y1 = b.Bottom - bottom, y2 = b.Top - bottom;
        var text = new System.Text.StringBuilder();
        int lastWord = -1;
        foreach (RunChar c in runs.Chars)
        {
            if (c.Line < 0 || c.Line >= runs.Lines.Count) continue;
            RunLine line = runs.Lines[c.Line];
            var character = TextRunService.CharacterBounds(c, line);
            double middleX = (character.Left + character.Right) / 2,
                middleY = (character.Top + character.Bottom) / 2;
            if (middleX < x1 || middleX > x2 || middleY < y1 || middleY > y2) continue;
            if (lastWord >= 0 && c.Word != lastWord) text.Append(' ');
            text.Append(c.Value);
            lastWord = c.Word;
        }
        string result = text.ToString().Trim();
        return result.Length > 300 ? result[..300] + "…" : result;
    }
}
