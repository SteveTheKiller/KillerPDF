using System.Buffers.Binary;
using KillerPDF.Presentation;
using KillerPDF.Services;
using KillerPdf.Engine.ColorManagement;
using KillerPdf.Engine.Rendering;
using Xunit;
using PdfRenderedPage = KillerPDF.Services.PdfRenderedPage;

namespace KillerPDF.Tests;

public sealed class PdfViewingFrameConversionTests
{
    // Linear sRGB red through the identity-XYZ monitor: D50 XYZ (0.4360747, 0.2225045, 0.0139322)
    // quantizes to device bytes R=111, G=57, B=4, so BGRA is 4, 57, 111, 255.
    private static readonly byte[] MonitorRed = [4, 57, 111, 255];
    private static readonly byte[] SrgbRed = [0, 0, 255, 255];

    [Fact]
    public void AProofThePageCannotTakeKeepsMonitorConversionAndSaysTheProofIsUnavailable()
    {
        var proof = new PdfDocumentProofSettings(enabled: true);

        var result = PdfViewingFrameConversion.Render(
            requested => requested?.Enabled == true ? throw new NotSupportedException("No proof here.") : RedFrame(),
            Compatible, proof, Monitor(), default);

        Assert.Equal(MonitorRed, result.Pixels);
        Assert.True(result.ProofUnavailable);
        Assert.Null(result.DisplayFallbackOperation);
    }

    [Fact]
    public void APageTheFloatRendererCannotDrawShowsTheDisplayFallback()
    {
        var result = PdfViewingFrameConversion.Render(_ => throw new NotSupportedException("Unported paint."),
            Compatible, null, Monitor(), default);

        Assert.Equal(SrgbRed, result.Pixels);
        Assert.Equal(PdfViewingFrameConversion.PreciseRenderUnavailable, result.DisplayFallbackOperation);
        Assert.False(result.ProofUnavailable);
    }

    [Fact]
    public void AnUnavailableProofWithoutMonitorConversionUsesTheCompatibleRenderAndSaysSo()
    {
        var result = PdfViewingFrameConversion.Render(_ => throw new NotSupportedException("No proof here."),
            Compatible, new PdfDocumentProofSettings(enabled: true), null, default);

        Assert.Equal(SrgbRed, result.Pixels);
        Assert.True(result.ProofUnavailable);
        Assert.Null(result.DisplayFallbackOperation);
    }

    [Fact]
    public void AnOrdinaryPageIsConvertedForTheMonitor()
    {
        var result = PdfViewingFrameConversion.Render(_ => RedFrame(), Compatible, null, Monitor(), default);

        Assert.Equal(MonitorRed, result.Pixels);
        Assert.False(result.ProofUnavailable);
        Assert.Null(result.DisplayFallbackOperation);
    }

    [Fact]
    public void APrimaryPageThatRefusesTheProofKeepsMonitorConversionWithoutADisplayFallback()
    {
        // The primary page's direct float render refuses the proof; the viewing fallback then
        // draws it unproofed and still converts it for the monitor, so monitor color is not lost.
        var proof = new PdfDocumentProofSettings(enabled: true);
        var kept = PdfViewingFrameConversion.Render(
            requested => requested?.Enabled == true ? throw new NotSupportedException("No proof here.") : RedFrame(),
            Compatible, proof, Monitor(), default);

        Assert.Equal(MonitorRed, kept.Pixels);
        Assert.Null(PdfViewingFrameConversion.LinearFallbackOperation(monitorRequested: true, kept.DisplayFallbackOperation));
    }

    [Fact]
    public void ACompositorPrimaryPageWithoutAFloatFrameStillShowsTheDisplayFallback()
    {
        // Without application monitor conversion the page only reaches the compositor as a float
        // frame; drawing the sRGB fallback instead is a display fallback.
        var kept = PdfViewingFrameConversion.Render(_ => throw new NotSupportedException("No proof here."),
            Compatible, new PdfDocumentProofSettings(enabled: true), null, default);

        Assert.Equal(PdfViewingFrameConversion.PreciseRenderUnavailable,
            PdfViewingFrameConversion.LinearFallbackOperation(monitorRequested: false, kept.DisplayFallbackOperation));
    }

    [Fact]
    public void AProofWhoseSavedProfileIsMissingNamesTheProfileNotThePage()
    {
        var keys = PdfViewingFrameConversion.ProofNoticeKeys(proofRequested: true, profileAvailable: false,
            proofRefusedForPage: true, preferenceNoticeKey: "Str_CM_ProfileMissing");

        Assert.Equal("Str_CM_ProfileMissing", keys.Page);
        Assert.Null(keys.StatusOnly);
    }

    [Fact]
    public void AnUnavailableProfileChosenInTheDialogSaysTheProfileIsUnavailable()
    {
        var keys = PdfViewingFrameConversion.ProofNoticeKeys(true, false, true, null);

        Assert.Equal("Str_CM_ProfileUnavailable", keys.Page);
    }

    [Fact]
    public void APageThatRefusesAnAvailableProofStillSaysSoAndKeepsTheOpenNotice()
    {
        var keys = PdfViewingFrameConversion.ProofNoticeKeys(true, true, true, "Str_CM_PreferencesSaveFailed");

        Assert.Equal("Str_CM_ProofUnavailablePage", keys.Page);
        Assert.Equal("Str_CM_PreferencesSaveFailed", keys.StatusOnly);
    }

    [Fact]
    public void AnOpenNoticeSurvivesARenderWarningThatIsNotAboutTheProof()
    {
        var keys = PdfViewingFrameConversion.ProofNoticeKeys(false, false, false, "Str_CM_PreferencesLoadFailed");

        Assert.Null(keys.Page);
        Assert.Equal("Str_CM_PreferencesLoadFailed", keys.StatusOnly);
    }

    private static PdfRenderedFrame RedFrame() => new(1, 1, [1f, 0f, 0f, 1f], PdfFrameColorEncoding.LinearSrgbD65);

    private static PdfRenderedPage Compatible() => new(1, 1, [0, 0, 255, 255], PdfRenderBackend.Engine, null);

    internal static PdfSdrDisplayConverter Monitor()
    {
        var converter = new PdfSdrDisplayConverter((_, _) => PdfColorProfile.Load(MonitorBytes()));
        converter.UpdateDisplay(new(1, PdfDisplayDiscoveryStatus.Available, "desktop-a",
            new PdfDisplayBounds(0, 0, 1920, 1080), null, null)
        {
            TargetAssociation = new(PdfDisplayTargetStatus.SingleTarget,
                [new PdfDisplayTarget("output-a", null)
                {
                    ColorState = new(PdfDisplayColorMode.ConventionalSdr, 15, 1, 0, 0, 8, null, null),
                    StandardProfile = new(PdfDisplayProfileStatus.Named, 0, "monitor.icc", null, null)
                }])
        });
        return converter;
    }

    // A v4 RGB display profile with identity curves and an identity XYZ matrix.
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
