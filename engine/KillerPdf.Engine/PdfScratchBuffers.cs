using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace KillerPdf.Engine;

internal static class PdfScratchBuffers
{
    // Raster callers size their work from the requested length, so an idle byte buffer up
    // to four times the request serves it; the sample pools account by array length and
    // keep exact buckets.
    internal static readonly ArrayPool<byte> Bytes = new PdfScratchBufferPool<byte>(32 * 1024 * 1024, largerBucketSearch: 2);
    internal static readonly ArrayPool<int> Integers = new PdfScratchBufferPool<int>(16 * 1024 * 1024);
    internal static readonly ArrayPool<float> Floats = new PdfScratchBufferPool<float>(16 * 1024 * 1024);
}

// A byte budget allows several simultaneously used buffers of the same size to be
// reused without retaining a fixed number in every possible size bucket.
internal sealed class PdfScratchBufferPool<T>(int maximumBytes, int largerBucketSearch = 0) : ArrayPool<T> where T : unmanaged
{
    private const int MaximumIdleCount = 64;
    private readonly int _largerBucketSearch = largerBucketSearch >= 0 ? largerBucketSearch
        : throw new ArgumentOutOfRangeException(nameof(largerBucketSearch));
    // Idle buffers are bucketed by their power-of-two length so renting is a constant-time
    // pop instead of a scan. Over budget, the largest idle buffers are released first.
    private readonly Stack<T[]>[] _idle = new Stack<T[]>[32];
    private readonly Lock _sync = new();
    private readonly int _maximumBytes = maximumBytes > 0 ? maximumBytes
        : throw new ArgumentOutOfRangeException(nameof(maximumBytes));
    private int _retainedBytes;
    private int _idleCount;

    internal int RetainedBytes
    {
        get { lock (_sync) return _retainedBytes; }
    }

    public override T[] Rent(int minimumLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(minimumLength);
        if (minimumLength == 0) return [];
        long rounded = BitOperations.RoundUpToPowerOf2((uint)Math.Max(16, minimumLength));
        if (rounded * Unsafe.SizeOf<T>() > _maximumBytes || rounded > Array.MaxLength)
            return new T[minimumLength];
        int length = (int)rounded;
        lock (_sync)
        {
            // Where allowed, an idle buffer a few buckets larger serves the request rather
            // than allocating a fresh one; page-sized surfaces vary in size from paint to
            // paint, and a new large-object allocation per size costs more than the slack.
            int index = BitOperations.Log2((uint)length);
            int last = Math.Min(index + _largerBucketSearch, _idle.Length - 1);
            for (int candidate = index; candidate <= last; candidate++)
            {
                Stack<T[]>? bucket = _idle[candidate];
                if (bucket is not { Count: > 0 }) continue;
                T[] buffer = bucket.Pop();
                _idleCount--;
                _retainedBytes -= buffer.Length * Unsafe.SizeOf<T>();
                return buffer;
            }
        }
        return new T[length];
    }

    public override void Return(T[] array, bool clearArray = false)
    {
        ArgumentNullException.ThrowIfNull(array);
        long bytes = (long)array.Length * Unsafe.SizeOf<T>();
        if (bytes == 0 || bytes > _maximumBytes) return;
        if (!BitOperations.IsPow2(array.Length) || array.Length < 16) return;
        if (clearArray) Array.Clear(array);
        lock (_sync)
        {
            int index = BitOperations.Log2((uint)array.Length);
            int release = _idle.Length - 1;
            while (_retainedBytes + bytes > _maximumBytes || _idleCount >= MaximumIdleCount)
            {
                while (_idle[release] is not { Count: > 0 }) release--;
                T[] largest = _idle[release].Pop();
                _idleCount--;
                _retainedBytes -= largest.Length * Unsafe.SizeOf<T>();
            }
            (_idle[index] ??= new Stack<T[]>()).Push(array);
            _idleCount++;
            _retainedBytes += (int)bytes;
        }
    }
}
