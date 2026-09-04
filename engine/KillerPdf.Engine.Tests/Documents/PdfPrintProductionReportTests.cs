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
