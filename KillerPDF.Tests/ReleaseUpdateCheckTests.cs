using System.Net;
using System.Net.Http;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class ReleaseUpdateCheckTests
{
    [Theory]
    [InlineData("v1.8.5", false)]
    [InlineData("v1.8.4", false)]
    [InlineData("v1.8.6", true)]
    [InlineData("v1.9.0", true)]
    [InlineData("v1.8.6-enhanced", false)]
    [InlineData("v1.8.6-beta", false)]
    [InlineData("../v1.8.6", false)]
    public void OnlyNewerStableReleaseTagsAreOffered(string tag, bool offered)
    {
        string json = System.Text.Json.JsonSerializer.Serialize(new { tag_name = tag });
        Assert.Equal(offered ? tag : null,
            ReleaseUpdateCheck.ParseNewerRelease(json, new Version(1, 8, 5, 0)));
    }

    [Theory]
    [InlineData("{\"tag_name\":\"v9.0.0\",\"draft\":true}")]
    [InlineData("{\"tag_name\":\"v9.0.0\",\"prerelease\":true}")]
    [InlineData("{\"tag_name\":42}")]
    [InlineData("{}")]
    [InlineData("[]")]
    public void UnpublishedOrMalformedReleaseMetadataIsNotOffered(string json)
        => Assert.Null(ReleaseUpdateCheck.ParseNewerRelease(json, new Version(1, 8, 5)));

    [Fact]
    public void StartupChecksDefaultOnAndRememberOptOut()
    {
        Assert.True(ReleaseUpdateCheck.IsEnabled(null));
        Assert.True(ReleaseUpdateCheck.IsEnabled("1"));
        Assert.False(ReleaseUpdateCheck.IsEnabled("0"));
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, "{\"tag_name\":\"v1.8.6\",\"draft\":false,\"prerelease\":false}", "v1.8.6")]
    [InlineData(HttpStatusCode.Forbidden, "rate limited", null)]
    [InlineData(HttpStatusCode.NotFound, "", null)]
    [InlineData(HttpStatusCode.OK, "invalid JSON", null)]
    public async Task FetchUsesOfficialRepositoryAndHandlesUnavailableResponses(
        HttpStatusCode status, string body, string? expected)
    {
        using var handler = new StubHandler(status, body);
        using var client = new HttpClient(handler);
        Assert.Equal(expected, await ReleaseUpdateCheck.FindNewerReleaseAsync(client, new Version(1, 8, 5)));
        Assert.Equal("https://api.github.com/repos/SteveTheKiller/KillerPDF/releases/latest", handler.Url);
        Assert.Equal("KillerPDF-UpdateCheck", handler.UserAgent);
    }

    [Fact]
    public async Task OfflineCheckIsQuiet()
    {
        using var client = new HttpClient(new OfflineHandler());
        Assert.Null(await ReleaseUpdateCheck.FindNewerReleaseAsync(client, new Version(1, 8, 5)));
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        internal string? Url;
        internal string? UserAgent;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Url = request.RequestUri!.ToString();
            UserAgent = request.Headers.UserAgent.ToString();
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    private sealed class OfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            => Task.FromException<HttpResponseMessage>(new HttpRequestException("offline"));
    }
}
