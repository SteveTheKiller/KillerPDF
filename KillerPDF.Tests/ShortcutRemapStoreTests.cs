using Xunit;

namespace KillerPDF.Tests;

// With two windows open, a shortcut moved in one is moved in both.
public sealed class ShortcutRemapStoreTests
{
    private static ShortcutRemap Moved()
    {
        var remap = new ShortcutRemap();
        remap.Assign(new KsCap(KbLayer.Ctrl, "F", ""), new KsCap(KbLayer.Alt, "K", ""));
        return remap;
    }

    [Fact]
    public void EveryWindowIsToldWhenTheMapIsSavedAndReadsTheNewOne()
    {
        string? saved = null;
        var store = new ShortcutRemapStore(() => saved, text => saved = text);
        int firstWindowRefreshes = 0, secondWindowRefreshes = 0;
        string? firstSees = null, secondSees = null;
        store.Changed += () => { firstWindowRefreshes++; firstSees = store.Current.Serialize(); };
        store.Changed += () => { secondWindowRefreshes++; secondSees = store.Current.Serialize(); };

        store.Save(Moved());

        Assert.Equal(1, firstWindowRefreshes);
        Assert.Equal(1, secondWindowRefreshes);
        Assert.Equal("Ctrl:F=Alt:K", firstSees);
        Assert.Equal("Ctrl:F=Alt:K", secondSees);
        Assert.Equal("Ctrl:F=Alt:K", saved);
    }

    [Fact]
    public void AWindowOpenedLaterStartsFromTheSavedMap()
    {
        var store = new ShortcutRemapStore(() => "Ctrl:F=Alt:K", _ => { });

        Assert.Equal("Ctrl:F=Alt:K", store.Current.Serialize());
        Assert.Same(store.Current, store.Current);
    }
}
