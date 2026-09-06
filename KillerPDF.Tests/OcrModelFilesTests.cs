using System;
using System.IO;
using KillerPdf.Engine.Documents;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class OcrModelFilesTests
{
    [Fact]
    public void ValidEngineModelInstallsTheLanguage()
    {
        string directory = CreateDirectory();
        try
        {
            WriteEngineModel(directory, "eng");

            Assert.True(OcrModelFiles.IsLanguageInstalled(directory, "eng"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void TesseractModelInstallsTheLanguage()
    {
        string directory = CreateDirectory();
        try
        {
            File.WriteAllBytes(Path.Combine(directory, "eng.traineddata"), []);

            Assert.True(OcrModelFiles.IsLanguageInstalled(directory, "eng"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CorruptEngineModelDoesNotInstallTheLanguage()
    {
        string directory = CreateDirectory();
        try
        {
            File.WriteAllBytes(Path.Combine(directory, "eng.kpocr"), [1, 2, 3]);

            Assert.False(OcrModelFiles.IsLanguageInstalled(directory, "eng"));
            Assert.Equal(["eng"],
                OcrModelFiles.MissingForCommonBackend(directory, ["eng"]));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void UnrelatedFilesDoNotInstallTheLanguage()
    {
        string directory = CreateDirectory();
        try
        {
            File.WriteAllBytes(Path.Combine(directory, "spa.kpocr"), []);
            File.WriteAllBytes(Path.Combine(directory, "eng.kpocr.part"), []);

            Assert.False(OcrModelFiles.IsLanguageInstalled(directory, "eng"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void TesseractUpgradeCheckExcludesEngineOnlyModels()
    {
        string directory = CreateDirectory();
        try
        {
            WriteEngineModel(directory, "eng");

            Assert.True(OcrModelFiles.HasEngineModel(directory, "eng"));
            Assert.False(OcrModelFiles.HasTesseractModel(directory, "eng"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CompleteEngineSetNeedsNoTesseractModels()
    {
        string directory = CreateDirectory();
        try
        {
            WriteEngineModel(directory, "eng");
            WriteEngineModel(directory, "spa");

            Assert.Empty(OcrModelFiles.MissingForCommonBackend(directory, ["eng", "spa"]));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void MixedSetRequiresACompleteTesseractBackend()
    {
        string directory = CreateDirectory();
        try
        {
            WriteEngineModel(directory, "eng");
            File.WriteAllBytes(Path.Combine(directory, "spa.traineddata"), []);

            Assert.Equal(["eng"],
                OcrModelFiles.MissingForCommonBackend(directory, ["eng", "spa"]));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CompleteTesseractSetNeedsNoEngineModels()
    {
        string directory = CreateDirectory();
        try
        {
            File.WriteAllBytes(Path.Combine(directory, "eng.traineddata"), []);
            File.WriteAllBytes(Path.Combine(directory, "spa.traineddata"), []);

            Assert.Empty(OcrModelFiles.MissingForCommonBackend(directory, ["eng", "spa"]));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "KillerPDF.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void WriteEngineModel(string directory, string code)
    {
        PdfOcrRecognitionModel model = PdfOcrModelTrainer.Train(1, 1,
        [
            new PdfOcrTrainingSample("A", new float[] { 1f }),
            new PdfOcrTrainingSample("B", new float[] { 0f })
        ]);
        File.WriteAllBytes(Path.Combine(directory, code + ".kpocr"), model.Save());
    }
}
