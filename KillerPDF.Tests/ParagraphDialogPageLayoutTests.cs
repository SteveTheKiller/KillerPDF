using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace KillerPDF.Tests;

// Source pins: Edit paragraph copies Transform's page window, so it has the prev/next flippers and the
// "4 / 6" counter under the page, and its Original checkbox sits in the options column on the right.
// Driving the dialog needs a window, which this project does not open.
public sealed class ParagraphDialogPageLayoutTests
{
    private static string Dialog() =>
        File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Controls", "ParagraphEditDialog.cs"));

    [Fact]
    public void ThePageColumnHasTransformsFlippersAndCounter()
    {
        string source = Dialog();
        Assert.Contains("PageNavButtons.Previous(() => ShowPage(_pageIndex - 1))", source);
        Assert.Contains("PageNavButtons.Next(() => ShowPage(_pageIndex + 1))", source);
        Assert.Contains("_pageCounter.Text = $\"{_pageIndex + 1} / {_pages.Count}\";", source);
        Match column = Regex.Match(source, @"var pageColumn = new DockPanel\(\);(?<body>.*?)root\.Children\.Add\(pageColumn\);",
            RegexOptions.Singleline);
        Assert.True(column.Success, "The page column was not found.");
        Assert.Contains("DockPanel.SetDock(navigation, Dock.Bottom)", column.Groups["body"].Value);
        Assert.DoesNotContain("_original", column.Groups["body"].Value);
    }

    [Fact]
    public void TheOriginalCheckboxSitsInTheOptionsColumn()
    {
        string source = Dialog();
        int original = source.IndexOf("content.Children.Add(_original);", System.StringComparison.Ordinal);
        int sidebar = source.IndexOf("Content = content", System.StringComparison.Ordinal);
        Assert.True(original > 0, "The Original checkbox is not added to the options column.");
        Assert.True(original < sidebar, "The Original checkbox must join the options before they go into the side scroller.");
    }

    [Fact]
    public void AFlipReloadsTheNewPagesParagraphsAndAsksBeforeDroppingAnEdit()
    {
        string source = Dialog();
        Match flip = Regex.Match(source, @"private void ShowPage\(int index\)\s*\{(?<body>.*?)\n    \}", RegexOptions.Singleline);
        Assert.True(flip.Success, "ShowPage was not found.");
        string body = flip.Groups["body"].Value;
        Assert.Contains("_operation is not null", body);
        Assert.Contains("_userEdited && KillerDialog.Show(this, L(\"Str_Paragraph_DiscardOnFlip\")", body);
        Assert.Contains("PdfParagraphEditor.InspectCandidates(_document, index)", body);
        Assert.Contains("_pageRotated(index)", body);
        Assert.Contains("_candidateBox.ItemsSource = CandidateItems();", body);
        Assert.Contains("_ = LoadOriginalImage();", body);
        Assert.True(body.IndexOf("Str_Paragraph_DiscardOnFlip", System.StringComparison.Ordinal)
            < body.IndexOf("_pageIndex = index", System.StringComparison.Ordinal));
    }

    [Fact]
    public void ApplyEditsThePageTheDialogEndedOn()
    {
        string source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Shell", "ParagraphEditing.cs"));
        Assert.Contains("new ParagraphEditDialog(this, document, pageIndex, _appScale, point, inspection, PageRotated)", source);
        int result = source.IndexOf("if (dialog.Result is not { } plan) return;", System.StringComparison.Ordinal);
        int page = source.IndexOf("pageIndex = dialog.PageIndex;", System.StringComparison.Ordinal);
        int reload = source.IndexOf("selectedPageAfterReload: pageIndex", System.StringComparison.Ordinal);
        Assert.True(result > 0 && page > result && reload > page);
    }

    [Fact]
    public void EveryLocaleCarriesTheFlipQuestion()
    {
        foreach (string file in Directory.GetFiles(Path.Combine(FindRepositoryRoot(), "Strings"), "*.xaml"))
            Assert.Contains("x:Key=\"Str_Paragraph_DiscardOnFlip\"", File.ReadAllText(file));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "KillerPDF.csproj")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("KillerPDF.csproj not found.");
    }
}
