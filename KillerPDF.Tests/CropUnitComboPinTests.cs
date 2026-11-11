using Xunit;
using static KillerPDF.Tests.ViewerSourcePin;

namespace KillerPDF.Tests;

// SOURCE PIN (viewer BF-015). The crop bar is built inside the viewer, which this project does not compile.
public sealed class CropUnitComboPinTests
{
    [Fact]
    public void TheCropUnitComboSetsItsHeightOnce()
    {
        string crop = Viewer("PdfViewer.Crop.cs");
        string combo = Between(crop, "var unitCombo = new ComboBox", "AutomationProperties.SetAutomationId(unitCombo, \"CropUnits\")");

        Assert.Equal(1, Count(combo, "ToolOptionsControls.ControlHeight"));
        Assert.DoesNotContain("unitCombo.Height", combo);
    }
}
