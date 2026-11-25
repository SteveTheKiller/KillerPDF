using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class ExternalLinkTests
{
    [Fact]
    public void TheLaunchedTextIsTheNormalizedUriThatWasChecked()
    {
        Assert.Equal("https://example.com/a%20b?q=1", ExternalLink.LaunchTarget("HTTPS://EXAMPLE.com/a b?q=1"));
        Assert.Equal("https://www.example.com/page", ExternalLink.LaunchTarget("www.example.com/page"));
        Assert.Equal("mailto:someone@example.com", ExternalLink.LaunchTarget("mailto:someone@example.com"));
    }

    [Theory]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData(@"\\server\share\a.pdf")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ms-msdt:/id PCWDiagnostic")]
    [InlineData("search-ms:query=x")]
    [InlineData("")]
    public void TargetsOutsideTheAllowListAreRefused(string raw) =>
        Assert.Null(ExternalLink.LaunchTarget(raw));
}
