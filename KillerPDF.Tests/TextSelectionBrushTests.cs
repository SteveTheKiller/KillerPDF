using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace KillerPDF.Tests;

/// <summary>
/// A text box's selection must use the theme's text selection resources (TextSelectionBrush, TextSelectionTextBrush,
/// TextSelectionOpacity). RowSelectedBrush is a list row tint: on most themes it is a near-black shade of the field
/// or fully transparent, so at the 0.3 selection opacity a drag or Ctrl+A selected the text with no visible
/// highlight (issue #527, the export DPI box). A UiKit field also has to dim when disabled, or the DPI box that
/// "Keep each page's resolution" turns off still looks editable.
/// </summary>
public sealed class TextSelectionBrushTests
{
    private static readonly Regex RowTintSelection = new(
        @"Selection(Text)?Brush(Property)?\s*(=|,)\s*(\w+\(\s*)?""(\{DynamicResource\s+)?RowSelectedBrush",
        RegexOptions.Compiled);

    [Fact]
    public void NoTextBoxSelectsWithTheListRowTint()
    {
        string root = RepoRoot();
        var offenders = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
            .Where(f => !IsBuildOrTestPath(root, f))
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (f, i, line)))
            .Where(x => RowTintSelection.IsMatch(x.line))
            .Select(x => $"{Path.GetRelativePath(root, x.f)}:{x.i + 1}")
            .ToList();

        Assert.True(offenders.Count == 0, "text selection painted with RowSelectedBrush: " + string.Join(", ", offenders));
    }

    [Theory]
    [InlineData("Controls/UiKit.cs", "public static TextBox Field(")]
    [InlineData("Controls/ProtectDocumentDialog.cs", "private static TextBox Field(")]
    public void CodeBuiltFieldsUseTheThemeTextSelection(string file, string signature)
    {
        string body = MethodBody(File.ReadAllText(Path.Combine(RepoRoot(), file)), signature);
        Assert.Contains("\"TextSelectionBrush\"", body);
        Assert.Contains("\"TextSelectionTextBrush\"", body);
        Assert.Contains("\"TextSelectionOpacity\"", body);
    }

    [Fact]
    public void TextSettingsSizeBoxUsesTheThemeTextSelection()
    {
        string source = File.ReadAllText(Path.Combine(RepoRoot(), "Shell", "TextSettingsBar.cs"));
        Assert.Contains("sizeBox.SetResourceReference(TextBox.SelectionBrushProperty, \"TextSelectionBrush\")", source);
        Assert.Contains("sizeBox.SetResourceReference(TextBox.SelectionTextBrushProperty, \"TextSelectionTextBrush\")", source);
        Assert.Contains("sizeBox.SetResourceReference(TextBox.SelectionOpacityProperty, \"TextSelectionOpacity\")", source);
    }

    [Theory]
    [InlineData("Controls/Controls.xaml", "x:Name=\"PART_EditableTextBox\"")]
    [InlineData("Shell/MainWindow.xaml", "x:Name=\"PageJumpBox\"")]
    public void XamlTextBoxesUseTheThemeTextSelection(string file, string marker)
    {
        string xaml = File.ReadAllText(Path.Combine(RepoRoot(), file));
        int start = xaml.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, marker + " not found");
        int end = xaml.IndexOf('>', start);
        string tag = xaml[start..end];
        Assert.Contains("SelectionBrush=\"{DynamicResource TextSelectionBrush}\"", tag);
        Assert.Contains("SelectionTextBrush=\"{DynamicResource TextSelectionTextBrush}\"", tag);
        Assert.Contains("SelectionOpacity=\"{DynamicResource TextSelectionOpacity}\"", tag);
    }

    [Fact]
    public void UiKitFieldDimsWhenDisabled()
    {
        string body = MethodBody(File.ReadAllText(Path.Combine(RepoRoot(), "Controls", "UiKit.cs")), "private static ControlTemplate FieldTemplate(");
        Assert.Matches(new Regex(@"new Trigger\s*\{\s*Property\s*=\s*UIElement\.IsEnabledProperty,\s*Value\s*=\s*false\s*\}"), body);
        Assert.Matches(new Regex(@"new Setter\(UIElement\.OpacityProperty,\s*0\.4\)"), body);
    }

    private static string MethodBody(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, signature + " not found");
        int open = source.IndexOf('{', start), depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[open..(i + 1)];
        }
        throw new InvalidDataException(signature + " has no closing brace");
    }

    private static bool IsBuildOrTestPath(string root, string file)
    {
        string[] parts = Path.GetRelativePath(root, file).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Any(p => p is "bin" or "obj" or ".git" or "KillerPDF.Tests" or "packages")
            || parts[0].EndsWith(".Tests", StringComparison.OrdinalIgnoreCase);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Controls", "Controls.xaml"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("KillerPDF repo root");
    }
}
