using KillerPdf.Engine.Documents;
using System.IO;
using Tesseract;

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
        private TesseractEngine? _engine;

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
            TesseractEngine engine = NativeEngine();
            if (!string.IsNullOrEmpty(characterWhitelist))
                engine.SetVariable("tessedit_char_whitelist", characterWhitelist);
            try
            {
                PdfOcrPreparedImage prepared = PdfOcrImagePreprocessor.PrepareBgra(
                    bgra, width, height, RasterOptions, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                using Pix pix = CreateGrayscalePix(prepared, cancellationToken);
                return Run(pix);
            }
            finally
            {
                if (!string.IsNullOrEmpty(characterWhitelist))
                    engine.SetVariable("tessedit_char_whitelist", string.Empty);
            }
        }

        private PdfOcrResult Run(Pix pix)
        {
            using var page = NativeEngine().Process(pix);
            string text = page.GetText() ?? "";
            float confidence = page.GetMeanConfidence();
            var words = new List<PdfOcrPixelWord>();

            using var iter = page.GetIterator();
            iter.Begin();
            do
            {
                if (iter.TryGetBoundingBox(PageIteratorLevel.Word, out var r))
                {
                    string w = iter.GetText(PageIteratorLevel.Word) ?? "";
                    if (!string.IsNullOrWhiteSpace(w))
                    {
                        words.Add(new PdfOcrPixelWord(w,
                            iter.GetConfidence(PageIteratorLevel.Word),
                            r.X1, r.Y1, r.X2, r.Y2));
                    }
                }
            }
            while (iter.Next(PageIteratorLevel.Word));

            return new PdfOcrResult(text, confidence, words);
        }

        private static unsafe Pix CreateGrayscalePix(
            PdfOcrPreparedImage image, CancellationToken cancellationToken)
        {
            Pix pix = Pix.Create(image.Width, image.Height, 8);
            PixData data = pix.GetData();
            uint* start = (uint*)data.Data.ToPointer();
            ReadOnlySpan<byte> pixels = image.Pixels.Span;
            for (int y = 0; y < image.Height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                uint* row = start + y * data.WordsPerLine;
                int source = y * image.Width;
                for (int x = 0; x < image.Width; x++)
                    PixData.SetDataByte(row, x, pixels[source + x]);
            }
            return pix;
        }

        private TesseractEngine NativeEngine()
        {
            if (_engine is not null) return _engine;
            if (_usesDefaultDataPath) OcrNativeBootstrap.EnsureReady();
            return _engine = new TesseractEngine(_dataPath, _language, EngineMode.Default);
        }

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

        public void Dispose() => _engine?.Dispose();
    }
}
