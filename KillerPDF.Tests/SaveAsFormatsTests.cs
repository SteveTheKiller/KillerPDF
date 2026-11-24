using KillerPDF.Shell;
using Xunit;

namespace KillerPDF.Tests;

// Save as offers EPUB beside PDF, the book File > Export writes, except where the copy becomes the open document.
public sealed class SaveAsFormatsTests
{
    [Fact]
    public void SaveAsOffersPdfThenEpub() =>
        Assert.Equal("PDF files|*.pdf|EPUB books|*.epub", SaveAsFormats.Filter("PDF files", "EPUB books", offerEpub: true));

    [Fact]
    public void ASaveThatOpensTheCopyOffersPdfOnly() =>
        Assert.Equal("PDF files|*.pdf", SaveAsFormats.Filter("PDF files", "EPUB books", offerEpub: false));

    [Theory]
    [InlineData(@"C:\Books\Novel.epub", true)]
    [InlineData(@"C:\Books\Novel.EPUB", true)]
    [InlineData(@"C:\Books\Novel.pdf", false)]
    [InlineData(@"C:\Books\Novel.epub.pdf", false)]
    public void TheFileNameDecidesTheFormat(string fileName, bool epub) =>
        Assert.Equal(epub, SaveAsFormats.IsEpub(fileName));
}
