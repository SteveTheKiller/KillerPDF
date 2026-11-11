using Xunit;
using static KillerPDF.Tests.ViewerSourcePin;

namespace KillerPDF.Tests;

// SOURCE PINS: OcrController asks the user for a file through a window dialog before it takes the snapshot, so it has no
// headless seam; the controller and the host are read as text. The delivered Searchable PDF and Scan to Text files are
// built from the snapshot the host writes, so the snapshot's skipped-picture count has to reach their success status.
// Window check on SN02: 1. Place a picture copied from a PDF that cannot be read. 2. Run Make Searchable PDF, then
// Convert Scan to Text. 3. Each success status in the status bar ends with the skipped-picture note.
public sealed class OcrSkippedPicturesTests
{
    [Fact]
    public void TheHostHandsBackTheSnapshotsSkippedPictureCount()
    {
        Assert.Contains("int SaveDocumentTo(string path);", Source("Features", "Ocr", "IOcrHost.cs"));
        string host = Source("Shell", "Ocr.cs");
        Assert.Contains("int IOcrHost.SaveDocumentTo(string path)", host);
        Assert.Contains("return copy.SkippedPictures;", host);
        Assert.DoesNotContain("only feeds recognition", host);
        Assert.DoesNotContain("WriteSavedCopyToTemp(forRasterization: true, throwaway: true).Path", host);
    }

    [Theory]
    [InlineData("internal async void MakeSearchablePdf()", "internal static (int pages, int words) BuildSearchablePdf(", "Str_St_SearchableSaved")]
    [InlineData("internal async void ConvertScanToText()", "internal sealed record ScanTextSummary(", "Str_St_ScanTextSaved")]
    public void TheSuccessStatusCarriesTheSkippedPictureNote(string start, string end, string statusKey)
    {
        string flow = Between(Source("Features", "Ocr", "OcrController.cs"), start, end);
        Assert.Contains("skippedPictures = _host.SaveDocumentTo(src);", flow);
        Assert.Contains("PicturesSkippedNote.Append(", flow);
        Assert.Contains($"_host.Loc(\"{statusKey}\")", flow);
        Assert.Contains("skippedPictures, _host.Loc(\"Str_Save_PicturesSkipped\")", flow);
    }

    // Extract All Text reads the same snapshot, so it keeps the count too, and no controller call throws it away.
    [Fact]
    public void ExtractAllTextCarriesTheSkippedPictureNoteAndNoCallDiscardsTheCount()
    {
        string controller = Source("Features", "Ocr", "OcrController.cs");
        Assert.DoesNotContain("_ = _host.SaveDocumentTo(", controller);
        string flow = Between(controller, "internal async void ExtractAllText()", "private static int ExtractText(");
        Assert.Contains("skippedPictures = _host.SaveDocumentTo(src);", flow);
        Assert.Contains("PicturesSkippedNote.Append(", flow);
        Assert.Contains("_host.Loc(\"Str_St_TextExtracted\")", flow);
        Assert.Contains("skippedPictures, _host.Loc(\"Str_Save_PicturesSkipped\")", flow);
    }
}
