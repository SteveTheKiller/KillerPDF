using System.Security.Cryptography;

namespace KillerPdf.Engine.Writing;

/// <summary>A write-only stream that feeds every byte to a hash and keeps none of them.</summary>
internal sealed class PdfHashingStream(IncrementalHash hash) : Stream
{
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    // Serialized objects write mostly single bytes and short tokens. Each hash call crosses into native code,
    // so short writes gather here and reach the hash in large spans. Flush before the hash is read.
    private readonly byte[] _buffer = new byte[8192];
    private int _buffered;

    /// <summary>Counts the calls made into the hash, so tests can bound the native work.</summary>
    internal int AppendCalls { get; private set; }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length >= _buffer.Length)
        {
            Flush();
            AppendCalls++;
            hash.AppendData(buffer);
            return;
        }
        if (buffer.Length > _buffer.Length - _buffered) Flush();
        buffer.CopyTo(_buffer.AsSpan(_buffered));
        _buffered += buffer.Length;
    }

    public override void WriteByte(byte value)
    {
        if (_buffered == _buffer.Length) Flush();
        _buffer[_buffered++] = value;
    }

    /// <summary>Hands the gathered bytes to the hash. The digest is complete only after this.</summary>
    public override void Flush()
    {
        if (_buffered == 0) return;
        AppendCalls++;
        hash.AppendData(_buffer, 0, _buffered);
        _buffered = 0;
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
