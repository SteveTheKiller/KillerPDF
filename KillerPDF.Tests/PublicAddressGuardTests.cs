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
    [InlineData("64:ff9b::192.168.1.1")]
    [InlineData("64:ff9b::7f00:1")]
    [InlineData("64:ff9b:1::c0a8:101")]
    [InlineData("2002:c0a8:0101::1")]
    [InlineData("2002:0a00:0001::")]
    [InlineData("::192.168.1.1")]
    [InlineData("::10.0.0.1")]
    [InlineData("192.0.2.10")]
    [InlineData("198.51.100.7")]
    [InlineData("203.0.113.200")]
    [InlineData("198.18.0.1")]
    [InlineData("198.19.255.255")]
    public void LocalAddressesAreRefused(string address) =>
        Assert.False(PublicAddressGuard.IsPublic(IPAddress.Parse(address)));

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("172.32.0.1")]
    [InlineData("2606:4700::1111")]
    [InlineData("64:ff9b::8.8.8.8")]
    [InlineData("2002:0808:0808::1")]
    public void PublicAddressesAreAllowed(string address) =>
        Assert.True(PublicAddressGuard.IsPublic(IPAddress.Parse(address)));

    [Fact]
    public async Task ADownloadAimedAtThisMachineNeverConnects()
    {
        // A live listener, so a refused port cannot pass for the guard: the download must fail with the
        // guard's own message and the listener must never see a connection.
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var http = new HttpClient(PublicAddressGuard.CreateHandler()) { Timeout = TimeSpan.FromSeconds(10) };
            foreach (string host in new[] { "127.0.0.1", "localhost" })
            {
                HttpRequestException refused = await Assert.ThrowsAsync<HttpRequestException>(
                    () => http.GetAsync($"http://{host}:{port}/x.pdf"));
                Assert.Contains("not on the public internet", Messages(refused));
            }
            Assert.False(listener.Pending(), "The guarded download connected to this machine.");
        }
        finally { listener.Stop(); }
    }

    private static string Messages(Exception? exception)
    {
        var text = new System.Text.StringBuilder();
        for (; exception is not null; exception = exception.InnerException) text.AppendLine(exception.Message);
        return text.ToString();
    }

    [Fact]
    public void ADownloadNeverGoesThroughAProxyThatWouldReachTheNetworkForIt()
    {
        using var handler = PublicAddressGuard.CreateHandler();
        Assert.False(handler.UseProxy);
    }
}
