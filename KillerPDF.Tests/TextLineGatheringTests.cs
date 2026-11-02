using System.IO;
using System.Text;
using System.Windows;
using KillerPdf.Engine.Documents;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

/// <summary>
/// Starting a Text tool edit on a line prefills the box with the line's words. A drop numeral or chapter
/// number beside the line shares its rows, so it was gathered with the line, prefilled into the box and
/// then lent the line its size and origin. The line holds only words at its own size.
/// </summary>
public sealed class TextLineGatheringTests
{
    private const double PageWidth = 400, PageHeight = 400;

    // A numeral at three times the body size stands left of two body lines and is as tall as both.
    private const string DropNumeralPage =
        "BT /F1 36 Tf 72 314 Td (7) Tj ET " +
        "BT /F1 12 Tf 110 330 Td (The line text beside) Tj ET " +
        "BT /F1 12 Tf 110 314 Td (second body line) Tj ET";

    [Fact]
    public void ANumeralBesideALineIsNotPartOfTheLine()
    {
        Assert.Equal("The line text beside", LineAt(DropNumeralPage, x: 150, pageY: 334));
        Assert.Equal("second body line", LineAt(DropNumeralPage, x: 150, pageY: 318));
    }

    [Fact]
    public void AClickBetweenTheNumeralAndTheLineStillGathersTheLine()
    {
        Assert.Equal("The line text beside", LineAt(DropNumeralPage, x: 104, pageY: 334));
    }

    [Fact]
    public void ClickingTheNumeralItselfGathersTheNumeral()
    {
        Assert.Equal("7", LineAt(DropNumeralPage, x: 80, pageY: 326));
    }

    [Fact]
    public void WordsOfSomewhatDifferentSizesStayOneLine()
    {
        const string page =
            "BT /F1 12 Tf 72 300 Td (Total due) Tj ET " +
            "BT /F1 18 Tf 140 300 Td (1,234) Tj ET " +
            "BT /F1 7 Tf 190 304 Td (2) Tj ET";
        Assert.Equal("Total due 1,234 2", LineAt(page, x: 80, pageY: 304));
    }

    [Fact]
    public void ALineStopsAtTheGapToAnotherColumn()
    {
        const string page =
            "BT /F1 12 Tf 72 300 Td (left column) Tj ET " +
            "BT /F1 12 Tf 300 300 Td (right column) Tj ET";
        Assert.Equal("left column", LineAt(page, x: 80, pageY: 304));
        Assert.Equal("right column", LineAt(page, x: 310, pageY: 304));
    }

    // Each recognized word's box follows its own letters, so "payment" hangs lower than "Please".
    [Fact]
    public void AClickLowOnARecognizedLineGathersAllOfIt()
    {
        Assert.Equal("Please remit payment within thirty days", LineInFile(ScanFixture(), x: 150, pageY: 596));
    }

    // The acceptance run met "paymentwithin" on the file a word edit had just saved.
    [Fact]
    public void ASavedWordEditLeavesTheNextLinesWordsApart()
    {
        string path = Path.Combine(Path.GetTempPath(), "KillerPDF-scan-" + Guid.NewGuid().ToString("N") + ".pdf");
        File.Copy(ScanFixture(), path);
        try
        {
            var cover = new CoverAnnotation { PageIndex = 0, PairId = "edit", Bounds = new Rect(183, 143, 27, 16) };
            cover.SetColor(System.Windows.Media.Colors.White);
            var text = new TextAnnotation
            {
                PageIndex = 0, PairId = "edit", Position = new Point(186, 144), Content = "via",
                FontSize = 12, FontName = "Times New Roman", Width = 40, Height = 16
            };
            PdfEngineBurn.Burn(path, new Dictionary<int, List<PageAnnotation>> { [0] = [cover, text] },
                new Dictionary<int, (int w, int h)> { [0] = (612, 792) });

            Assert.Equal("Please remit payment within thirty days", LineInFile(path, x: 150, pageY: 600));
        }
        finally { File.Delete(path); }
    }

    private static string ScanFixture() =>
        Path.Combine(FindRepositoryRoot(), "KillerPDF.Tests", "TestData", "scan-realocr.pdf");

    private static string LineInFile(string path, double x, double pageY)
    {
        using var document = PdfContentDocument.Open(path);
        var page = document.GetPage(1);
        Rect Canvas(PdfContentBounds b) => RedactionGeometry.PageToCanvas(
            b.Left, b.Bottom, b.Right, b.Top, page.Width, page.Height, page.Width, page.Height, 0);
        TextLineGathering.Candidate[] candidates = [.. page.GetWords().Select(word => new TextLineGathering.Candidate(
            word.Text, Canvas(word.BoundingBox), word.Letters.Count > 0 ? word.Letters[0].PointSize : 0))];
        Point click = RedactionGeometry.PageToCanvas(x, pageY, x, pageY, page.Width, page.Height, page.Width, page.Height, 0).TopLeft;

        return string.Join(" ", TextLineGathering.Gather(candidates, click).Select(index => candidates[index].Text));
    }

    // The viewer is not built here, so the Text tool is pinned to the gathering it is tested through.
    [Fact]
    public void TheTextToolGathersItsLineThroughTheTestedHelper()
    {
        string source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Controls", "Viewer", "PdfViewer.TextEditing.cs"));
        Assert.Contains("TextLineGathering.Gather(", source);
        Assert.Contains("Letters[0].PointSize", source);
        Assert.DoesNotContain("strictly left-to-right", source);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "KillerPDF.csproj"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new InvalidOperationException("KillerPDF.csproj was not found above the test output.");
    }

    // Reads the words the way the Text tool does: the page is turned onto a canvas whose y grows downward,
    // every word carries the size of its first letter, and the click lands on that canvas.
    private static string LineAt(string content, double x, double pageY)
    {
        using var document = PdfContentDocument.Open(Pdf(content));
        PdfExtractedWord[] words = [.. document.GetPage(1).GetWords()];
        Rect Canvas(PdfContentBounds b) => RedactionGeometry.PageToCanvas(
            b.Left, b.Bottom, b.Right, b.Top, PageWidth, PageHeight, PageWidth, PageHeight, 0);
        TextLineGathering.Candidate[] candidates = [.. words.Select(word => new TextLineGathering.Candidate(
            word.Text, Canvas(word.BoundingBox), word.Letters.Count > 0 ? word.Letters[0].PointSize : 0))];
        Point click = RedactionGeometry.PageToCanvas(x, pageY, x, pageY, PageWidth, PageHeight, PageWidth, PageHeight, 0).TopLeft;

        return string.Join(" ", TextLineGathering.Gather(candidates, click).Select(index => candidates[index].Text));
    }

    private static byte[] Pdf(string content)
    {
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {PageWidth} {PageHeight}] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream"
        ];
        var text = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int index = 0; index < objects.Length; index++)
        {
            offsets.Add(text.Length);
            text.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        int xref = text.Length;
        text.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) text.Append($"{offset:0000000000} 00000 n \n");
        text.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(text.ToString());
    }
}
