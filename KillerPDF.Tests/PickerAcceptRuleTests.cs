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

    // #513: Shift+Enter or Shift+click on Open opens the chosen files in a new window.
    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    public void ShiftInOpenModeAsksForANewWindow(bool shift, bool openMode, bool expected)
    {
        Assert.Equal(expected, PickerAcceptRule.OpensInNewWindow(shift, openMode));
    }

    // #513: Enter on the file list opens a single folder and accepts files, so Shift+Enter has a list to act on.
    [Theory]
    [InlineData(1, true, true)]
    [InlineData(2, true, false)]
    [InlineData(1, false, false)]
    public void EnterOnTheListOpensOneFolderOrAcceptsFiles(int selected, bool firstIsFolder, bool expected)
    {
        Assert.Equal(expected, PickerAcceptRule.ListEnterOpensFolder(selected, firstIsFolder));
    }

    [Fact]
    public void TheMainOpenDialogTakesSeveralFilesAndCanOpenThemInANewWindow()
    {
        string open = ShellSource.Between(ShellSource.Read("Shell", "FileOperations.cs"), "private void Open_Click(", "private sealed record OpenMenuCommand");
        Assert.Contains("Multiselect = true", open);
        Assert.Contains("dlg.OpenInNewWindow", open);
        string dialog = ShellSource.Read("Controls", "FileDialog.xaml.cs");
        Assert.Contains("PickerAcceptRule.OpensInNewWindow(", dialog);
        Assert.Contains("PickerAcceptRule.ListEnterOpensFolder(", dialog);
        // The Extended-selection list marks Enter and Shift+Enter handled itself, so the list's Enter has to be
        // caught on the way down; a bubbling OnKeyDown never sees it.
        string preview = ShellSource.Between(dialog, "protected override void OnPreviewKeyDown(", "protected override void OnKeyDown(");
        Assert.Contains("PickerAcceptRule.ListEnterOpensFolder(", preview);
    }

    // #513: Enter in the name box probes for a folder before it accepts. Shift has to be read before that
    // await, or a quick Shift+Enter has let go of Shift and the files open as tabs of the same window.
    [Fact]
    public void TheNameBoxReadsShiftBeforeItsFolderProbe()
    {
        string dialog = ShellSource.Read("Controls", "FileDialog.xaml.cs");
        string keyDown = ShellSource.Between(dialog, "private async void FileNameBox_KeyDown(", "private async Task AcceptTypedNameAsync(");
        int shift = keyDown.IndexOf("ModifierKeys.Shift", StringComparison.Ordinal);
        int await = keyDown.IndexOf("await ", StringComparison.Ordinal);
        Assert.True(shift >= 0 && shift < await, "The name box must read Shift before it awaits.");
        string typed = ShellSource.Between(dialog, "private async Task AcceptTypedNameAsync(bool shift)", "/// <summary>A wildcard retargets the listing");
        Assert.DoesNotContain("Accept();", typed);
        Assert.Contains("Accept(shift);", typed);
    }
}
