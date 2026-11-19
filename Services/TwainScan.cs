using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NTwain;
using NTwain.Data;

namespace KillerPDF.Services;

internal sealed record TwainScanResult(IReadOnlyList<string> Pages,
    IReadOnlyList<Exception> Errors, bool Canceled)
{
    internal bool Completed => Errors.Count == 0 && !Canceled;
}

internal enum ScannerStatus { Reported, NotClosed, NotStarted }

/// <summary>A TWAIN return code the scan stopped on. The app words it in the interface language
/// (<see cref="Key"/>, with the code as {0}); the English message is for logs.</summary>
internal sealed class ScannerStatusException(ScannerStatus status, string code)
    : InvalidOperationException(string.Format(status switch
    {
        ScannerStatus.NotClosed => "The scanner did not close ({0}).",
        ScannerStatus.NotStarted => "The scanner did not start ({0}).",
        _ => "The scanner reported an error ({0}).",
    }, code))
{
    internal string Code { get; } = code;

    internal string Key => status switch
    {
        ScannerStatus.NotClosed => "Str_Scan_NotClosed",
        ScannerStatus.NotStarted => "Str_Scan_NotStarted",
        _ => "Str_Scan_DeviceError",
    };
}

/// <summary>
/// Scan to PDF (#73): one TWAIN session on the window's message loop. Each page the scanner
/// hands over is saved as an image, gray and black-and-white pages as PNG and color pages as
/// JPEG, at the resolution the scanner reports, ready to be merged into a PDF.
/// </summary>
internal sealed class TwainScan : IDisposable
{
    private readonly ITwainSession _session;
    private readonly IntPtr _window;

    /// <summary>False when the 64-bit TWAIN Data Source Manager (TWAINDSM.dll) is not installed.</summary>
    internal static bool ManagerInstalled => PlatformInfo.Current.IsSupported;

    internal TwainScan(ITwainSession session, IntPtr window)
    {
        _session = session;
        _window = window;
    }

    internal static TwainScan Open(IntPtr window)
    {
        var session = new TwainSession(TWIdentity.CreateFromAssembly(DataGroups.Image, typeof(TwainScan).Assembly));
        ReturnCode code = session.Open(new WpfMessageLoopHook(window));
        if (code != ReturnCode.Success)
            throw new InvalidOperationException($"The TWAIN manager did not open ({code}).");
        return new TwainScan(session, window);
    }

    internal IReadOnlyList<DataSource> Sources => [.. _session.GetSources()];

    /// <summary>
    /// Scans from <paramref name="source"/> until the scanner is done or the user closes its
    /// window, and returns saved pages together with any failure or cancellation.
    /// </summary>
    internal Task<TwainScanResult> ScanAsync(IDataSource source, bool showSettings, string folder)
    {
        Directory.CreateDirectory(folder);
        var pages = new List<string>();
        var done = new TaskCompletionSource<TwainScanResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var errors = new List<Exception>();
        bool canceled = false;
        TwainSession? nativeSession = _session as TwainSession;

        void Transferred(object? sender, DataTransferredEventArgs e)
        {
            try
            {
                using Stream? image = e.GetNativeImageStream();
                if (image is null) throw new InvalidOperationException("The scanner returned no readable image.");
                pages.Add(SavePage(image, folder, pages.Count + 1));
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException or InvalidOperationException
                or ArgumentException or FileFormatException or UnauthorizedAccessException)
            {
                errors.Add(ex);
            }
        }
        void Failed(object? sender, TransferErrorEventArgs e) =>
            errors.Add(e.Exception ?? new ScannerStatusException(ScannerStatus.Reported, e.ReturnCode.ToString()));
        void Canceled(object? sender, TransferCanceledEventArgs e) => canceled = true;
        void Disabled(object? sender, EventArgs e)
        {
            canceled |= _session.DisableReason == Message.CloseDSReq;
            Complete();
        }
        void Complete()
        {
            if (done.Task.IsCompleted) return;
            _session.DataTransferred -= Transferred;
            _session.TransferError -= Failed;
            _session.SourceDisabled -= Disabled;
            if (nativeSession is not null) nativeSession.TransferCanceled -= Canceled;
            try
            {
                ReturnCode close = source.Close();
                if (close != ReturnCode.Success)
                    errors.Add(new ScannerStatusException(ScannerStatus.NotClosed, close.ToString()));
            }
            catch (Exception ex) { errors.Add(ex); }
            done.TrySetResult(new(Array.AsReadOnly(pages.ToArray()), Array.AsReadOnly(errors.ToArray()), canceled));
        }

        _session.DataTransferred += Transferred;
        _session.TransferError += Failed;
        _session.SourceDisabled += Disabled;
        if (nativeSession is not null) nativeSession.TransferCanceled += Canceled;
        try
        {
            ReturnCode code = source.Open();
            if (code == ReturnCode.Success)
                code = source.Enable(showSettings ? SourceEnableMode.ShowUI : SourceEnableMode.NoUI, false, _window);
            if (code != ReturnCode.Success)
            {
                errors.Add(new ScannerStatusException(ScannerStatus.NotStarted, code.ToString()));
                Complete();
            }
        }
        catch (Exception ex)
        {
            errors.Add(ex);
            Complete();
        }
        return done.Task;
    }

    // Gray and black-and-white pages compress best and stay exact as PNG; color pages are JPEG.
    private static string SavePage(Stream image, string folder, int number)
    {
        BitmapFrame frame = BitmapDecoder.Create(image, BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad).Frames[0];
        PixelFormat format = frame.Format;
        bool gray = format == PixelFormats.BlackWhite || format == PixelFormats.Gray2
            || format == PixelFormats.Gray4 || format == PixelFormats.Gray8 || format == PixelFormats.Gray16
            || format == PixelFormats.Indexed1 && IsGrayPalette(frame.Palette)
            || format == PixelFormats.Indexed8 && IsGrayPalette(frame.Palette);
        BitmapEncoder encoder = gray ? new PngBitmapEncoder() : new JpegBitmapEncoder { QualityLevel = 90 };
        encoder.Frames.Add(frame);
        string path = Path.Combine(folder, $"scan-{number:000}{(gray ? ".png" : ".jpg")}");
        AtomicFileOutput.Write(path, encoder.Save);
        return path;
    }

    private static bool IsGrayPalette(BitmapPalette? palette) =>
        palette is not null && palette.Colors.All(color => color.R == color.G && color.G == color.B);

    public void Dispose()
    {
        try { _session.Close(); }
        catch (Exception ex) when (ex is InvalidOperationException or TwainStateException) { }
    }
}
