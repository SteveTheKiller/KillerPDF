using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfDisplayPresentationPolicyTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData(0u, null)]
    [InlineData(2500u, 5)]
    public void HdrWithoutUsableReportedWhiteCannotClaimCompositorPresentation(uint? rawWhite, int? error)
    {
        var target = new PdfDisplayTarget("hdr-output", null)
        {
            ColorState = new(PdfDisplayColorMode.Hdr, 15, 3, 2, 0, 10, null, null),
            SdrWhiteLevel = new(rawWhite, 200, error)
        };
        var display = new PdfDisplayDiscoverySnapshot(44, PdfDisplayDiscoveryStatus.Available,
            "desktop", new PdfDisplayBounds(0, 0, 1920, 1080), null, null)
        {
            TargetAssociation = new(PdfDisplayTargetStatus.SingleTarget, [target])
        };
        var decision = PdfDisplayPresentationPolicy.Resolve(display);
        Assert.Equal(PdfPresentationMode.Unavailable, decision.Mode);
        Assert.Equal(PdfMonitorTransformOwner.Unknown, decision.TransformOwner);
        Assert.NotNull(decision.FailureOperation);
    }

    [Theory]
    [InlineData(PdfDisplayDiscoveryStatus.Unavailable, PdfDisplayTargetStatus.SingleTarget, 1)]
    [InlineData(PdfDisplayDiscoveryStatus.Available, PdfDisplayTargetStatus.Unavailable, 1)]
    [InlineData(PdfDisplayDiscoveryStatus.Available, PdfDisplayTargetStatus.MultipleTargets, 2)]
    [InlineData(PdfDisplayDiscoveryStatus.Available, PdfDisplayTargetStatus.SingleTarget, 0)]
    [InlineData(PdfDisplayDiscoveryStatus.Available, PdfDisplayTargetStatus.SingleTarget, 2)]
    public void IncompleteOrAmbiguousAssociationCannotClaimConversionOwnership(
        PdfDisplayDiscoveryStatus status, PdfDisplayTargetStatus associationStatus, int count)
    {
        var targets = Enumerable.Range(0, count).Select(index => new PdfDisplayTarget("output" + index, null)
        {
            ColorState = new(PdfDisplayColorMode.AdvancedColorSdr, 15, 3, 1, 0, 10, null, null)
        });
        var display = new PdfDisplayDiscoverySnapshot(45, status, "desktop",
            new PdfDisplayBounds(0, 0, 1920, 1080), null, null)
        {
            TargetAssociation = new(associationStatus, targets)
        };
        var decision = PdfDisplayPresentationPolicy.Resolve(display);
        Assert.Equal(PdfPresentationMode.Unavailable, decision.Mode);
        Assert.Equal(PdfMonitorTransformOwner.Unknown, decision.TransformOwner);
        Assert.NotNull(decision.FailureOperation);
    }

    [Fact]
    public void ConventionalSdrRequiresApplicationConversionInsteadOfCompositorConversion()
    {
        var target = new PdfDisplayTarget("legacy-output", null)
        {
            ColorState = new(PdfDisplayColorMode.ConventionalSdr, 15, 1, 0, 0, 8, null, null),
            StandardProfile = new(PdfDisplayProfileStatus.Named, 0, "monitor.icc", null, null),
            SdrWhiteLevel = new(2500, 200, null)
        };
        var display = new PdfDisplayDiscoverySnapshot(43, PdfDisplayDiscoveryStatus.Available,
            "legacy-desktop", new PdfDisplayBounds(0, 0, 1920, 1080), null, null)
        {
            TargetAssociation = new(PdfDisplayTargetStatus.SingleTarget, [target])
        };

        Assert.Equal(new PdfDisplayPresentationDecision(PdfPresentationMode.CompatibilitySdr,
            PdfMonitorTransformOwner.Application, 1, null), PdfDisplayPresentationPolicy.Resolve(display));
    }

    [Fact]
    public void AdvancedColorSdrUsesDisplayWhiteWithoutHdrWhiteScaling()
    {
        var target = new PdfDisplayTarget("sdr-output", null)
        {
            ColorState = new(PdfDisplayColorMode.AdvancedColorSdr, 15, 3, 1, 0, 10, null, null),
            SdrWhiteLevel = new(2500, 200, null)
        };
        var display = new PdfDisplayDiscoverySnapshot(42, PdfDisplayDiscoveryStatus.Available,
            "sdr-desktop", new PdfDisplayBounds(0, 0, 1920, 1080), null, null)
        {
            TargetAssociation = new(PdfDisplayTargetStatus.SingleTarget, [target])
        };

        Assert.Equal(new PdfDisplayPresentationDecision(PdfPresentationMode.LinearFloat,
            PdfMonitorTransformOwner.Compositor, 1, null), PdfDisplayPresentationPolicy.Resolve(display));
    }

    [Fact]
    public void ActiveHdrWith200NitSdrWhiteUsesCompositorLinearFloatAtTwoPointFiveScale()
    {
        var target = new PdfDisplayTarget("adapter-7:target-3", "HDR reference output")
        {
            ColorState = new(PdfDisplayColorMode.Hdr, 15, 3, 2, 0, 10, null, null),
            StandardProfile = new(PdfDisplayProfileStatus.NoProfile, 0, null, null, null),
            SdrWhiteLevel = new(2500, 200, null)
        };
        var display = new PdfDisplayDiscoverySnapshot(41, PdfDisplayDiscoveryStatus.Available,
            "desktop-display-2", new PdfDisplayBounds(1920, -180, 4480, 1260), null, null)
        {
            TargetAssociation = new(PdfDisplayTargetStatus.SingleTarget, [target])
        };

        var decision = PdfDisplayPresentationPolicy.Resolve(display);

        // HDR scRGB 1.0 represents 80 nits, so the independent 200-nit example needs 2.5.
        // https://learn.microsoft.com/en-us/windows/win32/direct3darticles/high-dynamic-range
        Assert.Equal(new PdfDisplayPresentationDecision(PdfPresentationMode.LinearFloat,
            PdfMonitorTransformOwner.Compositor, 2.5, null), decision);
    }
}
