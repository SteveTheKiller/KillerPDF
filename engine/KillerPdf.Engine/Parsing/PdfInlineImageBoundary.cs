using System.IO.Compression;
using KillerPdf.Engine.Syntax;

namespace KillerPdf.Engine.Parsing;

internal static class PdfInlineImageBoundary
{
    internal static int Find(ReadOnlyMemory<byte> source, string filter, CancellationToken cancellationToken, int earlyChange)
    {
        var bytes = source.Span;
        if (filter == "FlateDecode") return FlateLength(source, cancellationToken);
        if (filter == "DCTDecode") return JpegLength(bytes, cancellationToken);
        if (filter == "LZWDecode") return LzwLength(bytes, earlyChange, cancellationToken);
        for (int position = 0; position < bytes.Length; position++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte value = bytes[position];
            switch (filter)
            {
                case "ASCIIHexDecode":
                    if (value == '>') return position + 1;
                    if (!PdfInlineImageReader.Whitespace(value) && !char.IsAsciiHexDigit((char)value))
                        throw new PdfSyntaxException("Invalid inline ASCIIHex data", position);
                    break;
                case "ASCII85Decode":
                    if (value == '~' && position + 1 < bytes.Length && bytes[position + 1] == '>')
                        return position + 2;
                    if (!PdfInlineImageReader.Whitespace(value) && value != 'z' && value is not (>= 33 and <= 117))
                        throw new PdfSyntaxException("Invalid inline ASCII85 data", position);
                    break;
                case "RunLengthDecode":
                    if (value == 128) return position + 1;
                    int runBytes = value < 128 ? value + 1 : 1;
                    if (runBytes > bytes.Length - position - 1)
                        throw new PdfSyntaxException("Truncated inline RunLength data", position);
                    position += runBytes;
                    break;
                default:
                    throw new NotSupportedException($"Inline image filter {filter} has no supported unambiguous boundary reader.");
            }
        }
        throw new PdfSyntaxException("Inline image encoded data has no end marker", bytes.Length);
    }

    private static int LzwLength(ReadOnlySpan<byte> bytes, int earlyChange, CancellationToken cancellationToken)
    {
        int bit = 0, width = 9, next = 258, previousLength = 0;
        long decoded = 0;
        Span<int> lengths = stackalloc int[4096];
        lengths[..256].Fill(1);
        while (bit + width <= bytes.Length * 8)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int code = 0;
            for (int i = 0; i < width; i++, bit++) code = (code << 1) | ((bytes[bit / 8] >> (7 - bit % 8)) & 1);
            if (code == 257) return (bit + 7) / 8;
            if (code == 256)
            {
                width = 9; next = 258; previousLength = 0;
                continue;
            }
            if (code > next || (code == next && previousLength == 0) || (code >= 258 && previousLength == 0))
                throw new PdfSyntaxException("Invalid inline LZW code", bit / 8);
            int length = code == next ? previousLength + 1 : lengths[code];
            decoded += length;
            if (decoded > PdfContentStreamReader.MaximumSourceBytes)
                throw new PdfSyntaxException("Inline LZW image exceeds the decoded size limit", bit / 8);
            if (previousLength > 0 && next < 4096)
            {
                lengths[next++] = previousLength + 1;
                if (width < 12 && next + earlyChange == 1 << width) width++;
            }
            previousLength = length;
        }
        throw new PdfSyntaxException("Inline LZW image has no EOD code", bit / 8);
    }

    private static int FlateLength(ReadOnlyMemory<byte> source, CancellationToken cancellationToken)
    {
        // One-byte reads prevent ZLibStream buffering the following EI and content operators.
        using var input = new ExactInput(source, cancellationToken);
        using var inflater = new ZLibStream(input, CompressionMode.Decompress);
        Span<byte> output = stackalloc byte[8192];
        long decoded = 0;
        try
        {
            int count;
            while ((count = inflater.Read(output)) != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                decoded += count;
                if (decoded > PdfContentStreamReader.MaximumSourceBytes)
                    throw new PdfSyntaxException("Inline Flate image exceeds the decoded size limit", (int)input.Position);
            }
        }
        catch (InvalidDataException)
        {
            throw new PdfSyntaxException("Invalid or truncated inline Flate data", (int)input.Position);
        }
        return (int)input.Position;
    }

    private static int JpegLength(ReadOnlySpan<byte> bytes, CancellationToken cancellationToken)
    {
        if (bytes.Length < 2 || bytes[0] != 255 || bytes[1] != 216)
            throw new PdfSyntaxException("Inline DCT data must start with JPEG SOI", 0);
        int position = 2;
        bool entropy = false;
        while (position < bytes.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (bytes[position++] != 255)
            {
                if (entropy) continue;
                throw new PdfSyntaxException("Invalid JPEG marker", position - 1);
            }
            while (position < bytes.Length && bytes[position] == 255) position++;
            if (position >= bytes.Length) break;
            byte marker = bytes[position++];
            if (entropy && (marker == 0 || marker is >= 208 and <= 215)) continue;
            if (marker == 217) return position;
            if (marker == 1) continue;
            if (marker is 0 or 216 or >= 208 and <= 215)
                throw new PdfSyntaxException("Unexpected JPEG marker", position - 1);
            if (position + 2 > bytes.Length) break;
            int length = (bytes[position] << 8) | bytes[position + 1];
            if (length < 2 || length > bytes.Length - position)
                throw new PdfSyntaxException("Truncated JPEG marker segment", position);
            position += length;
            // DNL may interrupt an entropy-coded scan; SOS begins a new scan.
            entropy = marker == 218 || (entropy && marker == 220);
        }
        throw new PdfSyntaxException("Inline JPEG has no EOI marker", position);
    }

    private sealed class ExactInput(ReadOnlyMemory<byte> source, CancellationToken cancellationToken) : Stream
    {
        private int _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => source.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (buffer.IsEmpty) return 0;
            if (_position == source.Length) throw new InvalidDataException("Truncated zlib stream.");
            buffer[0] = source.Span[_position++];
            return 1;
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
