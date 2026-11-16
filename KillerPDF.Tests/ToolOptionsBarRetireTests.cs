using System.Windows.Controls;
using KillerPDF.Controls;
using Xunit;

namespace KillerPDF.Tests;

// #467: a layer toggle in More settings reselects the layer, which builds a new tool bar while the old bar's menu is
// still open. Nothing closed that menu, so it stayed open with no button under it and kept the mouse, and the app
// looked frozen until a taskbar menu took the mouse back. A bar the dock lets go of closes its menus.
public sealed class ToolOptionsBarRetireTests
{
    [Fact]
    public void ABarReplacedInTheDockIsRetired() => RunSta(() =>
    {
        var first = new ToolOptionsBar(EditTool.Text, "Text options", "More settings");
        var second = new ToolOptionsBar(EditTool.Text, "Text options", "More settings");
        var dock = new ToolOptionsDock { Bar = first };

        dock.Bar = second;

        Assert.True(first.Retired);
        Assert.False(second.Retired);
    });

    [Fact]
    public void ABarTakenOutOfTheDockIsRetired() => RunSta(() =>
    {
        var bar = new ToolOptionsBar(EditTool.Text, "Text options", "More settings");
        var dock = new ToolOptionsDock { Bar = bar };

        dock.Bar = null;

        Assert.True(bar.Retired);
    });

    [Fact]
    public void SettingTheSameBarAgainDoesNotRetireIt() => RunSta(() =>
    {
        var bar = new ToolOptionsBar(EditTool.Text, "Text options", "More settings");
        var dock = new ToolOptionsDock { Bar = bar };

        dock.Bar = bar;

        Assert.False(bar.Retired);
    });

    // A retired bar's menus are closed and its More settings button is let go, so neither holds the mouse.
    [Fact]
    public void RetiringABarClosesItsMenus() => RunSta(() =>
    {
        var bar = new ToolOptionsBar(EditTool.Text, "Text options", "More settings");

        bar.Retire();

        Assert.True(bar.Retired);
        Assert.False(bar.MenusOpen);
        Assert.False(bar.OverflowButton.IsChecked == true);
    });

    private static void RunSta(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
