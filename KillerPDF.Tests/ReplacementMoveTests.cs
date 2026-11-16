using System.Windows;
using KillerPDF.Services;
using Xunit;
using static KillerPDF.Tests.ViewerSourcePin;

namespace KillerPDF.Tests;

// #467: an in-place edit makes an unlinked replacement (a text box over its cover). A click lands on the text box. With
// the text box's hand off and the cover's on, the arrow keys and a drag move the cover; before, nothing moved, because
// only a linked pair brought the cover into the move. An unlocked, unlinked text box still moves on its own.
public sealed class ReplacementMoveTests
{
    private static (TextAnnotation Text, CoverAnnotation Cover, List<PageAnnotation> Page) Pair(bool linked)
    {
        var text = new TextAnnotation { PairId = "p", Position = new Point(10, 10), Width = 50, Height = 20, PairLinked = linked };
        var cover = new CoverAnnotation { PairId = "p", Bounds = new Rect(8, 8, 54, 24), PairLinked = linked };
        return (text, cover, [cover, text]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ALockedTextBoxHandsTheMoveToItsCover(bool linked)
    {
        var (text, cover, page) = Pair(linked);
        text.IsMovementLocked = true;

        Assert.Equal([cover], ReplacementLayers.MoveMembers([text], page));
    }

    [Fact]
    public void ALockedCoverHandsTheMoveToItsTextBox()
    {
        var (text, cover, page) = Pair(linked: false);
        cover.IsMovementLocked = true;

        Assert.Equal([text], ReplacementLayers.MoveMembers([cover], page));
    }

    [Fact]
    public void AnUnlinkedTextBoxThatIsFreeMovesAlone()
    {
        var (text, _, page) = Pair(linked: false);

        Assert.Equal([text], ReplacementLayers.MoveMembers([text], page));
    }

    [Fact]
    public void ALinkedPairMovesTogether()
    {
        var (text, cover, page) = Pair(linked: true);

        var moving = ReplacementLayers.MoveMembers([text], page);

        Assert.Equal(2, moving.Count);
        Assert.Contains(text, moving);
        Assert.Contains(cover, moving);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NothingMovesWhenBothLayersOrTheWholeReplacementAreLocked(bool wholeLocked)
    {
        var (text, cover, page) = Pair(linked: false);
        if (wholeLocked) text.PairMovementLocked = cover.PairMovementLocked = true;
        else text.IsMovementLocked = cover.IsMovementLocked = true;

        Assert.Empty(ReplacementLayers.MoveMembers([text], page));
    }

    // SOURCE PIN (the viewer is not compiled into this project): the arrow keys and a drag both take their movers from
    // MoveMembers, and a nudge moves the selection frame with the mark instead of putting the old frame back.
    [Fact]
    public void TheArrowKeysAndADragUseTheMoveRuleAndTheFrameFollowsANudge()
    {
        string annotations = Viewer("PdfViewer.Annotations.cs").Replace("\r\n", "\n");
        string nudge = Between(annotations, "private bool NudgeSelection(", "MarkDirty();");
        Assert.Contains("MovingMembers(selected)", nudge);
        Assert.Contains("MoveSelectionVisuals();", nudge);
        string press = Between(annotations, "// Only the members free to move are dragged", "_dragAnnot = lead;");
        Assert.Contains("MovingMembers([pa, .. _dragGroupOrig.Select(member => member.a)])", press);
    }
}
