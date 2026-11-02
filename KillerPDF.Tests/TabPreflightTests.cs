using System.IO;
using KillerPDF.Services;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Diagnostics;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Security;
using KillerPdf.Engine.Signing;
using Xunit;

namespace KillerPDF.Tests;

// R6-003: preflight reads a tab that reads its encrypted original under the tab's protection, so it
// checks the document instead of reporting that an open, readable document needs a password.
public sealed class TabPreflightTests : IDisposable
{
    private const string Owner = "owner-password";
    private const string Reader = "reader";
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "KillerPDF-preflight-" + Guid.NewGuid().ToString("N"));

    public TabPreflightTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, true);

    public static TheoryData<bool> Tabs => new() { false, true };

    // An owner-only file that forbids editing, or a signed file behind a user password, reopened to
    // prove its passwords and permissions.
    private (string Path, OpenedProtection Protection) Tab(bool signed)
    {
        string user = signed ? Reader : "";
        byte[] bytes = new PdfDocumentBuilder()
            .SetPasswordEncryption(new PdfPasswordEncryptionOptions
            {
                UserPassword = user,
                OwnerPassword = Owner,
                AllowDocumentModification = false,
            })
            .AddPage(300, 400, new PdfContentStreamBuilder()
                .BeginText().SetFont(PdfStandardFont.Helvetica, 12)
                .MoveText(30, 350).ShowLatin1Text("Proof").EndText())
            .Build();
        if (signed)
            bytes = PdfDetachedSignatureWriter.Sign(PdfDocument.Open(bytes, Owner), _ => new byte[64],
                new PdfSignatureOptions { FieldName = "Signature1" });
        string path = Path.Combine(_folder, signed ? "signed.pdf" : "owner-only.pdf");
        File.WriteAllBytes(path, bytes);

        Assert.Equal(PdfPasswordAuthenticationRole.Owner, PdfDocument.Open(bytes, Owner).PasswordAuthenticationRole);
        PdfDocument asUser = PdfDocument.Open(bytes, user);
        Assert.Equal(PdfPasswordAuthenticationRole.User, asUser.PasswordAuthenticationRole);
        Assert.False(asUser.DeclaredPermissions!.AllowDocumentModification);
        Assert.True(asUser.DeclaredPermissions!.AllowHighQualityPrinting);
        Assert.Equal(signed, PdfSignatureReader.Read(asUser).Any(signature => signature.IsSigned));

        OpenedProtection protection = OpenedProtection.Capture(path, signed ? Reader : null);
        Assert.Equal(PdfAccessRole.User, protection.Access.Role);
        return (path, protection);
    }

    [Theory]
    [MemberData(nameof(Tabs))]
    public void PreflightChecksATabThatReadsItsEncryptedOriginal(bool signed)
    {
        (string path, OpenedProtection protection) = Tab(signed);

        PdfPreflightReport report = PdfEngineIntegration.InspectPreflight(path, protection);

        Assert.DoesNotContain(report.Findings, finding => finding.Code == "DocumentChecksUnavailable");
        Assert.DoesNotContain(report.Findings, finding => finding.Code.Contains("AuthenticationFailed", StringComparison.Ordinal));
        Assert.Contains(report.Findings, finding => finding.Code == "OutputIntent.Missing");
    }

    [Theory]
    [MemberData(nameof(Tabs))]
    public void APreflightProfileFileChecksATabThatReadsItsEncryptedOriginal(bool signed)
    {
        (string path, OpenedProtection protection) = Tab(signed);
        var profile = new PdfPreflightProfile("Box check", [PdfPreflightCheck.StructuralIntegrity, PdfPreflightCheck.OutputIntent]);

        PdfPreflightReport report = PdfEngineIntegration.InspectPreflight(path, profile, protection);

        Assert.DoesNotContain(report.Findings, finding => finding.Code == "DocumentChecksUnavailable");
        Assert.Contains(report.Findings, finding => finding.Code == "OutputIntent.Missing");
    }

    [Fact]
    public void AFileNoTabHoldsAKeyForStillNeedsItsPassword()
    {
        (string path, _) = Tab(signed: true);

        PdfPreflightReport report = PdfEngineIntegration.InspectPreflight(path, OpenedProtection.None);

        Assert.Contains(report.Findings, finding => finding.Code == "DocumentChecksUnavailable");
    }
}
