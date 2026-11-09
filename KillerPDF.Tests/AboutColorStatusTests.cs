using System.IO;
using System.Xml.Linq;
using Xunit;

namespace KillerPDF.Tests;

/// <summary>
/// 1.9.3 ships managed SDR viewing and printer proofing, and says plainly in About that HDR and
/// Advanced Color monitors and physical color accuracy are still in progress (#264).
/// </summary>
public sealed class AboutColorStatusTests
{
    private const string Key = "Str_About_ColorStatus";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void AboutStatesThatHdrAndPhysicalColorAccuracyAreInProgress()
    {
        string root = RepositoryRoot();
        string english = Value(Path.Combine(root, "Strings", "en-US.xaml"));
        Assert.Contains("HDR", english, StringComparison.Ordinal);
        Assert.Contains("in progress", english, StringComparison.Ordinal);

        string xaml = File.ReadAllText(Path.Combine(root, "Shell", "MainWindow.xaml"));
        int at = xaml.IndexOf("{DynamicResource " + Key + "}", StringComparison.Ordinal);
        Assert.True(at > 0, "The About card must show " + Key + ".");
        int version = xaml.IndexOf("x:Name=\"AboutVersionBlock\"", StringComparison.Ordinal);
        int publisher = xaml.IndexOf("Str_About_Publisher", StringComparison.Ordinal);
        Assert.True(at > version && at < publisher, "The line belongs with the version details.");
        string element = xaml[xaml.LastIndexOf("<TextBlock", at, StringComparison.Ordinal)..xaml.IndexOf("/>", at, StringComparison.Ordinal)];
        Assert.Contains("TextWrapping=\"Wrap\"", element, StringComparison.Ordinal);
        Assert.DoesNotContain("#", element, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryLocaleTranslatesTheColorStatusLine()
    {
        string root = RepositoryRoot();
        string english = Value(Path.Combine(root, "Strings", "en-US.xaml"));
        char enDash = (char)0x2013, emDash = (char)0x2014;
        foreach (string file in Directory.GetFiles(Path.Combine(root, "Strings"), "*.xaml"))
        {
            string name = Path.GetFileName(file);
            string value = Value(file);
            Assert.False(string.IsNullOrWhiteSpace(value), name + " has no " + Key + ".");
            if (name != "en-US.xaml")
                Assert.NotEqual(english, value);
            Assert.DoesNotContain(enDash, value);
            Assert.DoesNotContain(emDash, value);
            Assert.Contains("HDR", value, StringComparison.Ordinal);
        }
    }

    private static string Value(string file)
    {
        var element = XDocument.Load(file).Descendants()
            .FirstOrDefault(e => (string?)e.Attribute(Xaml + "Key") == Key);
        return element?.Value ?? "";
    }

    private static string RepositoryRoot()
    {
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "KillerPDF.csproj")))
            root = Path.GetDirectoryName(root) ?? throw new DirectoryNotFoundException("KillerPDF.csproj");
        return root;
    }
}
