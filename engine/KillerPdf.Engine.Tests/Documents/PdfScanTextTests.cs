using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfScanTextTests
{
    private const double Scale = 3;
    private const double Size = 14;
    private const double PageWidth = 420;
    private static readonly int[] TwoLines = [0, 1];
    private static readonly bool[] ScanThenTyped = [true, false];
    private static readonly bool[] NoScans = [false, false];
    private static readonly bool[] OneScan = [true];

    [Theory]
    [InlineData("Times-Roman")]
    [InlineData("Courier")]
    public void WordsAreMeasuredOnTheirBaselineAtTheirXHeight(string fontName)
    {
        TrueTypeFont font = PdfStandardFontSubstitutes.Find(fontName);
        Scan scan = Render(font, "the quick brown fox jumps", "over the lazy dog");

        PdfScanTextPageAnalysis analysis = Analyze(scan);

        double xHeight = PdfScanTextWriter.XHeightRatio(font) * Size * Scale;
        Assert.All(analysis.Words, word =>
        {
            Assert.True(word.HasClearInk, word.Word.Text);
            Assert.True(word.Replaceable, word.Word.Text);
            Assert.InRange(word.Baseline, scan.Baselines[word.Line] - 2, scan.Baselines[word.Line] + 2);
            Assert.InRange(word.XHeight, xHeight * 0.88, xHeight * 1.12);
            Assert.False(word.CapitalsOnly);
        });
        Assert.Equal(TwoLines, analysis.Words.Select(word => word.Line).Distinct().Order().ToArray());
    }

    [Theory]
    [InlineData("Times-Roman", "Liberation Serif")]
    [InlineData("Helvetica", "Liberation Sans")]
    [InlineData("Courier", "Liberation Mono")]
    public void TheMatchingFaceIsChosen(string fontName, string family)
    {
        Scan scan = Render(PdfStandardFontSubstitutes.Find(fontName),
            "a simple sentence with several ordinary words", "followed by another line of text");

        PdfScanTextFace face = PdfScanTextWriter.ChooseFace(Analyze(scan), PdfScanTextWriter.BundledFaces());

        Assert.Equal(family, face.Family);
    }

    [Fact]
    public void MeasureLineReadsTheLineItWasGiven()
    {
        Scan scan = Render(PdfStandardFontSubstitutes.Find("Times-Roman"), "the reading lamp stood by the window");

        PdfScanLineLook look = Assert.IsType<PdfScanLineLook>(
            PdfScanTextAnalyzer.MeasureLine(scan.Pixels, scan.Width, scan.Height, scan.Words));

        Assert.True(look.BackgroundColor.Red > 200 && look.BackgroundColor.Green > 200 && look.BackgroundColor.Blue > 200);
        Assert.True(look.InkColor.Red < 120 && look.InkColor.Green < 120 && look.InkColor.Blue < 120);
        Assert.True(look.XHeight > 3);
        Assert.False(look.Bold);
        Assert.Equal(scan.Words.Count, look.Words.Count);
    }

    [Fact]
    public void MeasureLineHasNothingToSayWithoutWords()
    {
        Scan scan = Render(PdfStandardFontSubstitutes.Find("Times-Roman"), "the lamp");

        Assert.Null(PdfScanTextAnalyzer.MeasureLine(scan.Pixels, scan.Width, scan.Height, []));
    }

    [Fact]
    public void FontSizeForTurnsMeasuredHeightsBackIntoPoints()
    {
        PdfScanTextFace face = PdfScanTextWriter.BundledFaces()[0];
        double ratio = PdfScanTextWriter.XHeightRatio(face.Regular);

        Assert.Equal(12, PdfScanTextWriter.FontSizeFor(face, 12 * ratio, 0, capitalsOnly: false, pointsPerPixel: 1), 6);
        Assert.Equal(24, PdfScanTextWriter.FontSizeFor(face, 12 * ratio, 0, capitalsOnly: false, pointsPerPixel: 2), 6);
        Assert.Equal(10, PdfScanTextWriter.FontSizeFor(face, 0,
            10 * PdfScanTextWriter.CapHeightRatio(face.Regular), capitalsOnly: true, pointsPerPixel: 1), 6);
    }

    [Fact]
    public void AMisreadWordStaysInTheImage()
    {
        Scan scan = Render(PdfStandardFontSubstitutes.Find("Times-Roman"),
            "the reading lamp stood by the window", "and lit the open book");
        List<PdfOcrPixelWord> words = [.. scan.Words.Select(word =>
            word.Text == "open" ? word with { Text = "oven" } : word)];
        PdfScanTextPageAnalysis analysis = PdfScanTextAnalyzer.Analyze(
            scan.Pixels, scan.Width, scan.Height, words);
        PdfScanTextFace face = PdfScanTextWriter.BundledFaces()[0];

        int rejected = PdfScanTextWriter.Verify(analysis, face);

        Assert.Equal(1, rejected);
        Assert.False(analysis.Words.Single(word => word.Word.Text == "oven").Replaceable);
        Assert.All(analysis.Words.Where(word => word.Word.Text != "oven"),
            word => Assert.True(word.Replaceable, word.Word.Text));
    }

    [Fact]
    public void ALetterReadAsItsLookalikeStaysInTheImage()
    {
        Scan scan = Render(PdfStandardFontSubstitutes.Find("Times-Roman"),
            "Each of the hundred sequences", "holds many tracks");
        List<PdfOcrPixelWord> words = [.. scan.Words.Select(word =>
            word.Text == "Each" ? word with { Text = "Fach" } : word)];
        PdfScanTextPageAnalysis analysis = PdfScanTextAnalyzer.Analyze(
            scan.Pixels, scan.Width, scan.Height, words);

        PdfScanTextWriter.Verify(analysis, PdfScanTextWriter.BundledFaces()[0]);

        Assert.False(analysis.Words.Single(word => word.Word.Text == "Fach").Replaceable);
        Assert.All(analysis.Words.Where(word => word.Word.Text != "Fach"),
            word => Assert.True(word.Replaceable, word.Word.Text));
    }

    [Theory]
    [InlineData("Times-Roman", 0, "she will fill the cups", "fill", "fiil")]
    [InlineData("Times-Roman", 0, "a little kitten slept", "little", "iittle")]
    [InlineData("Helvetica", 1, "she will fill the cups", "fill", "fiil")]
    [InlineData("Helvetica", 1, "the soft fabric felt warm", "felt", "telt")]
    public void ALookalikeLetterInALongerWordStaysInTheImage(string fontName, int faceIndex,
        string line, string printed, string misread)
    {
        Scan scan = Render(PdfStandardFontSubstitutes.Find(fontName), line, "with a few more words");
        PdfScanTextFace face = PdfScanTextWriter.BundledFaces()[faceIndex];
        List<PdfOcrPixelWord> words = [.. scan.Words.Select(word =>
            word.Text == printed ? word with { Text = misread } : word)];
        PdfScanTextPageAnalysis misreadPage = PdfScanTextAnalyzer.Analyze(
            scan.Pixels, scan.Width, scan.Height, words);
        PdfScanTextPageAnalysis readPage = PdfScanTextAnalyzer.Analyze(
            scan.Pixels, scan.Width, scan.Height, scan.Words);

        PdfScanTextWriter.Verify(misreadPage, face);
        PdfScanTextWriter.Verify(readPage, face);

        Assert.False(misreadPage.Words.Single(word => word.Word.Text == misread).Replaceable);
        Assert.True(readPage.Words.Single(word => word.Word.Text == printed).Replaceable);
    }

    [Fact]
    public void ABracketPressedAgainstALetterStaysInTheImage()
    {
        Scan scan = Render(PdfStandardFontSubstitutes.Find("Times-Roman"),
            "TIMING CORRECTION works", "during playback");
        List<PdfOcrPixelWord> words = [.. scan.Words.Select(word =>
            word.Text == "CORRECTION" ? word with { Text = "CORRECT[ON" } : word)];

        PdfScanTextPageAnalysis analysis = PdfScanTextAnalyzer.Analyze(
            scan.Pixels, scan.Width, scan.Height, words);

        Assert.False(analysis.Words.Single(word => word.Word.Text == "CORRECT[ON").Replaceable);
        Assert.True(analysis.Words.Single(word => word.Word.Text == "TIMING").Replaceable);
    }

    [Fact]
    public void AMisreadFractionStaysInTheImage()
    {
        Scan scan = Render(PdfStandardFontSubstitutes.Find("Times-Roman"),
            "an ultra fast 3\u00BD inch disk drive", "stores complex songs in seconds");
        List<PdfOcrPixelWord> words = [.. scan.Words.Select(word =>
            word.Text == "3\u00BD" ? word with { Text = "314" } : word)];
        PdfScanTextPageAnalysis analysis = PdfScanTextAnalyzer.Analyze(
            scan.Pixels, scan.Width, scan.Height, words);

        PdfScanTextWriter.Verify(analysis, PdfScanTextWriter.BundledFaces()[0]);

        Assert.False(analysis.Words.Single(word => word.Word.Text == "314").Replaceable);
        Assert.All(analysis.Words.Where(word => word.Word.Text != "314"), word => Assert.True(word.Replaceable,
            $"{word.Word.Text} match {word.Match:0.000} shape {word.ShapeMatch:0.000} rival {word.Lookalike} {word.LookalikeMatch:0.000}"));
    }

    [Fact]
    public void ConvertedPagesShareOneFontAndDropTheScannedImages()
    {
        TrueTypeFont font = PdfStandardFontSubstitutes.Find("Times-Roman");
        Scan first = Render(font, "the first scanned page of text", "with a second line");
        Scan second = Render(font, "another page that was scanned", "and its closing words");
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(PageWidth, first.PageHeight, new PdfContentStreamBuilder().DrawImage(
                PdfImage.FromRgb(first.Width, first.Height, Rgb(first.Pixels)), 0, 0, PageWidth, first.PageHeight))
            .AddPage(PageWidth, second.PageHeight, new PdfContentStreamBuilder().DrawImage(
                PdfImage.FromRgb(second.Width, second.Height, Rgb(second.Pixels)), 0, 0, PageWidth, second.PageHeight))
            .Build());
        PdfScanTextFace face = PdfScanTextWriter.BundledFaces()[0];
        var pages = new List<PdfScanTextPage>();
        foreach ((Scan scan, int index) in new[] { (first, 0), (second, 1) })
        {
            PdfScanTextPageAnalysis analysis = Analyze(scan);
            PdfScanTextWriter.Verify(analysis, face);
            byte[] background = (byte[])scan.Pixels.Clone();
            PdfScanTextAnalyzer.Erase(background, scan.Width, scan.Height,
                analysis.Words.Where(word => word.Replaceable));
            PdfScanTextAnalyzer.ReleaseMasks(analysis);
            pages.Add(new PdfScanTextPage(index, analysis,
                PdfImage.FromRgb(scan.Width, scan.Height, Rgb(background)), face));
        }

        PdfScanTextResult result = PdfScanTextWriter.Write(document, pages, _ => font);

        PdfDocument written = PdfDocument.Open(result.Document);
        Assert.Equal(1, CountObjects(written, value => value is PdfDictionary dictionary
            && dictionary.TryGetValue(new PdfName("Type"u8), out PdfObject? type)
            && type is PdfName name && name.ValueAsLatin1() == "FontDescriptor"));
        Assert.Equal(2, CountObjects(written, value => value is PdfStream stream
            && stream.Dictionary.TryGetValue(new PdfName("Subtype"u8), out PdfObject? subtype)
            && subtype is PdfName name && name.ValueAsLatin1() == "Image"));
        var reader = new PdfPageContentReader(written);
        Assert.Contains("the first scanned page of text", reader.Read(0).Text);
        Assert.Contains("another page that was scanned", reader.Read(1).Text);
    }

    private static int CountObjects(PdfDocument document, Func<PdfObject, bool> predicate) =>
        document.CrossReferences.Keys.Count(number =>
        {
            try { return predicate(document.Resolve(number)); }
            catch (Exception exception) when (exception is FormatException or InvalidOperationException
                or KeyNotFoundException)
            {
                return false;
            }
        });

    [Fact]
    public void ErasingRemovesTheWordsInk()
    {
        Scan scan = Render(PdfStandardFontSubstitutes.Find("Helvetica"), "erase these words");
        PdfScanTextPageAnalysis analysis = Analyze(scan);
        byte[] background = (byte[])scan.Pixels.Clone();

        PdfScanTextAnalyzer.Erase(background, scan.Width, scan.Height, analysis.Words);

        Assert.True(Dark(scan.Pixels) > 500);
        Assert.Equal(0, Dark(background));
    }

    [Fact]
    public void ScannedPagesBecomeTextOverTheErasedImageAndOtherPagesStay()
    {
        TrueTypeFont font = PdfStandardFontSubstitutes.Find("Times-Roman");
        Scan scan = Render(font, "scanned words become real text", "that can be found and copied");
        byte[] rgb = Rgb(scan.Pixels);
        var vector = new PdfContentStreamBuilder().BeginText().SetFont(font, 12)
            .SetTextMatrix(1, 0, 0, 1, 20, 40).ShowUnicodeText("Typed page").EndText();
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(PageWidth, scan.PageHeight, new PdfContentStreamBuilder().DrawImage(
                PdfImage.FromRgb(scan.Width, scan.Height, rgb), 0, 0, PageWidth, scan.PageHeight))
            .AddPage(PageWidth, 100, vector)
            .Build());

        Assert.Equal(ScanThenTyped, PdfScanTextAnalyzer.ReadScannedPages(document).ToArray());

        PdfScanTextPageAnalysis analysis = Analyze(scan);
        PdfScanTextFace face = PdfScanTextWriter.ChooseFace(analysis, PdfScanTextWriter.BundledFaces());
        PdfScanTextWriter.Verify(analysis, face);
        byte[] background = (byte[])scan.Pixels.Clone();
        PdfScanTextAnalyzer.Erase(background, scan.Width, scan.Height,
            analysis.Words.Where(word => word.Replaceable && PdfScanTextWriter.CanSet(face, word)));
        PdfScanTextAnalyzer.ReleaseMasks(analysis);
        PdfScanTextResult result = PdfScanTextWriter.Write(document,
            [new PdfScanTextPage(0, analysis, PdfImage.FromRgb(scan.Width, scan.Height, Rgb(background)), face)],
            _ => font);

        Assert.InRange(result.ReplacedWords, scan.Words.Count - 2, scan.Words.Count);
        Assert.Equal(scan.Words.Count, result.ReplacedWords + result.SearchableWords);
        PdfDocument written = PdfDocument.Open(result.Document);
        var reader = new PdfPageContentReader(written);
        PdfPageContent page = reader.Read(0);
        Assert.Contains("scanned words become real text", page.Text);
        Assert.Contains("that can be found and copied", page.Text);
        Assert.Single(page.Images);
        Assert.Contains("Typed page", reader.Read(1).Text);
        Assert.Equal(NoScans, PdfScanTextAnalyzer.ReadScannedPages(written).ToArray());

        // The rewritten page still looks like the scan: the text lands where the ink was.
        byte[] rendered = new PdfPageRenderer(written)
            .Render(0, new PdfRenderOptions(scan.Width, scan.Height)).Pixels.ToArray();
        Assert.InRange(Overlap(scan.Pixels, rendered), 0.6, 1.0);
    }

    // Text set in the very face that printed the scan has to land on the scan's own ink, letter
    // for letter: the same size, the same width and the same weight, give or take a pixel.
    [Theory]
    [InlineData("Times-Roman", 0)]
    [InlineData("Helvetica", 1)]
    public void TextInThePrintingFaceCoversTheScansInk(string fontName, int faceIndex)
    {
        TrueTypeFont font = PdfStandardFontSubstitutes.Find(fontName);
        Scan scan = Render(font, "scanned words become real text", "that can be found and copied");
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(PageWidth, scan.PageHeight, new PdfContentStreamBuilder().DrawImage(
                PdfImage.FromRgb(scan.Width, scan.Height, Rgb(scan.Pixels)), 0, 0, PageWidth, scan.PageHeight))
            .Build());
        PdfScanTextPageAnalysis analysis = Analyze(scan);
        PdfScanTextFace face = PdfScanTextWriter.RankFaces(analysis, [PdfScanTextWriter.BundledFaces()[faceIndex]])[0].Face;
        PdfScanTextWriter.Verify(analysis, face);
        Assert.All(analysis.Words, word => Assert.True(word.Replaceable,
            $"{word.Word.Text} match {word.Match:0.000} shape {word.ShapeMatch:0.000} rival {word.Lookalike} {word.LookalikeMatch:0.000}"));
        byte[] background = (byte[])scan.Pixels.Clone();
        PdfScanTextAnalyzer.Erase(background, scan.Width, scan.Height,
            analysis.Words.Where(word => word.Replaceable && PdfScanTextWriter.CanSet(face, word)));
        PdfScanTextAnalyzer.ReleaseMasks(analysis);

        PdfScanTextResult result = PdfScanTextWriter.Write(document,
            [new PdfScanTextPage(0, analysis, PdfImage.FromRgb(scan.Width, scan.Height, Rgb(background)), face)],
            _ => font);

        Assert.Equal(scan.Words.Count, result.ReplacedWords);
        byte[] rendered = new PdfPageRenderer(PdfDocument.Open(result.Document))
            .Render(0, new PdfRenderOptions(scan.Width, scan.Height)).Pixels.ToArray();
        Assert.InRange(Covered(rendered, scan.Pixels, scan.Width), 0.95, 1.0);
        Assert.InRange(Covered(scan.Pixels, rendered, scan.Width), 0.95, 1.0);
    }

    [Fact]
    public void ImagePagesWithAnInvisibleTextLayerAreStillScans()
    {
        TrueTypeFont font = PdfStandardFontSubstitutes.Find("Helvetica");
        byte[] white = [.. Enumerable.Repeat((byte)255, 30 * 30 * 3)];
        var content = new PdfContentStreamBuilder()
            .DrawImage(PdfImage.FromRgb(30, 30, white), 0, 0, 200, 200)
            .BeginText().SetTextRenderingMode(PdfTextRenderingMode.Invisible).SetFont(font, 10)
            .SetTextMatrix(1, 0, 0, 1, 10, 10).ShowUnicodeText("hidden").EndText();
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(200, 200, content).Build());

        Assert.Equal(OneScan, PdfScanTextAnalyzer.ReadScannedPages(document).ToArray());
    }

    private sealed record Scan(byte[] Pixels, int Width, int Height, double PageHeight,
        List<PdfOcrPixelWord> Words, double[] Baselines);

    private static PdfScanTextPageAnalysis Analyze(Scan scan) =>
        PdfScanTextAnalyzer.Analyze(scan.Pixels, scan.Width, scan.Height, scan.Words);

    // Renders lines of text as a page image and returns recognizer-style word boxes, from the
    // font's ascender to its descender, as an OCR engine would report them.
    private static Scan Render(TrueTypeFont font, params string[] lines)
    {
        double leading = Size * 1.8;
        double pageHeight = 40 + lines.Length * leading;
        var content = new PdfContentStreamBuilder().BeginText();
        var words = new List<PdfOcrPixelWord>();
        double[] baselines = new double[lines.Length];
        double space = font.GetPdfAdvanceWidth(font.GetGlyphId(' ')) * Size / 1000;
        for (int line = 0; line < lines.Length; line++)
        {
            double baseline = pageHeight - 30 - line * leading;
            baselines[line] = (pageHeight - baseline) * Scale;
            double x = 20;
            foreach (string text in lines[line].Split(' '))
            {
                double advance = text.Sum(c => (double)font.GetPdfAdvanceWidth(font.GetGlyphId(c))) * Size / 1000;
                content.SetFont(font, Size).SetTextMatrix(1, 0, 0, 1, x, baseline).ShowUnicodeText(text);
                words.Add(new PdfOcrPixelWord(text, 0.95f,
                    (int)Math.Floor(x * Scale),
                    (int)Math.Floor((pageHeight - baseline - font.Ascender * Size / 1000) * Scale),
                    (int)Math.Ceiling((x + advance) * Scale),
                    (int)Math.Ceiling((pageHeight - baseline - font.Descender * Size / 1000) * Scale)));
                x += advance + space;
            }
        }
        content.EndText();
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(PageWidth, pageHeight, content).Build());
        int width = (int)(PageWidth * Scale), height = (int)Math.Round(pageHeight * Scale);
        byte[] pixels = new PdfPageRenderer(document)
            .Render(0, new PdfRenderOptions(width, height)).Pixels.ToArray();
        return new Scan(pixels, width, height, pageHeight, words, baselines);
    }

    private static byte[] Rgb(byte[] bgra)
    {
        byte[] rgb = new byte[bgra.Length / 4 * 3];
        for (int i = 0, j = 0; i < bgra.Length; i += 4, j += 3)
        {
            rgb[j] = bgra[i + 2];
            rgb[j + 1] = bgra[i + 1];
            rgb[j + 2] = bgra[i];
        }
        return rgb;
    }

    private static int Dark(byte[] bgra)
    {
        int count = 0;
        for (int i = 0; i < bgra.Length; i += 4)
            if (bgra[i + 1] < 160) count++;
        return count;
    }

    // Share of one image's dark pixels that have a dark pixel within a pixel, either way, in the other.
    private static double Covered(byte[] from, byte[] to, int width)
    {
        int height = from.Length / 4 / width, dark = 0, near = 0;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                if (from[(y * width + x) * 4 + 1] >= 128) continue;
                dark++;
                bool hit = false;
                for (int dy = -1; dy <= 1 && !hit; dy++)
                    for (int dx = -1; dx <= 1 && !hit; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        hit = (uint)nx < (uint)width && (uint)ny < (uint)height && to[(ny * width + nx) * 4 + 1] < 128;
                    }
                if (hit) near++;
            }
        return dark == 0 ? 0 : near / (double)dark;
    }

    // Share of the scan's dark pixels that are also dark, give or take a pixel, in the rendering.
    private static double Overlap(byte[] scan, byte[] rendered)
    {
        int dark = 0, kept = 0;
        for (int i = 0; i < scan.Length; i += 4)
        {
            if (scan[i + 1] >= 128) continue;
            dark++;
            if (rendered[i + 1] < 160 || i >= 4 && rendered[i - 3] < 160
                || i + 4 < rendered.Length && rendered[i + 5] < 160) kept++;
        }
        return dark == 0 ? 0 : kept / (double)dark;
    }
}
