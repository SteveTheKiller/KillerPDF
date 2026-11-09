using System.Buffers.Binary;
using System.IO;
using KillerPDF.Presentation;
using KillerPdf.Engine.ColorManagement;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPDF.Tests;

public sealed class PdfSdrDisplayConverterTests
{
    [Fact]
    public void CancellationDuringAProfileReadRemainsCancellationRatherThanAFallbackDiagnosis()
    {
        using var cancellation = new CancellationTokenSource();
        var converter = new PdfSdrDisplayConverter((name, token) =>
        {
            cancellation.Cancel();
            throw new IOException("Read interrupted");
        });
        converter.UpdateDisplay(Display(13));
        var frame = new PdfRenderedFrame(1, 1, [1f, 0f, 0f, 1f], PdfFrameColorEncoding.LinearSrgbD65);
        Assert.Throws<OperationCanceledException>(() => converter.Convert(frame, cancellation.Token));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void UnreadableOrUnsupportedProfileReturnsAnHonestUnavailableResult(int failure)
    {
        var converter = new PdfSdrDisplayConverter((name, token) => throw failure switch
        {
            0 => new IOException("File unavailable"),
            1 => new FormatException("Invalid ICC bytes"),
            2 => new NotSupportedException("Unsupported profile"),
            3 => new System.ComponentModel.Win32Exception(2),
            _ => new InvalidDataException("The Windows profile exceeds the 32 MiB input limit.")
        });
        converter.UpdateDisplay(Display(12));
        var result = converter.Convert(new PdfRenderedFrame(1, 1, [1f, 0f, 0f, 1f], PdfFrameColorEncoding.LinearSrgbD65));
        Assert.Equal(PdfSdrConversionOutcome.Unavailable, result.Outcome);
        Assert.Empty(result.BgraPixels.ToArray());
        Assert.Null(result.ProfileContentSha256);
        Assert.Equal("MonitorProfileConversionUnavailable", result.FailureOperation);
    }

    [Fact]
    public void DisplayChangeWhileReadingTheProfileRejectsOldPixels()
    {
        PdfSdrDisplayConverter converter = null!;
        converter = new PdfSdrDisplayConverter((name, token) =>
        {
            converter.UpdateDisplay(Display(11));
            return PdfColorProfile.Load(MonitorBytes());
        });
        converter.UpdateDisplay(Display(10));
        var result = converter.Convert(new PdfRenderedFrame(1, 1, [1f, 0f, 0f, 1f], PdfFrameColorEncoding.LinearSrgbD65));
        Assert.Equal(PdfSdrConversionOutcome.Stale, result.Outcome);
        Assert.Equal(10, result.DisplayGeneration);
        Assert.Empty(result.BgraPixels.ToArray());
        Assert.Null(result.ProfileContentSha256);
    }

    [Fact]
    public void AnSdrDisplayWithNoAssociatedProfileIsAnSrgbMonitorWithoutAFallback()
    {
        // Windows treats an unprofiled SDR monitor as sRGB, so its device bytes are the sRGB
        // encoding of the frame: linear red (1, 0, 0) is sRGB red, BGRA 0, 0, 255, 255.
        var converter = new PdfSdrDisplayConverter((name, token) => throw new InvalidOperationException("No profile file may be read."));
        converter.UpdateDisplay(Display(9, profile: PdfDisplayProfileStatus.NoProfile));
        var result = converter.Convert(new PdfRenderedFrame(1, 1, [1f, 0f, 0f, 1f], PdfFrameColorEncoding.LinearSrgbD65));
        Assert.Equal(PdfSdrConversionOutcome.Converted, result.Outcome);
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, result.BgraPixels.ToArray());
        Assert.Null(result.FailureOperation);
        Assert.Equal(9, result.DisplayGeneration);
    }

    [Theory]
    [InlineData(PdfDisplayProfileStatus.Unavailable)]
    [InlineData(PdfDisplayProfileStatus.CrossAdapterAmbiguous)]
    public void UnestablishedProfileReturnsNoManagedPixelsAndDoesNotReadAFile(PdfDisplayProfileStatus status)
    {
        var converter = new PdfSdrDisplayConverter((name, token) => throw new InvalidOperationException("No profile file may be read."));
        converter.UpdateDisplay(Display(9, profile: status));
        var result = converter.Convert(new PdfRenderedFrame(1, 1, [1f, 0f, 0f, 1f], PdfFrameColorEncoding.LinearSrgbD65));
        Assert.Equal(PdfSdrConversionOutcome.Unavailable, result.Outcome);
        Assert.Empty(result.BgraPixels.ToArray());
        Assert.Null(result.ProfileContentSha256);
        Assert.Equal("MonitorProfileNotEstablished", result.FailureOperation);
    }

