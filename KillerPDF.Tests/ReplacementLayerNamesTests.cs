using Xunit;

namespace KillerPDF.Tests;

// The replacement layer controls have a Group, a Cover and a Text row, each with a show, a move and a link
// button. A screen reader hears the name of the button, so each name has to say which row it belongs to.
public sealed class ReplacementLayerNamesTests
{
    [Fact]
    public void EachToggleIsNamedFromItsRowLabelAndItsAction()
    {
        string bar = ShellSource.Read("Shell", "ReplacementLayerBar.cs");
        string toggle = ShellSource.Between(bar, "void Toggle(", "RoutedEventHandler changed");

        // The name composes the row's label with the action through the shared "{0}: {1}" string.
        Assert.Contains("AutomationProperties.SetName(button, string.Format(Loc(\"Str_Bar_SwatchName\"), rowLabel, Loc(tooltip)))",
            toggle, StringComparison.Ordinal);
        Assert.DoesNotContain("AutomationProperties.SetName(button, Loc(tooltip))", toggle, StringComparison.Ordinal);
        // Every row passes its own label to its three buttons.
        string row = ShellSource.Between(bar, "void AddRow(", "void Place(");
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(row, @"row, [1-3]\);").Count);
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(row, @"rowLabel, row, ").Count);    }
}