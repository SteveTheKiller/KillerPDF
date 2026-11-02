using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using KillerPDF.Services;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Security;
using KillerPdf.Engine.Signing;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPDF.Tests;

// A file that allows editing and declares a limit keeps working and keeps its limit. Each test runs
// the steps the tab runs: the same strip, the same working document, the same publish, and then
// reopens the real output.
public sealed class ProtectedDocumentTests : IDisposable
{
    private const string Owner = "owner-password";
    private const string Reader = "reader";
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "KillerPDF-protected-" + Guid.NewGuid().ToString("N"));

    public ProtectedDocumentTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, true);

    private string InFolder(string name) => Path.Combine(_folder, name);

    private string Protected(string name, string user = "", bool print = true, bool lowQualityOnly = false,
        bool modify = true, bool copy = true, bool form = false, string? owner = Owner)
    {
        var builder = new PdfDocumentBuilder().SetPasswordEncryption(new PdfPasswordEncryptionOptions
        {
            UserPassword = user,
            OwnerPassword = owner ?? Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)),
            AllowLowQualityPrinting = print,
            AllowHighQualityPrinting = print && !lowQualityOnly,
            AllowDocumentModification = modify,
            AllowContentCopying = copy,
        }).AddBlankPage(200, 200);
        if (form) builder.AddTextField(0, "name", 10, 40, 80, 20, "old value", 10);
        string path = InFolder(name);
        File.WriteAllBytes(path, builder.Build());
        return path;
    }

    private static PdfEngineIntegration.FormEdits Text(string name, string value) => new(
        new Dictionary<string, string> { [name] = value }, new Dictionary<string, string>(),
        new Dictionary<string, IReadOnlyList<string>>(), new Dictionary<string, bool>(),
        new Dictionary<string, string>(), new Dictionary<string, double>());

    // What the tab holds after opening a protected file: its decrypted working copy.
    private string Strip(string path, string? password)
    {
        string working = InFolder(Path.GetFileNameWithoutExtension(path) + "-working-" + Guid.NewGuid().ToString("N")[..6] + ".pdf");
        if (password is null) Assert.True(PdfEngineIntegration.TryRemoveEncryptionWithoutPassword(path, working));
        else PdfEngineIntegration.RemoveEncryption(path, working, password);
        return working;
    }

    // Save: the working document is written out, the typed value goes in, and the result is published.
    private string SaveEdited(string working, string? field, string target, OpenedProtection protection)
    {
        string saved = InFolder("saved-" + Guid.NewGuid().ToString("N")[..6] + ".pdf");
        using (PdfWorkingDocument document = PdfWorkingDocument.Open(working)) document.Save(saved);
        if (field is not null) PdfEngineIntegration.ApplyFormValues(saved, Text(field, "new value"));
        ProtectedDocumentFiles.Publish(saved, target, protection);
        return target;
    }

    private static string FieldValue(PdfDocument document, string name) =>
        PdfFormWidgetReader.ReadPage(document, 0).Single(widget => widget.FieldName == name).Value;

    // R3-001: the owner's password is not what a save needs.
    [Fact]
    public void SavingAnEditableFileKeepsThePasswordThePermissionsAndTheOwnerPassword()
    {
        string path = Protected("print-limited.pdf", user: Reader, print: false, form: true);
        OpenedProtection protection = OpenedProtection.Capture(path, Reader);
        string working = Strip(path, Reader);
        string target = InFolder("saved.pdf");

        SaveEdited(working, "name", target, protection);

        Assert.True(PdfRestrictions.HasEncryption(target));
        Assert.Equal(PdfAccessRole.Refused, PdfRestrictions.Read(target, null).Role);
        PdfAccess reopened = PdfRestrictions.Read(target, Reader);
        Assert.Equal(PdfAccessRole.User, reopened.Role);
        Assert.Equal(PrintAllowance.Forbidden, reopened.Print);
        Assert.True(reopened.MayModify);
        Assert.Equal(PdfAccessRole.Owner, PdfRestrictions.Read(target, Owner).Role);
        PdfDocument document = PdfDocument.OpenWithCompatibilityRecovery(File.ReadAllBytes(target), Reader);
        Assert.Equal("new value", FieldValue(document, "name"));
    }

    [Fact]
    public void ASecondSaveKeepsItToo()
    {
        string path = Protected("twice.pdf", print: false, form: true);
        OpenedProtection protection = OpenedProtection.Capture(path, null);

        SaveEdited(Strip(path, null), "name", path, protection);
        SaveEdited(Strip(path, null), "name", path, protection);

        Assert.Equal(PrintAllowance.Forbidden, PdfRestrictions.Read(path, null).Print);
        Assert.Equal(PdfAccessRole.Owner, PdfRestrictions.Read(path, Owner).Role);
    }

    // The print limit survives even a save made after the owner password lifted it for this tab.
    [Fact]
    public void LiftingALimitForOnePrintDoesNotTakeItOutOfTheFile()
    {
        string path = Protected("lifted.pdf", print: false);
        OpenedProtection protection = OpenedProtection.Capture(path, null);
        Assert.True(protection.AskToPrint(_ => Owner).MayPrint);

        SaveEdited(Strip(path, null), null, path, protection);

        Assert.Equal(PrintAllowance.Forbidden, PdfRestrictions.Read(path, null).Print);
    }

    // R3-002: the stricter file saved its values; the more permissive one now does too.
    [Fact]
    public void FormValuesSaveIntoAPrintLimitedFormThatAllowsEditing()
    {
        string path = Protected("form.pdf", print: false, form: true);
        OpenedProtection protection = OpenedProtection.Capture(path, null);

        SaveEdited(Strip(path, null), "name", InFolder("form-saved.pdf"), protection);

        PdfDocument document = PdfDocument.OpenWithCompatibilityRecovery(File.ReadAllBytes(InFolder("form-saved.pdf")), "");
        Assert.True(document.IsEncrypted);
        Assert.Equal("new value", FieldValue(document, "name"));
    }

    // R3-003: a blank permissions password leaves a random owner password nobody was shown.
    [Fact]
    public void AFileWhoseOwnerPasswordNobodyKnowsStillSaves()
    {
        string path = Protected("no-owner.pdf", user: Reader, print: false, owner: null);
        OpenedProtection protection = OpenedProtection.Capture(path, Reader);

        SaveEdited(Strip(path, Reader), null, path, protection);

        PdfAccess reopened = PdfRestrictions.Read(path, Reader);
        Assert.Equal(PdfAccessRole.User, reopened.Role);
        Assert.Equal(PrintAllowance.Forbidden, reopened.Print);
    }

    [Fact]
    public void ACertificateRecipientAllowedToEditSavesForTheSameRecipients()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=recipient", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using X509Certificate2 certificate = request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(1));
        string path = InFolder("recipient.pdf");
        File.WriteAllBytes(path, PdfDocumentEncryptionWriter.EncryptForRecipients(
            PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage().Build()),
            new PdfCertificateEncryptionOptions
            {
                Recipients = [certificate],
                AllowLowQualityPrinting = false,
                AllowHighQualityPrinting = false,
                AllowDocumentModification = true,
                AllowAnnotationModification = true,
                AllowContentCopying = true,
                AllowFormFilling = true,
                AllowDocumentAssembly = true
            }));
        string working = InFolder("recipient-working.pdf");
        Assert.True(PdfEngineIntegration.TryRemoveCertificateEncryption(path, working, [certificate], out OpenedProtection protection));
        Assert.True(protection.KeepsEncryption);

        SaveEdited(working, null, path, protection);

        Assert.True(PdfEngineIntegration.IsCertificateEncrypted(path));
        PdfDocument reopened = PdfDocument.OpenWithCompatibilityRecovery(File.ReadAllBytes(path), certificate);
        Assert.False(reopened.DeclaredPermissions!.AllowLowQualityPrinting);
    }

    // A password is protection whether or not the file limits anything, and Save is not Remove Password.
    [Fact]
    public void AFileThatLimitsNothingKeepsItsPasswordToo()
    {
        string path = Protected("no-limits.pdf", user: Reader);
        OpenedProtection protection = OpenedProtection.Capture(path, Reader);

        Assert.True(ProtectedDocumentFiles.Publish(Strip(path, Reader), InFolder("no-limits-saved.pdf"), protection));

        Assert.Equal(PdfAccessRole.Refused, PdfRestrictions.Read(InFolder("no-limits-saved.pdf"), null).Role);
        Assert.Equal(PdfAccessRole.User, PdfRestrictions.Read(InFolder("no-limits-saved.pdf"), Reader).Role);
    }

    // Remove Password publishes with no protection, and on this file its own password is enough.
    [Fact]
    public void RemovingThePasswordOfAFileThatLimitsNothingNeedsNoOwner()
    {
        string path = Protected("remove.pdf", user: Reader);
        OpenedProtection protection = OpenedProtection.Capture(path, Reader);
        int asked = 0;

        Assert.Equal(ProtectionAnswer.Allowed, protection.AskToRemove(_ => { asked++; return null; }));
        Assert.False(ProtectedDocumentFiles.Publish(Strip(path, Reader), InFolder("removed.pdf"), OpenedProtection.None));

        Assert.Equal(0, asked);
        Assert.False(PdfRestrictions.HasEncryption(InFolder("removed.pdf")));
    }

    // Reduce Size is a save in a smaller form, so its output keeps the protection.
    [Fact]
    public void AReducedCopyKeepsTheProtection()
    {
        string path = Protected("reduce.pdf", user: Reader, print: false);
        OpenedProtection protection = OpenedProtection.Capture(path, Reader);
        string output = InFolder("reduced.pdf");

        var reduced = ProtectedDocumentFiles.ReduceSize(Strip(path, Reader), output, InFolder("reduced-working.pdf"), null,
            CancellationToken.None, protection);

        Assert.Equal(PdfAccessRole.Refused, PdfRestrictions.Read(output, null).Role);
        Assert.Equal(PrintAllowance.Forbidden, PdfRestrictions.Read(output, Reader).Print);
        // R4-005: the size reported is the size of the file delivered, after the encryption.
        Assert.Equal(new FileInfo(output).Length, reduced.After);
    }

    // Modify bit clear: a read-only tab, and the owner password unlocks it. The encryption is held all
    // the same, so whatever the owner saves goes back under it.
    [Fact]
    public void AFileThatForbidsEditingIsReadOnlyAndStillKeepsItsEncryption()
    {
        OpenedProtection protection = OpenedProtection.Capture(Protected("read-only.pdf", modify: false), null);

        Assert.False(protection.Access.MayModify);
        Assert.True(protection.KeepsEncryption);
    }

    // R4-001: the owner password opens a file that forbids editing, and the save keeps its user
    // password, its owner password and every limit it declares.
    [Fact]
    public void AFileOpenedWithItsOwnerPasswordSavesUnderTheSamePasswordsAndLimits()
    {
        string path = Protected("owner-opened.pdf", user: Reader, modify: false, copy: false, form: true);
        OpenedProtection protection = OpenedProtection.Capture(path, Owner);
        Assert.True(protection.Access.MayModify);
        string target = InFolder("owner-opened-saved.pdf");

        SaveEdited(Strip(path, Owner), "name", target, protection);

        AssertSameProtection(target, Reader, copy: false, print: true);
        PdfDocument document = PdfDocument.OpenWithCompatibilityRecovery(File.ReadAllBytes(target), Reader);
        Assert.Equal("new value", FieldValue(document, "name"));
    }

    // R4-001: Enter Password with the owner password unlocks the tab, and the save after it writes the
    // file back with the limit the unlock lifted for this tab only. Reduce Size does the same.
    [Fact]
    public void AnUnlockedFileThatForbidsEditingSavesAndReducesUnderItsOwnLimits()
    {
        string path = Protected("unlocked.pdf", modify: false, print: false);
        OpenedProtection opened = OpenedProtection.Capture(path, null);
        string unlocked = InFolder("unlocked-working.pdf");
        Assert.Equal(UnlockOutcome.Unlocked, opened.Unlock(path, unlocked, Owner).Outcome);
        OpenedProtection protection = opened.AfterUnlock();
        string reduced = InFolder("unlocked-reduced.pdf");

        ProtectedDocumentFiles.ReduceSize(unlocked, reduced, InFolder("unlocked-reduced-working.pdf"), null,
            CancellationToken.None, protection);
        SaveEdited(unlocked, null, path, protection);

        foreach (string output in new[] { path, reduced })
            AssertSameProtection(output, "", copy: true, print: false);
    }

    // Reads a saved file back: the user password opens it as a user under the same limits, nothing
    // else does, and the owner password is still the owner's.
    private static void AssertSameProtection(string path, string user, bool copy, bool print)
    {
        Assert.True(PdfRestrictions.HasEncryption(path));
        if (user.Length > 0) Assert.Equal(PdfAccessRole.Refused, PdfRestrictions.Read(path, null).Role);
        PdfAccess reopened = PdfRestrictions.Read(path, user);
        Assert.Equal(PdfAccessRole.User, reopened.Role);
        Assert.False(reopened.MayModify);
        Assert.Equal(copy, reopened.MayCopy);
        Assert.Equal(print ? PrintAllowance.Allowed : PrintAllowance.Forbidden, reopened.Print);
        Assert.Equal(PdfAccessRole.Owner, PdfRestrictions.Read(path, Owner).Role);
    }

    // R3-005: nothing has changed in a file just opened.
    [Fact]
    public void AnUntouchedProtectedFileOpensClean()
    {
        ProtectedOpen opened = ProtectedDocumentFiles.PrepareWorkingCopy(Protected("clean.pdf", print: false),
            InFolder("clean-working.pdf"), (_, _) => false);

        Assert.True(opened.Stripped);
        Assert.False(opened.StartsDirty);
        Assert.True(opened.Protection.KeepsEncryption);
    }

    // A page rebuild is not the file: it can drop bookmarks and forms, so it starts as unsaved work.
    [Fact]
    public void ARebuiltCopyStartsUnsaved()
    {
        string damaged = InFolder("not-really.pdf");
        File.WriteAllText(damaged, "%PDF-1.7 /Encrypt nothing that parses");
        string working = InFolder("rebuilt.pdf");

        ProtectedOpen opened = ProtectedDocumentFiles.PrepareWorkingCopy(damaged, working, (_, _) => true);

        Assert.True(opened.Stripped);
        Assert.True(opened.StartsDirty);
    }

    // R3-009: recovery follows the same line as a save.
    [Fact]
    public void RecoveryWritesAProtectedDocumentUnderItsEncryption()
    {
        string path = Protected("recover.pdf", user: Reader, print: false);
        OpenedProtection protection = OpenedProtection.Capture(path, Reader);
        string copy = InFolder("recovered.pdf");

        RecoveryRoute route = RenderFailureRecovery.Route(readOnly: false, protectedSource: true, protection);
        RenderFailureRecovery.WriteCopy(Strip(path, Reader), copy, route, protection);

        Assert.Equal(RecoveryRoute.UnderItsEncryption, route);
        Assert.Equal(PdfAccessRole.Refused, PdfRestrictions.Read(copy, null).Role);
        Assert.Equal(PrintAllowance.Forbidden, PdfRestrictions.Read(copy, Reader).Print);
    }

    [Fact]
    public void RecoveryNeverWritesADocumentItCannotEncrypt()
    {
        var unreadable = OpenedProtection.Capture(InFolder("gone.pdf"), null);

        Assert.Equal(RecoveryRoute.NotWritten, RenderFailureRecovery.Route(false, true, unreadable));
        Assert.Equal(RecoveryRoute.NotWritten, RenderFailureRecovery.Route(true, true, OpenedProtection.None));
        Assert.Equal(RecoveryRoute.Plain, RenderFailureRecovery.Route(false, false, OpenedProtection.None));
    }

    [Fact]
    public void ARecoveredTabGoesHomeOnlyWithItsProtection()
    {
        string path = Protected("home.pdf", print: false);
        var record = new RecoveredDocument(path, InFolder("copy.pdf"), null, Protected: true);

        Assert.False(RenderFailureRecovery.MayReturnHome(record, OpenedProtection.None));
        Assert.True(RenderFailureRecovery.MayReturnHome(record, OpenedProtection.Capture(path, null)));
        Assert.True(RenderFailureRecovery.MayReturnHome(record with { Protected = false }, OpenedProtection.None));
    }

    [Fact]
    public void TheManifestRemembersWhichTabsWereProtected()
    {
        string copy = InFolder("listed.pdf");
        File.WriteAllBytes(copy, [1]);

        string manifest = RenderFailureRecovery.WriteManifest(_folder,
            [new RecoveredDocument(InFolder("original.pdf"), copy, null, Protected: true)]);

        Assert.True(RenderFailureRecovery.ReadManifest(manifest).Single().Protected);
    }

    // R3-010: the password that opened the form is not held, the file key is, and that is enough.
    [Fact]
    public void AFormOpenedWithAUserPasswordSavesItsAllowedFilling()
    {
        string path = Protected("password-form.pdf", user: Reader, modify: false, form: true);
        OpenedProtection protection = OpenedProtection.Capture(path, Reader);
        Assert.True(protection.AllowsRestrictedFill);
        string filled = InFolder("password-form-filled.pdf");

        Exception? failure = Record.Exception(() =>
            PdfEngineIntegration.FillRestrictedForm(path, filled, Text("name", "new value"), protection));

        Assert.Null(failure);
        Assert.Equal(PdfAccessRole.Refused, PdfRestrictions.Read(filled, null).Role);
        Assert.Equal(PdfAccessRole.User, PdfRestrictions.Read(filled, Reader).Role);
        PdfDocument document = PdfDocument.OpenWithCompatibilityRecovery(File.ReadAllBytes(filled), Reader);
        Assert.Equal("new value", FieldValue(document, "name"));
    }

    [Fact]
    public void AViewingCopyComesBackFromTheHeldEncryption()
    {
        string path = Protected("viewing.pdf", user: Reader, modify: false);
        OpenedProtection protection = OpenedProtection.Capture(path, Reader);
        string copy = InFolder("viewing-copy.pdf");

        protection.WriteViewingCopy(path, copy);

        Assert.False(PdfRestrictions.HasEncryption(copy));
    }

    // R3-016: one failed read must not last the life of the tab.
    [Fact]
    public void AProtectionThatCouldNotBeReadIsReadAgain()
    {
        string path = Protected("locked.pdf", print: false);
        OpenedProtection protection;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            protection = OpenedProtection.Capture(path, null);
            Assert.True(protection.IsUnreadable);
            Assert.False(protection.KeepsEncryption);
        }

        PrintAnswer answer = protection.AskToPrint(_ => Owner);

        Assert.False(protection.IsUnreadable);
        Assert.True(answer.MayPrint);
        Assert.True(protection.KeepsEncryption);
    }

    [Fact]
    public void AReplacedFileDoesNotAnswerForTheOneThatCouldNotBeRead()
    {
        string path = Protected("replaced.pdf", print: false);
        OpenedProtection protection;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            protection = OpenedProtection.Capture(path, null);
        File.WriteAllBytes(path, new PdfDocumentBuilder().AddBlankPage().Build());

        Assert.False(protection.AskToPrint(_ => null).MayPrint);
        Assert.True(protection.IsUnreadable);
    }

    // R3-023: a cancel, a wrong password and no way to answer are three different ends.
    [Fact]
    public void ACancelIsNotARefusal()
    {
        OpenedProtection protection = OpenedProtection.Capture(Protected("cancel.pdf", print: false), null);

        Assert.Equal(ProtectionAnswer.Canceled, protection.AskToWrite(PdfOutput.Printable, _ => null));
    }

    [Fact]
    public void AWrongPasswordIsToldSoAndAskedAgain()
    {
        OpenedProtection protection = OpenedProtection.Capture(Protected("retry.pdf", print: false), null);
        var told = new List<bool>();
        var answers = new Queue<string?>(["wrong", Owner]);

        ProtectionAnswer answer = protection.AskToWrite(PdfOutput.Printable, wrong => { told.Add(wrong); return answers.Dequeue(); });

        Assert.Equal(ProtectionAnswer.Allowed, answer);
        Assert.Equal([false, true], told);
    }

    // R3-004, R3-008, R3-022: what each output answers to.
    [Theory]
    [InlineData(true, false, true, true, false)]    // limited printing: no new PDF or image
    [InlineData(true, false, true, false, true)]    // text follows the copy bit
    [InlineData(true, true, false, true, false)]    // copying forbidden: pages stay in, as on import
    [InlineData(true, true, false, false, false)]
    [InlineData(false, true, true, true, false)]    // editing forbidden: all content gated
    [InlineData(false, true, true, false, false)]
    [InlineData(true, true, true, true, true)]
    [InlineData(true, true, true, false, true)]
    public void AnOutputAnswersToThePermissionThatNamesIt(bool modify, bool print, bool copy, bool printable, bool allowed)
    {
        PdfAccess access = PdfRestrictions.Read(Protected($"o-{Guid.NewGuid():N}.pdf", print: print, modify: modify, copy: copy), null);

        Assert.Equal(allowed, access.Allows(printable ? PdfOutput.Printable : PdfOutput.Data));
    }

    [Fact]
    public void LowQualityPrintingIsALimitToo()
    {
        PdfAccess access = PdfRestrictions.Read(Protected("low.pdf", lowQualityOnly: true), null);

        Assert.False(access.Allows(PdfOutput.Printable));
        Assert.True(access.Allows(PdfOutput.Data));
    }

    [Fact]
    public void TheOwnerPasswordAndAPlainFileAllowEveryOutput()
    {
        foreach (PdfAccess access in new[]
        {
            PdfRestrictions.Read(Protected("owner.pdf", print: false, modify: false, copy: false), Owner),
            PdfAccess.Unrestricted
        })
        {
            Assert.True(access.Allows(PdfOutput.Printable));
            Assert.True(access.Allows(PdfOutput.Data));
        }
    }

    // R3-004: a merge opens its sources with no password, so it takes no pages from any encrypted file.
    [Fact]
    public void MergeTakesNoPagesFromAFileThatLimitsPrinting()
    {
        string limited = Protected("merge-limited.pdf", print: false);
        string plain = InFolder("merge-plain.pdf");
        File.WriteAllBytes(plain, new PdfDocumentBuilder().AddBlankPage().Build());

        Assert.IsType<InvalidOperationException>(Record.Exception(() => PdfEngineIntegration.MergeFiles([plain, limited])));
        Assert.IsType<InvalidOperationException>(Record.Exception(() => PdfEngineIntegration.MergeFiles([limited, plain])));
    }

    // R3-025: a right password is never reported as a wrong one.
    [Fact]
    public void AWrongOwnerPasswordIsReportedAsWrong()
    {
        string path = Protected("unlock.pdf", modify: false);
        OpenedProtection protection = OpenedProtection.Capture(path, null);

        Assert.Equal(UnlockOutcome.WrongPassword, protection.Unlock(path, InFolder("unlock-out.pdf"), "wrong").Outcome);
        Assert.Equal(UnlockOutcome.Unlocked, protection.Unlock(path, InFolder("unlock-out.pdf"), Owner).Outcome);
    }

    [Fact]
    public void AnOwnerPasswordIsRightEvenWhenTheFileHasGone()
    {
        string path = Protected("gone-after-open.pdf", modify: false);
        OpenedProtection protection = OpenedProtection.Capture(path, null);
        File.Delete(path);

        UnlockAnswer answer = protection.Unlock(path, InFolder("gone-out.pdf"), Owner);

        Assert.Equal(UnlockOutcome.Failed, answer.Outcome);
        Assert.False(string.IsNullOrEmpty(answer.Reason));
    }

    // R3-025: the writer refuses a signed file, which said nothing about the password.
    [Fact]
    public void AnOwnerPasswordIsRightOnASignedRestrictedFile()
    {
        string path = InFolder("signed-restricted.pdf");
        File.WriteAllBytes(path, new PdfDocumentBuilder()
            .SetPasswordEncryption(new PdfPasswordEncryptionOptions { UserPassword = "", OwnerPassword = Owner, AllowDocumentModification = false })
            .AddBlankPage(200, 200)
            .Build());
        string signed = InFolder("signed.pdf");
        File.WriteAllBytes(signed, PdfDetachedSignatureWriter.Sign(PdfDocument.Open(File.ReadAllBytes(path), Owner),
            _ => new byte[64], new PdfSignatureOptions { FieldName = "Signature1" }));
        Assert.Contains(PdfSignatureReader.Read(PdfDocument.Open(File.ReadAllBytes(signed), Owner)), signature => signature.IsSigned);
        OpenedProtection protection = OpenedProtection.Capture(signed, null);

        UnlockAnswer answer = protection.Unlock(signed, InFolder("signed-out.pdf"), Owner);

        Assert.Equal(UnlockOutcome.Failed, answer.Outcome);
    }

    // A signed PDF whose user password is not empty, as an author would send it.
    private string SignedWithUserPassword(string name)
    {
        string path = InFolder(name + "-unsigned.pdf");
        File.WriteAllBytes(path, new PdfDocumentBuilder()
            .SetPasswordEncryption(new PdfPasswordEncryptionOptions { UserPassword = Reader, OwnerPassword = Owner, AllowDocumentModification = false })
            .AddPage(200, 200, "q 0 0 50 50 re f Q"u8.ToArray())
            .Build());
        string signed = InFolder(name + ".pdf");
        File.WriteAllBytes(signed, PdfDetachedSignatureWriter.Sign(PdfDocument.Open(File.ReadAllBytes(path), Owner),
            _ => new byte[64], new PdfSignatureOptions { FieldName = "Signature1" }));
        return signed;
    }

    // R4-006: the writer cannot strip a signed file without clearing its signatures, so a signed PDF
    // that needs a password opens read-only under the key its password produced, by either password.
    [Theory]
    [InlineData(Reader)]
    [InlineData(Owner)]
    public void ASignedFileThatNeedsAPasswordOpensReadOnlyUnderItsKey(string password)
    {
        string signed = SignedWithUserPassword("signed-" + password);
        byte[] before = File.ReadAllBytes(signed);
        Assert.NotNull(Record.Exception(() => PdfEngineIntegration.RemoveEncryption(signed, InFolder("stripped.pdf"), password)));
        Assert.True(PdfEngineIntegration.HasSignedSignatures(signed, password));
        OpenedProtection protection = OpenedProtection.Capture(signed, password);

        protection.HoldForReading();
        PdfWorkingDocument? opened = null;
        Exception? failure = Record.Exception(() => opened = PdfWorkingDocument.Open(signed, isReadOnly: true));

        Assert.Null(failure);
        using (opened)
        {
            Assert.Equal(1, opened!.PageCount);
            Assert.Contains(PdfSignatureReader.Read(opened.ParsedDocument!), signature => signature.IsSigned);
        }
        // A reader that comes to the file on its own, as the text and render readers do, reads it too.
        string elsewhere = InFolder("signed-copy-" + password + ".pdf");
        File.Copy(signed, elsewhere);
        Assert.True(PdfEngineIntegration.OpenForReading(File.ReadAllBytes(elsewhere)).IsDecrypted);
        Assert.True(PdfPageRenderSession.OpenDocument(elsewhere).CanReadPageContent);
        Assert.Equal(before, File.ReadAllBytes(signed));
        GC.KeepAlive(protection);   // the key lasts as long as the tab that holds it
    }

    // R3-025 still holds there: the owner password is right, and the unlock fails with its own reason.
    [Fact]
    public void AnOwnerUnlockOfASignedPasswordFileFailsWithItsOwnReason()
    {
        string signed = SignedWithUserPassword("signed-unlock");
        OpenedProtection protection = OpenedProtection.Capture(signed, Reader);

        UnlockAnswer answer = protection.Unlock(signed, InFolder("signed-unlock-out.pdf"), Owner);

        Assert.Equal(UnlockOutcome.Failed, answer.Outcome);
        Assert.False(string.IsNullOrEmpty(answer.Reason));
    }

    // R3-011: the scan decides, and a plain file is not parsed or read into memory to answer.
    [Fact]
    public void APlainFileAnswersWithoutLoadingIt()
    {
        string path = InFolder("plain-large.pdf");
        File.WriteAllBytes(path, new PdfDocumentBuilder().AddBlankPage().Build());
        using (var pad = new FileStream(path, FileMode.Append)) pad.Write(new byte[32 * 1024 * 1024]);

        long used = AllocatedOnThisThread(() => Assert.Equal(PdfEncryptionState.Plain, PdfRestrictions.ReadEncryptionState(path)));

        Assert.True(used < 4 * 1024 * 1024, $"The answer allocated {used:N0} bytes.");
    }

    // R3-011, R3-024: a file that cannot be read is neither plain nor encrypted.
    [Fact]
    public void ALockedFileIsUnreadableNotEncrypted()
    {
        string path = InFolder("locked-plain.pdf");
        File.WriteAllBytes(path, new PdfDocumentBuilder().AddBlankPage().Build());

        using var hold = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

        Assert.Equal(PdfEncryptionState.Unreadable, PdfRestrictions.ReadEncryptionState(path));
    }

    private static long AllocatedOnThisThread(Action action)
    {
        long start = GC.GetAllocatedBytesForCurrentThread();
        action();
        return GC.GetAllocatedBytesForCurrentThread() - start;
    }
}

