using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace KillerPDF.Tests;

/// <summary>
/// Saving a fill-allowed restricted form writes the values into the protected file itself. Three
/// things about that path are checked here from the source: the conflict prompt's Save As button
/// opens Save As instead of doing nothing (BF-A001), a failed replace puts the typed values back on
/// the reopened read-only original (BF-A002), and the permission check that decides whether the
/// form can be saved this way runs once per open, not on every Ctrl+S (BF-A013).
/// </summary>
public sealed class RestrictedFormSaveTests
{
    [Fact]
    public void TheConflictPromptsSaveAsButtonOpensSaveAs()
    {
        string restricted = RestrictedSaveBranch();
        Assert.Contains("SaveAs_Click(this, new RoutedEventArgs())", restricted);
        Assert.Matches(@"== MessageBoxResult\.No\)\s*\{\s*SaveAs_Click", restricted);
        // The old shape: the prompt's result compared straight to Yes, so Save As fell through to return.
        Assert.DoesNotMatch(@"Loc\(""Str_Btn_CancelDlg""\)\) != MessageBoxResult\.Yes\)", restricted);
    }

    [Fact]
    public void AFailedReplaceKeepsTheTypedValues()
    {
        string body = Method("SaveRestrictedFormFill");
        Assert.DoesNotContain("RestoreReadOnlyOriginal(source, _originalFile ?? source, () => { }", body);
        Assert.Contains("RestoreReadOnlyOriginal(source, _originalFile ?? source, CarryOverlays, protection)", body);
        foreach (string dictionary in new[] { "_formTextValues", "_formChoiceValues", "_formCheckValues", "_formRadioValues", "_formFontSizes" })
            Assert.Matches(@"new Dictionary<string, \w+>\(" + dictionary + @"\)", body);
        Assert.Contains("MarkDirty(true)", body);
        // The successful save reopens the saved file clean; only the failure branch carries values.
        Assert.Contains("RestoreReadOnlyOriginal(destination, destination, () => { }, protection)", body);
    }

    [Fact]
    public void ThePermissionCheckRunsOncePerOpen()
    {
        string source = FileOperations();
        Match gate = Regex.Match(source, @"private bool CanSaveRestrictedFormFill\(\) =>(?<body>.*?);", RegexOptions.Singleline);
        Assert.True(gate.Success, "CanSaveRestrictedFormFill was not found.");
        Assert.DoesNotContain("PdfEngineIntegration.CanFillRestrictedForm(", gate.Groups["body"].Value);
        Assert.Contains("_restrictedFormFillable", gate.Groups["body"].Value);

        string open = Method("TryOpenProtectedReadOnly");
        int finish = open.IndexOf("FinishOpenFile(", StringComparison.Ordinal);
        int cache = open.IndexOf("_restrictedFormFillable = _protection.AllowsRestrictedFill;", StringComparison.Ordinal);
        Assert.True(finish >= 0 && cache > finish, "The protected read-only open does not cache the fill permission after FinishOpenFile.");

        Assert.Contains("_restrictedFormFillable = false;", Method("FinishOpenFile"));
    }

    private static string RestrictedSaveBranch()
    {
        string save = Method("SaveInPlace", "(bool otherPaneConfirmed, bool removeProtection = false)");
        Match branch = Regex.Match(save, @"if \(!removeProtection && CanSaveRestrictedFormFill\(\)[^\n]*\n\s*\{(?<body>.*?)\n            \}", RegexOptions.Singleline);
        Assert.True(branch.Success, "The restricted-form branch of SaveInPlace was not found.");
        return branch.Groups["body"].Value;
    }

    private static string Method(string name, string signature = "(")
    {
        string source = FileOperations();
        Match method = Regex.Match(source, @"private \w+ " + Regex.Escape(name) + Regex.Escape(signature) + @".*?\n        \{(?<body>.*?)\n        \}", RegexOptions.Singleline);
        Assert.True(method.Success, name + " was not found.");
        return method.Groups["body"].Value;
    }

    private static string FileOperations() =>
        File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Shell", "FileOperations.cs"));

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
