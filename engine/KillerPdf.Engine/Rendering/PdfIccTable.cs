namespace KillerPdf.Engine.Rendering;

internal abstract class PdfIccTable
{
    internal abstract int InputChannels { get; }
    internal abstract int OutputChannels { get; }
    internal virtual bool UsesLegacyLabEncoding => false;
    internal virtual double XyzEncodingScale => 65535d / 32768;
    internal abstract void Transform(ReadOnlySpan<double> input, Span<double> output, bool applyXyzMatrix = false);

    internal static PdfIccTable Read(ReadOnlyMemory<byte> data) => data.Length >= 4
        && (data.Span[..4].SequenceEqual("mAB "u8) || data.Span[..4].SequenceEqual("mBA "u8))
        ? new PdfIccMultiProcessLut(data) : new PdfIccLut(data);
}
