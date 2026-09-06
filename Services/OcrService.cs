using KillerPdf.Engine.Documents;
using System.IO;

namespace KillerPDF.Services
{
    /// <summary>
    /// Uses an installed engine recognition model when one is available, with Tesseract retained as
    /// the migration fallback. Instances are not thread-safe, so each operation owns its service.
    /// </summary>
    internal sealed class OcrService : IDisposable
    {
        private static readonly PdfOcrOptions RasterOptions = new(["und"],
            deskew: false, correctOrientation: false, detectPageSegments: false);
        private readonly string _dataPath;
        private readonly string _language;
        private readonly bool _usesDefaultDataPath;
        private readonly PdfOcrRecognitionModel? _engineModel;
        private TesseractOcrFallback? _fallback;

        /// <param name="tessDataPath">Folder holding installed OCR models. Defaults to the self-extracted cache (OcrNativeBootstrap).</param>
        /// <param name="language">Tesseract language code(s), e.g. "eng" or "eng+ben".</param>
        public OcrService(string? tessDataPath = null, string language = "eng")
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(language);
            _usesDefaultDataPath = tessDataPath is null;
            _dataPath = tessDataPath ?? OcrNativeBootstrap.EnsureLanguageData();
            _language = language;
            _engineModel = LoadEngineModel(_dataPath, language);
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
                    ? PdfOcrRecognizer.RecognizeBgra(
                        bgra, width, height, _engineModel, RasterOptions, cancellationToken)
                    : PdfOcrRecognizer.RecognizeBgra(
                        bgra, width, height, _engineModel, RasterOptions,
                        characterWhitelist, cancellationToken);
            PdfOcrPreparedImage prepared = PdfOcrImagePreprocessor.PrepareBgra(
                bgra, width, height, RasterOptions, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return NativeFallback().Recognize(
                prepared, characterWhitelist, cancellationToken);
        }

        private TesseractOcrFallback NativeFallback() =>
            _fallback ??= new TesseractOcrFallback(
                _dataPath, _language, _usesDefaultDataPath);

        private static PdfOcrRecognitionModel? LoadEngineModel(
            string dataPath, string language)
        {
            string[] languages = language.Split('+');
            if (languages.Length is < 1 or > 16 || languages.Any(item =>
                item.Length is < 1 or > 35
                || item.Any(character => !char.IsAsciiLetterOrDigit(character)
                    && character is not '_' and not '-')))
                return null;
            var models = new List<PdfOcrRecognitionModel>(languages.Length);
            foreach (string item in languages)
            {
                string modelPath = Path.Combine(dataPath, item + ".kpocr");
                if (!File.Exists(modelPath)) return null;
                models.Add(PdfOcrRecognitionModel.Load(File.ReadAllBytes(modelPath)));
            }
            return models.Count == 1 ? models[0] : PdfOcrRecognitionModel.Combine(models);
        }

        public void Dispose() => _fallback?.Dispose();
    }
}
