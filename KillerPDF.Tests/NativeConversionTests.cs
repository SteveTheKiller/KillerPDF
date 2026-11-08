using System.IO;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class NativeConversionTests
{
    // The layout refuses a page whose rounded-up CSS pixel area exceeds 4,194,304 (128 MiB of
    // two float RGBA planes). px = mm * 96 / 25.4, so 541.8 mm is 2047.75 px, which rounds up to
    // 2048 and fills the limit exactly; 542 mm is 2048.50 px, rounding up to 2049 (4,198,401 px).
    [Theory]
    [InlineData(210, 297, 15, true)]
    [InlineData(541.8, 541.8, 15, true)]
    [InlineData(542, 542, 15, false)]
    [InlineData(2000, 2000, 15, false)]
    [InlineData(2000, 100, 15, true)]
    // A 25 mm page with 11 mm margins leaves 3 mm (11.3 px), less than the 12 pt (16 px) body text.
    [InlineData(25, 25, 11, false)]
    [InlineData(25, 25, 10, true)]
    [InlineData(210, 297, -1, false)]
    [InlineData(double.NaN, 297, 15, false)]
    public void ConversionPageSizesMatchWhatEpubLayoutAccepts(double width, double height, double margin, bool fits) =>
        Assert.Equal(fits, NativePdfConversion.FitsLayout(width, height, margin));

    [Fact]
    public void AConfirmedReplaceWritesTheConvertedPdfOverTheExistingFile()
    {
        string directory = TempDirectory();
        try
        {
            string source = Path.Combine(directory, "book.djvu"), destination = Path.Combine(directory, "book.pdf");
            File.WriteAllBytes(source, NativeWorkingDocumentTests.DjvuPage());
            File.WriteAllText(destination, "older");
            using var owner = NativeWorkingDocument.OpenDjvu(File.ReadAllBytes(source));
            NativePdfConversion.WriteNewDjvu(owner, source, destination, replaceExisting: true);
            Assert.Equal("%PDF-"u8.ToArray(), File.ReadAllBytes(destination)[..5]);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void AnUnconfirmedExistingDestinationIsRefusedBeforeAnyConversionWork()
    {
        string directory = TempDirectory();
        try
        {
            string source = Path.Combine(directory, "book.djvu"), destination = Path.Combine(directory, "book.pdf");
            File.WriteAllBytes(source, NativeWorkingDocumentTests.DjvuPage());
            File.WriteAllText(destination, "older");
            var owner = NativeWorkingDocument.OpenDjvu(File.ReadAllBytes(source));
            // A closed owner fails as soon as conversion starts, so an IOException here proves the
            // destination was checked first.
            owner.Close();
            Assert.Throws<IOException>(() =>
                NativePdfConversion.WriteNewDjvu(owner, source, destination, replaceExisting: false));
            Assert.Equal("older", File.ReadAllText(destination));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void AConversionThatFailsPartwayLeavesTheFileItWouldReplaceIntact()
    {
        string directory = TempDirectory();
        try
        {
            string source = Path.Combine(directory, "book.djvu"), destination = Path.Combine(directory, "book.pdf");
            File.WriteAllBytes(source, NativeWorkingDocumentTests.DjvuPage());
            File.WriteAllText(destination, "older");
            var owner = NativeWorkingDocument.OpenDjvu(File.ReadAllBytes(source));
            owner.Close();
            Assert.Throws<ObjectDisposedException>(() =>
                NativePdfConversion.WriteNewDjvu(owner, source, destination, replaceExisting: true));
            Assert.Equal("older", File.ReadAllText(destination));
            Assert.Equal(new[] { "book.djvu", "book.pdf" },
                Directory.GetFiles(directory).Select(Path.GetFileName).Order().ToArray());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void EpubConversionKeepsGoingPastARefusedChapter()
    {
        string directory = TempDirectory();
        try
        {
            string source = Path.Combine(directory, "book.epub"), destination = Path.Combine(directory, "book.pdf");
            // The second chapter uses scripting, which stays a chapter refusal.
            File.WriteAllBytes(source, NativeDiagnosticsTests.Book("<p>A</p>", "<p onclick='go()'>A</p>", "<p>A</p>"));
            var font = NativeOpenTests.Options().DefaultFont;
            using var owner = NativeWorkingDocument.OpenEpub(File.ReadAllBytes(source),
                NativeOpenTests.Options() with { IsolateChapterFailures = true });
            var options = NativePdfConversion.EpubOptions(font, null, 595.28, 841.89, 42.52, "Chapter unavailable");
            NativePdfConversion.WriteNewEpub(owner, source, destination, options);
            var converted = new KillerPdf.Engine.Documents.PdfPageContentReader(
                KillerPdf.Engine.Documents.PdfDocument.Open(File.ReadAllBytes(destination)));
            // One page per chapter, the refused one as its unavailable-chapter page.
            Assert.Equal(3, converted.PageCount);
            Assert.Contains("Chapter unavailable", converted.Read(1).Text);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static string TempDirectory() =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "kpb3-" + Guid.NewGuid().ToString("N"))).FullName;
}
