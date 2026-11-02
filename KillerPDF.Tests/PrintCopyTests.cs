using System.IO;
using System.Security.Cryptography;
using KillerPDF.Services;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Security;
using KillerPdf.Engine.Signing;
using Xunit;

namespace KillerPDF.Tests;

// R6-001: a tab that reads its encrypted original where it lies still prints. The print copy is a
// decrypted throwaway made under the tab's own protection, in the temp folder and never beside the
// original, so every preparation step and the preview render read it as a plain document.
public sealed class PrintCopyTests : IDisposable
{
    private const string Owner = "owner-password";
    private const string Reader = "reader";
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "KillerPDF-printcopy-" + Guid.NewGuid().ToString("N"));
    private readonly string _originals;
    private readonly string _temp;

    public PrintCopyTests()
    {
        _originals = Path.Combine(_folder, "originals");
        _temp = Path.Combine(_folder, "temp");
        Directory.CreateDirectory(_originals);
        Directory.CreateDirectory(_temp);
    }

    public void Dispose() => Directory.Delete(_folder, true);

    private static byte[] Encrypted(string user, bool allowModification) => new PdfDocumentBuilder()
        .SetPasswordEncryption(new PdfPasswordEncryptionOptions
        {
            UserPassword = user,
            OwnerPassword = Owner,
            AllowDocumentModification = allowModification,
        })
        .AddPage(300, 400, new PdfContentStreamBuilder()
            .BeginText().SetFont(PdfStandardFont.Helvetica, 12)
            .MoveText(30, 350).ShowLatin1Text("Page one").EndText())
        .AddPage(300, 400, new PdfContentStreamBuilder()
            .BeginText().SetFont(PdfStandardFont.Helvetica, 12)
            .MoveText(30, 350).ShowLatin1Text("Page two").EndText())
        .Build();

    private static byte[] Sign(byte[] encrypted, PdfSignatureCertificationPermission? certification = null) =>
        PdfDetachedSignatureWriter.Sign(PdfDocument.Open(encrypted, Owner), _ => new byte[64],
            new PdfSignatureOptions { FieldName = "Signature1", CertificationPermission = certification });

    public static TheoryData<string> Files => new() { "owner-only", "owner-only-signed", "owner-only-certified", "signed-user-password" };

    // Each fixture is written, then reopened to prove it holds the passwords and permissions it was
    // made with, and that the app's own open sends it read-only to the encrypted original.
    private (string Path, OpenedProtection Protection) Original(string kind)
    {
        string user = kind == "signed-user-password" ? Reader : "";
        bool modify = kind is "owner-only-signed" or "owner-only-certified";
        byte[] bytes = Encrypted(user, modify);
        if (kind == "owner-only-signed" || kind == "signed-user-password") bytes = Sign(bytes);
        if (kind == "owner-only-certified") bytes = Sign(bytes, PdfSignatureCertificationPermission.FormFillingAndSignatures);
        string path = Path.Combine(_originals, kind + ".pdf");
        File.WriteAllBytes(path, bytes);

        PdfDocument asOwner = PdfDocument.Open(File.ReadAllBytes(path), Owner);
        Assert.Equal(PdfPasswordAuthenticationRole.Owner, asOwner.PasswordAuthenticationRole);
        PdfDocument asUser = PdfDocument.Open(File.ReadAllBytes(path), user);
        Assert.Equal(PdfPasswordAuthenticationRole.User, asUser.PasswordAuthenticationRole);
        PdfDocumentPermissions permissions = asUser.DeclaredPermissions!;
        Assert.True(permissions.AllowHighQualityPrinting && permissions.AllowLowQualityPrinting);
        Assert.Equal(modify, permissions.AllowDocumentModification);
        Assert.Equal(kind != "owner-only", PdfSignatureReader.Read(asUser).Any(signature => signature.IsSigned));

        // The routes: an owner-only file whose strip is refused, and a signed file that needs a password.
        if (user.Length == 0)
            Assert.False(PdfEngineIntegration.TryRemoveEncryptionWithoutPassword(path, Path.Combine(_temp, "strip.pdf")));
        else
            Assert.True(PdfEngineIntegration.OpensWhereItLies(path, Reader));
        OpenedProtection protection = OpenedProtection.Capture(path, user.Length == 0 ? null : user);
        Assert.Equal(PdfAccessRole.User, protection.Access.Role);
        return (path, protection);
    }

    private static PdfEngineIntegration.FormEdits NoFormValues() => new(
        new Dictionary<string, string>(), new Dictionary<string, string>(),
        new Dictionary<string, IReadOnlyList<string>>(), new Dictionary<string, bool>(),
        new Dictionary<string, string>(), new Dictionary<string, double>());

    // The print flow: write the copy, hand it over, ready it, then render every page of the preview
    // the way the print preview does, with no protection, because the copy is plain.
    private string Print(PdfWorkingDocument document)
    {
        string clean = Path.Combine(_temp, "printclean.pdf");
        string print = Path.Combine(_temp, "print.pdf");
        PrintCopy.Write(document, clean);
        File.Copy(clean, print, true);
        PrintCopy.Ready(print, null, NoFormValues(), new Dictionary<int, int> { [1] = 90 });
        using var session = PdfPageRenderSession.OpenEngineFirst(print, 400, 400, null, forPrinting: true);
        for (int index = 0; index < session.PageCount; index++)
            Assert.True(session.RenderPage(index, requireComplete: false).Width > 0);
        return print;
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void AReadOnlyOriginalPrints(string kind)
    {
        (string path, OpenedProtection protection) = Original(kind);
        byte[] before = File.ReadAllBytes(path);
        using PdfWorkingDocument document = ReadOnlyOriginal.Open(path, protection).Document;

        string? print = null;
        Exception? failure = Record.Exception(() => print = Print(document));

        Assert.Null(failure);
        PdfDocument printed = PdfDocument.Open(File.ReadAllBytes(print!));
        Assert.False(printed.IsEncrypted);
        Assert.Equal(2, PdfPageInformation.Read(printed).Count);
        // The original is untouched and nothing was written beside it.
        Assert.Equal(SHA256.HashData(before), SHA256.HashData(File.ReadAllBytes(path)));
        Assert.Equal([path], Directory.GetFiles(_originals));
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void AnOriginalPutBackAfterAFailedUnlockPrints(string kind)
    {
        (string path, OpenedProtection protection) = Original(kind);
        using PdfWorkingDocument document = ReadOnlyOriginal.Restore(path, protection).Document;

        Exception? failure = Record.Exception(() => Print(document));

        Assert.Null(failure);
    }

    [Fact]
    public void APlainTabsPrintCopyIsItsOwnBytes()
    {
        string path = Path.Combine(_originals, "plain.pdf");
        File.WriteAllBytes(path, new PdfDocumentBuilder().AddBlankPage().Build());
        using PdfWorkingDocument document = PdfWorkingDocument.Open(path, OpenedProtection.None);
        string clean = Path.Combine(_temp, "clean.pdf");

        PrintCopy.Write(document, clean);

        Assert.Equal(File.ReadAllBytes(path), File.ReadAllBytes(clean));
    }
}
