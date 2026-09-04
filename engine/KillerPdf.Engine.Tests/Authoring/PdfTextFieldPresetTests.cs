using KillerPdf.Engine.Authoring;
using Xunit;

namespace KillerPdf.Engine.Tests.Authoring;

public sealed class PdfTextFieldPresetTests
{
    [Fact]
    public void PresetsRoundTripRenameReorderAndRemove()
    {
        var compact = new PdfTextFieldPreset("Compact", 120, 20, 10,
            new PdfFormFieldAppearanceStyle
            {
                BackgroundColor = new PdfRgbColor(0.9, 0.9, 0.9),
                BorderColor = new PdfRgbColor(0.2, 0.3, 0.4),
                TextColor = new PdfRgbColor(0.1, 0.2, 0.3),
                BorderStyle = PdfFormFieldBorderStyle.Dashed,
                BorderWidth = 2,
                DashPattern = [3, 2]
            });
        var collection = new PdfTextFieldPresetCollection([
            compact, new PdfTextFieldPreset("Wide", 240, 24, 12)]);

        PdfTextFieldPresetCollection restored =
            PdfTextFieldPresetCollection.FromJson(collection.ToJson());
        PdfTextFieldPresetCollection edited = restored.Rename("Wide", "Full width")
            .Move(1, 0).Remove("Compact");

        PdfTextFieldPreset preset = Assert.Single(edited.Presets);
        Assert.Equal("Full width", preset.Name);
        Assert.Equal(240, preset.Width);
        Assert.Equal(PdfFormFieldBorderStyle.Dashed,
            restored.Presets[0].AppearanceStyle.BorderStyle);
        Assert.Equal([3d, 2d], restored.Presets[0].AppearanceStyle.DashPattern);
    }

    [Fact]
    public void PresetsRejectInvalidGeometryAndDuplicateNames()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PdfTextFieldPreset("Bad", 0, 20, 10));
        Assert.Throws<ArgumentException>(() => new PdfTextFieldPresetCollection([
            new PdfTextFieldPreset("Name", 100, 20, 10),
            new PdfTextFieldPreset("name", 120, 20, 10)]));
    }
}
