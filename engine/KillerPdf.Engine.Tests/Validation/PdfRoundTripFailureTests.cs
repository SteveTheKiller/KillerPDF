using System.Globalization;
using KillerPdf.Engine.Validation;
using Xunit;

namespace KillerPdf.Engine.Tests.Validation;

public sealed class PdfRoundTripFailureTests
{
    public static TheoryData<string> SupportedCultures => new()
    {
        "en-US", "de-DE", "fr-FR", "es", "it-IT", "cs-CZ", "hu-HU", "pl-PL",
        "ru-RU", "tr-TR", "ja-JP", "zh-CN", "zh-TW", "kk-KZ", "bn"
    };

    [Theory]
    [MemberData(nameof(SupportedCultures))]
    public void EveryFailureHasTranslatedTextAndPreservesItsNumericDetails(string name)
    {
        CultureInfo culture = CultureInfo.GetCultureInfo(name);
        foreach (PdfRoundTripFailureCode code in Enum.GetValues<PdfRoundTripFailureCode>())
        {
            var failure = new PdfRoundTripFailure(code, 1234, 5678, 9012);
            string message = failure.Format(culture);
            Assert.False(string.IsNullOrWhiteSpace(message));
            Assert.DoesNotContain("{", message);
            Assert.DoesNotContain("}", message);
            if (name != "en-US")
                Assert.NotEqual(failure.Format(CultureInfo.GetCultureInfo("en-US")), message);
            if (code == PdfRoundTripFailureCode.RewriteMismatch)
            {
                Assert.Contains(1234.ToString("N0", culture), message);
                Assert.Contains(5678.ToString("N0", culture), message);
                Assert.Contains(9012.ToString("N0", culture), message);
            }
        }
    }

    [Fact]
    public void FormatFallsBackToEnglishForUnsupportedLanguages()
    {
        var failure = new PdfRoundTripFailure(PdfRoundTripFailureCode.SourceInspection);
        Assert.Equal("The source PDF failed structural inspection.",
            failure.Format(CultureInfo.GetCultureInfo("fi-FI")));
        Assert.Equal(failure.Format(CultureInfo.GetCultureInfo("en-US")),
            failure.Format(CultureInfo.InvariantCulture));
    }

    [Fact]
    public void ValidatorLocalizesTheExistingFailureMessageAndRetainsStructuredCode()
    {
        CultureInfo previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("de-DE");
            PdfRoundTripResult result = PdfRoundTripValidator.Validate("broken"u8.ToArray());
            Assert.Equal(PdfRoundTripFailureCode.SourceInspection, result.Failure?.Code);
            Assert.Equal("Die Strukturprüfung der Quell-PDF ist fehlgeschlagen.", result.FailureMessage);
            Assert.Equal("The source PDF failed structural inspection.",
                result.Failure!.Format(CultureInfo.GetCultureInfo("en-US")));
            Assert.Equal("de-DE", CultureInfo.CurrentUICulture.Name);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    [Fact]
    public async Task ExplicitCulturesCanBeFormattedConcurrentlyWithoutChangingTheCallerCulture()
    {
        CultureInfo previous = CultureInfo.CurrentUICulture;
        var failure = new PdfRoundTripFailure(PdfRoundTripFailureCode.SourceInspection);
        string[] messages = await Task.WhenAll(
            Task.Run(() => failure.Format(CultureInfo.GetCultureInfo("de-DE"))),
            Task.Run(() => failure.Format(CultureInfo.GetCultureInfo("fr-FR"))));
        Assert.Equal("Die Strukturprüfung der Quell-PDF ist fehlgeschlagen.", messages[0]);
        Assert.Equal("La vérification de la structure du PDF source a échoué.", messages[1]);
        Assert.Same(previous, CultureInfo.CurrentUICulture);
    }
}
