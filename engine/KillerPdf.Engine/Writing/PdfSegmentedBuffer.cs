namespace KillerPdf.Engine.Writing;

/// <summary>
/// A write-only stream that keeps its bytes in fixed-size segments, so a large document grows without the doubling
/// copies of a <see cref="MemoryStream"/>, and then hands them over as one exactly sized array.
/// </summary>
internal sealed class PdfSegmentedBuffer : Stream
{
    private const int SegmentSize = 1 << 20;
    private readonly List<byte[]> _segments = [];
    private int _usedInLast = SegmentSize;
    private long _length;

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => _length;
    public override long Position
    {
        get => _length;
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        while (!buffer.IsEmpty)
        {
            if (_usedInLast == SegmentSize)
            {
                _segments.Add(new byte[SegmentSize]);
                _usedInLast = 0;
            }
            int take = Math.Min(buffer.Length, SegmentSize - _usedInLast);
            buffer[..take].CopyTo(_segments[^1].AsSpan(_usedInLast));
            _usedInLast += take;
            _length += take;
            buffer = buffer[take..];
        }
    }

    public override void WriteByte(byte value) => Write([value]);

    /// <summary>Copies everything written into one new array and releases the segments.</summary>
    internal byte[] ToArrayAndRelease()
    {
        byte[] result = new byte[checked((int)_length)];
        long offset = 0;
        for (int index = 0; index < _segments.Count; index++)
        {
            int count = (int)Math.Min(SegmentSize, _length - offset);
            _segments[index].AsSpan(0, count).CopyTo(result.AsSpan((int)offset));
            offset += count;
            _segments[index] = [];
        }
        _segments.Clear();
        return result;
    }

    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
