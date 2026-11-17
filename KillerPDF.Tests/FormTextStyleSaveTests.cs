using System.IO;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class FormTextStyleSaveTests
{
    [Fact]
    public void FormTextStyleRoundTripsFontBoldItalicColorAndDecorations()
    {
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-form-style-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, new PdfDocumentBuilder().AddBlankPage()
                .AddTextField(0, "customer", 20, 20, 160, 24, "Hello", 12)
                .Build());
            var style = new FormTextStyle("Arial", Bold: true, Italic: true, TextColor: "#FF0000",
                Underline: true, StrikeThrough: false);
            PdfEngineIntegration.ApplyFormValues(path, new PdfEngineIntegration.FormEdits(
                new Dictionary<string, string>(), new Dictionary<string, string>(),
                new Dictionary<string, IReadOnlyList<string>>(), new Dictionary<string, bool>(),
                new Dictionary<string, string>(), new Dictionary<string, double>(),
                new Dictionary<string, FormTextStyle> { ["customer"] = style }));

            PdfFormWidgetInfo field = Assert.Single(
                PdfFormWidgetReader.ReadPage(PdfDocument.Open(File.ReadAllBytes(path)), 0));
            Assert.Equal("Hello", field.Value);
            Assert.Contains("1 0 0 rg", field.DefaultAppearance);
            Assert.True(field.Underline);
            Assert.False(field.StrikeThrough);
            // Arial is on every Windows install; the embedded face names its weight and slant.
            Assert.NotEqual(string.Empty, field.AppearanceFontName);
            DetectedPdfFontStyle detected = PdfFontStyle.FromPdfName(field.AppearanceFontName);
            Assert.Equal("Arial", detected.Family, ignoreCase: true);
            Assert.True(detected.Bold);
            Assert.True(detected.Italic);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void UnreadableInstalledFaceFallsBackInsteadOfThrowing()
    {
        Assert.Null(PdfEngineIntegration.TryLoadFormFace([0, 1, 2, 3, 4, 5, 6, 7]));
        Assert.Null(PdfEngineIntegration.TryLoadFormFace([]));
    }
}
