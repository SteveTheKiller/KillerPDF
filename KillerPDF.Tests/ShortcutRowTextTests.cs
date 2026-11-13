using Xunit;

namespace KillerPDF.Tests;

// The F1 list prints the key of every row from the table, and after a remap it has to print the keys
// as they are now, in the same friendly names the default rows use.
public sealed class ShortcutRowTextTests
{
    private static KsCap Cap(KbLayer layer, string id) => new(layer, id, "");

    private static string RowKeys(string labelKey, ShortcutRemap remap)
    {
        KsBinding row = ShortcutTable.KsAll.Single(binding => binding.LabelKey == labelKey);
        return ShortcutRowText.RowKeys(row.Keys, row.Caps, remap);
    }

    [Fact]
    public void TheDocumentToolRowsFollowAMovedDocumentToolsChord()
    {
        var remap = new ShortcutRemap();
        Assert.Null(remap.Assign(Cap(KbLayer.CtrlShift, "D"), Cap(KbLayer.Alt, "D")));

        Assert.Equal("%alt%+D, P", RowKeys("Str_DocumentTools_Preflight", remap));
        Assert.Equal("%alt%+D, 0", RowKeys("Str_CM_Title", remap));
    }

    [Fact]
    public void TheDocumentToolRowsKeepTheirTextWhileTheChordIsUnmoved()
    {
        Assert.Equal("%ctrl%+%shift%+D, P", RowKeys("Str_DocumentTools_Preflight", new ShortcutRemap()));
    }

    [Fact]
    public void AMovedKeyInAMultiKeyRowKeepsTheOtherKeysAsFriendlyNames()
    {
        var remap = new ShortcutRemap();
        Assert.Null(remap.Assign(Cap(KbLayer.Base, "F4"), Cap(KbLayer.Base, "J")));

        Assert.Equal("%ctrl%+D / J", RowKeys("Str_KS_DocInfo", remap));
    }

    [Fact]
    public void AMovedKeyKeepsTheRestOfItsRowsOwnText()
    {
        var remap = new ShortcutRemap();
        Assert.Null(remap.Assign(Cap(KbLayer.Base, "F11"), Cap(KbLayer.Base, "K")));
        Assert.Equal("K / %esc%", RowKeys("Str_KS_FullScreen", remap));

        remap = new ShortcutRemap();
        Assert.Null(remap.Assign(Cap(KbLayer.Base, "L"), Cap(KbLayer.Base, "J")));
        Assert.Equal("J (%or% U)", RowKeys("Str_Lbl_Line", remap));
    }

    [Fact]
    public void ARowWhoseKeysAreNotWrittenOutOneByOneStillNamesEveryKey()
    {
        var remap = new ShortcutRemap();
        Assert.Null(remap.Assign(Cap(KbLayer.Ctrl, "I"), Cap(KbLayer.Base, "J")));

        Assert.Equal("%ctrl%+B / J / %ctrl%+U", RowKeys("Str_KS_TextStyle", remap));
    }
}
