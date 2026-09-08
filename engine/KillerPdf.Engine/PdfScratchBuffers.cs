using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace KillerPdf.Engine;

internal static class PdfScratchBuffers
{
    internal static readonly ArrayPool<byte> Bytes = new PdfScratchBufferPool<byte>(32 * 1024 * 1024);
    internal static readonly ArrayPool<int> Integers = new PdfScratchBufferPool<int>(16 * 1024 * 1024);
    internal static readonly ArrayPool<float> Floats = new PdfScratchBufferPool<float>(16 * 1024 * 1024);
}

// A byte budget allows several simultaneously used buffers of the same size to be
// reused without retaining a fixed number in every possible size bucket.
internal sealed class PdfScratchBufferPool<T>(int maximumBytes) : ArrayPool<T> where T : unmanaged
{
    private readonly LinkedList<T[]> _idle = [];
    private readonly object _sync = new();
    private readonly int _maximumBytes = maximumBytes > 0 ? maximumBytes
        : throw new ArgumentOutOfRangeException(nameof(maximumBytes));
    private int _retainedBytes;

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
            for (var node = _idle.Last; node is not null; node = node.Previous)
            {
                if (node.Value.Length != length) continue;
                T[] buffer = node.Value;
                _idle.Remove(node);
                _retainedBytes -= length * Unsafe.SizeOf<T>();
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
        if (clearArray) Array.Clear(array);
        lock (_sync)
        {
            while (_retainedBytes + bytes > _maximumBytes || _idle.Count >= 64)
            {
                T[] oldest = _idle.First!.Value;
                _idle.RemoveFirst();
                _retainedBytes -= oldest.Length * Unsafe.SizeOf<T>();
            }
            _idle.AddLast(array);
            _retainedBytes += (int)bytes;
        }
    }
}
