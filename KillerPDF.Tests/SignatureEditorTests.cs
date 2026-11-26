using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

// The signature editor's image work (threshold, weight, feather, ink color, stroke drawing) is plain arithmetic
// on BGRA pixels, tested here directly; the wiring into the card's Edit and the picker is checked from source.
public sealed class SignatureEditorTests
{
    private static byte[] Solid(int width, int height, byte b, byte g, byte r, byte a)
    {
        var pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = b; pixels[i + 1] = g; pixels[i + 2] = r; pixels[i + 3] = a; }
        return pixels;
    }

    private static byte Alpha(byte[] pixels, int width, int x, int y) => pixels[(y * width + x) * 4 + 3];

    // One opaque dark pixel in the middle of a transparent field whose leftover color is near-white paper.
    private static byte[] Dot(int size, out int center)
    {
        var pixels = Solid(size, size, 250, 250, 250, 0);
        center = size / 2;
        int o = (center * size + center) * 4;
        pixels[o] = 10; pixels[o + 1] = 20; pixels[o + 2] = 200; pixels[o + 3] = 255;
        return pixels;
    }

    [Fact]
    public void ThresholdAlpha_DropsPaperKeepsInkAndLeavesColorsAlone()
    {
        byte[] pixels = [20, 20, 20, 255, 245, 245, 245, 255];
        byte[] result = SignatureInk.ThresholdAlpha(pixels, SignatureInk.DefaultSensitivity);

        Assert.Equal(255, result[3]);
        Assert.Equal(0, result[7]);
        Assert.Equal(pixels[..3], result[..3]);
        // Running it again over cleaned ink changes nothing: no creeping darkening on a re-edit.
        Assert.Equal(result, SignatureInk.ThresholdAlpha(result, SignatureInk.DefaultSensitivity));
    }

    [Fact]
    public void ThresholdAlpha_HigherKeepsFainterInk()
    {
        byte[] faint = [150, 150, 150, 255];
        Assert.True(SignatureInk.ThresholdAlpha(faint, SignatureInk.MaxSensitivity)[3]
            > SignatureInk.ThresholdAlpha(faint, SignatureInk.MinSensitivity)[3]);
    }

    [Fact]
    public void Weight_GrowsWithTheInksOwnColorAndThinsBackAway()
    {
        byte[] dot = Dot(7, out int c);

        byte[] grown = SignatureInk.Weight(dot, 7, 7, 1);
        Assert.Equal(255, Alpha(grown, 7, c + 1, c + 1));
        Assert.Equal(0, Alpha(grown, 7, c + 2, c));
        int o = ((c + 1) * 7 + c) * 4;
        Assert.Equal(new byte[] { 10, 20, 200 }, grown[o..(o + 3)]);   // the ink's color, not the paper's

        byte[] thinned = SignatureInk.Weight(grown, 7, 7, -1);
        Assert.Equal(255, Alpha(thinned, 7, c, c));
        Assert.Equal(0, Alpha(thinned, 7, c + 1, c));

        Assert.Equal(dot, SignatureInk.Weight(dot, 7, 7, 0));
    }

    [Fact]
    public void Weight_ThinningEatsInFromThePictureEdge()
    {
        byte[] full = Solid(3, 3, 0, 0, 0, 255);
        byte[] thinned = SignatureInk.Weight(full, 3, 3, -1);
        Assert.Equal(0, Alpha(thinned, 3, 0, 0));
        Assert.Equal(0, Alpha(thinned, 3, 2, 1));
        Assert.Equal(255, Alpha(thinned, 3, 1, 1));   // only the middle is more than one pixel from the edge
    }

    // A 16-pixel-tall navy bar across the middle of a transparent field whose leftover color is near-white paper.
    private static byte[] Bar(int width, int height, int top, int bottom, int left, int right)
    {
        var pixels = Solid(width, height, 250, 250, 250, 0);
        for (int y = top; y <= bottom; y++)
            for (int x = left; x <= right; x++)
            {
                int o = (y * width + x) * 4;
                pixels[o] = 10; pixels[o + 1] = 20; pixels[o + 2] = 200; pixels[o + 3] = 255;
            }
        return pixels;
    }

    private static int SolidRun(byte[] pixels, int width, int height, int x)
    {
        int run = 0;
        for (int y = 0; y < height; y++) if (Alpha(pixels, width, x, y) >= 128) run++;
        return run;
    }

    [Fact]
    public void Feather_KeepsAThickStrokesCoreSolidAndItsWidth()
    {
        const int w = 60, h = 40;
        byte[] bar = Bar(w, h, 12, 27, 10, 49);
        byte[] soft = SignatureInk.Feather(bar, w, h, 12);   // the widest ramp the slider reaches at 2 px per unit

        // The core is untouched: fully opaque, not blurred.
        Assert.Equal(255, Alpha(soft, w, 30, 20));
        Assert.Equal(255, Alpha(soft, w, 30, 19));
        // The edge is a ramp: pixels on both sides of the outline sit between paper and ink.
        foreach (int y in new[] { 9, 10, 11, 12, 13, 14, 25, 26, 27, 28, 29 })
            Assert.InRange(Alpha(soft, w, 30, y), (byte)1, (byte)254);
        Assert.True(Alpha(soft, w, 30, 13) > Alpha(soft, w, 30, 11));
        // The line keeps its width: the run of at-least-half pixels is the original 16, give or take one.
        Assert.InRange(SolidRun(soft, w, h, 30), SolidRun(bar, w, h, 30) - 1, SolidRun(bar, w, h, 30) + 1);
        // Every pixel that has ink carries the ink's color, never the near-white paper.
        for (int i = 0; i < soft.Length; i += 4)
            if (soft[i + 3] > 0) Assert.Equal(new byte[] { 10, 20, 200 }, soft[i..(i + 3)]);
        // Feather 0 leaves the ink exactly as it was.
        Assert.Equal(bar, SignatureInk.Feather(bar, w, h, 0));
    }

    [Fact]
    public void Feather_NeverFadesTheMiddleOfAThinLine()
    {
        const int w = 40, h = 20;
        byte[] line = Bar(w, h, 9, 11, 5, 34);   // three pixels tall
        byte[] soft = SignatureInk.Feather(line, w, h, 12);
        Assert.Equal(255, Alpha(soft, w, 20, 10));
        Assert.InRange(Alpha(soft, w, 20, 8), (byte)1, (byte)254);
        Assert.InRange(SolidRun(soft, w, h, 20), 2, 4);
        // A single dot has no room for a ramp and stays as it was.
        byte[] dot = Dot(15, out _);
        byte[] fed = SignatureInk.Feather(dot, 15, 15, 12);
        for (int i = 3; i < dot.Length; i += 4) Assert.Equal(dot[i], fed[i]);
    }

    [Fact]
    public void Recolor_PaintsTheInkAndKeepsItsShape()
    {
        byte[] pixels = [10, 20, 30, 128, 1, 2, 3, 0];
        byte[] blue = SignatureInk.Recolor(pixels, 0xFF1F4FBF);
        Assert.Equal(new byte[] { 0xBF, 0x4F, 0x1F, 128, 0xBF, 0x4F, 0x1F, 0 }, blue);
        Assert.Equal(64, SignatureInk.Recolor(pixels, 0x801F4FBF)[3]);
    }

    [Fact]
    public void RasterizeStrokes_CoversTheLineWithRoundSoftEdges()
    {
        var strokes = new List<IReadOnlyList<(double X, double Y)>> { new List<(double, double)> { (2, 5), (18, 5) } };
        byte[] pixels = SignatureInk.RasterizeStrokes(strokes, 4, 20, 10, 1);

        Assert.Equal(255, Alpha(pixels, 20, 10, 5));
        Assert.Equal(255, Alpha(pixels, 20, 10, 3));
        Assert.Equal(0, Alpha(pixels, 20, 10, 0));
        Assert.Equal(0, Alpha(pixels, 20, 10, 9));
        Assert.True(Alpha(pixels, 20, 1, 5) > 0);    // round cap past the end point
        for (int i = 0; i < pixels.Length; i += 4) Assert.Equal(new byte[] { 0, 0, 0 }, pixels[i..(i + 3)]);
    }

    [Fact]
    public void RasterizeStrokes_ScalesPointsAndWidth()
    {
        var strokes = new List<IReadOnlyList<(double X, double Y)>> { new List<(double, double)> { (1, 2), (9, 2) } };
        byte[] pixels = SignatureInk.RasterizeStrokes(strokes, 1, 40, 16, 4);
        Assert.Equal(255, Alpha(pixels, 40, 20, 8));
        Assert.Equal(0, Alpha(pixels, 40, 20, 13));
    }

    [Fact]
    public void RasterizeStrokes_DrawsASinglePointAsARoundDot()
    {
        // A tap on the pad is a dot (the i's dot, a full stop), not nothing (BF-A002).
        var dot = new List<IReadOnlyList<(double X, double Y)>> { new List<(double, double)> { (5, 5) } };
        byte[] pixels = SignatureInk.RasterizeStrokes(dot, 4, 10, 10, 1);
        Assert.Equal(255, Alpha(pixels, 10, 5, 5));
        Assert.Equal(255, Alpha(pixels, 10, 4, 4));
        Assert.Equal(0, Alpha(pixels, 10, 0, 0));
        Assert.Equal(0, Alpha(pixels, 10, 9, 9));
        // Round: the same reach straight across as down.
        Assert.Equal(Alpha(pixels, 10, 7, 5), Alpha(pixels, 10, 5, 7));
    }

    [Fact]
    public void FinishStroke_KeepsATapAsADotEveryRendererDraws()
    {
        // The page, the ghost, the picker and the saved PDF skip strokes under two points, so a tap is stored as
        // a tiny two-point segment that their round caps turn into a dot.
        var tap = SignatureInk.FinishStroke([new System.Windows.Point(12, 30)]);
        Assert.NotNull(tap);
        Assert.Equal(2, tap!.Count);
        Assert.Equal(new System.Windows.Point(12, 30), tap[0]);
        Assert.True((tap[1] - tap[0]).Length is > 0 and < 0.1);

        var line = new List<System.Windows.Point> { new(1, 1), new(5, 5) };
        Assert.Same(line, SignatureInk.FinishStroke(line));
        Assert.Equal(2, line.Count);
        Assert.Null(SignatureInk.FinishStroke([]));
        Assert.Null(SignatureInk.FinishStroke(null));
    }

    [Fact]
    public void ThePadFinishesAStrokeTheSameWayOnReleaseAndOnLostCapture()
    {
        string editor = ShellSource.Read("Shell", "SignatureEditor.cs");
        string pad = ShellSource.Between(editor, "canvas.MouseLeftButtonDown +=", "inkLayer = ink;");
        // Losing the capture (Alt+Tab, a dialog, a touch cancel) keeps the stroke already on screen, so the preview
        // and the saved signature match, and refreshes so a feathered look bakes again (BF-A001).
        Assert.Contains("canvas.LostMouseCapture += (s, e) => EndStroke();", pad, StringComparison.Ordinal);
        Assert.Contains("canvas.MouseLeftButtonUp += (s, e) => EndStroke();", pad, StringComparison.Ordinal);
        string end = ShellSource.Between(editor, "void EndStroke()", "canvas.MouseLeftButtonDown +=");
        Assert.Contains("SignatureInk.FinishStroke(", end, StringComparison.Ordinal);
        Assert.Contains("refresh();", end, StringComparison.Ordinal);
        // The finisher clears its state before releasing capture, since the release raises LostMouseCapture again.
        Assert.True(end.IndexOf("current = null;", StringComparison.Ordinal) < end.IndexOf("ReleaseMouseCapture()", StringComparison.Ordinal));
        Assert.DoesNotContain("Count: > 1", editor, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePadHasANameAndANewSignatureOpensWithFocusOnTheControls()
    {
        string editor = ShellSource.Read("Shell", "SignatureEditor.cs");
        Assert.Contains("AutomationProperties.SetName(canvas, Loc(\"Str_Sig_DrawingPad\"))", editor, StringComparison.Ordinal);
        // Every editor, a new empty pad included, puts keyboard focus on its first slider (BF-A007).
        Assert.Contains("win.Loaded += (s, e) => focusTarget.Focus();", editor, StringComparison.Ordinal);
        Assert.DoesNotContain("if (imageMode || strokes.Count > 0) win.Loaded", editor, StringComparison.Ordinal);
    }

    [Fact]
    public void Process_RunsThresholdThenWeightThenColorThenFeather()
    {
        // A dark pixel on white paper: the scan drops the paper, the ink turns navy, and the feather spreads it.
        var scan = Solid(9, 9, 245, 245, 245, 255);
        int o = (4 * 9 + 4) * 4;
        scan[o] = scan[o + 1] = scan[o + 2] = 10;

        byte[] plain = SignatureInk.Process(scan, 9, 9, new SignatureLook(50, true, 0, 0, null), 1);
        Assert.Equal(SignatureInk.Apply(scan, 50), plain);

        byte[] look = SignatureInk.Process(scan, 9, 9, new SignatureLook(50, true, 1, 2, 0xFF14286E), 1);
        Assert.Equal(new byte[] { 0x6E, 0x28, 0x14 }, look[o..(o + 3)]);
        Assert.Equal(0, Alpha(look, 9, 0, 0));
        Assert.Equal(255, Alpha(look, 9, 4, 4));     // the core stays solid
        Assert.True(Alpha(look, 9, 6, 4) > 0);       // grown by one, then a feather ramp past the new edge
        Assert.True(Alpha(look, 9, 4, 4) > Alpha(look, 9, 7, 4));
    }

    [Fact]
    public void Process_ScalesWeightAndFeatherWithThePictureSize()
    {
        byte[] dot = Dot(21, out int c);
        byte[] small = SignatureInk.Process(dot, 21, 21, new SignatureLook(null, false, 1, 0, null), 1);
        byte[] large = SignatureInk.Process(dot, 21, 21, new SignatureLook(null, false, 1, 0, null), 3);
        Assert.Equal(0, Alpha(small, 21, c + 3, c));
        Assert.Equal(255, Alpha(large, 21, c + 3, c));
    }

    [Fact]
    public void TheCardsEditOpensPlacedSignaturesAndIsHiddenWhereItWouldDoNothing()
    {
        string menu = ShellSource.Read("Shell", "ContextMenu.cs");
        string edit = ShellSource.Between(menu, "private void EditAnnotation(", "private PageAnnotation? PairPartner(");
        Assert.Contains("hit is SignatureAnnotation signature", edit, StringComparison.Ordinal);
        Assert.Contains("EditPlacedSignature(signature)", edit, StringComparison.Ordinal);
        Assert.Contains("private static bool CanEditAnnotation(", edit, StringComparison.Ordinal);

        string card = ShellSource.Read("Shell", "AnnotationCard.cs");
        string top = ShellSource.Between(card, "private List<CardTile> MarkCardTopRow(", "private CardTile PasteTile(");
        Assert.Contains("if (!multi && CanEditAnnotation(hit))", top, StringComparison.Ordinal);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(top, @"=> EditAnnotation\(hit\)"));
    }

    [Fact]
    public void CreateImportAndEditShareOneEditorWindow()
    {
        string signing = ShellSource.Read("Shell", "Signing.cs");
        string editor = ShellSource.Read("Shell", "SignatureEditor.cs");

        Assert.DoesNotContain("ShowSignatureSensitivityDialog", signing, StringComparison.Ordinal);
        Assert.Contains("ImportPictureSignature(bmp", signing, StringComparison.Ordinal);
        Assert.Contains("EditSavedSignature(sigCopy)", signing, StringComparison.Ordinal);
        Assert.Contains("\"EditSignature_\" + sigCopy.Id", signing, StringComparison.Ordinal);
        Assert.Equal(1, CountOf(signing + editor, "new Window"));
        foreach (string entry in new[] { "private void OpenSignatureCreator(", "private SavedSignature? ImportPictureSignature(",
                     "private void EditSavedSignature(", "private void EditPlacedSignature(" })
            Assert.Contains(entry, editor, StringComparison.Ordinal);
        // A placed signature changes as one undo step and drops its decoded bitmap so the new look shows.
        string placed = editor[editor.IndexOf("private void EditPlacedSignature(", StringComparison.Ordinal)..];
        Assert.Contains("ApplyCardStyle([sig]", placed, StringComparison.Ordinal);
        Assert.Contains("placed.CachedBitmap = null", placed, StringComparison.Ordinal);
        // The editor offers threshold (pictures), pen, feather and ink color, with the app's color picker.
        foreach (string key in new[] { "Str_Sig_Threshold", "Str_Sig_Pen", "Str_Sig_Feather", "Str_Sig_InkColor", "Str_Sig_ColorCustom" })
            Assert.Contains($"Loc(\"{key}\")", editor, StringComparison.Ordinal);
        Assert.Contains("new ColorPickerDialog(win,", editor, StringComparison.Ordinal);
    }

    private static int CountOf(string source, string text)
    {
        int count = 0;
        for (int i = source.IndexOf(text, StringComparison.Ordinal); i >= 0; i = source.IndexOf(text, i + 1, StringComparison.Ordinal)) count++;
        return count;
    }
}
