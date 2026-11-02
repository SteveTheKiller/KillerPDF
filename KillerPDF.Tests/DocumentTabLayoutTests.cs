using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace KillerPDF.Tests;

public sealed class DocumentTabLayoutTests
{
    [Fact]
    public void VisibleTabsUseTheirOwnLeftAlignedWidth()
    {
        string root = FindRepositoryRoot();
        string source = File.ReadAllText(Path.Combine(root, "Controls", "Viewer", "PdfViewer.TabStrip.cs"));
        var document = XDocument.Load(Path.Combine(root, "Controls", "Viewer", "PdfViewer.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        Assert.Contains("int visibleCount = overflow ? cap : n;", source, StringComparison.Ordinal);
        Assert.Contains("visibleCount * TabCeilingWidth", source, StringComparison.Ordinal);
        Assert.Contains("PaneBorder.BorderThickness = new Thickness(1);", source, StringComparison.Ordinal);
        Assert.DoesNotContain("PaneBorder.BorderThickness = show", source, StringComparison.Ordinal);

        XElement host = document.Descendants()
            .Single(element => (string?)element.Attribute(x + "Name") == "TabStripHost");
        Assert.Equal("Left", (string?)host.Attribute("HorizontalAlignment"));
        Assert.DoesNotContain(document.Descendants(), element =>
            (string?)element.Attribute(x + "Name") is "TabEdgeLeft" or "TabEdgeRight" or "TabBarRing");
    }

    [Fact]
    public void RetroTabsHaveDistinctFacesAndContinuousPaneJoin()
    {
        string root = FindRepositoryRoot();
        string source = File.ReadAllText(Path.Combine(root, "Controls", "Viewer", "PdfViewer.TabStrip.cs"));
        var document = XDocument.Load(Path.Combine(root, "Controls", "Viewer", "PdfViewer.xaml"));
        // KillerPDF's own 98SE keys (Themes/App) sit beside the kit palette, which owns every key it defines.
        var themes = new[] { Path.Combine(root, "Themes", "App", "98SE.xaml"), Path.Combine(root, "Themes", "98SE.xaml") }
            .Select(XDocument.Load).ToArray();
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        string BrushColor(string key) => (string)themes.Select(theme => theme.Descendants()
            .SingleOrDefault(element => element.Name.LocalName == "SolidColorBrush" &&
                (string?)element.Attribute(x + "Key") == key)).First(element => element != null)!
            .Attribute("Color")!;

        Assert.Equal("#c0c0c0", BrushColor("TabActiveBrush"), ignoreCase: true);
        Assert.Equal("#9f9f9f", BrushColor("TabInactiveBrush"), ignoreCase: true);
        Assert.Equal("#9f9f9f", BrushColor("BgRecentPanel"), ignoreCase: true);
        Assert.Equal("#c0c0c0", BrushColor("FocusedPaneBrush"), ignoreCase: true);
        Assert.Equal("#c0c0c0", BrushColor("BgCanvas"), ignoreCase: true);
        Assert.DoesNotContain("TabBarRing", source, StringComparison.Ordinal);

        XElement join = document.Descendants()
            .Single(element => (string?)element.Attribute(x + "Name") == "RetroTabJoinLine");
        Assert.Equal("1", (string?)join.Attribute("Height"));
        Assert.Equal("Bottom", (string?)join.Attribute("VerticalAlignment"));
        Assert.Equal("{DynamicResource PaneBorderBrush}", (string?)join.Attribute("Background"));
        Assert.Equal("{DynamicResource RetroTabJoinVisibility}", (string?)join.Attribute("Visibility"));

        XElement innerJoin = document.Descendants()
            .Single(element => (string?)element.Attribute(x + "Name") == "RetroTabInnerJoin");
        Assert.Equal("1,0,2,-1", (string?)innerJoin.Attribute("Margin"));
        Assert.Equal("{DynamicResource RetroTabJoinVisibility}", (string?)innerJoin.Attribute("Visibility"));
        foreach (string segmentName in new[] { "RetroTabInnerJoinLeft", "RetroTabInnerJoinRight" })
        {
            XElement segment = document.Descendants()
                .Single(element => (string?)element.Attribute(x + "Name") == segmentName);
            Assert.Equal("{DynamicResource BevelLightBrush}", (string?)segment.Attribute("Background"));
        }
        Assert.Contains("UpdateRetroTabInnerJoin", source, StringComparison.Ordinal);
        Assert.Contains("Canvas.SetLeft(RetroTabInnerJoinRight, activeRight)", source, StringComparison.Ordinal);
        Assert.DoesNotContain(document.Descendants(), element =>
            (string?)element.Attribute(x + "Name") == "tabActiveRetroSeamPatch");

        XElement outline = document.Descendants()
            .Single(element => (string?)element.Attribute(x + "Name") == "tabActiveRetroOuterOutline");
        Assert.Equal("{DynamicResource TabActiveOuterOutlineMargin}", (string?)outline.Attribute("Margin"));
        Assert.Equal("{DynamicResource PaneBorderBrush}", (string?)outline.Attribute("BorderBrush"));
        Assert.Equal("1,1,1,0", (string?)outline.Attribute("BorderThickness"));
        XElement inactiveOutline = document.Descendants()
            .Single(element => (string?)element.Attribute(x + "Name") == "tabInactiveRetroOuterOutline");
        Assert.Equal("{DynamicResource PaneBorderBrush}", (string?)inactiveOutline.Attribute("BorderBrush"));
        Assert.Equal("1,1,1,0", (string?)inactiveOutline.Attribute("BorderThickness"));

        string ThicknessValue(string key) => themes.Select(theme => theme.Descendants()
            .SingleOrDefault(element => element.Name.LocalName == "Thickness" &&
                (string?)element.Attribute(x + "Key") == key)).First(element => element != null)!.Value;
        Assert.Equal("-8,-4,-5,-4", ThicknessValue("TabActiveOuterOutlineMargin"));
        Assert.Equal("-8,-6,-6,-2", ThicknessValue("TabInactiveOuterOutlineMargin"));
        Assert.Equal("8,6,5,2", ThicknessValue("TabPadding"));
        Assert.Equal("-2,3,0,0", ThicknessValue("TabActiveMargin"));
        Assert.Equal("0,3,0,0", ThicknessValue("TabActiveFirstMargin"));
        Assert.Equal("-2,3,0,0", ThicknessValue("TabActiveLastMargin"));
        Assert.Equal("0,3,0,0", ThicknessValue("TabActiveOnlyMargin"));
    }

    [Fact]
    public void TabDragCanTearAFreeTabIntoANewWindow()
    {
        string root = FindRepositoryRoot();
        string strip = File.ReadAllText(Path.Combine(root, "Controls", "Viewer", "PdfViewer.TabStrip.cs"));
        string tearOut = File.ReadAllText(Path.Combine(root, "Shell", "TabTearOut.cs"));

        Assert.Contains("Host?.IsOutsideWindow(e) == true", strip, StringComparison.Ordinal);
        Assert.Contains("Host.TearOutTab(this, s)", strip, StringComparison.Ordinal);
        Assert.Contains("MinimumHorizontalDragDistance", strip, StringComparison.Ordinal);
        Assert.Contains("MinimumVerticalDragDistance", strip, StringComparison.Ordinal);
        Assert.Contains("new MainWindow(session)", tearOut, StringComparison.Ordinal);
        Assert.Contains("source.DetachSessionExt(session)", tearOut, StringComparison.Ordinal);
    }

    [Fact]
    public void DragGhostUsesTheActiveTabThemeResources()
    {
        string root = FindRepositoryRoot();
        var window = XDocument.Load(Path.Combine(root, "Shell", "MainWindow.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement ghost = window.Descendants()
            .Single(element => (string?)element.Attribute(x + "Name") == "TabDragGhost");
        XElement ghostText = window.Descendants()
            .Single(element => (string?)element.Attribute(x + "Name") == "TabDragGhostText");

        // The same face as the active tab, its rounded face and corner included.
        Assert.Equal("RetroTabBorder", ghost.Name.LocalName);
        Assert.Equal("{DynamicResource TabActiveCornerRadius}", (string?)ghost.Attribute("CornerRadius"));
        Assert.Equal("{DynamicResource TabActiveBrush}", (string?)ghost.Attribute("Background"));
        Assert.Equal("{DynamicResource TabActivePadding}", (string?)ghost.Attribute("Padding"));
        Assert.Equal("{DynamicResource BarShadowEffect}", (string?)ghost.Attribute("Effect"));
        Assert.Equal("{DynamicResource TabTitleFontSize}", (string?)ghostText.Attribute("FontSize"));
    }

    [Fact]
    public void OrdinaryTabsMatchKillerShellsTabGeometry()
    {
        string root = FindRepositoryRoot();
        string theme = File.ReadAllText(Path.Combine(root, "Services", "AppTheme.cs"));
        var document = XDocument.Load(Path.Combine(root, "Controls", "Viewer", "PdfViewer.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement Named(string name) => document.Descendants()
            .Single(element => (string?)element.Attribute(x + "Name") == name);

        // The tab paddings, margins and active ring are the kit's: KillerPDF no longer states them.
        foreach (string kitKey in new[] { "TabPadding", "TabActivePadding", "TabFocusPadding", "TabFocusFirstPadding",
            "TabFocusLastPadding", "TabFocusOnlyPadding", "TabActiveRingFadeBrush", "TabActiveRingBrush" })
            Assert.DoesNotContain($"\"{kitKey}\"", theme, StringComparison.Ordinal);
        // 98SE keeps its classic corner, title and close x.
        Assert.Contains("Default(\"TabActiveCornerRadius\", compactDialogCaption ? new CornerRadius(2, 2, 0, 0) : new CornerRadius(3, 3, 0, 0));",
            theme, StringComparison.Ordinal);
        Assert.Contains("Default(\"TabTitleFontSize\", compactDialogCaption ? 11.0 : 12.0);", theme, StringComparison.Ordinal);
        Assert.Contains("Default(\"TabCloseFontSize\", compactDialogCaption ? 14.0 : 17.0);", theme, StringComparison.Ordinal);

        XElement tab = Named("tabBd");
        Assert.Equal("{DynamicResource PaneBorderBrush}", (string?)tab.Attribute("DividerBrush"));
        Assert.Equal("Center", (string?)Named("tabContents").Attribute("VerticalAlignment"));
        Assert.Equal("{DynamicResource TabTitleFontSize}", (string?)Named("tabLbl").Attribute("FontSize"));
        Assert.Equal("{DynamicResource TabCloseFontSize}", (string?)Named("tabClose").Attribute("FontSize"));
        Assert.Contains(document.Descendants(), element => element.Name.LocalName == "Setter"
            && (string?)element.Attribute("TargetName") == "tabBd"
            && (string?)element.Attribute("Property") == "CornerRadius"
            && (string?)element.Attribute("Value") == "{DynamicResource TabActiveCornerRadius}");

        // One pane open: the active tab's sides fade toward its foot.
        XElement lone = document.Descendants().Single(element => element.Name.LocalName == "MultiDataTrigger"
            && element.Descendants().Any(setter => (string?)setter.Attribute("Value") == "{DynamicResource TabActiveRingFadeBrush}"));
        string[] conditions = lone.Descendants().Where(element => element.Name.LocalName == "Condition")
            .Select(condition => $"{(string?)condition.Attribute("Binding")}={(string?)condition.Attribute("Value")}").ToArray();
        Assert.Equal(new[] { "{Binding IsActive}=True", "{Binding PaneFocused}=False", "{Binding PaneDimmed}=False" }, conditions);
        string[] setters = lone.Descendants().Where(element => element.Name.LocalName == "Setter")
            .Select(setter => $"{(string?)setter.Attribute("Property")}={(string?)setter.Attribute("Value")}").ToArray();
        Assert.Equal(new[]
        {
            "BorderThickness={DynamicResource TabFocusThickness}",
            "Padding={DynamicResource TabFocusPadding}",
            "BorderBrush={DynamicResource TabActiveRingFadeBrush}",
        }, setters);

        // The card's corners at the band and the content clip under them follow the tab.
        string strip = File.ReadAllText(Path.Combine(root, "Controls", "Viewer", "PdfViewer.TabStrip.cs"));
        Assert.Contains("CardCorners.AtTabBand(r, tabRadius, firstActive, lastActive, TabsAtBottom)", strip, StringComparison.Ordinal);
        Assert.Contains("ClipToCard(DocPaneContent);", strip, StringComparison.Ordinal);
    }

    [Fact]
    public void InactiveTabCloseDoesNotSwitchTheActiveDocument()
    {
        string source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Controls", "Viewer", "PdfViewer.Tabs.cs"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        int start = source.IndexOf("private void CloseTabCore(DocumentSession s)", StringComparison.Ordinal);
        int focus = source.IndexOf("Host?.FocusViewer(this);", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && focus > start);
        string inactiveClose = source[start..focus];

        Assert.Contains("if (!ReferenceEquals(s, _active))", inactiveClose, StringComparison.Ordinal);
        Assert.Contains("if (!ReferenceEquals(s, _active) && s.IsDirty)", inactiveClose, StringComparison.Ordinal);
        Assert.Contains("if (res == MessageBoxResult.Cancel) return;", inactiveClose, StringComparison.Ordinal);
        // Save is the one answer that brings the tab forward, because a save works on the live
        // document; Don't Save closes it in the background like a clean tab.
        int save = inactiveClose.IndexOf("if (res == MessageBoxResult.Yes)", StringComparison.Ordinal);
        int switchTab = inactiveClose.IndexOf("SwitchToTab(s);", StringComparison.Ordinal);
        Assert.True(save >= 0 && switchTab > save);
        Assert.Contains("if (!_sessions.Contains(s) || ReferenceEquals(s, _active)) return;", inactiveClose, StringComparison.Ordinal);
        Assert.Contains("CancelAndRelease(s.ThumbCts);", inactiveClose, StringComparison.Ordinal);
        Assert.Contains("s.ThumbCts = null;", inactiveClose, StringComparison.Ordinal);
        Assert.Contains("Services.TabRelease.Release(s.Doc, s.Protection);", inactiveClose, StringComparison.Ordinal);
        Assert.Contains("_sessions.Remove(s);", inactiveClose, StringComparison.Ordinal);
        Assert.Contains("_renderLru.Remove(s);", inactiveClose, StringComparison.Ordinal);
        Assert.Contains("s.RenderCache.Clear();", inactiveClose, StringComparison.Ordinal);
        Assert.Contains("s.RenderCacheSize.Clear();", inactiveClose, StringComparison.Ordinal);
        Assert.Contains("RebuildTabStrip();\n                return;", inactiveClose, StringComparison.Ordinal);
        foreach (string activeMutation in new[] { "CommitActiveTextBox(", "CancelTransientForSwitch(",
            "CaptureSessionState(", "SetActiveSession(", "ApplySessionState(", "RenderActiveSession(",
            "ShowEmptyState(", "CancelRenderWork(" })
            Assert.DoesNotContain(activeMutation, inactiveClose, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "KillerPDF.csproj")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the KillerPDF repository root.");
    }
}
