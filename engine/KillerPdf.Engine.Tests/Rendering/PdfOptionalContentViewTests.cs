using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfOptionalContentViewTests
{
    [Fact]
    public void Render_ViewAutoStateReversesRawDefaultForTwoLayeredForms()
    {
        PdfDocument document = CreateDocument(
            "/AS [<< /Event /View /Category [/View] /OCGs [5 0 R 6 0 R] >>]");
        PdfOptionalContentInfo info = PdfOptionalContentReader.Read(document);

        Assert.Equal(2, info.Groups.Count);
        Assert.False(info.Groups.Single(group => group.Name == "Left").IsInitiallyVisible);
        Assert.True(info.Groups.Single(group => group.Name == "Right").IsInitiallyVisible);
        PdfOptionalContentConfigurationInfo raw = Assert.Single(info.Configurations);
        Assert.DoesNotContain(5, raw.VisibleGroupObjectNumbers);
        Assert.Contains(6, raw.VisibleGroupObjectNumbers);
        Assert.Equal([5], info.ViewVisibleGroupObjectNumbers.Order());
        Assert.Equal(PdfOptionalContentReader.Read(CreateDocument(null)).ToJson(), info.ToJson());

        AssertCells(document, leftVisible: true, rightVisible: false);
    }

    [Theory]
    [InlineData(null, false, true)]
    [InlineData("/AS [<< /Event /Export /Category [/View] /OCGs [5 0 R 6 0 R] >>]", false, true)]
    [InlineData("/AS [<< /Event /View /Category [/View] >>]", false, true)]
    [InlineData("/AS [<< /Event /View /Category [/View] /OCGs [5 0 R] >>]", true, true)]
    [InlineData("/AS [<< /Event /View /Category [/Zoom] /OCGs [5 0 R 6 0 R] >>]", false, true)]
    [InlineData("/AS [<< /Event /View /Category [/View /Zoom] /OCGs [5 0 R 6 0 R] >>]", false, true)]
    [InlineData("/AS [<< /Event /View /Category [/View] /OCGs [5 0 R] >> << /Event /View /Category [/View /Zoom] /OCGs [5 0 R] >>]", false, true)]
    [InlineData("/AS [<< /Event /View /Category [/View /Zoom] /OCGs [5 0 R] >> << /Event /View /Category [/View] /OCGs [5 0 R] >>]", false, true)]
    [InlineData("/AS [<< /Category [/View] /OCGs [5 0 R 6 0 R] >>]", false, true)]
    [InlineData("/AS [<< /Event (View) /Category [/View] /OCGs [5 0 R 6 0 R] >>]", false, true)]
    [InlineData("/AS [<< /Event /View /OCGs [5 0 R 6 0 R] >>]", false, true)]
    [InlineData("/AS [<< /Event /View /Category /View /OCGs [5 0 R 6 0 R] >>]", false, true)]
    [InlineData("/AS [<< /Event /View /Category [] /OCGs [5 0 R 6 0 R] >>]", false, true)]
    [InlineData("/AS [<< /Event /View /Category [/View] /OCGs [5 1 R 6 1 R] >>]", false, true)]
    [InlineData("/AS [<< /Event /View /Category [/View] /OCGs [5 0 R 6 0 R] >> << /Event /View /Category [/View] /OCGs [5 0 R 6 0 R] >>]", true, false)]
    [InlineData("/AS 42", false, true)]
    public void Render_ViewAutoStateAffectsOnlySupportedListedGroups(
        string? autoState, bool leftVisible, bool rightVisible)
    {
        AssertCells(CreateDocument(autoState), leftVisible, rightVisible);
    }

    [Fact]
    public void Render_ViewAutoStateResolvesIndirectContainersAndUsage()
    {
        PdfDocument document = CreateDocument("/AS 9 0 R", leftUsage: "12 0 R",
            extraObjects: ["[10 0 R]", "<< /Event /View /Category 11 0 R /OCGs 14 0 R >>",
                "[/View]", "<< /View 13 0 R >>", "<< /ViewState /ON >>", "[5 0 R 6 0 R]"]);
        AssertCells(document, leftVisible: true, rightVisible: false);
    }

    [Theory]
    [InlineData("<< >>")]
    [InlineData("<< /View 42 >>")]
    [InlineData("<< /View << /ViewState /Unexpected >> >>")]
    public void Render_MissingOrMalformedViewPreferenceKeepsRawState(string leftUsage)
    {
        AssertCells(CreateDocument("/AS [<< /Event /View /Category [/View] /OCGs [5 0 R 6 0 R] >>]",
            leftUsage: leftUsage), leftVisible: false, rightVisible: false);
    }

    [Fact]
    public void Render_CyclicUsageApplicationContainerKeepsRawState()
    {
        AssertCells(CreateDocument("/AS 9 0 R", extraObjects: ["9 0 R"]),
            leftVisible: false, rightVisible: true);
    }

    [Fact]
    public void FlattenPageContentRetainsRawDefaultPolicy()
    {
        PdfDocument document = CreateDocument(
            "/AS [<< /Event /View /Category [/View] /OCGs [5 0 R 6 0 R] >>]",
            markedContent: true);
        AssertCells(document, leftVisible: true, rightVisible: false);
        PdfDocument flattened = PdfDocument.Open(PdfOptionalContentEditor.FlattenPageContent(document));
        AssertCells(flattened, leftVisible: false, rightVisible: true);
    }

    private static void AssertCells(PdfDocument document, bool leftVisible, bool rightVisible)
    {
        PdfRenderedPage page = new PdfPageRenderer(document).Render(0,
            new PdfRenderOptions(2, 1, includeAnnotations: false, includeFormFields: false));

        Assert.Empty(page.Diagnostics);
        Assert.Equal(leftVisible ? [0, 0, 255, 255]
            : [255, 255, 255, 255], Pixel(page, 0));
        Assert.Equal(rightVisible ? [255, 0, 0, 255]
            : [255, 255, 255, 255], Pixel(page, 1));
    }

    private static byte[] Pixel(PdfRenderedPage page, int x) =>
        page.Pixels.Slice(x * 4, 4).ToArray();

    private static PdfDocument CreateDocument(string? autoState,
        string leftUsage = "<< /View << /ViewState /ON >> /Export << /ExportState /ON >> >>",
        string[]? extraObjects = null, bool markedContent = false)
    {
        string pageContent = markedContent
            ? "/OC /Left BDC 1 0 0 rg 0 0 1 1 re f EMC /OC /Right BDC 0 0 1 rg 1 0 1 1 re f EMC"
            : "q /Left Do Q q 1 0 0 1 1 0 cm /Right Do Q";
        string resources = markedContent ? "/Properties << /Left 5 0 R /Right 6 0 R >>"
            : "/XObject << /Left 7 0 R /Right 8 0 R >>";
        const string leftContent = "1 0 0 rg 0 0 1 1 re f";
        const string rightContent = "0 0 1 rg 0 0 1 1 re f";
        string[] objects =
        [
            $"<< /Type /Catalog /Pages 2 0 R /OCProperties << /OCGs [5 0 R 6 0 R] /D << /BaseState /OFF /ON [6 0 R] /OFF [5 0 R] {autoState} >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 2 1] >>",
            $"<< /Type /Page /Parent 2 0 R /Resources << {resources} >> /Contents 4 0 R >>",
            Stream($"<< /Length {Encoding.ASCII.GetByteCount(pageContent)} >>", pageContent),
            $"<< /Type /OCG /Name (Left) /Usage {leftUsage} >>",
            "<< /Type /OCG /Name (Right) /Usage << /View << /ViewState /OFF >> /Export << /ExportState /OFF >> >> >>",
            Stream($"<< /Type /XObject /Subtype /Form /FormType 1 /BBox [0 0 1 1] /Resources << >> /OC 5 0 R /Length {Encoding.ASCII.GetByteCount(leftContent)} >>", leftContent),
            Stream($"<< /Type /XObject /Subtype /Form /FormType 1 /BBox [0 0 1 1] /Resources << >> /OC 6 0 R /Length {Encoding.ASCII.GetByteCount(rightContent)} >>", rightContent),
            .. extraObjects ?? []
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
        return PdfDocument.Open(Encoding.Latin1.GetBytes(pdf.ToString()));
    }

    private static string Stream(string dictionary, string content) =>
        $"{dictionary}\nstream\n{content}\nendstream";
}
