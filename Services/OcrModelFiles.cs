using System.IO;
using KillerPdf.Engine.Documents;

namespace KillerPDF.Services
{
    internal static class OcrModelFiles
    {
        internal static bool IsLanguageInstalled(string directory, string code) =>
            HasEngineModel(directory, code) || HasTesseractModel(directory, code);

        internal static bool HasEngineModel(string directory, string code)
        {
            string path = Path.Combine(directory, code + ".kpocr");
            if (!File.Exists(path)) return false;
            try
            {
                PdfOcrRecognitionModel.Load(File.ReadAllBytes(path));
                return true;
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                return false;
            }
        }

        internal static bool HasTesseractModel(string directory, string code) =>
            File.Exists(Path.Combine(directory, code + ".traineddata"));

        internal static IReadOnlyList<string> MissingForCommonBackend(
            string directory, IEnumerable<string> languages)
        {
            string[] requested = languages.Distinct(StringComparer.Ordinal).ToArray();
            if (requested.All(code => HasEngineModel(directory, code))) return [];
            return requested.Where(code => !HasTesseractModel(directory, code)).ToArray();
        }
    }
}
