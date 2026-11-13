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
    // ISO 32000-1 annex C.2 caps a PDF page side at 14,400 pt (200 in, 5,080 mm). A 50 mm tall strip
    // stays under the layout's pixel area (5,100 mm is 19,276 x 189 px = 3,643,164 px), so only the
    // PDF limit refuses 5,100 mm (14,457 pt), while exactly 5,080 mm (14,400 pt) is accepted.
    [InlineData(5080, 50, 5, true)]
    [InlineData(5100, 50, 5, false)]
    [InlineData(50, 5100, 5, false)]
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

    // A page whose text holds a byte that is not UTF-8 (0xB8, a 1999 single-byte copyright sign in the djvu2spec
    // reference edition) converts with a replacement character, and Convert to PDF hands that page's notice back.
    [Fact]
    public void DjvuConversionReturnsTheNoticeNamingAPageWhoseTextWasReplaced()
    {
        string directory = TempDirectory();
        try
        {
            string source = Path.Combine(directory, "book.djvu"), destination = Path.Combine(directory, "book.pdf");
            File.WriteAllBytes(source, NativeWorkingDocumentTests.DjvuPageWithText([0x41, 0xb8], zoned: true));
            using var owner = NativeWorkingDocument.OpenDjvu(File.ReadAllBytes(source));

            var notices = NativePdfConversion.WriteNewDjvu(owner, source, destination);

            var notice = Assert.Single(notices);
            Assert.Equal("TextEncodingInvalid", notice.Code);
            Assert.Equal(0, notice.PageIndex);
            Assert.Equal("%PDF-"u8.ToArray(), File.ReadAllBytes(destination)[..5]);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    // Text without located zones cannot be placed over the page, so the page converts without it.
    [Fact]
    public void DjvuConversionReturnsTheNoticeNamingAPageWhoseTextWasLeftOut()
    {
        string directory = TempDirectory();
        try
        {
            string source = Path.Combine(directory, "book.djvu"), destination = Path.Combine(directory, "book.pdf");
            File.WriteAllBytes(source, NativeWorkingDocumentTests.DjvuPageWithText("AB"u8.ToArray(), zoned: false));
            using var owner = NativeWorkingDocument.OpenDjvu(File.ReadAllBytes(source));

            var notices = NativePdfConversion.WriteNewDjvu(owner, source, destination);

            var notice = Assert.Single(notices);
            Assert.Equal("TextNotConverted", notice.Code);
            Assert.Equal(0, notice.PageIndex);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    // The note after conversion names the pages in two groups with consecutive pages joined: text left out (pages 3
    // and 5) and text with replaced characters (pages 1, 2 and 7). Page 3 also had replaced characters, but its text
    // was left out, so it is named once. A notice about the whole document names no page and adds nothing.
    [Fact]
    public void TheDjvuConversionNoteNamesThePagesThatLostTextAndThoseWithReplacedCharacters()
    {
        KillerPdf.Engine.Formats.Djvu.DjvuDiagnostic Notice(string code, int? page) => new(code, "") { PageIndex = page };
        var notices = new[]
        {
            Notice("TextEncodingInvalid", 0), Notice("TextEncodingInvalid", 1), Notice("TextEncodingInvalid", 2),
            Notice("TextCharactersReplaced", 6), Notice("TextCharactersReplaced", 2), Notice("TextNotConverted", 4),
            Notice("TextNotConverted", 2), Notice("Iw44RenderingLimited", null)
        };

        string? note = NativePdfConversion.DjvuConversionNote(notices, key => key + ": {0}");

        Assert.Equal("Str_NativePdf_TextLeftOut: 3, 5\nStr_NativePdf_TextReplaced: 1-2, 7", note);
        Assert.Null(NativePdfConversion.DjvuConversionNote([Notice("Iw44RenderingLimited", null)], key => key));
        Assert.Null(NativePdfConversion.DjvuConversionNote([], key => key));
    }

    // Convert to PDF shows the note once the converted PDF is open in its tab.
    [Fact]
    public void ConvertToPdfShowsTheDjvuNoteAfterOpeningTheConvertedPdf()
    {
        string shell = File.ReadAllText(Path.Combine(RepositoryRoot(), "Shell", "NativeConversion.cs"));
        int open = shell.IndexOf("viewer.OpenInNewTabExt(destination);", StringComparison.Ordinal);
        int note = shell.IndexOf("ShowDjvuConversionNote(notices);", StringComparison.Ordinal);
        Assert.True(open >= 0 && note > open, "the note follows opening the converted PDF");
        Assert.Contains("notices = NativePdfConversion.WriteNewDjvu(", shell);
    }

    [Theory]
    [InlineData("PageNotConverted", 0)]
    [InlineData("BookmarksNotConverted", null)]
    [InlineData("BookmarkNotConverted", null)]
    public void ConversionCompletionDisclosesPageAndBookmarkFailures(string code, int? page)
    {
        var diagnostic = new KillerPdf.Engine.Formats.Djvu.DjvuDiagnostic(code, "Could not preserve this content") { PageIndex = page };
        string? note = NativePdfConversion.DjvuConversionNote([diagnostic],
            key => key == "Str_Djvu_Diagnostics_Page" ? "Page {0}" : "Whole document");
        Assert.Equal((page is null ? "Whole document" : "Page 1") + ": Could not preserve this content", note);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "KillerPDF.csproj")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("KillerPDF.csproj was not found.");
    }

    private static string TempDirectory() =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "kpb3-" + Guid.NewGuid().ToString("N"))).FullName;
}
