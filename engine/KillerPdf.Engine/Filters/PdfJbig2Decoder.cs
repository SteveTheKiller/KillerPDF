using System.Runtime.CompilerServices;
using KillerPdf.Engine.Filters.Jbig2;
using KillerPdf.Engine.Objects;

namespace KillerPdf.Engine.Filters;

internal static class PdfJbig2Decoder
{
    private static readonly ConditionalWeakTable<PdfStream, GlobalState> GlobalCache = new();

    public static byte[] Decode(
        ReadOnlySpan<byte> encoded,
        PdfStream? globals,
        Func<PdfStream, byte[]> decodeGlobals,
        int maximumDecodedBytes,
        int? expectedWidth,
        int? expectedHeight)
    {
        ArgumentNullException.ThrowIfNull(decodeGlobals);

        try
        {
            if (globals is null)
                return DecodePage(encoded, null, maximumDecodedBytes, expectedWidth, expectedHeight);

            GlobalState state = GlobalCache.GetValue(globals,
                stream => new GlobalState(decodeGlobals(stream)));
            lock (state.SyncRoot)
            {
                return DecodePage(encoded, state.Document.GlobalSegments,
                    maximumDecodedBytes, expectedWidth, expectedHeight);
            }
        }
        catch (PdfFilterException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new PdfFilterException(
                $"The JBIG2Decode stream contains invalid image data. {ex.Message}", ex);
        }
    }

    private static byte[] DecodePage(
        ReadOnlySpan<byte> encoded,
        Jbig2Globals? globals,
        int maximumDecodedBytes,
        int? expectedWidth,
        int? expectedHeight)
    {
        using var document = new Jbig2Document(new ImageInputStream(encoded), globals);
        Jbig2Page page = document.GetPage(1)
            ?? throw new PdfFilterException("The JBIG2Decode stream has no image page.");
        SegmentHeader pageInformationSegment = page.GetPageInformationSegment()
            ?? throw new PdfFilterException("The JBIG2Decode stream has no page information segment.");
        var pageInformation = (PageInformation)pageInformationSegment.GetSegmentData();

        int checkedHeight = pageInformation.BitmapHeight >= 0
            ? pageInformation.BitmapHeight
            : expectedHeight ?? throw new PdfFilterException(
                "A striped JBIG2 image requires a PDF Height entry.");
        EnsureDimensions(pageInformation.BitmapWidth, checkedHeight,
            maximumDecodedBytes, expectedWidth, expectedHeight);

        Jbig2Bitmap bitmap = page.GetBitmap();
        EnsureDimensions(bitmap.Width, bitmap.Height,
            maximumDecodedBytes, expectedWidth, expectedHeight);

        byte[] output = bitmap.ByteArray;
        for (int index = 0; index < output.Length; index++)
            output[index] = (byte)~output[index];
        return output;
    }

    private static void EnsureDimensions(
        int width,
        int height,
        int maximumDecodedBytes,
        int? expectedWidth,
        int? expectedHeight)
    {
        if (width <= 0 || height <= 0)
            throw new PdfFilterException("The JBIG2Decode stream has invalid image dimensions.");
        if (expectedWidth is not null && width != expectedWidth
            || expectedHeight is not null && height != expectedHeight)
            throw new PdfFilterException(
                "The JBIG2 image dimensions do not match its PDF image dictionary.");

        long byteLength = ((long)width + 7) / 8 * height;
        if (byteLength > maximumDecodedBytes)
            throw new PdfFilterException("Decoded stream exceeds the configured safety limit.");
    }

    private sealed class GlobalState
    {
        public GlobalState(byte[] encoded)
        {
            Document = new Jbig2Document(new ImageInputStream(encoded));
        }

        public object SyncRoot { get; } = new();
        public Jbig2Document Document { get; }
    }
}
