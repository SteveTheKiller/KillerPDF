using System.IO;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

// The picture card (Steve 2026-11-19): a picture that is part of the page gets the same card as a placed one, and the
// card turns, flips, replaces, color corrects, resets the shape of and reads the text in a picture. The first edit of a
// picture that is part of the page lifts it off the page into a placed picture, in the same Undo step.
public sealed class PictureCardTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "KillerPDF.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([Root(), .. parts]));

    private static string Between(string text, string start, string end)
    {
        int a = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(a >= 0, "missing: " + start);
        int b = text.IndexOf(end, a + start.Length, StringComparison.Ordinal);
        Assert.True(b > a, "missing: " + end);
        return text[a..b];
    }

    [Theory]
    [InlineData(200, 100, 400, 200, false)]   // drawn in its own proportions
    [InlineData(200, 100, 401, 200, false)]   // a quarter percent off is rounding, not a stretch
    [InlineData(200, 150, 400, 200, true)]    // squashed
    [InlineData(100, 200, 400, 200, true)]    // sideways footprint over upright pixels
    [InlineData(200, 100, 0, 0, false)]       // unreadable pixels never offer Reset Size
    public void ResetSizeIsOfferedOnlyForAStretchedPicture(double width, double height, int pixelWidth, int pixelHeight, bool expected) =>
        Assert.Equal(expected, PictureShape.OutOfShape(width, height, pixelWidth, pixelHeight));

    [Fact]
    public void ResetSizeKeepsTheWidthAndRestoresTheProportions()
    {
        double height = PictureShape.HeightFor(300, 1200, 800);

        Assert.Equal(200, height, 6);
        Assert.False(PictureShape.OutOfShape(300, height, 1200, 800));
    }

    [Theory]
    [InlineData(true, 0, true)]
    [InlineData(false, 0, false)]
    [InlineData(true, 180, true)]
    [InlineData(true, 90, false)]     // a quarter-turned picture shows its pixels sideways
    [InlineData(false, 90, true)]
    [InlineData(true, 270, false)]
    [InlineData(true, -90, false)]
    public void AFlipSeenOnThePageLandsOnThePicturesOwnPixels(bool horizontal, int rotation, bool acrossPixels) =>
        Assert.Equal(acrossPixels, PictureShape.FlipsAcrossPixels(horizontal, rotation));

    [Fact]
    public void BothKindsOfPictureGetTheFullCard()
    {
        string card = Read("Shell", "PictureCard.cs");
        string grid = Between(card, "private void AddPictureCard(", "private FrameworkElement PictureCardHeader(");

        foreach (string key in new[] { "Str_Ctx_FlipHorizontal", "Str_Ctx_FlipVertical", "Str_Ctx_ReplacePicture",
            "Str_ColorCorrection", "Str_Ctx_CopyText", "Str_Ctx_ResetSize", "Str_Ctx_RotateCCWShort", "Str_Ctx_UpsideDown",
            "Str_Ctx_RotateCWShort" })
            Assert.Contains("Loc(\"" + key + "\")", grid);
        // Twelve tiles in four full rows, and Paste, Raise and Lower start in the Hidden drawer.
        Assert.Contains("clipboard, turns, changes, tools, layers, spares", grid);
        Assert.Contains("[\"\\uE77F\", \"\\uE70E\", \"\\uE70D\"]", grid);
        // Raise and Lower only reorder placed pictures.
        Assert.Contains("if (placed && _annotations.TryGetValue(picture.PageIndex", grid);
    }

    [Fact]
    public void TheFirstEditLiftsAPictureOffThePageInOneUndoStep()
    {
        string card = Read("Shell", "PictureCard.cs");
        string lift = Between(card, "private ImageAnnotation? LiftPicture(", "private void FlipPicture(");

        // The edit lands on the copy before the reload, which removes the picture from the page and adds the copy in
        // the same document history step.
        Assert.True(lift.IndexOf("edit(lifted);", StringComparison.Ordinal) < lift.IndexOf("SaveTempAndReload(", StringComparison.Ordinal));
        Assert.Contains("finalizeSavedFile: source.Remove", lift);
        Assert.Contains("remapAnnotations:", lift);
        // The lifted copy goes beneath every mark, where the picture was.
        Assert.Contains("list.Insert(0, lifted);", lift);

        // Every edit goes through EditPicture, which lifts first when the picture is part of the page.
        string edit = Between(card, "private ImageAnnotation? EditPicture(", "private ImageAnnotation? LiftPicture(");
        Assert.Contains("if (LiftPicture(edit) is not { } lifted) return null;", edit);
        Assert.DoesNotContain("PushPageSnapshotUndoExt", Between(card, "private void FlipPicture(", "private static bool PictureOutOfShape("));

        // The viewer removes the lifted picture with the same checked step Delete uses.
        string viewer = Read("Controls", "Viewer", "PdfViewer.SourceImages.cs");
        Assert.Contains("finalizeSavedFile: RemoveSelectedPlacement(selection)", Between(viewer, "private bool DeleteSelectedSourceImage()", "internal (ImageAnnotation Image"));
        Assert.Contains("(image, RemoveSelectedPlacement(selection))", viewer);
    }

    [Fact]
    public void CopyTextReadsThePicturesOwnPixels()
    {
        // Region OCR renders the page without its marks, so a placed picture would read as whatever is under it.
        string card = Read("Shell", "PictureCard.cs");
        Assert.Contains("Ocr.OcrPicture(upright);", Between(card, "private void CopyPictureText(", "private void CorrectPictureColor("));
        string ocr = Read("Features", "Ocr", "OcrController.cs");
        Assert.Contains("\"Str_Busy_Picture\", \"Str_St_OcrCopiedPicture\"", Between(ocr, "internal async void OcrPicture(", "private async Task CopyRecognizedText("));
    }

    [Fact]
    public void PictureColorCorrectionShowsOnlyColorAndLevels()
    {
        string window = Read("Controls", "TransformWindow.cs");
        string sections = Between(window, "mode == TransformWindowMode.PictureColorCorrection", ": new (string Title, UIElement Body)[]");
        Assert.Contains("(S(\"Str_Cc_Color\"), color), (S(\"Str_Tf_Levels\"), levels) }", sections);
        Assert.DoesNotContain("Str_Tf_Quality", sections);

        string card = Read("Shell", "PictureCard.cs");
        string color = Between(card, "private void CorrectPictureColor(", "private static BitmapSource PictureBitmap(");
        Assert.Contains("new TransformWindow(this, 1, [0], first", color);
        Assert.Contains("TransformWindowMode.PictureColorCorrection", color);
        // After the first Apply lifts the picture, later Applies change the lifted copy.
        Assert.Contains("current = edited;", color);
        Assert.Contains("isPlaced = true;", color);
    }
}
