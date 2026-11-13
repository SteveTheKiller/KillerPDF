using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace KillerPDF.Tests;

/// <summary>
/// The kit's implicit TextBox style sets Height 28 and Padding 6,3. The footer zoom box keeps its
/// PART_EditableTextBox Hidden, which still takes layout space, so without its own values the 28-tall
/// box outgrew the 20-tall combo and pushed "Fit Page" below the visible area, leaving only the tops
/// of the letters showing.
/// </summary>
public sealed class FooterZoomBoxLayoutTests
{
    [Fact]
    public void TheHiddenZoomTextBoxDoesNotTakeTheKitInputHeight()
    {
        string window = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Shell", "MainWindow.xaml"));
        int style = window.IndexOf("x:Key=\"FooterZoomCombo\"", StringComparison.Ordinal);
        Assert.True(style >= 0, "FooterZoomCombo was not found.");
        Match box = Regex.Match(window[style..], @"<TextBox x:Name=""PART_EditableTextBox""[^>]*>", RegexOptions.Singleline);
        Assert.True(box.Success, "The zoom box template has no PART_EditableTextBox.");
        Assert.Contains("Height=\"18\"", box.Value);
        Assert.Contains("MinHeight=\"0\"", box.Value);
        Assert.Contains("Padding=\"0\"", box.Value);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "KillerPDF.csproj"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new InvalidOperationException("KillerPDF.csproj was not found above the test output.");
    }
}
