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
        var points = FieldZMark.Points(new Rect(10, 20, 200, 100), 0, 1)!;

        Assert.Equal(new Rect(14, 24, 192, 92), Bounds(points));
    }

    [Fact]
    public void TheZStartsJustUnderTheLastLineAndRunsTheFullWidth()
    {
        var points = FieldZMark.Points(new Rect(10, 20, 200, 100), 30, 1)!;

        // 20 top + 30 of text + 2 gap + 4 inset.
        Assert.Equal(new Rect(14, 56, 192, 60), Bounds(points));
    }

    [Fact]
    public void TheZScalesWithTheCanvas()
    {
        var points = FieldZMark.Points(new Rect(0, 0, 400, 200), 0, 2)!;

        Assert.Equal(new Rect(8, 8, 384, 184), Bounds(points));
    }

    // The Z is the Killer Sans Z: a closed outline, filled, with bars 68/649 of its height and a diagonal cut flush
    // where it meets them, a little heavier than the bars the way the glyph draws it.
    [Fact]
    public void TheZIsTheKillerSansZOutline()
    {
        var points = FieldZMark.Points(new Rect(10, 20, 200, 100), 0, 1)!;

        Assert.Equal(11, points.Count);
        Assert.Equal(points[0], points[^1]);
        double bar = points[2].Y - points[1].Y;
        Assert.Equal(92 * 68.0 / 649, bar, 6);
        Assert.Equal(bar, points[5].Y - points[4].Y, 6);
        // The diagonal's edges run from (L, D-t) up to (R-w, U+t) and from (L+w, D-t) up to (R, U+t), parallel.
        Vector left = points[8] - points[7], right = points[2] - points[3];
        Assert.Equal(left.Y / left.X, right.Y / right.X, 6);
        left.Normalize();
        Assert.Equal(bar * 90 / 68, Math.Abs(Vector.CrossProduct(left, points[3] - points[7])), 3);
    }

    [Fact]
    public void AWideShallowSpaceStillGetsASolidZInsideIt()
    {
        var points = FieldZMark.Points(new Rect(0, 0, 508, 68), 0, 1)!;

        Assert.True(points[2].Y - points[1].Y >= 6);
        Assert.Equal(new Rect(4, 4, 500, 60), Bounds(points));
    }

    private static Rect Bounds(IReadOnlyList<Point> points)
    {
        double left = points.Min(p => p.X), top = points.Min(p => p.Y);
        return new Rect(left, top, points.Max(p => p.X) - left, points.Max(p => p.Y) - top);
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
