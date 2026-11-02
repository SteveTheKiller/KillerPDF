using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerPDF.Services;

namespace KillerPDF.Controls;

public partial class PdfViewer
{
    private sealed record RenderWarning(string Message);
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<BitmapSource, RenderWarning> RenderWarnings = new();

    private static void RememberRenderWarning(BitmapSource bitmap, PdfRenderedPage rendered)
    {
        string message = string.Join(" ", rendered.RenderDiagnostics
            .Where(item => item.BlocksPublication).Select(item => item.Message));
        if (message.Length > 0) RenderWarnings.GetValue(bitmap, _ => new RenderWarning(message));
    }

    private void PresentRenderWarning(FrameworkElement image, BitmapSource bitmap, int page)
    {
        string? warning = RenderWarnings.TryGetValue(bitmap, out var result)
            ? string.Format(Loc("Str_PageRenderError"), page + 1) + ": " + result.Message : null;
        image.ToolTip = warning;
        if (warning is not null) SetStatus(string.Format(Loc("Str_RenderError"), warning));
    }

    internal RenderMeasurement? LastPrimaryRenderMeasurement { get; private set; }
    internal sealed record RenderMeasurement(int Page, double ParseMilliseconds,
        double RasterMilliseconds, double CopyMilliseconds, double PresentationMilliseconds);

    private (BitmapSource Bitmap, RenderMeasurement Measurement) PreparePrimaryBitmap(
        PdfBackgroundRenderCache.Request request, int page, int maximumSize, int rotation,
        bool invert, CancellationToken token)
    {
        var timer = Stopwatch.StartNew();
        using var lease = request.Rent(token);
        double parse = timer.Elapsed.TotalMilliseconds;
        timer.Restart();
        PdfRenderedPage rendered = lease.Render(page, maximumSize, maximumSize, token);
        double raster = timer.Elapsed.TotalMilliseconds;
        timer.Restart();
        int width = rendered.Width, height = rendered.Height;
        byte[] pixels = rendered.Pixels;
        if (width <= 0 || height <= 0 || pixels.Length != checked(width * height * 4))
            throw new InvalidOperationException("The renderer returned an empty page.");
        if (invert)
        {
            PdfContentDocument? content = null;
            try { BitmapHelpers.InvertBgraInPlaceExcept(pixels, width, height, ImageRectsFor(request.Path, request.Protection, page, ref content)); }
            finally { content?.Dispose(); }
        }
        if (rotation != 0)
            (pixels, width, height) = BitmapHelpers.RotateBitmap(pixels, width, height, rotation);
        token.ThrowIfCancellationRequested();
        double longest = Math.Max(width, height);
        int dipWidth = Math.Max(1, (int)Math.Round(2048.0 * width / longest));
        int dipHeight = Math.Max(1, (int)Math.Round(2048.0 * height / longest));
        var bitmap = new WriteableBitmap(width, height,
            96.0 * width / dipWidth, 96.0 * height / dipHeight, PixelFormats.Bgra32, null);
        bitmap.WritePixels(new Int32Rect(0, 0, width, height), pixels, width * 4, 0);
        bitmap.Freeze();
        RememberRenderWarning(bitmap, rendered);
        return (bitmap, new RenderMeasurement(page, parse, raster, timer.Elapsed.TotalMilliseconds, 0));
    }
}
