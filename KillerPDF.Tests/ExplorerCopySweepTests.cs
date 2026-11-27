using System.IO;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

/// <summary>
/// BF-A-012: a PDF opened from a zip folder is copied to a temp folder that nothing deleted. The sweep at startup
/// removes copy folders that have sat untouched for a week and never disturbs one in use.
/// </summary>
public sealed class ExplorerCopySweepTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 27, 12, 0, 0, DateTimeKind.Utc);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "killerpdf-sweep-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private string Copy(int ageDays, string name = "doc.pdf")
    {
        string folder = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, name);
        File.WriteAllText(file, "x");
        File.SetLastWriteTimeUtc(file, Now.AddDays(-ageDays));
        return folder;
    }

    [Fact]
    public void AFolderUntouchedForAWeekIsDeletedAndANewerOneKept()
    {
        string old = Copy(8);
        string fresh = Copy(2);
        int removed = ExplorerCopySweep.Sweep(_root, Now.AddDays(-7), _ => false);
        Assert.Equal(1, removed);
        Assert.False(Directory.Exists(old));
        Assert.True(Directory.Exists(fresh));
    }

    [Fact]
    public void AFolderIsAsNewAsItsNewestFile()
    {
        string folder = Copy(30);
        string recent = Path.Combine(folder, "edited.pdf");
        File.WriteAllText(recent, "y");
        File.SetLastWriteTimeUtc(recent, Now.AddDays(-1));
        Assert.Equal(0, ExplorerCopySweep.Sweep(_root, Now.AddDays(-7), _ => false));
        Assert.True(Directory.Exists(folder));
    }

    // The sweep runs before the stored session reopens its tabs, so a copy the session lists is in use even though
    // nothing has it open yet.
    [Fact]
    public void ACopyTheStoredSessionReopensIsKept()
    {
        string listed = Copy(30, "listed.pdf");
        string unlisted = Copy(30, "unlisted.pdf");
        var inUse = ExplorerCopySweep.InUse([Path.Combine(listed, "LISTED.PDF")]);
        Assert.Equal(1, ExplorerCopySweep.Sweep(_root, Now.AddDays(-7), inUse));
        Assert.True(Directory.Exists(listed));
        Assert.False(Directory.Exists(unlisted));
        Assert.Equal(0, ExplorerCopySweep.Sweep(_root, Now.AddDays(-7), ExplorerCopySweep.InUse(null)));
        Assert.True(Directory.Exists(listed));
    }

    [Fact]
    public void StartupKeepsTheStoredSessionsCopies()
    {
        Assert.Contains("ExplorerCopySweep.InUse(StoredSessionFiles())", ShellSource.Read("App.xaml.cs"));
    }

    [Fact]
    public void AFolderWithAFileInUseIsKept()
    {
        string busy = Copy(30, "busy.pdf");
        string idle = Copy(30, "idle.pdf");
        string busyFile = Path.Combine(busy, "busy.pdf");
        Assert.Equal(1, ExplorerCopySweep.Sweep(_root, Now.AddDays(-7), path => path == busyFile));
        Assert.True(Directory.Exists(busy));
        Assert.False(Directory.Exists(idle));
    }

    [Fact]
    public void AFileLockedByAnotherProcessCountsAsInUse()
    {
        string folder = Copy(30);
        string file = Path.Combine(folder, "doc.pdf");
        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.True(ExplorerCopySweep.IsLocked(file));
            Assert.Equal(0, ExplorerCopySweep.Sweep(_root, Now.AddDays(-7), ExplorerCopySweep.IsLocked));
            Assert.True(Directory.Exists(folder));
        }
        Assert.False(ExplorerCopySweep.IsLocked(file));
        Assert.Equal(1, ExplorerCopySweep.Sweep(_root, Now.AddDays(-7), ExplorerCopySweep.IsLocked));
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public void OnlyGuidNamedFoldersAreTouched()
    {
        string other = Path.Combine(_root, "notes");
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "a.txt"), "x");
        File.SetLastWriteTimeUtc(Path.Combine(other, "a.txt"), Now.AddDays(-90));
        Assert.Equal(0, ExplorerCopySweep.Sweep(_root, Now.AddDays(-7), _ => false));
        Assert.True(Directory.Exists(other));
    }

    [Fact]
    public void AMissingRootIsFineAndAThrowingCheckSkipsTheFolder()
    {
        Assert.Equal(0, ExplorerCopySweep.Sweep(Path.Combine(_root, "absent"), Now, _ => false));
        string folder = Copy(30);
        Assert.Equal(0, ExplorerCopySweep.Sweep(_root, Now.AddDays(-7), _ => throw new IOException("busy")));
        Assert.True(Directory.Exists(folder));
    }

    [Fact]
    public void StartupSweepsTheExplorerCopiesAfterTheStaleTempCleanup()
    {
        string app = ShellSource.Read("App.xaml.cs");
        Assert.Contains("ExplorerCopySweep.Sweep(", app);
        Assert.True(app.IndexOf("ExplorerCopySweep.Sweep(", StringComparison.Ordinal) > app.IndexOf("CleanupStaleTemps();", StringComparison.Ordinal));
        Assert.Contains("ShellItems.CopyRoot", app);
        string command = ShellSource.Read("Services", "ExplorerOpenCommand.cs");
        Assert.Contains("Path.Combine(CopyRoot, Guid.NewGuid().ToString(\"N\"))", command);
    }
}
