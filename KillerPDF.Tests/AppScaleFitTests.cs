using KillerPDF.Controls;
using Xunit;

namespace KillerPDF.Tests;

// SN01 M5: at 250% app size the rail's icons ran up over its chevron, and a message such as "This
// paragraph cannot be edited" opened at 100%.
public sealed class AppScaleFitTests
{
    // The rail's eleven icons and their gaps, in the rail's own units, below a sidebar shown at 250%.
    private const double IconsHeight = 340;

    [Theory]
    [InlineData(800 / 2.5 - 36)]
    [InlineData(500 / 2.5 - 36)]
    public void TheRailsIconsFitBelowItsChevron(double below)
    {
        double scale = ScaledDialogFit.RailScale(below, IconsHeight);

        Assert.True(IconsHeight * scale <= below + 0.001, $"{IconsHeight * scale} of icons in {below}.");
        Assert.True(scale > 0);
    }

    [Fact]
    public void IconsThatFitKeepTheirSize() =>
        Assert.Equal(1, ScaledDialogFit.RailScale(600, IconsHeight));

    private sealed record Shown(double? Scale, Shown? Owner);

    [Fact]
    public void AMessageTakesTheScaleOfTheWindowItOpensOver()
    {
        var main = new Shown(2.5, null);
        var dialog = new Shown(null, main);

        Assert.Equal(2.5, ScaledDialogFit.OwnerScale(dialog, shown => shown.Scale, shown => shown.Owner));
        Assert.Equal(2.5, ScaledDialogFit.OwnerScale(main, shown => shown.Scale, shown => shown.Owner));
    }

    [Fact]
    public void AMessageWithNoScaledOwnerStaysAtFullSize()
    {
        Assert.Equal(1, ScaledDialogFit.OwnerScale<Shown>(null, shown => shown.Scale, shown => shown.Owner));
        Assert.Equal(1, ScaledDialogFit.OwnerScale(new Shown(null, null), shown => shown.Scale, shown => shown.Owner));
    }
}
