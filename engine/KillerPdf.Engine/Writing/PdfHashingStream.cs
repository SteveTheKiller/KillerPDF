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

    public override void Write(byte[] buffer, int offset, int count) => hash.AppendData(buffer, offset, count);
    public override void Write(ReadOnlySpan<byte> buffer) => hash.AppendData(buffer);
    public override void WriteByte(byte value) => hash.AppendData([value]);
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
