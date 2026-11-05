using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using KillerPDF.Services;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPDF.Tests;

public sealed class ReplacementTextBlurTests
{
    [Fact]
    public void OlderAnnotationSettingsKeepTheSharpDefault() =>
        Assert.Equal(SaveAndRender(0), SaveAndRender(null));

    [Fact]
    public void APositiveSubpixelBlurDoesNotEraseSavedReplacementText()
    {
        double originalInk = Ink(SaveAndRender(0)).Mass;
        Assert.InRange(Ink(SaveAndRender(double.Epsilon)).Mass / originalInk, .95, 1.05);
    }

    [Fact]
    public void SavedBlurSoftensOnlyReplacementTextAndKeepsItSearchable()
    {
        byte[] sharp = SaveAndRender(0);
        byte[] blurred = SaveAndRender(1);
        var before = Ink(sharp);
        var after = Ink(blurred);
        Assert.True(before.Mass > 10000, "The sharp replacement must contain visible ink.");
        Assert.InRange(after.Mass / before.Mass, .95, 1.05);
        Assert.InRange(Math.Abs(after.X - before.X), 0, 1);
        Assert.InRange(Math.Abs(after.Y - before.Y), 0, 1);
        Assert.True(EdgeEnergy(blurred) < EdgeEnergy(sharp) * .85,
            "The saved replacement should have softer edges than sharp text.");
        for (int y = 0; y < 800; y++)
            for (int x = 0; x < 800; x++)
                if (x < 120 || x >= 640 || y < 120 || y >= 360)
                    Assert.Equal(sharp.AsSpan((y * 800 + x) * 4, 4).ToArray(),
                        blurred.AsSpan((y * 800 + x) * 4, 4).ToArray());
    }

    private static (double Mass, double X, double Y) Ink(byte[] pixels)
    {
        double mass = 0, horizontal = 0, vertical = 0;
        for (int y = 120; y < 360; y++)
            for (int x = 120; x < 640; x++)
            {
                int ink = 255 - pixels[(y * 800 + x) * 4];
                mass += ink;
                horizontal += x * ink;
                vertical += y * ink;
            }
        return (mass, horizontal / mass, vertical / mass);
    }

    private static byte[] SaveAndRender(double? blur)
    {
        string path = Path.Combine(Path.GetTempPath(), $"kp-replacement-blur-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, new PdfDocumentBuilder().AddBlankPage(200, 200).Build());
            TextAnnotation Text(string value, double top, string pair)
            {
                var text = new TextAnnotation { Content = value, Position = new Point(40, top),
                    FontName = "Arial", FontSize = 16, Width = 110, Height = 30, PairId = pair };
                JsonObject settings = JsonSerializer.SerializeToNode(text)!.AsObject();
                if (blur.HasValue) settings["BlurRadius"] = blur.Value;
                else settings.Remove("BlurRadius");
                return settings.Deserialize<TextAnnotation>()!;
            }
            PdfEngineBurn.Burn(path, new Dictionary<int, List<PageAnnotation>>
            {
                [0] = [new CoverAnnotation { Bounds = new Rect(38, 38, 115, 35), PairId = "edit" },
                    Text("Receipt", 40, "edit"), Text("Plain", 130, "")]
            }, new Dictionary<int, (int w, int h)> { [0] = (200, 200) });
            PdfDocument document = PdfDocument.Open(File.ReadAllBytes(path));
            string[] words = new PdfPageContentReader(document).Read(0).Words.Select(word => word.Text).ToArray();
            Assert.Single(words, word => word == "Receipt");
            Assert.Equal(1, SearchService.Search(path, "Receipt").TotalHits);
            Assert.Equal(1, SearchService.Search(path, "Plain").TotalHits);
            return new PdfPageRenderer(document).Render(0, new PdfRenderOptions(800, 800)).Pixels.ToArray();
        }
        finally { File.Delete(path); }
    }

    private static long EdgeEnergy(byte[] pixels)
    {
        long sum = 0;
        for (int y = 120; y < 360; y++)
            for (int x = 120; x < 640; x++)
            {
                int offset = (y * 800 + x) * 4;
                int dx = pixels[offset] - pixels[offset + 4];
                int dy = pixels[offset] - pixels[offset + 800 * 4];
                sum += dx * dx + dy * dy;
            }
        return sum;
    }
}
