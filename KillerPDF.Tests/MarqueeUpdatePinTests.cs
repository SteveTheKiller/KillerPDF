using Xunit;
using static KillerPDF.Tests.ViewerSourcePin;

namespace KillerPDF.Tests;

// SOURCE PIN (viewer BF-011). The viewer is not compiled into this project, so allocation per mouse move cannot be
// counted here; this pins that the per-move set is gone and the visible-canvas scan reuses one list.
public sealed class MarqueeUpdatePinTests
{
    [Fact]
    public void AMarqueeMoveDoesNotBuildAHashSetOfTheVisibleCanvases()
    {
        string annotations = Viewer("PdfViewer.Annotations.cs");
        string update = Between(annotations, "internal void UpdateMarquee(Point startInStartCanvas, Point currentInStartCanvas)", "private static void PlaceMarquee(");

        Assert.DoesNotContain("ToHashSet()", update);
        Assert.DoesNotContain(".Where(", update);
        Assert.Contains("_nativeMarqueePages.Clear();", update);
        Assert.Contains("private readonly List<Canvas> _nativeMarqueePages = new();", annotations);
    }
}
