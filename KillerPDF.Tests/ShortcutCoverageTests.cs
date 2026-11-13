using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace KillerPDF.Tests;

// Keys the app handles are on the F1 list, the keyboard map and the website table, and a key the
// app handles but the user cannot move is not offered in Customize Shortcuts.
public sealed class ShortcutCoverageTests
{
    private static KsBinding Row(string labelKey) => ShortcutTable.KsAll.Single(binding => binding.LabelKey == labelKey);

    [Fact]
    public void BackspaceAndTheFindBarChordsAreOnTheListAndTheMap()
    {
        Assert.Equal("Tools", Row("Str_KS_RemoveLastPoint").Cat);
        Assert.Contains(ShortcutTable.Cap("Back"), Row("Str_KS_RemoveLastPoint").Caps);
        Assert.Equal("Search", Row("Str_KS_SearchOptions").Cat);
        Assert.Contains(ShortcutTable.Cap("Alt:C"), Row("Str_KS_SearchOptions").Caps);
        Assert.Contains(ShortcutTable.Cap("Alt:W"), Row("Str_KS_SearchOptions").Caps);

        var map = ShortcutTable.BuildMap();
        Assert.Equal("Str_KS_RemoveLastPoint", map[KbLayer.Base]["Back"].Label);
        Assert.Equal("Str_KS_SearchOptions", map[KbLayer.Alt]["C"].Label);
        Assert.Equal("Str_KS_SearchOptions", map[KbLayer.Alt]["W"].Label);
    }

    [Fact]
    public void ReplaceAllAndSaveTextAreOnTheList()
    {
        Assert.Equal("%ctrl%+%alt%+%enter%", Row("Str_Replace_All").Keys);
        Assert.Equal("Search", Row("Str_Replace_All").Cat);
        Assert.Equal("%ctrl%+%enter%", Row("Str_KS_CommitText").Keys);
        Assert.Equal("Edit", Row("Str_KS_CommitText").Cat);
        Assert.Contains(ShortcutTable.Cap("Ctrl:Enter"), Row("Str_KS_CommitText").Caps);
    }

    [Fact]
    public void BackspaceHasAKeyNameToken()
    {
        Assert.Contains(("%back%", "Str_Key_Backspace"), ShortcutTable.KeyTokens);
        Assert.Equal("%back%", Row("Str_KS_RemoveLastPoint").Keys);
    }

    [Fact]
    public void TheFindBarChordsStayWhereTheFindBoxReadsThem()
    {
        string[] movable = [.. ShortcutRemap.Commands.Select(command => ShortcutRemap.Name(command.Original))];

        Assert.DoesNotContain("Alt:C", movable);
        Assert.DoesNotContain("Alt:W", movable);
        Assert.Equal("Str_KS_SearchOptions",
            new ShortcutRemap().Assign(new KsCap(KbLayer.Ctrl, "F", ""), ShortcutTable.Cap("Alt:C")));
    }

    [Fact]
    public void TheDeleteRowSaysItAlsoDeletesPagesAndEnterNoLongerSavesText()
    {
        var english = XDocument.Load(Path.Combine(RepoRoot(), "Strings", "en-US.xaml")).Root!.Elements()
            .ToDictionary(e => (string)e.Attribute(XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml") + "Key")!, e => e.Value);

        Assert.Contains("pages", english["Str_KS_DeleteAnnot"]);
        Assert.DoesNotContain("text", english["Str_KS_ConfirmCancel"]);
    }

    [Fact]
    public void TheDeletePagesButtonNamesItsKey()
    {
        string tips = ShellSource.Between(ShellSource.Read("Shell", "ShortcutGestures.cs"),
            "private void ApplyShortcutTooltips()", "ApplyOverflowGestures();");

        Assert.Contains("Tip(DeletePagesBtn, \"Str_TT_DeletePages\", \"Del\");", tips);
    }

    [Fact]
    public void TheKeyboardMapsBaseLayerCaptionIsTranslated()
    {
        string caption = ShellSource.Between(ShellSource.Read("Shell", "KeyboardMapOverlay.cs"), "KbLayer.AltShift =>", "};");

        Assert.DoesNotContain("\"BASE\"", caption);
        Assert.Contains("Loc(\"Str_KS_LayerBase\").ToUpperInvariant()", caption);
    }

    [Theory]
    [InlineData("de")]
    [InlineData("es")]
    [InlineData("it")]
    [InlineData("nb")]
    [InlineData("hu")]
    [InlineData("sl")]
    [InlineData("tr")]
    [InlineData("ja")]
    public void TheWebsiteSpellsZoomInWithAPlusWhereTheEqualsKeyNeedsShift(string locale)
        => Assert.Equal("+", ShortcutTable.SiteZoomInChar(locale));

    [Theory]
    [InlineData("en")]
    [InlineData("fr")]
    [InlineData("zh-cn")]
    public void TheWebsiteSpellsZoomInWithEqualsElsewhere(string locale)
        => Assert.Equal("=", ShortcutTable.SiteZoomInChar(locale));

    [Fact]
    public void TheGeneratedWebsiteDataFollowsThoseSpellings()
    {
        string data = File.ReadAllText(Path.Combine(RepoRoot(), "pdf-landing", "shortcuts.generated.js"));

        Assert.Contains("Strg++ / Strg+-", data);
        Assert.DoesNotContain("Strg+=", data);
        Assert.Contains("Ctrl+= / Ctrl+-", data);
    }

    private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Strings", "en-US.xaml")))
            dir = dir.Parent;
        Assert.True(dir != null, "could not locate the repo root from the test source");
        return dir!.FullName;
    }
}
