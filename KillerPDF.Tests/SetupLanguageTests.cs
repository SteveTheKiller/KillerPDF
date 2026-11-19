using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace KillerPDF.Tests;

// #506: the uninstall dialogs showed English for a German user and lost their close button,
// because the uninstall path never merged the chosen language and its ownerless dialogs found
// no close style.
public sealed class SetupLanguageTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "KillerPDF.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    // Source pins: LocaleManager, the uninstall path and dialog chrome need a running app and
    // windows, which this project does not load.
    [Fact]
    public void SetupAppliesTheSavedOrWindowsLanguageWithoutSavingIt()
    {
        string locale = File.ReadAllText(Path.Combine(Root(), "Services", "LocaleManager.cs"));
        Match setup = Regex.Match(locale, @"internal static void InitializeForSetup\(\)\s*\{(?<body>[^}]*)\}");
        Assert.True(setup.Success, "LocaleManager has no InitializeForSetup.");
        Assert.Contains("ApplyInternal(_current);", setup.Groups["body"].Value);
        Assert.DoesNotContain("SetSetting", setup.Groups["body"].Value);
        Assert.Contains("=> ParseSaved(saved) ?? LocaleForCulture(culture);", locale);
    }

    [Fact]
    public void UninstallLoadsTheLanguageBeforeAnyDialog()
    {
        string app = File.ReadAllText(Path.Combine(Root(), "App.xaml.cs"));
        Assert.Contains("ThemeManager.InitializeInstaller(); LocaleManager.InitializeForSetup();", app);
        Assert.DoesNotContain("TryFindResource(\"Str_Dlg_Uninstalled\")", app);
    }

    [Fact]
    public void OwnerlessDialogsFindTheCloseStyleInAppResources()
    {
        string frame = File.ReadAllText(Path.Combine(Root(), "Controls", "DialogFrame.cs"));
        Assert.DoesNotContain("owner?.TryFindResource(\"ChromeCloseButton\")", frame);
        Assert.DoesNotContain("owner?.TryFindResource(\"OverlayCloseButton\")", frame);
        Assert.DoesNotContain("owner?.TryFindResource(closeStyle)", frame);
    }
}
