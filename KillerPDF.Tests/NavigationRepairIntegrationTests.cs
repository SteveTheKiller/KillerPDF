using System.Collections.Generic;
using System.IO;
using System.Text;
using KillerPDF.Services;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Parsing;
using Xunit;

namespace KillerPDF.Tests;

public sealed class NavigationRepairIntegrationTests
{
    [Fact]
    public void RepairNavigationLinks_RemovesSelectedUnsafeAndUnresolvedLinks()
    {
        string path = Path.Combine(Path.GetTempPath(),
            $"killerpdf-navigation-{System.Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, DocumentWithUnsafeAndUnresolvedLinks());
            Assert.Equal(2, PdfEngineIntegration.InspectNavigation(path).Count);

            PdfEngineIntegration.RepairNavigationLinks(
                path, removeUnsafeLinks: true, removeUnresolvedLinks: false);
            PdfNavigationFinding remaining = Assert.Single(
                PdfEngineIntegration.InspectNavigation(path));
            Assert.Equal(PdfNavigationFindingCode.LinkUnresolvedDestination, remaining.Code);

            PdfEngineIntegration.RepairNavigationLinks(
                path, removeUnsafeLinks: false, removeUnresolvedLinks: true);
            Assert.Empty(PdfEngineIntegration.InspectNavigation(path));
            Assert.Empty(PdfLinkReader.ReadPage(
                PdfDocument.Open(File.ReadAllBytes(path)), 0));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static byte[] DocumentWithUnsafeAndUnresolvedLinks()
    {
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 100 100] >>",
            "<< /Type /Page /Parent 2 0 R /Annots [5 0 R 6 0 R] >>",
            "<< >>",
            "<< /Type /Annot /Subtype /Link /Rect [0 0 10 10] /A << /S /URI /URI (javascript:alert) >> >>",
            "<< /Type /Annot /Subtype /Link /Rect [0 0 10 10] /Dest (missing) >>"
        ];
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int index = 0; index < objects.Length; index++)
        {
            offsets.Add(Encoding.Latin1.GetByteCount(pdf.ToString()));
            pdf.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        int xref = Encoding.Latin1.GetByteCount(pdf.ToString());
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) pdf.Append($"{offset:0000000000} 00000 n \n");
        pdf.Append($"trailer << /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(pdf.ToString());
    }
}
