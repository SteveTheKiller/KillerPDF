using System.Net;
using System.Net.Http;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class PublicAddressGuardTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")]
    [InlineData("100.64.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("fd00::1")]
    [InlineData("::ffff:192.168.1.1")]
    public void LocalAddressesAreRefused(string address) =>
        Assert.False(PublicAddressGuard.IsPublic(IPAddress.Parse(address)));

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("172.32.0.1")]
    [InlineData("2606:4700::1111")]
    public void PublicAddressesAreAllowed(string address) =>
        Assert.True(PublicAddressGuard.IsPublic(IPAddress.Parse(address)));

    [Fact]
    public async Task ADownloadAimedAtThisMachineNeverConnects()
    {
        using var http = new HttpClient(PublicAddressGuard.CreateHandler()) { Timeout = TimeSpan.FromSeconds(10) };
        await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync("https://127.0.0.1:9/x.pdf"));
        await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync("https://localhost:9/x.pdf"));
    }
}
