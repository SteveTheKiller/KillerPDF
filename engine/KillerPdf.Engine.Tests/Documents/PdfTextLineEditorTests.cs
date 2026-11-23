using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Parsing;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Tests.Fonts;
using System.Text;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfTextLineEditorTests
{
    private const string Helvetica = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>";

    [Fact]
    public void RewritesTheLineInItsOwnFontWithoutCoveringIt()
    {
        PdfDocument document = PdfDocument.Open(SinglePage(
            "BT /F1 12 Tf 0 0 0.5 rg 72 700 Td (Hello OLD world) Tj 0 -20 Td (Next line) Tj ET"));
        PdfContentBounds region = Region(document, "OLD");
        PdfExtractedLetter firstOld = Letter(document, "O");

        PdfTextLineEditResult result = PdfTextLineEditor.Replace(document, new PdfTextLineEdit(0, region, "NEWER"));

        Assert.Equal(PdfTextLineFont.Original, result.Font);
        Assert.Equal(3, result.RemovedGlyphs);
        PdfDocument output = PdfDocument.Open(result.Document);
        Assert.True(output.CrossReferences.Sections.Count > 1, "the change is an incremental update");
        string letters = Letters(output);
        Assert.DoesNotContain("OLD", letters);
        Assert.Contains("NEWER", letters);
        // The new text starts where the old text started, and everything else keeps its place.
        PdfExtractedLetter firstNew = Letter(output, "N");
        Assert.Equal(firstOld.StartBaseLine.X, firstNew.StartBaseLine.X, 3);
        Assert.Equal(firstOld.StartBaseLine.Y, firstNew.StartBaseLine.Y, 3);
        Assert.Equal(12, firstNew.PointSize, 3);
        AssertOthersUnmoved(document, output, region, "NEWER");
        // Nothing is painted over the page: no rectangle, no path, no image.
        IReadOnlyList<PdfContentInstruction> content = new PdfPageContentReader(output).ReadInstructions(0);
        Assert.DoesNotContain(content, instruction => instruction.Operator is "re" or "f" or "S" or "Do");
        Assert.Single(content, instruction => instruction.Operator == "rg");
    }

    [Fact]
    public void ChangesSizeColorAndUnderlineOnlyForTheNewText()
    {
        PdfDocument document = PdfDocument.Open(SinglePage(
            "BT /F1 12 Tf 72 700 Td (Price OLD here) Tj 0 -20 Td (Next line) Tj ET"));
        PdfContentBounds region = Region(document, "OLD");

        PdfTextLineEditResult result = PdfTextLineEditor.Replace(document, new PdfTextLineEdit(0, region, "NEW")
        {
            FontSize = 24,
            FillColor = new PdfRgbColor(1, 0, 0),
            Underline = true
        });

        PdfDocument output = PdfDocument.Open(result.Document);
        Assert.Equal(24, Letter(output, "N").PointSize, 3);
        Assert.Equal(12, Letter(output, "x").PointSize, 3);
        AssertOthersUnmoved(document, output, region, "NEW");
        IReadOnlyList<PdfContentInstruction> content = new PdfPageContentReader(output).ReadInstructions(0);
        int red = IndexOf(content, instruction => instruction.Operator == "rg" && Number(instruction.Operands[0]) == 1);
        int restored = IndexOf(content, instruction => instruction.Operator == "g" && Number(instruction.Operands[0]) == 0);
        int end = IndexOf(content, instruction => instruction.Operator == "ET");
        int stroke = IndexOf(content, instruction => instruction.Operator == "S");
        Assert.True(red >= 0 && restored > red && end > restored, "the color is set for the new text and put back after it");
        Assert.True(stroke > end, "the underline is drawn after the text object");
    }

    [Fact]
    public void BreaksLinesBelowTheFirstAndKeepsTheRestInPlace()
    {
        PdfDocument document = PdfDocument.Open(SinglePage(
            "BT /F1 10 Tf 72 700 Td (Address OLD) Tj ET BT /F1 10 Tf 72 600 Td (Footer) Tj ET"));
        PdfContentBounds region = Region(document, "OLD");

        PdfDocument output = PdfDocument.Open(PdfTextLineEditor.Replace(document,
            new PdfTextLineEdit(0, region, "One\nTwo")).Document);

        PdfExtractedLetter one = Letter(output, "O");
        PdfExtractedLetter two = Letter(output, "T");
        Assert.Equal(one.StartBaseLine.X, two.StartBaseLine.X, 3);
        Assert.Equal(one.StartBaseLine.Y - 12, two.StartBaseLine.Y, 3);
        AssertOthersUnmoved(document, output, region, "OneTwo");
    }

    [Fact]
    public void UsesTheSubstituteOnlyWhenTheEmbeddedFontLacksAGlyph()
    {
        PdfDocument document = PdfDocument.Open(Type3Page("BT /T3 12 Tf 72 700 Td (BB) Tj ET"));
        var area = new PdfContentBounds(60, 690, 120, 720);
        TrueTypeFont substitute = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(format12: false));

        PdfTextLineEditResult kept = PdfTextLineEditor.Replace(document,
            new PdfTextLineEdit(0, area, "B") { Substitute = substitute });
        PdfTextLineEditResult substituted = PdfTextLineEditor.Replace(document,
            new PdfTextLineEdit(0, area, "A") { Substitute = substitute });

        Assert.Equal(PdfTextLineFont.Original, kept.Font);
        Assert.Equal("B", Letters(PdfDocument.Open(kept.Document)));
        Assert.Equal(PdfTextLineFont.Substitute, substituted.Font);
        Assert.Equal("A", Letters(PdfDocument.Open(substituted.Document)));
        Assert.Throws<NotSupportedException>(() => PdfTextLineEditor.Replace(document,
            new PdfTextLineEdit(0, area, "A")));
    }

    // Bitmap fonts from dvips draw at sizes like 0.12, which another font would take literally.
    [Fact]
    public void AType3FontWithItsOwnGlyphSpaceHandsNoSizeToASubstitute()
    {
        byte[] page = Type3Page("BT /T3 1.2 Tf 72 700 Td (BB) Tj ET");
        PdfDocument document = PdfDocument.Open(Encoding.Latin1.GetBytes(Encoding.Latin1.GetString(page)
            .Replace("/FontMatrix [0.001 0 0 0.001 0 0]", "/FontMatrix [0.010 0 0 0.010 0 0]", StringComparison.Ordinal)));
        var area = new PdfContentBounds(60, 690, 120, 720);

        Assert.Throws<NotSupportedException>(() => PdfTextLineEditor.Replace(document, new PdfTextLineEdit(0, area, "A")
        {
            Substitute = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(format12: false))
        }));
        Assert.Equal("B", Letters(PdfDocument.Open(PdfTextLineEditor.Replace(document, new PdfTextLineEdit(0, area, "B")).Document)));
    }

    [Fact]
    public void ChangesTextInsideAFormOnlyForThatPage()
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /XObject << /Fm1 6 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /XObject << /Fm1 6 0 R >> >> /Contents 5 0 R >>",
            Stream("q 1 0 0 1 100 500 cm /Fm1 Do Q"),
            Stream("BT /F1 12 Tf 10 10 Td (Shared OLD text) Tj ET",
                "/Type /XObject /Subtype /Form /BBox [0 0 400 100] /Resources << /Font << /F1 7 0 R >> >>"),
            Helvetica
        };
        PdfDocument document = PdfDocument.Open(CreatePdf(objects));
        PdfContentBounds region = Region(document, "OLD");

        PdfDocument output = PdfDocument.Open(PdfTextLineEditor.Replace(document,
            new PdfTextLineEdit(0, region, "NEW")).Document);

        Assert.Contains("NEW", Letters(output));
        Assert.DoesNotContain("OLD", Letters(output));
        Assert.Contains("OLD", Letters(output, 1));
        AssertOthersUnmoved(document, output, region, "NEW");
    }

    // #431: a Word export draws an empty paragraph's smaller space just under a title, before it. The
    // space rides the line but the title's own face and size give the line its look.
    [Fact]
    public void AStraySmallerSpaceDoesNotGiveTheLineItsLook()
    {
        PdfDocument document = PdfDocument.Open(TwoFontPage(
            "BT /F1 10 Tf 72 697 Td ( ) Tj ET BT /F2 12 Tf 72 700 Td (Fiberglass) Tj ET"));

        PdfTextLineInspection title = PdfTextLineEditor.Inspect(document, 0, Region(document, "Fiberglass"));

        Assert.True(title.CanEdit);
        Assert.Equal("Times-Bold", title.FontName);
        Assert.Equal(12, title.FontSize, 3);
    }

    // #431: Word exports name their fonts CIDFont+F1 and CIDFont+F2, so only the descriptor tells the
    // bold face from the regular one.
    [Fact]
    public void InspectionReadsBoldAndItalicFromTheDescriptor()
    {
        PdfDocument document = PdfDocument.Open(CreatePdf([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R /F2 6 0 R >> >> /Contents 5 0 R >>",
            Helvetica,
            Stream("BT /F1 12 Tf 72 700 Td (Plain) Tj ET BT /F2 12 Tf 72 600 Td (Heavy) Tj ET"),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding /FontDescriptor 7 0 R >>",
            "<< /Type /FontDescriptor /FontName /Helvetica /Flags 262208 /ItalicAngle -12 /FontBBox [0 0 1000 1000] >>"
        ]));

        PdfTextLineInspection plain = PdfTextLineEditor.Inspect(document, 0, Region(document, "Plain"));
        PdfTextLineInspection heavy = PdfTextLineEditor.Inspect(document, 0, Region(document, "Heavy"));

        Assert.False(plain.Bold);
        Assert.False(plain.Italic);
        Assert.True(heavy.Bold);
        Assert.True(heavy.Italic);
    }

    // #431: the embedded program still names the face (Arial-BoldMT) when the resource name does not.
    [Fact]
    public void InspectionReportsTheNameTheEmbeddedProgramGivesItself()
    {
        string program = Encoding.Latin1.GetString(KillerPdf.Engine.Tests.Fonts.TrueTypeFontTests.BuildTestFont(format12: false));
        PdfDocument document = PdfDocument.Open(CreatePdf([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R /F2 6 0 R >> >> /Contents 5 0 R >>",
            Helvetica,
            Stream("BT /F1 12 Tf 72 700 Td (Plain) Tj ET BT /F2 12 Tf 72 600 Td (AB) Tj ET"),
            "<< /Type /Font /Subtype /TrueType /BaseFont /CIDFont+F2 /Encoding /WinAnsiEncoding /FirstChar 65 /LastChar 66 /Widths [500 500] /FontDescriptor 7 0 R >>",
            "<< /Type /FontDescriptor /FontName /CIDFont+F2 /Flags 32 /FontBBox [0 0 1000 1000] /ItalicAngle 0 /Ascent 800 /Descent -200 /CapHeight 700 /StemV 80 /FontFile2 8 0 R >>",
            Stream(program, $"/Length1 {program.Length}")
        ]));

        PdfTextLineInspection plain = PdfTextLineEditor.Inspect(document, 0, Region(document, "Plain"));
        PdfTextLineInspection embedded = PdfTextLineEditor.Inspect(document, 0, Region(document, "AB"));

        Assert.Equal("", plain.ProgramName);
        Assert.Equal("CIDFont+F2", embedded.FontName);
        Assert.Equal("KillerTest", embedded.ProgramName);
    }

    [Fact]
    public void InspectionReportsWhatCannotBeRewritten()
    {
        PdfDocument document = PdfDocument.Open(SinglePage(
            "BT /F1 12 Tf 72 700 Td (Visible) Tj ET BT 3 Tr /F1 12 Tf 72 600 Td (Hidden) Tj ET"));

        PdfTextLineInspection visible = PdfTextLineEditor.Inspect(document, 0, Region(document, "Visible"));
        PdfTextLineInspection hidden = PdfTextLineEditor.Inspect(document, 0, Region(document, "Hidden"));
        PdfTextLineInspection empty = PdfTextLineEditor.Inspect(document, 0, new PdfContentBounds(300, 100, 400, 150));

        Assert.True(visible.CanEdit);
        Assert.Equal(7, visible.Glyphs);
        Assert.Equal("Helvetica", visible.FontName);
        Assert.Equal(12, visible.FontSize, 3);
        Assert.Equal(PdfTextLineBlocker.InvisibleText, hidden.Blocker);
        Assert.Equal(PdfTextLineBlocker.NoText, empty.Blocker);
        Assert.Throws<NotSupportedException>(() => PdfTextLineEditor.Replace(document,
            new PdfTextLineEdit(0, Region(document, "Hidden"), "Shown")));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfTextLineEditor.Replace(document,
            new PdfTextLineEdit(0, Region(document, "Visible"), "x") { FontSize = 0 }));
    }

    // The way Word writes a highlighted word: its background is painted between the pieces of the line.
    private const string Highlighted = "BT /F1 10 Tf 30 700 Td (Plain words then ) Tj ET 1 1 0 rg 110 697 60 12 re f 0 g " +
        "BT /F1 10 Tf 110 700 Td (highlight) Tj ET BT /F1 10 Tf 170 700 Td ( and rest) Tj ET";

    [Fact]
    public void ContentPaintedBetweenThePiecesOfALineStaysUnderTheNewText()
    {
        PdfDocument document = PdfDocument.Open(SinglePage(Highlighted));
        var region = new PdfContentBounds(28, 695, 215, 712);
        int before = Dark(document, 112, 695, 168, 712);
        Assert.True(before > 100, "the fixture shows dark text over the highlight");

        PdfDocument output = PdfDocument.Open(PdfTextLineEditor.Replace(document,
            new PdfTextLineEdit(0, region, "Plain words then highlight and rest")).Document);

        Assert.True(Dark(output, 112, 695, 168, 712) > before / 2, "the new text shows over the highlight");
        Assert.Contains("highlight", Letters(output));
    }

    [Fact]
    public void ASubstituteFontAlsoLandsOverContentPaintedBetweenThePieces()
    {
        PdfDocument document = PdfDocument.Open(SinglePage(Highlighted));

        PdfDocument output = PdfDocument.Open(PdfTextLineEditor.Replace(document,
            new PdfTextLineEdit(0, new PdfContentBounds(28, 695, 215, 712), "AAAA")
            {
                Substitute = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(format12: false)),
                UseSubstitute = true
            }).Document);

        IReadOnlyList<PdfContentInstruction> content = new PdfPageContentReader(output).ReadInstructions(0);
        int highlight = IndexOf(content, instruction => instruction.Operator == "f");
        int written = IndexOf(content, instruction => instruction.Operator == "Tf"
            && instruction.Operands[0] is PdfName name && name.ValueAsLatin1().StartsWith("KPEF", StringComparison.Ordinal));
        Assert.True(highlight >= 0 && written > highlight, "the new text is written after the highlight");
    }

    [Fact]
    public void ALineThatChangesLookMidwayIsWrittenInItsFirstGlyphsLook()
    {
        PdfDocument document = PdfDocument.Open(TwoFontPage(
            "BT /F1 10 Tf 1 0 0 rg 72 700 Td (First ) Tj ET " +
            "BT /F2 14 Tf 0 0 1 rg 2 Tc 3 Tw 120 Tz 2 Ts 110 700 Td (second) Tj ET " +
            "BT 300 700 Td (Tail) Tj ET"));
        var region = new PdfContentBounds(70, 695, 200, 715);
        PdfExtractedLetter tail = Letter(document, "T");

        PdfDocument output = PdfDocument.Open(PdfTextLineEditor.Replace(document,
            new PdfTextLineEdit(0, region, "Edited line")).Document);

        PdfExtractedLetter written = Letter(output, "E");
        Assert.Equal(72, written.StartBaseLine.X, 3);
        Assert.Equal(700, written.StartBaseLine.Y, 3);
        Assert.Equal(10, written.PointSize, 3);
        Assert.Contains("Helvetica", written.FontName, StringComparison.Ordinal);
        // The spacing of the last piece stays out of the new text.
        PdfExtractedLetter next = Letter(output, "d");
        Assert.Equal(written.EndBaseLine.X, next.StartBaseLine.X, 3);
        Assert.True(Ink(output, 72, 695, 130, 715, (red, green, blue) => red > 150 && blue < 100) > 20, "the new text keeps the first fill");
        Assert.Equal(0, Ink(output, 72, 695, 130, 715, IsBlue));
        // What follows keeps the state it had before the edit.
        PdfExtractedLetter tailAfter = Letter(output, "T");
        Assert.Equal(tail.StartBaseLine.X, tailAfter.StartBaseLine.X, 3);
        Assert.Equal(tail.PointSize, tailAfter.PointSize, 3);
        Assert.Equal(tail.FontName, tailAfter.FontName);
        Assert.True(Ink(output, 295, 695, 350, 725, IsBlue) > 20, "the tail stays blue");
    }

    [Fact]
    public void ALineDrawnUnderTwoTransformationsKeepsItsFirstPlaceAndSize()
    {
        PdfDocument document = PdfDocument.Open(SinglePage(
            "q 1 0 0 1 72 700 cm BT /F1 10 Tf (Left ) Tj ET Q q 2 0 0 2 100 700 cm BT /F1 5 Tf (right) Tj ET Q"));

        PdfDocument output = PdfDocument.Open(PdfTextLineEditor.Replace(document,
            new PdfTextLineEdit(0, new PdfContentBounds(70, 695, 130, 712), "Edited")).Document);

        PdfExtractedLetter written = Letter(output, "E");
        Assert.Equal(72, written.StartBaseLine.X, 3);
        Assert.Equal(700, written.StartBaseLine.Y, 3);
        Assert.Equal(10, written.PointSize, 3);
    }

    // Browsers give each piece of a line a clip of its own, and no piece's clip holds the whole line.
    [Fact]
    public void ALineWhosePiecesHaveTheirOwnClipsIsNotCutOffByTheLastOne()
    {
        PdfDocument document = PdfDocument.Open(SinglePage(
            "0 0 1 rg q 30 690 60 30 re W n q 1 0 0 1 32 700 cm BT /F1 10 Tf (Left piece of) Tj ET Q Q " +
            "q 90 690 80 30 re W n 0 1 0 rg q 1 0 0 1 92 700 cm BT /F1 10 Tf (the right piece) Tj ET Q Q"));
        var region = new PdfContentBounds(30, 695, 170, 712);
        int left = Ink(document, 32, 695, 88, 712, IsBlue), right = Ink(document, 92, 695, 165, 712, (red, green, blue) => green > 150 && red < 100);

        PdfDocument output = PdfDocument.Open(PdfTextLineEditor.Replace(document,
            new PdfTextLineEdit(0, region, "Left piece of the right piece")).Document);

        Assert.Contains("rightpiece", Letters(output).Replace(" ", "", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Equal(32, Letter(output, "L").StartBaseLine.X, 3);
        Assert.True(Ink(output, 32, 695, 88, 712, IsBlue) > left / 2, "the text shows inside the first clip");
        Assert.True(Ink(output, 92, 695, 165, 712, IsBlue) > right / 2, "and past it, in the first piece's fill");
    }

    [Fact]
    public void AnUnderlineFollowsTheNewTextPastThePiecesClips()
    {
        PdfDocument document = PdfDocument.Open(SinglePage(
            "q 30 690 60 30 re W n BT /F1 10 Tf 32 700 Td (Left piece of) Tj ET Q " +
            "q 90 690 80 30 re W n BT /F1 10 Tf 92 700 Td (the right piece) Tj ET Q BT /F1 10 Tf 32 650 Td (Below) Tj ET"));
        PdfExtractedLetter below = Letter(document, "B");

        PdfDocument output = PdfDocument.Open(PdfTextLineEditor.Replace(document,
            new PdfTextLineEdit(0, new PdfContentBounds(30, 695, 170, 712), "Left piece of the right piece") { Underline = true }).Document);

        Assert.Contains(new PdfPageContentReader(output).Read(0).Paths, path => !path.IsClippingPath
            && path.BoundingBox.Left < 40 && path.BoundingBox.Right > 140 && Math.Abs(path.BoundingBox.Bottom - 698.8) < 0.5);
        Assert.Equal(below.StartBaseLine.X, Letter(output, "B").StartBaseLine.X, 3);
        Assert.Equal(10, Letter(output, "B").PointSize, 3);
    }

    [Fact]
    public void ALineTooLongForItsClipIsRefused()
    {
        PdfDocument document = PdfDocument.Open(SinglePage(
            "q 30 690 60 30 re W n BT /F1 10 Tf 32 700 Td (Short) Tj ET BT /F1 10 Tf 60 700 Td (cell) Tj ET Q"));

        var error = Assert.Throws<NotSupportedException>(() => PdfTextLineEditor.Replace(document,
            new PdfTextLineEdit(0, new PdfContentBounds(30, 695, 90, 712), "A much longer line of words")));

        Assert.Contains("cut off", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("paragraph", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ALineThatWouldRunOffThePageIsRefused()
    {
        PdfDocument document = PdfDocument.Open(SinglePage("BT /F1 10 Tf 560 700 Td (Short) Tj ET"));

        var error = Assert.Throws<NotSupportedException>(() => PdfTextLineEditor.Replace(document,
            new PdfTextLineEdit(0, Region(document, "Short"), "A much longer line of words")));

        Assert.Contains("cut off", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AShapePaintedLaterOverTheNewTextIsRefused()
    {
        PdfDocument document = PdfDocument.Open(SinglePage(
            "BT /F1 10 Tf 72 700 Td (Short) Tj ET 0.5 g 110 690 200 30 re f"));

        var error = Assert.Throws<NotSupportedException>(() => PdfTextLineEditor.Replace(document,
            new PdfTextLineEdit(0, Region(document, "Short"), "A much longer line of words")));

        Assert.Contains("cover", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("paragraph", error.Message, StringComparison.OrdinalIgnoreCase);
        // The same line, short enough to stay clear of the shape.
        Assert.Contains("Brief", Letters(PdfDocument.Open(PdfTextLineEditor.Replace(document,
            new PdfTextLineEdit(0, Region(document, "Short"), "Brief")).Document)));
    }

    // A translucent highlight or a stamp laid over the page lets the old text show, and the new text too.
    [Fact]
    public void AShapeAlreadyOverTheOldTextDoesNotStopTheEdit()
    {
        PdfDocument document = PdfDocument.Open(SinglePage("BT /F1 10 Tf 72 700 Td (Some words here) Tj ET 1 1 0 rg 60 690 200 30 re f"));

        Assert.Contains("Other", Letters(PdfDocument.Open(PdfTextLineEditor.Replace(document,
            new PdfTextLineEdit(0, Region(document, "Some words here"), "Other")).Document)));
    }

    [Fact]
    public void ANeighborsLettersDoNotStopALongerLine()
    {
        PdfDocument document = PdfDocument.Open(SinglePage(
            "BT /F1 10 Tf 72 700 Td (Short) Tj ET BT /F1 10 Tf 120 700 Td (next) Tj ET"));

        Assert.Contains("next", Letters(PdfDocument.Open(PdfTextLineEditor.Replace(document,
            new PdfTextLineEdit(0, Region(document, "Short"), "A longer line of words")).Document)));
    }

    // The copy painted last is the text the reader sees, and the shadow under it leaves with it.
    [Fact]
    public void AShadowLeavesWithItsLineAndLendsItNothing()
    {
        PdfDocument document = PdfDocument.Open(SinglePage(
            "0.8 g BT /F1 10 Tf 72.6 699.4 Td (Shadowed line) Tj ET 0 g BT /F1 10 Tf 72 700 Td (Shadowed line) Tj ET"));
        var region = new PdfContentBounds(70, 695, 150, 712);

        PdfDocument output = PdfDocument.Open(PdfTextLineEditor.Replace(document,
            new PdfTextLineEdit(0, region, "New words")).Document);

        Assert.Equal("New words", Letters(output));
        Assert.Equal(72, Letter(output, "N").StartBaseLine.X, 3);
        Assert.Equal(700, Letter(output, "N").StartBaseLine.Y, 3);
        Assert.True(Dark(output, 70, 695, 150, 712) > 100, "the new text is black like the top copy");
    }

    // A thin rule crosses the text without hiding it.
    [Theory]
    [InlineData("0 G 0.5 w 72 703 m 140 703 l S")]
    [InlineData("0 g 60 703 200 0.4 re f")]
    [InlineData("0 G 0.5 w 90 690 m 90 720 l S")]
    public void AThinRulePaintedLaterDoesNotStopTheEdit(string rule)
    {
        PdfDocument document = PdfDocument.Open(SinglePage($"BT /F1 10 Tf 72 700 Td (Some words) Tj ET {rule}"));

        Assert.Contains("Other", Letters(PdfDocument.Open(PdfTextLineEditor.Replace(document,
            new PdfTextLineEdit(0, Region(document, "Some words"), "Other text")).Document)));
    }

    [Fact]
    public void EveryNewRowIsCheckedAndUsesTheFirstGlyphsLeading()
    {
        PdfDocument covered = PdfDocument.Open(SinglePage(
            "BT /F1 10 Tf 72 700 Td (Address) Tj ET 0.5 g 60 680 200 10 re f"));
        PdfDocument clear = PdfDocument.Open(TwoFontPage(
            "BT /F1 10 Tf 72 700 Td (Ad) Tj ET BT /F2 12 Tf 8 TL 85 700 Td (dress) Tj ET"));

        Assert.Throws<NotSupportedException>(() => PdfTextLineEditor.Replace(covered,
            new PdfTextLineEdit(0, Region(covered, "Address"), "One\nTwo")));
        PdfDocument output = PdfDocument.Open(PdfTextLineEditor.Replace(clear,
            new PdfTextLineEdit(0, new PdfContentBounds(70, 695, 140, 712), "One\nTwo")).Document);

        Assert.Equal(72, Letter(output, "T").StartBaseLine.X, 3);
        Assert.Equal(688, Letter(output, "T").StartBaseLine.Y, 3);
    }

    private const string Numeral = "BT /F1 60 Tf 0.75 g 90 680 Td (13) Tj ET BT /F1 10 Tf 0 g 60 700 Td (Certificate number:) Tj ET";

    [Fact]
    public void InspectionCountsTheLineAndNotTextBehindIt()
    {
        PdfDocument document = PdfDocument.Open(SinglePage(Numeral));

        PdfTextLineInspection inspection = PdfTextLineEditor.Inspect(document, 0, Inflated(Region(document, "Certificate number:")));

        Assert.True(inspection.CanEdit);
        Assert.Equal(19, inspection.Glyphs);
        Assert.Equal(10, inspection.FontSize, 3);
    }

    [Fact]
    public void ALargeNumeralBehindTheLineStaysAndLendsItNothing()
    {
        PdfDocument document = PdfDocument.Open(SinglePage(Numeral));

        PdfTextLineEditResult result = PdfTextLineEditor.Replace(document,
            new PdfTextLineEdit(0, Inflated(Region(document, "Certificate number:")), "Certificate no."));

        Assert.Equal(19, result.RemovedGlyphs);
        PdfDocument output = PdfDocument.Open(result.Document);
        IReadOnlyList<PdfExtractedLetter> letters = new PdfPageContentReader(output).Read(0).Letters;
        Assert.Equal("13", string.Concat(letters.Where(letter => letter.PointSize > 50).Select(letter => letter.Value)));
        PdfExtractedLetter written = Letter(output, "C");
        Assert.Equal(10, written.PointSize, 3);
        Assert.Equal(60, written.StartBaseLine.X, 3);
        Assert.Equal(700, written.StartBaseLine.Y, 3);
        Assert.True(Ink(output, 60, 695, 80, 712, (red, green, blue) => red < 100) > 20, "the new text keeps the line's black");
    }

    [Fact]
    public void ASuperscriptLeavesWithItsLine()
    {
        PdfDocument document = PdfDocument.Open(SinglePage(
            "BT /F1 10 Tf 72 700 Td (Area in m) Tj /F1 6 Tf 4 Ts (2) Tj /F1 10 Tf 0 Ts ( only) Tj ET"));
        PdfContentBounds region = Region(document, "Area in m2 only");

        PdfTextLineEditResult result = PdfTextLineEditor.Replace(document, new PdfTextLineEdit(0, region, "Edited"));

        Assert.Equal("Edited", Letters(PdfDocument.Open(result.Document)));
    }

    [Fact]
    public void TextRunningAcrossTheLineStays()
    {
        PdfDocument document = PdfDocument.Open(SinglePage(
            "BT /F1 40 Tf 0.75 0.75 -0.75 0.75 90 680 Tm (W) Tj ET BT /F1 10 Tf 0 g 1 0 0 1 72 700 Tm (Some words here) Tj ET"));

        PdfDocument output = PdfDocument.Open(PdfTextLineEditor.Replace(document,
            new PdfTextLineEdit(0, Region(document, "Some words here"), "Other")).Document);

        Assert.Equal("WOther", Letters(output));
    }

    // Underlines come as a filled box, a filled outline or a stroked line, and every one of them leaves.
    [Theory]
    [InlineData("0 g 30 247 33 .5 re f")]
    [InlineData("0 g 30 247 m 63 247 l 63 247.5 l 30 247.5 l h f")]
    [InlineData("0 g 30 247 m 63 247 l 63 247.5 l 30 247.5 l f")]
    [InlineData("0 G .5 w 30 247.2 m 63 247.2 l S")]
    [InlineData("q 1 0 0 1 30 247 cm 0 G .5 w 0 .2 m 33 .2 l S Q")]
    public void AnUnderlineLeavesWithItsParagraphWhateverItIsDrawnWith(string rule)
    {
        PdfDocument source = PdfDocument.Open(SmallPage($"BT /F1 10 Tf 1 0 0 1 30 250 Tm (Old line) Tj ET {rule}"));
        PdfParagraphPlan plan = PlanParagraph(source);

        Assert.False(plan.HasOverflow, plan.OverflowReason);
        Assert.DoesNotContain(new PdfPageContentReader(PdfDocument.Open(plan.GetPreviewDocument())).Read(0).Paths,
            path => !path.IsClippingPath);
    }

    // Two shapes painted as one are not an underline, and neither is a fat stroke.
    [Theory]
    [InlineData("0 g 30 247 m 40 247 l 40 247.5 l 30 247.5 l h 50 247 m 63 247 l 63 247.5 l 50 247.5 l h f")]
    [InlineData("0 g 30 247 10 .5 re 50 247 13 .5 re f")]
    [InlineData("0 G 3 w 30 246 m 63 246 l S")]
    public void ACompoundPathOrAThickStrokeStays(string rule)
    {
        PdfDocument source = PdfDocument.Open(SmallPage($"BT /F1 10 Tf 1 0 0 1 30 250 Tm (Old line) Tj ET {rule}"));

        Assert.Single(new PdfPageContentReader(PdfDocument.Open(PlanParagraph(source).GetPreviewDocument())).Read(0).Paths,
            path => !path.IsClippingPath);
    }

    [Fact]
    public void AnotherPageThatCannotBeReadOffersNoGlyphsAndStopsNothing()
    {
        PdfDocument document = PdfDocument.Open(CreatePdf([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /T3 5 0 R >> >> /Contents 6 0 R >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /T3 5 0 R >> >> /Contents 8 0 R >>",
            "<< /Type /Font /Subtype /Type3 /FontBBox [0 0 600 700] /FontMatrix [0.001 0 0 0.001 0 0] " +
                "/CharProcs << /B 7 0 R >> /Encoding << /Type /Encoding /Differences [66 /B] >> " +
                "/FirstChar 66 /LastChar 66 /Widths [600] /Resources << >> >>",
            Stream("BT /T3 12 Tf 72 700 Td (BB) Tj ET"),
            Stream("600 0 0 0 600 700 d1 0 0 600 700 re f"),
            Stream("not flate data", "/Filter /FlateDecode")
        ]));

        PdfTextLineEditResult result = PdfTextLineEditor.Replace(document, new PdfTextLineEdit(0, new PdfContentBounds(60, 690, 120, 720), "A")
        {
            Substitute = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(format12: false))
        });

        Assert.Equal(PdfTextLineFont.Substitute, result.Font);
        Assert.Equal("A", Letters(PdfDocument.Open(result.Document)));
    }

    private static PdfParagraphPlan PlanParagraph(PdfDocument source)
    {
        PdfParagraphCandidate candidate = Assert.Single(PdfParagraphEditor.FindParagraphs(source, 0));
        return PdfParagraphEditor.Plan(source, 0, candidate.SourceRegion, new PdfContentBounds(30, 248, 200, 258),
            "New", PdfStandardFontSubstitutes.OcrTrainingFonts()[0], 10, 12, PdfParagraphAlignment.Left);
    }

    private static PdfContentBounds Inflated(PdfContentBounds box) =>
        new(box.Left - 0.5, box.Bottom - 0.5, box.Right + 0.5, box.Top + 0.5);

    private static bool IsBlue(int red, int green, int blue) => blue > 150 && red < 100 && green < 100;

    private static int Dark(PdfDocument document, int left, int bottom, int right, int top) =>
        Ink(document, left, bottom, right, top, (red, green, blue) => red < 128 && green < 128 && blue < 128);

    // Pixels in an area of the page that pass the test, at four pixels to the point.
    private static int Ink(PdfDocument document, int left, int bottom, int right, int top, Func<int, int, int, bool> test)
    {
        const int Scale = 4;
        PdfRenderedPage page = new PdfPageRenderer(document).Render(0, new PdfRenderOptions(612 * Scale, 792 * Scale));
        ReadOnlySpan<byte> pixels = page.Pixels.Span;
        int count = 0;
        for (int y = page.Height - top * Scale; y < page.Height - bottom * Scale; y++)
            for (int x = left * Scale; x < right * Scale; x++)
            {
                int offset = (y * page.Width + x) * 4;
                if (test(pixels[offset + 2], pixels[offset + 1], pixels[offset])) count++;
            }
        return count;
    }

    private static void AssertOthersUnmoved(PdfDocument before, PdfDocument after, PdfContentBounds region, string inserted)
    {
        var expected = new PdfPageContentReader(before).Read(0).Letters
            .Where(letter => !CenterInside(letter.BoundingBox, region))
            .Select(letter => (letter.Value, Math.Round(letter.StartBaseLine.X, 3), Math.Round(letter.StartBaseLine.Y, 3)))
            .ToList();
        var actual = new PdfPageContentReader(after).Read(0).Letters
            .Select(letter => (letter.Value, Math.Round(letter.StartBaseLine.X, 3), Math.Round(letter.StartBaseLine.Y, 3)))
            .ToList();
        // The new letters are the only additions.
        foreach (char character in inserted)
        {
            int index = actual.FindIndex(item => item.Value == character.ToString()
                && !expected.Contains(item));
            Assert.True(index >= 0, $"'{character}' was not written.");
            actual.RemoveAt(index);
        }
        Assert.Equal(expected, actual);
    }

    private static bool CenterInside(PdfContentBounds box, PdfContentBounds region)
    {
        double x = (box.Left + box.Right) / 2, y = (box.Bottom + box.Top) / 2;
        return x >= region.Left && x <= region.Right && y >= region.Bottom && y <= region.Top;
    }

    private static PdfContentBounds Region(PdfDocument document, string word)
    {
        IReadOnlyList<PdfExtractedLetter> letters = new PdfPageContentReader(document).Read(0).Letters;
        string text = string.Concat(letters.Select(letter => letter.Value));
        int start = text.IndexOf(word, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{word}' was not found.");
        PdfExtractedLetter[] selected = [.. letters.Skip(start).Take(word.Length)];
        return new PdfContentBounds(
            selected.Min(letter => letter.BoundingBox.Left), selected.Min(letter => letter.BoundingBox.Bottom),
            selected.Max(letter => letter.BoundingBox.Right), selected.Max(letter => letter.BoundingBox.Top));
    }

    private static PdfExtractedLetter Letter(PdfDocument document, string value, int occurrence = 0) =>
        new PdfPageContentReader(document).Read(0).Letters.Where(letter => letter.Value == value).ElementAt(occurrence);

    private static string Letters(PdfDocument document, int pageIndex = 0) =>
        string.Concat(new PdfPageContentReader(document).Read(pageIndex).Letters.Select(letter => letter.Value));

    private static int IndexOf(IReadOnlyList<PdfContentInstruction> content, Func<PdfContentInstruction, bool> match)
    {
        for (int index = 0; index < content.Count; index++)
            if (match(content[index])) return index;
        return -1;
    }

    private static double Number(PdfObject value) => value switch
    {
        PdfInteger integer => integer.Value,
        PdfReal real => real.Value,
        _ => double.NaN
    };

    private static byte[] SinglePage(string content) =>
        CreatePdf([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            Helvetica,
            Stream(content)
        ]);

    private static byte[] TwoFontPage(string content) =>
        CreatePdf([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R /F2 6 0 R >> >> /Contents 5 0 R >>",
            Helvetica,
            Stream(content),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Times-Bold /Encoding /WinAnsiEncoding >>"
        ]);

    // The size the paragraph planner's own tests work at.
    private static byte[] SmallPage(string content) =>
        CreatePdf([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            Helvetica,
            Stream(content)
        ]);

    // A Type 3 font is embedded and holds only the glyph it defines, a B.
    private static byte[] Type3Page(string content) =>
        CreatePdf([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /T3 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type3 /FontBBox [0 0 600 700] /FontMatrix [0.001 0 0 0.001 0 0] " +
                "/CharProcs << /B 6 0 R >> /Encoding << /Type /Encoding /Differences [66 /B] >> " +
                "/FirstChar 66 /LastChar 66 /Widths [600] /Resources << >> >>",
            Stream(content),
            Stream("600 0 0 0 600 700 d1 0 0 600 700 re f")
        ]);

    private static string Stream(string content, string entries = "") =>
        $"<< {entries} /Length {Encoding.Latin1.GetByteCount(content)} >>\nstream\n{content}\nendstream";

    private static byte[] CreatePdf(List<string> objects)
    {
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int index = 0; index < objects.Count; index++)
        {
            offsets.Add(Encoding.Latin1.GetByteCount(pdf.ToString()));
            pdf.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        int xref = Encoding.Latin1.GetByteCount(pdf.ToString());
        pdf.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) pdf.Append($"{offset:0000000000} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\n").Append($"startxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(pdf.ToString());
    }
}
