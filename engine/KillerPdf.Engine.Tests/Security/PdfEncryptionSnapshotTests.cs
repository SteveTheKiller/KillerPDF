using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Filters;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Security;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Security;

// A document edited as plain bytes goes back under the encryption of the file it came from, so a
// save needs no owner password and every limit survives it.
public sealed class PdfEncryptionSnapshotTests
{
    private const string User = "user-password";
    private const string Owner = "owner-password";
    private static readonly byte[] EditedContent = "q 1 0 0 rg 10 10 50 50 re f Q\n"u8.ToArray();

    public static TheoryData<string> Files => ["revision 2 RC4", "revision 4", "revision 6", "AES-256-GCM"];

    private static byte[] File(string name) => name switch
    {
        "revision 2 RC4" => PdfEncryptionTests.Revision2Fixture(),
        "revision 4" => PdfEncryptionTests.Revision4Fixture(),
        "AES-256-GCM" => Modern(PdfPasswordEncryptionAlgorithm.Aes256Gcm),
        _ => Modern(PdfPasswordEncryptionAlgorithm.Aes256Cbc)
    };

    private static byte[] Modern(PdfPasswordEncryptionAlgorithm algorithm) => new PdfDocumentBuilder()
        .SetPasswordEncryption(new PdfPasswordEncryptionOptions
        {
            Algorithm = algorithm,
            UserPassword = User,
            OwnerPassword = Owner,
            AllowLowQualityPrinting = false,
            AllowHighQualityPrinting = false,
            AllowDocumentModification = true
        })
        .AddPage(200, 200, "q 0 0 10 10 re f Q"u8.ToArray())
        .Build();

    // The working copy shares nothing with the original: not its object layout and not its /ID.
    private static PdfDocument EditedPlainCopy() => PdfDocument.Open(new PdfDocumentBuilder()
        .SetMetadata(new PdfDocumentMetadata { Title = "The edited title" })
        .AddPage(300, 300, EditedContent)
        .AddPage(300, 300, EditedContent)
        .Build());

    private static PdfDictionary EncryptionOf(byte[] file)
    {
        PdfDocument document = PdfDocument.OpenWithCompatibilityRecovery(file);
        Assert.True(document.CrossReferences.TryGetTrailerValue(new PdfName("Encrypt"u8), out PdfObject? value));
        return Assert.IsType<PdfDictionary>(document.Resolve(Assert.IsType<PdfIndirectReference>(value)));
    }

    private static byte[] Written(PdfObject value) => PdfObjectWriter.Write(value);

    private static byte[] Identifier(byte[] file)
    {
        PdfDocument document = PdfDocument.OpenWithCompatibilityRecovery(file);
        Assert.True(document.CrossReferences.TryGetTrailerValue(new PdfName("ID"u8), out PdfObject? value));
        return Written(value);
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void AnEditedCopySavedUnderTheHeldEncryptionNeedsTheSamePasswords(string name)
    {
        byte[] original = File(name);
        PdfEncryptionSnapshot encryption = PdfDocument.Open(original, User).CaptureEncryption()!;

        byte[] saved = PdfDocumentEncryptionWriter.EncryptUnder(EditedPlainCopy(), encryption);

        Assert.True(PdfDocument.OpenWithCompatibilityRecovery(saved).IsEncrypted);
        Assert.False(PdfDocument.OpenWithCompatibilityRecovery(saved).IsDecrypted);
        Assert.Equal(PdfPasswordAuthenticationRole.User, PdfDocument.Open(saved, User).PasswordAuthenticationRole);
        Assert.Equal(PdfPasswordAuthenticationRole.Owner, PdfDocument.Open(saved, Owner).PasswordAuthenticationRole);
        Assert.ThrowsAny<Exception>(() => PdfDocument.Open(saved, "wrong"));
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void TheSavedFileCarriesTheOriginalDictionaryAndIdentifier(string name)
    {
        byte[] original = File(name);
        PdfEncryptionSnapshot encryption = PdfDocument.Open(original, User).CaptureEncryption()!;

        byte[] saved = PdfDocumentEncryptionWriter.EncryptUnder(EditedPlainCopy(), encryption);

        Assert.True(PdfDocument.OpenWithCompatibilityRecovery(saved).IsEncrypted);
        PdfDictionary before = EncryptionOf(original), after = EncryptionOf(saved);
        foreach (string key in new[] { "O", "U", "P" })
        {
            PdfName entry = new(Encoding.ASCII.GetBytes(key));
            Assert.Equal(Written(before[entry]), Written(after[entry]));
        }
        Assert.Equal(Identifier(original), Identifier(saved));
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void TheEditIsWhatReopensAndNothingOfItSitsInTheClear(string name)
    {
        PdfEncryptionSnapshot encryption = PdfDocument.Open(File(name), User).CaptureEncryption()!;

        byte[] saved = PdfDocumentEncryptionWriter.EncryptUnder(EditedPlainCopy(), encryption);

        Assert.True(saved.AsSpan().IndexOf("The edited title"u8) < 0);
        PdfDocument reopened = PdfDocument.Open(saved, User);
        Assert.Equal(2, PdfPageInformation.Read(reopened).Count);
        Assert.Equal("The edited title", PdfDocumentInformation.Read(reopened).Title);
    }

    [Fact]
    public void OneSaveFollowsAnother()
    {
        PdfEncryptionSnapshot encryption = PdfDocument.Open(File("revision 4"), User).CaptureEncryption()!;

        byte[] first = PdfDocumentEncryptionWriter.EncryptUnder(EditedPlainCopy(), encryption);
        Assert.True(PdfDocument.OpenWithCompatibilityRecovery(first).IsEncrypted);
        PdfEncryptionSnapshot held = PdfDocument.Open(first, User).CaptureEncryption()!;
        byte[] second = PdfDocumentEncryptionWriter.EncryptUnder(EditedPlainCopy(), held);

        Assert.Equal(PdfPasswordAuthenticationRole.Owner, PdfDocument.Open(second, Owner).PasswordAuthenticationRole);
        Assert.Equal(Identifier(first), Identifier(second));
    }

    // Certificate holders have no password; the key they unwrapped is what the save writes under.
    [Fact]
    public void ACertificateFileGoesBackForTheSameRecipients()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=recipient", rsa,
            System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(1));
        byte[] original = PdfDocumentEncryptionWriter.EncryptForRecipients(
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
            });
        PdfEncryptionSnapshot encryption = PdfDocument.Open(original, certificate).CaptureEncryption()!;
        Assert.True(encryption.IsCertificateEncryption);

        byte[] saved = PdfDocumentEncryptionWriter.EncryptUnder(EditedPlainCopy(), encryption);

        PdfDocument reopened = PdfDocument.Open(saved, certificate);
        Assert.True(reopened.IsEncrypted);
        Assert.NotNull(reopened.DeclaredPermissions);
        Assert.False(reopened.DeclaredPermissions.AllowLowQualityPrinting);
        Assert.Equal(2, PdfPageInformation.Read(reopened).Count);
    }

