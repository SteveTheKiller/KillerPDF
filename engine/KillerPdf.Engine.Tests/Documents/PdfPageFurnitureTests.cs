using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfPageFurnitureTests
{
    [Fact]
    public void FormatterExpandsBuiltInAndCustomTokensDeterministically()
    {
        var context = new PdfPageFurnitureContext
        {
            PageNumber = 3,
            TotalPages = 12,
            PageLabel = "iii",
            FileName = "report.pdf",
            Title = "Quarterly report",
            Author = "Ada",
            Date = new DateOnly(2026, 9, 3),
            CustomTokens = new Dictionary<string, string?> { ["case"] = "A-42" }
        };

        string value = PdfPageFurnitureFormatter.Format(
            "{title} | {label} | {page}/{pages} | {filename} | {author} | {date} | {case}", context);

        Assert.Equal("Quarterly report | iii | 3/12 | report.pdf | Ada | 2026-09-03 | A-42", value);
    }

    [Fact]
    public void FormatterSupportsLiteralOpeningBraceAndRejectsUnknownTokens()
    {
        var context = new PdfPageFurnitureContext
        {
            PageNumber = 1,
            TotalPages = 1,
            Date = new DateOnly(2026, 9, 3)
        };

        Assert.Equal("{Page 1", PdfPageFurnitureFormatter.Format("{{Page {page}", context));
        Assert.Throws<KeyNotFoundException>(() =>
            PdfPageFurnitureFormatter.Format("{missing}", context));
    }

    [Fact]
    public void BatesPlanContinuesAcrossDocumentsAndPreservesOrder()
    {
        IReadOnlyList<PdfBatesNumber> result = PdfBatesNumbering.Plan([2, 0, 2],
            new PdfBatesNumberingOptions
            {
                StartNumber = 98,
                DigitCount = 4,
                Prefix = "CASE-",
                Suffix = "-A"
            });

        Assert.Equal(["CASE-0098-A", "CASE-0099-A", "CASE-0100-A", "CASE-0101-A"],
            result.Select(value => value.Text));
        Assert.Equal([(0, 0), (0, 1), (2, 0), (2, 1)],
            result.Select(value => (value.DocumentIndex, value.PageIndex)));
    }

    [Fact]
    public void BatesPlanRejectsInvalidCountsAndNumericOverflow()
    {
        Assert.Throws<ArgumentException>(() =>
            PdfBatesNumbering.Plan([1, -1], new PdfBatesNumberingOptions()));
        Assert.Throws<OverflowException>(() => PdfBatesNumbering.Plan([2],
            new PdfBatesNumberingOptions { StartNumber = long.MaxValue }));
    }
}
