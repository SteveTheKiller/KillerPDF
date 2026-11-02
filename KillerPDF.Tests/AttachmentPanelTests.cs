using System.IO;
using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Security;
using KillerPdf.Engine.Writing;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

/// <summary>A limit stops an attachment from being saved, never from being seen: the panel's row is
/// what leads to the owner password prompt.</summary>
public sealed class AttachmentPanelTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("kp-attachments-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static string RestrictedFixture()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "KillerPDF.csproj")))
            directory = directory.Parent;
        return Path.Combine(directory!.FullName, "KillerPDF.Tests", "TestData", "restricted-noedit.pdf");
    }

    [Fact]
    public void ARestrictedFileStillListsItsAttachments()
    {
        IReadOnlyList<PdfAttachmentInfo> attachments = [];

        Assert.Null(Record.Exception(() => attachments = PdfEngineIntegration.ReadAttachments(RestrictedFixture())));

        PdfAttachmentInfo note = Assert.Single(attachments);
        Assert.Equal("note.txt", note.FileName);
        Assert.NotEmpty(note.Data.ToArray());
    }

    [Fact]
    public void AListedAttachmentLeadsToTheOwnerPasswordPrompt()
    {
        OpenedProtection protection = OpenedProtection.Capture(RestrictedFixture(), null);

        Assert.False(protection.Access.Allows(PdfOutput.Data));
        Assert.Equal(ProtectionAnswer.Canceled, protection.AskToWrite(PdfOutput.Data, _ => null));
    }

    // A tab that can edit reads its plain working copy, with the original's encryption still in hand.
    [Fact]
    public void AnEditableProtectedTabListsThemFromItsWorkingCopy()
    {
        string plain = Path.Combine(_folder, "source.pdf");
        File.WriteAllBytes(plain, new PdfDocumentBuilder().AddBlankPage().Build());
        PdfEngineIntegration.AddAttachment(plain, "kept.txt", Encoding.ASCII.GetBytes("kept"));
        string locked = Path.Combine(_folder, "limited.pdf");
        File.WriteAllBytes(locked, PdfDocumentEncryptionWriter.EncryptWithPasswords(
            PdfDocument.Open(File.ReadAllBytes(plain)), new PdfPasswordEncryptionOptions
            {
                UserPassword = "",
                OwnerPassword = "owner-password",
                AllowLowQualityPrinting = false,
                AllowHighQualityPrinting = false
            }));
        OpenedProtection protection = OpenedProtection.Capture(locked, null);
        string working = Path.Combine(_folder, "working.pdf");
        protection.WriteViewingCopy(locked, working);

        IReadOnlyList<PdfAttachmentInfo> attachments = [];
        Assert.Null(Record.Exception(() => attachments = PdfEngineIntegration.ReadAttachments(working, protection)));

        Assert.Equal("kept", Encoding.ASCII.GetString(Assert.Single(attachments).Data.ToArray()));
    }

    [Fact]
    public void AFileThatTakesAPasswordListsThemWithTheKeyTheTabHolds()
    {
        string plain = Path.Combine(_folder, "plain.pdf");
        File.WriteAllBytes(plain, new PdfDocumentBuilder().AddBlankPage().Build());
        PdfEngineIntegration.AddAttachment(plain, "kept.txt", Encoding.ASCII.GetBytes("kept"));
        string locked = Path.Combine(_folder, "locked.pdf");
        File.WriteAllBytes(locked, PdfDocumentEncryptionWriter.EncryptWithPasswords(
            PdfDocument.Open(File.ReadAllBytes(plain)), new PdfPasswordEncryptionOptions
            {
                UserPassword = "reader",
                OwnerPassword = "owner-password",
                AllowDocumentModification = false
            }));

        IReadOnlyList<PdfAttachmentInfo> attachments = [];
        Assert.Null(Record.Exception(() => attachments = PdfEngineIntegration.ReadAttachments(
            locked, OpenedProtection.Capture(locked, "reader"))));

        Assert.Equal("kept", Encoding.ASCII.GetString(Assert.Single(attachments).Data.ToArray()));
    }
}