    // A rewrite ends usage rights as any save does, and the writer's refusal of an encrypted rewrite
    // must not leave such a form unsavable.
    [Fact]
    public void AFormWithUsageRightsSavesUnderItsEncryptionAndLosesTheRights()
    {
        PdfEncryptionSnapshot encryption = PdfDocument.Open(File("revision 6"), User).CaptureEncryption()!;
        PdfDocument plain = PdfDocument.Open(WithUsageRights());

        byte[] saved = [];
        Assert.Null(Record.Exception(() => saved = PdfDocumentEncryptionWriter.EncryptUnder(plain, encryption)));

        PdfDocument reopened = PdfDocument.Open(saved, User);
        Assert.True(reopened.IsEncrypted);
        Assert.True(reopened.CrossReferences.TryGetTrailerValue(new PdfName("Root"u8), out PdfObject? root));
        var catalog = Assert.IsType<PdfDictionary>(reopened.Resolve(Assert.IsType<PdfIndirectReference>(root)));
        Assert.False(catalog.ContainsKey(new PdfName("Perms"u8)));
    }

    private static byte[] WithUsageRights()
    {
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R /Perms << /UR3 4 0 R >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>",
            "<< /Type /Sig /Filter /Adobe.PPKLite /SubFilter /adbe.pkcs7.detached /ByteRange [0 10 20 30] /Contents <0000> "
                + "/Reference [<< /Type /SigRef /TransformMethod /UR3 /TransformParams << /Type /TransformParams /V /2.2 >> >>] >>"
        ];
        var text = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int index = 0; index < objects.Length; index++)
        {
            offsets.Add(text.Length);
            text.Append(index + 1).Append(" 0 obj\n").Append(objects[index]).Append("\nendobj\n");
        }
        int xref = text.Length;
        text.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (int offset in offsets) text.Append(offset.ToString("0000000000")).Append(" 00000 n \n");
        text.Append("trailer\n<< /Size ").Append(objects.Length + 1).Append(" /Root 1 0 R >>\nstartxref\n")
            .Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(text.ToString());
    }

    // R3-012: the snapshot alone answers the owner password, with no bytes of the file held.
    [Theory]
    [MemberData(nameof(Files))]
    public void TheSnapshotKnowsTheOwnerPasswordWithoutTheFile(string name)
    {
        PdfEncryptionSnapshot encryption = PdfDocument.Open(File(name), User).CaptureEncryption()!;

        Assert.Equal(PdfPasswordAuthenticationRole.Owner, encryption.RoleFor(Owner));
        Assert.Equal(PdfPasswordAuthenticationRole.User, encryption.RoleFor(User));
        Assert.Equal(PdfPasswordAuthenticationRole.None, encryption.RoleFor("wrong"));
    }

    [Fact]
    public void BytesOpenUnderTheHeldEncryptionWithNoPassword()
    {
        byte[] original = File("revision 6");
        PdfEncryptionSnapshot encryption = PdfDocument.Open(original, User).CaptureEncryption()!;

        PdfDocument reopened = PdfDocument.OpenWithEncryption(original, encryption);

        Assert.True(reopened.IsDecrypted);
        Assert.True(PdfPageInformation.Read(reopened).Count > 0);
        Assert.Throws<InvalidOperationException>(() => PdfDocument.OpenWithEncryption(Modern(PdfPasswordEncryptionAlgorithm.Aes256Cbc), encryption));
    }

    [Fact]
    public void AnUnencryptedOrUnauthenticatedDocumentHasNothingToHold()
    {
        Assert.Null(PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage().Build()).CaptureEncryption());
        Assert.Null(PdfDocument.Open(File("revision 6")).CaptureEncryption());
    }
}
