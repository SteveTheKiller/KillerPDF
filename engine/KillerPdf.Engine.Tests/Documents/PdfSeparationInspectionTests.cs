using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfSeparationInspectionTests
{
    [Fact]
    public void InspectReportsProcessAndSpotColorantsWithPageLocations()
    {
        var orange = new PdfSpotColor("Killer Orange", new PdfCmykColor(0, 0.72, 1, 0));
        byte[] source = new PdfDocumentBuilder()
            .AddPage(100, 100, new PdfContentStreamBuilder()
                .SetFillCmyk(1, 0, 0, 0).Rectangle(0, 0, 20, 20).Fill()
                .SetFillSpotColor(orange, 0.8).Rectangle(20, 20, 20, 20).Fill())
            .AddPage(100, 100, new PdfContentStreamBuilder()
                .SetStrokeSpotColor(orange, 0.4).Rectangle(10, 10, 30, 30).Stroke())
            .Build();

        PdfSeparationReport report = PdfSeparationInspection.Inspect(PdfDocument.Open(source));

        Assert.Equal(["Cyan", "Killer Orange"],
            report.Colorants.Select(colorant => colorant.Name));
        Assert.True(report.Colorants[0].IsProcess);
        Assert.Equal([0], report.Colorants[0].PageIndexes);
        PdfSeparationColorant spot = report.Colorants[^1];
        Assert.False(spot.IsProcess);
        Assert.Equal([0, 1], spot.PageIndexes);
    }
}
