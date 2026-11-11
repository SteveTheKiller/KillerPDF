using Xunit;
using static KillerPDF.Tests.ViewerSourcePin;

namespace KillerPDF.Tests;

// SOURCE PIN (round 2, app BF-003). The crop gesture lives in the viewer, which this project does not compile, so
// this pins the code and the window check stays a written SN02 step list:
// 1. Open a three-page PDF in continuous view. 2. Choose Crop and drag a box on page 1. 3. Click once, without
// dragging, on page 2. 4. Type a new width in the crop bar fields. 5. The handles sit on the corners of the box on
// page 1. 6. Switch the app language: the box is still on page 1. 7. Click Apply: page 1 is cropped, page 2 is not.
public sealed class CropNoDragCanvasPinTests
{
    [Fact]
    public void AClickWithoutADragKeepsTheCropBoxOnItsOwnCanvasAsWellAsItsPage()
    {
        string annotations = Viewer("PdfViewer.Annotations.cs");
        string branch = Between(annotations, "_cropPageIndex = _cropPrevPageIndex;", "break;");

        Assert.Contains("_cropPreviewRect.Parent is Canvas boxCanvas", branch);
        Assert.Contains("_activeCanvas = _gestureCanvas = boxCanvas;", branch);
        Assert.Contains("UpdateCropRectVisuals();", branch);
        Assert.True(branch.IndexOf("_activeCanvas = _gestureCanvas = boxCanvas;", StringComparison.Ordinal)
            < branch.IndexOf("UpdateCropRectVisuals();", StringComparison.Ordinal));
    }
}
