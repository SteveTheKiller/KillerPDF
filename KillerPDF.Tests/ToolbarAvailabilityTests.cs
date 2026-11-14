using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class ToolbarAvailabilityTests
{
    private static readonly ToolbarAvailability.State NoDocument = new(false, false, false, false, 0, -1, 0);
    private static readonly ToolbarAvailability.State ThreePagesSecondSelected = new(true, true, false, false, 1, 1, 3);

    [Fact]
    public void SaveMenuWaitsForADocumentNowThatDocumentToolsHaveTheirOwnPanel()
    {
        // Document tools moved to their sidebar panel, so with no PDF open the Save dropdown has
        // nothing to offer and waits for a document like the Save button beside it.
        Assert.False(ToolbarAvailability.MainEnabled("save", NoDocument));
        Assert.False(ToolbarAvailability.GroupEnabled("save", NoDocument));
    }

    [Theory]
    [InlineData("ocr")]
    [InlineData("print")]
    [InlineData("flatten")]
    [InlineData("undo")]
    [InlineData("delete")]
    public void OtherGroupsWaitForADocument(string id)
    {
        Assert.False(ToolbarAvailability.MainEnabled(id, NoDocument));
        Assert.False(ToolbarAvailability.GroupEnabled(id, NoDocument));
    }

    [Fact]
    public void NewAndOpenNeedNoDocument()
    {
        Assert.True(ToolbarAvailability.MainEnabled("new", NoDocument));
        Assert.True(ToolbarAvailability.MainEnabled("open", NoDocument));
    }

    [Fact]
    public void PageCommandsFollowTheSelection()
    {
        Assert.True(ToolbarAvailability.MainEnabled("delete", ThreePagesSecondSelected));
        Assert.True(ToolbarAvailability.MainEnabled("moveup", ThreePagesSecondSelected));
        Assert.True(ToolbarAvailability.MainEnabled("movedown", ThreePagesSecondSelected));
        Assert.True(ToolbarAvailability.MainEnabled("undo", ThreePagesSecondSelected));
        Assert.False(ToolbarAvailability.MainEnabled("redo", ThreePagesSecondSelected));
        Assert.False(ToolbarAvailability.MainEnabled("delete", ThreePagesSecondSelected with { SelectedCount = 3 }));
        Assert.False(ToolbarAvailability.MainEnabled("movedown", ThreePagesSecondSelected with { SelectedIndex = 2 }));
    }
}
