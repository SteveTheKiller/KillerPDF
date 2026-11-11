using System.IO;
using KillerPDF.Features;
using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPDF.Tests;

// Form-aware OCR of several pages re-read and re-parsed the file for every page's form hints.
public sealed class OcrFormHintsTests
{
    [Fact]
    public void TheFileIsReadOnceForAllThePagesOfARun()
    {
        int reads = 0;
        var hints = new OcrFormHints(() =>
        {
            reads++;
            return [[], [], []];
        });

        for (int page = 0; page < 3; page++) Assert.Empty(hints.ForPage(page));

        Assert.Equal(1, reads);
    }

    [Fact]
    public void AFileThatCannotBeReadGivesNoHintsAndIsNotReadAgain()
    {
        int reads = 0;
        var hints = new OcrFormHints(() => { reads++; throw new InvalidDataException("damaged"); });

        Assert.Empty(hints.ForPage(0));
        Assert.Empty(hints.ForPage(1));

        Assert.Equal(1, reads);
    }

    [Fact]
    public void APageBeyondTheFileGivesNoHints()
    {
        var hints = new OcrFormHints(() => [[]]);
        Assert.Empty(hints.ForPage(5));
        Assert.Empty(hints.ForPage(-1));
    }

    [Fact]
    public void TheClipboardRunNoLongerReadsTheFileForEveryPage()
    {
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "KillerPDF.csproj")))
            root = Path.GetDirectoryName(root) ?? throw new DirectoryNotFoundException("KillerPDF.csproj");
        string source = File.ReadAllText(Path.Combine(root, "Features", "Ocr", "OcrController.cs"));
        Assert.DoesNotContain("ReadFormHints(", source, StringComparison.Ordinal);
        Assert.Contains("new OcrFormHints(", source, StringComparison.Ordinal);
    }
}
