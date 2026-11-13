using Xunit;

namespace KillerPDF.Tests;

// #190: commands can move to keys the user chooses.
public sealed class ShortcutRemapTests
{
    private static KsCap Cap(KbLayer layer, string id) => new(layer, id, "");

    [Fact]
    public void Commands_LeaveNavigationAndTheToggleWhereTheyAre()
    {
        string[] names = [.. ShortcutRemap.Commands.Select(command => ShortcutRemap.Name(command.Original))];

        Assert.Contains("Ctrl:F", names);
        Assert.Contains("Base:V", names);
        Assert.DoesNotContain("Base:F2", names);
        Assert.DoesNotContain("CtrlShift:K", names);
        Assert.DoesNotContain("Base:Esc", names);
        Assert.DoesNotContain("Base:Left", names);
        Assert.Equal(names.Length, names.Distinct().Count());
    }

    [Fact]
    public void Assign_MovesTheCommandAndFreesItsOldKey()
    {
        var remap = new ShortcutRemap();

        Assert.Null(remap.Assign(Cap(KbLayer.Ctrl, "F"), Cap(KbLayer.Alt, "K")));

        Assert.Equal<(KbLayer, string)?>((KbLayer.Ctrl, "F"), remap.Translate(KbLayer.Alt, "K"));
        Assert.Null(remap.Translate(KbLayer.Ctrl, "F"));
        Assert.Equal<(KbLayer, string)?>((KbLayer.Ctrl, "Q"), remap.Translate(KbLayer.Ctrl, "Q"));
        Assert.Equal(Cap(KbLayer.Alt, "K"), remap.Effective(Cap(KbLayer.Ctrl, "F")));
    }

    [Fact]
    public void Assign_RefusesAKeyAnotherCommandUses()
    {
        var remap = new ShortcutRemap();

        Assert.Equal("Str_KS_Replace", remap.Assign(Cap(KbLayer.Ctrl, "F"), Cap(KbLayer.Ctrl, "H")));
        Assert.Equal(0, remap.Count);
    }

    [Fact]
    public void Assign_BackToItsOwnKeyClearsTheCustomization()
    {
        var remap = new ShortcutRemap();
        remap.Assign(Cap(KbLayer.Ctrl, "F"), Cap(KbLayer.Alt, "K"));

        Assert.Null(remap.Assign(Cap(KbLayer.Ctrl, "F"), Cap(KbLayer.Ctrl, "F")));

        Assert.Equal(0, remap.Count);
        Assert.Equal<(KbLayer, string)?>((KbLayer.Ctrl, "F"), remap.Translate(KbLayer.Ctrl, "F"));
    }

    [Fact]
    public void Parse_RoundTripsAndDropsWhatCannotApply()
    {
        var remap = new ShortcutRemap();
        remap.Assign(Cap(KbLayer.Ctrl, "F"), Cap(KbLayer.Alt, "K"));
        remap.Assign(Cap(KbLayer.Base, "F5"), Cap(KbLayer.Shift, "F12"));

        ShortcutRemap restored = ShortcutRemap.Parse(remap.Serialize() + ";Base:Esc=Alt:J;Ctrl:O=Ctrl:H;nonsense");

        Assert.Equal(remap.Serialize(), restored.Serialize());
        Assert.Equal(2, restored.Count);
    }

    [Fact]
    public void BuildMap_ShowsAMovedCommandOnItsNewKey()
    {
        var remap = new ShortcutRemap();
        remap.Assign(Cap(KbLayer.Ctrl, "F"), Cap(KbLayer.Alt, "K"));

        var map = ShortcutTable.BuildMap(remap.Effective);

        Assert.Equal("Str_KS_Find", map[KbLayer.Alt]["K"].Label);
        Assert.False(map[KbLayer.Ctrl].ContainsKey("F"));
    }

    [Theory]
    [InlineData("F1")]
    [InlineData("F12")]
    public void HelpAndAbout_StayOnTheirKeys(string id)
    {
        string[] names = [.. ShortcutRemap.Commands.Select(command => ShortcutRemap.Name(command.Original))];

        Assert.False(ShortcutRemap.IsRemappable(Cap(KbLayer.Base, id)));
        Assert.DoesNotContain("Base:" + id, names);
    }

    [Theory]
    [InlineData("F1", "Str_KS_ThisList")]
    [InlineData("F12", "Str_KS_About")]
    public void Assign_RefusesBareF1AndF12ForAnyOtherCommand(string id, string owner)
    {
        var remap = new ShortcutRemap();

        Assert.Equal(owner, remap.Assign(Cap(KbLayer.Base, "F5"), Cap(KbLayer.Base, id)));
        Assert.Equal(0, remap.Count);
    }

    [Fact]
    public void Parse_IgnoresSavedMovesOfHelpAndAboutAndSavedCommandsOnBareF1AndF12()
    {
        ShortcutRemap restored = ShortcutRemap.Parse(
            "Base:F1=Alt:J;Base:F12=Alt:Y;Ctrl:P=Base:F12;Ctrl:O=Base:F1");

        Assert.Equal(0, restored.Count);
        Assert.Equal<(KbLayer, string)?>((KbLayer.Base, "F1"), restored.Translate(KbLayer.Base, "F1"));
        Assert.Equal<(KbLayer, string)?>((KbLayer.Base, "F12"), restored.Translate(KbLayer.Base, "F12"));
    }
}
