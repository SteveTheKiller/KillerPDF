using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfXfaLocalesTests
{
    [Fact]
    public void ReadExposesOrderedLocaleSymbolsPatternsAndTypefaces()
    {
        PdfXfaInfo info = Info("""
            <localeSet xmlns="http://www.xfa.org/schema/xfa-locale-set/2.7/">
              <locale name="en_US" desc="English (United States)">
                <numberSymbols><numberSymbol name="decimal">.</numberSymbol><numberSymbol name="grouping">,</numberSymbol></numberSymbols>
                <numberPatterns><numberPattern name="numeric">z,zz9.zzz</numberPattern></numberPatterns>
                <datePatterns><datePattern name="short">M/D/YY</datePattern></datePatterns>
                <timePatterns><timePattern name="short">h:MM A</timePattern></timePatterns>
                <currencySymbols><currencySymbol name="isoname">USD</currencySymbol></currencySymbols>
                <typeFaces><typeface name="Myriad Pro"/></typeFaces>
              </locale>
            </localeSet>
            """);

        PdfXfaLocale locale = Assert.Single(PdfXfaLocales.Read(info));

        Assert.Equal("en_US", locale.Name);
        Assert.Equal("English (United States)", locale.Description);
        Assert.Equal(["decimal", "grouping"], locale.NumberSymbols.Select(value => value.Name));
        Assert.Equal("z,zz9.zzz", Assert.Single(locale.NumberPatterns).Value);
        Assert.Equal("M/D/YY", Assert.Single(locale.DatePatterns).Value);
        Assert.Equal("h:MM A", Assert.Single(locale.TimePatterns).Value);
        Assert.Equal("USD", Assert.Single(locale.CurrencySymbols).Value);
        Assert.Equal("Myriad Pro", Assert.Single(locale.Typefaces));
    }

    [Fact]
    public void ReadRejectsDuplicateLocaleNames()
    {
        PdfXfaInfo info = Info("""
            <localeSet><locale name="en_US"/><locale name="EN_US"/></localeSet>
            """);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => PdfXfaLocales.Read(info));

        Assert.Contains("unique", exception.Message, StringComparison.Ordinal);
    }

    private static PdfXfaInfo Info(string localeSet) => new()
    {
        IsPacketArray = true,
        Packets = [new PdfXfaPacket("localeSet", System.Text.Encoding.UTF8.GetBytes(localeSet))]
    };
}
