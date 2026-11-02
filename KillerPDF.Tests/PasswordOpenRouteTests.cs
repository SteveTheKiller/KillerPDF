using System.IO;
using System.Text;
using KillerPDF.Services;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Security;
using KillerPdf.Engine.Signing;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPDF.Tests;

// R5-003: a file whose user password opened it and whose strip failed opens where it lies only when a
// rewrite would clear a signature or a certification. Reader usage rights alone do not stop the
// viewing copy, so a Reader-extended form keeps that route.
public sealed class PasswordOpenRouteTests : IDisposable
{
    private const string Owner = "owner-password";
    private const string Reader = "reader";
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "KillerPDF-openroute-" + Guid.NewGuid().ToString("N"));

    public PasswordOpenRouteTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, true);

    private string InFolder(string name) => Path.Combine(_folder, name);

    private static PdfPasswordEncryptionOptions Limits() => new()
    {
        UserPassword = Reader,
        OwnerPassword = Owner,
        AllowDocumentModification = false,
        AllowFormFilling = true,
    };

    // A form Reader extended with usage rights, and no signed field. Every writer drops usage rights,
    // so they are added the way Reader's server adds them: an update that appends a new catalog.
    private string UsageRightsForm()
    {
        byte[] encrypted = new PdfDocumentBuilder().SetPasswordEncryption(Limits())
            .AddBlankPage(200, 200).AddTextField(0, "name", 10, 120, 120, 20, "old value", 10).Build();
        PdfDocument document = PdfDocument.Open(encrypted, Owner);
        string Value(string key)
        {
            Assert.True(document.CrossReferences.TryGetTrailerValue(new PdfName(Encoding.ASCII.GetBytes(key)), out PdfObject? value));
            return Encoding.Latin1.GetString(PdfObjectWriter.Write(value!));
        }
        Assert.True(document.CrossReferences.TryGetTrailerValue(new PdfName("Root"u8), out PdfObject? rootValue));
        var root = Assert.IsType<PdfIndirectReference>(rootValue);
        var catalog = Assert.IsType<PdfDictionary>(document.Resolve(root));
        var rest = new StringBuilder();
        // The update is written in the clear, so only entries that hold no string are carried over.
        foreach (var (name, value) in catalog.Where(entry => entry.Value is PdfIndirectReference or PdfName))
            rest.Append(Encoding.Latin1.GetString(PdfObjectWriter.Write(name))).Append(' ')
                .Append(Encoding.Latin1.GetString(PdfObjectWriter.Write(value))).Append(' ');
        string text = Encoding.Latin1.GetString(encrypted);
        int marker = text.LastIndexOf("startxref", StringComparison.Ordinal);
        string previous = text[(marker + "startxref".Length)..].Trim().Split('\n', '\r')[0].Trim();
        var update = new StringBuilder();
        int offset = encrypted.Length;
        update.Append($"{root.ObjectNumber} 0 obj\n<< {rest}/Perms << /UR3 << /Type /Sig >> >> >>\nendobj\n");
        int xref = offset + update.Length;
        update.Append($"xref\n{root.ObjectNumber} 1\n{offset:D10} 00000 n \n");
        update.Append($"trailer\n<< /Size {Value("Size")} /Root {root.ObjectNumber} 0 R /Encrypt {Value("Encrypt")} /ID {Value("ID")} /Prev {previous} >>\n");
        update.Append($"startxref\n{xref}\n%%EOF\n");
        string path = InFolder("usage-rights.pdf");
        File.WriteAllBytes(path, [.. encrypted, .. Encoding.Latin1.GetBytes(update.ToString())]);
        return path;
    }

    private string SignedForm()
    {
        string unsigned = InFolder("signed-unsigned.pdf");
        File.WriteAllBytes(unsigned, new PdfDocumentBuilder().SetPasswordEncryption(Limits()).AddBlankPage(200, 200).Build());
        string signed = InFolder("signed.pdf");
        File.WriteAllBytes(signed, PdfDetachedSignatureWriter.Sign(PdfDocument.Open(File.ReadAllBytes(unsigned), Owner),
            _ => new byte[64], new PdfSignatureOptions { FieldName = "Signature1" }));
        return signed;
    }

    [Fact]
    public void AReaderExtendedFormThatNeedsAPasswordKeepsTheViewingCopy()
    {
        string path = UsageRightsForm();
        // The fixture is what the route sees: usage rights the save warning still names, a user
        // password that opens it, and a strip the user role cannot make.
        Assert.True(PdfEngineIntegration.HasSignedSignatures(path, Reader));
        Assert.Equal(PdfAccessRole.User, PdfRestrictions.Read(path, Reader).Role);
        Assert.ThrowsAny<Exception>(() => PdfEngineIntegration.RemoveEncryption(path, InFolder("stripped.pdf"), Reader));

        Assert.False(PdfEngineIntegration.OpensWhereItLies(path, Reader));

        // The viewing copy that route writes opens, and the original keeps its passwords and limits.
        byte[] before = File.ReadAllBytes(path);
        OpenedProtection protection = OpenedProtection.Capture(path, Reader);
        string copy = InFolder("viewing.pdf");
        protection.WriteViewingCopy(path, copy);
        using (PdfWorkingDocument document = PdfWorkingDocument.Open(copy, null, isReadOnly: true))
            Assert.Equal(1, document.PageCount);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(PdfAccessRole.Refused, PdfRestrictions.Read(path, null).Role);
        Assert.Equal(PdfAccessRole.Owner, PdfRestrictions.Read(path, Owner).Role);
    }

    [Fact]
    public void ASignedFileThatNeedsAPasswordStillOpensWhereItLies()
    {
        string path = SignedForm();

        Assert.True(PdfEngineIntegration.OpensWhereItLies(path, Reader));
    }
}
