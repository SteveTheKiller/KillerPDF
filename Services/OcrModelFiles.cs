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
            return PdfOcrRecognitionModelFiles.TryLoad(directory, code, out _);
        }

        internal static bool HasTesseractModel(string directory, string code) =>
            File.Exists(Path.Combine(directory, code + ".traineddata"));

        internal static string SelectProvider(string directory,
            IEnumerable<string> languages, PdfOcrProviderPreference? preference = null)
        {
            string[] requested = languages.Distinct(StringComparer.Ordinal).ToArray();
            var installed = new List<InstalledProvider>(2);
            if (requested.All(code => HasEngineModel(directory, code)))
                installed.Add(new InstalledProvider("engine", requested, 10));
            if (requested.All(code => HasTesseractModel(directory, code)))
                installed.Add(new InstalledProvider("tesseract", requested, 0));
            return PdfOcrProviderSelector.Select(installed,
                new PdfOcrOptions(requested), preference).Descriptor.Id;
        }

        internal static IReadOnlyList<string> MissingForCommonBackend(
            string directory, IEnumerable<string> languages)
        {
            string[] requested = languages.Distinct(StringComparer.Ordinal).ToArray();
            if (requested.All(code => HasEngineModel(directory, code))) return [];
            return requested.Where(code => !HasTesseractModel(directory, code)).ToArray();
        }

        private sealed class InstalledProvider : IPdfOcrProviderMetadata
        {
            internal InstalledProvider(string id, IEnumerable<string> languages,
                int automaticPriority)
            {
                Descriptor = new PdfOcrProviderDescriptor(id, id, new Version(1, 0),
                    languages, automaticPriority);
            }

            public PdfOcrProviderDescriptor Descriptor { get; }

            public bool Supports(PdfOcrOptions options) => true;
        }
    }
}
