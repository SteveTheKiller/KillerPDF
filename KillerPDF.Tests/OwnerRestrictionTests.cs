using KillerPDF.Services;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Security;
using System.IO;
using Xunit;

namespace KillerPDF.Tests;

// A PDF whose owner password forbids editing opens read-only and only becomes editable with the
// owner password, the way Acrobat treats it.
public sealed class OwnerRestrictionTests
{
    private static string WriteOwnerRestricted(string userPassword = "")
    {
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-owner-unlock-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, new PdfDocumentBuilder()
            .SetPasswordEncryption(new PdfPasswordEncryptionOptions
            {
                UserPassword = userPassword,
                OwnerPassword = "owner",
                AllowDocumentModification = false
            })
            .AddBlankPage(320, 480)
            .Build());
        return path;
    }

    [Fact]
    public void OpeningWithoutThePassword_DoesNotStripTheRestriction()
    {
        string source = WriteOwnerRestricted();
        string output = source + ".unlocked.pdf";
        try
        {
            Assert.False(PdfEngineIntegration.TryRemoveEncryptionWithoutPassword(source, output));
            Assert.True(PdfEngineIntegration.CanReadWithoutPassword(source));
        }
        finally
        {
            File.Delete(source);
            if (File.Exists(output)) File.Delete(output);
        }
    }

    [Fact]
    public void TheOwnerPassword_WritesAnUnrestrictedCopy()
    {
        string source = WriteOwnerRestricted();
        string output = source + ".unlocked.pdf";
        try
        {
            PdfEngineIntegration.RemoveEncryption(source, output, "owner");

            PdfDocument unlocked = PdfDocument.Open(File.ReadAllBytes(output));
            Assert.False(unlocked.IsEncrypted);
            Assert.Single(PdfPageInformation.Read(unlocked));
        }
        finally
        {
            File.Delete(source);
            if (File.Exists(output)) File.Delete(output);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("wrong")]
    public void AnyOtherPassword_LeavesItRestricted(string password)
    {
        string source = WriteOwnerRestricted();
        string output = source + ".unlocked.pdf";
        try
        {
            Assert.ThrowsAny<Exception>(() => PdfEngineIntegration.RemoveEncryption(source, output, password));
        }
        finally
        {
            File.Delete(source);
            if (File.Exists(output)) File.Delete(output);
        }
    }

    [Fact]
    public void TheUserPassword_WritesAViewingCopyButNeverAnUnlock()
    {
        string source = WriteOwnerRestricted("reader");
        string output = source + ".view.pdf";
        try
        {
            PdfEngineIntegration.WriteViewingCopy(source, output, "reader");

            Assert.Single(PdfPageInformation.Read(PdfDocument.Open(File.ReadAllBytes(output))));
            Assert.ThrowsAny<Exception>(() => PdfEngineIntegration.RemoveEncryption(source, output + ".2", "reader"));
        }
        finally
        {
            File.Delete(source);
            if (File.Exists(output)) File.Delete(output);
        }
    }

    [Fact]
    public void AWrongPassword_WritesNoViewingCopy()
    {
        string source = WriteOwnerRestricted("reader");
        string output = source + ".view.pdf";
        try
        {
            Assert.ThrowsAny<Exception>(() => PdfEngineIntegration.WriteViewingCopy(source, output, "wrong"));
            Assert.False(File.Exists(output));
        }
        finally { File.Delete(source); }
    }

    [Fact]
    public void UnlockingRefusesASourceWithNoEncryptionToCheckThePasswordAgainst()
    {
        string source = WriteOwnerRestricted("reader");
        string copy = source + ".view.pdf";
        string output = source + ".unlocked.pdf";
        try
        {
            PdfEngineIntegration.WriteViewingCopy(source, copy, "reader");

            Assert.ThrowsAny<Exception>(() => PdfEngineIntegration.UnlockWithPassword(copy, output, "reader"));
            Assert.False(File.Exists(output));
            PdfEngineIntegration.UnlockWithPassword(source, output, "owner");
            Assert.False(PdfDocument.Open(File.ReadAllBytes(output)).IsEncrypted);
        }
        finally
        {
            File.Delete(source);
            if (File.Exists(copy)) File.Delete(copy);
            if (File.Exists(output)) File.Delete(output);
        }
    }

    [Fact]
    public void TheWriterStillRefusesAUserRewriteByDefault()
    {
        string source = WriteOwnerRestricted("reader");
        try
        {
            PdfDocument document = PdfDocument.Open(File.ReadAllBytes(source), "reader");
            Assert.Throws<InvalidOperationException>(() => KillerPdf.Engine.Writing.PdfDocumentWriter.Write(
                document, new KillerPdf.Engine.Writing.PdfDocumentWriteOptions { RemoveEncryption = true }));
        }
        finally { File.Delete(source); }
    }

    [Theory]
    [InlineData("", "", true)]
    [InlineData("user", "user", true)]
    [InlineData("user", "owner", false)]
    [InlineData("user", "wrong", false)]
    public void IsRestrictedForPassword_FlagsOnlyTheRestrictedUserRole(
        string userPassword, string entered, bool restricted)
    {
        string source = WriteOwnerRestricted(userPassword);
        try
        {
            Assert.Equal(restricted, PdfEngineIntegration.IsRestrictedForPassword(source, entered));
        }
        finally { File.Delete(source); }
    }
}
