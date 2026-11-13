using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Rendering;

namespace KillerPdf.Engine.Formats.Djvu;

public sealed partial class DjvuDocument
{
    // A compound page (7.1.3) converts as its layers, so text stays as sharp as the scan and the file stays small:
    // the background as an image at its own stored resolution, and the foreground as the page's one-bit mask used as a
    // luminosity soft mask, once per foreground color (each FGbz palette color, or black) over a fill of that color,
    // or once over the FG44 image. Each color's mask is cut to the rectangle its pixels cover. A page with many colors
    // (or colors whose rectangles together cover several pages) draws them as one RGB image under one mask instead. Everything is drawn in
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
                string reason = usedColors > MaximumColorMasks
                    ? $"Page {pageIndex + 1} has {usedColors} foreground colors. "
                    : $"Page {pageIndex + 1} has foreground colors that overlap across the page. ";
                diagnostics.Add(new("ForegroundColorsMerged", reason +
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

        // The foreground colors inside the rectangle as one RGB image, each pixel its palette color. Pixels no shape
        // covers take the nearest shape color so a viewer that smooths the image never averages black into an edge.
        void DrawPaletteImage((int Left, int Top, int Right, int Bottom) box)
        {
            int w = box.Right - box.Left, h = box.Bottom - box.Top;
            var rgb = BuildPaletteRgb(w, h, (x, y) => Masked(box.Left + x, box.Top + y),
                (x, y) => layers.Index(box.Left + x, box.Top + y), layers.Palette, encoded, token);
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

    // The RGB samples of a merged foreground, row by row. A masked pixel holds its palette color (through the eight-bit
    // sRGB table). Every other pixel holds the color of the nearest masked pixel in its row (the left one on a tie),
    // and a row with no masked pixel copies the nearest row that has one, so a viewer that averages the image while
    // scaling it never pulls black into a glyph edge. The page mask still hides every unmasked pixel.
    internal static byte[] BuildPaletteRgb(int w, int h, Func<int, int, bool> masked, Func<int, int, int> rank,
        byte[] palette, byte[] encoded, CancellationToken token)
    {
        var rgb = new byte[3L * w * h];
        var nearest = new int[w];
        var rowHasShape = new bool[h];
        for (int y = 0; y < h; y++)
        {
            token.ThrowIfCancellationRequested();
            int last = -1;
            for (int x = 0; x < w; x++)
            {
                if (masked(x, y)) last = x;
                nearest[x] = last;
            }
            if (last < 0) continue;
            rowHasShape[y] = true;
            // Right to left, taking the right neighbor only where it is strictly closer than the left one.
            int next = -1;
            for (int x = w - 1; x >= 0; x--)
            {
                if (nearest[x] == x) next = x;
                else if (next >= 0 && (nearest[x] < 0 || next - x < x - nearest[x])) nearest[x] = next;
            }
            for (int x = 0; x < w; x++)
            {
                int source = nearest[x];
                int shape = rank(source, y);
                long at = 3L * (y * w + x);
                for (int channel = 0; channel < 3; channel++) rgb[at + channel] = encoded[palette[3 * shape + channel]];
            }
        }
        int above = -1;
        var copyFrom = new int[h];
        for (int y = 0; y < h; y++)
        {
            if (rowHasShape[y]) above = y;
            copyFrom[y] = above;
        }
        int below = -1;
        for (int y = h - 1; y >= 0; y--)
        {
            if (rowHasShape[y]) below = y;
            else if (below >= 0 && (copyFrom[y] < 0 || below - y < y - copyFrom[y])) copyFrom[y] = below;
        }
        for (int y = 0; y < h; y++)
            if (!rowHasShape[y] && copyFrom[y] >= 0)
                Array.Copy(rgb, 3L * copyFrom[y] * w, rgb, 3L * y * w, 3L * w);
        return rgb;
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
        PdfImage image;
        if (options.JpegEncoder is { } encoder)
        {
            try { image = PdfImage.FromJpeg(encoder(rgb, width, height, 3, options.JpegQuality)); }
            catch (Exception error) when (error is not (OperationCanceledException or OutOfMemoryException))
            {
                // The platform encoder failed on this page: keep it lossless, and the rest of the book unaffected.
                image = PdfImage.FromRgbRows(width, height, rgb, token);
                diagnostics.Add(new("JpegEncodingFailed", $"Page {pageIndex + 1} could not be compressed as JPEG ({error.Message.TrimEnd('.')}). " +
                    "It is stored as a lossless image, which makes it larger.") { PageIndex = pageIndex });
            }
        }
        else image = PdfImage.FromRgbRows(width, height, rgb, token);
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
