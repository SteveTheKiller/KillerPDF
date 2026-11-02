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
    [InlineData("Features/Ocr/OcrController.cs", "internal async void MakeSearchablePdf()", "CaptureDocumentOrigin")]
    [InlineData("Features/Ocr/OcrController.cs", "internal async void ConvertScanToText()", "CaptureDocumentOrigin")]
    [InlineData("Shell/FileOperations.cs", "void ExportDocument_Click", "forRasterization: options.OfficeMode")]
    [InlineData("Shell/Macros.cs", "Str_Macro_RedactNothing", "Str_Macro_Running")]
    [InlineData("Shell/ProductionTools.cs", "ReferenceEquals(_doc, sourceDocument)", "string[] sourcePaths")]
    [InlineData("Shell/DocumentTools.cs", "private async void RunDataMerge()", "Str_DataMerge_SelectData")]
    [InlineData("Shell/MacroDataMerge.cs", "if (!batch && !sourceCurrent())", "snapshot = WriteSavedCopyToTemp()")]
    [InlineData("Shell/FileOperations.cs", "private async Task ExportImagesFlow(", "EnsureEngineDocumentSession()")]
    [InlineData("Shell/SidebarPanels.cs", "private void SaveAttachmentCopy(", "var dialog = new Controls.FileDialog")]
    public void OutputPathsAskForTheOwnerPasswordFirst(string file, string start, string end)
    {
        string source = Read(file);
        int from = start == "Str_Macro_RedactNothing"
            ? source.IndexOf("PrepareMacroRedaction(sourceViewer)", StringComparison.Ordinal)
            : source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, start);
        int to = source.IndexOf(end, from, StringComparison.Ordinal);
        Assert.True(to > from, end);
        Assert.Matches(@"EnsureEditable\(|Ensure(Document)?MayWrite\(|EnsureMayRemoveProtection\(", source[from..to]);
    }

    // The unlock reopens the document and drops the page selection, so these end the command and the
    // user repeats it on the unlocked document.
    [Theory]
    [InlineData("Shell/FileOperations.cs", "private async Task ExportImagesFlow(", "EnsureEngineDocumentSession()")]
    [InlineData("Shell/SidebarPanels.cs", "private void SaveAttachmentCopy(", "var dialog = new Controls.FileDialog")]
    public void ImagesAndAttachmentsEndTheCommandAfterTheUnlock(string file, string start, string end)
    {
        string source = Read(file);
        int from = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, start);
        int to = source.IndexOf(end, from, StringComparison.Ordinal);
        Assert.True(to > from, end);
        Assert.Matches(@"EnsureEditable\(continueAfterUnlock: false\)|EnsureMayWrite\(PdfOutput\.\w+\)", source[from..to]);
    }

    // Printing is not editing: a PDF whose owner forbids only printing still opens for editing, so the
    // print command reads the print permission itself instead of asking EnsureEditable.
    [Fact]
    public void PrintingReadsThePrintPermissionBeforeItPreparesAnything()
    {
        string source = Read("Shell/FileOperations.cs");
        int from = source.IndexOf("private void Print_Click(", StringComparison.Ordinal);
        int to = source.IndexOf("private async void RunPrintFlow(", from, StringComparison.Ordinal);
        Assert.True(from >= 0 && to > from);
        string body = source[from..to];
        Assert.Contains("TryAllowPrinting(", body, StringComparison.Ordinal);
        Assert.DoesNotContain("EnsureEditable(", body, StringComparison.Ordinal);
        Assert.True(body.IndexOf("TryAllowPrinting(", StringComparison.Ordinal) < body.IndexOf("RunPrintFlow(", StringComparison.Ordinal));
    }

    [Fact]
    public void RecoveryWritesNoDecryptedCopyOfARestrictedDocument()
    {
        string source = Read("Shell/RenderRecovery.cs");
        int name = source.IndexOf("string name = session.SuggestedName", StringComparison.Ordinal);
        Assert.Contains("session.Doc?.IsReadOnly == true", source[(name - 400)..name], StringComparison.Ordinal);
    }
}
