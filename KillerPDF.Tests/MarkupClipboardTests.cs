using System.Windows;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

// #497: once a picture, shape or text box was copied, every paste brought it back, whatever was copied afterward, in
// every open document, until a restart. Copied marks are pasted only while the Windows clipboard has not changed since
// they were copied; anything copied later, in KillerPDF or another program, is what pastes.
public sealed class MarkupClipboardTests
{
    private static PageAnnotation Mark() => new TextAnnotation { Position = new Point(1, 2), Content = "copied" };

    [Fact]
    public void CopiedMarksPasteWhileNothingElseWasCopied()
    {
        var clipboard = new MarkupClipboard();
        clipboard.Set([Mark()], systemSequence: 41);

        Assert.True(clipboard.IsCurrent(41));
        Assert.Single(clipboard.Items);
    }

    [Fact]
    public void AnythingCopiedAfterwardWins()
    {
        var clipboard = new MarkupClipboard();
        clipboard.Set([Mark()], systemSequence: 41);

        Assert.False(clipboard.IsCurrent(42));
    }

    // SOURCE PIN: every paste path and every Paste menu row asks whether the copied marks are still current, never
    // whether the list merely has something in it.
    [Fact]
    public void EveryPastePathAsksWhetherTheCopiedMarksAreCurrent()
    {
        foreach (string file in new[] { "KeyboardShortcuts.cs", "ContextMenu.cs", "AnnotationCard.cs", "PictureCard.cs" })
            Assert.DoesNotContain("_annotationClipboard.Count", ShellSource.Read("Shell", file));
        string keys = ShellSource.Read("Shell", "KeyboardShortcuts.cs");
        Assert.Contains("if (MarkupToPaste()) PasteAnnotations(pageIndex);", keys);
        Assert.Contains("if (!MarkupToPaste() || pageIdx < 0", ShellSource.Read("Shell", "ContextMenu.cs"));
    }

    [Fact]
    public void NothingCopiedIsNeverCurrent() => Assert.False(new MarkupClipboard().IsCurrent(0));

    [Fact]
    public void CopyingMarksAgainMakesThemCurrentAgain()
    {
        var clipboard = new MarkupClipboard();
        clipboard.Set([Mark()], systemSequence: 41);
        clipboard.Set([Mark(), Mark()], systemSequence: 57);

        Assert.True(clipboard.IsCurrent(57));
        Assert.Equal(2, clipboard.Items.Count);
    }
}
