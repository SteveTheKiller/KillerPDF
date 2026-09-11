using System.IO;
using KillerPdf.Engine.Authoring;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class TextFieldPresetStoreTests
{
    [Fact]
    public void SaveAndLoad_PreservesValidatedPresetOrderAndStyle()
    {
        string root = Path.Combine(Path.GetTempPath(), $"killerpdf-presets-{Guid.NewGuid():N}");
        string file = Path.Combine(root, "presets.json");
        try
        {
            var store = new TextFieldPresetStore(root, file);
            var presets = new PdfTextFieldPresetCollection([
                new PdfTextFieldPreset("Answer", 180, 24, 12),
                new PdfTextFieldPreset("Notes", 240, 72, 11,
                    new PdfFormFieldAppearanceStyle { BorderWidth = 2 })
            ]);

            store.Save(presets);
            PdfTextFieldPresetCollection loaded = store.Load();

            Assert.Equal(["Answer", "Notes"], loaded.Presets.Select(preset => preset.Name));
            Assert.Equal(240, loaded.Presets[1].Width);
            Assert.Equal(2, loaded.Presets[1].AppearanceStyle.BorderWidth);
            Assert.DoesNotContain(".tmp", Directory.EnumerateFiles(root));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
