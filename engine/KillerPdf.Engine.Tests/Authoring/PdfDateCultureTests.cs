using System.Globalization;
using System.Text;
using KillerPdf.Engine.Authoring;
using Xunit;

namespace KillerPdf.Engine.Tests.Authoring;

[Collection(nameof(CurrentCultureCollection))]
public sealed class PdfDateCultureTests
{
    [Theory]
    [InlineData("th-TH")]
    [InlineData("ar-SA")]
    [InlineData("fa-IR")]
    public void Build_WritesGregorianDatesUnderNonGregorianRegionSettings(string cultureName)
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(cultureName);
            var offset = TimeSpan.FromHours(-7);
            var metadata = new PdfAnnotationMetadata
            {
                CreationDate = new DateTimeOffset(2026, 8, 22, 12, 34, 56, offset),
                ModificationDate = new DateTimeOffset(2026, 8, 23, 13, 45, 0, offset)
            };

            byte[] pdf = new PdfDocumentBuilder()
                .SetMetadata(new PdfDocumentMetadata
                {
                    CreationDate = new DateTimeOffset(2026, 8, 21, 11, 22, 33, offset)
                })
                .AddBlankPage()
                .AddUriLink(0, 10, 10, 50, 20, "https://example.com/", annotationMetadata: metadata)
                .AddAttachment("evidence.txt", "evidence"u8.ToArray(), "text/plain", null,
                    PdfAssociatedFileRelationship.Data,
                    new DateTimeOffset(2026, 8, 24, 14, 15, 16, offset),
                    new DateTimeOffset(2026, 8, 25, 15, 16, 17, offset))
                .Build();
            string text = Encoding.Latin1.GetString(pdf);

            Assert.Contains("D:20260821112233-07'00'", text);
            Assert.Contains("D:20260822123456-07'00'", text);
            Assert.Contains("D:20260823134500-07'00'", text);
            Assert.Contains("D:20260824141516-07'00'", text);
            Assert.Contains("D:20260825151617-07'00'", text);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}

[CollectionDefinition(nameof(CurrentCultureCollection), DisableParallelization = true)]
public sealed class CurrentCultureCollection;
