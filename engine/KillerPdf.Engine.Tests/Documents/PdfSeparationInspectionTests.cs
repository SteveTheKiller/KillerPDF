using System.Text;
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

    [Fact]
    public void InspectIgnoresDeclaredButUnusedSpotColorants()
    {
        string content = "0 0 10 10 re f";
        string spot = "[/Separation /Unused /DeviceCMYK "
            + "<< /FunctionType 2 /Domain [0 1] /C0 [0 0 0 0] /C1 [0 1 1 0] /N 1 >>]";
        byte[] source = CreatePdf([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 100 100] "
                + $"/Resources << /ColorSpace << /Spot {spot} >> >> >>",
            "<< /Type /Page /Parent 2 0 R /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream"
        ]);

        PdfSeparationReport report = PdfSeparationInspection.Inspect(PdfDocument.Open(source));

        Assert.Empty(report.Colorants);
    }

    private static byte[] CreatePdf(IReadOnlyList<string> objects)
    {
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int index = 0; index < objects.Count; index++)
        {
            offsets.Add(Encoding.Latin1.GetByteCount(pdf.ToString()));
            pdf.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        int xref = Encoding.Latin1.GetByteCount(pdf.ToString());
        pdf.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) pdf.Append($"{offset:0000000000} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\n")
            .Append($"startxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(pdf.ToString());
    }
}
