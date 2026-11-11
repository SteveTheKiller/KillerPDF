using Xunit;

namespace KillerPDF.Tests;

// Reading the page information for Extract Image can fail when the working copy cannot be read. That
// failure shows the Extract Image message like any other, instead of escaping the menu click.
public sealed class ExtractImageFailureTests
{
    [Fact]
    public void ReadingThePageInformationIsInsideTheTryThatShowsTheFailureMessage()
    {
        string source = ShellSource.Read("Shell", "ExtractImage.cs");
        string handled = ShellSource.Between(source, "try {", "catch (Exception ex) when (ex is not OutOfMemoryException)");

        Assert.Contains("ReadPageInformation(", handled, StringComparison.Ordinal);
        Assert.Contains("_pageRotations.TryGetValue(pageIndex", handled, StringComparison.Ordinal);
        int firstTry = source.IndexOf("try", StringComparison.Ordinal);
        Assert.True(source.IndexOf("ReadPageInformation(", StringComparison.Ordinal) > firstTry);
    }
}