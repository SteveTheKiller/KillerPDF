using System.Windows;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

// #467: a replacement-text object is one thing to copy, its layers move on their own when only one is locked, and
// a hidden layer can always be found again.
public sealed class ReplacementObjectTests
{
    private static (TextAnnotation Text, CoverAnnotation Cover, List<PageAnnotation> Page) Pair(bool linked = true)
    {
        var text = new TextAnnotation { PairId = "p", Position = new Point(10, 10), Width = 50, Height = 20, PairLinked = linked };
        var cover = new CoverAnnotation { PairId = "p", Bounds = new Rect(10, 10, 50, 20), PairLinked = linked };
        var other = new InkAnnotation();
        return (text, cover, [cover, text, other]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CopyingEitherLayerCopiesTheWholeObject(bool linked)
    {
        var (text, cover, page) = Pair(linked);

        var fromText = ReplacementLayers.CopyMembers([text], page);
        var fromCover = ReplacementLayers.CopyMembers([cover], page);

        Assert.Equal(2, fromText.Count);
        Assert.Contains(cover, fromText);
        Assert.Contains(text, fromCover);
    }

    // A paste adds the copied marks in list order, so the cover has to come before its text or the pasted cover
    // hides the text and the edit draws white.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CopyingKeepsThePageStackOrderWhicheverLayerWasSelected(bool fromText)
    {
        var (text, cover, page) = Pair();
        var ink = (InkAnnotation)page[2];

        var copied = ReplacementLayers.CopyMembers(fromText ? [ink, text] : [ink, cover], page);

        Assert.Equal([cover, text, ink], copied);
    }

    [Fact]
    public void CopyingKeepsOtherSelectedMarksOnce()
    {
        var (text, cover, page) = Pair();
        var ink = (InkAnnotation)page[2];

        var copied = ReplacementLayers.CopyMembers([text, cover, ink], page);

        Assert.Equal(3, copied.Count);
    }

    [Fact]
    public void ALockedTextBoxLeavesTheCoverFreeToMove()
    {
        var (text, cover, _) = Pair();
        text.IsMovementLocked = true;

        Assert.Equal([cover], ReplacementLayers.MovableMembers([text, cover]));
    }

    [Fact]
    public void LockingTheWholeObjectStillHoldsBothLayers()
    {
        var (text, cover, page) = Pair();
        ReplacementLayers.SetMovementAllowed(ReplacementLayers.FindPair(text, page)!, ReplacementLayer.Group, false);

        Assert.Empty(ReplacementLayers.MovableMembers([text, cover]));
    }

    [Fact]
    public void AHiddenReplacementLayerStillShowsWhereItIsOnScreen()
    {
        var (text, cover, page) = Pair();
        ReplacementLayers.SetVisibility(ReplacementLayers.FindPair(text, page)!, ReplacementLayer.Group, false);

        Assert.False(ReplacementLayers.IsVisible(text));
        Assert.True(ReplacementLayers.ShowsWhenHidden(text));
        Assert.True(ReplacementLayers.ShowsWhenHidden(cover));
        Assert.False(ReplacementLayers.ShowsWhenHidden(new InkAnnotation { IsVisible = false }));
        Assert.False(ReplacementLayers.ShowsWhenHidden(new TextAnnotation { PairId = "q" }));   // visible, nothing to find
    }
}
