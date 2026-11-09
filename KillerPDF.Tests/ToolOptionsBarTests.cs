using KillerPDF.Controls;
using Xunit;

namespace KillerPDF.Tests;

// The docked tool options bar (1.9.3 redesign, section 3).
public sealed class ToolOptionsBarTests
{
    // A language or theme change rebuilds the open bars from this catalog. The Measure bar is the
    // draw bar with the Measure controls in front, so it has to be rebuilt with them; it used to be
    // left out and kept the old language and accent until the tool was chosen again.
    [Fact]
    public void TheMeasureToolsBarIsRebuiltWithTheOtherDrawBars()
    {
        Assert.Equal(ToolOptionsBarKind.Draw, ToolOptionsCatalog.BarFor(EditTool.Measure));
    }
}
