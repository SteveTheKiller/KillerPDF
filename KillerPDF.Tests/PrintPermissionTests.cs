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

    private static PrintAllowance Allowance(string path, string? password = null) => PdfRestrictions.Read(path, password).Print;

    [Fact]
    public void AFileThatMayNotBePrintedIsForbiddenEvenThoughItOpensForEditing()
    {
        Assert.Equal(PrintAllowance.Forbidden, Allowance(Pdf("none.pdf", low: false, high: false)));
    }

    [Fact]
    public void AFileThatMayOnlyBePrintedAtLowQualityIsSaidSo()
    {
        Assert.Equal(PrintAllowance.LowQualityOnly, Allowance(Pdf("low.pdf", low: true, high: false)));
    }

    [Fact]
    public void AFileThatMayBePrintedIsAllowedWhateverElseItForbids()
    {
        Assert.Equal(PrintAllowance.Allowed, Allowance(Pdf("full.pdf", low: true, high: true, modification: false)));
    }

    [Fact]
    public void AFileWithNoEncryptionAllowsPrintingAndNoFileDoesNot()
    {
        string plain = Path.Combine(_folder, "plain.pdf");
        File.WriteAllBytes(plain, new PdfDocumentBuilder().AddBlankPage().Build());

        Assert.Equal(PrintAllowance.Allowed, Allowance(plain));
        Assert.Equal(PrintAllowance.Forbidden, Allowance(Path.Combine(_folder, "missing.pdf")));
    }

    [Fact]
    public void OnlyTheOwnerPasswordLiftsTheLimit()
    {
        string path = Pdf("none.pdf", low: false, high: false, user: "reader");

        Assert.Equal(PdfAccessRole.Owner, PdfRestrictions.Read(path, "owner-password").Role);
        Assert.Equal(PdfAccessRole.User, PdfRestrictions.Read(path, "reader").Role);
        Assert.Equal(PdfAccessRole.Refused, PdfRestrictions.Read(path, "wrong").Role);
        Assert.Equal(PrintAllowance.Allowed, Allowance(path, "owner-password"));
        Assert.Equal(PrintAllowance.Forbidden, Allowance(path, "reader"));
        Assert.Equal(PrintAllowance.Forbidden, Allowance(path));
    }

    [Fact]
    public void LowQualityPrintingIsHalfTheResolution()
    {
        Assert.Equal(300, PrintPermission.Dpi(PrintAllowance.Allowed));
        Assert.Equal(150, PrintPermission.Dpi(PrintAllowance.LowQualityOnly));
    }
}
