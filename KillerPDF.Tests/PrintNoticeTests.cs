using System.IO;
using KillerPDF.Services;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Security;
using Xunit;

namespace KillerPDF.Tests;

// SN01 M2: a wrong owner password at the print gate says it was wrong, on a file that forbids printing
// as on one that allows only low quality printing.
public sealed class PrintNoticeTests : IDisposable
{
    private const string Owner = "owner-password";
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "KillerPDF-printnotice-" + Guid.NewGuid().ToString("N"));

    public PrintNoticeTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, true);

    private OpenedProtection Opened(bool lowQualityOnly)
    {
        string path = Path.Combine(_folder, (lowQualityOnly ? "low" : "forbidden") + ".pdf");
        File.WriteAllBytes(path, new PdfDocumentBuilder().SetPasswordEncryption(new PdfPasswordEncryptionOptions
        {
            UserPassword = "",
            OwnerPassword = Owner,
            AllowLowQualityPrinting = lowQualityOnly,
            AllowHighQualityPrinting = false,
        }).AddBlankPage(200, 200).Build());
        return OpenedProtection.Capture(path, null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AWrongOwnerPasswordIsToldItWasWrong(bool lowQualityOnly)
    {
        OpenedProtection protection = Opened(lowQualityOnly);
        Assert.True(protection.CanLift);

        PrintAnswer answer = protection.AskToPrint(_ => "not the owner password");

        Assert.True(answer.WrongPassword);
        Assert.Equal(lowQualityOnly, answer.MayPrint);
        Assert.Equal(PrintNotice.WrongPassword, PrintPermission.NoticeFor(answer, recipient: false, protection.CanLift));
    }

    [Fact]
    public void ACanceledPromptOnAForbiddenFileOnlyNotesTheRefusal()
    {
        OpenedProtection protection = Opened(lowQualityOnly: false);

        PrintAnswer answer = protection.AskToPrint(_ => null);

        Assert.False(answer.MayPrint);
        Assert.Equal(PrintNotice.RefusedQuietly, PrintPermission.NoticeFor(answer, recipient: false, protection.CanLift));
    }

    [Fact]
    public void TheRightOwnerPasswordPrintsAtFullResolutionWithNothingToSay()
    {
        OpenedProtection protection = Opened(lowQualityOnly: false);

        PrintAnswer answer = protection.AskToPrint(_ => Owner);

        Assert.True(answer.MayPrint);
        Assert.Equal(PrintPermission.FullDpi, answer.Dpi);
        Assert.Equal(PrintNotice.None, PrintPermission.NoticeFor(answer, recipient: false, protection.CanLift));
    }
}
