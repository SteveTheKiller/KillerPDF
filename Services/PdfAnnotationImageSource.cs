using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;

namespace KillerPDF.Services;

internal static class PdfAnnotationImageSource
{
    private const int MaximumAssetBytes = 5 * 1024 * 1024;

    internal static BitmapSource RenderPdf(ReadOnlyMemory<byte> bytes, double previewWidth, double previewHeight)
    {
        var asset = ReadPdfAsset(bytes);
        var renderer = new PdfPageRenderer(PdfDocument.Open(asset.ToPdfBytes()));
        double scale = Math.Min(2048d / Math.Max(asset.Width, asset.Height),
            Math.Max(1, Math.Max(previewWidth / asset.Width, previewHeight / asset.Height)));
        int width = Math.Max(1, (int)Math.Round(asset.Width * scale));
        int height = Math.Max(1, (int)Math.Round(asset.Height * scale));
        var options = new PdfRenderOptions(width, height, transparentBackground: true);
        byte[] pixels;
        try { pixels = renderer.RenderFrame(0, options).ToSrgbBgra32(); }
        catch (NotSupportedException)
        {
            var rendered = renderer.Render(0, options);
            if (rendered.Diagnostics.Count != 0)
                throw new InvalidDataException("The image asset could not be rendered completely.");
            pixels = rendered.Pixels.ToArray();
        }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, checked(width * 4));
        bitmap.Freeze();
        return bitmap;
    }

    internal static PdfImageAsset ReadPdfAsset(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length > MaximumAssetBytes)
            throw new InvalidDataException("The image asset exceeds its size limit.");
        var document = PdfDocument.Open(bytes);
        if (document.IsEncrypted || PdfPageInformation.Read(document).Count != 1)
            throw new InvalidDataException("An image asset requires one unencrypted page.");
        var placements = new PdfPageContentReader(document).ReadImagePlacements(0);
        if (placements.Count != 1)
            throw new InvalidDataException("An image asset requires one image resource.");
        // Rebuild the bounded closed image graph rather than rendering arbitrary page content.
        return PdfImagePlacementExtractor.Extract(document, placements[0]);
    }
}
