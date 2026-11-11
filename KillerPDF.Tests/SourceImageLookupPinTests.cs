using Xunit;
using static KillerPDF.Tests.ViewerSourcePin;

namespace KillerPDF.Tests;

// SOURCE PINS (VIEW-004 with image BF-016 and image BF-017). The viewer is not compiled into this project, so the
// counted-work check (placements read once per page and document, a picture extracted once, nothing encoded on a
// press) is a written window check on the lab VM, not an executable test here.
public sealed class SourceImageLookupPinTests
{
    [Fact]
    public void PlacementsAreReadOncePerPageAndDocumentAndShareOneReader()
    {
        string images = Viewer("PdfViewer.SourceImages.cs");
        string cache = Source("Services", "PlacementReadCache.cs");

        Assert.Equal(1, Count(cache, ".ReadImagePlacements(page)"));
        Assert.Contains("reader = new PdfPageContentReader(source);", cache);
        Assert.Contains("_pages.TryGetValue(page, out var cached)", cache);
        Assert.Contains("ReferenceEquals(owner, doc)", cache);
        Assert.Contains("MaximumPages", cache);
        Assert.Contains("_placementCache.PlacementsForPage(doc, page)", images);
    }

    // The set of pictures a press can select is the set whose extraction succeeds, as before. Extraction runs once per
    // page and picture; a repeat press reads the remembered result, and the base-64 copy waits for copy or cut.
    [Fact]
    public void APictureIsExtractedOncePerPageAndTheBase64CopyWaitsForCopyOrCut()
    {
        string images = Viewer("PdfViewer.SourceImages.cs");
        string select = Between(images, "private bool TrySelectSourceImage(int page, Point point, out bool wasSelected)", "private ImageAnnotation? MaterializedSourceImage()");
        string asset = Source("Services", "PlacementReadCache.cs");

        Assert.Contains("AssetForPlacement(page, ordinal, image) is not { } asset) return false;", select);
        Assert.DoesNotContain("PdfImagePlacementExtractor.Extract(", select);
        Assert.DoesNotContain("Convert.ToBase64String(", select);
        Assert.DoesNotContain("new PdfPageContentReader(", select);
        Assert.Equal(1, Count(asset, "PdfImagePlacementExtractor.Extract(source, image)"));
        Assert.Contains("_assets.TryGetValue((page, ordinal), out var known)", asset);
        Assert.Contains("_assets[(page, ordinal)] = asset;", asset);
        Assert.Contains("Convert.ToBase64String(selection.Asset.ToPdfBytes())", Between(images, "private ImageAnnotation? MaterializedSourceImage()", "private PdfImageAsset? AssetForPlacement("));
        Assert.Contains("&& ReferenceEquals(selection.Owner, _doc) ? MaterializedSourceImage() : null;", images);
    }

    [Fact]
    public void TheSelectedPictureIsFoundAgainByItsOrdinalNotByMatchingFields()
    {
        string images = Viewer("PdfViewer.SourceImages.cs");

        Assert.DoesNotContain("SingleOrDefault(image => image.ResourceName", images);
        Assert.Equal(2, Count(images, "ResolveSelectedPlacement(placements, selection.Occurrence, selection.Ordinal)"));
        Assert.Contains("(PdfWorkingDocument Owner, PdfImagePlacementSelection Occurrence, ImageAnnotation Image, int Ordinal, PdfImageAsset Asset)? _sourceImageSelection;", images);
        // The edit compares the saved copy's picture with the selected one's own extraction, with no base-64 round trip.
        Assert.Equal(2, Count(images, "SequenceEqual(selection.Asset.ToPdfBytes())"));
        Assert.DoesNotContain("Convert.FromBase64String(", images);
    }
}
