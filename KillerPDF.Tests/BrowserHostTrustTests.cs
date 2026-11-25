using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

// Any website can hand KillerPDF a killerpdf:// link after one browser prompt (KP-SEC-07). KillerPDF names
// the host and asks first, unless the user chose to always open PDFs from that host.
public sealed class BrowserHostTrustTests
{
    [Fact]
    public void AHostIsTrustedOnlyAfterItIsAddedAndMatchesWithoutRegardToCase()
    {
        string saved = BrowserHostTrust.Add(null, new Uri("https://Docs.Example.com/a.pdf"));

        Assert.True(BrowserHostTrust.IsTrusted(saved, new Uri("https://docs.example.com/other.pdf")));
        Assert.False(BrowserHostTrust.IsTrusted(saved, new Uri("https://example.com/a.pdf")));
        Assert.False(BrowserHostTrust.IsTrusted(saved, new Uri("https://evil-docs.example.com/a.pdf")));
        Assert.False(BrowserHostTrust.IsTrusted(null, new Uri("https://docs.example.com/a.pdf")));
    }

    [Fact]
    public void AddingKeepsEarlierHostsAndDoesNotRepeatOne()
    {
        string saved = BrowserHostTrust.Add(null, new Uri("https://a.example/x.pdf"));
        saved = BrowserHostTrust.Add(saved, new Uri("https://b.example/x.pdf"));
        saved = BrowserHostTrust.Add(saved, new Uri("https://A.example/y.pdf"));

        Assert.Equal("a.example;b.example", saved);
    }

    [Fact]
    public void AnInternationalHostIsShownAndStoredInItsAsciiForm()
    {
        var target = new Uri("https://xn--80ak6aa92e.com/a.pdf");
        Assert.Equal("xn--80ak6aa92e.com", BrowserHostTrust.DisplayHost(target));
        Assert.True(BrowserHostTrust.IsTrusted(BrowserHostTrust.Add(null, target), target));
    }
}
