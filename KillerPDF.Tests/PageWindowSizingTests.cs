using System.Runtime.ExceptionServices;
using System.Windows;
using KillerFamily.Windowing;
using KillerPDF.Controls;
using Xunit;

namespace KillerPDF.Tests;

// The page windows (Print, Transform, Color correction, Stamp) open at least as big as their open
// options and come back at the size and place they were left, per window type. The windows here
// are built and never shown.
public sealed class PageWindowSizingTests
{
    // Spec 2.2: the minimum is the options column (measured with the preview collapsed, frame and
    // title bar included) plus a 264 page and 18 of preview frame, so 456 + 24 halo + 282 = 762.
    [Fact]
    public void TheMinimumIsTheOpenOptionsPlusAOnePagePreview()
    {
        Size min = PageWindowSizing.Minimum(new Size(480, 627), PageWindowSizing.PreviewFloor, 1);

        Assert.Equal(762, min.Width);
        Assert.Equal(627, min.Height);
    }

    // The measured options are already drawn at the app's scale; the preview floor is not, so it
    // grows by the same factor: 708 + 282 x 1.5 = 1131.
    [Fact]
    public void ThePreviewFloorGrowsWithTheDialogScale()
    {
        Size min = PageWindowSizing.Minimum(new Size(708, 930), PageWindowSizing.PreviewFloor, 1.5);

        Assert.Equal(1131, min.Width);
        Assert.Equal(930, min.Height);
    }

    [Fact]
    public void EachWindowTypeSavesUnderItsOwnKey()
    {
        RunSta(() =>
        {
            var saved = new Dictionary<string, string>();
            var print = new Window { Left = 100, Top = 50, Width = 900, Height = 700 };

            PageWindowSizing.Save(print, "Print", (k, v) => saved[k] = v);

            Assert.Equal("100,50,900,700,0", Assert.Single(saved, p => p.Key == "PageWin.Print.L2.WindowPlacement").Value);
            Assert.DoesNotContain("WindowPlacement", saved.Keys);
        });
    }

    // A translation or a new setting can make the options taller than the saved height, so a
    // restored size never goes below the current minimum.
    [Fact]
    public void ARestoredSizeIsRaisedToTheCurrentMinimum()
    {
        RunSta(() =>
        {
            var saved = new Dictionary<string, string> { ["PageWin.Print.L2.WindowPlacement"] = "100,50,900,600,0" };
            var print = new Window { MinWidth = 762, MinHeight = 650, Width = 762, Height = 650 };

            bool restored = PageWindowSizing.Restore(print, "Print", k => saved.GetValueOrDefault(k));

            Assert.True(restored);
            Assert.Equal(100, print.Left);
            Assert.Equal(50, print.Top);
            Assert.Equal(900, print.Width);
            Assert.Equal(650, print.Height);
            Assert.Equal(WindowStartupLocation.Manual, print.WindowStartupLocation);
        });
    }

    [Fact]
    public void AnotherWindowTypesPlacementIsNotUsed()
    {
        RunSta(() =>
        {
            var saved = new Dictionary<string, string> { ["PageWin.Print.L2.WindowPlacement"] = "100,50,900,700,0" };
            var stamp = new Window { Width = 800, Height = 676, WindowStartupLocation = WindowStartupLocation.CenterOwner };

            bool restored = PageWindowSizing.Restore(stamp, "Stamp", k => saved.GetValueOrDefault(k));

            Assert.False(restored);
            Assert.Equal(800, stamp.Width);
            Assert.Equal(WindowStartupLocation.CenterOwner, stamp.WindowStartupLocation);
        });
    }

    // Steve 2026-11-26: a size saved for the taller pre-1.9.7 layout left Transform with empty
    // space under its options. That key is not read, so the window opens at its options' height.
    [Fact]
    public void ASizeSavedForTheOlderLayoutIsNotUsed()
    {
        RunSta(() =>
        {
            var saved = new Dictionary<string, string> { ["PageWin.Transform.WindowPlacement"] = "100,50,1000,800,0" };
            var transform = new Window { MinWidth = 762, MinHeight = 560, Width = 900, Height = 560,
                WindowStartupLocation = WindowStartupLocation.CenterOwner };

            bool restored = PageWindowSizing.Restore(transform, "Transform", k => saved.GetValueOrDefault(k));

            Assert.False(restored);
            Assert.Equal(560, transform.Height);
            Assert.Equal(900, transform.Width);
        });
    }

    [Fact]
    public void AWindowLeftMaximizedOpensMaximized()
    {
        RunSta(() =>
        {
            var saved = new Dictionary<string, string> { ["PageWin.Print.L2.WindowPlacement"] = "100,50,900,700,1" };
            var print = new Window { MinWidth = 762, MinHeight = 603 };

            PageWindowSizing.Restore(print, "Print", k => saved.GetValueOrDefault(k));

            Assert.Equal(WindowState.Maximized, print.WindowState);
        });
    }

    // Clamping is WorkAreaFit's (unchanged). These pin the two ways a page window calls it.
    // A first open keeps a margin: a 1270 x 603 fitted default on a 1280 x 690 work area caps
    // its width at 90% (1152) and is centered: (1280 - 1152) / 2 = 64, (690 - 603) / 2 = 43.5.
    [Fact]
    public void AFirstOpenIsCappedToNinetyPercentOfTheWorkAreaAndCentered()
    {
        Rect fitted = WorkAreaFit.FitRect(new Rect(0, 0, 1270, 603), new Rect(0, 0, 1280, 690),
            762, 603, keepMargin: true, centered: true);

        Assert.Equal(new Rect(64, 43.5, 1152, 603), fitted);
    }

    // A restored placement saved on a bigger monitor takes the whole work area and moves on screen.
    [Fact]
    public void ARestoredPlacementIsClampedToTheWorkAreaItOpensOn()
    {
        Rect fitted = WorkAreaFit.FitRect(new Rect(600, 300, 1400, 900), new Rect(0, 0, 1366, 728),
            762, 650, keepMargin: false, centered: false);

        Assert.Equal(new Rect(0, 0, 1366, 728), fitted);
    }

    // Every dialog that declares itself resizable gets resize edges, and can never be dragged
    // smaller than it opened: the certificate catalog opens 900 x 650 over a 650 x 440 minimum.
    [Fact]
    public void AResizableDialogKeepsItsOpeningSizeAsItsMinimum()
    {
        Size min = PageWindowSizing.OpeningMinimum(new Size(900, 650), new Size(650, 440), new Size(1920, 1040));

        Assert.Equal(new Size(900, 650), min);
    }

    // A declared minimum larger than the opening size still wins.
    [Fact]
    public void TheDeclaredMinimumStillWins()
        => Assert.Equal(new Size(460, 360),
            PageWindowSizing.OpeningMinimum(new Size(300, 200), new Size(460, 360), new Size(1920, 1040)));

    // An opening size that is not known yet (0 or NaN) keeps the declared side; a size-to-content
    // dialog's height is its laid-out height.
    [Fact]
    public void AnUnknownOpeningSideKeepsTheDeclaredOne()
        => Assert.Equal(new Size(320, 500),
            PageWindowSizing.OpeningMinimum(new Size(double.NaN, 500), new Size(320, 0), new Size(1920, 1040)));

    // A dialog opened larger than a small work area can still be resized on it.
    [Fact]
    public void TheMinimumNeverExceedsTheWorkArea()
        => Assert.Equal(new Size(1366, 728),
            PageWindowSizing.OpeningMinimum(new Size(2000, 1200), new Size(650, 440), new Size(1366, 728)));

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
