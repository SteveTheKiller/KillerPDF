using System.Text;
using UglyToad.PdfPig;
using Xunit;

namespace KillerPDF.Tests
{
    /// <summary>
    /// Pins the PdfPig property that in-place text editing reads its font size from (#163).
    /// Letter.FontSize is the size as written in the content stream, so a generator that emits
    /// "/F1 1 Tf" and applies the scale through the text matrix reports 1 no matter how large the
    /// glyphs actually draw. Detection used that value, which collapsed the replacement text onto
    /// its lower clamp and read back as 3pt. Letter.PointSize is the size in points and is right
    /// for both spellings, so these tests fail if a PdfPig upgrade changes either meaning.
    /// </summary>
    public class PdfTextSizeTests
    {
        // Two runs that draw at the same visual size: the first sized by Tf with an identity text
        // matrix, the second sized entirely by a 12x text matrix.
        const string Content =
            "BT\n/F1 12 Tf\n1 0 0 1 72 700 Tm\n(Conventional) Tj\nET\n" +
            "BT\n/F1 1 Tf\n12 0 0 12 72 660 Tm\n(Scaled) Tj\nET\n";

        /// <summary>A single-page PDF holding <see cref="Content"/>, built by hand so the two runs
        /// keep their exact Tf and Tm operands - no library would emit the second one.</summary>
        static byte[] BuildPdf()
        {
            string[] objects =
            [
                "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
                "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n",
                "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] "
                    + "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>\nendobj\n",
                $"4 0 obj\n<< /Length {Content.Length} >>\nstream\n{Content}\nendstream\nendobj\n",
                "5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica "
                    + "/Encoding /WinAnsiEncoding >>\nendobj\n",
            ];

            var pdf = new StringBuilder("%PDF-1.4\n");
            var offsets = new int[objects.Length];
            for (int i = 0; i < objects.Length; i++)
            {
                offsets[i] = pdf.Length;
                pdf.Append(objects[i]);
            }

            int startXref = pdf.Length;
            pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
            foreach (int offset in offsets)
                pdf.Append($"{offset:D10} 00000 n \n");
            pdf.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\n")
               .Append($"startxref\n{startXref}\n%%EOF\n");

            // The content is pure ASCII, so string length above is also the byte offset.
            return Encoding.ASCII.GetBytes(pdf.ToString());
        }

        static (double FontSize, double PointSize) FirstLetterOf(string word)
        {
            using var doc = PdfDocument.Open(BuildPdf());
            var letter = doc.GetPage(1).GetWords().Single(w => w.Text == word).Letters[0];
            return (letter.FontSize, letter.PointSize);
        }

        [Fact]
        public void FontSize_MatchesPoints_OnlyWhenTfCarriesTheScale()
        {
            Assert.Equal(12, FirstLetterOf("Conventional").FontSize, 3);
        }

        [Fact]
        public void FontSize_ReportsTheRawOperand_WhenTheTextMatrixCarriesTheScale()
        {
            // The glyphs draw at 12pt, but Tf said 1. This is the value that produced #163.
            Assert.Equal(1, FirstLetterOf("Scaled").FontSize, 3);
        }

        [Fact]
        public void PointSize_IsTheVisualSize_ForBothSpellings()
        {
            Assert.Equal(12, FirstLetterOf("Conventional").PointSize, 3);
            Assert.Equal(12, FirstLetterOf("Scaled").PointSize, 3);
        }
    }
}
