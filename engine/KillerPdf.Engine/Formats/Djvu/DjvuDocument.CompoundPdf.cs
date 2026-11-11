using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Rendering;

namespace KillerPdf.Engine.Formats.Djvu;

public sealed partial class DjvuDocument
{
    // A compound page (7.1.3) converts as its layers, so text stays as sharp as the scan and the file stays small:
    // the background as an image at its own stored resolution, and the foreground as the page's one-bit mask used as a
    // luminosity soft mask, once per foreground color (each FGbz palette color, or black) over a fill of that color,
    // or once over the FG44 image. Each color's mask is cut to the rectangle its pixels cover. A page with many colors\r\n    // (or colors whose rectangles together cover several pages) draws them as one RGB image under one mask instead. Everything is drawn in
    // stored orientation under the page's turn, as the located text is.
    // Past this many colors, or boxes covering more than this many page areas together, the foreground is one image.
    private const int MaximumColorMasks = 16, BoxAreaPages = 4;

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
        if (layers.Background is { } background) DrawLayer(content, background, gamma, encoded, scale);

        int stride = (width + 7) / 8;
        var mask = raster.Samples;
        bool Masked(int x, int y) => (mask[y * stride + (x >> 3)] & (0x80 >> (x & 7))) != 0;
        if (layers.Foreground is { } foreground)
        {
            if (Bounds(rank: -1) is { } box)
            {
                content.SaveState().SetGraphicsState(SoftMask(box, rank: -1));
                DrawLayer(content, foreground, gamma, encoded, scale);
                content.RestoreState();
            }
        }
        else
        {
            var boxes = ColorBounds();
            int usedColors = boxes.Count(box => box is not null);
            long boxArea = boxes.Sum(box => box is { } b ? (long)(b.Right - b.Left) * (b.Bottom - b.Top) : 0);
            if (usedColors > MaximumColorMasks || boxArea > BoxAreaPages * width * height)
            {
                // Many colors, or boxes that overlap across the page: one image of the colors under one mask.
                if (Bounds(rank: -1) is { } all)
                {
                    content.SaveState().SetGraphicsState(SoftMask(all, rank: -1));
                    DrawPaletteImage(all);
                    content.RestoreState();
                }
                diagnostics.Add(new("ForegroundColorsMerged", $"Page {pageIndex + 1} has {usedColors} foreground colors. " +
                    "They are drawn as one image under one mask, with the colors unchanged.") { PageIndex = pageIndex });
                boxes = [];
            }
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

        // The foreground colors inside the rectangle as one RGB image, each pixel its palette color (black where no
        // shape lies, which the page mask hides).
        void DrawPaletteImage((int Left, int Top, int Right, int Bottom) box)
        {
            int w = box.Right - box.Left, h = box.Bottom - box.Top;
            var rgb = new byte[3L * w * h];
            for (int y = 0; y < h; y++)
            {
                token.ThrowIfCancellationRequested();
                for (int x = 0; x < w; x++)
                {
                    int px = box.Left + x, py = box.Top + y;
                    if (!Masked(px, py)) continue;
                    int rank = layers.Index(px, py);
                    long at = 3L * (y * w + x);
                    for (int channel = 0; channel < 3; channel++) rgb[at + channel] = encoded[layers.Palette[3 * rank + channel]];
                }
            }
            content.DrawImage(PdfImage.FromRgb(w, h, rgb), box.Left * scale, (height - box.Bottom) * scale, w * scale, h * scale);
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

    // A color photo page (one color IW44 image at page size, no mask) converts as one RGB image at its stored
    // resolution, never reduced: the eight-bit sRGB values of its linear colors at the page gamma, as compound
    // backgrounds convert, written over the decoded samples in place, and drawn in stored orientation under the page's
    // turn. The full decode holds at most DjvuIw44Decoder.ColorWorkingBytes (about 240 MB for a 600 dpi letter page)
    // and is released before the image is compressed. A supplied platform encoder stores photo pages as JPEG;
    // without it the image uses lossless Flate compression.
    private void AddColorPhotoPdfPage(PdfDocumentBuilder builder, DjvuPageInfo page, List<Chunk> chunks,
        DjvuTextPage text, int pageIndex, DjvuPdfConversionOptions options,
        ICollection<DjvuDiagnostic> diagnostics, CancellationToken token)
    {
        var raster = DecodeColorIw44Raster(page, WithoutAnnotations(chunks), token, callerBytesPerPixel: 0);
        var encoded = SrgbSamples(raster.Linear);
        var rgb = raster.Samples;
        for (int i = 0; i < rgb.Length; i++) rgb[i] = encoded[rgb[i]];
        token.ThrowIfCancellationRequested();
        int width = page.Width, height = page.Height;
        double scale = 72d / page.Dpi;
        bool quarterTurn = page.Orientation is DjvuOrientation.Clockwise90 or DjvuOrientation.CounterClockwise90;
        var image = options.JpegEncoder is { } encoder
            ? PdfImage.FromJpeg(encoder(rgb, width, height, 3, options.JpegQuality))
            : PdfImage.FromRgbRows(width, height, rgb, token);
        token.ThrowIfCancellationRequested();
        var content = new PdfContentStreamBuilder().SaveState();
        switch (page.Orientation)
        {
            case DjvuOrientation.Clockwise90: content.Transform(0, -1, 1, 0, 0, width * scale); break;
            case DjvuOrientation.CounterClockwise90: content.Transform(0, 1, -1, 0, height * scale, 0); break;
            case DjvuOrientation.UpsideDown: content.Transform(-1, 0, 0, -1, width * scale, height * scale); break;
        }
        content.DrawImage(image, 0, 0, width * scale, height * scale).RestoreState();
        AddLocatedPdfText(content, text, page, pageIndex, diagnostics, token);
        builder.AddPage((quarterTurn ? height : width) * scale, (quarterTurn ? width : height) * scale, content);
    }

    // A layer at its stored size, placed so each stored sample covers its reduction's square of page pixels from the
    // bottom left corner. Gray samples keep the page gamma as a calibrated gray space, as grayscale pages convert; color
    // samples are written as the sRGB values of their linear colors.
    private static void DrawLayer(PdfContentStreamBuilder content, PdfNativeImageLayer layer, double gamma,
        byte[] encoded, double scale)
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
        content.DrawImage(image, 0, 0, coveredWidth * scale, coveredHeight * scale);
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
