using KillerPDF.Controls;
using Xunit;

namespace KillerPDF.Tests;

public sealed class PickerAcceptRuleTests
{
    [Fact]
    public void AQuotedCopyAsPathCanBeAcceptedAfterTheFolderListingFails()
    {
        Assert.True(PickerAcceptRule.CanAccept(false, false, false, false, "\"C:\\Data\\My book.pdf\""));
    }

    [Theory]
    [InlineData("  \"C:\\Data\\My book.pdf\"  ")]
    [InlineData("\"\\\\server\\share\\My book.pdf\"")]
    public void QuotedAbsolutePathsKeepSpacesAndNetworkRoots(string path)
    {
        Assert.True(PickerAcceptRule.CanAccept(false, false, false, false, path));
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void QuotesDoNotBypassBusyAcceptOrFolderGuards(bool listingBusy, bool accepting, bool folderMode)
    {
        Assert.False(PickerAcceptRule.CanAccept(listingBusy, false, accepting, folderMode, "\"C:\\Data\\book.pdf\""));
    }

    [Fact]
    public void AQuotedRelativeNameStillNeedsTheFolderListing()
    {
        Assert.False(PickerAcceptRule.CanAccept(false, false, false, false, "\"book.pdf\""));
    }
}