    [Theory]
    [InlineData(PdfDisplayColorMode.AdvancedColorSdr)]
    [InlineData(PdfDisplayColorMode.Hdr)]
    public void CompositorManagedDisplaysNeverLoadOrApplyAnApplicationProfile(PdfDisplayColorMode mode)
    {
        int reads = 0;
        var converter = new PdfSdrDisplayConverter((name, token) => { reads++; return PdfColorProfile.Load(MonitorBytes()); });
        converter.UpdateDisplay(Display(8, mode));
        var result = converter.Convert(new PdfRenderedFrame(1, 1, [1f, 0f, 0f, 1f], PdfFrameColorEncoding.LinearSrgbD65));
        Assert.Equal(PdfSdrConversionOutcome.Unavailable, result.Outcome);
        Assert.Empty(result.BgraPixels.ToArray());
        Assert.Equal(0, reads);
        Assert.NotNull(result.FailureOperation);
    }

    [Fact]
    public void TheMonitorProfileIsReadOncePerDisplayObservation()
    {
        int reads = 0;
        var converter = new PdfSdrDisplayConverter((name, token) => { reads++; return PdfColorProfile.Load(MonitorBytes()); });
        var frame = new PdfRenderedFrame(1, 1, [1f, 0f, 0f, 1f], PdfFrameColorEncoding.LinearSrgbD65);
        converter.UpdateDisplay(Display(20));

        for (int page = 0; page < 5; page++)
            Assert.Equal(PdfSdrConversionOutcome.Converted, converter.Convert(frame).Outcome);
        Assert.Equal(1, reads);

        // A new display observation can name a different profile, so it is read again.
        converter.UpdateDisplay(Display(21));
        Assert.Equal(new byte[] { 4, 57, 111, 255 }, converter.Convert(frame).BgraPixels.ToArray());
        Assert.Equal(2, reads);
    }

    [Fact]
    public void ObservedSdrProfileProducesDeviceBytesWithDisplayAndProfileIdentity()
    {
        var profile = PdfColorProfile.Load(MonitorBytes());
        string? requested = null;
        var converter = new PdfSdrDisplayConverter((name, token) => { requested = name; return profile; });
        converter.UpdateDisplay(Display(7));
        var frame = new PdfRenderedFrame(1, 1, [1f, 0f, 0f, .5f], PdfFrameColorEncoding.LinearSrgbD65);

        var result = converter.Convert(frame);

        Assert.Equal(PdfSdrConversionOutcome.Converted, result.Outcome);
        Assert.Equal("monitor.icc", requested);
        Assert.Equal(7, result.DisplayGeneration);
        Assert.Equal("desktop-a", result.MonitorIdentity);
        Assert.Equal(profile.ContentSha256, result.ProfileContentSha256);
        Assert.Equal(new byte[] { 4, 57, 111, 128 }, result.BgraPixels.ToArray());
        Assert.Null(result.FailureOperation);
    }

    private static PdfDisplayDiscoverySnapshot Display(long generation,
        PdfDisplayColorMode mode = PdfDisplayColorMode.ConventionalSdr,
        PdfDisplayProfileStatus profile = PdfDisplayProfileStatus.Named) =>
        new(generation, PdfDisplayDiscoveryStatus.Available, "desktop-a", new PdfDisplayBounds(0, 0, 1920, 1080), null, null)
        {
            TargetAssociation = new(PdfDisplayTargetStatus.SingleTarget,
                [new PdfDisplayTarget("output-a", null)
                {
                    ColorState = new(mode, 15, 1, 0, 0, 8, null, null),
                    StandardProfile = new(profile, 0, profile == PdfDisplayProfileStatus.Named ? "monitor.icc" : null, null, null)
                }])
        };

    private static byte[] MonitorBytes()
    {
        var data = new byte[300];
        BinaryPrimitives.WriteUInt32BigEndian(data, 300);
        data[8] = 4;
        "mntr"u8.CopyTo(data.AsSpan(12));
        "RGB "u8.CopyTo(data.AsSpan(16));
        "XYZ "u8.CopyTo(data.AsSpan(20));
        "acsp"u8.CopyTo(data.AsSpan(36));
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(128), 6);
        string[] tags = ["rTRC", "gTRC", "bTRC", "rXYZ", "gXYZ", "bXYZ"];
        for (int index = 0; index < tags.Length; index++)
        {
            int entry = 132 + index * 12;
            System.Text.Encoding.ASCII.GetBytes(tags[index]).CopyTo(data, entry);
            int offset = index < 3 ? 204 : 216 + (index - 3) * 20;
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(entry + 4), (uint)offset);
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(entry + 8), index < 3 ? 12u : 20u);
            if (index < 3) "curv"u8.CopyTo(data.AsSpan(offset));
            else
            {
                "XYZ "u8.CopyTo(data.AsSpan(offset));
                BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(offset + 8 + (index - 3) * 4), 65536);
            }
        }
        return data;
    }
}
