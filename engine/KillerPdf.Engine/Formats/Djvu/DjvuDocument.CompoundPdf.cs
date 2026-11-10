using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Rendering;

namespace KillerPdf.Engine.Formats.Djvu;

public sealed partial class DjvuDocument
{
    // A compound page (7.1.3) converts as its layers, so text stays as sharp as the scan and the file stays small:
    // the background as an image at its own stored resolution, and the foreground as the page's one-bit mask used as a
    // luminosity soft mask, once per foreground color (each FGbz palette color, or black) over a fill of that color,
    // or once over the FG44 image. Each color's mask is cut to the rectangle its pixels cover. Everything is drawn in
    // stored orientation under the page's turn, as the located text is.
    private void AddCompoundPdfPage(PdfDocumentBuilder builder, DjvuPageInfo page, List<Chunk> chunks,
        DjvuTextPage text, int pageIndex, ICollection<DjvuDiagnostic> diagnostics, CancellationToken token)
    {
        var raster = DecodeCompoundRaster(page, WithoutAnnotations(chunks), token);
        var layers = raster.Layers!;
        int width = page.Width, height = page.Height;
        double scale = 72d / page.Dpi;
        double gamma = InfoGamma(chunks);
        bool quarterTurn = page.Orientation is DjvuOrientation.Clockwise90 or DjvuOrientation.CounterClockwise90;
        var encoded = SrgbSamples(raster.Linear);
        var content = new PdfContentStreamBuilder().SaveState();
        switch (page.Orientation)
        {
            case DjvuOrientation.Clockwise90: content.Transform(0, -1, 1, 0, 0, width * scale); break;
            case DjvuOrientation.CounterClockwise90: content.Transform(0, 1, -1, 0, height * scale, 0); break;
            case DjvuOrientation.UpsideDown: content.Transform(-1, 0, 0, -1, width * scale, height * scale); break;
        }
        // A reduced layer's last row and column can reach past the page; the page edge cuts them as the viewer does.
        content.Rectangle(0, 0, width * scale, height * scale).Clip();
        if (layers.Background is { } background) DrawLayer(content, background, gamma, encoded, height, scale);

        int stride = (width + 7) / 8;
        var mask = raster.Samples;
        bool Masked(int x, int y) => (mask[y * stride + (x >> 3)] & (0x80 >> (x & 7))) != 0;
        if (layers.Foreground is { } foreground)
        {
            if (Bounds(rank: -1) is { } box)
            {
                content.SaveState().SetGraphicsState(SoftMask(box, rank: -1));
                DrawLayer(content, foreground, gamma, encoded, height, scale);
                content.RestoreState();
            }
        }
        else
        {
            var boxes = ColorBounds();
            for (int rank = 0; rank < boxes.Length; rank++)
            {
                if (boxes[rank] is not { } box) continue;
                token.ThrowIfCancellationRequested();
                content.SaveState().SetGraphicsState(SoftMask(box, rank))
                    .SetFillRgb(encoded[layers.Palette[3 * rank]] / 255d, encoded[layers.Palette[3 * rank + 1]] / 255d,
                        encoded[layers.Palette[3 * rank + 2]] / 255d)
                    .Rectangle(box.Left * scale, (height - box.Bottom) * scale, (box.Right - box.Left) * scale,
                        (box.Bottom - box.Top) * scale)
                    .Fill().RestoreState();
            }
        }
        content.RestoreState();
        AddLocatedPdfText(content, text, page, pageIndex, diagnostics, token);
        builder.AddPage((quarterTurn ? height : width) * scale, (quarterTurn ? width : height) * scale, content);

        // The rectangle (right and bottom exclusive) a color's masked pixels cover, or the whole mask's for rank -1.
        (int Left, int Top, int Right, int Bottom)? Bounds(int rank)
        {
            int left = width, top = height, right = 0, bottom = 0;
            for (int y = 0; y < height; y++)
            {
                token.ThrowIfCancellationRequested();
                for (int x = 0; x < width; x++)
                {
                    if (!Masked(x, y) || (rank >= 0 && layers.Index(x, y) != rank)) continue;
                    left = Math.Min(left, x); right = Math.Max(right, x + 1);
                    top = Math.Min(top, y); bottom = Math.Max(bottom, y + 1);
                }
            }
            return right == 0 ? null : (left, top, right, bottom);
        }

        // One pass over the page for every color's rectangle.
        (int Left, int Top, int Right, int Bottom)?[] ColorBounds()
        {
            int ranks = layers.Palette.Length / 3;
            var result = new (int Left, int Top, int Right, int Bottom)?[ranks];
            for (int y = 0; y < height; y++)
            {
                token.ThrowIfCancellationRequested();
                for (int x = 0; x < width; x++)
                {
                    if (!Masked(x, y)) continue;
                    int rank = layers.Index(x, y);
                    result[rank] = result[rank] is { } b
                        ? (Math.Min(b.Left, x), Math.Min(b.Top, y), Math.Max(b.Right, x + 1), Math.Max(b.Bottom, y + 1))
                        : (x, y, x + 1, y + 1);
                }
            }
            return result;
        }

        // The masked pixels of one color (or all of them) inside the rectangle, white where painted, as a one-bit image
        // drawn in an isolated gray group whose luminosity is the soft mask; outside the image the mask is black.
        PdfGraphicsState SoftMask((int Left, int Top, int Right, int Bottom) box, int rank)
        {
            int w = box.Right - box.Left, h = box.Bottom - box.Top;
            var samples = new byte[(long)w * h];
            for (int y = 0; y < h; y++)
            {
                token.ThrowIfCancellationRequested();
                for (int x = 0; x < w; x++)
                {
                    int px = box.Left + x, py = box.Top + y;
                    if (Masked(px, py) && (rank < 0 || layers.Index(px, py) == rank)) samples[(long)y * w + x] = 255;
                }
            }
            // Lossless JBIG2 (a PDF 1.4 filter, like soft masks) when it is smaller than CCITT G4 or Flate.
            var group = new PdfContentStreamBuilder().DrawImage(PdfImage.FromBitonal(w, h, samples, allowJbig2: true),
                box.Left * scale, (height - box.Bottom) * scale, w * scale, h * scale);
            var form = new PdfFormXObject(width * scale, height * scale, group, isolatedTransparencyGroup: true,
                transparencyGroupColorSpace: PdfTransparencyGroupColorSpace.Gray);
            return new PdfGraphicsState(softMask: new PdfSoftMask(form, PdfSoftMaskSubtype.Luminosity,
                new PdfSoftMaskBackdrop(0)));
        }
    }

