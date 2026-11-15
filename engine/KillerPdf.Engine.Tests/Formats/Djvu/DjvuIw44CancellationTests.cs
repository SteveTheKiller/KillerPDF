using System.Diagnostics;
using KillerPdf.Engine.Formats.Djvu;
using Xunit;

namespace KillerPdf.Engine.Tests.Formats.Djvu;

// A timing test runs alone: sharing the cores with the rest of the suite measured the other tests' load, and it failed
// two runs in six on the lab VM while the decoder itself stopped promptly.
[CollectionDefinition(nameof(DjvuIw44CancellationCollection), DisableParallelization = true)]
public sealed class DjvuIw44CancellationCollection;

[Collection(nameof(DjvuIw44CancellationCollection))]
public sealed class DjvuIw44CancellationTests
{
    // The largest color decode the limits admit takes about 14 seconds on the development machine; once cancelled it
    // stops within a block row of work. The time is measured from the moment the cancellation fires, not from the start,
    // so the up-front plane allocation (about 173 MB here) is not counted against the decoder's response. The moment is
    // written before Cancel runs, on a thread of its own: a registered callback runs after the token flips, and in the
    // full suite the decode saw the cancellation and returned before that callback had written the time.
    [Fact]
    public void TheLargestAdmittedDecodeStopsWhenCancelled()
    {
        var chunks = DjvuIw44RealPageTests.CodedChunks(color: true, 4800, 6016, crcb: 0x80, 8u, payloadDivisor: 8);
        var watch = Stopwatch.StartNew();
        long cancelledAt = -1;
        using var cancellation = new CancellationTokenSource();
        var canceller = new Thread(() =>
        {
            Thread.Sleep(200);
            Interlocked.Exchange(ref cancelledAt, watch.ElapsedMilliseconds);
            cancellation.Cancel();
        }) { IsBackground = true };
        canceller.Start();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            DjvuIw44Decoder.DecodeColor(chunks, 4800, 6016, cancellation.Token, callerBytesPerPixel: 0));
        long stoppedAt = watch.ElapsedMilliseconds;
        canceller.Join();

        long cancelled = Interlocked.Read(ref cancelledAt);
        Assert.True(cancelled >= 0 && stoppedAt >= cancelled, "The decode stopped before it was cancelled.");
        long response = stoppedAt - cancelled;
        Assert.True(response < 3000, $"The decode took {response} ms to stop after it was cancelled.");
    }
}
