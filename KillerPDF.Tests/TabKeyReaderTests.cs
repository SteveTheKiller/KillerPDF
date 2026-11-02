using System.IO;
using KillerPDF.Services;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Parsing;
using KillerPdf.Engine.Security;
using KillerPdf.Engine.Signing;
using Xunit;

namespace KillerPDF.Tests;

// R5-004: a read-only tab reads its file where it lies, the encrypted original. Every reader of that
// file takes the tab's protection, so an owner-only file opens through its empty password and a signed
// file that needs a password opens under the key the tab holds.
public sealed class TabKeyReaderTests : IDisposable
{
    private const string Owner = "owner-password";
    private const string Reader = "reader";
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "KillerPDF-tabkey-" + Guid.NewGuid().ToString("N"));

    public TabKeyReaderTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, true);

    private string InFolder(string name) => Path.Combine(_folder, name);

    private static byte[] Content(string user) => new PdfDocumentBuilder()
        .SetPasswordEncryption(new PdfPasswordEncryptionOptions
        {
            UserPassword = user,
            OwnerPassword = Owner,
            AllowDocumentModification = false,
        })
        .AddPage(300, 400, new PdfContentStreamBuilder()
            .BeginText().SetFont(PdfStandardFont.HelveticaBold, 22)
            .MoveText(30, 350).ShowLatin1Text("Chapter One").EndText()
            .BeginText().SetFont(PdfStandardFont.Helvetica, 10)
            .MoveText(30, 300).ShowLatin1Text("Body copy").EndText())
        .AddTextNote(0, 100, 100, "A comment")
        .AddBookmark("Chapter One", 0)
        .Build();

    // An owner-only file that forbids editing: the read-only tab reads the original.
    private (string Path, OpenedProtection Protection) OwnerOnly()
    {
        string path = InFolder("owner-only.pdf");
        File.WriteAllBytes(path, Content(""));
        return (path, OpenedProtection.Capture(path, null));
    }

    // A signed file that needs a password: the tab reads the original under its key.
    private (string Path, OpenedProtection Protection) SignedWithUserPassword()
    {
        string unsigned = InFolder("signed-unsigned.pdf");
        File.WriteAllBytes(unsigned, Content(Reader));
        string path = InFolder("signed.pdf");
        File.WriteAllBytes(path, PdfDetachedSignatureWriter.Sign(PdfDocument.Open(File.ReadAllBytes(unsigned), Owner),
            _ => new byte[64], new PdfSignatureOptions { FieldName = "Signature1" }));
        return (path, OpenedProtection.Capture(path, Reader));
    }

    public static TheoryData<bool> Tabs => new() { false, true };

    private (string Path, OpenedProtection Protection) Tab(bool signed) => signed ? SignedWithUserPassword() : OwnerOnly();

    [Theory]
    [MemberData(nameof(Tabs))]
    public void TheCommentsPanelReadsTheTabsFile(bool signed)
    {
        (string path, OpenedProtection protection) = Tab(signed);
        Assert.Equal(PdfAccessRole.User, protection.Access.Role);

        IReadOnlyList<PdfCommentThread>? threads = null;
        Exception? failure = Record.Exception(() => threads = PdfEngineIntegration.ReadCommentThreads(path, protection));

        Assert.Null(failure);
        Assert.Equal("A comment", Assert.Single(threads!).Comment.Contents);
    }

    [Theory]
    [MemberData(nameof(Tabs))]
    public void TheNavigationAuditReadsTheTabsFile(bool signed)
    {
        (string path, OpenedProtection protection) = Tab(signed);
        Assert.Null(Record.Exception(() => PdfEngineIntegration.InspectNavigation(path, protection)));
        string? report = null;
        Assert.Null(Record.Exception(() => report = PdfEngineIntegration.FormatNavigationAudit(path, protection)));
        Assert.False(string.IsNullOrWhiteSpace(report));
    }

    [Theory]
    [MemberData(nameof(Tabs))]
    public void AutoBookmarksReadTheTabsText(bool signed)
    {
        (string path, OpenedProtection protection) = Tab(signed);
        bool hasText = false;
        Assert.Null(Record.Exception(() => hasText = PdfEngineIntegration.HasTextLayer(path, protection)));
        Assert.True(hasText);
        IReadOnlyList<PdfBookmarkProposal>? proposals = null;
        Assert.Null(Record.Exception(() => proposals = PdfEngineIntegration.DetectBookmarkHeadings(path,
            new PdfBookmarkDetectionOptions { MinimumPointSize = 14 }, protection)));
        Assert.Equal("Chapter One", Assert.Single(proposals!).Title);
    }

    [Theory]
    [MemberData(nameof(Tabs))]
    public void TheTableOfContentsPlanReadsTheTabsBookmarks(bool signed)
    {
        (string path, OpenedProtection protection) = Tab(signed);
        PdfTableOfContentsPlan? plan = null;
        Assert.Null(Record.Exception(() => plan = PdfEngineIntegration.PlanTableOfContents(path, 6, protection)));
        Assert.Equal("Chapter One", Assert.Single(plan!.Entries).Title);
    }

    [Theory]
    [MemberData(nameof(Tabs))]
    public void TheSeparationPreviewAndTheStructureCompareReadTheTabsFile(bool signed)
    {
        (string path, OpenedProtection protection) = Tab(signed);
        // The plates come from the production report, as the Separations window asks for them.
        PdfPrintProductionReport? production = null;
        Assert.Null(Record.Exception(() => production = PdfEngineIntegration.InspectPrintProduction(path, protection)));
        PdfSeparationPreview? preview = null;
        Assert.Null(Record.Exception(() => preview = PdfEngineIntegration.CreateSeparationPreview(path,
            production!.Colorants.Select(colorant => colorant.Name), protection)));
        Assert.Single(preview!.Pages);
        PdfStructuralComparison? comparison = null;
        Assert.Null(Record.Exception(() => comparison = PdfEngineIntegration.CompareStructure(path, protection, path, protection)));
        Assert.Empty(comparison!.Changes);
    }
}
