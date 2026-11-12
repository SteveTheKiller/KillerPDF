using System;
using System.IO;
using System.Linq;
using Xunit;

namespace KillerPDF.Tests;

// Fix Page Orientation was on the thumbnail menu and the empty-area menu but not on the menu a
// right-click on the page itself opens. Every row that offers it also names Alt+Shift+O.
public sealed class PageMenuOrientationTests
{
    [Fact]
    public void ThePageMenuOffersFixPageOrientationWithItsShortcut()
    {
        string root = FindRepositoryRoot();
        string pageMenuFile = File.ReadAllText(Path.Combine(root, "Shell", "ContextMenu.cs"));
        int build = pageMenuFile.IndexOf("private void AddPageMenuItems(", StringComparison.Ordinal);
        int end = pageMenuFile.IndexOf("\n        private ", build + 10, StringComparison.Ordinal);
        Assert.True(build >= 0 && end > build);
        string pageMenu = pageMenuFile[build..end];
        // On the page menu it sits under Page with the other page tools.
        Assert.Contains("tools.Items.Add(MakeMenuItem(Loc(\"Str_Pages_FixOrientation\")", pageMenu, StringComparison.Ordinal);

        // The page menu, the empty-area menu and the thumbnail card each offer it once.
        string menu = pageMenuFile + "\n" + File.ReadAllText(Path.Combine(root, "Shell", "PageCard.cs"));
        string[] rows = menu.Split('\n').Where(l => l.Contains("Loc(\"Str_Pages_FixOrientation\")", StringComparison.Ordinal)).ToArray();
        Assert.Equal(3, rows.Length);
        int at = 0;
        foreach (string _ in rows)
        {
            at = menu.IndexOf("Loc(\"Str_Pages_FixOrientation\")", at, StringComparison.Ordinal);
            string call = menu[at..menu.IndexOf("));", at, StringComparison.Ordinal)];
            Assert.Contains("GestureFor(\"AltShift:O\")", call, StringComparison.Ordinal);
            at += 10;
        }
    }

    private static string FindRepositoryRoot([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "KillerPDF.csproj")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the KillerPDF repository root.");
    }
}
