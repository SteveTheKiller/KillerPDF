using KillerPdf.Engine.Documents;
namespace KillerPDF.Services
{
    /// <summary>
    /// Uses an installed engine recognition model when one is available, with Tesseract retained as
    /// the migration fallback. Instances are not thread-safe, so each operation owns its service.
    /// </summary>
    internal sealed class OcrService : IDisposable
    {
        private static readonly PdfOcrOptions FallbackRasterOptions = new(["und"],
            deskew: false, correctOrientation: false, detectPageSegments: false);
        private readonly string _dataPath;
        private readonly string _language;
        private readonly bool _usesDefaultDataPath;
        private readonly PdfOcrRasterProvider? _engineProvider;
        private readonly PdfOcrLanguageModel? _engineLanguageModel;
        private readonly PdfOcrOptions _engineRasterOptions;
        private TesseractOcrFallback? _fallback;

        /// <param name="tessDataPath">Folder holding installed OCR models. Defaults to the persistent OCR model folder.</param>
        /// <param name="language">Tesseract language code(s), e.g. "eng" or "eng+ben".</param>
        public OcrService(string? tessDataPath = null, string language = "eng")
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(language);
            _usesDefaultDataPath = tessDataPath is null;
            _dataPath = tessDataPath ?? OcrNativeBootstrap.TessDataDir;
            _language = language;
            _engineRasterOptions = new PdfOcrOptions(language.Split('+',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                deskew: false, correctOrientation: false, removeBackground: true,
                removeNoise: true, detectPageSegments: true);
            if (PdfOcrRecognitionModelFiles.TryCreateCatalog(
                _dataPath, language, out PdfOcrRecognitionModelCatalog? models))
            {
                PdfOcrRecognitionModelFiles.TryLoadLanguageCombined(
                    _dataPath, language, out _engineLanguageModel);
                _engineProvider = new PdfEngineOcrProvider(models!,
                    typeof(PdfOcrRecognitionModel).Assembly.GetName().Version ?? new Version(1, 0),
                    _ => _engineLanguageModel);
            }
        }

        /// <summary>
        /// OCR a rendered page straight from the render pipeline (raw BGRA, 4 bytes/pixel).
        /// </summary>
        public PdfOcrResult RecognizeBgra(ReadOnlyMemory<byte> bgra, int width, int height,
            string? characterWhitelist = null,
            CancellationToken cancellationToken = default)
        {
            if (_engineProvider is not null)
                return _engineProvider.RecognizeBgra(bgra, width, height, checked(width * 4),
                    _engineRasterOptions, characterWhitelist, cancellationToken);
            PdfOcrPreparedImage prepared = PdfOcrImagePreprocessor.PrepareBgra(
                bgra, width, height, FallbackRasterOptions, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return NativeFallback().Recognize(
                prepared, characterWhitelist, cancellationToken);
        }

        private TesseractOcrFallback NativeFallback() =>
            _fallback ??= new TesseractOcrFallback(
                _dataPath, _language, _usesDefaultDataPath);

        public void Dispose() => _fallback?.Dispose();
    }
}
