using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace KillerPDF.Tests;

// Steve's rule: a KillerPDF window title reads "KillerPDF - Name", never "KillerPDF: Name". The
// frame draws everything after the wordmark, so a colon shows up as ": Name" in the title bar.
public sealed partial class WindowTitleHyphenTests
{
    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "KillerPDF.csproj")))
            directory = directory.Parent;
        return directory!.FullName;
    }

    [Fact]
    public void NoStringInAnyLanguageTitlesAWindowWithAColon()
    {
        var offenders = Directory.EnumerateFiles(Path.Combine(Root(), "Strings"), "*.xaml")
            .SelectMany(file => File.ReadLines(file)
                .Where(line => ColonValue().IsMatch(line))
                .Select(line => Path.GetFileName(file) + ": " + line.Trim()))
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void NoWindowTitleInCodeIsBuiltWithAColon()
    {
        string root = Root();
        var offenders = new[] { "Controls", "Shell", "Features", "Services" }
            .SelectMany(folder => Directory.EnumerateFiles(Path.Combine(root, folder), "*.cs", SearchOption.AllDirectories))
            .Append(Path.Combine(root, "App.xaml.cs"))
            .SelectMany(file => File.ReadLines(file)
                .Where(line => ColonLiteral().IsMatch(line))
                .Select(line => Path.GetRelativePath(root, file) + ": " + line.Trim()))
            .ToList();

        Assert.Empty(offenders);
    }

    // A string resource whose value starts "KillerPDF:".
    [GeneratedRegex(@"<sys:String [^>]*>\s*KillerPDF\s*:")]
    private static partial Regex ColonValue();

    // A C# string literal (plain or interpolated) that starts "KillerPDF:".
    [GeneratedRegex(@"\$?@?""KillerPDF\s*:")]
    private static partial Regex ColonLiteral();
}
