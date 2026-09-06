using KillerPdf.Engine.Documents;

namespace KillerPDF.Services;

internal static class FormAwareOcr
{
    internal static PdfOcrResult Recognize(
        OcrService service, ReadOnlyMemory<byte> bgra, int width, int height,
        IReadOnlyList<PdfFormWidgetInfo> widgets, int additionalRotation = 0,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<PdfOcrFormRegion> regions = PdfOcrFormLayout.MapRegions(
            widgets, width, height, additionalRotation);
        return PdfOcrFormRecognizer.Recognize(service.RecognizeBgra,
            bgra, width, height, regions, cancellationToken);
    }
}
