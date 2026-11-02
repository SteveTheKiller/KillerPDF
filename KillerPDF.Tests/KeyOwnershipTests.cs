using System.IO;
using KillerPDF.Services;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Security;
using KillerPdf.Engine.Signing;
using Xunit;

namespace KillerPDF.Tests;

// The tab owns its key. A signed PDF that needs a password is read where it lies, under the key the
// tab's own password produced, and nothing else in the process can read it with that key.
public sealed class KeyOwnershipTests : IDisposable
{
    private const string Owner = "owner-password";
    private const string Reader = "reader";
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "KillerPDF-keys-" + Guid.NewGuid().ToString("N"));

    public KeyOwnershipTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, true);

    private string InFolder(string name) => Path.Combine(_folder, name);

    // A signed form whose user password lets the reader fill it and nothing more.
    private string SignedForm(string name)
    {
        string unsigned = InFolder(name + "-unsigned.pdf");
        File.WriteAllBytes(unsigned, new PdfDocumentBuilder()
            .SetPasswordEncryption(new PdfPasswordEncryptionOptions
            {
                UserPassword = Reader,
                OwnerPassword = Owner,
                AllowDocumentModification = false,
                AllowFormFilling = true,
            })
            .AddPage(200, 200, "q 0 0 50 50 re f Q"u8.ToArray())
            .AddTextField(0, "name", 10, 120, 120, 20, "old value", 10)
            .Build());
        string signed = InFolder(name + ".pdf");
        File.WriteAllBytes(signed, PdfDetachedSignatureWriter.Sign(PdfDocument.Open(File.ReadAllBytes(unsigned), Owner),
            _ => new byte[64], new PdfSignatureOptions { FieldName = "Signature1" }));
        return signed;
    }

    private static PdfEngineIntegration.FormEdits Text(string name, string value) => new(
        new Dictionary<string, string> { [name] = value }, new Dictionary<string, string>(),
        new Dictionary<string, IReadOnlyList<string>>(), new Dictionary<string, bool>(),
        new Dictionary<string, string>(), new Dictionary<string, double>());

    private static string FieldValue(PdfDocument document, string name) =>
        PdfFormWidgetReader.ReadPage(document, 0).Single(widget => widget.FieldName == name).Value;

    // The saved file still asks for the same passwords and declares the same limits, and the tab's
    // key still fits it, which needs the same /O and /U.
    private static void AssertSameProtection(string path, OpenedProtection tab)
    {
        Assert.Equal(PdfAccessRole.Refused, PdfRestrictions.Read(path, null).Role);
        PdfAccess reader = PdfRestrictions.Read(path, Reader);
        Assert.Equal(PdfAccessRole.User, reader.Role);
        Assert.False(reader.MayModify);
        Assert.Equal(PdfAccessRole.Owner, PdfRestrictions.Read(path, Owner).Role);
        Assert.True(OpenedProtection.Capture(path, Reader).AllowsRestrictedFill);
        Assert.True(tab.OpenHeld(File.ReadAllBytes(path))!.IsDecrypted);
    }

    // R5-001: a restricted fill saved into a signed original reopens it under the tab's own key, so
    // the tab can still fill, print and save after the reopen, and after a collection.
    [Fact]
    public void ARestrictedFillSavedIntoASignedOriginalKeepsTheTabsKey()
    {
        string signed = SignedForm("fill");
        OpenedProtection protection = OpenedProtection.Capture(signed, Reader);
        Assert.True(protection.AllowsRestrictedFill);

        string filled = InFolder("filled-1.pdf");
        PdfEngineIntegration.FillRestrictedForm(signed, filled, Text("name", "first"), protection);
        PdfEngineIntegration.ReplaceFileAtomically(filled, signed);
        (PdfWorkingDocument document, OpenedProtection reopened) = ReadOnlyOriginal.Restore(signed, protection);
        document.Dispose();

        Assert.Equal(PdfAccessRole.User, reopened.Access.Role);
        Assert.True(reopened.AllowsRestrictedFill);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        string second = InFolder("filled-2.pdf");
        Assert.Null(Record.Exception(() => PdfEngineIntegration.FillRestrictedForm(signed, second, Text("name", "second"), reopened)));
        PdfEngineIntegration.ReplaceFileAtomically(second, signed);
        Assert.Equal("second", FieldValue(PdfDocument.OpenWithCompatibilityRecovery(File.ReadAllBytes(signed), Reader), "name"));
        Assert.Contains(PdfSignatureReader.Read(PdfDocument.OpenWithCompatibilityRecovery(File.ReadAllBytes(signed), Reader)),
            signature => signature.IsSigned);
        AssertSameProtection(signed, reopened);
    }

    // R5-002: one tab's key opens nothing for another. A second tab of the same signed file, or of a
    // byte copy of it, takes the password prompt like the first did.
    [Fact]
    public void ASecondTabOfASignedFileAsksForItsOwnPassword()
    {
        string signed = SignedForm("second-tab");
        OpenedProtection first = OpenedProtection.Capture(signed, Reader);
        using (PdfWorkingDocument tab = PdfWorkingDocument.Open(signed, first, isReadOnly: true))
            Assert.Equal(1, tab.PageCount);

        // The open route reads a file before any password is asked, with no tab's key.
        Exception? second = Record.Exception(() => PdfWorkingDocument.Open(signed, null).Dispose());

        Assert.NotNull(second);
        Assert.Contains("password", second.Message, StringComparison.OrdinalIgnoreCase);
        string elsewhere = InFolder("second-tab-copy.pdf");
        File.Copy(signed, elsewhere);
        Assert.False(PdfEngineIntegration.OpenForReading(File.ReadAllBytes(elsewhere)).IsDecrypted);
        GC.KeepAlive(first);
    }

    // R5-007: the decrypted parse the tab registered for rendering is served only to the tab that
    // holds the key, never to a caller that comes to the path with none, before or after the tab closes.
    [Fact]
    public void TheRenderCacheNeverHandsTheDecryptedParseToACallerWithoutTheKey()
    {
        string signed = SignedForm("cache");
        OpenedProtection protection = OpenedProtection.Capture(signed, Reader);
        PdfWorkingDocument tab = PdfWorkingDocument.Open(signed, protection, isReadOnly: true);

        PdfDocument parsed = tab.ParsedDocument!;
        Assert.False(PdfPageRenderSession.OpenDocument(signed, null).CanReadPageContent);
        Assert.Same(parsed, PdfPageRenderSession.OpenDocument(signed, protection));
        // A second tab of the file holds its own key and gets its own parse, never the first tab's.
        OpenedProtection secondTab = OpenedProtection.Capture(signed, Reader);
        PdfDocument second = PdfPageRenderSession.OpenDocument(signed, secondTab);
        Assert.NotSame(parsed, second);
        Assert.True(second.CanReadPageContent);

        PdfPageRenderSession.ForgetKey(protection);
        tab.Dispose();
        Assert.False(PdfPageRenderSession.OpenDocument(signed, null).CanReadPageContent);
    }
}
