using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

// #513: Shift+Enter on PDFs selected in File Explorer opens them together in a new window. Explorer
// starts one launch per file and says nothing about Shift, so each launch reads Shift itself and
// marks the path it forwards to the running window.
public sealed class ShiftLaunchTests
{
    [Fact]
    public void AMarkedPathComesBackWhole()
    {
        string message = ShiftLaunch.Mark(@"C:\Data\My book.pdf");
        Assert.True(ShiftLaunch.TryUnmark(message, out string path));
        Assert.Equal(@"C:\Data\My book.pdf", path);
    }

    [Fact]
    public void APlainPathIsNotANewWindowLaunch()
    {
        Assert.False(ShiftLaunch.TryUnmark(@"C:\Data\book.pdf", out _));
    }

    [Fact]
    public void AMarkerWithoutAPathIsIgnored()
    {
        Assert.False(ShiftLaunch.TryUnmark(ShiftLaunch.Mark(""), out _));
    }

    // With KillerPDF closed, the first launch of the selection becomes the window and the rest
    // join it, so one Shift+Enter never makes two windows.
    [Theory]
    [InlineData(false, 1.0, true)]
    [InlineData(false, 60.0, true)]
    [InlineData(true, 1.0, false)]
    [InlineData(true, 60.0, true)]
    public void ShiftLaunchesJoinAWindowTheSameSelectionJustStarted(bool primaryStartedWithShift, double secondsSinceStart, bool expected)
    {
        Assert.Equal(expected, ShiftLaunch.OpensNewWindow(primaryStartedWithShift, TimeSpan.FromSeconds(secondsSinceStart)));
    }

    [Fact]
    public void ShiftIsReadBeforeAnythingElseAtStartup()
    {
        string app = ShellSource.Read("App.xaml.cs");
        string startup = ShellSource.Between(app, "protected override void OnStartup(", "if (e.Args.Length > 0");
        Assert.Contains("ShiftLaunch.ShiftHeld()", startup);
        string secondary = ShellSource.Between(app, "if (!isPrimary)", "StartPipeServer();");
        Assert.Contains("ShiftLaunch.Mark(", secondary);
    }
}
