using System;
using System.IO;
using Xunit;

namespace KillerPDF.Tests
{
    // The nav row's buttons are slimmer than toolbar buttons so the row usually fits the card's three tiles. When it
    // doesn't (Book layout on the row in the two-page views), the card grows to fit the whole row, so "Page 20 of 20"
    // is never cut off at the card's edge (Steve 2026-11-25).
    public class NavRowWidthTests
    {
        private static string Menu()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "KillerPDF.sln"))) dir = dir.Parent;
            return File.ReadAllText(Path.Combine(dir!.FullName, "Shell", "ContextMenu.cs"));
        }

        [Fact]
        public void TheNavRowReportsItsFullWidthSoTheCardGrowsToFitIt()
        {
            string menu = Menu();
            Assert.Contains("var row = new DockPanel { Margin = new Thickness(4, 0, 4, 2), LastChildFill = true };", menu);
            Assert.DoesNotContain("class NavRowPanel", menu);
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
