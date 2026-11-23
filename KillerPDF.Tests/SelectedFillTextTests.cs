using System;
using System.IO;
using Xunit;

namespace KillerPDF.Tests
{
    // Trash Polka Yellow fills RowSelectedBrush with the bright yellow itself, so anything that stays filled
    // with it while a mode is on takes TileCheckedTextBrush where the theme states one, like a checked tile.
    public class SelectedFillTextTests
    {
        private static string Source(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "KillerPDF.sln"))) dir = dir.Parent;
            return File.ReadAllText(Path.Combine([dir!.FullName, .. parts]));
        }

        [Fact]
        public void BookLayoutButtonTakesTheCheckedTextWhileOn()
        {
            string menu = Source("Shell", "ContextMenu.cs");
            int book = menu.IndexOf("if (Controls.PdfViewer.BookMode)", StringComparison.Ordinal);
            Assert.True(book > 0);
            string block = menu[book..menu.IndexOf("buttons.Children.Add(book);", book, StringComparison.Ordinal)];
            Assert.Contains("TileCheckedTextBrush", block);
        }

        [Fact]
        public void ArmedColorPickerChipsTakeTheCheckedText()
        {
            string picker = Source("Controls", "ColorPickerDialog.cs");
            Assert.Contains("private static Brush SelectedFillText()", picker);
            Assert.Contains("CrosshairIcon(SelectedFillText())", picker);
            Assert.Contains("_replaceArmed ? SelectedFillText() : R(\"TextBrush\")", picker);
        }
    }
}
