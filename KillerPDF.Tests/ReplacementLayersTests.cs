using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class ReplacementLayersTests
{
    [Fact]
    public void HiddenChildStaysHiddenAfterGroupVisibilityCycle()
    {
        var pair = Pair();
        ReplacementLayers.SetVisibility(pair, ReplacementLayer.Text, false);
        ReplacementLayers.SetVisibility(pair, ReplacementLayer.Group, false);
        Assert.False(ReplacementLayers.IsVisible(pair.Cover));
        ReplacementLayers.SetVisibility(pair, ReplacementLayer.Group, true);
        Assert.False(ReplacementLayers.IsVisible(pair.Text));
        Assert.True(ReplacementLayers.IsVisible(pair.Cover));
    }

    [Fact]
    public void LockedChildStaysLockedAfterGroupMovementCycle()
    {
        var pair = Pair();
        ReplacementLayers.SetMovementAllowed(pair, ReplacementLayer.Cover, false);
        ReplacementLayers.SetMovementAllowed(pair, ReplacementLayer.Group, false);
        Assert.False(ReplacementLayers.CanMove(pair.Text));
        ReplacementLayers.SetMovementAllowed(pair, ReplacementLayer.Group, true);
        Assert.False(ReplacementLayers.CanMove(pair.Cover));
        Assert.True(ReplacementLayers.CanMove(pair.Text));
    }

    [Fact]
    public void PairLookupRequiresExactlyOneTextAndCoverOnTheLeadPage()
    {
        var pair = Pair();
        PageAnnotation[] page = [pair.Cover, pair.Text];
        Assert.Equal(pair, ReplacementLayers.FindPair(pair.Cover, page));
        Assert.Equal(pair, ReplacementLayers.FindPair(pair.Text, page));
        Assert.Null(ReplacementLayers.FindPair(pair.Text, [pair.Text]));
        Assert.Null(ReplacementLayers.FindPair(pair.Text,
            [pair.Text, pair.Cover, new CoverAnnotation { PairId = "replacement" }]));
        Assert.Null(ReplacementLayers.FindPair(pair.Text,
            [pair.Text, pair.Cover, new TextAnnotation { PairId = "replacement" }]));
        pair.Cover.PageIndex = 1;
        Assert.Null(ReplacementLayers.FindPair(pair.Text, page));
        pair.Text.PairId = "";
        Assert.Null(ReplacementLayers.FindPair(pair.Text, page));
        Assert.Null(ReplacementLayers.FindPair(new HighlightAnnotation { PairId = "replacement" }, page));
    }

    [Fact]
    public void ReplacementMembersMoveIndependentlyByDefault()
    {
        var pair = Pair();
        PageAnnotation[] page = [pair.Cover, pair.Text];
        Assert.Equal(new PageAnnotation[] { pair.Text }, ReplacementLayers.SelectionMembers(pair.Text, page));
        Assert.Equal(new PageAnnotation[] { pair.Cover }, ReplacementLayers.SelectionMembers(pair.Cover, page));
    }

    [Fact]
    public void LinkingReplacementConnectsManualGroupsTransitivelyWithoutChangingTheirIdentity()
    {
        var pair = Pair();
        pair.Text.GroupId = "text-group";
        pair.Cover.GroupId = "cover-group";
        var textPeer = new ImageAnnotation { GroupId = "text-group" };
        var coverPeer = new HighlightAnnotation { GroupId = "cover-group" };
        var unrelated = new TextAnnotation();
        var otherPage = new ImageAnnotation { PageIndex = 1, GroupId = "cover-group" };
        PageAnnotation[] page = [coverPeer, unrelated, pair.Cover, textPeer, pair.Text, otherPage, pair.Cover];

        ReplacementLayers.SetLinked(pair, true);
        Assert.Equal(new PageAnnotation[] { pair.Text, coverPeer, pair.Cover, textPeer },
            ReplacementLayers.SelectionMembers(pair.Text, page));
        ReplacementLayers.SetLinked(pair, false);
        Assert.Equal(new PageAnnotation[] { pair.Text, textPeer },
            ReplacementLayers.SelectionMembers(pair.Text, page));
        Assert.Equal("replacement", pair.Text.PairId);
        Assert.Equal("replacement", pair.Cover.PairId);
        Assert.Equal("text-group", pair.Text.GroupId);
        Assert.Equal("cover-group", pair.Cover.GroupId);
    }

    [Fact]
    public void LockedCompanionBlocksTheWholeConnectedSelection()
    {
        var pair = Pair();
        ReplacementLayers.SetLinked(pair, true);
        pair.Text.GroupId = "manual";
        var companion = new ImageAnnotation { GroupId = "manual", IsMovementLocked = true };
        var members = ReplacementLayers.SelectionMembers(pair.Cover, [pair.Cover, pair.Text, companion]);
        Assert.False(ReplacementLayers.CanMoveTogether(members));
        companion.IsMovementLocked = false;
        Assert.True(ReplacementLayers.CanMoveTogether(members));
        ReplacementLayers.SetMovementAllowed(pair, ReplacementLayer.Group, false);
        Assert.False(ReplacementLayers.CanMoveTogether(members));
        Assert.False(ReplacementLayers.CanMoveTogether([]));
    }

    [Fact]
    public void MalformedOrOneSidedLinksDoNotCollectAnnotationsWithTheSamePairId()
    {
        var pair = Pair();
        pair.Text.PairLinked = true;
        Assert.Equal(new PageAnnotation[] { pair.Text },
            ReplacementLayers.SelectionMembers(pair.Text, [pair.Text, pair.Cover]));
        pair.Cover.PairLinked = true;
        var extra = new CoverAnnotation { PairId = "replacement", PairLinked = true };
        Assert.Equal(new PageAnnotation[] { pair.Text },
            ReplacementLayers.SelectionMembers(pair.Text, [pair.Text, pair.Cover, extra]));
        pair.Cover.PageIndex = 1;
        Assert.Equal(new PageAnnotation[] { pair.Text },
            ReplacementLayers.SelectionMembers(pair.Text, [pair.Text, pair.Cover]));
        Assert.Empty(ReplacementLayers.SelectionMembers(pair.Text, [pair.Cover]));
    }

    [Fact]
    public void UnpairedAndUnrelatedAnnotationsUseOnlyTheirOwnVisibilityAndMovementPreferences()
    {
        var text = new TextAnnotation { PairVisible = false, PairMovementLocked = true };
        var image = new ImageAnnotation { PairId = "replacement", PairVisible = false, PairMovementLocked = true };
        Assert.True(ReplacementLayers.IsVisible(text));
        Assert.True(ReplacementLayers.CanMove(text));
        Assert.True(ReplacementLayers.IsVisible(image));
        Assert.True(ReplacementLayers.CanMove(image));
        text.IsVisible = false;
        image.IsMovementLocked = true;
        Assert.False(ReplacementLayers.IsVisible(text));
        Assert.False(ReplacementLayers.CanMove(image));
    }

    private static ReplacementPair Pair() => new(
        new TextAnnotation { PairId = "replacement", PageIndex = 0 },
        new CoverAnnotation { PairId = "replacement", PageIndex = 0 });
}
