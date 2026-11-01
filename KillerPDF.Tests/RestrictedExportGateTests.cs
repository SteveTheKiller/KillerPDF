using System.IO;
using Xunit;

namespace KillerPDF.Tests;

// A restricted PDF reads from a decrypted copy, so anything that writes that copy elsewhere would
// hand back an unrestricted document. Every such path needs the owner password first.
public sealed class RestrictedExportGateTests
{
    private static string Read(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "KillerPDF.csproj")))
            directory = directory.Parent;
        return File.ReadAllText(Path.Combine(directory!.FullName, relative));
    }

    [Theory]
    [InlineData("Shell/FileOperations.cs", "private void OpenProtectDialog()", "Str_Dlg_SaveBeforeProtect")]
    [InlineData("Shell/FileOperations.cs", "private async void SaveFlattened_Click", "Str_Dlg_SaveFlattened")]
    [InlineData("Shell/PageOperations.cs", "if (_doc is null || _currentFile is null)", "Str_Lbl_Extract")]
    [InlineData("Shell/DirtyTracking.cs", "private void OpenSignDialog()", "suggestedSource")]
    [InlineData("Shell/Macros.cs", "Str_Macro_RedactNothing", "Str_Macro_Running")]
    [InlineData("Shell/ProductionTools.cs", "ReferenceEquals(_doc, sourceDocument)", "string[] sourcePaths")]
    [InlineData("Shell/DocumentTools.cs", "private async void RunDataMerge()", "Str_DataMerge_SelectData")]
    [InlineData("Shell/MacroDataMerge.cs", "if (!batch && !sourceCurrent())", "snapshot = WriteSavedCopyToTemp()")]
    public void OutputPathsAskForTheOwnerPasswordFirst(string file, string start, string end)
    {
        string source = Read(file);
        int from = start == "Str_Macro_RedactNothing"
            ? source.IndexOf("PrepareMacroRedaction(sourceViewer)", StringComparison.Ordinal)
            : source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, start);
        int to = source.IndexOf(end, from, StringComparison.Ordinal);
        Assert.True(to > from, end);
        Assert.Contains("EnsureEditable(", source[from..to], StringComparison.Ordinal);
    }

    [Fact]
    public void RestrictedPagesCannotBeDraggedOut()
    {
        string source = Read("Shell/DragDrop.cs");
        int payload = source.IndexOf("new PageDragPayload(", StringComparison.Ordinal);
        Assert.Contains("!ActiveViewer.IsOwnerRestricted", source[(payload - 700)..payload], StringComparison.Ordinal);
    }

    [Fact]
    public void RecoveryWritesNoDecryptedCopyOfARestrictedDocument()
    {
        string source = Read("Shell/RenderRecovery.cs");
        int name = source.IndexOf("string name = session.SuggestedName", StringComparison.Ordinal);
        Assert.Contains("session.Doc?.IsReadOnly == true", source[(name - 400)..name], StringComparison.Ordinal);
    }
}
