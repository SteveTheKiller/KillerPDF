using System.IO;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class OverlayVisibilityPersistenceTests
{
    [Theory]
    [InlineData(PdfMeasurementKind.Distance, 36, false, true)]
    [InlineData(PdfMeasurementKind.Perimeter, 36, false, true)]
    [InlineData(PdfMeasurementKind.Area, 36, false, true)]
    [InlineData(PdfMeasurementKind.Angle, 36, false, true)]
    [InlineData(PdfMeasurementKind.Distance, 2, false, false)]
    [InlineData(PdfMeasurementKind.Distance, 34, false, false)]
    [InlineData(PdfMeasurementKind.Distance, 0, true, false)]
    [InlineData(PdfMeasurementKind.Distance, 4, true, true)]
    [InlineData(PdfMeasurementKind.Distance, 1023, false, false)]
    public void MeasurementAndRedactionRetainFlagsThroughOverlaySave(
        PdfMeasurementKind kind, int flags, bool screenVisible, bool printVisible)
    {
        var editor = new PdfIncrementalAnnotationEditor(PdfDocument.Open(
            new PdfDocumentBuilder().AddBlankPage(100, 100).Build()));
        var metadata = new PdfAnnotationMetadata { Flags = (PdfAnnotationFlags)flags };
        PdfPoint first = new(10, 20), middle = new(50, 30), last = new(80, 20);
        switch (kind)
        {
            case PdfMeasurementKind.Distance:
                editor.AddDistanceMeasurement(0, first, last,
                    PdfEngineIntegration.ActualSizeProfile, annotationMetadata: metadata);
                break;
            case PdfMeasurementKind.Perimeter:
                editor.AddPolyline(0, [first, middle, last], annotationMetadata: metadata,
                    intent: PdfVertexAnnotationIntent.Dimension, measurement: PdfEngineIntegration.ActualSizeProfile);
                break;
            case PdfMeasurementKind.Area:
                editor.AddPolygon(0, [first, middle, last], annotationMetadata: metadata,
                    intent: PdfVertexAnnotationIntent.Dimension, measurement: PdfEngineIntegration.ActualSizeProfile);
                break;
            case PdfMeasurementKind.Angle:
                editor.AddAngleMeasurement(0, first, middle, last, annotationMetadata: metadata);
                break;
        }
        editor.AddRedactionMark(0,
            [new PdfTextQuad(new PdfPoint(20, 80), new PdfPoint(70, 80),
                new PdfPoint(20, 50), new PdfPoint(70, 50))], annotationMetadata: metadata);
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-overlay-flags-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, editor.Build());
            PdfDocument source = PdfDocument.Open(File.ReadAllBytes(path));
            var measurements = PdfEngineIntegration.ReadMeasurements(source);
            var marks = PdfEngineIntegration.ReadRedactionMarks(source);
            Assert.Single(measurements);
            Assert.Single(marks);

            PdfEngineIntegration.WriteMeasurements(path, measurements);
            PdfEngineIntegration.WriteRedactionMarks(path, marks);

            PdfDocument reopened = PdfDocument.Open(File.ReadAllBytes(path));
            PdfObject Resolve(PdfObject value) => value is PdfIndirectReference reference
                ? reopened.Resolve(reference) : value;
            PdfDictionary catalog = Assert.IsType<PdfDictionary>(Resolve(reopened.Trailer[new PdfName("Root"u8)]));
            PdfDictionary pages = Assert.IsType<PdfDictionary>(Resolve(catalog[new PdfName("Pages"u8)]));
            PdfArray kids = Assert.IsType<PdfArray>(Resolve(pages[new PdfName("Kids"u8)]));
            PdfDictionary page = Assert.IsType<PdfDictionary>(Resolve(Assert.Single(kids)));
            var annotations = Assert.IsType<PdfArray>(page[new PdfName("Annots"u8)]);
            Assert.Equal(2, annotations.Count);
            foreach (PdfObject item in annotations)
            {
                PdfDictionary annotation = Assert.IsType<PdfDictionary>(
                    item is PdfIndirectReference reference ? reopened.Resolve(reference) : item);
                Assert.Equal(flags, Assert.IsType<PdfInteger>(annotation[new PdfName("F"u8)]).Value);
            }
            var renderer = new PdfPageRenderer(reopened);
            AssertVisibility(renderer.Render(0, new PdfRenderOptions(100, 100)), screenVisible);
            AssertVisibility(renderer.Render(0, new PdfRenderOptions(100, 100) { ForPrinting = true }), printVisible);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnsupportedHighFlagsCannotBeSilentlyTruncatedWhenSaving(bool redaction)
    {
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-overlay-high-flags-{Guid.NewGuid():N}.pdf");
        byte[] original = new PdfDocumentBuilder().AddBlankPage(100, 100).Build();
        const long flags = (1L << 32) | 4;
        try
        {
            File.WriteAllBytes(path, original);
            if (redaction)
                Assert.Throws<OverflowException>(() => PdfEngineIntegration.WriteRedactionMarks(path,
                    [new PdfEngineIntegration.RedactionMarkData(0, 10, 10, 50, 50, AnnotationFlags: flags)]));
            else
                Assert.Throws<OverflowException>(() => PdfEngineIntegration.WriteMeasurements(path,
                    [new PdfEngineIntegration.MeasurementData(0, PdfMeasurementKind.Distance,
                        [new PdfPoint(10, 10), new PdfPoint(80, 10)], false,
                        PdfEngineIntegration.ActualSizeProfile, null, new PdfRgbColor(1, 0, 0), 1, 1,
                        null, null, AnnotationFlags: flags)]));
            Assert.Equal(original, File.ReadAllBytes(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void PrintSessionIncludesPrintOnlyRedactionMarks()
    {
        var editor = new PdfIncrementalAnnotationEditor(PdfDocument.Open(
            new PdfDocumentBuilder().AddBlankPage(100, 100).Build()));
        editor.AddRedactionMark(0,
            [new PdfTextQuad(new PdfPoint(20, 80), new PdfPoint(70, 80),
                new PdfPoint(20, 50), new PdfPoint(70, 50))],
            annotationMetadata: new PdfAnnotationMetadata { Flags = PdfAnnotationFlags.Print | PdfAnnotationFlags.NoView });
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-print-redaction-mark-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, editor.Build());
            using var screen = PdfPageRenderSession.OpenEngineFirst(path, 1.0, null);
            using var print = PdfPageRenderSession.OpenEngineFirst(path, 1.0, null, forPrinting: true);
            Assert.All(screen.RenderPage(0).Pixels, value => Assert.Equal((byte)255, value));
            Assert.Contains(print.RenderPage(0).Pixels, value => value < 255);
        }
        finally { File.Delete(path); }
    }

    private static void AssertVisibility(KillerPdf.Engine.Rendering.PdfRenderedPage page, bool visible)
    {
        if (visible) Assert.Contains(page.Pixels.ToArray(), value => value < 128);
        else Assert.All(page.Pixels.ToArray(), value => Assert.Equal((byte)255, value));
    }
}
