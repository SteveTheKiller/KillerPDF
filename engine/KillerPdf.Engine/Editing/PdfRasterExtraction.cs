using System.Globalization;
using System.IO.Compression;
using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;

namespace KillerPdf.Engine.Editing;

/// <summary>The selected pixels of a rendered page as a detached transparent image, with the pixel
/// rectangle of the source frame it covers.</summary>
public sealed class PdfRasterExtractionResult
{
    internal PdfRasterExtractionResult(PdfImageAsset image, int left, int top, int width, int height)
    {
        Image = image; Left = left; Top = top; Width = width; Height = height;
    }

    /// <summary>Gets the extracted image; unselected pixels are fully transparent.</summary>
    public PdfImageAsset Image { get; }
    /// <summary>Gets the left edge of the covered rectangle in source frame pixels.</summary>
    public int Left { get; }
    /// <summary>Gets the top edge of the covered rectangle in source frame pixels.</summary>
    public int Top { get; }
    /// <summary>Gets the width of the covered rectangle in pixels.</summary>
    public int Width { get; }
    /// <summary>Gets the height of the covered rectangle in pixels.</summary>
    public int Height { get; }
}

/// <summary>Turns a raster selection over a rendered page into editable images.</summary>
/// <remarks>
/// Pixels are copied from the frame's own sRGB output, never from a monitor- or proof-converted view.
/// The result is a canonical detached image, so it is limited like extracted page pictures to
/// <see cref="MaximumPixels"/> pixels and five MiB of compressed data.
/// </remarks>
public static class PdfRasterExtraction
{
    private const int MaximumAssetBytes = 5 * 1024 * 1024;

    /// <summary>Gets the largest extracted rectangle, in pixels, that a detached image can carry.</summary>
    public const int MaximumPixels = MaximumAssetBytes / 4;

    /// <summary>Gets the largest edge feathering radius in pixels.</summary>
    public const int MaximumFeatherRadius = 32;

    /// <summary>Whether the document's limits let page pixels be copied into a new image, by the same
    /// content-copy rule that extracting an original picture follows.</summary>
    public static bool AllowsExtraction(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document.PasswordAuthenticationRole != KillerPdf.Engine.Security.PdfPasswordAuthenticationRole.User
            || document.DeclaredPermissions is KillerPdf.Engine.Security.PdfDocumentPermissions { AllowContentCopying: true };
    }

