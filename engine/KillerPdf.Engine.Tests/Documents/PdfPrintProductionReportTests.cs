using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfPrintProductionReportTests
{
    [Fact]
    public void InspectCombinesBoxesSeparationsAndDataSafeOutputIntentDetails()
    {
        var orange = new PdfSpotColor("Killer Orange", new PdfCmykColor(0, 0.72, 1, 0));
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .SetOutputIntent(PdfIccProfile.Load(Profile("CMYK")), "FOGRA39",
                "ISO Coated v2", "http://www.color.org", "Press proofing")
            .AddPage(612, 792, new PdfContentStreamBuilder()
                .SetFillSpotColor(orange, 0.8).Rectangle(20, 20, 20, 20).Fill())
            .Build());

        PdfPrintProductionReport report = PdfPrintProductionReport.Inspect(document);
        using JsonDocument json = JsonDocument.Parse(report.ToJson());

        Assert.Single(report.Pages);
        Assert.Equal(new PdfPageBoxBounds(0, 0, 612, 792), report.Pages[0].MediaBox);
        Assert.Equal("Killer Orange", Assert.Single(report.Colorants).Name);
        Assert.Equal("FOGRA39", Assert.Single(report.OutputIntents)
            .OutputConditionIdentifier);
        JsonElement profile = json.RootElement.GetProperty("outputIntents")[0]
            .GetProperty("profile");
        Assert.Equal("CMYK", profile.GetProperty("colorSpace").GetString());
        Assert.Equal(4, profile.GetProperty("componentCount").GetInt32());
        Assert.Equal(132, profile.GetProperty("byteCount").GetInt32());
        Assert.False(profile.TryGetProperty("data", out _));
        string text = report.ToText();
        Assert.Contains("Print production: pages 1, colorants 1, output intents 1", text,
            StringComparison.Ordinal);
        Assert.Contains("MediaBox: 0, 0 to 612, 792 pt (612 x 792, effective)", text,
            StringComparison.Ordinal);
        Assert.Contains("Spot colorant Killer Orange: pages 1", text,
            StringComparison.Ordinal);
        Assert.Contains("Output intent FOGRA39: GTS_PDFA1, CMYK, 4 components, 132 profile bytes",
            text, StringComparison.Ordinal);
    }

    [Fact]
    public void SeparationPreviewSelectsKnownPlatesPerPage()
    {
        var orange = new PdfSpotColor("Killer Orange", new PdfCmykColor(0, 0.72, 1, 0));
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(300, 400, new PdfContentStreamBuilder()
                .SetFillCmyk(0.1, 0.2, 0.3, 0.4).Rectangle(10, 10, 20, 20).Fill())
            .AddPage(300, 400, new PdfContentStreamBuilder()
                .SetFillSpotColor(orange, 0.8).Rectangle(20, 20, 20, 20).Fill())
            .Build());

        PdfSeparationPreview preview = PdfSeparationPreview.Create(
            document, ["Black", "Killer Orange"]);

        Assert.Equal(["Black", "Killer Orange"],
            preview.Plates.Select(plate => plate.Name));
        Assert.True(preview.Plates[0].IsProcess);
        Assert.False(preview.Plates[1].IsProcess);
        Assert.Equal(["Black"], preview.Pages[0].PlateNames);
        Assert.Equal(["Killer Orange"], preview.Pages[1].PlateNames);
        using JsonDocument json = JsonDocument.Parse(preview.ToJson());
        Assert.Equal(1, json.RootElement.GetProperty("version").GetInt32());
        Assert.Equal("Killer Orange", json.RootElement.GetProperty("plates")[1]
            .GetProperty("name").GetString());
        Assert.Equal("Black", json.RootElement.GetProperty("pages")[0]
            .GetProperty("plateNames")[0].GetString());
        string text = preview.ToText();
        Assert.Contains("Separation preview: plates 2, pages 2", text,
            StringComparison.Ordinal);
        Assert.Contains("Process plate Black: pages 1", text, StringComparison.Ordinal);
        Assert.Contains("Spot plate Killer Orange: pages 2", text, StringComparison.Ordinal);
        Assert.Contains("Page 1: Black", text, StringComparison.Ordinal);
        Assert.Contains("Page 2: Killer Orange", text, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() =>
            PdfSeparationPreview.Create(document, ["Killer Orange", "Killer Orange"]));
        Assert.Throws<ArgumentException>(() =>
            PdfSeparationPreview.Create(document, ["Missing plate"]));

        PdfSeparationPreview selected = PdfSeparationPreview.Create(
            document, ["Black", "Killer Orange"], [1]);
        Assert.Equal([1], selected.Pages.Select(page => page.PageIndex));
        Assert.Empty(selected.Plates[0].PageIndexes);
        Assert.Equal([1], selected.Plates[1].PageIndexes);
        Assert.Equal(["Killer Orange"], selected.Pages[0].PlateNames);
        Assert.Contains("Process plate Black: pages", selected.ToText(), StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() =>
            PdfSeparationPreview.Create(document, ["Black"], []));
        Assert.Throws<ArgumentException>(() =>
            PdfSeparationPreview.Create(document, ["Black"], [0, 0]));
        Assert.Throws<ArgumentException>(() =>
            PdfSeparationPreview.Create(document, ["Black"], [2]));
    }

    private static byte[] Profile(string colorSpace)
    {
        byte[] result = new byte[132];
        BinaryPrimitives.WriteUInt32BigEndian(result, (uint)result.Length);
        Encoding.ASCII.GetBytes(colorSpace).CopyTo(result, 16);
        "acsp"u8.CopyTo(result.AsSpan(36, 4));
        return result;
    }
}
