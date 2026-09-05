using KillerPdf.Engine.Syntax;

namespace KillerPdf.Engine.CrossReference;

/// <summary>The final startxref declaration and the byte offset it points to.</summary>
public readonly record struct PdfStartXref(long Offset, int MarkerOffset)
{
    private static ReadOnlySpan<byte> Marker => "startxref"u8;
    private static ReadOnlySpan<byte> EndMarker => "%%EOF"u8;

    /// <summary>Finds and validates the final startxref declaration and end-of-file marker.</summary>
    public static PdfStartXref Find(
        ReadOnlySpan<byte> source, bool allowPastEndOffset = false)
    {
        int markerOffset = FindFinalMarkerOutsideComment(source);
        if (markerOffset < 0)
            throw new PdfSyntaxException("The PDF does not contain a final startxref declaration", source.Length);
        if (markerOffset > 0 && !IsWhitespace(source[markerOffset - 1]))
            throw new PdfSyntaxException(
                "The final startxref marker is not token-delimited", markerOffset);

        int position = markerOffset + Marker.Length;
        if (position >= source.Length || !IsWhitespace(source[position]))
            throw new PdfSyntaxException(
                "The startxref marker is not followed by whitespace", position);
        SkipTrivia(source, ref position, stopAtEndMarker: false);
        int numberOffset = position;
        long offset = 0;
        while (position < source.Length && source[position] is >= (byte)'0' and <= (byte)'9')
        {
            try
            {
                offset = checked(offset * 10 + source[position] - (byte)'0');
            }
            catch (OverflowException ex)
            {
                throw new PdfSyntaxException($"The startxref offset is too large: {ex.Message}", numberOffset);
            }
            position++;
        }

        if (position == numberOffset)
            throw new PdfSyntaxException("The startxref declaration does not contain a byte offset", numberOffset);
        if (offset >= source.Length && !allowPastEndOffset)
            throw new PdfSyntaxException("The startxref offset points beyond the end of the file", numberOffset);
        if (offset >= markerOffset && !allowPastEndOffset)
            throw new PdfSyntaxException(
                "The startxref offset must point before its declaration", numberOffset);

        if (position >= source.Length || !IsWhitespace(source[position]))
            throw new PdfSyntaxException(
                "The startxref offset is not followed by whitespace", position);
        SkipTrivia(source, ref position, stopAtEndMarker: true);
        if (!source[position..].StartsWith(EndMarker))
            throw new PdfSyntaxException("The startxref declaration is not followed by %%EOF", position);
        position += EndMarker.Length;
        SkipWhitespace(source, ref position);

        return new PdfStartXref(offset, markerOffset);
    }

    private static int FindFinalMarkerOutsideComment(ReadOnlySpan<byte> source)
    {
        int searchEnd = source.Length;
        while (searchEnd > 0)
        {
            int candidate = source[..searchEnd].LastIndexOf(Marker);
            if (candidate < 0)
                return -1;
            int commentStart = CommentStart(source, candidate);
            if (commentStart < 0)
                return candidate;
            searchEnd = commentStart;
        }
        return -1;
    }

    private static int CommentStart(ReadOnlySpan<byte> source, int offset)
    {
        int lineStart = offset;
        while (lineStart > 0
            && source[lineStart - 1] is not ((byte)'\r') and not ((byte)'\n'))
            lineStart--;
        int relative = source[lineStart..offset].IndexOf((byte)'%');
        return relative >= 0 ? lineStart + relative : -1;
    }

    private static void SkipWhitespace(ReadOnlySpan<byte> source, ref int position)
    {
        while (position < source.Length && IsWhitespace(source[position]))
            position++;
    }

    private static void SkipTrivia(
        ReadOnlySpan<byte> source, ref int position, bool stopAtEndMarker)
    {
        while (true)
        {
            SkipWhitespace(source, ref position);
            if (position >= source.Length
                || (stopAtEndMarker && source[position..].StartsWith(EndMarker))
                || source[position] != (byte)'%')
                return;
            while (position < source.Length
                && source[position] is not ((byte)'\r') and not ((byte)'\n'))
                position++;
        }
    }

    private static bool IsWhitespace(byte value) =>
        value is 0x00 or 0x09 or 0x0A or 0x0C or 0x0D or 0x20;
}
