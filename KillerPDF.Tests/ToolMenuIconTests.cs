using System.Windows;
using System.Windows.Controls;
using KillerPDF.Shell;
using Xunit;

namespace KillerPDF.Tests;

// #485: the Docked and Floating Tool Menu rows show the layout they switch to, drawn like the sidebar's
// side icons: a small window with the tool menu docked as a full-width strip, or hanging as a short tab.
public sealed class ToolMenuIconTests
{
    private static (Grid Icon, Border Menu) Parts(bool floating)
    {
        var icon = Assert.IsType<Grid>(ToolMenuIcons.For(floating));
        icon.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        icon.Arrange(new Rect(icon.DesiredSize));
        return (icon, Assert.IsType<Border>(icon.Children[^1]));
    }

    [Fact]
    public void TheDockedIconShowsTheMenuAcrossTheTop() => RunSta(() =>
    {
        var (icon, menu) = Parts(floating: false);
        Assert.Equal(VerticalAlignment.Top, menu.VerticalAlignment);
        Assert.Equal(icon.ActualWidth, menu.ActualWidth);
    });

    [Fact]
    public void TheFloatingIconShowsAShortTabHangingFromTheTop() => RunSta(() =>
    {
        var (icon, menu) = Parts(floating: true);
        Assert.Equal(VerticalAlignment.Top, menu.VerticalAlignment);
        Assert.InRange(menu.ActualWidth, 2, icon.ActualWidth / 2);
        Assert.True(menu.ActualHeight > 0);
    });

    private static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { error = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }
}
