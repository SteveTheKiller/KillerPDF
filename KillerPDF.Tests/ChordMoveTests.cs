using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace KillerPDF.Tests;

// Source pins for the 1.9.5 chord moves: Ctrl+R and Ctrl+L turn pages right and left, Ctrl+Shift+R
// recognizes a scan (FineReader's key), and Ctrl+Shift+T reopens a closed tab (every browser's key).
// The handlers need a window, which this project does not open.
public sealed class ChordMoveTests
{
    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([FindRepositoryRoot(), .. parts]));

    [Fact]
    public void TheKeyHandlerRunsTheMovedChords()
    {
        string keys = Read("Shell", "KeyboardShortcuts.cs");
        Assert.Matches(@"key == Key\.L && mods == ModifierKeys\.Control\)\s*\{\s*if \(!e\.IsRepeat\) RotatePages_Click\(-90\)", keys);
        Assert.Matches(@"key == Key\.R && mods == ModifierKeys\.Control\)\s*\{\s*if \(!e\.IsRepeat\) RotatePages_Click\(90\)", keys);
        Assert.Matches(@"key == Key\.R && mods == \(ModifierKeys\.Control \| ModifierKeys\.Shift\)\)\s*\{\s*if \(!e\.IsRepeat\) ConvertScanToText\(\)", keys);
        Assert.Matches(@"key == Key\.T && mods == \(ModifierKeys\.Control \| ModifierKeys\.Shift\)\)\s*\{\s*if \(!e\.IsRepeat\) ActiveViewer\.ReopenClosedTab\(\)", keys);
    }

    [Fact]
    public void MenusShowTheMovedChords()
    {
        Assert.Contains("GestureFor(clockwise ? \"Ctrl:R\" : \"Ctrl:L\")", Read("Shell", "ContextMenu.cs"));
        Assert.Contains("\"Ctrl:L\", () => RotatePages_Click(-90)", Read("Shell", "PageCard.cs"));
        Assert.Contains("ConvertScanToText(), GestureFor(\"CtrlShift:R\")", Read("Shell", "Ocr.cs"));
        Assert.True(Regex.IsMatch(Read("Controls", "Viewer", "PdfViewer.TabStrip.cs"),
            @"Str_Ctx_ReopenClosedTab.*?GestureFor\(""CtrlShift:T""\)", RegexOptions.Singleline));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "KillerPDF.csproj")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("KillerPDF.csproj not found.");
    }
}
