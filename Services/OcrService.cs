using KillerPdf.Engine.Documents;
using Tesseract;

namespace KillerPDF.Services
{
    /// <summary>
    /// Local Tesseract OCR. The tessdata folder (with at least eng.traineddata) must sit next to the
    /// exe; the native engine loads language data by path. A TesseractEngine is NOT thread-safe, so run
    /// OCR off the UI thread and create a fresh OcrService per operation (or serialize calls). Dispose when done.
    /// </summary>
    internal sealed class OcrService : IDisposable
    {
        private static readonly PdfOcrOptions RasterOptions = new(["und"],
            deskew: false, correctOrientation: false, detectPageSegments: false);
        private readonly TesseractEngine _engine;

        /// <param name="tessDataPath">Folder holding *.traineddata. Defaults to the self-extracted cache (OcrNativeBootstrap).</param>
        /// <param name="language">Tesseract language code(s), e.g. "eng" or "eng+ben".</param>
        public OcrService(string? tessDataPath = null, string language = "eng")
        {
            // EnsureReady() extracts the embedded natives + language data and configures the native
            // loader, so it must run before the engine is constructed.
            string dataPath = tessDataPath ?? OcrNativeBootstrap.EnsureReady();
            _engine = new TesseractEngine(dataPath, language, EngineMode.Default);
        }

        /// <summary>OCR an image file on disk (PNG, TIFF, JPEG, BMP).</summary>
        public PdfOcrResult RecognizeImageFile(string imagePath)
        {
            using var pix = Pix.LoadFromFile(imagePath);
            return Run(pix);
        }

        /// <summary>OCR an encoded image already in memory (e.g. PNG bytes).</summary>
        public PdfOcrResult RecognizeImageBytes(byte[] encodedImage)
        {
            using var pix = Pix.LoadFromMemory(encodedImage);
            return Run(pix);
        }

        /// <summary>
        /// OCR a rendered page straight from the render pipeline (raw BGRA, 4 bytes/pixel).
        /// </summary>
        public PdfOcrResult RecognizeBgra(ReadOnlyMemory<byte> bgra, int width, int height,
            string? characterWhitelist = null)
        {
            if (!string.IsNullOrEmpty(characterWhitelist))
                _engine.SetVariable("tessedit_char_whitelist", characterWhitelist);
            try
            {
                PdfOcrPreparedImage prepared = PdfOcrImagePreprocessor.PrepareBgra(
                    bgra, width, height, RasterOptions);
                using Pix pix = CreateGrayscalePix(prepared);
                return Run(pix);
            }
            finally
            {
                if (!string.IsNullOrEmpty(characterWhitelist))
                    _engine.SetVariable("tessedit_char_whitelist", string.Empty);
            }
        }

        private PdfOcrResult Run(Pix pix)
        {
            using var page = _engine.Process(pix);
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

        private static unsafe Pix CreateGrayscalePix(PdfOcrPreparedImage image)
        {
            Pix pix = Pix.Create(image.Width, image.Height, 8);
            PixData data = pix.GetData();
            uint* start = (uint*)data.Data.ToPointer();
            ReadOnlySpan<byte> pixels = image.Pixels.Span;
            for (int y = 0; y < image.Height; y++)
            {
                uint* row = start + y * data.WordsPerLine;
                int source = y * image.Width;
                for (int x = 0; x < image.Width; x++)
                    PixData.SetDataByte(row, x, pixels[source + x]);
            }
            return pix;
        }

        public void Dispose() => _engine.Dispose();
    }
}
