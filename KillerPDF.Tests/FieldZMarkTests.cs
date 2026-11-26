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

// #536: Fill Unused Space draws a Z through the part of a form field nobody wrote in and locks the field.
public sealed class FieldZMarkTests
{
    [Fact]
    public void AnEmptyFieldGetsAZAcrossTheWholeBoxInsideItsInset()
    {
        var points = FieldZMark.Points(new Rect(10, 20, 200, 100), 0, 1);

        Assert.Equal([new Point(14, 24), new Point(206, 24), new Point(14, 116), new Point(206, 116)], points);
    }

    [Fact]
    public void TheZStartsJustUnderTheLastLineAndRunsTheFullWidth()
    {
        var points = FieldZMark.Points(new Rect(10, 20, 200, 100), 30, 1);

        // 20 top + 30 of text + 2 gap + 4 inset.
        Assert.Equal([new Point(14, 56), new Point(206, 56), new Point(14, 116), new Point(206, 116)], points);
    }

    [Fact]
    public void TheZScalesWithTheCanvas()
    {
        var points = FieldZMark.Points(new Rect(0, 0, 400, 200), 0, 2);

        Assert.Equal(new Point(8, 8), points![0]);
        Assert.Equal(new Point(392, 192), points[3]);
    }

    [Theory]
    [InlineData(90)]    // text runs almost to the bottom
    [InlineData(120)]   // text overflows the box
    public void AFieldWithTooLittleRoomLeftGetsNoZ(double usedBottom)
    {
        Assert.Null(FieldZMark.Points(new Rect(10, 20, 200, 100), usedBottom, 1));
    }

    [Fact]
    public void AFieldTooNarrowForAZGetsNone()
    {
        Assert.Null(FieldZMark.Points(new Rect(0, 0, 12, 100), 0, 1));
    }

    [Fact]
    public void FillingUnusedSpaceLocksTheFieldAndKeepsWhatWasTyped()
    {
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-field-zmark-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, new PdfDocumentBuilder()
                .AddBlankPage()
                .AddTextField(0, "notes", 20, 20, 300, 120, "Only one line",
                    options: new PdfTextFieldOptions { Multiline = true, Required = true })
                .Build());

            PdfEngineIntegration.SetFormFieldReadOnly(path, "notes");

            PdfFormWidgetInfo field = Assert.Single(PdfFormWidgetReader.ReadPage(
                PdfDocument.Open(File.ReadAllBytes(path)), 0));
            Assert.Equal("Only one line", field.Value);
            Assert.Equal(1, field.Flags & 1);        // read-only now
            Assert.Equal(2, field.Flags & 2);        // still required
            Assert.Equal(1 << 12, field.Flags & (1 << 12));   // still multiline
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void LockingAFieldThatIsGoneSaysSo()
    {
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-field-zmark-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, new PdfDocumentBuilder()
                .AddBlankPage()
                .AddTextField(0, "notes", 20, 20, 300, 120, "")
                .Build());

            Assert.Throws<InvalidOperationException>(() => PdfEngineIntegration.SetFormFieldReadOnly(path, "missing"));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Theory]
    [InlineData(true, "2 J", "0 j")]
    [InlineData(false, "1 J", "1 j")]
    public void TheZSavesWithSquareEndsAndSharpCornersWhileOtherInkStaysRound(bool squareEnds, string cap, string join)
    {
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-field-zmark-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, new PdfDocumentBuilder().AddBlankPage().Build());
            var ink = new InkAnnotation
            {
                PageIndex = 0, StrokeWidth = 1.5, SquareEnds = squareEnds,
                Points = [new Point(20, 20), new Point(180, 20), new Point(20, 180), new Point(180, 180)],
            };
            ink.SetColor(System.Windows.Media.Colors.Black);
            var annotations = new Dictionary<int, List<PageAnnotation>> { [0] = [ink] };
            PdfEngineBurn.Burn(path, annotations, new Dictionary<int, (int w, int h)> { [0] = (200, 200) });

            string streams = AllDecodedStreams(path);
            Assert.Contains(cap, streams);
            Assert.Contains(join, streams);
            if (squareEnds) Assert.Contains("4 M", streams);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static string AllDecodedStreams(string path)
    {
        PdfDocument document = PdfDocument.Open(File.ReadAllBytes(path));
        var text = new StringBuilder();
        foreach (int objectNumber in document.CrossReferences.Keys)
            if (document.Resolve(objectNumber) is PdfStream stream)
                try { text.AppendLine(Encoding.Latin1.GetString(PdfStreamDecoder.Decode(stream, document.Resolve))); }
                catch (InvalidDataException) { }
        return text.ToString();
    }
}
