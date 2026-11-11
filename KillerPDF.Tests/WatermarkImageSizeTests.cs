using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using KillerPDF.Services;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Filters;
using KillerPdf.Engine.Objects;
using EngineDocument = KillerPdf.Engine.Documents.PdfDocument;
using Xunit;

namespace KillerPDF.Tests;

// #478: an image watermark at Scale 100 must be applied at the size the dialog previews: the
// image's own size from its DPI, with Scale as a percent of that. The apply path used half the
// page width instead, so a 298 x 88 px, 144 DPI logo on a landscape page came out about 2.8x
// larger than the preview.
public sealed class WatermarkImageSizeTests
{
    private const double PageWidth = 842, PageHeight = 595;

    [Fact]
    public void ImageWatermarkAtScale100BurnsAtTheImagesOwnSizeFromItsDpi()
    {
        // 5669 px/m is the 144 DPI the issue's PNG carries: 298 px / (5669 * 0.0254) dpi * 72.
        byte[] png = Png(298, 88, pixelsPerMeter: 5669);
        (double width, double height) = BurnedWatermarkSize(png);
        double dpi = 5669 * 0.0254;
        Assert.InRange(width, 298 / dpi * 72 - 0.05, 298 / dpi * 72 + 0.05);   // 149.0 pt
        Assert.InRange(height, 88 / dpi * 72 - 0.05, 88 / dpi * 72 + 0.05);    // 44.0 pt
    }

    [Fact]
    public void ImageWatermarkWithoutDpiBurnsAtTheNinetySixDpiFallback()
    {
        // No pHYs chunk: images without a resolution are 96 DPI, the same fallback the
        // image-to-PDF import uses, so 298 px is 298 / 96 * 72 = 223.5 pt.
        byte[] png = Png(298, 88, pixelsPerMeter: null);
        (double width, double height) = BurnedWatermarkSize(png);
        Assert.InRange(width, 223.45, 223.55);
        Assert.InRange(height, 65.95, 66.05);
    }

    // In a DPI-aware process GDI+ reports the display's DPI (144 at 150%) for an image that stores
    // none, so the burn trusts the reported resolution only when the image says it is stored. This
    // holds on any build machine, whatever its display scaling.
    [Fact]
    public void AReportedResolutionTheImageDoesNotStoreFallsBackToNinetySixDpi()
    {
        (double dpiX, double dpiY) = PdfEngineBurn.StoredResolution(flags: 0x2, horizontal: 144, vertical: 144);

        (double width, double height) = WatermarkImageSize.Points(298, 88, dpiX, dpiY, PageWidth, 1.0);
        Assert.InRange(width, 223.45, 223.55);    // 298 / 96 * 72
        Assert.InRange(height, 65.95, 66.05);     // 88 / 96 * 72
    }

    [Fact]
    public void AStoredResolutionIsUsed()
    {
        const int hasRealDpi = 0x1000;   // ImageFlags.HasRealDpi
        Assert.Equal((72d, 144d), PdfEngineBurn.StoredResolution(hasRealDpi, 72, 144));
    }

    [Theory]
    [InlineData(5669)]
    [InlineData(2835)]   // 72 DPI
    [InlineData(null)]
    public void PreviewDecoderGetsTheSameSizeTheBurnDraws(int? pixelsPerMeter)
    {
        // The dialog preview and the on-page stamp decode with WPF while the burn decodes with
        // GDI+; both must reach the same DPI, or preview and output drift apart again.
        byte[] png = Png(298, 88, pixelsPerMeter);
        var bitmap = new System.Windows.Media.Imaging.BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
        bitmap.StreamSource = new MemoryStream(png);
        bitmap.EndInit();
        (double previewWidth, double previewHeight) = WatermarkImageSize.Points(bitmap.PixelWidth,
            bitmap.PixelHeight, bitmap.DpiX, bitmap.DpiY, PageWidth, 1.0);

        (double width, double height) = BurnedWatermarkSize(png);
        Assert.InRange(width, previewWidth - 0.01, previewWidth + 0.01);
        Assert.InRange(height, previewHeight - 0.01, previewHeight + 0.01);
    }

    [Fact]
    public void ScaleIsAPercentOfTheImagesOwnSizeAndAWideImageStopsAtThePageWidth()
    {
        // 298 px at 72 DPI is 298 pt; Scale 50 halves it.
        Assert.Equal(149, WatermarkImageSize.Points(298, 88, 72, 72, PageWidth, 0.5).Width, 6);
        // 2000 px at 72 DPI would be 2000 pt; it is capped at the 842 pt page, keeping its shape.
        (double width, double height) = WatermarkImageSize.Points(2000, 500, 72, 72, PageWidth, 1.0);
        Assert.Equal(842, width, 6);
        Assert.Equal(210.5, height, 6);
    }

