using System.IO;
using KillerPDF.Services;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Diagnostics;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Security;
using KillerPdf.Engine.Signing;
using Xunit;

namespace KillerPDF.Tests;

// R6-002: Document Tools > Accessibility reviews a tab that reads its encrypted original. The snapshot
// keeps the typed form values and the review opens it under the tab's protection.
public sealed class AccessibilityReviewSourceTests : IDisposable
{
    private const string Owner = "owner-password";
    private const string Reader = "reader";
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "KillerPDF-a11yreview-" + Guid.NewGuid().ToString("N"));

    public AccessibilityReviewSourceTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, true);

    private static byte[] Form(string user) => new PdfDocumentBuilder()
        .SetPasswordEncryption(new PdfPasswordEncryptionOptions
        {
            UserPassword = user,
            OwnerPassword = Owner,
            AllowDocumentModification = false,
            AllowFormFilling = true,
        })
        .AddPage(300, 400, new PdfContentStreamBuilder()
            .BeginText().SetFont(PdfStandardFont.Helvetica, 12)
            .MoveText(30, 350).ShowLatin1Text("Application").EndText())
        .AddTextField(0, "name", 30, 200, 160, 24, "", 10)
        .Build();

    public static TheoryData<bool> Tabs => new() { false, true };

    // An owner-only form that forbids editing and allows filling, or the same form signed and behind a
    // user password. Each is reopened to prove its passwords and permissions.
    private (string Path, OpenedProtection Protection) Tab(bool signed)
    {
        string user = signed ? Reader : "";
        byte[] bytes = Form(user);
        if (signed)
            bytes = PdfDetachedSignatureWriter.Sign(PdfDocument.Open(bytes, Owner), _ => new byte[64],
                new PdfSignatureOptions { FieldName = "Signature1" });
        string path = Path.Combine(_folder, signed ? "signed.pdf" : "owner-only.pdf");
        File.WriteAllBytes(path, bytes);

        Assert.Equal(PdfPasswordAuthenticationRole.Owner, PdfDocument.Open(bytes, Owner).PasswordAuthenticationRole);
        PdfDocument asUser = PdfDocument.Open(bytes, user);
        Assert.Equal(PdfPasswordAuthenticationRole.User, asUser.PasswordAuthenticationRole);
        Assert.False(asUser.DeclaredPermissions!.AllowDocumentModification);
        Assert.True(asUser.DeclaredPermissions!.AllowFormFilling);
        Assert.Equal(signed, PdfSignatureReader.Read(asUser).Any(signature => signature.IsSigned));

        OpenedProtection protection = OpenedProtection.Capture(path, signed ? Reader : null);
        Assert.Equal(PdfAccessRole.User, protection.Access.Role);
        Assert.True(protection.AllowsRestrictedFill);
        return (path, protection);
    }

    private static PdfEngineIntegration.FormEdits Typed(string name, string value) => new(
        new Dictionary<string, string> { [name] = value }, new Dictionary<string, string>(),
        new Dictionary<string, IReadOnlyList<string>>(), new Dictionary<string, bool>(),
        new Dictionary<string, string>(), new Dictionary<string, double>());

    [Theory]
    [MemberData(nameof(Tabs))]
    public void TheReviewReadsATabThatReadsItsEncryptedOriginal(bool signed)
    {
        (string path, OpenedProtection protection) = Tab(signed);
        using PdfWorkingDocument document = ReadOnlyOriginal.Open(path, protection).Document;

        byte[]? snapshot = null;
        PdfAccessibilityReport? report = null;
        PdfDocument? opened = null;
        Exception? failure = Record.Exception(() =>
        {
            snapshot = AccessibilityReviewSource.Snapshot(document, Typed("name", "Ada"), protection,
                Path.Combine(_folder, "exchange.tmp"));
            report = AccessibilityReviewSource.Inspect(snapshot, protection);
            opened = AccessibilityReviewSource.Open(snapshot, protection);
        });

        Assert.Null(failure);
        Assert.Contains(report!.Findings, finding => finding.Code == PdfAccessibilityFindingCode.MissingDocumentLanguage);
        Assert.True(opened!.IsDecrypted);
        Assert.Equal("Ada", Assert.Single(PdfFormWidgetReader.ReadPage(opened, 0), widget => widget.FieldName == "name").Value);
        Assert.False(File.Exists(Path.Combine(_folder, "exchange.tmp")));
    }

    [Theory]
    [MemberData(nameof(Tabs))]
    public void TheReviewInspectsAnUntypedTabThatReadsItsEncryptedOriginal(bool signed)
    {
        (string path, OpenedProtection protection) = Tab(signed);
        using PdfWorkingDocument document = ReadOnlyOriginal.Open(path, protection).Document;
        byte[] snapshot = AccessibilityReviewSource.Snapshot(document, NothingTyped(), protection,
            Path.Combine(_folder, "exchange.tmp"));

        PdfAccessibilityReport? report = null;
        Exception? failure = Record.Exception(() => report = AccessibilityReviewSource.Inspect(snapshot, protection));

        Assert.Null(failure);
        Assert.Contains(report!.Findings, finding => finding.Code == PdfAccessibilityFindingCode.MissingDocumentLanguage);
    }

    private static PdfEngineIntegration.FormEdits NothingTyped() => new(
        new Dictionary<string, string>(), new Dictionary<string, string>(),
        new Dictionary<string, IReadOnlyList<string>>(), new Dictionary<string, bool>(),
        new Dictionary<string, string>(), new Dictionary<string, double>());

    [Fact]
    public void APlainTabIsReviewedAsBefore()
    {
        string path = Path.Combine(_folder, "plain.pdf");
        File.WriteAllBytes(path, new PdfDocumentBuilder().AddBlankPage().Build());
        using PdfWorkingDocument document = PdfWorkingDocument.Open(path, OpenedProtection.None);

        byte[] snapshot = AccessibilityReviewSource.Snapshot(document, NothingTyped(), OpenedProtection.None,
            Path.Combine(_folder, "exchange.tmp"));

        Assert.Equal(File.ReadAllBytes(path), snapshot);
        Assert.Contains(AccessibilityReviewSource.Inspect(snapshot, OpenedProtection.None).Findings,
            finding => finding.Code == PdfAccessibilityFindingCode.MissingDocumentLanguage);
    }
}
