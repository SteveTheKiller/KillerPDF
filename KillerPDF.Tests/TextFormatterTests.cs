using PdfSharpCore.Drawing;
using PdfSharpCore.Drawing.Layout;
using PdfSharpCore.Pdf;
using Xunit;

namespace KillerPDF.Tests
{
    /// <summary>
    /// Regression cover for burning multi-line text annotations into a document (#142).
    /// CreateBlocks emits a Block(BlockType.LineBreak) per newline, and that constructor never sets
    /// Text. CreateLayout then skips assigning those blocks a Location, so they keep XPoint(0,0) and
    /// GetLines' GroupBy lumps them in with the first line. The Justify branch of DrawString called
    /// block.Text.Trim() on them and threw NullReferenceException.
    /// Only the Justify path was affected - the other alignments join blocks with string.Join, which
    /// tolerates nulls. The short DrawString overload defaults to Justify, so KillerPDF hit it.
    /// </summary>
    public class TextFormatterTests
    {
        static void Draw(string content, TextFormatAlignment? alignment = null)
        {
            var doc  = new PdfDocument();
            var page = doc.AddPage();
            var gfx  = XGraphics.FromPdfPage(page);
            var font = new XFont("Segoe UI", 12, XFontStyle.Regular);
            var rect = new XRect(20, 20, 300, 200);
            var tf   = new XTextFormatter(gfx);

            if (alignment == null)
                tf.DrawString(content, font, XBrushes.Black, rect);
            else
                tf.DrawString(content, font, XBrushes.Black, rect, alignment);
        }

        [Fact]
        public void DrawString_SingleLine_DoesNotThrow()
        {
            Draw("Hello world");
        }

        [Fact]
        public void DrawString_MultiLine_DoesNotThrow()
        {
            // The short overload defaults to Justify - this is the exact call KillerPDF makes when
            // burning a text annotation, and the one that crashed.
            Draw("Hello\nworld");
        }

        [Fact]
        public void DrawString_MultiLineCrLf_DoesNotThrow()
        {
            Draw("Hello\r\nworld");
        }

        [Fact]
        public void DrawString_MultiLineLeftAligned_DoesNotThrow()
        {
            // What the annotation burn now passes explicitly, matching the on-screen TextBlock.
            Draw("Hello\nworld", new TextFormatAlignment { Horizontal = XParagraphAlignment.Left });
        }

        [Fact]
        public void DrawString_ConsecutiveNewlines_DoesNotThrow()
        {
            // A blank line produces two adjacent LineBreak blocks, so the line group is entirely
            // non-drawable. Guards the empty-after-filter path.
            Draw("Hello\n\n\nworld");
        }
    }
}
