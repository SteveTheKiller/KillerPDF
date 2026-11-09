using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerPDF.Services;
using PdfRenderedFrame = KillerPdf.Engine.Rendering.PdfRenderedFrame;

namespace KillerPDF.Controls;

public partial class PdfViewer
{
    // ProofUnavailable shows the localized proof notice; English engine messages never reach it.
    private sealed record RenderWarning(string Message, string? DisplayFallbackOperation = null, bool ProofUnavailable = false);
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<BitmapSource, RenderWarning> RenderWarnings = new();

    private static void RememberRenderWarning(BitmapSource bitmap, PdfRenderedPage rendered)
    {
        string message = CompatibilityWarning(rendered);
        if (message.Length > 0 || rendered.DisplayFallbackOperation is not null)
            RenderWarnings.GetValue(bitmap, _ => new RenderWarning(message, rendered.DisplayFallbackOperation));
    }

    private static void RememberRenderWarning(BitmapSource bitmap, KillerPDF.Presentation.PdfViewingPixels viewing)
    {
        string message = viewing.Compatibility is { } page ? CompatibilityWarning(page) : "";
        string? fallback = viewing.DisplayFallbackOperation ?? viewing.Compatibility?.DisplayFallbackOperation;
        if (message.Length > 0 || fallback is not null || viewing.ProofUnavailable)
            RenderWarnings.AddOrUpdate(bitmap, new RenderWarning(message, fallback, viewing.ProofUnavailable));
    }

    private static string CompatibilityWarning(PdfRenderedPage rendered) => string.Join(" ", rendered.RenderDiagnostics
        .Where(item => item.BlocksPublication && item.Message != rendered.DisplayFallbackOperation)
        .Select(item => item.Message));

    private void PresentRenderWarning(FrameworkElement image, BitmapSource bitmap, int page, string? positionStatus = null)
    {
        string? warning = null;
        // A display the policy can manage still falls back when its native viewport could not be built.
        bool displayFallback = KillerPDF.Presentation.PdfDisplayFallbackNotice.Reason(_presentationDisplay, DocInvert) is not null
            || _nativeViewportUnavailable && !DocInvert;
        if (RenderWarnings.TryGetValue(bitmap, out var result))
        {
            if (result.Message.Length > 0)
                warning = string.Format(Loc("Str_RenderError"),
                    string.Format(Loc("Str_PageRenderError"), page + 1) + ": " + result.Message);
            if (result.ProofUnavailable)
            {
                string proofNotice = Loc("Str_CM_ProofUnavailablePage");
                warning = warning is null
                    ? (positionStatus ?? string.Format(Loc("Str_PageOf"), page + 1, ReadPageCount)) + ". " + proofNotice
                    : warning + " " + proofNotice;
            }
            displayFallback |= result.DisplayFallbackOperation is not null;
        }
        if (displayFallback)
        {
            string fallback = Loc("Str_CM_DisplayFallback");
            warning = warning is null
                ? (positionStatus ?? string.Format(Loc("Str_PageOf"), page + 1, ReadPageCount)) + ". " + fallback
                : warning + " " + fallback;
        }
        image.ToolTip = warning;
        if (warning is not null && page == _currentPage
            && (Host is null || Host.IsViewerFocused(this)))
        {
            // A render status replaces the open status, so it must keep the book's refused-chapter notice.
            if (_nativeDoc is { UnavailableChapterCount: > 0 } native)
                warning += " " + string.Format(Loc("Str_Epub_ChaptersUnavailable"), native.UnavailableChapterCount);
            SetStatus(warning);
        }
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
                RenderWarnings.GetValue(bitmap, _ => new RenderWarning("", failure));
            return (bitmap, new RenderMeasurement(page, parse, raster, timer.Elapsed.TotalMilliseconds, 0),
                monitor is null ? frame : null);
        }
        catch (NotSupportedException)
        {
            // The fallback keeps the requested proof and monitor conversion where they still
            // apply, and records what it could not keep instead of showing engine text.
            var fallback = native is not null
                ? PrepareNativePrimaryBitmap(native, page, maximumSize, 0, false, token, proof, monitor)
                : PreparePrimaryBitmap(request!, page, maximumSize, 0, false, token, proof, monitor);
            RenderWarnings.TryGetValue(fallback.Bitmap, out var recorded);
            string? displayFallback = KillerPDF.Presentation.PdfViewingFrameConversion.LinearFallbackOperation(
                monitor is not null, recorded?.DisplayFallbackOperation);
            if (displayFallback != recorded?.DisplayFallbackOperation)
                RenderWarnings.AddOrUpdate(fallback.Bitmap, (recorded ?? new RenderWarning(""))
                    with { DisplayFallbackOperation = displayFallback });
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
        bool proofUnavailable = false;
        try { frame = RenderNativeFrame(revision, page, maximumSize, maximumSize, token, proof); }
        catch (NotSupportedException) when (proof?.Enabled == true)
        {
            proofUnavailable = true;
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
        if (proofUnavailable || conversion?.FailureOperation is not null)
            RenderWarnings.GetValue(bitmap, _ => new RenderWarning("", conversion?.FailureOperation, proofUnavailable));
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
        var rendered = RenderViewingPage(lease, page, maximumSize, token, proof, monitor: monitor);
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

    private static KillerPDF.Presentation.PdfViewingPixels RenderViewingPage(PdfBackgroundRenderCache.Lease lease, int page,
        int maximumSize, CancellationToken token, KillerPdf.Engine.ColorManagement.PdfDocumentProofSettings? proof,
        int? maximumHeight = null, KillerPDF.Presentation.PdfSdrDisplayConverter? monitor = null)
    {
        int height = maximumHeight ?? maximumSize;
        return KillerPDF.Presentation.PdfViewingFrameConversion.Render(
            requested => lease.RenderFrame(page, maximumSize, height, token, requested),
            () => lease.Render(page, maximumSize, height, token), proof, monitor, token);
    }
}
