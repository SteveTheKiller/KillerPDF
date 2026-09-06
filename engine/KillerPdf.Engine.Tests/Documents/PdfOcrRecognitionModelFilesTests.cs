using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfOcrRecognitionModelFilesTests
{
    [Fact]
    public void TryLoadCombinedRequiresEveryValidRequestedModel()
    {
        string directory = CreateDirectory();
        try
        {
            File.WriteAllBytes(Path.Combine(directory, "eng.kpocr"), TinyModel("E").Save());
            File.WriteAllBytes(Path.Combine(directory, "spa.kpocr"), TinyModel("S").Save());

            Assert.True(PdfOcrRecognitionModelFiles.TryLoadCombined(
                directory, "eng+spa", out PdfOcrRecognitionModel? combined));
            Assert.Equal(["E", "S"], combined!.Labels);
            Assert.False(PdfOcrRecognitionModelFiles.TryLoadCombined(
                directory, "eng+fra", out _));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void TryLoadRejectsCorruptAndUnsafeLanguageNames()
    {
        string directory = CreateDirectory();
        try
        {
            File.WriteAllBytes(Path.Combine(directory, "eng.kpocr"), [1, 2, 3]);

            Assert.False(PdfOcrRecognitionModelFiles.TryLoad(directory, "eng", out _));
            Assert.False(PdfOcrRecognitionModelFiles.TryLoad(directory, "../eng", out _));
            Assert.False(PdfOcrRecognitionModelFiles.TryLoadCombined(
                directory, "eng+", out _));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void TryLoadLanguageCombinedRequiresEveryContextModel()
    {
        string directory = CreateDirectory();
        try
        {
            File.WriteAllBytes(Path.Combine(directory, "eng.kplm"),
                PdfOcrLanguageModel.Train(["GOOD"]).Save());
            File.WriteAllBytes(Path.Combine(directory, "spa.kplm"),
                PdfOcrLanguageModel.Train(["BUENO"]).Save());

            Assert.True(PdfOcrRecognitionModelFiles.TryLoadLanguageCombined(
                directory, "eng+spa", out PdfOcrLanguageModel? combined));
            Assert.Equal(combined!.Save(), PdfOcrLanguageModel.Load(combined.Save()).Save());
            Assert.False(PdfOcrRecognitionModelFiles.TryLoadLanguageCombined(
                directory, "eng+fra", out _));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void TryLoadLanguageRejectsOversizedFilesBeforeAllocatingTheirContents()
    {
        string directory = CreateDirectory();
        try
        {
            string path = Path.Combine(directory, "oversized.kplm");
            using (var output = new FileStream(path, FileMode.CreateNew,
                FileAccess.Write, FileShare.None))
                output.SetLength(PdfOcrLanguageModel.MaximumModelBytes + 1L);
            long before = GC.GetAllocatedBytesForCurrentThread();

            Assert.False(PdfOcrRecognitionModelFiles.TryLoadLanguageCombined(
                directory, "oversized", out _));

            Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 1_000_000);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static PdfOcrRecognitionModel TinyModel(string label) =>
        PdfOcrModelTrainer.Train(1, 1,
            [new PdfOcrTrainingSample(label, new float[] { 1 })]);

    private static string CreateDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(),
            "KillerPdf.Engine.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
