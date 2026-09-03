using System.Globalization;
using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Security;
using KillerPdf.Engine.Validation;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Validation;

public sealed class PdfRoundTripValidatorTests
{
    [Fact]
    public void Validate_ReturnsReopenableBytesAndAStableSha256()
    {
        PdfRoundTripResult result = PdfRoundTripValidator.Validate(ValidPdf());

        Assert.True(result.Succeeded, result.FailureMessage);
        Assert.True(result.IsDeterministic);
        Assert.NotNull(result.RewrittenBytes);
        Assert.Equal(64, result.RewrittenSha256!.Length);
        Assert.True(result.RewrittenInspection!.IsStructurallyValid);
        Assert.Null(result.FailureMessage);
    }

    [Fact]
    public void Validate_ReturnsDiagnosticsInsteadOfThrowingForDamage()
    {
        PdfRoundTripResult result = PdfRoundTripValidator.Validate("broken"u8.ToArray());

        Assert.False(result.Succeeded);
        Assert.True(result.SourceInspection.RequiresRepair);
        Assert.Null(result.RewrittenBytes);
    }

    [Fact]
    public void ValidateAuthenticated_VerifiesSemanticStabilityWithRandomizedCiphertext()
    {
        byte[] source = new PdfDocumentBuilder()
            .SetPasswordEncryption(new PdfPasswordEncryptionOptions
            {
                UserPassword = "user-password",
                OwnerPassword = "owner-password"
            })
            .AddBlankPage()
            .Build();

        PdfRoundTripResult result = PdfRoundTripValidator.ValidateAuthenticated(
            source, "owner-password", new PdfDocumentWriteOptions
            {
                CrossReferenceFormat = PdfCrossReferenceFormat.Stream,
                UseObjectStreams = true,
                CompressStructuralStreams = true
            });

        Assert.True(result.Succeeded, result.FailureMessage);
        Assert.False(result.IsDeterministic);
        Assert.NotNull(result.RewrittenBytes);
        PdfDocument rewritten = PdfDocument.Open(
            result.RewrittenBytes, "user-password");
        Assert.True(rewritten.IsDecrypted);
        Assert.True(rewritten.CrossReferences.Sections[0].IsStream);
        Assert.Null(result.FailureMessage);
    }

    [Fact]
    public void Validate_ReturnsAFailureForAnIncorrectEncryptionPassword()
    {
        byte[] source = new PdfDocumentBuilder()
            .SetPasswordEncryption(new PdfPasswordEncryptionOptions
            {
                UserPassword = "user-password",
                OwnerPassword = "owner-password"
            })
            .AddBlankPage()
            .Build();

        PdfRoundTripResult result = PdfRoundTripValidator.ValidateAuthenticated(source, "wrong");

        Assert.False(result.Succeeded);
        Assert.False(result.IsDeterministic);
        Assert.Null(result.RewrittenBytes);
        Assert.Equal(PdfRoundTripFailureCode.AuthenticationFailed, result.Failure?.Code);
        Assert.Contains("password is incorrect", result.Failure!.Format(CultureInfo.GetCultureInfo("en-US")),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_RequestsAuthenticationForEncryptedInputWithoutCredentials()
    {
        byte[] source = new PdfDocumentBuilder()
            .SetPasswordEncryption(new PdfPasswordEncryptionOptions
            {
                UserPassword = "user-password",
                OwnerPassword = "owner-password"
            })
            .AddBlankPage()
            .Build();

        PdfRoundTripResult result = PdfRoundTripValidator.Validate(source);

        Assert.False(result.Succeeded);
        Assert.True(result.SourceInspection.RequiresAuthentication);
        Assert.False(result.SourceInspection.RequiresRepair);
        Assert.Equal(PdfRoundTripFailureCode.AuthenticationRequired, result.Failure?.Code);
        Assert.Contains("authentication is required", result.Failure!.Format(CultureInfo.GetCultureInfo("en-US")),
            StringComparison.Ordinal);
    }

    private static byte[] ValidPdf()
    {
        var source = new StringBuilder("%PDF-2.0\n");
        int objectOffset = source.Length;
        source.Append("1 0 obj << /Type /Catalog >> endobj\n");
        int xrefOffset = source.Length;
        source.Append("xref\n0 2\n0000000000 65535 f\n");
        source.Append($"{objectOffset:0000000000} 00000 n\n");
        source.Append("trailer << /Size 2 /Root 1 0 R >>\n");
        source.Append($"startxref\n{xrefOffset}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(source.ToString());
    }
}
