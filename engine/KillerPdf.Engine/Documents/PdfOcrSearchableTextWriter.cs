using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Fonts;

namespace KillerPdf.Engine.Documents;

/// <summary>One OCR page in top-left-origin pixel coordinates.</summary>
public sealed record PdfOcrPixelPage(
    int PixelWidth, int PixelHeight, IReadOnlyList<PdfOcrPixelWord> Words);

/// <summary>A searchable PDF and the number of words written into it.</summary>
public sealed record PdfOcrSearchableTextResult(byte[] Document, int WrittenWords);

/// <summary>Writes pixel-space OCR results as invisible searchable PDF text.</summary>
public static class PdfOcrSearchableTextWriter
{
    /// <summary>Writes all supplied OCR pages using fonts selected by the caller.</summary>
    public static PdfOcrSearchableTextResult Write(PdfDocument document,
        IReadOnlyList<PdfOcrPixelPage> pages, Func<string, TrueTypeFont?> fontResolver)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(fontResolver);
        IReadOnlyList<PdfPageInformation> information = PdfPageInformation.Read(document);
        if (pages.Count != information.Count)
            throw new ArgumentException(
                "The OCR page count must match the PDF page count.", nameof(pages));
        var editor = new PdfIncrementalPageEditor(document);
        int writtenWords = 0;
        for (int pageIndex = 0; pageIndex < pages.Count; pageIndex++)
        {
            PdfOcrPixelPage page = pages[pageIndex];
            if (page.PixelWidth <= 0 || page.PixelHeight <= 0)
                throw new ArgumentOutOfRangeException(nameof(pages),
                    "OCR pixel dimensions must be positive.");
            PdfPageInformation geometry = information[pageIndex];
            double displayWidth = geometry.Rotation is 90 or 270
                ? geometry.Height : geometry.Width;
            double displayHeight = geometry.Rotation is 90 or 270
                ? geometry.Width : geometry.Height;
            double scaleX = displayWidth / page.PixelWidth;
            double scaleY = displayHeight / page.PixelHeight;
            var content = new PdfContentStreamBuilder().SaveState();
            ApplyDisplayTransform(content, geometry);
            content.BeginText().SetTextRenderingMode(PdfTextRenderingMode.Invisible);
            int pageWords = 0;
            foreach (PdfOcrPixelWord word in page.Words)
            {
                if (string.IsNullOrWhiteSpace(word.Text)) continue;
                TrueTypeFont? font = fontResolver(word.Text);
                if (font is null) continue;
                double height = Math.Max(1, (word.Bottom - word.Top) * scaleY);
                double width = Math.Max(1, (word.Right - word.Left) * scaleX);
                double naturalWidth = font.MapText(word.Text)
                    .Sum(mapping => font.GetPdfAdvanceWidth(mapping.Glyph))
                    * height / 1000;
                double horizontalScale = naturalWidth > 0
                    ? Math.Clamp(width / naturalWidth * 100, 10, 1000) : 100;
                content.SetFont(font, height).SetHorizontalTextScale(horizontalScale)
                    .SetTextMatrix(1, 0, 0, -1, word.Left * scaleX,
                        word.Top * scaleY + height * 0.8)
                    .ShowUnicodeText(word.Text);
                pageWords++;
            }
            content.EndText().RestoreState();
            if (pageWords == 0) continue;
            editor.AppendPageContent(pageIndex, geometry.Width, geometry.Height, content);
            writtenWords += pageWords;
        }
        return new PdfOcrSearchableTextResult(editor.Build(), writtenWords);
    }

    private static void ApplyDisplayTransform(
        PdfContentStreamBuilder content, PdfPageInformation page)
    {
        switch (page.Rotation)
        {
            case 0: content.Transform(1, 0, 0, -1, 0, page.Height); break;
            case 90: content.Transform(0, 1, 1, 0, 0, 0); break;
            case 180: content.Transform(-1, 0, 0, 1, page.Width, 0); break;
            case 270: content.Transform(0, -1, -1, 0, page.Width, page.Height); break;
            default: throw new InvalidOperationException("The PDF page rotation is unsupported.");
        }
    }
}
