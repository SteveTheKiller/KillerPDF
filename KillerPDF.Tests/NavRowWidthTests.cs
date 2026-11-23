using System;
using System.IO;
using Xunit;

namespace KillerPDF.Tests
{
    // A card is three tiles wide. The nav row on top takes that width instead of setting it, and its buttons are
    // slimmer than toolbar buttons, so back, forward, undo, redo, print, Book and "Page 1 of 85" never stretch the tiles.
    public class NavRowWidthTests
    {
        private static string Menu()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "KillerPDF.sln"))) dir = dir.Parent;
            return File.ReadAllText(Path.Combine(dir!.FullName, "Shell", "ContextMenu.cs"));
        }

        [Fact]
        public void TheNavRowReportsNoWidthOfItsOwn()
        {
            string menu = Menu();
            Assert.Contains("var row = new NavRowPanel", menu);
            int panel = menu.IndexOf("private sealed class NavRowPanel : DockPanel", StringComparison.Ordinal);
            Assert.True(panel > 0);
            Assert.Contains("return new Size(0, size.Height);", menu[panel..(panel + 400)]);
        }

        [Fact]
        public void NavRowButtonsAreSlimmerThanToolbarButtons()
        {
            string menu = Menu();
            int nav = menu.IndexOf("private Button NavRowButton(", StringComparison.Ordinal);
            string body = menu[nav..menu.IndexOf("return button;", nav, StringComparison.Ordinal)];
            Assert.Contains("button.Width = 26;", body);
            Assert.Contains("button.Height = 28;", body);
        }
    }
}
