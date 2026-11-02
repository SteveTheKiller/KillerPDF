using KillerPDF.Controls;
using Xunit;

namespace KillerPDF.Tests;

/// <summary>The paragraph dialog at 2.5 times scale held a 288 wide side panel and a 200 high text
/// field in a window that could shrink to 640 by 460, so the field stood 660 across and 500 high.</summary>
public sealed class ScaledDialogFitTests
{
    private const double Scale = 2.5;
    private const double SidePanel = 288, TextField = 200;

    [Fact]
    public void TheWindowHoldsItsSidePanelAtFullScale()
    {
        double smallest = ScaledDialogFit.Minimum(640, SidePanel + 32, Scale, workArea: 1880);

        Assert.True(smallest >= SidePanel * Scale, $"A window {smallest} wide cannot hold a panel {SidePanel * Scale} wide.");
    }

    [Fact]
    public void TheMinimumNeverPassesTheScreen()
    {
        Assert.Equal(1000, ScaledDialogFit.Minimum(640, 600, Scale, workArea: 1000));
    }

    [Fact]
    public void ANormalScaleKeepsTheOldMinimum()
    {
        Assert.Equal(640, ScaledDialogFit.Minimum(640, 320, 1, workArea: 1880));
    }

    [Fact]
    public void TheFieldFitsItsScrollArea()
    {
        const double scrollArea = 160;

        double height = ScaledDialogFit.FieldHeight(TextField, 80, scrollArea);

        Assert.True(height <= scrollArea, $"A field {height} high cannot show whole in a scroll area {scrollArea} high.");
        Assert.True(height >= 80);
    }

    [Fact]
    public void ARoomyScrollAreaKeepsTheFullField()
    {
        Assert.Equal(TextField, ScaledDialogFit.FieldHeight(TextField, 80, 400));
    }

    [Fact]
    public void AScrollAreaNotLaidOutYetKeepsTheFullField()
    {
        Assert.Equal(TextField, ScaledDialogFit.FieldHeight(TextField, 80, 0));
    }
}
