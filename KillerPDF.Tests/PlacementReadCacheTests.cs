using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using KillerPdf.Engine.Documents;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

// The viewer's picture placement cache held the strict parse by itself, so the document's idle drop freed nothing,
// and a later page was read against a second parse while the reader stayed on the first.
public sealed class PlacementReadCacheTests
{
    [Fact]
    public void TheIdleDropFreesTheParseTheCacheWasReadFrom()
    {
        using var scope = new Scope();
        var cache = new PlacementReadCache();
        var weak = ReadFirstPage(cache, scope.Document);

        scope.Clock.Advance(PdfWorkingDocument.ContentCopyIdleLimit);
        Assert.False(scope.Document.HoldsContentCopy);
        Collect();

        Assert.False(weak.TryGetTarget(out _));
    }

    [Fact]
    public void APageReadAfterTheDropIsReadAndExtractedAgainstOneParse()
    {
        using var scope = new Scope();
        var cache = new PlacementReadCache();
        ReadFirstPage(cache, scope.Document);
        scope.Clock.Advance(PdfWorkingDocument.ContentCopyIdleLimit);

        var page1 = cache.PlacementsForPage(scope.Document, 1);
        Assert.NotNull(page1);
        var picture = Assert.Single(page1);
        // Extraction refuses a placement that was read from another parse than the one it is extracted from.
        Assert.NotNull(cache.AssetForPlacement(scope.Document, 1, 0, picture));

        // The first page is read again from the same parse instead of being served from the dropped one.
        var page0 = Assert.Single(cache.PlacementsForPage(scope.Document, 0)!);
        Assert.NotNull(cache.AssetForPlacement(scope.Document, 0, 0, page0));
    }

    [Fact]
    public void APageReadTwiceInsideTheWindowIsServedFromTheCache()
    {
        using var scope = new Scope();
        var cache = new PlacementReadCache();
        var first = cache.PlacementsForPage(scope.Document, 0);
        Assert.Same(first, cache.PlacementsForPage(scope.Document, 0));
    }

    [Fact]
    public void ReleasingTheCacheFreesTheParseOnceTheDocumentLetsItGo()
    {
        using var scope = new Scope();
        var cache = new PlacementReadCache();
        var weak = ReadFirstPage(cache, scope.Document);

        cache.Release();
        scope.Clock.Advance(PdfWorkingDocument.ContentCopyIdleLimit);
        Collect();

        Assert.False(weak.TryGetTarget(out _));
    }

    [Fact]
    public void CachedReadsKeepTheParseAliveThroughTheIdleCheck()
    {
        using var scope = new Scope();
        var cache = new PlacementReadCache();
        var parse = scope.Document.OpenStrictForContentCopy();
        var first = cache.PlacementsForPage(scope.Document, 0);
        Assert.NotNull(first);

        scope.Clock.Advance(TimeSpan.FromSeconds(90));
        Assert.Same(first, cache.PlacementsForPage(scope.Document, 0));
        scope.Clock.Advance(TimeSpan.FromSeconds(90));

        // 180 s after the one real parse, but only 90 s after the last cached read.
        Assert.True(scope.Document.HoldsContentCopy);
        Assert.Same(parse, scope.Document.OpenStrictForContentCopy());
        Assert.Same(first, cache.PlacementsForPage(scope.Document, 0));
    }

    [Fact]
    public void CachedAssetReadsKeepTheParseAliveThroughTheIdleCheck()
    {
        using var scope = new Scope();
        var cache = new PlacementReadCache();
        var picture = Assert.Single(cache.PlacementsForPage(scope.Document, 0)!);
        Assert.NotNull(cache.AssetForPlacement(scope.Document, 0, 0, picture));

        scope.Clock.Advance(TimeSpan.FromSeconds(90));
        Assert.NotNull(cache.AssetForPlacement(scope.Document, 0, 0, picture));
        scope.Clock.Advance(TimeSpan.FromSeconds(90));

        Assert.True(scope.Document.HoldsContentCopy);
    }

    [Fact]
    public void AnAssetAskedForAfterTheParseWasDroppedIsNotRememberedAsRefused()
    {
        using var scope = new Scope();
        var cache = new PlacementReadCache();
        var picture = Assert.Single(cache.PlacementsForPage(scope.Document, 0)!);
        scope.Clock.Advance(PdfWorkingDocument.ContentCopyIdleLimit);
        Assert.False(scope.Document.HoldsContentCopy);

        Assert.Null(cache.AssetForPlacement(scope.Document, 0, 0, picture));
        Assert.Equal(0, cache.AssetsRemembered);

        var again = Assert.Single(cache.PlacementsForPage(scope.Document, 0)!);
        Assert.NotNull(cache.AssetForPlacement(scope.Document, 0, 0, again));
    }

    [Fact]
    public void AnOverdueParseReplacedInlineRaisesTheDropEvent()
    {
        using var scope = new Scope();
        var old = scope.Document.OpenStrictForContentCopy();
        int dropped = 0;
        scope.Document.ContentCopyDropped += () => dropped++;

        scope.Clock.Skip(PdfWorkingDocument.ContentCopyIdleLimit);
        var replacement = scope.Document.OpenStrictForContentCopy();

        Assert.Equal(1, dropped);
        Assert.NotNull(replacement);
        Assert.NotSame(old, replacement);
    }

    // Closing a document is the end of its parse (a cancelled Transform or Stamp preview closes and replaces it), so
    // the cache must let go of what it read, not keep a closed document's parse until the next read or the tab closes.
    [Fact]
    public void ClosingTheDocumentDropsWhatTheCacheReadFromIt()
    {
        using var scope = new Scope();
        var cache = new PlacementReadCache();
        Assert.NotNull(cache.PlacementsForPage(scope.Document, 0));
        Assert.Equal(1, cache.PagesRemembered);

        scope.Document.Close();

        Assert.Equal(0, cache.PagesRemembered);
    }

