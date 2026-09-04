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
        PdfPageContent page = Read("SSN 123-45-6789 but not 0123-45-67890");

        PdfRedactionMatch match = Assert.Single(PdfRedactionSearch.Find([page],
            new PdfRedactionSearchOptions
            {
                Kind = PdfRedactionSearchKind.SocialSecurityNumber
            }).Matches);

        Assert.Equal("123-45-6789", match.Text);
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
