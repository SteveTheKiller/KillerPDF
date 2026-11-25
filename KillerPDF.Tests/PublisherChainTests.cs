using System.Security;
using System.Security.Cryptography.X509Certificates;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class PublisherChainTests
{
    private const string Certum = "D3DD483E2BBF4C05E8AF10F5FA7626CFD3DC3092";

    [Fact]
    public void ACertificateUnderAnotherRootIsRefusedHoweverItIsNamed()
    {
        Assert.Throws<SecurityException>(() =>
            PublisherChain.Decide("0563B8630D62D75ABBC8AB1E4BDFB5A899B24D43", []));
        Assert.Throws<SecurityException>(() => PublisherChain.Decide(null, []));
    }

    [Fact]
    public void ARevokedCertificateIsRefused()
    {
        Assert.Throws<SecurityException>(() =>
            PublisherChain.Decide(Certum, [X509ChainStatusFlags.Revoked]));
    }

    [Fact]
    public void AnUnreachableRevocationServerDoesNotBlockTheUpdate()
    {
        PublisherChain.Decide(Certum.ToLowerInvariant(),
            [X509ChainStatusFlags.RevocationStatusUnknown, X509ChainStatusFlags.OfflineRevocation]);
    }
}
