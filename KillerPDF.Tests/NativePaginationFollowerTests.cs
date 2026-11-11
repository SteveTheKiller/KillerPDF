using System.IO;
using KillerPDF.Services;
using System.Runtime.ExceptionServices;
using System.Windows.Threading;
using Xunit;

namespace KillerPDF.Tests;

// The window follows a long EPUB's pagination and shows the pages it adds. Showing them is display only: a view update
// that fails must not leave the book short of its pages, or a page jump past the frontier waits for them forever.
public sealed class NativePaginationFollowerTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    [Fact]
    public void EveryFollowingWindowReceivesItsOwnPageGrowth()
    {
        OnDispatcher(async () =>
        {
            using var gate = new ManualResetEventSlim();
            using var book = NativeWorkingDocument.OpenEpub(NativeProgressiveOpenTests.Book(),
                NativeProgressiveOpenTests.Gated(gate), inBackground: true);
            int initial = book.ShownPageCount;
            var first = new List<(int Before, int After)>();
            var second = new List<(int Before, int After)>();
            Task followingFirst = NativePaginationFollower.FollowAsync(book, TimeSpan.FromMilliseconds(20),
                () => false, (before, after, _) => first.Add((before, after)));
            Task followingSecond = NativePaginationFollower.FollowAsync(book, TimeSpan.FromMilliseconds(20),
                () => false, (before, after, _) => second.Add((before, after)));
            gate.Set();
            await Task.WhenAll(followingFirst, followingSecond).WaitAsync(Patience);
            Assert.Contains(first, update => update.Before == initial && update.After > initial);
            Assert.Contains(second, update => update.Before == initial && update.After > initial);
        });
    }

    private static void OnDispatcher(Func<Task> action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try { await action(); }
                catch (Exception error) { failure = error; }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Pagination observers did not finish.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [Fact]
    public async Task AFailedViewUpdateStillTakesEveryPageInAndShowsTheFinishedBook()
    {
        using var gate = new ManualResetEventSlim();
        using var book = NativeWorkingDocument.OpenEpub(NativeProgressiveOpenTests.Book(),
            NativeProgressiveOpenTests.Gated(gate), inBackground: true);
        var shown = new List<(int Before, int After, bool Finished)>();
        bool failed = false;

        var following = NativePaginationFollower.FollowAsync(book, TimeSpan.FromMilliseconds(20), () => false,
            (before, after, finished) =>
            {
                shown.Add((before, after, finished));
                if (failed) return;
                failed = true;
                gate.Set();
                throw new InvalidOperationException("A view could not take in the new pages.");
            });
        await Record.ExceptionAsync(() => following.WaitAsync(Patience));
        await book.Pagination.WaitAsync(Patience);

        Assert.True(failed);
        Assert.Equal(book.PageCount, book.ShownPageCount);
        Assert.Equal((book.PageCount, true), (shown[^1].After, shown[^1].Finished));
    }

    // When pagination stops the reader sees a friendly sentence; the cause goes to the app log for support.
    [Fact]
    public async Task APaginationFailureReachesTheLogWithItsCause()
    {
        using var gate = new ManualResetEventSlim();
        using var book = NativeWorkingDocument.OpenEpub(NativeProgressiveOpenTests.Book(),
            NativeOpenTests.Options() with
            {
                FontFaceResolver = request =>
                {
                    if (request.Family?.Name == "Later")
                    {
                        Assert.True(gate.Wait(Patience), "The test never opened the gate.");
                        throw new InvalidOperationException("pagination fixture failure");
                    }
                    return null;
                }
            }, inBackground: true);
        gate.Set();
        await Record.ExceptionAsync(() => book.Pagination.WaitAsync(Patience));
        Assert.True(book.Pagination.IsFaulted, "The fixture did not fault the pagination.");

        var logged = new List<(Exception Error, string Context)>();
        NativePaginationFollower.LogStopped(book, "book.epub", (error, context) => { logged.Add((error, context)); return ""; });

        var entry = Assert.Single(logged);
        Assert.Contains("pagination fixture failure", entry.Error.Message);
        Assert.Contains("book.epub", entry.Context);
    }

    // The window's wiring has no headless seam (it shows a dialog), so this pins that the stop notice also logs the cause.
    [Fact]
    public void TheWindowLogsThePaginationCauseWhereItShowsTheStopNotice()
    {
        string root = Path.GetDirectoryName(Path.GetDirectoryName(SourcePath()))!;
        string window = File.ReadAllText(Path.Combine(root, "Shell", "NativePagination.cs"));
        Assert.Contains("NativePaginationFollower.LogStopped(book, fileName, CrashReporter.Capture);", window);
    }

    private static string SourcePath([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;
}
