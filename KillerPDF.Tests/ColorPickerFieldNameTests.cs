using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace KillerPDF.Tests;

/// <summary>The picker's R, G and B boxes show only one-letter labels, so each passes a spoken name.</summary>
public sealed class ColorPickerFieldNameTests
{
    [Fact]
    public void PrecisionColorFieldsHaveSpokenNames()
    {
        string root = FindRepositoryRoot();
        string source = File.ReadAllText(Path.Combine(root, "Controls", "ColorPickerDialog.cs"));
        foreach (var (letter, key) in new[] { ("R", "Str_Color_Red"), ("G", "Str_Color_Green"), ("B", "Str_Color_Blue") })
            Assert.Matches(new Regex($@"FieldGroup\(""{letter}"", _{letter.ToLowerInvariant()}Box, L\(""{key}""\)\)"), source);
        Assert.Contains("AutomationProperties.SetName(box, name)", source);
        Assert.Contains("SetName(_hexBox, L(\"Str_Color_Hex\"))", source);
        foreach (string file in Directory.GetFiles(Path.Combine(root, "Strings"), "*.xaml"))
        {
            string strings = File.ReadAllText(file);
            foreach (string key in new[] { "Str_Color_Red", "Str_Color_Green", "Str_Color_Blue", "Str_Color_Hex" })
                Assert.True(strings.Contains($"x:Key=\"{key}\""), $"{Path.GetFileName(file)} lacks {key}");
        }
    }

    // Setting DialogResult can throw once the eyedropper's nested capture window has run, so the close X, Escape
    // and Cancel all go through one guarded Cancel, as OK goes through Accept.
    [Fact]
    public void EveryWayToCancelThePickerIsGuarded()
    {
        string source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Controls", "ColorPickerDialog.cs"));
        Assert.Contains("try { DialogResult = false; } catch (InvalidOperationException) { }", source);
        Assert.Single(Regex.Matches(source, @"(?<!capture\.)DialogResult = false"));
        Assert.Contains("if (e.Key == Key.Escape) Cancel();", source);
        Assert.Contains("DialogFrame.Frame(this, Owner, ColorPdfTitle,\n                Cancel, panel", source.Replace("\r\n", "\n"));
        Assert.Contains("cancel.Click += (_, _) => Cancel();", source);
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
