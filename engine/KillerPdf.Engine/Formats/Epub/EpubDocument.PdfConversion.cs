using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Rendering;

namespace KillerPdf.Engine.Formats.Epub;

public sealed partial class EpubDocument
{
    /// <summary>Explicitly converts supported native EPUB content into an independent PDF.</summary>
    public byte[] ConvertToPdf(EpubPdfConversionOptions options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.DefaultFont);
        const double pointsPerCssPixel = 72d / 96d;
        var layout = CreateLayout(new EpubLayoutOptions(
            options.PageWidthPoints / pointsPerCssPixel,
            options.PageHeightPoints / pointsPerCssPixel, options.DefaultFont)
        {
            Margin = options.MarginPoints / pointsPerCssPixel,
            FontSize = options.FontSizePoints / pointsPerCssPixel
        }, cancellationToken);
        var builder = new PdfDocumentBuilder();
        foreach (var page in layout.Pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (page.CanvasBackground is not null
                || page.Glyphs.Any(paint => paint is not NativeGlyphPaint
                    && paint is not NativeImagePaint { Image: { Alpha: null, Components: 3, Bits: 8 } }))
                throw new NotSupportedException(
                    "EPUB PDF conversion currently supports text and opaque RGB PNG images without backgrounds.");
            var content = new PdfContentStreamBuilder();
            foreach (var paint in page.Glyphs.OfType<NativeImagePaint>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var image = paint.Image;
                content.DrawImage(PdfImage.FromRgb(image.Width, image.Height, image.Samples),
                    paint.Left * pointsPerCssPixel,
                    options.PageHeightPoints - (paint.Top + image.Height) * pointsPerCssPixel,
                    image.Width * pointsPerCssPixel, image.Height * pointsPerCssPixel);
            }
            foreach (var placement in page.TextPlacements)
            {
                cancellationToken.ThrowIfCancellationRequested();
                double size = placement.Height * pointsPerCssPixel;
                double baseline = (placement.Top + options.DefaultFont.Ascender
                    * placement.Height / options.DefaultFont.UnitsPerEm) * pointsPerCssPixel;
                var color = placement.Color ?? new PdfRgbColor(0, 0, 0);
                content.SetFillRgb(color.Red, color.Green, color.Blue)
                    .BeginText()
                    .SetFont(options.DefaultFont, size)
                    .SetTextMatrix(1, 0, 0, 1, placement.Left * pointsPerCssPixel,
                        options.PageHeightPoints - baseline)
                    .ShowUnicodeText(page.Text.Substring(placement.Start - page.TextStart, placement.Length))
                    .EndText();
            }
            builder.AddPage(options.PageWidthPoints, options.PageHeightPoints, content);
        }
        cancellationToken.ThrowIfCancellationRequested();
        byte[] result = builder.Build();
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }
}
