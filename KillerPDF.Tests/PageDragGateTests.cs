using System.IO;
using KillerPDF.Services;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Security;
using Xunit;

namespace KillerPDF.Tests;

// SN01 D13: dragging pages out of a limited tab dropped nothing and said nothing. A limit an owner
// password can lift asks for it, a limit nothing can lift says so, and once the owner has lifted it,
// by unlocking the tab or by the password asked for an output, the pages go.
public sealed class PageDragGateTests : IDisposable
{
    private const string Owner = "owner-password";
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "KillerPDF-pagedrag-" + Guid.NewGuid().ToString("N"));

    public PageDragGateTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, true);

    private OpenedProtection Opened(bool print, bool modify)
    {
        string path = Path.Combine(_folder, $"limited-{print}-{modify}.pdf");
        File.WriteAllBytes(path, new PdfDocumentBuilder().SetPasswordEncryption(new PdfPasswordEncryptionOptions
        {
            UserPassword = "",
            OwnerPassword = Owner,
            AllowLowQualityPrinting = print,
            AllowHighQualityPrinting = print,
            AllowDocumentModification = modify,
        }).AddBlankPage(200, 200).Build());
        return OpenedProtection.Capture(path, null);
    }

    [Fact]
    public void APrintLimitedTabAsksForTheOwnerPassword() =>
        Assert.Equal(PageDragAnswer.AskOwner, PageDragGate.Decide(Opened(print: false, modify: true), ownerRestricted: false));

    [Fact]
    public void AReadOnlyTabAsksForTheOwnerPassword() =>
        Assert.Equal(PageDragAnswer.AskOwner, PageDragGate.Decide(Opened(print: true, modify: false), ownerRestricted: true));

    [Fact]
    public void TheOwnerPasswordForAnOutputLetsThePagesGo()
    {
        OpenedProtection protection = Opened(print: false, modify: true);

        Assert.Equal(ProtectionAnswer.Allowed, protection.AskToWrite(PdfOutput.Printable, _ => Owner));

        Assert.Equal(PageDragAnswer.Allowed, PageDragGate.Decide(protection, ownerRestricted: false));
    }

    [Fact]
    public void AnUnlockedTabLetsThePagesGo() =>
        Assert.Equal(PageDragAnswer.Allowed, PageDragGate.Decide(Opened(print: false, modify: false).AfterUnlock(), ownerRestricted: false));

    [Fact]
    public void AnUnlimitedFileLetsThePagesGo() =>
        Assert.Equal(PageDragAnswer.Allowed, PageDragGate.Decide(OpenedProtection.None, ownerRestricted: false));

    [Fact]
    public void ACertificateRecipientIsRefused() =>
        Assert.Equal(PageDragAnswer.Refused, PageDragGate.Decide(OpenedProtection.ForRecipient(
            PdfAccess.Refused("sealed")), ownerRestricted: true));
}
