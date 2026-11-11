using System.IO;
using System.Text;
using KillerPdf.Engine.Documents;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class SourceImageContentCopyTests
{
    [Fact]
    public void RepeatedPictureProbesReuseOneStrictParseWhileItIsInUse()
    {
        string folder = Path.Combine(Path.GetTempPath(), "kp-source-copy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string path = Path.Combine(folder, "picture.pdf");
            File.WriteAllBytes(path, Pdf("q 50 0 0 50 120 120 cm /Im1 Do Q"));
            using PdfWorkingDocument document = PdfWorkingDocument.Open(path, null);
            var first = document.OpenStrictForContentCopy();
            Assert.NotNull(first);
            Assert.Same(first, document.OpenStrictForContentCopy());
        }
        finally { Directory.Delete(folder, true); }
    }

    // A copy not asked for in 2 minutes is dropped (it can be a large parse), and the next click builds a new one
    // from the same unchanged source. Time here is a manual clock; nothing sleeps.
    [Fact]
    public void TheStrictParseIsReusedInsideTheIdleWindowAndRebuiltAfterItIsDropped()
    {
        string folder = Path.Combine(Path.GetTempPath(), "kp-source-copy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string path = Path.Combine(folder, "picture.pdf");
            File.WriteAllBytes(path, Pdf("q 50 0 0 50 120 120 cm /Im1 Do Q"));
            var clock = new ManualClock();
            using PdfWorkingDocument document = PdfWorkingDocument.Open(path, null);
            document.ContentCopyClock = clock;
            var first = document.OpenStrictForContentCopy();
            Assert.NotNull(first);

            // Each use restarts the 2 minutes: 119 s, a use, then 119 s more is still inside the window.
            clock.Advance(TimeSpan.FromSeconds(119));
            Assert.Same(first, document.OpenStrictForContentCopy());
            clock.Advance(TimeSpan.FromSeconds(119));
            Assert.True(document.HoldsContentCopy);
            Assert.Same(first, document.OpenStrictForContentCopy());

            // Unused for the full 2 minutes: dropped without anyone asking for it.
            clock.Advance(PdfWorkingDocument.ContentCopyIdleLimit);
            Assert.False(document.HoldsContentCopy);

            var second = document.OpenStrictForContentCopy();
            Assert.NotNull(second);
            Assert.NotSame(first, second);
            Assert.Equal(PdfPageInformation.Read(first).Count, PdfPageInformation.Read(second).Count);
            Assert.Same(second, document.OpenStrictForContentCopy());
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void ClosingTheDocumentDropsTheStrictParse()
    {
        string folder = Path.Combine(Path.GetTempPath(), "kp-source-copy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string path = Path.Combine(folder, "picture.pdf");
            File.WriteAllBytes(path, Pdf("q 50 0 0 50 120 120 cm /Im1 Do Q"));
            using PdfWorkingDocument document = PdfWorkingDocument.Open(path, null);
            Assert.NotNull(document.OpenStrictForContentCopy());
            Assert.True(document.HoldsContentCopy);

            document.Close();

            Assert.False(document.HoldsContentCopy);
            Assert.Throws<ObjectDisposedException>(() => document.OpenStrictForContentCopy());
        }
        finally { Directory.Delete(folder, true); }
    }

    // A clock that moves only when told to, and runs the timers that fall due as it moves.
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
    private static byte[] Pdf(string content)
    {
        var objects = new List<byte[]>
        {
            Ascii("<< /Type /Catalog /Pages 2 0 R >>"),
            Ascii("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Ascii("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /XObject << /Im1 5 0 R >> >> /Contents 4 0 R >>"),
            Stream("", Ascii(content)),
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
