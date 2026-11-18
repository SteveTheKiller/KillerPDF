using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace KillerPDF.Tests;

// The Redact bar's Mark pages asks for its page list in a small flyout under its own button, not a
// dialog; the Find and Properties dialogs are sized to their content; and the Find dialog's button
// says it draws redaction boxes (Steve, 2026-11-17). The viewer is not linked into this project, so
// the wiring is pinned in its source.
public sealed class RedactBarFlyoutTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "KillerPDF.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([Root(), .. parts]));

    private static string Between(string text, string start, string end)
    {
        int from = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, "missing " + start);
        int to = text.IndexOf(end, from + start.Length, StringComparison.Ordinal);
        return text[from..(to < 0 ? text.Length : to)];
    }

    [Fact]
    public void MarkPages_OpensAFlyoutOnItsOwnButton_NotADialog()
    {
        string source = Read("Controls", "Viewer", "PdfViewer.Redaction.cs");
        Assert.DoesNotContain("KillerDialog.PromptText", source);
        Assert.Contains("OpenMarkPagesFlyout(markPagesButton)", source);

        string flyout = Between(source, "private void OpenMarkPagesFlyout(", "private void MarkPagesForRedaction(");
        Assert.Contains("ToolOptionsBar.MakePopup(anchor,", flyout);
        Assert.Contains("Str_Redact_MarkPagesPrompt", flyout);
        Assert.Contains("Str_Redact_MarkPagesConfirm", flyout);
        Assert.Contains("CurrentPageIndex + 1", flyout);
        Assert.Contains("Key.Enter", flyout);
    }

    [Fact]
    public void MarkPages_KeepsItsValidation()
    {
        string source = Read("Controls", "Viewer", "PdfViewer.Redaction.cs");
        string apply = Between(source, "private void MarkPagesForRedaction(string range)", "private void EditRedactionProperties(");
        Assert.Contains("CliParsePageRange(range, session.PageCount", apply);
        Assert.Contains("Str_InvalidRange", apply);
        Assert.Contains("Str_Redact_MarkedPages", apply);
    }

    [Fact]
    public void MarkPagesFlyout_ClosesWithTheBar()
    {
        string source = Read("Controls", "Viewer", "PdfViewer.Redaction.cs");
        Assert.Contains("_markPagesFlyout.IsOpen = false", Between(source, "internal void HideRedactionBar()", "internal void LoadRedactionMarksFromDocument()"));
    }

    [Theory]
    [InlineData("RedactionFindDialog.cs", 400)]
    [InlineData("RedactionPropertiesDialog.cs", 340)]
    public void RedactionDialogs_AreSizedToTheirContent(string file, int width)
    {
        string source = Read("Controls", file);
        Match match = Regex.Match(source, @"\n\s*Width = (\d+);");
        Assert.True(match.Success, "no window width in " + file);
        Assert.Equal(width, int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void FindDialog_SaysItDrawsRedactionBoxes()
    {
        string strings = Read("Strings", "en-US.xaml");
        Assert.Contains("x:Key=\"Str_RedactFind_Mark\">Draw redaction boxes<", strings);
        Assert.Contains("x:Key=\"Str_RedactFind_Hint\">Draws a redaction box over each checked match on the page. "
            + "Nothing is removed until you click Redact on the Redact bar.<", strings);
    }

    [Fact]
    public void FindDialog_NoLocaleKeepsTheOldMarkCheckedWording()
    {
        foreach (string file in Directory.GetFiles(Path.Combine(Root(), "Strings"), "*.xaml"))
            Assert.DoesNotContain("x:Key=\"Str_RedactFind_Mark\">Mark checked<", File.ReadAllText(file));
    }
}
