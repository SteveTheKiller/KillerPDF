using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using KillerPDF.Controls;
using Xunit;

namespace KillerPDF.Tests;

// BF-B-005: the bar seal looked the active tab's face up by walking the tab's visual tree on every layout pass.
public sealed class TabFaceCacheTests
{
    static Border Tab(string faceName = "tabBd") =>
        new() { Child = new Grid { Children = { new Border { Name = faceName } } } };

    static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { error = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    [Fact]
    public void TheSameTabIsSearchedOnce() => RunSta(() =>
    {
        var cache = new TabFaceCache("tabBd");
        var tab = Tab();
        var first = cache.Find(tab);
        Assert.NotNull(first);
        Assert.Same(first, cache.Find(tab));
        Assert.Same(first, cache.Find(tab));
        Assert.Equal(1, cache.Searches);
    });

    [Fact]
    public void AnotherTabIsSearchedAgain() => RunSta(() =>
    {
        var cache = new TabFaceCache("tabBd");
        var one = Tab();
        var two = Tab();
        var faceOne = cache.Find(one);
        var faceTwo = cache.Find(two);
        Assert.NotSame(faceOne, faceTwo);
        Assert.Equal(2, cache.Searches);
        Assert.Same(faceOne, cache.Find(one));
        Assert.Equal(3, cache.Searches);
    });

    [Fact]
    public void ARebuiltTemplateIsSearchedAgain() => RunSta(() =>
    {
        var cache = new TabFaceCache("tabBd");
        var tab = Tab();
        var old = cache.Find(tab);
        tab.Child = new Grid { Children = { new Border { Name = "tabBd" } } };
        var fresh = cache.Find(tab);
        Assert.NotNull(fresh);
        Assert.NotSame(old, fresh);
    });

    [Fact]
    public void ATabWithoutAFaceYetIsSearchedAgainUntilItHasOne() => RunSta(() =>
    {
        var cache = new TabFaceCache("tabBd");
        var tab = new Border();
        Assert.Null(cache.Find(tab));
        tab.Child = new Grid { Children = { new Border { Name = "tabBd" } } };
        Assert.NotNull(cache.Find(tab));
    });

    [Fact]
    public void NoTabGivesNoFace() => RunSta(() =>
    {
        var cache = new TabFaceCache("tabBd");
        Assert.NotNull(cache.Find(Tab()));
        Assert.Null(cache.Find(null));
    });

    [Fact]
    public void TheBarSealLooksTheTabFaceUpThroughTheCache()
    {
        string seal = ViewerSourcePin.Viewer("PdfViewer.BarSeal.cs");
        Assert.Contains("_tabFaces.Find(container)", seal);
        Assert.DoesNotContain("FindNamed(container", seal);
    }
}