    /// <summary>Copies the selected pixels into a transparent image of their bounding box.</summary>
    /// <param name="source">The rendered page.</param>
    /// <param name="selection">A selection with the frame's dimensions and at least one selected pixel.</param>
    /// <param name="featherRadius">Zero for hard edges; otherwise the box radius that softens the mask edge.</param>
    /// <param name="cancellationToken">Stops the work; nothing is returned once it is requested.</param>
    /// <exception cref="NotSupportedException">The selected rectangle exceeds the detached image limits.</exception>
    public static PdfRasterExtractionResult Extract(PdfRenderedFrame source, PdfRasterSelection selection,
        int featherRadius = 0, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(selection);
        cancellationToken.ThrowIfCancellationRequested();
        if (selection.Width != source.Width || selection.Height != source.Height)
            throw new ArgumentException("The selection dimensions must match the frame.", nameof(selection));
        if (featherRadius is < 0 or > MaximumFeatherRadius) throw new ArgumentOutOfRangeException(nameof(featherRadius));
        var (left, top, width, height) = Bounds(selection, featherRadius, cancellationToken);
        if ((long)width * height > MaximumPixels)
            throw new NotSupportedException("The selected area is too large for an extracted image.");
        byte[] bgra = source.ToSrgbBgra32(cancellationToken);
        byte[] alpha = Coverage(selection, left, top, width, height, featherRadius, cancellationToken);
        var rgb = new byte[width * height * 3];
        for (int y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = 0; x < width; x++)
            {
                int from = ((top + y) * source.Width + left + x) * 4, pixel = y * width + x;
                rgb[pixel * 3] = bgra[from + 2];
                rgb[pixel * 3 + 1] = bgra[from + 1];
                rgb[pixel * 3 + 2] = bgra[from];
                // The page's own transparency limits the copy, so a transparent source stays transparent.
                alpha[pixel] = (byte)((alpha[pixel] * bgra[from + 3] + 127) / 255);
            }
        }
        return new PdfRasterExtractionResult(CreateAsset(width, height, rgb, alpha, cancellationToken), left, top, width, height);
    }

    private static (int Left, int Top, int Width, int Height) Bounds(PdfRasterSelection selection, int radius,
        CancellationToken cancellationToken)
    {
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        ReadOnlySpan<byte> coverage = selection.Coverage;
        for (int y = 0; y < selection.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = 0; x < selection.Width; x++)
            {
                if (coverage[y * selection.Width + x] == 0) continue;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }
        if (maxX < 0) throw new ArgumentException("The selection is empty.", nameof(selection));
        minX = Math.Max(0, minX - radius); minY = Math.Max(0, minY - radius);
        maxX = Math.Min(selection.Width - 1, maxX + radius); maxY = Math.Min(selection.Height - 1, maxY + radius);
        return (minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    private static byte[] Coverage(PdfRasterSelection selection, int left, int top, int width, int height,
        int radius, CancellationToken cancellationToken)
    {
        ReadOnlySpan<byte> coverage = selection.Coverage;
        var alpha = new byte[width * height];
        if (radius == 0)
        {
            for (int y = 0; y < height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                coverage.Slice((top + y) * selection.Width + left, width).CopyTo(alpha.AsSpan(y * width, width));
            }
            return alpha;
        }
        // A separable box average of the binary mask over a (2r+1) square softens both sides of the edge.
        var rows = new int[width * height];
        for (int y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = 0; x < width; x++)
            {
                int sum = 0, sourceY = top + y;
                for (int dx = -radius; dx <= radius; dx++)
                {
                    int sourceX = left + x + dx;
                    if ((uint)sourceX < (uint)selection.Width && coverage[sourceY * selection.Width + sourceX] != 0) sum++;
                }
                rows[y * width + x] = sum;
            }
        }
        int window = (2 * radius + 1) * (2 * radius + 1);
        for (int y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = 0; x < width; x++)
            {
                int sum = 0;
                for (int dy = -radius; dy <= radius; dy++)
                {
                    int row = y + dy, sourceY = top + row;
                    if ((uint)sourceY >= (uint)selection.Height) continue;
                    if ((uint)row < (uint)height) { sum += rows[row * width + x]; continue; }
                    for (int dx = -radius; dx <= radius; dx++)
                    {
                        int sourceX = left + x + dx;
                        if ((uint)sourceX < (uint)selection.Width && coverage[sourceY * selection.Width + sourceX] != 0) sum++;
                    }
                }
                alpha[y * width + x] = (byte)((sum * 255 + window / 2) / window);
            }
        }
        return alpha;
    }

    /// <summary>Samples the color around a selection for a Cut fill: the rounded mean sRGB of the
    /// opaque unselected pixels that touch it, or white when there are none.</summary>
    public static (byte Red, byte Green, byte Blue) SampleFillColor(PdfRenderedFrame source, PdfRasterSelection selection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(selection);
        if (selection.Width != source.Width || selection.Height != source.Height)
            throw new ArgumentException("The selection dimensions must match the frame.", nameof(selection));
        byte[] bgra = source.ToSrgbBgra32(cancellationToken);
        ReadOnlySpan<byte> coverage = selection.Coverage;
        int width = selection.Width, height = selection.Height;
        long red = 0, green = 0, blue = 0, count = 0;
        for (int y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = 0; x < width; x++)
            {
                int pixel = y * width + x;
                if (coverage[pixel] != 0 || bgra[pixel * 4 + 3] != 255) continue;
                bool touches = x > 0 && coverage[pixel - 1] != 0 || x + 1 < width && coverage[pixel + 1] != 0
                    || y > 0 && coverage[pixel - width] != 0 || y + 1 < height && coverage[pixel + width] != 0;
                if (!touches) continue;
                blue += bgra[pixel * 4]; green += bgra[pixel * 4 + 1]; red += bgra[pixel * 4 + 2]; count++;
            }
        }
        if (count == 0) return (255, 255, 255);
        return (Mean(red), Mean(green), Mean(blue));
        byte Mean(long sum) => (byte)((sum + count / 2) / count);
    }

    /// <summary>A solid color image shaped like the extracted selection, for covering a cut area.</summary>
    public static PdfImageAsset CreateFill(PdfRasterSelection selection, PdfRasterExtractionResult extracted,
        byte red, byte green, byte blue, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(extracted);
        if ((long)extracted.Left + extracted.Width > selection.Width || (long)extracted.Top + extracted.Height > selection.Height)
            throw new ArgumentException("The extracted rectangle lies outside the selection.", nameof(extracted));
        byte[] alpha = Coverage(selection, extracted.Left, extracted.Top, extracted.Width, extracted.Height, 0, cancellationToken);
        var rgb = new byte[alpha.Length * 3];
        for (int pixel = 0; pixel < alpha.Length; pixel++)
        {
            if ((pixel & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            rgb[pixel * 3] = red; rgb[pixel * 3 + 1] = green; rgb[pixel * 3 + 2] = blue;
        }
        return CreateAsset(extracted.Width, extracted.Height, rgb, alpha, cancellationToken);
    }

    /// <summary>Builds the canonical single-image page that detached image assets use.</summary>
    internal static PdfImageAsset CreateAsset(int width, int height, byte[] rgb, byte[] alpha,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var seed = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage(width, height).Build());
        var seedPage = PdfPageTree.Read(seed).Pages[0];
        var update = new PdfIncrementalUpdateBuilder(seed);
        var mask = new PdfStream(new PdfDictionary(new Dictionary<PdfName, PdfObject>
        {
            [Name("Type")] = Name("XObject"), [Name("Subtype")] = Name("Image"),
            [Name("Width")] = new PdfInteger(width), [Name("Height")] = new PdfInteger(height),
            [Name("ColorSpace")] = Name("DeviceGray"), [Name("BitsPerComponent")] = new PdfInteger(8),
            [Name("Filter")] = Name("FlateDecode")
        }), Deflate(alpha, cancellationToken));
        var imageEntries = new Dictionary<PdfName, PdfObject>
        {
            [Name("Type")] = Name("XObject"), [Name("Subtype")] = Name("Image"),
            [Name("Width")] = new PdfInteger(width), [Name("Height")] = new PdfInteger(height),
            [Name("ColorSpace")] = Name("DeviceRGB"), [Name("BitsPerComponent")] = new PdfInteger(8),
            [Name("Filter")] = Name("FlateDecode"), [Name("SMask")] = update.AddObject(mask)
        };
        byte[] samples = Deflate(rgb, cancellationToken);
        if ((long)samples.Length + mask.EncodedData.Length > MaximumAssetBytes)
            throw new NotSupportedException("The selected area is too large for an extracted image.");
        var imageReference = update.AddObject(new PdfStream(new PdfDictionary(imageEntries), samples));
        var content = string.Create(CultureInfo.InvariantCulture, $"q {width} 0 0 {height} 0 0 cm /Image Do Q\n");
        var contentReference = update.AddObject(new PdfStream(new PdfDictionary([]), Encoding.ASCII.GetBytes(content)));
        var pageEntries = seedPage.Dictionary.ToDictionary(pair => pair.Key, pair => pair.Value);
        pageEntries[Name("Resources")] = new PdfDictionary([
            new(Name("XObject"), new PdfDictionary([new(Name("Image"), imageReference)]))]);
        pageEntries[Name("Contents")] = contentReference;
        update.ReplaceObject(seedPage.Reference.ObjectNumber, new PdfDictionary(pageEntries));
        cancellationToken.ThrowIfCancellationRequested();
        byte[] result = update.Build();
        cancellationToken.ThrowIfCancellationRequested();
        if (result.Length > MaximumAssetBytes)
            throw new NotSupportedException("The selected area is too large for an extracted image.");
        return new PdfImageAsset(width, height, result);
    }

    private static byte[] Deflate(byte[] data, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true)) zlib.Write(data);
        return output.ToArray();
    }

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
}