    // The closed document's bytes are held by whoever still references the document, so the cache must not keep
    // following it once it has closed.
    [Fact]
    public void ClosingTheDocumentMakesTheCacheLetGoOfIt()
    {
        using var scope = new Scope();
        var cache = new PlacementReadCache();
        Assert.NotNull(cache.PlacementsForPage(scope.Document, 0));
        Assert.True(cache.FollowsADocument);

        scope.Document.Close();

        Assert.True(scope.Document.IsClosed);
        Assert.False(cache.FollowsADocument);
    }

    // An idle drop leaves the document open and usable, so the cache keeps following it.
    [Fact]
    public void TheIdleDropOfAnOpenDocumentKeepsTheCacheFollowingIt()
    {
        using var scope = new Scope();
        var cache = new PlacementReadCache();
        Assert.NotNull(cache.PlacementsForPage(scope.Document, 0));

        scope.Clock.Advance(PdfWorkingDocument.ContentCopyIdleLimit);

        Assert.False(scope.Document.HoldsContentCopy);
        Assert.False(scope.Document.IsClosed);
        Assert.True(cache.FollowsADocument);
    }
    [Fact]
    public void ClosingADocumentThatHeldNoParseRaisesNoDropEvent()
    {
        using var scope = new Scope();
        int dropped = 0;
        scope.Document.ContentCopyDropped += () => dropped++;

        scope.Document.Close();

        Assert.Equal(0, dropped);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<PdfDocument> ReadFirstPage(PlacementReadCache cache, PdfWorkingDocument document)
    {
        var placements = cache.PlacementsForPage(document, 0);
        Assert.NotNull(placements);
        Assert.NotNull(cache.AssetForPlacement(document, 0, 0, Assert.Single(placements)));
        return new WeakReference<PdfDocument>(document.OpenStrictForContentCopy()!);
    }

    private static void Collect()
    {
        for (int i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }

    private sealed class Scope : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "kp-placement-cache-" + Guid.NewGuid().ToString("N"));
        internal ManualClock Clock { get; } = new();
        internal PdfWorkingDocument Document { get; }

        internal Scope()
        {
            Directory.CreateDirectory(_folder);
            string path = Path.Combine(_folder, "pictures.pdf");
            File.WriteAllBytes(path, TwoPagePdf());
            Document = PdfWorkingDocument.Open(path, null);
            Document.ContentCopyClock = Clock;
        }

        public void Dispose()
        {
            Document.Dispose();
            Directory.Delete(_folder, true);
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _now;
        private readonly List<ManualTimer> _timers = [];

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            timer.Change(dueTime, period);
            _timers.Add(timer);
            return timer;
        }

        // Moves time without running any timer, as when the timer thread has not been scheduled yet.
        public void Skip(TimeSpan by) => _now += by.Ticks;

        public void Advance(TimeSpan by)
        {
            _now += by.Ticks;
            foreach (var timer in _timers.ToArray()) timer.RunIfDue(_now);
        }

        private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
        {
            private long? _due;

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                _due = dueTime == Timeout.InfiniteTimeSpan ? null : clock._now + dueTime.Ticks;
                return true;
            }

            public void RunIfDue(long now)
            {
                if (_due is not { } due || due > now) return;
                _due = null;
                callback(state);
            }

            public void Dispose() { _due = null; clock._timers.Remove(this); }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    // Two pages that each draw the same 2 by 2 picture.
    private static byte[] TwoPagePdf()
    {
        var objects = new List<byte[]>
        {
            Ascii("<< /Type /Catalog /Pages 2 0 R >>"),
            Ascii("<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>"),
            Ascii("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /XObject << /Im1 7 0 R >> >> /Contents 5 0 R >>"),
            Ascii("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /XObject << /Im1 7 0 R >> >> /Contents 6 0 R >>"),
            Stream("", Ascii("q 50 0 0 50 120 120 cm /Im1 Do Q")),
            Stream("", Ascii("q 50 0 0 50 20 20 cm /Im1 Do Q")),
            Stream("/Type /XObject /Subtype /Image /Width 2 /Height 2 /ColorSpace /DeviceRGB /BitsPerComponent 8",
                [255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 0])
        };
        using var output = new MemoryStream();
        output.Write(Ascii("%PDF-1.4\n"));
        var offsets = new List<long>();
        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(output.Position);
            output.Write(Ascii((i + 1) + " 0 obj\n"));
            output.Write(objects[i]);
            output.Write(Ascii("\nendobj\n"));
        }
        long xref = output.Position;
        var table = new StringBuilder("xref\n0 " + (objects.Count + 1) + "\n0000000000 65535 f \n");
        foreach (long offset in offsets) table.Append(offset.ToString("D10") + " 00000 n \n");
        table.Append("trailer\n<< /Size " + (objects.Count + 1) + " /Root 1 0 R >>\nstartxref\n" + xref + "\n%%EOF\n");
        output.Write(Ascii(table.ToString()));
        return output.ToArray();
    }

    private static byte[] Ascii(string text) => Encoding.Latin1.GetBytes(text);

    private static byte[] Stream(string dictionary, byte[] data)
    {
        using var buffer = new MemoryStream();
        buffer.Write(Ascii("<< " + dictionary + " /Length " + data.Length + " >>\nstream\n"));
        buffer.Write(data);
        buffer.Write(Ascii("\nendstream"));
        return buffer.ToArray();
    }
}
