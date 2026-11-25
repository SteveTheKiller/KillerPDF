using System.Security;
using System.Security.Cryptography.X509Certificates;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class PublisherChainTests
{
    private const string Certum = "D3DD483E2BBF4C05E8AF10F5FA7626CFD3DC3092";
    private const string PublisherKey = "E09F43EEE1DF45D4FDE291303F671C978DFC992E59FAD9DF29367B7223F0A83B";

    [Fact]
    public void ACertificateUnderAnotherRootIsRefusedHoweverItIsNamed()
    {
        Assert.Throws<SecurityException>(() =>
            PublisherChain.Decide("0563B8630D62D75ABBC8AB1E4BDFB5A899B24D43", PublisherKey, [], elevated: false));
        Assert.Throws<SecurityException>(() => PublisherChain.Decide(null, PublisherKey, [], elevated: false));
    }

    [Fact]
    public void ACertificateUnderThePinnedRootOnAnotherKeyIsRefused()
    {
        // Certum issues code-signing certificates to anyone named Stephen Riley; only the
        // publisher's own key is accepted.
        Assert.Throws<SecurityException>(() => PublisherChain.Decide(Certum,
            "0000000000000000000000000000000000000000000000000000000000000000", [], elevated: false));
        Assert.Throws<SecurityException>(() => PublisherChain.Decide(Certum, null, [], elevated: false));
        PublisherChain.Decide(Certum, PublisherKey.ToLowerInvariant(), [], elevated: false);
    }

    [Fact]
    public void ARevokedCertificateIsRefused()
    {
        Assert.Throws<SecurityException>(() =>
            PublisherChain.Decide(Certum, PublisherKey, [X509ChainStatusFlags.Revoked], elevated: false));
    }

    [Fact]
    public void AnUnreachableRevocationServerDoesNotBlockAPerUserUpdate()
    {
        PublisherChain.Decide(Certum.ToLowerInvariant(), PublisherKey,
            [X509ChainStatusFlags.RevocationStatusUnknown, X509ChainStatusFlags.OfflineRevocation], elevated: false);
    }

    [Fact]
    public void AnElevatedUpdateRefusesRevocationItCouldNotCheck()
    {
        Assert.Throws<SecurityException>(() => PublisherChain.Decide(Certum, PublisherKey,
            [X509ChainStatusFlags.RevocationStatusUnknown], elevated: true));
        Assert.Throws<SecurityException>(() => PublisherChain.Decide(Certum, PublisherKey,
            [X509ChainStatusFlags.OfflineRevocation], elevated: true));
        PublisherChain.Decide(Certum, PublisherKey, [], elevated: true);
    }
}
