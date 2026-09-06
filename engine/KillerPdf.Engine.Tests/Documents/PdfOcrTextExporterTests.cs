using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfOcrTextExporterTests
{
    [Fact]
    public void FormatWritesPlainTextPagesWithNormalizedLines()
    {
        string text = PdfOcrTextExporter.Format(
            ["First\r\nline\n", "Second\rpage"],
            PdfOcrTextFormat.PlainText, "\n");

        Assert.Equal("----- Page 1 -----\nFirst\nline\n\n"
            + "----- Page 2 -----\nSecond\npage\n\n", text);
    }

    [Fact]
    public void FormatWritesMarkdownHeadingsForEmptyAndPopulatedPages()
    {
        string text = PdfOcrTextExporter.Format(
            ["", "Recognized"], PdfOcrTextFormat.Markdown, "\n");

        Assert.Equal("## Page 1\n\n\n\n"
            + "## Page 2\n\nRecognized\n\n", text);
    }

    [Fact]
    public void FormatRejectsInvalidInputs()
    {
        Assert.Throws<ArgumentNullException>(() =>
            PdfOcrTextExporter.Format(null!, PdfOcrTextFormat.PlainText));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PdfOcrTextExporter.Format([], (PdfOcrTextFormat)99));
        Assert.Throws<ArgumentException>(() =>
            PdfOcrTextExporter.Format([null!], PdfOcrTextFormat.PlainText));
        Assert.Throws<ArgumentException>(() =>
            PdfOcrTextExporter.Format([], PdfOcrTextFormat.PlainText, ""));
    }
}
