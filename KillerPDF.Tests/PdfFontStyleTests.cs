using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests
{
    public class PdfFontStyleTests
    {
        [Theory]
        // #187: families are normalized to the installed Windows family the PostScript name
        // means - the raw PS name resolves to nothing in WPF and read as "all formatting lost".
        [InlineData("ABCDEF+Helvetica-Bold", "Arial", true, false)]
        [InlineData("Helvetica-Oblique", "Arial", false, true)]
        [InlineData("TimesNewRomanPS-BoldItalicMT", "Times New Roman", true, true)]
        [InlineData("Arial-Regular", "Arial", false, false)]
        [InlineData("ArialMT", "Arial", false, false)]
        [InlineData("TimesNewRomanPSMT", "Times New Roman", false, false)]
        [InlineData("CourierNewPS-BoldMT", "Courier New", true, false)]
        [InlineData("GHIJKL+BookAntiqua", "Book Antiqua", false, false)]
        [InlineData("ABCDEF+Calibri", "Calibri", false, false)]
        [InlineData("ABCDEF+Calibri-Bold", "Calibri", true, false)]
        [InlineData("ABCDEF+SegoeUI", "Segoe UI", false, false)]
        public void DetectsFaceStyleFromPdfFontName(string source, string family, bool bold, bool italic)
        {
            var detected = PdfFontStyle.FromPdfName(source);

            Assert.Equal(family, detected.Family);
            Assert.Equal(bold, detected.Bold);
            Assert.Equal(italic, detected.Italic);
        }

        [Theory]
        [InlineData("Calibri", "Calibri")]
        [InlineData("segoeui", "Segoe UI")]
        [InlineData("CalibriLight", "Calibri Light")]
        [InlineData("MissingFont", "Segoe UI")]
        public void ResolvesInstalledFamilyOrExplicitFallback(string requested, string expected)
        {
            Assert.Equal(expected, PdfFontStyle.ResolveInstalledFamily(requested,
                new[] { "Calibri", "Calibri Light", "Segoe UI" }));
        }
    }
}
