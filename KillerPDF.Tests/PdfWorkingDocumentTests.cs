using System;
using System.IO;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Security;
using KillerPdf.Engine.Syntax;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class PdfWorkingDocumentTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnOpeningPasswordIsRequiredBeforePublishingAWorkingDocument(bool readOnly)
    {
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-password-{Guid.NewGuid():N}.pdf");
        byte[] protectedBytes = new PdfDocumentBuilder()
            .SetPasswordEncryption(new PdfPasswordEncryptionOptions
                { UserPassword = "opening-password", OwnerPassword = "owner-password" })
            .AddBlankPage(320, 480).Build();
        try
        {
            File.WriteAllBytes(path, protectedBytes);
            InvalidOperationException error = Assert.Throws<InvalidOperationException>(
                () => PdfWorkingDocument.Open(path, null, readOnly));
            Assert.Contains("password", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(protectedBytes, File.ReadAllBytes(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void OwnerRestrictedWorkingDocumentsAuthenticateTheEmptyOpeningPassword()
    {
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-owner-working-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, new PdfDocumentBuilder()
                .SetPasswordEncryption(new PdfPasswordEncryptionOptions
                    { UserPassword = "", OwnerPassword = "owner", AllowDocumentModification = false })
                .AddBlankPage(320, 480).Build());

            using PdfWorkingDocument working = PdfWorkingDocument.Open(path, null, isReadOnly: true);
            PdfDocument parsed = Assert.IsType<PdfDocument>(working.ParsedDocument);
            Assert.True(parsed.IsDecrypted);
            Assert.Equal(PdfPasswordAuthenticationRole.User, parsed.PasswordAuthenticationRole);
            Assert.True(working.IsReadOnly);
            Assert.Equal(1, working.PageCount);
        }
        finally { File.Delete(path); }
    }

    // A full or unavailable temp folder must not turn into a repair offer or a "damaged file" or
    // "wrong password" message on any open path; each reports the working-copy problem.
    [Fact]
    public void EveryOpenPathReportsAWorkingCopyFailureAsSuch()
    {
        string source = FileOperations();
        Assert.Equal(10, Count(source, "catch (WorkingCopyUnavailableException"));
        Assert.Contains("when (error is not OutOfMemoryException and not WorkingCopyUnavailableException)", source);
    }

    // Unlocking an owner-restricted PDF reopens it. With no room for a working copy, putting the
    // read-only original back fails the same way, and that must not escape the failed unlock's
    // catch as an unhandled exception.
    [Fact]
    public void FailedUnlockReportsTheWorkingCopyAndPutsTheOriginalBackSafely()
    {
        string source = FileOperations();
        string unlock = Method(source, "private bool UnlockOwnerRestrictions(");
        Assert.DoesNotContain("TryOpenProtectedReadOnly(", unlock);
        // Form entries and annotations made while read-only come back with the original too, not
        // only with the unlocked copy.
        Assert.Equal(2, Count(unlock, "RestoreReadOnlyOriginal(sourcePath, displayPath, CarryOverlays, protection);"));
        Assert.Contains("CarryOverlays();", unlock[..unlock.IndexOf("catch (WorkingCopyUnavailableException copy)", StringComparison.Ordinal)]);
        // Only the reopen writes a working copy; removing the encryption never does.
        Assert.Equal(1, Count(unlock, "catch (WorkingCopyUnavailableException"));
        string reopen = unlock[unlock.IndexOf("FinishOpenFile(displayPath, unlockedPath);", StringComparison.Ordinal)..];
        Assert.Contains("catch (WorkingCopyUnavailableException copy)", reopen);

        string restore = Method(source, "private void RestoreReadOnlyOriginal(");
        Assert.Contains("ReadOnlyOriginal.Restore(sourcePath, protection)", restore);
        Assert.Contains("carryOverlays();", restore);
        Assert.Contains("catch (WorkingCopyUnavailableException)", restore);
    }

    private static string FileOperations() => File.ReadAllText(Path.Combine(
        Path.GetDirectoryName(Path.GetDirectoryName(SourcePath()))!, "Shell", "FileOperations.cs"));

    private static string Method(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, signature + " was not found.");
        string rest = source[start..];
        return rest[..System.Text.RegularExpressions.Regex.Match(rest, @"\r?\n        \}\r?\n").Index];
    }

    private static int Count(string source, string value) => source.Split(value).Length - 1;

    private static string SourcePath([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;

    // Opening writes a complete copy that the viewer renders from. Registering it with the parse
    // already made keeps one parse tree instead of parsing the same bytes a second time.
    [Fact]
    public void RegisteredCopySharesTheParsedDocument()
    {
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-copy-{Guid.NewGuid():N}.pdf");
        string copy = Path.ChangeExtension(path, ".complete.pdf");
        try
        {
            File.WriteAllBytes(path, new PdfDocumentBuilder().AddBlankPage().Build());
            using PdfWorkingDocument document = PdfWorkingDocument.Open(path, null);
            PdfDocument parsed = Assert.IsType<PdfDocument>(document.ParsedDocument);
            document.Save(copy);
            document.RegisterCopy(copy);

            Assert.Same(parsed, PdfPageRenderSession.OpenDocument(copy, null));
            Assert.Null(document.ParsedDocument);
        }
        finally
        {
            foreach (string file in new[] { path, copy }) if (File.Exists(file)) File.Delete(file);
        }
    }

    [Fact]
    public void Open_AcceptsValidObjectsBeyondIncorrectTrailerSize()
    {
        byte[] source = new PdfDocumentBuilder().AddBlankPage().Build();
        int marker = source.AsSpan().LastIndexOf("/Size "u8);
        Assert.True(marker >= 0);
        int digit = marker + "/Size ".Length;
        Assert.InRange(source[digit], (byte)'2', (byte)'9');
        source[digit++] = (byte)'1';
        while (digit < source.Length && source[digit] is >= (byte)'0' and <= (byte)'9')
            source[digit++] = (byte)' ';

        Assert.Throws<PdfSyntaxException>(() => PdfDocument.Open(source));

        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-trailer-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, source);
            using PdfWorkingDocument document = PdfWorkingDocument.Open(path, null);
            Assert.Equal(1, document.PageCount);
            Assert.False(document.IsReadOnly);
            PdfDocument parsed = Assert.IsType<PdfDocument>(document.ParsedDocument);
            Assert.Same(parsed, PdfPageRenderSession.OpenDocument(path, null));
            Assert.Null(document.ParsedDocument);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void RegisteredWorkingParseDoesNotSurviveFileReplacement()
    {
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-cache-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, new PdfDocumentBuilder().AddBlankPage().Build());
            using PdfWorkingDocument working = PdfWorkingDocument.Open(path, null);
            PdfDocument parsed = Assert.IsType<PdfDocument>(working.ParsedDocument);
            Assert.Same(parsed, PdfPageRenderSession.OpenDocument(path, null));
            Assert.Null(working.ParsedDocument);

            File.WriteAllBytes(path, new PdfDocumentBuilder().AddBlankPage().AddBlankPage().Build());
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(2));
            PdfDocument replacement = PdfPageRenderSession.OpenDocument(path, null);
            Assert.NotSame(parsed, replacement);
            Assert.Equal(2, PdfPageInformation.Read(replacement).Count);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void RegisteredWorkingParseRendersSamePixelsAsFreshOpen()
    {
        byte[] source = new PdfDocumentBuilder().AddPage(200, 100,
            new PdfContentStreamBuilder().SetFillRgb(0.8, 0.1, 0.3)
                .Rectangle(10, 10, 90, 50).Fill()).Build();
        string cachedPath = Path.Combine(Path.GetTempPath(), $"killerpdf-cached-{Guid.NewGuid():N}.pdf");
        string freshPath = Path.Combine(Path.GetTempPath(), $"killerpdf-fresh-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(cachedPath, source);
            File.WriteAllBytes(freshPath, source);
            using PdfWorkingDocument working = PdfWorkingDocument.Open(cachedPath, null);
            using var cached = PdfPageRenderSession.OpenEngineFirst(cachedPath, 256, 256, null);
            using var fresh = PdfPageRenderSession.OpenEngineFirst(freshPath, 256, 256, null);
            Assert.Equal(fresh.RenderPage(0).Pixels, cached.RenderPage(0).Pixels);
        }
        finally
        {
            if (File.Exists(cachedPath)) File.Delete(cachedPath);
            if (File.Exists(freshPath)) File.Delete(freshPath);
        }
    }
}
