using System.IO;
using Xunit;

namespace KillerPDF.Tests;

// #513: a window opened with Shift in the Open dialog is placed like a torn-out tab, beside its source. The saved
// placement treats it that way only when its files are known before the window restores its bounds, or a saved
// maximized window opens maximized over its source.
public sealed class NewWindowPlacementTests
{
    private static string MainWindowSource()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "KillerPDF.csproj")))
            directory = directory.Parent;
        return File.ReadAllText(Path.Combine(directory!.FullName, "Shell", "MainWindow.xaml.cs")).ReplaceLineEndings("\n");
    }

    [Fact]
    public void TheStartupFilesAreSetBeforeTheWindowRestoresItsBounds()
    {
        string source = MainWindowSource();
        int restore = source.IndexOf("            RestoreWindowBounds();", StringComparison.Ordinal);
        Assert.True(restore >= 0, "RestoreWindowBounds call not found");
        int body = source.LastIndexOf("        {\n", restore, StringComparison.Ordinal);
        string beforeRestore = source[body..restore];

        Assert.Contains("_startupFiles = startupFiles;", beforeRestore, StringComparison.Ordinal);
        Assert.Contains("_startupTearOutSession = startupTearOutSession;", beforeRestore, StringComparison.Ordinal);
        Assert.Contains("tornOut: _startupTearOutSession is not null || _startupFiles is not null", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TheNewWindowOpensAsANormalWindow()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "KillerPDF.csproj")))
            directory = directory.Parent;
        string tearOut = File.ReadAllText(Path.Combine(directory!.FullName, "Shell", "TabTearOut.cs"));
        int open = tearOut.IndexOf("internal void OpenInNewWindow(", StringComparison.Ordinal);
        int show = tearOut.IndexOf("window.Show();", open, StringComparison.Ordinal);
        Assert.Contains("WindowState = WindowState.Normal", tearOut[open..show], StringComparison.Ordinal);
    }
}
