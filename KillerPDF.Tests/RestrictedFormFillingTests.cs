using System.IO;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Security;
using KillerPdf.Engine.Writing;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

// A published form that opens without a password, forbids editing and allows filling opens read-only in
// KillerPDF. As in Acrobat, its fields can still be filled and saved, and the saved file keeps its
// encryption and permissions. A form whose author forbids filling stays read-only.
public sealed class RestrictedFormFillingTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "killerpdf-restricted-fill-" + Guid.NewGuid().ToString("N"));

    public RestrictedFormFillingTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void AFillableRestrictedFormSavesItsValuesAndKeepsItsProtection(bool fill, bool annotate)
    {
        string source = Form("fillable.pdf", fill, annotate);
        string saved = Path.Combine(_directory, "saved.pdf");

        Assert.True(OpenedProtection.Capture(source, null).AllowsRestrictedFill);
        PdfEngineIntegration.FillRestrictedForm(source, saved, Edits(("name", "Pat Example")), null);

        PdfDocument document = PdfDocument.Open(File.ReadAllBytes(saved), string.Empty);
        Assert.True(document.IsEncrypted);
        Assert.False(document.DeclaredPermissions!.AllowDocumentModification);
        Assert.Equal("Pat Example", Assert.Single(PdfFormWidgetReader.ReadPage(document, 0)).Value);
    }

    [Fact]
    public void AFormWhoseAuthorForbidsFillingStaysReadOnly()
    {
        string source = Form("locked.pdf", fill: false, annotate: false);
        byte[] before = File.ReadAllBytes(source);

        Assert.False(OpenedProtection.Capture(source, null).AllowsRestrictedFill);
        Assert.Throws<InvalidOperationException>(() => PdfEngineIntegration.FillRestrictedForm(
            source, Path.Combine(_directory, "never.pdf"), Edits(("name", "x")), null));
        Assert.Equal(before, File.ReadAllBytes(source));
        Assert.False(File.Exists(Path.Combine(_directory, "never.pdf")));
    }

    [Fact]
    public void AnUnrestrictedOrUnencryptedFileIsNotTreatedAsARestrictedForm()
    {
        string plain = Path.Combine(_directory, "plain.pdf");
        File.WriteAllBytes(plain, new PdfDocumentBuilder().AddBlankPage()
            .AddTextField(0, "name", 20, 20, 160, 24, string.Empty, 11).Build());

        Assert.False(OpenedProtection.Capture(plain, null).AllowsRestrictedFill);
    }

    private string Form(string name, bool fill, bool annotate)
    {
        string path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, PdfDocumentEncryptionWriter.EncryptWithPasswords(
            PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage(200, 300)
                .AddTextField(0, "name", 20, 20, 160, 24, string.Empty, 11).Build()),
            new PdfPasswordEncryptionOptions
            {
                UserPassword = string.Empty,
                OwnerPassword = "owner",
                AllowDocumentModification = false,
                AllowFormFilling = fill,
                AllowAnnotationModification = annotate
            }));
        return path;
    }

    private static PdfEngineIntegration.FormEdits Edits(params (string Name, string Value)[] text) =>
        new(text.ToDictionary(item => item.Name, item => item.Value), new Dictionary<string, string>(),
            new Dictionary<string, IReadOnlyList<string>>(), new Dictionary<string, bool>(),
            new Dictionary<string, string>(), new Dictionary<string, double>());
}
