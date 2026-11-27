using Xunit;

namespace KillerPDF.Tests;

// BF-A-014: source pins for the wiring. The host needs a window, so the pure key has its own tests.
public sealed class ViewportClipRefreshPinTests
{
    [Fact]
    public void TheHostStopsBeforeCombiningWhenNothingTheClipIsBuiltFromChanged()
    {
        string host = ViewerSourcePin.Source("Services", "Presentation", "WindowsPdfViewportHost.cs");
        string refresh = ViewerSourcePin.Between(host, "void RefreshNativeClip()", "void OnOcclusionChanged");

        int keyed = refresh.IndexOf("ViewportClipKey.Build(", StringComparison.Ordinal);
        int same = refresh.IndexOf("IsSameAs(installedKey)", StringComparison.Ordinal);
        int cut = refresh.IndexOf("ViewportOcclusion.Cut(", StringComparison.Ordinal);
        int describe = refresh.IndexOf("clip.ToString(", StringComparison.Ordinal);
        Assert.True(keyed >= 0, "The refresh builds no key.");
        Assert.True(same > keyed, "The key is never compared with the installed one.");
        Assert.True(cut > same, "The clip is cut before the key comparison.");
        Assert.True(describe > same, "The clip is described before the key comparison.");
        // A failed refresh must retry in full on the next pass, and a released window starts over.
        Assert.Equal(2, ViewerSourcePin.Count(host, "installedKey = null;"));
    }
}
