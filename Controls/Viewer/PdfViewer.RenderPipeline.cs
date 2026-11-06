using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerPDF.Services;
using PdfRenderedFrame = KillerPdf.Engine.Rendering.PdfRenderedFrame;

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

    private (BitmapSource Bitmap, RenderMeasurement Measurement, PdfRenderedFrame? Frame) PrepareLinearPrimaryBitmap(
        PdfBackgroundRenderCache.Request? request, PdfCapturedReadRevision? native, int page,
        int maximumSize, CancellationToken token, KillerPDF.Presentation.PdfSdrDisplayConverter? monitor = null,
        KillerPdf.Engine.ColorManagement.PdfDocumentProofSettings? proof = null)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            PdfRenderedFrame frame;
            double parse = 0;
            if (native is not null) frame = RenderNativeFrame(native, page, maximumSize, maximumSize, token, proof);
            else
            {
                using var lease = request!.Rent(token);
                parse = timer.Elapsed.TotalMilliseconds;
                timer.Restart();
                frame = lease.RenderFrame(page, maximumSize, maximumSize, token, proof);
            }
            double raster = timer.Elapsed.TotalMilliseconds;
            timer.Restart();
            int width = frame.Width, height = frame.Height;
            var conversion = monitor?.Convert(frame, token);
            if (conversion?.Outcome == KillerPDF.Presentation.PdfSdrConversionOutcome.Stale)
                throw new OperationCanceledException(token);
            byte[] pixels = conversion?.Outcome == KillerPDF.Presentation.PdfSdrConversionOutcome.Converted
                ? conversion.BgraPixels.ToArray() : frame.ToSrgbBgra32(token);
            int dipWidth = Math.Max(1, (int)Math.Round(2048d * width / Math.Max(width, height)));
            int dipHeight = Math.Max(1, (int)Math.Round(2048d * height / Math.Max(width, height)));
            var bitmap = new WriteableBitmap(width, height, 96d * width / dipWidth,
                96d * height / dipHeight, PixelFormats.Bgra32, null);
            bitmap.WritePixels(new Int32Rect(0, 0, width, height), pixels, width * 4, 0);
            bitmap.Freeze();
            if (conversion?.FailureOperation is { } failure)
                RenderWarnings.GetValue(bitmap, _ => new RenderWarning(failure));
            return (bitmap, new RenderMeasurement(page, parse, raster, timer.Elapsed.TotalMilliseconds, 0),
                monitor is null ? frame : null);
        }
        catch (NotSupportedException exception)
        {
            var fallback = native is not null
                ? PrepareNativePrimaryBitmap(native, page, maximumSize, 0, false, token, proof, monitor)
                : PreparePrimaryBitmap(request!, page, maximumSize, 0, false, token);
            RenderWarnings.GetValue(fallback.Bitmap, _ => new RenderWarning(exception.Message));
            return (fallback.Bitmap, fallback.Measurement, null);
        }
    }

    private (BitmapSource Bitmap, RenderMeasurement Measurement) PrepareNativePrimaryBitmap(
        PdfCapturedReadRevision revision, int page, int maximumSize, int rotation,
        bool invert, CancellationToken token, KillerPdf.Engine.ColorManagement.PdfDocumentProofSettings? proof = null,
        KillerPDF.Presentation.PdfSdrDisplayConverter? monitor = null)
    {
        var timer = Stopwatch.StartNew();
        PdfRenderedFrame frame;
        string? proofWarning = null;
        try { frame = RenderNativeFrame(revision, page, maximumSize, maximumSize, token, proof); }
        catch (NotSupportedException exception) when (proof?.Enabled == true)
        {
            proofWarning = exception.Message;
            frame = RenderNativeFrame(revision, page, maximumSize, maximumSize, token);
        }
        double raster = timer.Elapsed.TotalMilliseconds;
        timer.Restart();
        int width = frame.Width, height = frame.Height;
        var conversion = monitor?.Convert(frame, token);
        if (conversion?.Outcome == KillerPDF.Presentation.PdfSdrConversionOutcome.Stale)
            throw new OperationCanceledException(token);
        byte[] pixels = conversion?.Outcome == KillerPDF.Presentation.PdfSdrConversionOutcome.Converted
            ? conversion.BgraPixels.ToArray() : frame.ToSrgbBgra32(token);
        if (conversion?.FailureOperation is { } failure)
            proofWarning = proofWarning is null ? failure : proofWarning + " " + failure;
        if (invert && BitmapHelpers.DocInvertImages)
            BitmapHelpers.InvertBgraInPlaceExcept(pixels, width, height, []);
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
        if (proofWarning is not null) RenderWarnings.GetValue(bitmap, _ => new RenderWarning(proofWarning));
        return (bitmap, new RenderMeasurement(page, 0, raster, timer.Elapsed.TotalMilliseconds, 0));
    }

    private (BitmapSource Bitmap, RenderMeasurement Measurement) PreparePrimaryBitmap(
        PdfBackgroundRenderCache.Request request, int page, int maximumSize, int rotation,
        bool invert, CancellationToken token,
        KillerPdf.Engine.ColorManagement.PdfDocumentProofSettings? proof = null,
        KillerPDF.Presentation.PdfSdrDisplayConverter? monitor = null)
    {
        var timer = Stopwatch.StartNew();
        using var lease = request.Rent(token);
        double parse = timer.Elapsed.TotalMilliseconds;
        timer.Restart();
        PdfRenderedPage rendered = RenderViewingPage(lease, page, maximumSize, token, proof, monitor: monitor);
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

    private static PdfRenderedPage RenderViewingPage(PdfBackgroundRenderCache.Lease lease, int page,
        int maximumSize, CancellationToken token, KillerPdf.Engine.ColorManagement.PdfDocumentProofSettings? proof,
        int? maximumHeight = null, KillerPDF.Presentation.PdfSdrDisplayConverter? monitor = null)
    {
        int height = maximumHeight ?? maximumSize;
        if (proof?.Enabled != true && monitor is null) return lease.Render(page, maximumSize, height, token);
        try
        {
            var frame = lease.RenderFrame(page, maximumSize, height, token, proof);
            var conversion = monitor?.Convert(frame, token);
            if (conversion?.Outcome == KillerPDF.Presentation.PdfSdrConversionOutcome.Stale)
                throw new OperationCanceledException(token);
            byte[] pixels = conversion?.Outcome == KillerPDF.Presentation.PdfSdrConversionOutcome.Converted
                ? conversion.BgraPixels.ToArray() : frame.ToSrgbBgra32(token);
            return new PdfRenderedPage(frame.Width, frame.Height, pixels, PdfRenderBackend.Engine, null)
            {
                RenderDiagnostics = conversion?.FailureOperation is { } failure
                    ? [new(PdfRenderDiagnosticKind.Degraded, failure)] : []
            };
        }
        catch (NotSupportedException error)
        {
            var normal = lease.Render(page, maximumSize, height, token);
            return normal with { RenderDiagnostics = [.. normal.RenderDiagnostics,
                new(PdfRenderDiagnosticKind.Degraded, error.Message)] };
        }
    }
}
