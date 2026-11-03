using System.Buffers.Binary;
using KillerPdf.Engine.ColorManagement;
using Xunit;

namespace KillerPdf.Engine.Tests.ColorManagement;

public sealed class PdfColorProfileMetadataTests
{
    [Fact]
    public void LoadedProfileReportsItsDeclaredClassVersionAndColorSpaces()
    {
        byte[] bytes = Gray();
        var profile = PdfColorProfile.Load(bytes);
        Array.Fill(bytes, (byte)255);

        Assert.Equal("mntr", profile.ProfileClassSignature);
        Assert.Equal("GRAY", profile.DeviceColorSpaceSignature);
        Assert.Equal("XYZ ", profile.ConnectionColorSpaceSignature);
        Assert.Equal(4, profile.MajorVersion);
    }

    [Theory]
    [InlineData("scnr", 2)]
    [InlineData("mntr", 4)]
    [InlineData("prtr", 2)]
    [InlineData("spac", 4)]
    public void SupportedClassesKeepTheirOriginalSignatures(string signature, int version)
    {
        byte[] bytes = Gray();
        System.Text.Encoding.ASCII.GetBytes(signature).CopyTo(bytes, 12);
        bytes[8] = (byte)version;
        var profile = PdfColorProfile.Load(bytes);

        Assert.Equal(signature, profile.ProfileClassSignature);
        Assert.Equal(version, profile.MajorVersion);
        Assert.Equal("GRAY", profile.DeviceColorSpaceSignature);
        Assert.Equal("XYZ ", profile.ConnectionColorSpaceSignature);
    }

    [Fact]
    public void BundledCmykViewingAssumptionReportsInputClassAndLabConnectionSpace()
    {
        var profile = PdfCmykViewingDefault.LoadProfile();

        Assert.Equal("scnr", profile.ProfileClassSignature);
        Assert.Equal("CMYK", profile.DeviceColorSpaceSignature);
        Assert.Equal("Lab ", profile.ConnectionColorSpaceSignature);
        Assert.Equal(2, profile.MajorVersion);
    }

    private static byte[] Gray()
    {
        byte[] data = new byte[156];
        BinaryPrimitives.WriteInt32BigEndian(data, 156);
        data[8] = 4;
        "mntr"u8.CopyTo(data.AsSpan(12));
        "GRAY"u8.CopyTo(data.AsSpan(16));
        "XYZ "u8.CopyTo(data.AsSpan(20));
        "acsp"u8.CopyTo(data.AsSpan(36));
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(128), 1);
        "kTRC"u8.CopyTo(data.AsSpan(132));
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(136), 144);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(140), 12);
        "curv"u8.CopyTo(data.AsSpan(144));
        return data;
    }
}
