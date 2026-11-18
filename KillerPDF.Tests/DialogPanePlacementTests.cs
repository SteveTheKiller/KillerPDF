using System.Windows;
using KillerPDF.Controls;
using Xunit;

namespace KillerPDF.Tests;

// With the document area split, a small dialog opens centered on the pane it was opened from;
// page windows (Print, Transform, Color correction, Stamp) and any dialog bigger than the pane
// open centered on the app window. Trash Polka's text fields are black, its dropdowns unchanged.
public sealed class DialogPanePlacementTests
{
    // A 400 x 300 dialog over the right pane of a split (960..1900 x 120..1000) sits at
    // 960 + (940 - 400) / 2 = 1230, 120 + (880 - 300) / 2 = 410.
    [Fact]
    public void ADialogIsCenteredOnThePane()
        => Assert.Equal(new Point(1230, 410),
            PageWindowSizing.CenteredIn(new Rect(960, 120, 940, 880), new Size(400, 300)));

    [Fact]
    public void ADialogBiggerThanThePaneDoesNotUseIt()
    {
        var pane = new Rect(960, 120, 940, 880);
        Assert.True(PageWindowSizing.FitsOnPane(pane, new Size(940, 880)));
        Assert.False(PageWindowSizing.FitsOnPane(pane, new Size(941, 300)));
        Assert.False(PageWindowSizing.FitsOnPane(pane, new Size(400, 881)));
        Assert.False(PageWindowSizing.FitsOnPane(Rect.Empty, new Size(400, 300)));
        Assert.False(PageWindowSizing.FitsOnPane(pane, new Size(double.NaN, 300)));
    }

    // Every dialog goes through Configure, so the hook there reaches each dialog without its own
    // file changing; page windows are skipped by their registration, not by handler order.
    [Fact]
    public void ConfigureCentersSmallDialogsOnTheFocusedPane()
    {
        string frame = ShellSource.Read("Controls", "DialogFrame.cs");
        string configure = ShellSource.Between(frame, "public static void Configure(", "// ---- Page windows");
        Assert.Contains("win.SourceInitialized += (_, _) => CenterOnOwnerPane(win);", configure);

        string pane = ShellSource.Between(frame, "private static void CenterOnOwnerPane(Window win)", "private static Size OpeningSize(");
        Assert.Contains("if (PageWindows.TryGetValue(win, out _)) return;", pane);
        Assert.Contains("main.ActivePaneScreenArea() is not Rect pane", pane);
        Assert.Contains("if (!PageWindowSizing.FitsOnPane(pane, size)) return;", pane);
        Assert.Contains("WorkAreaFit.Fit(win, keepMargin: false);", pane);

        string split = ShellSource.Read("Shell", "SplitPane.cs");
        string area = ShellSource.Between(split, "internal Rect? ActivePaneScreenArea()", "/// <summary>Pane A's chosen width.");
        Assert.Contains("if (!_isSplit || WindowState == WindowState.Minimized) return null;", area);
        Assert.Contains("ActiveViewer is not { IsVisible: true } pane", area);
    }

    // Transform opened at the far left of the app: a restored page window replayed its saved
    // position. The saved size and maximized state still come back, but it is centered on the app.
    [Fact]
    public void ARestoredPageWindowIsCenteredOnTheApp()
    {
        string frame = ShellSource.Read("Controls", "DialogFrame.cs");
        string sizing = ShellSource.Between(frame, "internal static void EnablePageSizing(", "private static void ApplyWindowState(");
        Assert.Contains("else if (win.WindowState == WindowState.Normal) CenterOnOwner(win);", sizing);
        Assert.True(sizing.IndexOf("else if (win.WindowState == WindowState.Normal) CenterOnOwner(win);", StringComparison.Ordinal)
            < sizing.IndexOf("WorkAreaFit.Fit(win, keepMargin: !restored);", StringComparison.Ordinal));
    }

    [Fact]
    public void TrashPolkaTextFieldsAreBlackAndDropdownsKeepTheirFace()
    {
        string theme = ShellSource.Read("Themes", "TrashPolka.xaml");
        Assert.Contains("<SolidColorBrush x:Key=\"TextFieldBrush\" Color=\"#000000\" po:Freeze=\"True\"/>", theme);
        Assert.Contains("<SolidColorBrush x:Key=\"ComboFieldBrush\" Color=\"#101010\" po:Freeze=\"True\"/>", theme);
        Assert.Contains("<SolidColorBrush x:Key=\"TextFieldBrush\" Color=\"#101010\" po:Freeze=\"True\"/>",
            ShellSource.Read("Themes", "Dark.xaml"));
    }
}
