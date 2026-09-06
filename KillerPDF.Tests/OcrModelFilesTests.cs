using System;
using System.IO;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class OcrModelFilesTests
{
    [Theory]
    [InlineData("eng.kpocr")]
    [InlineData("eng.traineddata")]
    public void EitherModelFormatInstallsTheLanguage(string fileName)
    {
        string directory = CreateDirectory();
        try
        {
            File.WriteAllBytes(Path.Combine(directory, fileName), []);

            Assert.True(OcrModelFiles.IsLanguageInstalled(directory, "eng"));
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
            File.WriteAllBytes(Path.Combine(directory, "eng.kpocr"), []);

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
            File.WriteAllBytes(Path.Combine(directory, "eng.kpocr"), []);
            File.WriteAllBytes(Path.Combine(directory, "spa.kpocr"), []);

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
            File.WriteAllBytes(Path.Combine(directory, "eng.kpocr"), []);
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
}
