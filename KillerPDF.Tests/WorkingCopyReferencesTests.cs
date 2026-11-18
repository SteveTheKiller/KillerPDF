using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

// After an edit the superseded working copy is deleted unless something still names it (BF-A005). A tab
// that has not loaded yet names it by DeferredPath, and a tab opened on a recovered or temp copy names it
// by OriginalFile; both would lose their file if only CurrentFile counted.
public sealed class WorkingCopyReferencesTests
{
    private const string Copy = @"C:\Temp\KillerPDF\edit-1.pdf";

    private static WorkingCopyReferences.SessionFiles Other(string? current = null, string? original = null, string? deferred = null)
        => new(current, original, deferred, IsEdited: false);

    [Fact]
    public void NothingNamingTheCopyLetsItGo()
    {
        Assert.False(WorkingCopyReferences.InUse(Copy,
            [Other(@"C:\Temp\KillerPDF\other.pdf", @"C:\Docs\a.pdf"), new(Copy, @"C:\Docs\b.pdf", null, IsEdited: true)],
            [@"C:\Docs\c.pdf", null]));
    }

    [Fact]
    public void AnotherTabsCurrentFileKeepsIt()
        => Assert.True(WorkingCopyReferences.InUse(Copy, [Other(current: Copy.ToUpperInvariant())], []));

    [Fact]
    public void AnotherTabsDeferredPathKeepsIt()
        => Assert.True(WorkingCopyReferences.InUse(Copy, [Other(deferred: Copy)], []));

    [Fact]
    public void AnotherTabsOriginalFileKeepsIt()
        => Assert.True(WorkingCopyReferences.InUse(Copy, [Other(original: Copy)], []));

    [Fact]
    public void TheEditedTabsOwnOriginalFileKeepsItButItsStaleCurrentFileDoesNot()
    {
        Assert.True(WorkingCopyReferences.InUse(Copy, [new(null, Copy, null, IsEdited: true)], []));
        Assert.False(WorkingCopyReferences.InUse(Copy, [new(Copy, @"C:\Docs\b.pdf", null, IsEdited: true)], []));
    }

    [Fact]
    public void ALiveFileOfAnotherWindowKeepsIt()
        => Assert.True(WorkingCopyReferences.InUse(Copy, [], [null, Copy]));

    [Fact]
    public void TheWindowFeedsEveryNameIntoTheCheck()
    {
        string reload = ShellSource.Read("Shell", "TempReload.cs");
        string check = ShellSource.Between(reload, "private bool WorkingCopyInUse(string path)", "private static void DeleteWorkingCopy(");
        Assert.Contains("WorkingCopyReferences.InUse(path", check);
        foreach (string name in new[] { "session.CurrentFile", "session.OriginalFile", "session.DeferredPath", "window._currentFile", "window._originalFile", "_originalFile" })
            Assert.Contains(name, check);
    }
}
