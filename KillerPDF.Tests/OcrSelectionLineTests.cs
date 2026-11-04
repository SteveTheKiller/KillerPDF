using System.IO;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Fonts;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class OcrSelectionLineTests
{
    [Theory]
    [InlineData(33, false)]
    [InlineData(20, false)]
    [InlineData(33, true)]
    public void ShortRecognizedWordBoxesStayOnTheirVisualLineWhenCopied(int wordTop, bool fullLine)
    {
        string path = Path.Combine(Path.GetTempPath(), $"kp-ocr-short-words-{Guid.NewGuid():N}.pdf");
        try
        {
            TrueTypeFont font = TrueTypeFont.Load(File.ReadAllBytes(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "arial.ttf")));
            PdfDocument original = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage(200, 120).Build());
            PdfOcrPixelWord[] words = fullLine ?
            [
                new("Price", 1, 10, 20, 50, 40), new(":", 1, 60, 30, 64, 34),
                new("3280", 1, 70, 20, 115, 40), new("yen", 1, 125, wordTop, 145, 40),
                new("Next", 1, 10, 60, 55, 80)
            ] :
            [
                new("3280", 1, 70, 20, 115, 40), new("yen", 1, 125, wordTop, 145, 40)
            ];
            var recognized = new PdfOcrPixelPage(200, 120, words);
            File.WriteAllBytes(path, PdfOcrSearchableTextWriter.Write(original, [recognized], _ => font).Document);

            var runs = Assert.IsType<PageTextRuns>(new TextRunService().GetPage(path, null, 0));
            string copied = TextRunService.TextForRange(runs, 0, runs.Chars.Count, out _);

            Assert.Equal(fullLine ? "Price : 3280 yen\nNext" : "3280 yen", copied);
        }
        finally { File.Delete(path); }
    }
}
