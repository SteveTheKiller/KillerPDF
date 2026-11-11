using Xunit;
using static KillerPDF.Tests.ViewerSourcePin;

namespace KillerPDF.Tests;

// SOURCE PINS (color BF-004). The viewer is not compiled into this project, so a cache hit under a monitor profile
// cannot be observed here; the written window check in the 1.9.3 notes covers behavior (needs-vm). What the identity
// is made of is tested for real in PdfSdrDisplayConverterTests (the profile content hash ignores the display
// generation and the frame, and follows the profile's bytes).
public sealed class MonitorRenderCachePinTests
{
    [Fact]
    public void TheRenderCacheKeyCarriesTheMonitorIdentityNextToTheWorkingCmykGeneration()
    {
        string tabs = Viewer("PdfViewer.Tabs.cs");

        // Both dictionaries, the eviction scan's default key, and the two accessors.
        Assert.Equal(3, Count(tabs, "PdfDocumentProofViewStamp? proofStamp, long workingCmyk, string? monitor)"));
        Assert.Contains("string? monitorIdentity = null)", Between(tabs, "internal static System.Windows.Media.Imaging.BitmapSource? TryGetCachedRender(", "// #122: cap the number"));
        Assert.Contains("KillerPDF.Services.WorkingCmykProfiles.Generation, monitorIdentity), out var b)", tabs);
        Assert.Contains("key != (page, bucket, rot, continuous, proofStamp, workingCmyk, monitorIdentity)", tabs);
        Assert.Equal(2, Count(tabs, "(page, bucket, rot, continuous, proofStamp, workingCmyk, monitorIdentity)] ="));
    }

    [Fact]
    public void TheMonitorIdentityIsTheProfileContentHashAndTheTransformOwnerNeverTheDisplayGeneration()
    {
        string tabs = Viewer("PdfViewer.Tabs.cs");
        string helper = Between(tabs, "internal bool TryMonitorCacheIdentity(", "// #122: cap the number");

        Assert.Contains("PdfMonitorTransformOwner.Application", helper);
        Assert.Contains("probe.ProfileContentSha256", helper);
        string identity = Between(helper, "string known = ", ";");
        Assert.DoesNotContain("Generation", identity);
        // Anything but a converted probe leaves the page uncached, as a monitor profile did before.
        Assert.Contains("probe.Outcome != KillerPDF.Presentation.PdfSdrConversionOutcome.Converted", helper);
        // The bucket is the resolution, untouched by the monitor.
        Assert.DoesNotContain("bucket + ", tabs);
    }

    [Fact]
    public void NoRenderPathSkipsTheCacheBecauseAMonitorProfileIsInUse()
    {
        string viewport = Viewer("PdfViewer.Viewport.cs").Replace("\r\n", "\n");

        Assert.DoesNotContain("continuousMonitor is null ? TryGetCachedRender", viewport);
        Assert.DoesNotContain("if (continuousMonitor is null) CacheRender", viewport);
        Assert.DoesNotContain("var cachedTile = secondaryMonitor is null", viewport);
        Assert.DoesNotContain("if (secondaryMonitor is null)\n", viewport);
        Assert.DoesNotContain("linear || primaryMonitor is not null ? null", viewport);
        Assert.DoesNotContain("primaryMonitor is null) CacheRender", viewport);
        // Four lookups (prefetch, continuous, primary, secondary) and five stores, all under the identity; the prefetch is handed\r\n        // the identity by the primary render, the other three paths establish their own.
        Assert.Equal(9, Count(viewport, "monitorIdentity: "));
        Assert.Equal(3, Count(viewport, "TryMonitorCacheIdentity("));
    }

    // Under a monitor profile the neighbour pages must be drawn the way the primary draws them, converted for the
    // monitor, or the next page step would show sRGB pixels from the cache.
    [Fact]
    public void TheNeighbourPrefetchConvertsForTheMonitorAndStoresUnderItsIdentity()
    {
        string viewport = Viewer("PdfViewer.Viewport.cs");
        string prefetch = Between(viewport, "private void KickOffAdjacentPrefetch(", "/// <summary>The page's image boxes");

        Assert.Contains("KillerPDF.Presentation.PdfSdrDisplayConverter? monitor", prefetch);
        Assert.Contains("PreparePrimaryBitmap(renderRequest, target, scaledMax, rot, false, token, null, monitor)", prefetch);
        Assert.Contains("ReferenceEquals(display, _presentationDisplay)", prefetch);
    }
}
