using System.IO;
using System.Text;
using System.Windows;
using KillerPDF.Services;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Filters;
using KillerPdf.Engine.Objects;
using Xunit;

namespace KillerPDF.Tests;

public sealed class AnnotationSaveDiagnosticTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void BurnMatrix_PreservesEveryAnnotationKind(bool tagged, bool forRasterization)
    {
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-save-matrix-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, tagged ? TaggedSource() : new PdfDocumentBuilder().AddBlankPage(200, 200).Build());
            PdfEngineBurn.Burn(path, EveryAnnotationKind(),
                new Dictionary<int, (int w, int h)> { [0] = (200, 200) },
                forRasterization: forRasterization);

            PdfDocument reopened = PdfDocument.Open(File.ReadAllBytes(path));
            Assert.Single(PdfPageInformation.Read(reopened));
            string streams = AllDecodedStreams(reopened);
            Assert.Contains("BT", streams);
            Assert.Contains("20 30 40 12 re", streams);
            Assert.Contains("1 J", streams);
            Assert.Contains(" Do", streams);
            Assert.Contains(forRasterization ? "/Artifact BMC" : tagged ? "/Figure <</MCID 0>> BDC" : "q", streams);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void RepeatedNormalSaveFromCleanWorkingCopy_PreservesMarkup()
    {
        byte[] clean = TaggedSource();
        for (int save = 0; save < 3; save++)
        {
            string path = Path.Combine(Path.GetTempPath(), $"killerpdf-repeat-save-{Guid.NewGuid():N}.pdf");
            try
            {
                File.WriteAllBytes(path, clean);
                PdfEngineBurn.Burn(path, EveryAnnotationKind(),
                    new Dictionary<int, (int w, int h)> { [0] = (200, 200) });
                PdfDocument reopened = PdfDocument.Open(File.ReadAllBytes(path));
                string streams = AllDecodedStreams(reopened);
                Assert.Contains("BT", streams);
                Assert.Contains("1 J", streams);
                Assert.Contains(" Do", streams);
                Assert.Contains("/Figure <</MCID 0>> BDC", streams);
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }
    }

    [Fact]
    public void MissingRenderDimensions_MustNotSilentlyDropMarkup()
    {
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-missing-dims-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, new PdfDocumentBuilder().AddBlankPage(200, 200).Build());
            PdfEngineBurn.Burn(path, EveryAnnotationKind(), new Dictionary<int, (int w, int h)>());

            string streams = AllDecodedStreams(PdfDocument.Open(File.ReadAllBytes(path)));
            Assert.Contains("1 J", streams);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static Dictionary<int, List<PageAnnotation>> EveryAnnotationKind() => new()
    {
        [0] =
        [
            new TextAnnotation
            {
                PageIndex = 0, Position = new Point(10, 10), Width = 120, Height = 30,
                Content = "Saved annotation", FontName = "Segoe UI", FontSize = 12
            },
            new HighlightAnnotation { PageIndex = 0, Bounds = new Rect(20, 30, 40, 12) },
            new InkAnnotation
            {
                PageIndex = 0, Points = [new Point(20, 60), new Point(80, 90)], StrokeWidth = 3
            },
            new SignatureAnnotation
            {
                PageIndex = 0, Position = new Point(20, 100), Scale = 1,
                Strokes = [[new Point(0, 0), new Point(30, 5), new Point(60, 0)]]
            },
            new ImageAnnotation
            {
                PageIndex = 0, Position = new Point(120, 120), SourceWidth = 10, SourceHeight = 10,
                Scale = 1,
                ImageData = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl2pGQAAAAASUVORK5CYII="
            }
        ]
    };

    private static byte[] TaggedSource() => new PdfDocumentBuilder()
        .SetMetadata(new PdfDocumentMetadata { Title = "Tagged save source", Language = "en-US" })
        .EnablePdfUa2Conformance()
        .AddPage(200, 200, new PdfContentStreamBuilder()
            .BeginMarkedContent(PdfStructureType.Figure, 0)
            .Rectangle(10, 10, 20, 20).Fill().EndMarkedContent())
        .AddStructureContainer(PdfStructureType.Document)
        .AddStructureElement(PdfStructureType.Figure, 0, 0, 1, alternateDescription: "Square")
        .Build();

    private static string AllDecodedStreams(PdfDocument document)
    {
        var text = new StringBuilder();
        foreach (int objectNumber in document.CrossReferences.Keys)
            if (document.Resolve(objectNumber) is PdfStream stream)
                try
                {
                    text.AppendLine(Encoding.GetEncoding("ISO-8859-1")
                        .GetString(PdfStreamDecoder.Decode(stream, document.Resolve)));
                }
                catch { }
        return text.ToString();
    }
}
