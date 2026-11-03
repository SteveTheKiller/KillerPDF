using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class OcrPdfOutputSuggestionTests
{
    [Fact]
    public void OriginalSourceSuppliesNameAndFolderInsteadOfWorkingCopy()
    {
        var suggestion = OcrPdfOutputSuggestion.Create(@"C:\casefiles\source.pdf", @"C:\temp\working.pdf");

        Assert.Equal("source-searchable.pdf", suggestion.FileName);
        Assert.Equal(@"C:\casefiles", suggestion.InitialDirectory);
    }

    [Theory]
    [InlineData(null, null, "document-searchable.pdf", "")]
    [InlineData(null, @"C:\temp\imported.pdf", "imported-searchable.pdf", "")]
    [InlineData(@"relative\source.pdf", @"C:\temp\working.pdf", "source-searchable.pdf", "")]
    [InlineData(@"C:source.pdf", @"C:\temp\working.pdf", "source-searchable.pdf", "")]
    [InlineData(@"\\server\share\casefiles\source.pdf", @"C:\temp\working.pdf", "source-searchable.pdf", @"\\server\share\casefiles")]
    [InlineData(@"C:\casefiles\source report.PDF", null, "source report-searchable.pdf", @"C:\casefiles")]
    [InlineData(@"C:\source.pdf", null, "source-searchable.pdf", @"C:\")]
    [InlineData("", @"C:\temp\working.pdf", "document-searchable.pdf", "")]
    [InlineData("   ", null, "document-searchable.pdf", "")]
    [InlineData("C:\\casefiles\\bad\0.pdf", @"C:\temp\working.pdf", "document-searchable.pdf", "")]
    [InlineData(@"C:\casefiles\bad?.pdf", @"C:\temp\working.pdf", "document-searchable.pdf", "")]
    [InlineData(null, @"C:\temp\bad?.pdf", "document-searchable.pdf", "")]
    [InlineData(@"C:\casefiles\", @"C:\temp\working.pdf", "document-searchable.pdf", "")]
    public void SourceAndImportedPathsHaveBoundedSuggestions(
        string? originalFile, string? workingFile, string expectedName, string expectedDirectory)
    {
        var suggestion = OcrPdfOutputSuggestion.Create(originalFile, workingFile);

        Assert.Equal(expectedName, suggestion.FileName);
        Assert.Equal(expectedDirectory, suggestion.InitialDirectory);
    }
}
