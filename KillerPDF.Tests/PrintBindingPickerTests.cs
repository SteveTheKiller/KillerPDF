using System.IO;
using Xunit;

namespace KillerPDF.Tests;

// The print window is not linked into this project, so these pin its setup order. The dialog
// itself is exercised by native acceptance on a UI VM.
public sealed class PrintBindingPickerTests
{
    private static string Preview()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "KillerPDF.csproj")))
            directory = directory.Parent;
        return File.ReadAllText(Path.Combine(directory!.FullName, "Controls", "PrintPreviewWindow.cs"));
    }

    [Fact]
    public void SavedBindingLoadsIntoTheTicketValueBeforeThePickerShowsIt()
    {
        string source = Preview();
        int restored = source.IndexOf("_duplexShort = App.GetSetting(\"PrintDuplexBinding\") == \"1\";", StringComparison.Ordinal);
        int shown = source.IndexOf("_flipCombo.SelectedIndex = _duplexShort ? 1 : 0;", StringComparison.Ordinal);

        Assert.True(restored >= 0 && shown > restored);
    }

    [Fact]
    public void BindingPickerHasTheVisibleLabelAsItsAccessibleName()
    {
        Assert.Contains("System.Windows.Automation.AutomationProperties.SetName(_flipCombo, S(\"Str_Print_FlipOn\"));",
            Preview(), StringComparison.Ordinal);
    }
}
