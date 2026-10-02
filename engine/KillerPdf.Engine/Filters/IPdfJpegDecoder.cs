namespace KillerPdf.Engine.Filters;

/// <summary>Optional JPEG decoder for compatible page rendering.</summary>
public interface IPdfJpegDecoder
{
    /// <summary>
    /// Decodes only JPEG streams whose dimensions and color transform the implementation supports.
    /// Returns false to use the engine decoder.
    /// </summary>
    bool TryDecode(ReadOnlyMemory<byte> encoded, int maximumDecodedBytes, int reduction,
        int? colorTransform, out JpegDecodedImage image);
}
