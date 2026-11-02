using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerPdf.Engine.Documents;

namespace KillerPDF.Services;

/// <summary>
/// The work behind Fix Page Orientation and Straighten Scanned Pages (#436) that needs no window:
/// reading which way pages run, measuring a scan's tilt and drawing it level. The page menu and
/// the command line both call it, so they turn and straighten pages the same way.
/// </summary>
internal static class PageArrangementService
{
    /// <summary>The smallest tilt, in degrees, that Straighten Scanned Pages corrects.</summary>
    internal const double MinimumTilt = 0.15;

    /// <summary>
    /// The rotation that shows each page's text upright, for every page that has text. A page
    /// whose text runs several ways maps to null; a page without text is left out.
    /// </summary>
    internal static Dictionary<int, int?> UprightFromText(string file, IReadOnlyList<int> pages, CancellationToken ct)
    {
        var result = new Dictionary<int, int?>();
        // Turning and straightening edit the document, so the file is a plain working copy.
        using var content = PdfContentDocument.Open(file, null);
        foreach (int page in pages)
        {
            ct.ThrowIfCancellationRequested();
            if (page >= content.NumberOfPages) continue;
            List<(double, double, int)> baselines;
            try
            {
                baselines = [.. content.GetPage(page + 1).GetWords()
                    .Where(word => word.Letters.Count > 0)
                    .Select(word =>
                    {
                        var first = word.Letters[0];
                        var last = word.Letters[^1];
                        return (last.EndBaseLine.X - first.StartBaseLine.X,
                            last.EndBaseLine.Y - first.StartBaseLine.Y, word.Letters.Count);
                    })];
            }
            catch (Exception error) when (error is FormatException or NotSupportedException or InvalidOperationException)
            {
                continue;
            }
            if (baselines.Count > 0) result[page] = PageGeometry.UprightRotation(baselines);
        }
        return result;
    }

    /// <summary>
    /// How far a scan must turn clockwise to read upright, or null when no reading is clearly
    /// better. The ink's line direction picks the two candidates, so a page costs two OCR
    /// passes at most, and one when it already reads cleanly as it is.
    /// </summary>
    internal static int? ScanUprightTurn(OcrService ocr, byte[] bgra, int width, int height, CancellationToken ct)
    {
        byte[] gray = PageGeometry.DownscaleGray(bgra, width, height, 800, out int grayWidth, out int grayHeight);
        double ratio = PageGeometry.LineAxisRatio(gray, grayWidth, grayHeight);
        int[] candidates = ratio >= 1.25 ? [0, 180] : ratio <= 0.8 ? [90, 270] : [0, 180, 90, 270];
        int best = -1;
        double bestScore = 0, runnerUp = 0;
        foreach (int turn in candidates)
        {
            ct.ThrowIfCancellationRequested();
            PdfOcrResult result;
            if (turn == 0)
                result = ocr.RecognizeBgra(bgra, width, height, cancellationToken: ct);
            else
            {
                PdfOcrBgraImage turned = PdfOcrImagePreprocessor.RotateBgra(bgra, width, height, turn, ct);
                result = ocr.RecognizeBgra(turned.Pixels, turned.Width, turned.Height, cancellationToken: ct);
            }
            double score = ReadingScore(result);
            if (score > bestScore) { runnerUp = bestScore; bestScore = score; best = turn; }
            else runnerUp = Math.Max(runnerUp, score);
            if (turn == 0 && score >= 0.85) return 0;
        }
        return best >= 0 && bestScore - runnerUp >= 0.08 ? best : null;
    }

    // Mean word confidence, scaled down for pages with only a few words so a stray reading of
    // noise cannot outvote real text.
    private static double ReadingScore(PdfOcrResult result)
    {
        var words = result.Words.Where(word => word.Text.Any(char.IsLetterOrDigit)).ToList();
        if (words.Count < 3) return 0;
        return words.Average(word => word.Confidence) * Math.Min(1.0, words.Count / 20.0);
    }

    /// <summary>Scanned pages among the targets that have no text layer, and how many scans do have one.</summary>
    internal static (List<int> Scans, int WithText) ScansWithoutText(string file, IReadOnlyList<int> pages, CancellationToken ct)
    {
        byte[] bytes = File.ReadAllBytes(file);
        IReadOnlyList<bool> scanned = PdfScanTextAnalyzer.ReadScannedPages(
            PdfDocument.Open(bytes, string.Empty), ct);
        using var content = PdfContentDocument.Open(bytes, null);
        var scans = new List<int>();
        int withText = 0;
        foreach (int page in pages)
        {
            ct.ThrowIfCancellationRequested();
            if (page >= scanned.Count || !scanned[page]) continue;
            bool hasText;
            try { hasText = content.GetPage(page + 1).GetWords().Any(); }
            catch (Exception error) when (error is FormatException or NotSupportedException or InvalidOperationException)
            {
                hasText = false;
            }
            if (hasText) withText++;
            else scans.Add(page);
        }
        return (scans, withText);
    }

    /// <summary>A scan's tilt in degrees, measured from its lines of text on a display-sized copy.</summary>
    internal static double MeasureTilt(byte[] bgra, int width, int height)
    {
        byte[] gray = PageGeometry.DownscaleGray(bgra, width, height, 1600, out int grayWidth, out int grayHeight);
        return PageGeometry.EstimateSkewDegrees(gray, grayWidth, grayHeight);
    }

    /// <summary>
    /// The page image turned back level by <paramref name="tilt"/> degrees and encoded the way
    /// the scan was made: black-and-white scans stay bitonal, gray and color ones become JPEG.
    /// </summary>
    internal static PdfEngineIntegration.RasterPage LevelPage(BitmapSource source, double tilt,
        double pageWidth, double pageHeight)
    {
        var pixels = new byte[source.PixelWidth * source.PixelHeight * 4];
        source.CopyPixels(pixels, source.PixelWidth * 4, 0);
        ScanTone tone = PageGeometry.ClassifyTone(pixels);

        BitmapSource level = RotateWithinPage(source, -tilt);
        if (tone == ScanTone.BlackAndWhite)
            level = PageQualityConverter.ApplyColorMode(level, PageColorMode.BlackAndWhite, 160);
        var levelPixels = new byte[level.PixelWidth * level.PixelHeight * 4];
        level.CopyPixels(levelPixels, level.PixelWidth * 4, 0);
        ReadOnlyMemory<byte> jpeg = tone == ScanTone.BlackAndWhite
            ? default : BitmapHelpers.EncodeJpeg(level, 90, tone == ScanTone.Grayscale);
        return new PdfEngineIntegration.RasterPage(level.PixelWidth, level.PixelHeight,
            pageWidth, pageHeight, levelPixels, jpeg,
            Bitonal: tone == ScanTone.BlackAndWhite,
            Grayscale: tone == ScanTone.Grayscale);
    }

    // Turns the image about its center inside the same canvas, on white, so the page keeps
    // its size and only the corners the turn uncovers are filled.
    private static BitmapSource RotateWithinPage(BitmapSource source, double degrees)
    {
        int width = source.PixelWidth, height = source.PixelHeight;
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            dc.PushTransform(new RotateTransform(degrees, width / 2.0, height / 2.0));
            dc.DrawImage(source, new Rect(0, 0, width, height));
            dc.Pop();
        }
        var result = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        result.Render(visual);
        result.Freeze();
        return result;
    }
}
