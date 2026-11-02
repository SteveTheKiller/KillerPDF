using System.IO;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Security;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class PrintPermissionTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "KillerPDF-print-permission-" + Guid.NewGuid().ToString("N"));

    public PrintPermissionTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, true);

    private string Pdf(string name, bool low, bool high, bool modification = true, string user = "")
    {
        string path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, new PdfDocumentBuilder()
            .SetPasswordEncryption(new PdfPasswordEncryptionOptions
            {
                UserPassword = user,
                OwnerPassword = "owner-password",
                AllowLowQualityPrinting = low,
                AllowHighQualityPrinting = high,
                AllowDocumentModification = modification
            })
            .AddBlankPage()
            .Build());
        return path;
    }

    [Fact]
    public void AFileThatMayNotBePrintedIsForbiddenEvenThoughItOpensForEditing()
    {
        Assert.Equal(PrintAllowance.Forbidden, PrintPermission.ReadFor(Pdf("none.pdf", low: false, high: false)));
    }

    [Fact]
    public void AFileThatMayOnlyBePrintedAtLowQualityIsSaidSo()
    {
        Assert.Equal(PrintAllowance.LowQualityOnly, PrintPermission.ReadFor(Pdf("low.pdf", low: true, high: false)));
    }

    [Fact]
    public void AFileThatMayBePrintedIsAllowedWhateverElseItForbids()
    {
        Assert.Equal(PrintAllowance.Allowed, PrintPermission.ReadFor(Pdf("full.pdf", low: true, high: true, modification: false)));
    }

    [Fact]
    public void AFileWithNoEncryptionOrNoFileAllowsPrinting()
    {
        string plain = Path.Combine(_folder, "plain.pdf");
        File.WriteAllBytes(plain, new PdfDocumentBuilder().AddBlankPage().Build());

        Assert.Equal(PrintAllowance.Allowed, PrintPermission.ReadFor(plain));
        Assert.Equal(PrintAllowance.Allowed, PrintPermission.ReadFor(Path.Combine(_folder, "missing.pdf")));
    }

    [Fact]
    public void OnlyTheOwnerPasswordLiftsTheLimit()
    {
        string path = Pdf("none.pdf", low: false, high: false, user: "reader");

        Assert.True(PrintPermission.IsOwnerPassword(path, "owner-password"));
        Assert.False(PrintPermission.IsOwnerPassword(path, "reader"));
        Assert.False(PrintPermission.IsOwnerPassword(path, "wrong"));
        Assert.Equal(PrintAllowance.Allowed, PrintPermission.ForPassword(path, "owner-password"));
        Assert.Equal(PrintAllowance.Forbidden, PrintPermission.ForPassword(path, "reader"));
        Assert.Equal(PrintAllowance.Forbidden, PrintPermission.ForPassword(path, null));
    }

    [Fact]
    public void LowQualityPrintingIsHalfTheResolution()
    {
        Assert.Equal(300, PrintPermission.Dpi(PrintAllowance.Allowed));
        Assert.Equal(150, PrintPermission.Dpi(PrintAllowance.LowQualityOnly));
    }
}
