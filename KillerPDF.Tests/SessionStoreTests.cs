using System.IO;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

// #502: the whole-session store. Everything here runs on files in a private temp folder and
// never creates a window.
public sealed class SessionStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kp-session-" + Guid.NewGuid().ToString("N"));

    public SessionStoreTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string Doc(string name)
    {
        string path = Path.Combine(_root, name);
        File.WriteAllText(path, "%PDF-1.7");
        return path;
    }

    private string StoreFile => Path.Combine(_root, "session.json");

    [Fact]
    public void ASavedSessionReadsBackWithItsOrderPagesZoomAndActiveTabs()
    {
        string a = Doc("a.pdf"), b = Doc("b.pdf"), c = Doc("c.pdf");
        var saved = new SessionSnapshot([
            new SessionWindow([
                new SessionPane([new SessionTab(a, 3, 1.5), new SessionTab(b, 0, 0.75)], 1),
                new SessionPane([new SessionTab(c, 7, 2)], 0)]),
            new SessionWindow([new SessionPane([new SessionTab(c, 1, 1)], 0)])]);

        SessionStore.Save(StoreFile, saved);
        SessionSnapshot? read = SessionStore.Load(StoreFile);

        Assert.NotNull(read);
        Assert.Equal(2, read!.Windows.Count);
        SessionPane first = read.Windows[0].Panes[0];
        Assert.Equal([a, b], first.Tabs.Select(tab => tab.Path));
        Assert.Equal(3, first.Tabs[0].Page);
        Assert.Equal(1.5, first.Tabs[0].Zoom);
        Assert.Equal(0.75, first.Tabs[1].Zoom);
        Assert.Equal(1, first.Active);
        Assert.Equal(7, read.Windows[0].Panes[1].Tabs[0].Page);
        Assert.Equal(4, read.FileCount);
    }

    [Fact]
    public void AFailedWriteLeavesThePreviousSessionWholeAndNoTemporaryFile()
    {
        string a = Doc("a.pdf"), b = Doc("b.pdf");
        SessionStore.Save(StoreFile, OnePane(a));

        using (AtomicFileOutput.UseFileOperations(new AtomicFileOutput.FileOperations
        {
            FlushToDisk = _ => throw new IOException("disk full")
        }))
            Assert.Throws<IOException>(() => SessionStore.Save(StoreFile, OnePane(a, b)));

        Assert.Equal([a], SessionStore.Load(StoreFile)!.Windows[0].Panes[0].Tabs.Select(tab => tab.Path));
        Assert.Equal(["session.json"], Directory.GetFiles(_root, "*session*").Select(Path.GetFileName));
    }

    [Fact]
    public void ADamagedStoreReadsAsNoSession()
    {
        File.WriteAllText(StoreFile, "{ not json");
        Assert.Null(SessionStore.Load(StoreFile));
        Assert.Null(SessionStore.Load(Path.Combine(_root, "absent.json")));
    }

    [Fact]
    public void MissingFilesAreSkippedAndNamedOnce()
    {
        string a = Doc("a.pdf"), gone = Path.Combine(_root, "moved.pdf"), b = Doc("b.pdf");
        var saved = new SessionSnapshot([
            new SessionWindow([new SessionPane([new SessionTab(a, 0, 1), new SessionTab(gone, 2, 1), new SessionTab(b, 0, 1)], 1)]),
            new SessionWindow([new SessionPane([new SessionTab(gone, 0, 1)], 0)])]);

        (SessionSnapshot present, IReadOnlyList<string> missing) = SessionStore.WithoutMissing(saved, File.Exists);

        Assert.Equal([gone], missing);
        Assert.Single(present.Windows);
        SessionPane pane = present.Windows[0].Panes[0];
        Assert.Equal([a, b], pane.Tabs.Select(tab => tab.Path));
        // The active tab was the missing one; the tab before it takes its place.
        Assert.Equal(0, pane.Active);
    }

    [Fact]
    public void ChoosingNeverWipesTheStoredSession()
    {
        string a = Doc("a.pdf");
        SessionStore.Save(StoreFile, OnePane(a));
        var legacy = new Dictionary<string, string?>
        {
            ["OpenTabs"] = a, ["ActiveTab"] = a, ["OpenTabsB"] = a, ["ActiveTabB"] = a, ["LastFile"] = a, ["Other"] = "kept"
        };

        SessionStore.ApplyMode(SessionRestoreMode.Never, StoreFile, key => legacy.Remove(key));

        Assert.False(File.Exists(StoreFile));
        Assert.Equal(["Other"], legacy.Keys);
        Assert.Equal(SessionRestoreAction.None,
            SessionStore.Decide(SessionRestoreMode.Never, ["KillerPDF.exe"], File.Exists, forced: false, restorableFiles: 1));
    }

    [Fact]
    public void AskAndAlwaysKeepTheStoredSession()
    {
        string a = Doc("a.pdf");
        SessionStore.Save(StoreFile, OnePane(a));
        SessionStore.ApplyMode(SessionRestoreMode.Always, StoreFile, _ => throw new InvalidOperationException());
        SessionStore.ApplyMode(SessionRestoreMode.Ask, StoreFile, _ => throw new InvalidOperationException());
        Assert.True(File.Exists(StoreFile));
    }

    [Fact]
    public void ALaunchWithAFileArgumentNeverRestores()
    {
        string a = Doc("a.pdf");
        foreach (SessionRestoreMode mode in Enum.GetValues<SessionRestoreMode>())
            Assert.Equal(SessionRestoreAction.None,
                SessionStore.Decide(mode, ["KillerPDF.exe", a], File.Exists, forced: false, restorableFiles: 3));
        Assert.Equal(SessionRestoreAction.None,
            SessionStore.Decide(SessionRestoreMode.Always, ["KillerPDF.exe", "killerpdf:open?x"], File.Exists, forced: false, restorableFiles: 3));
    }

    [Fact]
    public void APlainLaunchFollowsTheSetting()
    {
        string[] args = ["KillerPDF.exe"];
        Assert.Equal(SessionRestoreAction.Silent, SessionStore.Decide(SessionRestoreMode.Always, args, File.Exists, false, 2));
        Assert.Equal(SessionRestoreAction.Ask, SessionStore.Decide(SessionRestoreMode.Ask, args, File.Exists, false, 2));
        Assert.Equal(SessionRestoreAction.None, SessionStore.Decide(SessionRestoreMode.Ask, args, File.Exists, false, 0));
        // A restart after a render failure brings the session back without asking.
        Assert.Equal(SessionRestoreAction.Silent,
            SessionStore.Decide(SessionRestoreMode.Ask, ["KillerPDF.exe", "--render-recovery", "12"], File.Exists, forced: true, 2));
    }

    [Fact]
    public void TheSettingDefaultsToAskAndRoundTrips()
    {
        Assert.Equal(SessionRestoreMode.Ask, SessionStore.ParseMode(null));
        Assert.Equal(SessionRestoreMode.Ask, SessionStore.ParseMode("garbage"));
        foreach (SessionRestoreMode mode in Enum.GetValues<SessionRestoreMode>())
            Assert.Equal(mode, SessionStore.ParseMode(SessionStore.ModeValue(mode)));
    }

    [Fact]
    public void OnlyRealFilesOnDiskAreCaptured()
    {
        string a = Doc("a.pdf"), temp = Doc("killerpdf_import_1.pdf");
        SessionPane pane = SessionStore.PaneFrom(
            [(a, 4, 1.25), (null, 0, 1), (temp, 0, 1), (Path.Combine(_root, "gone.pdf"), 0, 1), (a, 9, 1)],
            activePath: temp,
            isTemporary: path => path == temp);

        Assert.Equal([a], pane.Tabs.Select(tab => tab.Path));
        Assert.Equal(4, pane.Tabs[0].Page);
        Assert.Equal(0, pane.Active);
    }

    [Fact]
    public void SeveralWindowsRestoreIntoOneInOrder()
    {
        string a = Doc("a.pdf"), b = Doc("b.pdf"), c = Doc("c.pdf");
        var saved = new SessionSnapshot([
            new SessionWindow([new SessionPane([new SessionTab(a, 0, 1), new SessionTab(b, 0, 1)], 1),
                               new SessionPane([new SessionTab(c, 0, 1)], 0)]),
            new SessionWindow([new SessionPane([new SessionTab(c, 5, 1), new SessionTab(a, 0, 1)], 0)])]);

        SessionWindow one = SessionStore.IntoOneWindow(saved);

        Assert.Single(one.Panes);
        Assert.Equal([a, b, c], one.Panes[0].Tabs.Select(tab => tab.Path));
        Assert.Equal(b, one.Panes[0].Tabs[one.Panes[0].Active].Path);
        // A single split window keeps both panes.
        Assert.Equal(2, SessionStore.IntoOneWindow(new SessionSnapshot([saved.Windows[0]])).Panes.Count);
    }

    [Fact]
    public void AnOlderSessionInTheSettingsIsReadWhenNoStoreExists()
    {
        string a = Doc("a.pdf"), b = Doc("b.pdf"), c = Doc("c.pdf");
        var legacy = new Dictionary<string, string>
        {
            ["OpenTabs"] = a + "|" + b, ["ActiveTab"] = b, ["OpenTabsB"] = c
        };

        SessionSnapshot snapshot = SessionStore.FromLegacy(key => legacy.GetValueOrDefault(key));

        SessionWindow window = Assert.Single(snapshot.Windows);
        Assert.Equal([a, b], window.Panes[0].Tabs.Select(tab => tab.Path));
        Assert.Equal(1, window.Panes[0].Active);
        Assert.Equal([c], window.Panes[1].Tabs.Select(tab => tab.Path));
    }

    [Fact]
    public void RecoveredOriginalsAreDroppedFromTheStore()
    {
        string a = Doc("a.pdf"), b = Doc("b.pdf");
        SessionStore.Save(StoreFile, OnePane(a, b));

        SessionStore.RemoveFiles(StoreFile, [a.ToUpperInvariant()]);

        Assert.Equal([b], SessionStore.Load(StoreFile)!.Windows[0].Panes[0].Tabs.Select(tab => tab.Path));
    }

    [Fact]
    public void RestoredTabsAreDeferredUntilOpened()
    {
        string a = Doc("a.pdf"), b = Doc("locked.pdf");
        var pane = new SessionPane([new SessionTab(a, 2, 1.5), new SessionTab(b, 6, 0.5)], 1);

        // Held open with no sharing, the way a file nobody may read looks: building the tabs must
        // not touch either file.
        List<FakeTab> tabs;
        FakeTab? active;
        using (new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.None))
            (tabs, active) = SessionStore.Build(pane, path => new FakeTab(path),
                (tab, stored) => { tab.Page = stored.Page; tab.Zoom = stored.Zoom; });

        Assert.Equal([a, b], tabs.Select(tab => tab.DeferredPath));
        Assert.Equal(6, tabs[1].Page);
        Assert.Equal(0.5, tabs[1].Zoom);
        Assert.Same(tabs[1], active);
    }

    private sealed class FakeTab(string path)
    {
        public string DeferredPath { get; } = path;
        public int Page { get; set; }
        public double Zoom { get; set; }
    }

    private static SessionSnapshot OnePane(params string[] paths)
        => new([new SessionWindow([new SessionPane([.. paths.Select(path => new SessionTab(path, 0, 1))], 0)])]);
}
