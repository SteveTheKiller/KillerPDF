using System.Text.RegularExpressions;
using Xunit;

namespace KillerPDF.Tests;

// In split view the Delete key must act on the pane that has focus. The key handler read the left
// pane's selected form field and then deleted it from whichever pane was active, so a selected
// annotation in the right pane was never removed.
public sealed class SplitDeleteFormFieldTests
{
    [Fact]
    public void DeleteReadsAndDeletesTheFormFieldOfTheActivePaneOnly()
    {
        string keys = ShellSource.Read("Shell", "KeyboardShortcuts.cs");

        Assert.DoesNotMatch(new Regex(@"(?<!Active)Viewer\.(HasSelectedFormFieldExt|DeleteSelectedFormFieldExt)"), keys);
        Assert.Contains("ActiveViewer.HasSelectedFormFieldExt", keys, StringComparison.Ordinal);
        Assert.Contains("ActiveViewer.DeleteSelectedFormFieldExt()", keys, StringComparison.Ordinal);
    }
}