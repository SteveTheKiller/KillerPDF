using System.Buffers.Binary;
using KillerPdf.Engine.ColorManagement;
using Xunit;

namespace KillerPdf.Engine.Tests.ColorManagement;

public sealed class PdfColorProfileIdentityTests
{
    // Independent Python hashlib digest of the complete 156-byte fixture.
    private const string V4Digest = "7F7BE1EAABD868E6F6D9C72E24DF59BB195CCEAAEBA23644FD0C814A72D49FB1";

    [Fact]
    public void ContentIdentityMatchesDeclaredProfileDigest()
    {
        var profile = PdfColorProfile.Load(Gray(version: 4));
        Assert.Equal(V4Digest, profile.ContentSha256);
    }

    [Fact]
    public void ContentIdentitySurvivesCallerBufferMutation()
    {
        byte[] callerData = Gray(version: 4);
        var profile = PdfColorProfile.Load(callerData);
        Array.Fill(callerData, (byte)255);
        Assert.Equal(V4Digest, profile.ContentSha256);
    }

    [Fact]
    public void ContentIdentityIgnoresSuppliedBytesOutsideDeclaredProfile()
    {
        byte[] declared = Gray(version: 4);
        byte[] withTrailingBytes = new byte[declared.Length + 19];
        declared.CopyTo(withTrailingBytes, 0);
        withTrailingBytes.AsSpan(declared.Length).Fill(173);
        var exact = PdfColorProfile.Load(declared);
        var extended = PdfColorProfile.Load(withTrailingBytes);
        Assert.Equal(V4Digest, exact.ContentSha256);
        Assert.Equal(V4Digest, extended.ContentSha256);
    }

    [Fact]
    public void ContentIdentityDistinguishesChangedValidDeclaredBytes()
    {
        var v4 = PdfColorProfile.Load(Gray(version: 4));
        var v2 = PdfColorProfile.Load(Gray(version: 2));
        Assert.Equal(V4Digest, v4.ContentSha256);
        Assert.Equal("D23117CF29C6611928E1CE2C4FABA843618767B0C02B000068989E1AFC141F8F", v2.ContentSha256);
        Assert.NotEqual(v4.ContentSha256, v2.ContentSha256);
    }

    private static byte[] Gray(byte version)
    {
        byte[] data = new byte[156];
        Write32(data, 0, 156);
        data[8] = version;
        "mntr"u8.CopyTo(data.AsSpan(12));
        "GRAY"u8.CopyTo(data.AsSpan(16));
        "XYZ "u8.CopyTo(data.AsSpan(20));
        "acsp"u8.CopyTo(data.AsSpan(36));
        Write32(data, 128, 1);
        "kTRC"u8.CopyTo(data.AsSpan(132));
        Write32(data, 136, 144);
        Write32(data, 140, 12);
        "curv"u8.CopyTo(data.AsSpan(144));
        return data;
    }

    private static void Write32(byte[] data, int offset, int value)
        => BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(offset), value);
}
