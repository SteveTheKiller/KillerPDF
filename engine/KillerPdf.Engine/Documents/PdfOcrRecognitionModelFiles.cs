namespace KillerPdf.Engine.Documents;

/// <summary>Loads installed engine OCR models from a bounded language expression.</summary>
public static class PdfOcrRecognitionModelFiles
{
    /// <summary>Attempts to load one valid installed language model.</summary>
    public static bool TryLoad(string directory, string language,
        out PdfOcrRecognitionModel? model) =>
        TryLoadCombined(directory, language, out model);

    /// <summary>Attempts to load and combine every model in a plus-separated language expression.</summary>
    public static bool TryLoadCombined(string directory, string languages,
        out PdfOcrRecognitionModel? model)
    {
        model = null;
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(languages))
            return false;
        string[] requested = languages.Split('+');
        if (requested.Length is < 1 or > 16 || requested.Any(language =>
            language.Length is < 1 or > 35 || language.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-')))
            return false;
        try
        {
            var loaded = new List<PdfOcrRecognitionModel>(requested.Length);
            foreach (string language in requested)
            {
                string path = Path.Combine(directory, language + ".kpocr");
                if (!File.Exists(path)) return false;
                loaded.Add(PdfOcrRecognitionModel.Load(File.ReadAllBytes(path)));
            }
            model = loaded.Count == 1 ? loaded[0] : PdfOcrRecognitionModel.Combine(loaded);
            return true;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            model = null;
            return false;
        }
    }
}