[CollectionDefinition("Measures the managed heap", DisableParallelization = true)]
public sealed class ManagedHeapCollection;

// R3-012: holding a file's encryption costs nothing like holding the file. Alone in its collection,
// because a heap measurement cannot share the process with other tests.
[Collection("Measures the managed heap")]
public sealed class OpenedProtectionMemoryTests
{
    [Fact]
    public void ATabHoldsNoCopyOfTheFile()
    {
        const int Size = 48 * 1024 * 1024;
        string path = Path.Combine(Path.GetTempPath(), "KillerPDF-held-" + Guid.NewGuid().ToString("N") + ".pdf");
        try
        {
            File.WriteAllBytes(path, new PdfDocumentBuilder()
                .SetPasswordEncryption(new PdfPasswordEncryptionOptions
                {
                    UserPassword = "",
                    OwnerPassword = "owner-password",
                    AllowLowQualityPrinting = false,
                    AllowHighQualityPrinting = false
                })
                .AddBlankPage()
                .AddAttachment("noise.bin", RandomNumberGenerator.GetBytes(Size))
                .Build());
            Assert.True(new FileInfo(path).Length > Size);
            long before = GC.GetTotalMemory(forceFullCollection: true);

            OpenedProtection held = OpenedProtection.Capture(path, null);

            long growth = GC.GetTotalMemory(forceFullCollection: true) - before;
            File.Delete(path);
            Assert.True(held.AskToPrint(_ => "owner-password").MayPrint);
            Assert.True(growth < Size / 4, $"The tab held {growth:N0} bytes.");
        }
        finally { File.Delete(path); }
    }
}
