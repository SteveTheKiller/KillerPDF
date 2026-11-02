using System.IO;
using System.Xml.Linq;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

// SN01 M1: the owner password prompt before an export on a read-only tab asks for the export, not
// for editing, and the prompt that unlocks editing still says so.
public sealed class UnlockPromptTests
{
    private static string English(string key)
    {
        string directory = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(directory, "KillerPDF.csproj")))
            directory = Path.GetDirectoryName(directory) ?? throw new DirectoryNotFoundException("Repository root not found.");
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        return XDocument.Load(Path.Combine(directory, "Strings", "en-US.xaml")).Root!.Elements()
            .Single(element => (string?)element.Attribute(x + "Key") == key).Value;
    }

    [Fact]
    public void AnOutputAsksForTheOwnerPasswordWithoutSpeakingOfEditing()
    {
        string prompt = English(UnlockPrompt.KeyFor(UnlockPurpose.Output));

        Assert.DoesNotContain("edit", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("owner password", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EditingStillAsksToEdit() =>
        Assert.Contains("edit", English(UnlockPrompt.KeyFor(UnlockPurpose.Edit)), StringComparison.OrdinalIgnoreCase);
}
