using KillerPdf.Engine.Documents;
using Tesseract;

namespace KillerPDF.Services
{
    internal sealed class TesseractOcrFallback : IDisposable
    {
        private readonly string _dataPath;
        private readonly string _language;
        private readonly bool _usesDefaultDataPath;
        private TesseractEngine? _engine;

        internal TesseractOcrFallback(
            string dataPath, string language, bool usesDefaultDataPath)
        {
            _dataPath = dataPath;
            _language = language;
            _usesDefaultDataPath = usesDefaultDataPath;
        }

        internal PdfOcrResult Recognize(PdfOcrPreparedImage image,
            string? characterWhitelist, CancellationToken cancellationToken)
        {
            TesseractEngine engine = NativeEngine();
            if (!string.IsNullOrEmpty(characterWhitelist))
                engine.SetVariable("tessedit_char_whitelist", characterWhitelist);
            try
            {
                using Pix pix = CreateGrayscalePix(image, cancellationToken);
                using var page = engine.Process(pix);
                string text = page.GetText() ?? "";
                float confidence = page.GetMeanConfidence();
                var words = new List<PdfOcrPixelWord>();

                using var iter = page.GetIterator();
                iter.Begin();
                do
                {
                    if (iter.TryGetBoundingBox(PageIteratorLevel.Word, out var bounds))
                    {
                        string word = iter.GetText(PageIteratorLevel.Word) ?? "";
                        if (!string.IsNullOrWhiteSpace(word))
                        {
                            words.Add(new PdfOcrPixelWord(word,
                                iter.GetConfidence(PageIteratorLevel.Word),
                                bounds.X1, bounds.Y1, bounds.X2, bounds.Y2));
                        }
                    }
                }
                while (iter.Next(PageIteratorLevel.Word));

                return new PdfOcrResult(text, confidence, words);
            }
            finally
            {
                if (!string.IsNullOrEmpty(characterWhitelist))
                    engine.SetVariable("tessedit_char_whitelist", string.Empty);
            }
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

        public void Dispose() => _engine?.Dispose();
    }
}
