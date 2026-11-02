using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using KillerPDF.Services;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Security;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPDF.Tests;

// One function answers what a reader may do with a PDF, for the app and the command line alike, and
// what it answered at open stays with the tab whatever then happens to the file.
public sealed class RestrictionSeamTests : IDisposable
{
    private const string Owner = "owner-password";
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "KillerPDF-restrictions-" + Guid.NewGuid().ToString("N"));

    public RestrictionSeamTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, true);

    private string Protected(string name, string user = "", bool low = true, bool high = true,
        bool modify = true, bool copy = true)
    {
        string path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, new PdfDocumentBuilder()
            .SetPasswordEncryption(new PdfPasswordEncryptionOptions
            {
                UserPassword = user,
                OwnerPassword = Owner,
                AllowLowQualityPrinting = low,
                AllowHighQualityPrinting = high,
                AllowDocumentModification = modify,
                AllowContentCopying = copy
            })
            .AddBlankPage()
            .Build());
        return path;
    }

    private string Plain(string name)
    {
        string path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, new PdfDocumentBuilder().AddBlankPage().Build());
        return path;
    }

    // R2-001: a failed authentication is a refusal, never "not restricted".
    [Fact]
    public void APasswordThatOpensNothingRefusesEverything()
    {
        PdfAccess access = PdfRestrictions.Read(Protected("locked.pdf", user: "reader", modify: false), "wrong");

        Assert.Equal(PdfAccessRole.Refused, access.Role);
        Assert.False(access.MayModify);
        Assert.False(access.MayCopy);
        Assert.Equal(PrintAllowance.Forbidden, access.Print);
    }

    [Fact]
    public void NoFileAndAFileThatCannotBeReadRefuseEverything()
    {
        string garbage = Path.Combine(_folder, "garbage.pdf");
        File.WriteAllText(garbage, "/Encrypt this is not a PDF");

        foreach (string path in new[] { Path.Combine(_folder, "missing.pdf"), garbage })
        {
            PdfAccess access = PdfRestrictions.Read(path, null);
            Assert.Equal(PdfAccessRole.Refused, access.Role);
            Assert.False(access.MayModify);
            Assert.Equal(PrintAllowance.Forbidden, access.Print);
            Assert.False(string.IsNullOrEmpty(access.Refusal));
        }
    }

    // R3-018: a password that opens nothing is refused even where no password would have opened it.
    [Fact]
    public void AWrongPasswordIsRefusedWhereAnEmptyPasswordWouldOpen()
    {
        string path = Protected("empty.pdf", low: true, high: false, modify: false, copy: false);

        PdfAccess wrong = PdfRestrictions.Read(path, "wrong");
        PdfAccess none = PdfRestrictions.Read(path, null);

        Assert.Equal(PdfAccessRole.Refused, wrong.Role);
        Assert.Equal(PdfAccessRole.User, none.Role);
        Assert.False(none.MayModify);
        Assert.False(none.MayCopy);
        Assert.Equal(PrintAllowance.LowQualityOnly, none.Print);
    }

    [Fact]
    public void TheOwnerPasswordLiftsEveryLimitAndAPlainFileHasNone()
    {
        PdfAccess owner = PdfRestrictions.Read(Protected("owner.pdf", user: "reader", low: false, high: false, modify: false, copy: false), Owner);
        PdfAccess plain = PdfRestrictions.Read(Plain("plain.pdf"), null);

        Assert.Equal(PdfAccessRole.Owner, owner.Role);
        Assert.Equal(PdfAccessRole.Unencrypted, plain.Role);
        foreach (PdfAccess access in new[] { owner, plain })
        {
            Assert.True(access.MayModify);
            Assert.True(access.MayCopy);
            Assert.Equal(PrintAllowance.Allowed, access.Print);
            Assert.False(access.DeclaresLimits);
        }
    }

    [Fact]
    public void TheUserRoleReadsTheDeclaredPermissions()
    {
        PdfAccess access = PdfRestrictions.Read(Protected("user.pdf", user: "reader", low: false, high: false), "reader");

        Assert.Equal(PdfAccessRole.User, access.Role);
        Assert.True(access.MayModify);
        Assert.Equal(PrintAllowance.Forbidden, access.Print);
        Assert.True(access.DeclaresLimits);
    }

    private static X509Certificate2 Recipient()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=recipient", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(1));
    }

    private string ForRecipient(X509Certificate2 certificate, bool low, bool high)
    {
        string path = Path.Combine(_folder, $"recipient-{low}-{high}.pdf");
        File.WriteAllBytes(path, PdfDocumentEncryptionWriter.EncryptForRecipients(
            PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage().Build()),
            new PdfCertificateEncryptionOptions
            {
                Recipients = [certificate],
                AllowLowQualityPrinting = low,
                AllowHighQualityPrinting = high,
                AllowDocumentModification = true,
                AllowAnnotationModification = true,
                AllowFormFilling = true,
                AllowDocumentAssembly = true
            }, false));
        return path;
    }

    // R2-005: a recipient has no owner password to ask for, so the permissions it opened with stand.
    [Fact]
    public void ACertificateRecipientKeepsItsPrintLimitAndIsNeverAskedForAPassword()
    {
        using X509Certificate2 certificate = Recipient();
        Assert.True(PdfEngineIntegration.TryRemoveCertificateEncryption(ForRecipient(certificate, false, false),
            Path.Combine(_folder, "recipient.dec.pdf"), [certificate], out OpenedProtection protection));
        Assert.Equal(PdfAccessRole.Recipient, protection.Access.Role);
        Assert.Equal(PrintAllowance.Forbidden, protection.Access.Print);

        int asked = 0;
        PrintAnswer answer = protection.AskToPrint(_ => { asked++; return Owner; });

        Assert.False(answer.MayPrint);
        Assert.Equal(0, asked);
        Assert.False(protection.CanLift);
    }

    [Fact]
    public void ALowQualityRecipientPrintsAtLowQualityWithoutBeingAsked()
    {
        using X509Certificate2 certificate = Recipient();
        Assert.True(PdfEngineIntegration.TryRemoveCertificateEncryption(ForRecipient(certificate, true, false),
            Path.Combine(_folder, "recipient-low.dec.pdf"), [certificate], out OpenedProtection protection));

        int asked = 0;
        PrintAnswer answer = protection.AskToPrint(_ => { asked++; return Owner; });

        Assert.True(answer.MayPrint);
        Assert.Equal(PrintPermission.LowQualityDpi, answer.Dpi);
        Assert.Equal(0, asked);
    }

    // Taking the protection off is what asks for the owner password, and the answer lasts for the tab.
    [Fact]
    public void RemovingAPrintLimitTakesTheOwnerPassword()
    {
        OpenedProtection protection = OpenedProtection.Capture(Protected("no-print.pdf", low: false, high: false), null);

        Assert.Equal(ProtectionAnswer.Canceled, protection.AskToRemove(_ => null));
        Assert.Equal(ProtectionAnswer.Allowed, protection.AskToRemove(_ => Owner));
        Assert.Equal(PrintPermission.FullDpi, protection.AskToPrint(_ => null).Dpi);
    }

    [Fact]
    public void AnyRealLimitAsksBeforeRemovingAndNoLimitDoesNot()
    {
        OpenedProtection limited = OpenedProtection.Capture(Protected("no-copy.pdf", copy: false), null);
        OpenedProtection unlimited = OpenedProtection.Capture(Protected("no-limit.pdf", user: "reader"), "reader");
        var plain = OpenedProtection.Capture(Plain("plain-save.pdf"), null);
        int asked = 0;

        Assert.Equal(ProtectionAnswer.Canceled, limited.AskToRemove(_ => null));
        Assert.Equal(ProtectionAnswer.Allowed, unlimited.AskToRemove(_ => { asked++; return null; }));
        Assert.Equal(ProtectionAnswer.Allowed, plain.AskToRemove(_ => { asked++; return null; }));
        Assert.Equal(0, asked);
    }

    [Fact]
    public void ARecipientWithALimitCannotTakeItOff()
    {
        using X509Certificate2 certificate = Recipient();
        Assert.True(PdfEngineIntegration.TryRemoveCertificateEncryption(ForRecipient(certificate, false, false),
            Path.Combine(_folder, "recipient-save.dec.pdf"), [certificate], out OpenedProtection protection));
        int asked = 0;

        Assert.Equal(ProtectionAnswer.Refused, protection.AskToRemove(_ => { asked++; return Owner; }));
        Assert.Equal(0, asked);
    }

    // R2-007: the low quality limit asks too, and never stands in the way of the print it allows.
    [Fact]
    public void TheOwnerPasswordLiftsALowQualityLimitAndAnyOtherAnswerStillPrintsAtLowQuality()
    {
        string path = Protected("low.pdf", low: true, high: false);

        int asked = 0;
        PrintAnswer lifted = OpenedProtection.Capture(path, null).AskToPrint(_ => { asked++; return Owner; });
        PrintAnswer canceled = OpenedProtection.Capture(path, null).AskToPrint(_ => null);
        PrintAnswer wrong = OpenedProtection.Capture(path, null).AskToPrint(_ => "wrong");

        Assert.Equal(1, asked);
        Assert.Equal((true, PrintPermission.FullDpi), (lifted.MayPrint, lifted.Dpi));
        Assert.Equal((true, PrintPermission.LowQualityDpi, false), (canceled.MayPrint, canceled.Dpi, canceled.WrongPassword));
        Assert.Equal((true, PrintPermission.LowQualityDpi, true), (wrong.MayPrint, wrong.Dpi, wrong.WrongPassword));
    }

    [Fact]
    public void AForbiddenFileStaysForbiddenWithoutTheOwnerPassword()
    {
        string path = Protected("forbidden.pdf", low: false, high: false);

        Assert.False(OpenedProtection.Capture(path, null).AskToPrint(_ => null).MayPrint);
        PrintAnswer wrong = OpenedProtection.Capture(path, null).AskToPrint(_ => "wrong");
        Assert.False(wrong.MayPrint);
        Assert.True(wrong.WrongPassword);
        Assert.True(OpenedProtection.Capture(path, null).AskToPrint(_ => Owner).MayPrint);
    }

    // R2-017: what the file said at open stands, whatever has since happened to it.
    [Fact]
    public void AForbiddenFileStaysForbiddenAfterItIsDeleted()
    {
        string path = Protected("deleted.pdf", low: false, high: false);
        OpenedProtection protection = OpenedProtection.Capture(path, null);

        File.Delete(path);

        Assert.False(protection.AskToPrint(_ => null).MayPrint);
        Assert.True(protection.AskToPrint(_ => Owner).MayPrint);
    }

    [Fact]
    public void AForbiddenFileStaysForbiddenAfterItIsReplaced()
    {
        string path = Protected("replaced.pdf", low: false, high: false);
        OpenedProtection protection = OpenedProtection.Capture(path, null);

        File.WriteAllBytes(path, new PdfDocumentBuilder().AddBlankPage().Build());

        Assert.False(protection.AskToPrint(_ => null).MayPrint);
    }

    [Fact]
    public void ADamagedFileThatNamesNoEncryptionIsNotHeldToALimit()
    {
        string path = Path.Combine(_folder, "damaged.pdf");
        File.WriteAllText(path, "%PDF-1.7 nothing that parses");

        Assert.True(OpenedProtection.Capture(path, null).AskToPrint(_ => null).MayPrint);
    }

    // R2-018: the engine's own check, for the file shapes that keep /Encrypt far from the end.
    [Fact]
    public void EncryptionFarFromTheEndIsFound()
    {
        string path = Protected("padded.pdf", low: false, high: false);
        File.AppendAllText(path, "\n%" + new string('p', 4096) + "\n");

        Assert.True(PdfRestrictions.HasEncryption(path));
    }

    // qpdf's own regression sample: a linearized file whose /Encrypt sits in the first-page trailer.
    [Fact]
    public void ARealLinearizedEncryptedFileIsFound()
    {
        string path = Path.Combine(RepositoryRoot(), "KillerPDF.Tests", "TestData", "linearized-encrypted-qpdf.pdf");
        string text = Encoding.Latin1.GetString(File.ReadAllBytes(path));
        Assert.Contains("/Linearized", text[..2048], StringComparison.Ordinal);
        Assert.DoesNotContain("/Encrypt", text[^2048..], StringComparison.Ordinal);

        Assert.True(PdfRestrictions.HasEncryption(path));
        Assert.NotEqual(PdfAccessRole.Unencrypted, PdfRestrictions.Read(path, null).Role);
    }

    [Fact]
    public void ALargeCrossReferenceStreamFileIsFound()
    {
        var builder = new PdfDocumentBuilder().SetPasswordEncryption(new PdfPasswordEncryptionOptions
        {
            UserPassword = "",
            OwnerPassword = Owner,
            AllowLowQualityPrinting = false,
            AllowHighQualityPrinting = false
        });
        for (int page = 0; page < 1500; page++) builder.AddBlankPage();
        byte[] rewritten = PdfDocumentWriter.Write(PdfDocument.Open(builder.Build(), Owner),
            new PdfDocumentWriteOptions { CrossReferenceFormat = PdfCrossReferenceFormat.Stream });
        string path = Path.Combine(_folder, "xref-stream.pdf");
        File.WriteAllBytes(path, rewritten);
        Assert.DoesNotContain("/Encrypt", Encoding.Latin1.GetString(rewritten)[^2048..], StringComparison.Ordinal);

        Assert.True(PdfRestrictions.HasEncryption(path));
        Assert.Equal(PrintAllowance.Forbidden, PdfRestrictions.Read(path, null).Print);
    }

    [Fact]
    public void APlainFileHasNoEncryptionAndAnUnreadableOneIsNotCalledPlain()
    {
        Assert.False(PdfRestrictions.HasEncryption(Plain("plain-check.pdf")));
        Assert.True(PdfRestrictions.HasEncryption(Path.Combine(_folder, "missing.pdf")));
    }

    private static string RepositoryRoot()
    {
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "KillerPDF.csproj")))
            root = Path.GetDirectoryName(root) ?? throw new DirectoryNotFoundException("KillerPDF.csproj");
        return root;
    }

    // R2-022: --print renders through the session the print preview uses.
    [Fact]
    public void ThePrintSessionShowsAFieldThatOnlyPrints()
    {
        string path = Path.Combine(_folder, "print-only.pdf");
        File.WriteAllBytes(path, new PdfDocumentBuilder().AddBlankPage(100, 100)
            .AddTextField(0, "print.only", 10, 40, 80, 20, "PRINT ONLY", 10,
                new PdfTextFieldOptions { Visibility = PdfFormFieldVisibility.HiddenButPrintable })
            .Build());

        using PdfPageRenderSession session = PrintPermission.OpenRenderSession(path, PrintAllowance.Allowed, null);

        Assert.Contains(session.RenderPage(0).Pixels, value => value < 128);
    }
}
