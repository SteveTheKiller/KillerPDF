using System.Buffers.Binary;
using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.ColorManagement;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.ColorManagement;

public sealed class PdfColorTransformSelectionTests
{
    [Theory]
    [InlineData(PdfRenderingIntent.Saturation, PdfColorTransformDirection.DeviceToConnection, "A2B2",
        PdfRenderingIntent.Saturation, false)]
    [InlineData(PdfRenderingIntent.Saturation, PdfColorTransformDirection.ConnectionToDevice, "B2A2",
        PdfRenderingIntent.Saturation, false)]
    [InlineData(PdfRenderingIntent.RelativeColorimetric, PdfColorTransformDirection.ConnectionToDevice, "B2A0",
        PdfRenderingIntent.Perceptual, true)]
    public void EachDirectionReportsItsOwnSelectedTable(PdfRenderingIntent intent,
        PdfColorTransformDirection direction, string expectedTag, PdfRenderingIntent expectedTableIntent, bool fallback)
    {
        var profile = PdfColorProfile.Load(Profile("RGB ", ("A2B0", IdentityLut()), ("A2B1", IdentityLut()),
            ("A2B2", IdentityLut()), ("B2A0", IdentityLut()), ("B2A2", IdentityLut())));
        var selection = profile.GetTransformSelection(intent, direction);

        Assert.Equal(intent, selection.RequestedIntent);
        Assert.Equal(direction, selection.Direction);
        Assert.True(selection.IsAvailable);
        Assert.False(selection.IsMatrixTrc);
        Assert.Equal(expectedTag, selection.TableSignature);
        Assert.Equal(expectedTableIntent, selection.EffectiveTableIntent);
        Assert.Equal(fallback, selection.UsesIntentFallback);
        Assert.False(selection.AppliesMediaWhiteScaling);
    }

