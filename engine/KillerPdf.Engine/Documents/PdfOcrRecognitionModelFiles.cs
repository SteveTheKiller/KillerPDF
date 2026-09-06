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
        if (!TryLanguages(directory, languages, out string[] requested)) return false;
        try
        {
            var loaded = new List<PdfOcrRecognitionModel>(requested.Length);
            foreach (string language in requested)
            {
                string path = Path.Combine(directory, language + ".kpocr");
                if (!TryReadBounded(path, PdfOcrRecognitionModel.MaximumModelBytes,
                    out byte[] bytes))
                    return false;
                loaded.Add(PdfOcrRecognitionModel.Load(bytes));
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

    /// <summary>Attempts to load and combine every installed context model requested.</summary>
    public static bool TryLoadLanguageCombined(string directory, string languages,
        out PdfOcrLanguageModel? model)
    {
        model = null;
        if (!TryLanguages(directory, languages, out string[] requested)) return false;
        try
        {
            var loaded = new List<PdfOcrLanguageModel>(requested.Length);
            foreach (string language in requested)
            {
                string path = Path.Combine(directory, language + ".kplm");
                if (!TryReadBounded(path, PdfOcrLanguageModel.MaximumModelBytes,
                    out byte[] bytes))
                    return false;
                loaded.Add(PdfOcrLanguageModel.Load(bytes));
            }
            model = loaded.Count == 1 ? loaded[0] : PdfOcrLanguageModel.Combine(loaded);
            return true;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            model = null;
            return false;
        }
    }

    private static bool TryReadBounded(string path, int maximumBytes, out byte[] bytes)
    {
        bytes = [];
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is <= 0 || input.Length > maximumBytes)
            return false;
        bytes = new byte[checked((int)input.Length)];
        input.ReadExactly(bytes);
        return input.Position == input.Length;
    }

    private static bool TryLanguages(string directory, string languages,
        out string[] requested)
    {
        requested = [];
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(languages))
            return false;
        requested = [.. languages.Split('+')
            .Select(language => language.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)];
        return requested.Length is >= 1 and <= 16 && !requested.Any(language =>
            language.Length is < 1 or > 35 || language.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-'));
    }
}
