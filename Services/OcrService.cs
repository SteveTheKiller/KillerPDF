using KillerPdf.Engine.Documents;
namespace KillerPDF.Services
{
    /// <summary>
    /// Uses an installed engine recognition model when one is available, with Tesseract retained as
    /// the migration fallback. Instances are not thread-safe, so each operation owns its service.
    /// </summary>
    internal sealed class OcrService : IDisposable
    {
        private static readonly PdfOcrOptions EngineRasterOptions = new(["und"],
            deskew: false, correctOrientation: false, removeBackground: true,
            removeNoise: true, detectPageSegments: true);
        private static readonly PdfOcrOptions FallbackRasterOptions = new(["und"],
            deskew: false, correctOrientation: false, detectPageSegments: false);
        private readonly string _dataPath;
        private readonly string _language;
        private readonly bool _usesDefaultDataPath;
        private readonly PdfOcrRecognitionModel? _engineModel;
        private readonly PdfOcrLanguageModel? _engineLanguageModel;
        private TesseractOcrFallback? _fallback;

        /// <param name="tessDataPath">Folder holding installed OCR models. Defaults to the self-extracted cache (OcrNativeBootstrap).</param>
        /// <param name="language">Tesseract language code(s), e.g. "eng" or "eng+ben".</param>
        public OcrService(string? tessDataPath = null, string language = "eng")
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(language);
            _usesDefaultDataPath = tessDataPath is null;
            _dataPath = tessDataPath ?? OcrNativeBootstrap.EnsureLanguageData();
            _language = language;
            PdfOcrRecognitionModelFiles.TryLoadCombined(
                _dataPath, language, out _engineModel);
            if (_engineModel is not null)
                PdfOcrRecognitionModelFiles.TryLoadLanguageCombined(
                    _dataPath, language, out _engineLanguageModel);
        }

        /// <summary>
        /// OCR a rendered page straight from the render pipeline (raw BGRA, 4 bytes/pixel).
        /// </summary>
        public PdfOcrResult RecognizeBgra(ReadOnlyMemory<byte> bgra, int width, int height,
            string? characterWhitelist = null,
            CancellationToken cancellationToken = default)
        {
            if (_engineModel is not null)
                return string.IsNullOrEmpty(characterWhitelist)
                    ? _engineLanguageModel is null
                        ? PdfOcrRecognizer.RecognizeBgra(
                            bgra, width, height, _engineModel,
                            EngineRasterOptions, cancellationToken)
                        : PdfOcrRecognizer.RecognizeBgra(
                            bgra, width, height, _engineModel,
                            _engineLanguageModel, EngineRasterOptions, cancellationToken)
                    : PdfOcrRecognizer.RecognizeBgra(
                        bgra, width, height, _engineModel, EngineRasterOptions,
                        characterWhitelist, cancellationToken);
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
