using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class NativeConversionDestinationTests
{
    [Theory]
    [InlineData("C:/books/output.pdf", "c:/BOOKS/output.pdf")]
    [InlineData("C:/books/output.pdf", "C:/books/chapter/../output.pdf")]
    public void MatchesCanonicalOpenPaths(string destination, string openPath) =>
        Assert.True(NativePdfConversion.IsDestinationOpen(destination, [openPath]));

    [Fact]
    public void LaterPathsAreCheckedAfterEmptyInvalidAndUnrelatedEntries() =>
        Assert.True(NativePdfConversion.IsDestinationOpen("C:/books/output.pdf",
            [null, "", "bad\0path", "C:/books/other.pdf", "C:/books/output.pdf"]));

    [Fact]
    public void UnrelatedPathsDoNotBlockDestination() =>
        Assert.False(NativePdfConversion.IsDestinationOpen("C:/books/output.pdf",
            [null, "", "bad\0path", "C:/books/other.pdf", "C:/books/output.pdf.backup"]));

    [Fact]
    public void EmptyWindowInventoryDoesNotBlockDestination() =>
        Assert.False(NativePdfConversion.IsDestinationOpen("C:/books/output.pdf", []));
}