    [Theory]
    [InlineData(PdfRenderingIntent.Perceptual)]
    [InlineData(PdfRenderingIntent.Saturation)]
    public void MissingIntentVariantReportsUnavailableOnRepeatedRequests(PdfRenderingIntent intent)
    {
        var profile = PdfColorProfile.Load(Profile("RGB ", ("A2B1", IdentityLut()), ("B2A1", IdentityLut())));
        for (int attempt = 0; attempt < 2; attempt++)
            foreach (var direction in new[] { PdfColorTransformDirection.DeviceToConnection,
                PdfColorTransformDirection.ConnectionToDevice })
            {
                var selection = profile.GetTransformSelection(intent, direction);
                Assert.Equal(intent, selection.RequestedIntent);
                Assert.Equal(direction, selection.Direction);
                AssertUnavailable(selection);
            }
        Assert.Throws<NotSupportedException>(() => profile.ToXyz([0.2, 0.3, 0.4], new double[3], intent));
        Assert.Throws<NotSupportedException>(() => profile.FromXyz([0.2, 0.3, 0.4], new double[3], intent));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    public void InvalidIntentAndDirectionAreRejected(int invalid)
    {
        var profile = PdfCmykViewingDefault.LoadProfile();
        Assert.Equal("intent", Assert.Throws<ArgumentOutOfRangeException>(
            () => profile.GetTransformSelection((PdfRenderingIntent)invalid)).ParamName);
        Assert.Equal("direction", Assert.Throws<ArgumentOutOfRangeException>(
            () => profile.GetTransformSelection(direction: (PdfColorTransformDirection)invalid)).ParamName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AbsoluteReportsActualWhiteScalingWithoutCallingTheTableAbsolute(bool lut)
    {
        byte[] curve = new byte[12];
        "curv"u8.CopyTo(curve);
        var profile = PdfColorProfile.Load(lut
            ? Profile("RGB ", ("A2B1", IdentityLut()), ("B2A1", IdentityLut()), ("wtpt", MediaWhite()))
            : Profile("GRAY", ("kTRC", curve), ("wtpt", MediaWhite())));
        foreach (var direction in new[] { PdfColorTransformDirection.DeviceToConnection,
            PdfColorTransformDirection.ConnectionToDevice })
        {
            // Alternate requests on one profile to exercise cached variants and matrix/TRC clones.
            foreach (var intent in new[] { PdfRenderingIntent.RelativeColorimetric,
                PdfRenderingIntent.AbsoluteColorimetric, PdfRenderingIntent.RelativeColorimetric,
                PdfRenderingIntent.AbsoluteColorimetric })
            {
                var selection = profile.GetTransformSelection(intent, direction);
                Assert.Equal(intent, selection.RequestedIntent);
                Assert.Equal(direction, selection.Direction);
                Assert.True(selection.IsAvailable);
                Assert.Equal(!lut, selection.IsMatrixTrc);
                Assert.Equal(lut ? direction == PdfColorTransformDirection.DeviceToConnection ? "A2B1" : "B2A1"
                    : null, selection.TableSignature);
                Assert.Equal(lut ? PdfRenderingIntent.RelativeColorimetric : (PdfRenderingIntent?)null,
                    selection.EffectiveTableIntent);
                Assert.False(selection.UsesIntentFallback);
                Assert.Equal(intent == PdfRenderingIntent.AbsoluteColorimetric, selection.AppliesMediaWhiteScaling);
            }
        }
    }

    [Theory]
    [InlineData(PdfRenderingIntent.Perceptual)]
    [InlineData(PdfRenderingIntent.RelativeColorimetric)]
    [InlineData(PdfRenderingIntent.Saturation)]
    [InlineData(PdfRenderingIntent.AbsoluteColorimetric)]
    public void MatrixTrcHasNoTableIntentOrFallbackAndNoScalingWithoutMediaWhite(PdfRenderingIntent intent)
    {
        byte[] curve = new byte[12];
        "curv"u8.CopyTo(curve);
        var profile = PdfColorProfile.Load(Profile("GRAY", ("kTRC", curve)));
        foreach (var direction in new[] { PdfColorTransformDirection.DeviceToConnection,
            PdfColorTransformDirection.ConnectionToDevice })
        {
            var selection = profile.GetTransformSelection(intent, direction);
            Assert.Equal(intent, selection.RequestedIntent);
            Assert.Equal(direction, selection.Direction);
            if (intent == PdfRenderingIntent.AbsoluteColorimetric)
            {
                // Without media white an absolute transform would be relative in disguise.
                Assert.False(selection.IsAvailable);
                continue;
            }
            Assert.True(selection.IsAvailable);
            Assert.True(selection.IsMatrixTrc);
            Assert.Null(selection.TableSignature);
            Assert.Null(selection.EffectiveTableIntent);
            Assert.False(selection.UsesIntentFallback);
            Assert.False(selection.AppliesMediaWhiteScaling);
        }
    }

    [Theory]
    [InlineData(PdfColorTransformDirection.DeviceToConnection, "A2B1")]
    [InlineData(PdfColorTransformDirection.ConnectionToDevice, "B2A1")]
    public void RelativeRequestReportsItsActualColorimetricTable(PdfColorTransformDirection direction, string tag)
    {
        var profile = PdfColorProfile.Load(Profile("RGB ", ("A2B0", IdentityLut()),
            ("A2B1", IdentityLut()), ("B2A0", IdentityLut()), ("B2A1", IdentityLut())));
        var selection = profile.GetTransformSelection(direction: direction);

        Assert.Equal(PdfRenderingIntent.RelativeColorimetric, selection.RequestedIntent);
        Assert.Equal(direction, selection.Direction);
        Assert.True(selection.IsAvailable);
        Assert.False(selection.IsMatrixTrc);
        Assert.Equal(tag, selection.TableSignature);
        Assert.Equal(PdfRenderingIntent.RelativeColorimetric, selection.EffectiveTableIntent);
        Assert.False(selection.UsesIntentFallback);
        Assert.False(selection.AppliesMediaWhiteScaling);
    }

    [Fact]
    public void BundledCmykMissingReverseIsUnavailableAndStillThrowsOnConversion()
    {
        var profile = PdfCmykViewingDefault.LoadProfile();
        var selection = profile.GetTransformSelection(direction: PdfColorTransformDirection.ConnectionToDevice);

        Assert.Equal(PdfRenderingIntent.RelativeColorimetric, selection.RequestedIntent);
        Assert.Equal(PdfColorTransformDirection.ConnectionToDevice, selection.Direction);
        AssertUnavailable(selection);
        Assert.Throws<NotSupportedException>(() => profile.FromXyz([0.2, 0.3, 0.4], new double[4]));
    }

    [Fact]
    public void BundledCmykPerceptualRequestUsesItsTableWithoutFallback()
    {
        var selection = PdfCmykViewingDefault.LoadProfile().GetTransformSelection(PdfRenderingIntent.Perceptual);

        Assert.Equal(PdfRenderingIntent.Perceptual, selection.RequestedIntent);
        Assert.True(selection.IsAvailable);
        Assert.False(selection.IsMatrixTrc);
        Assert.Equal("A2B0", selection.TableSignature);
        Assert.Equal(PdfRenderingIntent.Perceptual, selection.EffectiveTableIntent);
        Assert.False(selection.UsesIntentFallback);
        Assert.False(selection.AppliesMediaWhiteScaling);
    }

    [Fact]
    public void BundledCmykRelativeRequestReportsPerceptualForwardFallback()
    {
        var profile = PdfCmykViewingDefault.LoadProfile();
        Assert.Equal("73E1BA37D2BAD5BAB2A964F40A9EED96209666EFC067C3322626214BBEF234A0",
            profile.ContentSha256);

        var selection = profile.GetTransformSelection();

        Assert.Equal(PdfRenderingIntent.RelativeColorimetric, selection.RequestedIntent);
        Assert.Equal(PdfColorTransformDirection.DeviceToConnection, selection.Direction);
        Assert.True(selection.IsAvailable);
        Assert.False(selection.IsMatrixTrc);
        Assert.Equal("A2B0", selection.TableSignature);
        Assert.Equal(PdfRenderingIntent.Perceptual, selection.EffectiveTableIntent);
        Assert.True(selection.UsesIntentFallback);
        Assert.False(selection.AppliesMediaWhiteScaling);
    }

    [Fact]
    public void ProofRefusesAPrinterTableSubstitutedForTheRequestedIntent()
    {
        var complete = PdfColorProfile.Load(Profile("RGB ", ("A2B0", IdentityLut()), ("A2B1", IdentityLut()),
            ("B2A0", IdentityLut()), ("B2A1", IdentityLut())));
        // The printer has no B2A1, so a relative-colorimetric proof would silently use the perceptual B2A0.
        var printer = PdfColorProfile.Load(Profile("RGB ", ("A2B0", IdentityLut()), ("A2B1", IdentityLut()),
            ("B2A0", IdentityLut())));

        Assert.Throws<NotSupportedException>(() => new PdfProofTransform(complete, printer, complete));
        Assert.Throws<NotSupportedException>(() => new PdfProofTransform(complete, printer, complete, new PdfProofOptions()));
        Assert.Throws<NotSupportedException>(() => new PdfProofTransform(complete, printer, complete,
            PdfRenderingIntent.RelativeColorimetric, blackPointCompensation: false));
        _ = new PdfProofTransform(complete, printer, complete, PdfRenderingIntent.Perceptual, blackPointCompensation: false);
    }

    [Fact]
    public void AbsoluteIntentWithoutMediaWhiteIsUnavailableInsteadOfSilentlyRelative()
    {
        var profile = PdfColorProfile.Load(Profile("RGB ", ("A2B1", IdentityLut()), ("B2A1", IdentityLut())));
        double[] device = [0.5, 0.5, 0.5], xyz = new double[3];

        Assert.False(profile.GetTransformSelection(PdfRenderingIntent.AbsoluteColorimetric).IsAvailable);
        Assert.False(profile.GetTransformSelection(PdfRenderingIntent.AbsoluteColorimetric,
            PdfColorTransformDirection.ConnectionToDevice).IsAvailable);
        Assert.Throws<NotSupportedException>(() => profile.ToXyz(device, xyz, PdfRenderingIntent.AbsoluteColorimetric));
        Assert.Throws<NotSupportedException>(() => profile.FromXyz(xyz, device, PdfRenderingIntent.AbsoluteColorimetric));
        profile.ToXyz(device, xyz, PdfRenderingIntent.RelativeColorimetric);
    }

    [Fact]
    public void DisplayConversionRefusesAMonitorWithoutAnActualRelativeReverse()
    {
        // The monitor has no B2A1, so relative display conversion would silently use the perceptual B2A0.
        var monitor = PdfColorProfile.Load(Profile("RGB ", ("A2B0", IdentityLut()), ("A2B1", IdentityLut()),
            ("B2A0", IdentityLut())));
        var frame = new PdfRenderedFrame(1, 1, [0.5f, 0.5f, 0.5f, 1f], PdfFrameColorEncoding.LinearSrgbD65);

        Assert.Throws<NotSupportedException>(() => frame.ToDisplayBgra32(monitor));
    }

    private static void AssertUnavailable(PdfColorTransformSelection selection)
    {
        Assert.False(selection.IsAvailable);
        Assert.False(selection.IsMatrixTrc);
        Assert.Null(selection.TableSignature);
        Assert.Null(selection.EffectiveTableIntent);
        Assert.False(selection.UsesIntentFallback);
        Assert.False(selection.AppliesMediaWhiteScaling);
    }

    // Minimal ICC encoding fixtures, not profile-conformance specimens.
    private static byte[] Profile(string space, params (string Name, byte[] Data)[] tags)
    {
        int size = 132 + tags.Length * 12 + tags.Sum(tag => (tag.Data.Length + 3) & ~3);
        byte[] data = new byte[size];
        Write32(data, 0, size);
        data[8] = 4;
        "mntr"u8.CopyTo(data.AsSpan(12));
        Encoding.ASCII.GetBytes(space).CopyTo(data, 16);
        "XYZ "u8.CopyTo(data.AsSpan(20));
        "acsp"u8.CopyTo(data.AsSpan(36));
        Write32(data, 128, tags.Length);
        int offset = 132 + tags.Length * 12;
        for (int index = 0; index < tags.Length; index++)
        {
            int entry = 132 + index * 12;
            Encoding.ASCII.GetBytes(tags[index].Name).CopyTo(data, entry);
            Write32(data, entry + 4, offset);
            Write32(data, entry + 8, tags[index].Data.Length);
            tags[index].Data.CopyTo(data, offset);
            offset += (tags[index].Data.Length + 3) & ~3;
        }
        return data;
    }

    private static byte[] IdentityLut()
    {
        byte[] data = new byte[124];
        "mft2"u8.CopyTo(data);
        data[8] = data[9] = 3;
        data[10] = 2;
        for (int row = 0; row < 3; row++) Write32(data, 12 + row * 16, 65536);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(48), 2);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(50), 2);
        int offset = 52;
        void Write(ushort value)
        {
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(offset), value);
            offset += 2;
        }
        for (int channel = 0; channel < 3; channel++) { Write(0); Write(65535); }
        for (int cell = 0; cell < 8; cell++)
            for (int channel = 0; channel < 3; channel++)
                Write((ushort)(((cell >> (2 - channel)) & 1) * 65535));
        for (int channel = 0; channel < 3; channel++) { Write(0); Write(65535); }
        return data;
    }

    private static byte[] MediaWhite()
    {
        byte[] data = new byte[20];
        "XYZ "u8.CopyTo(data);
        Write32(data, 8, 49152);
        Write32(data, 12, 57344);
        Write32(data, 16, 32768);
        return data;
    }

    private static void Write32(byte[] data, int offset, int value)
        => BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(offset), value);
}
