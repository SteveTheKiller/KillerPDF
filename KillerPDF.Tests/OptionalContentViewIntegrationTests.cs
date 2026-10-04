using System.IO;
using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class OptionalContentViewIntegrationTests
{
    [Fact]
    public void InspectLayers_ReportsEffectiveViewVisibilityBesideRawDefault()
    {
        WithViewDocument(path =>
        {
            PdfOptionalContentInfo layers = PdfEngineIntegration.InspectLayers(path);

            AssertRawVisibility(layers, leftVisible: false, rightVisible: true);
            AssertViewVisibility(layers, leftVisible: true, rightVisible: false);
            AssertRenderedCells(path, leftVisible: true, rightVisible: false);
        });
    }

    [Fact]
    public void ApplyLayerEdits_NameAndLockPreserveViewVisibilityAndRawDefault()
    {
        WithViewDocument(path =>
        {
            PdfOptionalContentInfo original = PdfEngineIntegration.InspectLayers(path);
            int left = original.Groups.Single(group => group.Name == "Left").ObjectNumber;
            int right = original.Groups.Single(group => group.Name == "Right").ObjectNumber;

            PdfEngineIntegration.ApplyLayerEdits(path,
            [
                new(left, "Reviewed Left", true, true),
                new(right, "Right", false, false)
            ]);

            PdfOptionalContentInfo saved = PdfEngineIntegration.InspectLayers(path);
            Assert.Equal("Reviewed Left", saved.Groups.Single(group => group.ObjectNumber == left).Name);
            Assert.True(saved.Groups.Single(group => group.ObjectNumber == left).IsLocked);
            AssertRawVisibility(saved, leftVisible: false, rightVisible: true);
            AssertViewVisibility(saved, leftVisible: true, rightVisible: false);
            AssertRenderedCells(path, leftVisible: true, rightVisible: false);
        });
    }

    [Fact]
    public void ApplyLayerEdits_ExplicitViewToggleSurvivesReopenAndCanBeReversed()
    {
        WithViewDocument(path =>
        {
            PdfOptionalContentInfo original = PdfEngineIntegration.InspectLayers(path);
            int left = original.Groups.Single(group => group.Name == "Left").ObjectNumber;
            int right = original.Groups.Single(group => group.Name == "Right").ObjectNumber;

            PdfEngineIntegration.ApplyLayerEdits(path,
            [
                new(left, "Left", true, false),
                new(right, "Right", true, false)
            ]);

            PdfOptionalContentInfo visible = PdfEngineIntegration.InspectLayers(path);
            AssertRawVisibility(visible, leftVisible: false, rightVisible: true);
            AssertViewVisibility(visible, leftVisible: true, rightVisible: true);
            AssertRenderedCells(path, leftVisible: true, rightVisible: true);

            PdfEngineIntegration.ApplyLayerEdits(path,
            [
                new(left, "Left", true, false),
                new(right, "Right", false, false)
            ]);

            PdfOptionalContentInfo hidden = PdfEngineIntegration.InspectLayers(path);
            AssertRawVisibility(hidden, leftVisible: false, rightVisible: false);
            AssertViewVisibility(hidden, leftVisible: true, rightVisible: false);
            AssertRenderedCells(path, leftVisible: true, rightVisible: false);
        });
    }

    private static void AssertRawVisibility(
        PdfOptionalContentInfo layers, bool leftVisible, bool rightVisible)
    {
        Assert.Equal(leftVisible, layers.Groups.Single(group => group.Name.EndsWith("Left", StringComparison.Ordinal))
            .IsInitiallyVisible);
        Assert.Equal(rightVisible, layers.Groups.Single(group => group.Name == "Right")
            .IsInitiallyVisible);
        PdfOptionalContentConfigurationInfo configuration = Assert.Single(layers.Configurations);
        Assert.Equal(leftVisible, configuration.VisibleGroupObjectNumbers.Contains(5));
        Assert.Equal(rightVisible, configuration.VisibleGroupObjectNumbers.Contains(6));
    }

    private static void AssertViewVisibility(
        PdfOptionalContentInfo layers, bool leftVisible, bool rightVisible)
    {
        Assert.Equal(leftVisible, layers.ViewVisibleGroupObjectNumbers.Contains(5));
        Assert.Equal(rightVisible, layers.ViewVisibleGroupObjectNumbers.Contains(6));
    }

    private static void AssertRenderedCells(string path, bool leftVisible, bool rightVisible)
    {
        PdfDocument document = PdfDocument.Open(File.ReadAllBytes(path));
        var page = new PdfPageRenderer(document).Render(0,
            new PdfRenderOptions(2, 1, includeAnnotations: false, includeFormFields: false));

        Assert.Empty(page.Diagnostics);
        Assert.Equal(leftVisible ? new byte[] { 0, 0, 255, 255 }
            : new byte[] { 255, 255, 255, 255 }, page.Pixels.Slice(0, 4).ToArray());
        Assert.Equal(rightVisible ? new byte[] { 255, 0, 0, 255 }
            : new byte[] { 255, 255, 255, 255 }, page.Pixels.Slice(4, 4).ToArray());
    }

    private static void WithViewDocument(Action<string> run)
    {
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-view-layers-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, CreateViewDocument());
            run(path);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static byte[] CreateViewDocument()
    {
        const string pageContent = "q /Left Do Q q 1 0 0 1 1 0 cm /Right Do Q";
        const string leftContent = "1 0 0 rg 0 0 1 1 re f";
        const string rightContent = "0 0 1 rg 0 0 1 1 re f";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R /OCProperties << /OCGs [5 0 R 6 0 R] /D << /BaseState /OFF /ON [6 0 R] /OFF [5 0 R] /AS [<< /Event /View /Category [/View] /OCGs [5 0 R 6 0 R] >>] >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 2 1] >>",
            "<< /Type /Page /Parent 2 0 R /Resources << /XObject << /Left 7 0 R /Right 8 0 R >> >> /Contents 4 0 R >>",
            Stream($"<< /Length {Encoding.ASCII.GetByteCount(pageContent)} >>", pageContent),
            "<< /Type /OCG /Name (Left) /Usage << /View << /ViewState /ON >> >> >>",
            "<< /Type /OCG /Name (Right) /Usage << /View << /ViewState /OFF >> >> >>",
            Stream($"<< /Type /XObject /Subtype /Form /FormType 1 /BBox [0 0 1 1] /Resources << >> /OC 5 0 R /Length {Encoding.ASCII.GetByteCount(leftContent)} >>", leftContent),
            Stream($"<< /Type /XObject /Subtype /Form /FormType 1 /BBox [0 0 1 1] /Resources << >> /OC 6 0 R /Length {Encoding.ASCII.GetByteCount(rightContent)} >>", rightContent)
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
        foreach (int offset in offsets)
            pdf.Append($"{offset:0000000000} 00000 n \n");
        pdf.Append($"trailer << /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(pdf.ToString());
    }

    private static string Stream(string dictionary, string content) =>
        $"{dictionary}\nstream\n{content}\nendstream";
}
