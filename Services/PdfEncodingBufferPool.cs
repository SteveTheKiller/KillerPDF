namespace KillerPDF.Services;

/// <summary>Shares one spare output buffer between sequential encoding sessions.</summary>
internal sealed class PdfEncodingBufferPool
{
    private const int MaximumRetainedBytes = 16 * 1024 * 1024;
    private readonly object _sync = new();
    private byte[]? _spare;

    internal byte[] Rent(int minimumLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(minimumLength);
        if (minimumLength == 0) return [];
        lock (_sync)
        {
            byte[]? spare = _spare;
            _spare = null;
            if (spare is not null && spare.Length >= minimumLength) return spare;
        }
        return new byte[minimumLength];
    }

    internal void Return(byte[] buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (buffer.Length == 0 || buffer.Length > MaximumRetainedBytes) return;
        lock (_sync)
        {
            if (_spare is null || _spare.Length < buffer.Length) _spare = buffer;
        }
    }
}
