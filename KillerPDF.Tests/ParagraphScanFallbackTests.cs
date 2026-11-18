using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace KillerPDF.Tests;

// Source pin: Edit paragraph on a page whose text it cannot rebuild, such as recognized text over a
// scan, hands a click on that text to the Text tool's line edit instead of refusing (found on the #467
// reporter's file). Driving it needs a window, which this project does not open.
public sealed class ParagraphScanFallbackTests
{
    [Fact]
    public void AnUneditableParagraphUnderTheClickFallsBackToTheLineEdit()
    {
        string source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Shell", "ParagraphEditing.cs"));
        Match empty = Regex.Match(source, @"if \(inspection\.Candidates\.Count == 0\)\s*\{(?<body>.*?RefusalMessage)", RegexOptions.Singleline);
        Assert.True(empty.Success, "EditParagraph has no empty-candidates branch.");
        string body = empty.Groups["body"].Value;
        Assert.Contains("ActiveViewer.IsTextAtPositionExt(pageIndex, clicked)", body);
        Assert.Contains("EditTextAtPosition(clicked, pageIndex)", body);
        Assert.True(body.IndexOf("EditTextAtPosition", System.StringComparison.Ordinal)
            < body.IndexOf("RefusalMessage", System.StringComparison.Ordinal));
    }

    // Source pin: selecting a text box puts its own typeface on the bar, so a bar change cannot
    // restyle it in whatever face the tool had (the #467 reporter's file showed Segoe Print).
    [Fact]
    public void SelectingATextBoxPutsItsTypefaceOnTheBar()
    {
        string root = FindRepositoryRoot();
        string select = File.ReadAllText(Path.Combine(root, "Controls", "Viewer", "PdfViewer.Annotations.cs"));
        Match sync = Regex.Match(select, @"if \(annot is TextAnnotation tsel\)\s*\{(?<body>.*?)ShowTextSettings\(\)", RegexOptions.Singleline);
        Assert.True(sync.Success);
        Assert.Contains("_textFontName = string.IsNullOrEmpty(tsel.FontName) ? \"Segoe UI\" : tsel.FontName;", sync.Groups["body"].Value);
        Assert.Contains("_textBold = tsel.Bold;", sync.Groups["body"].Value);
        string bar = File.ReadAllText(Path.Combine(root, "Shell", "TextSettingsBar.cs"));
        Assert.Contains("if (!fontBox.Items.Contains(_textFontName)) fontBox.Items.Insert(0, _textFontName);", bar);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "KillerPDF.csproj")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("KillerPDF.csproj not found.");
    }
}
