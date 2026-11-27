using System.Net;
using System.Net.Http;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

// A killerpdf:// open names the site and asks before downloading. A redirect to another host asks again, so the
// confirmed site cannot hand the download to one the user never saw.
public sealed class BrowserRedirectTests
{
    private sealed class Script(Func<Uri, HttpResponseMessage> answer) : HttpMessageHandler
    {
        internal readonly List<Uri> Asked = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Asked.Add(request.RequestUri!);
            return Task.FromResult(answer(request.RequestUri!));
        }
    }

    private static HttpResponseMessage Redirect(string to) =>
        new(HttpStatusCode.Found) { Headers = { Location = new Uri(to, UriKind.RelativeOrAbsolute) } };

    private static HttpResponseMessage Pdf() => new(HttpStatusCode.OK) { Content = new ByteArrayContent("%PDF-"u8.ToArray()) };

    [Fact]
    public async Task ARedirectToAnotherHostAsksAndARefusalStopsTheDownload()
    {
        var inner = new Script(uri => uri.Host == "docs.example" ? Redirect("https://elsewhere.example/a.pdf") : Pdf());
        var hostsAsked = new List<string>();
        using var http = new HttpClient(new BrowserRedirects(inner, next => { hostsAsked.Add(next.Host); return Task.FromResult(false); }));

        await Assert.ThrowsAsync<BrowserRedirectRefusedException>(() => http.GetAsync("https://docs.example/a.pdf"));
        Assert.Equal(["elsewhere.example"], hostsAsked);
        Assert.Single(inner.Asked);
    }

    [Fact]
    public async Task AnAcceptedHostAndTheSameHostAreFollowed()
    {
        var inner = new Script(uri => uri.AbsolutePath switch
        {
            "/a.pdf" => Redirect("/b.pdf"),
            "/b.pdf" => Redirect("https://cdn.example/c.pdf"),
            _ => Pdf(),
        });
        var hostsAsked = new List<string>();
        using var http = new HttpClient(new BrowserRedirects(inner, next => { hostsAsked.Add(next.Host); return Task.FromResult(true); }));

        using HttpResponseMessage response = await http.GetAsync("https://docs.example/a.pdf");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["cdn.example"], hostsAsked);
        Assert.Equal(["https://docs.example/a.pdf", "https://docs.example/b.pdf", "https://cdn.example/c.pdf"],
            inner.Asked.Select(uri => uri.AbsoluteUri));
    }

    [Fact]
    public async Task ARedirectOffHttpsOrAnEndlessLoopFails()
    {
        using var downgrade = new HttpClient(new BrowserRedirects(
            new Script(_ => Redirect("http://docs.example/a.pdf")), _ => Task.FromResult(true)));
        await Assert.ThrowsAsync<HttpRequestException>(() => downgrade.GetAsync("https://docs.example/a.pdf"));

        using var loop = new HttpClient(new BrowserRedirects(
            new Script(_ => Redirect("https://docs.example/a.pdf")), _ => Task.FromResult(true)));
        await Assert.ThrowsAsync<HttpRequestException>(() => loop.GetAsync("https://docs.example/a.pdf"));
    }

    [Fact]
    public void TheBrowserDownloadFollowsRedirectsOnlyThroughTheGate()
    {
        string open = ShellSource.Read("Shell", "ExternalOpen.cs");
        Assert.Contains("guard.AllowAutoRedirect = false;", open);
        Assert.Contains("new HttpClient(new BrowserRedirects(guard, ConfirmBrowserRedirectAsync))", open);
        Assert.DoesNotContain("new HttpClient(PublicAddressGuard.CreateHandler())", open);
    }
}