    // A layer at its stored size, placed so each stored sample covers its reduction's square of page pixels from the
    // top left corner. Gray samples keep the page gamma as a calibrated gray space, as grayscale pages convert; color
    // samples are written as the sRGB values of their linear colors.
    private static void DrawLayer(PdfContentStreamBuilder content, PdfNativeImageLayer layer, double gamma,
        byte[] encoded, int pageHeight, double scale)
    {
        var samples = layer.Samples;
        PdfImage image;
        if (layer.Channels == 1)
            image = PdfImage.FromCalibratedGray(layer.Width, layer.Height, samples,
                new PdfCalGrayColorSpace(whiteX: 0.95047, whiteZ: 1.08883, gamma: gamma));
        else
        {
            var rgb = new byte[samples.Length];
            for (int i = 0; i < rgb.Length; i++) rgb[i] = encoded[samples[i]];
            image = PdfImage.FromRgb(layer.Width, layer.Height, rgb);
        }
        double coveredWidth = (double)layer.Width * layer.Reduction, coveredHeight = (double)layer.Height * layer.Reduction;
        content.DrawImage(image, 0, (pageHeight - coveredHeight) * scale, coveredWidth * scale, coveredHeight * scale);
    }

    // Each stored sample value's linear channel value, written back as an eight-bit sRGB value.
    private static byte[] SrgbSamples(float[] linear)
    {
        var result = new byte[256];
        for (int value = 0; value < 256; value++)
        {
            double l = Math.Clamp(linear[value], 0f, 1f);
            double encoded = l <= 0.0031308 ? 12.92 * l : 1.055 * Math.Pow(l, 1 / 2.4) - 0.055;
            result[value] = (byte)Math.Round(encoded * 255);
        }
        return result;
    }
}
