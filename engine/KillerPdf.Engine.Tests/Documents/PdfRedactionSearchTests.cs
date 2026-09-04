using System.Text;
using System.Text.Json;
using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfRedactionSearchTests
{
    [Fact]
    public void FindsPhraseAcrossWordsAndSupportsReviewExclusion()
    {
        PdfPageContent page = Read("Account number 12345 remains");

        PdfRedactionReview original = PdfRedactionSearch.Find([page], new PdfRedactionSearchOptions
        {
            Kind = PdfRedactionSearchKind.ExactText,
            Query = "number 12345"
        });
        PdfRedactionMatch match = Assert.Single(original.Matches);
        PdfRedactionReview excluded = original.Exclude(match.Id);

        Assert.Equal(2, match.WordCount);
        Assert.True(match.Bounds.Width > 0);
        Assert.Empty(excluded.Included);
        Assert.Single(excluded.Include(match.Id).Included);
        Assert.Single(original.Included);
    }

    [Fact]
    public void FindsCommonEmailAndPhonePatterns()
    {
        PdfPageContent page = Read("Email steve@example.com or call (206) 555-0123");

        PdfRedactionMatch email = Assert.Single(PdfRedactionSearch.Find([page],
            new PdfRedactionSearchOptions { Kind = PdfRedactionSearchKind.EmailAddress }).Matches);
        PdfRedactionMatch phone = Assert.Single(PdfRedactionSearch.Find([page],
            new PdfRedactionSearchOptions { Kind = PdfRedactionSearchKind.PhoneNumber }).Matches);

        Assert.Equal("steve@example.com", email.Text);
        Assert.Equal("(206) 555-0123", phone.Text);
    }

    [Fact]
    public void FindsSocialSecurityNumbersWithoutMatchingLongerDigitRuns()
    {
        PdfPageContent page = Read(
            "SSN 123-45-6789 but not 0123-45-67890, 000-12-3456, 666-12-3456, 900-12-3456, 123-00-3456, or 123-45-0000");

        PdfRedactionMatch match = Assert.Single(PdfRedactionSearch.Find([page],
            new PdfRedactionSearchOptions
            {
                Kind = PdfRedactionSearchKind.SocialSecurityNumber
            }).Matches);

        Assert.Equal("123-45-6789", match.Text);
    }

    [Fact]
    public void FindsOnlyPaymentCardNumbersWithValidChecksums()
    {
        PdfPageContent page = Read(
            "Cards 4111 1111 1111 1111 and 4111 1111 1111 1112");

        PdfRedactionMatch match = Assert.Single(PdfRedactionSearch.Find([page],
            new PdfRedactionSearchOptions
            {
                Kind = PdfRedactionSearchKind.PaymentCardNumber
            }).Matches);

        Assert.Equal("4111 1111 1111 1111", match.Text);
        Assert.Equal(4, match.WordCount);
    }

    [Fact]
    public void FindsOnlyInternationalBankAccountNumbersWithValidChecksums()
    {
        PdfPageContent page = Read(
            "IBAN GB82 WEST 1234 5698 7654 32, invalid GB81 WEST 1234 5698 7654 32.");

        PdfRedactionMatch match = Assert.Single(PdfRedactionSearch.Find([page],
            new PdfRedactionSearchOptions
            {
                Kind = PdfRedactionSearchKind.InternationalBankAccountNumber
            }).Matches);

        Assert.Equal("GB82 WEST 1234 5698 7654 32", match.Text);
        Assert.Equal(6, match.WordCount);
    }

    [Fact]
    public void ValidatesTimeoutAndHonorsCancellation()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfRedactionSearch.Find([], new PdfRedactionSearchOptions
        {
            Kind = PdfRedactionSearchKind.RegularExpression, Query = ".*", Timeout = TimeSpan.Zero
        }));
        Assert.Throws<OperationCanceledException>(() => PdfRedactionSearch.Find([Read("text")],
            new PdfRedactionSearchOptions { Kind = PdfRedactionSearchKind.ExactText, Query = "text" },
            new CancellationToken(true)));
    }

    [Fact]
    public void ReviewReportTracksSelectionsWithoutLeakingMatchedTextByDefault()
    {
        PdfRedactionReview review = PdfRedactionSearch.Find([Read("secret account")],
            new PdfRedactionSearchOptions
            {
                Kind = PdfRedactionSearchKind.ExactText,
                Query = "secret"
            });
        review = review.Exclude(Assert.Single(review.Matches).Id);

        string safeJson = review.ToJson();
        string detailedJson = review.ToJson(includeMatchedText: true);
        using JsonDocument report = JsonDocument.Parse(safeJson);

        Assert.Equal(1, report.RootElement.GetProperty("matchCount").GetInt32());
        Assert.Equal(0, report.RootElement.GetProperty("includedCount").GetInt32());
        Assert.False(report.RootElement.GetProperty("matches")[0]
            .GetProperty("included").GetBoolean());
        Assert.DoesNotContain("secret", safeJson, StringComparison.Ordinal);
        Assert.Contains("secret", detailedJson, StringComparison.Ordinal);
    }

    [Fact]
    public void ReviewCarriesReasonAndOverlayTextIntoPrivacySafeReport()
    {
        PdfRedactionReview review = PdfRedactionSearch.Find([Read("secret account")],
            new PdfRedactionSearchOptions
            {
                Kind = PdfRedactionSearchKind.ExactText,
                Query = "secret",
                ReasonCode = "Privacy",
                OverlayText = "REMOVED"
            });

        PdfRedactionMatch match = Assert.Single(review.Matches);
        string json = review.ToJson();

        Assert.Equal("Privacy", match.ReasonCode);
        Assert.Equal("REMOVED", match.OverlayText);
        Assert.Contains("\"reasonCode\":\"Privacy\"", json, StringComparison.Ordinal);
        Assert.Contains("\"overlayText\":\"REMOVED\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", json, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => PdfRedactionSearch.Find([Read("secret")],
            new PdfRedactionSearchOptions
            {
                Kind = PdfRedactionSearchKind.ExactText,
                Query = "secret",
                ReasonCode = " "
            }));
    }

    [Fact]
    public void ReviewCanExcludeAndRestoreAllMatches()
    {
        PdfRedactionReview original = PdfRedactionSearch.Find([Read("secret secret")],
            new PdfRedactionSearchOptions
            {
                Kind = PdfRedactionSearchKind.ExactText,
                Query = "secret"
            });

        PdfRedactionReview excluded = original.ExcludeAll();
        PdfRedactionReview restored = excluded.IncludeAll();

        Assert.Equal(2, original.Included.Count);
        Assert.Empty(excluded.Included);
        Assert.Equal(2, restored.Included.Count);
        Assert.Equal(2, excluded.Matches.Count);
    }

    [Fact]
    public void ManualRegionsUseTheSameReviewAndPrivacySafeReport()
    {
        PdfRedactionReview review = PdfRedactionReview.FromRegions([
            new PdfRedactionRegion(2, new PdfContentBounds(10, 20, 110, 70),
                "Private image", "REMOVED")
        ]);

        PdfRedactionMatch region = Assert.Single(review.Included);
        string json = review.ToJson();

        Assert.Equal("region:0", region.Id);
        Assert.Equal(PdfRedactionTargetKind.PageRegion, region.TargetKind);
        Assert.Equal(2, region.PageIndex);
        Assert.Equal(100, region.Bounds.Width);
        Assert.Equal(0, region.WordCount);
        Assert.DoesNotContain("\"text\":\"", json, StringComparison.Ordinal);
        Assert.Contains("\"targetKind\":1", json, StringComparison.Ordinal);
        Assert.Empty(review.Exclude(region.Id).Included);
    }

    [Theory]
    [InlineData(-1, 0, 0, 10, 10)]
    [InlineData(0, 10, 0, 10, 10)]
    [InlineData(0, 0, 10, 10, 10)]
    [InlineData(0, 0, 0, double.PositiveInfinity, 10)]
    public void ManualRegionsRejectInvalidPagesAndGeometry(
        int pageIndex, double left, double bottom, double right, double top)
    {
        Assert.ThrowsAny<ArgumentException>(() => PdfRedactionReview.FromRegions([
            new PdfRedactionRegion(pageIndex, new PdfContentBounds(left, bottom, right, top))
        ]));
    }

    private static PdfPageContent Read(string text)
    {
        string content = $"BT /F1 12 Tf 20 100 Td ({text}) Tj ET";
        string stream = $"<< /Length {Encoding.Latin1.GetByteCount(content)} >>\nstream\n{content}\nendstream";
        string[] objects = ["<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 300 400] /Resources << /Font << /F1 4 0 R >> >> >>",
            "<< /Type /Page /Parent 2 0 R /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", stream];
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int index = 0; index < objects.Length; index++)
        {
            offsets.Add(Encoding.Latin1.GetByteCount(pdf.ToString()));
            pdf.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        int xref = Encoding.Latin1.GetByteCount(pdf.ToString());
        pdf.Append($"xref\n0 6\n0000000000 65535 f \n");
        foreach (int offset in offsets) pdf.Append($"{offset:0000000000} 00000 n \n");
        pdf.Append($"trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return new PdfPageContentReader(PdfDocument.Open(Encoding.Latin1.GetBytes(pdf.ToString()))).Read(0);
    }
}