    // GDI+ cannot read JPEG XR, so the burn reads its stored resolution from the WIC frame the stamp preview uses.
    // 96 px stored at 192 DPI is 96 / 192 * 72 = 36 pt; sizing it at the 96 DPI fallback would give 72 pt.
    [Fact]
    public void AJpegXrWatermarkIsSizedFromItsStoredResolution()
    {
        byte[] pixels = new byte[96 * 96 * 4];
        Array.Fill(pixels, (byte)200);
        var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(96, 96, 192, 192,
            System.Windows.Media.PixelFormats.Bgra32, null, pixels, 96 * 4);
        var encoder = new System.Windows.Media.Imaging.WmpBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        (double width, double height) = BurnedWatermarkSize(stream.ToArray());
        Assert.InRange(width, 35.95, 36.05);
        Assert.InRange(height, 35.95, 36.05);
    }

    private static (double Width, double Height) BurnedWatermarkSize(byte[] png)
    {
        string folder = Path.Combine(Path.GetTempPath(), $"killerpdf-wm478-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            string image = Path.Combine(folder, "logo.png");
            string pdf = Path.Combine(folder, "page.pdf");
            File.WriteAllBytes(image, png);
            File.WriteAllBytes(pdf, new PdfDocumentBuilder().AddBlankPage(PageWidth, PageHeight).Build());
            var spec = new StampSpec
            {
                WmEnabled = true, WmIsImage = true, WmImagePath = image, WmScale = 1.0,
                WmAngle = 0, WmOpacity = 1, WmPosH = 1, WmPosV = 1,
            };
            PdfEngineBurn.Burn(pdf, new Dictionary<int, List<PageAnnotation>>(),
                new Dictionary<int, (int w, int h)>(), spec);

            EngineDocument document = EngineDocument.Open(File.ReadAllBytes(pdf));
            var text = new StringBuilder();
            foreach (int objectNumber in document.CrossReferences.Keys)
                if (document.Resolve(objectNumber) is PdfStream stream)
                    try { text.AppendLine(Encoding.Latin1.GetString(PdfStreamDecoder.Decode(stream, document.Resolve))); }
                    catch { }
            Match draw = Regex.Match(text.ToString(),
                @"(-?[\d.]+) 0 0 (-?[\d.]+) -?[\d.]+ -?[\d.]+ cm\s*/Im\d+ Do");
            Assert.True(draw.Success, "the burn drew no watermark image");
            return (Math.Abs(double.Parse(draw.Groups[1].Value, CultureInfo.InvariantCulture)),
                Math.Abs(double.Parse(draw.Groups[2].Value, CultureInfo.InvariantCulture)));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    // A plain 8-bit RGBA PNG, optionally with a pHYs chunk in pixels per meter.
    private static byte[] Png(int width, int height, int? pixelsPerMeter)
    {
        var raw = new MemoryStream();
        for (int y = 0; y < height; y++)
        {
            raw.WriteByte(0);
            for (int x = 0; x < width; x++) raw.Write([(byte)x, (byte)y, 0x80, 0xFF]);
        }
        var packed = new MemoryStream();
        using (var zlib = new ZLibStream(packed, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(raw.ToArray());

        var output = new MemoryStream();
        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        byte[] header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; header[9] = 6;
        Chunk(output, "IHDR", header);
        if (pixelsPerMeter is int ppm)
        {
            byte[] physical = new byte[9];
            BinaryPrimitives.WriteInt32BigEndian(physical, ppm);
            BinaryPrimitives.WriteInt32BigEndian(physical.AsSpan(4), ppm);
            physical[8] = 1;
            Chunk(output, "pHYs", physical);
        }
        Chunk(output, "IDAT", packed.ToArray());
        Chunk(output, "IEND", []);
        return output.ToArray();
    }

    private static void Chunk(Stream output, string type, byte[] data)
    {
        byte[] length = new byte[4], crc = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        byte[] typed = [.. Encoding.ASCII.GetBytes(type), .. data];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(typed));
        output.Write(length);
        output.Write(typed);
        output.Write(crc);
    }

    private static uint Crc32(byte[] bytes)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte value in bytes)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
        }
        return ~crc;
    }
}
