using System.IO;
using System.Windows;
using KillerPDF.Services;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPDF.Tests;

// A save keeps text added with the Text tool as an editable annotation. Production layouts and Data Merge rebuild
// pages from their content and drop annotations, so their source copy draws KillerPDF's text into the page, as
// 1.9.2 did for every output; otherwise the text vanished from an N-up sheet.
public sealed class OutputSnapshotFlatteningTests
{
    [Fact]
    public void AddedTextSurvivesAProductionLayoutWhenTheCopyIsFlattenedForOutput()
    {
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-output-flatten-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, new PdfDocumentBuilder().AddBlankPage(612, 792).AddBlankPage(612, 792).Build());
            var annotations = new Dictionary<int, List<PageAnnotation>>
            {
                [0] = [new TextAnnotation { PageIndex = 0, Content = "MARKERTEXT", Position = new Point(60, 400), Width = 220, Height = 30 }]
            };
            PdfEngineBurn.Burn(path, annotations, new Dictionary<int, (int w, int h)> { [0] = (612, 792) },
                measurementsAsAnnotations: true, flattenEditing: true);

            var preset = new PdfImpositionPreset("Two-up", 2, 1, 792, 612, margin: 18, gutter: 12, duplex: false,
                includeFoldMarks: false, includeColorBars: false, includePageInformation: false,
                bindingEdge: PdfImpositionBindingEdge.Short);
            PdfDocument imposed = PdfDocument.Open(PdfImpositionMacro.Execute(PdfImpositionMacro.NUpStep(preset),
                File.ReadAllBytes(path)));

            Assert.Contains("MARKERTEXT", new PdfPageContentReader(imposed).Read(0).Text, StringComparison.Ordinal);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
