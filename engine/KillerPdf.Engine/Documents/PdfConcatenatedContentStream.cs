namespace KillerPdf.Engine.Documents;

// Page content arrays are one logical program, with whitespace between streams.
internal sealed class PdfConcatenatedContentStream(IEnumerable<Stream> streams) : Stream
{
    private readonly IEnumerator<Stream> _streams = streams.GetEnumerator();
    private Stream? _current;
    private bool _finished;

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (count == 0 || _finished) return 0;
        while (true)
        {
            if (_current is null)
            {
                if (!_streams.MoveNext())
                {
                    _finished = true;
                    return 0;
                }
                _current = _streams.Current;
            }
            int read = _current.Read(buffer, offset, count);
            if (read > 0) return read;
            _current.Dispose();
            _current = null;
            buffer[offset] = (byte)'\n';
            return 1;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _current?.Dispose();
            _streams.Dispose();
            _finished = true;
        }
        base.Dispose(disposing);
    }

    public override bool CanRead => !_finished;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
