using System.Security.Cryptography;
using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.CrossReference;
using KillerPdf.Engine.Filters;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Security;
using KillerPdf.Engine.Syntax;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Security;

public sealed class PdfEncryptionTests
{
    [Fact]
    public void IncrementalEncryption_ResolvesIndirectMetadataTypeBeforeApplyingExemption()
    {
        byte[] source = new PdfDocumentBuilder()
            .SetMetadata(new PdfDocumentMetadata { Title = "initial metadata" })
            .SetPasswordEncryption(new PdfPasswordEncryptionOptions
            {
                UserPassword = "user",
                OwnerPassword = "owner",
                EncryptMetadata = false
            })
            .AddBlankPage()
            .Build();
        PdfDocument document = PdfDocument.Open(source, "owner");
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(document.Resolve(
            Assert.IsType<PdfIndirectReference>(document.Trailer[new PdfName("Root"u8)])));
        PdfIndirectReference metadataReference = Assert.IsType<PdfIndirectReference>(
            catalog[new PdfName("Metadata"u8)]);
        PdfStream metadata = Assert.IsType<PdfStream>(document.Resolve(metadataReference));
        var update = new PdfIncrementalUpdateBuilder(document);
        PdfIndirectReference metadataType = update.AddObject(new PdfName("Metadata"u8));
        PdfIndirectReference metadataTypeAlias = update.AddObject(metadataType);
        var dictionary = new PdfDictionary(metadata.Dictionary.Select(entry =>
            entry.Key.Equals(new PdfName("Type"u8))
                ? new KeyValuePair<PdfName, PdfObject>(entry.Key, metadataTypeAlias)
                : entry));
        const string replacement = "aliased metadata payload unique";
        byte[] incremented = update.ReplaceObject(metadataReference.ObjectNumber,
            new PdfStream(dictionary, Encoding.ASCII.GetBytes(replacement))).Build();

        Assert.True(incremented.AsSpan().IndexOf(Encoding.ASCII.GetBytes(replacement)) >= 0);
        PdfStream reopenedMetadata = Assert.IsType<PdfStream>(
            PdfDocument.Open(incremented, "owner").Resolve(metadataReference));
        Assert.Equal(replacement,
            Encoding.ASCII.GetString(reopenedMetadata.EncodedData.Span));
    }

    [Fact]
    public void PdfUa2_EncryptionRequiresAccessibilityExtractionPermission()
    {
        static PdfDocumentBuilder Builder(bool allowAccessibility) => new PdfDocumentBuilder()
            .SetMetadata(new PdfDocumentMetadata
            {
                Title = "Accessible encrypted PDF",
                Language = "en-US"
            })
            .EnablePdfUa2Conformance()
            .SetPasswordEncryption(new PdfPasswordEncryptionOptions
            {
                UserPassword = "user",
                OwnerPassword = "owner",
                AllowAccessibilityExtraction = allowAccessibility
            })
            .AddBlankPage()
            .AddStructureContainer(PdfStructureType.Document);

        Assert.Throws<InvalidOperationException>(() => Builder(false).Build());
        PdfDocument document = PdfDocument.Open(Builder(true).Build(), "user");
        Assert.True(document.DeclaredPermissions!.AllowAccessibilityExtraction);
    }

    [Theory]
    [InlineData("new-user")]
    [InlineData("new-owner")]
    public void Authoring_CreatesRevision6Aes256Document(string password)
    {
        byte[] bytes = new PdfDocumentBuilder()
            .SetPasswordEncryption(new PdfPasswordEncryptionOptions
            {
                UserPassword = "new-user",
                OwnerPassword = "new-owner"
            })
            .AddPage(612, 792, "BT (private authored text) Tj ET"u8.ToArray())
            .Build();

        PdfDocument document = PdfDocument.Open(bytes, password);

        Assert.True(document.IsEncrypted);
        Assert.True(document.IsDecrypted);
        Assert.Equal(-1, bytes.AsSpan().IndexOf("private authored text"u8));
        Assert.ThrowsAny<CryptographicException>(() => PdfDocument.Open(bytes, "wrong"));
    }

    [Fact]
    public void Authoring_RejectsEncryptionWithPdfAConformance()
    {
        var builder = new PdfDocumentBuilder()
            .EnablePdfA4Conformance()
            .SetPasswordEncryption(new PdfPasswordEncryptionOptions
            {
                UserPassword = "user",
                OwnerPassword = "owner"
            });

        Assert.Throws<InvalidOperationException>(() => builder.Build());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Authoring_RejectsPasswordsContainingUnpairedSurrogates(bool invalidUser)
    {
        string invalid = new([invalidUser ? '\uD800' : '\uDC00']);
        var builder = new PdfDocumentBuilder()
            .SetPasswordEncryption(new PdfPasswordEncryptionOptions
            {
                UserPassword = invalidUser ? invalid : "user",
                OwnerPassword = invalidUser ? "owner" : invalid
            })
            .AddBlankPage();

        ArgumentException error = Assert.Throws<ArgumentException>(() => builder.Build());

        Assert.Contains("Unicode scalar", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Revision6PasswordPreparationRemovesSaslPrepTableB1Characters()
    {
        byte[] bytes = new PdfDocumentBuilder()
            .SetPasswordEncryption(new PdfPasswordEncryptionOptions
            {
                UserPassword = "pass\u00ADword",
                OwnerPassword = "owner\uFEFFpassword"
            })
            .AddBlankPage()
            .Build();

        Assert.True(PdfDocument.Open(bytes, "password").IsDecrypted);
        Assert.True(PdfDocument.Open(bytes, "ownerpassword").IsDecrypted);
    }

    [Fact]
    public void Revision6PasswordPreparationMapsSpacesAndAppliesCompatibilityNormalization()
    {
        byte[] bytes = new PdfDocumentBuilder()
            .SetPasswordEncryption(new PdfPasswordEncryptionOptions
            {
                UserPassword = "p\u00A0\u00AAs",
                OwnerPassword = "owner"
            })
            .AddBlankPage()
            .Build();

        Assert.True(PdfDocument.Open(bytes, "p as").IsDecrypted);
    }

    [Theory]
    [InlineData("password\u0007")]
    [InlineData("password\uE000")]
    [InlineData("password\uFDD0")]
    [InlineData("password\u202E")]
    public void Authoring_RejectsSaslPrepProhibitedPasswordCharacters(string password)
    {
        var builder = new PdfDocumentBuilder()
            .SetPasswordEncryption(new PdfPasswordEncryptionOptions
            {
                UserPassword = password,
                OwnerPassword = "owner"
            })
            .AddBlankPage();

        ArgumentException error = Assert.Throws<ArgumentException>(() => builder.Build());

        Assert.Contains("prohibited SASLprep", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\u05D0Latin\u05D1", "mix RandALCat and LCat")]
    [InlineData("1\u05D0", "begin and end")]
    [InlineData("\u05D01", "begin and end")]
    public void Authoring_RejectsInvalidSaslPrepBidirectionalPasswords(
        string password,
        string expectedMessage)
    {
        var builder = new PdfDocumentBuilder()
            .SetPasswordEncryption(new PdfPasswordEncryptionOptions
            {
                UserPassword = password,
                OwnerPassword = "owner"
            })
            .AddBlankPage();

        ArgumentException error = Assert.Throws<ArgumentException>(() => builder.Build());

        Assert.Contains(expectedMessage, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Authoring_AcceptsSaslPrepBidirectionalPasswordWithRandAlAtBothEnds()
    {
        const string password = "\u05D01\u05D1";
        byte[] bytes = new PdfDocumentBuilder()
            .SetPasswordEncryption(new PdfPasswordEncryptionOptions
            {
                UserPassword = password,
                OwnerPassword = "owner"
            })
            .AddBlankPage()
            .Build();

        Assert.True(PdfDocument.Open(bytes, password).IsDecrypted);
    }

    [Fact]
    public void Authoring_RejectsUnicode32UnassignedPasswordCharacters()
    {
        const string emojiAssignedAfterUnicode32 = "password\U0001F600";
        var builder = new PdfDocumentBuilder()
            .SetPasswordEncryption(new PdfPasswordEncryptionOptions
            {
                UserPassword = emojiAssignedAfterUnicode32,
                OwnerPassword = "owner"
            })
            .AddBlankPage();

        ArgumentException error = Assert.Throws<ArgumentException>(() => builder.Build());

        Assert.Contains("Unicode 3.2 unassigned", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Authoring_WritesTypedPermissionBits()
    {
        byte[] bytes = new PdfDocumentBuilder()
            .SetPasswordEncryption(new PdfPasswordEncryptionOptions
            {
                UserPassword = "user",
                OwnerPassword = "owner",
                AllowContentCopying = false,
                AllowDocumentModification = false,
                AllowAnnotationModification = false,
                AllowFormFilling = false,
                AllowDocumentAssembly = false,
                AllowHighQualityPrinting = false
            }).AddBlankPage().Build();
        PdfDocument document = PdfDocument.Open(bytes);
        Assert.Equal(PdfPasswordAuthenticationRole.None,
            document.PasswordAuthenticationRole);
        Assert.Null(document.DeclaredPermissions);
        Assert.True(document.CrossReferences.TryGetTrailerValue(
            new PdfName("Encrypt"u8), out PdfObject encryptionReference));
        PdfDictionary encryption = Assert.IsType<PdfDictionary>(
            document.Resolve(Assert.IsType<PdfIndirectReference>(encryptionReference)));
        int permissions = checked((int)Assert.IsType<PdfInteger>(
            encryption[new PdfName("P"u8)]).Value);

        Assert.NotEqual(0, permissions & (1 << 2));
        Assert.Equal(0, permissions & (1 << 3));
        Assert.Equal(0, permissions & (1 << 4));
        Assert.Equal(0, permissions & (1 << 5));
        Assert.Equal(0, permissions & (1 << 8));
        Assert.NotEqual(0, permissions & (1 << 9));
        Assert.Equal(0, permissions & (1 << 10));
        Assert.Equal(0, permissions & (1 << 11));
    }

    [Fact]
    public void Authoring_RejectsHighQualityPrintingWhenPrintingIsDisabled()
    {
        var builder = new PdfDocumentBuilder()
            .SetPasswordEncryption(new PdfPasswordEncryptionOptions
            {
                UserPassword = "user",
                OwnerPassword = "owner",
                AllowLowQualityPrinting = false,
                AllowHighQualityPrinting = true
            })
            .AddBlankPage();

        ArgumentException error = Assert.Throws<ArgumentException>(() => builder.Build());

        Assert.Contains("printing is disabled", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("user", PdfPasswordAuthenticationRole.User)]
    [InlineData("owner", PdfPasswordAuthenticationRole.Owner)]
    public void Open_ExposesAuthenticationRoleAndDeclaredPermissions(
        string password, PdfPasswordAuthenticationRole expectedRole)
    {
        byte[] bytes = new PdfDocumentBuilder()
            .SetPasswordEncryption(new PdfPasswordEncryptionOptions
            {
                UserPassword = "user",
                OwnerPassword = "owner",
                AllowLowQualityPrinting = true,
                AllowDocumentModification = false,
                AllowContentCopying = false,
                AllowAnnotationModification = false,
                AllowFormFilling = false,
                AllowAccessibilityExtraction = true,
                AllowDocumentAssembly = false,
                AllowHighQualityPrinting = false
            })
            .AddBlankPage()
            .Build();

        PdfDocument document = PdfDocument.Open(bytes, password);
        PdfDocumentPermissions permissions = Assert.IsType<PdfDocumentPermissions>(
            document.DeclaredPermissions);

        Assert.Equal(expectedRole, document.PasswordAuthenticationRole);
        Assert.True(permissions.AllowLowQualityPrinting);
        Assert.False(permissions.AllowDocumentModification);
        Assert.False(permissions.AllowContentCopying);
        Assert.False(permissions.AllowAnnotationModification);
        Assert.False(permissions.AllowFormFilling);
        Assert.True(permissions.AllowAccessibilityExtraction);
        Assert.False(permissions.AllowDocumentAssembly);
        Assert.False(permissions.AllowHighQualityPrinting);
    }

    [Fact]
    public void UserPasswordPermissions_RestrictPageAndAnnotationEditorsWhileOwnerBypasses()
    {
        byte[] bytes = RestrictedEditingDocument(
            allowModification: false, allowAssembly: false, allowAnnotations: false);
        PdfDocument user = PdfDocument.Open(bytes, "user");

        InvalidOperationException assemblyError = Assert.Throws<InvalidOperationException>(() =>
            new PdfIncrementalPageEditor(user).AddBlankPage().Build());
        InvalidOperationException modificationError = Assert.Throws<InvalidOperationException>(() =>
            new PdfIncrementalPageEditor(user).SetCropBox(0, 0, 0, 300, 400).Build());
        InvalidOperationException annotationError = Assert.Throws<InvalidOperationException>(() =>
            new PdfIncrementalAnnotationEditor(user)
                .AddTextNote(0, 10, 10, "restricted").Build());
        InvalidOperationException rewriteError = Assert.Throws<InvalidOperationException>(() =>
            PdfDocumentWriter.Write(user));

        Assert.Contains("assembly", assemblyError.Message, StringComparison.Ordinal);
        Assert.Contains("page-geometry modification", modificationError.Message, StringComparison.Ordinal);
        Assert.Contains("annotation modification", annotationError.Message, StringComparison.Ordinal);
        Assert.Contains("full document rewrite", rewriteError.Message, StringComparison.Ordinal);
        Assert.NotEmpty(new PdfIncrementalPageEditor(PdfDocument.Open(bytes, "owner"))
            .AddBlankPage().Build());
        Assert.NotEmpty(new PdfIncrementalPageEditor(PdfDocument.Open(bytes, "owner"))
            .SetCropBox(0, 0, 0, 300, 400).Build());
        Assert.NotEmpty(new PdfIncrementalAnnotationEditor(PdfDocument.Open(bytes, "owner"))
            .AddTextNote(0, 10, 10, "owner edit").Build());
        Assert.NotEmpty(PdfDocumentWriter.Write(PdfDocument.Open(bytes, "owner")));
    }

    [Fact]
    public void UserPasswordPermissions_HonorSpecializedAssemblyAndAnnotationGrants()
    {
        byte[] bytes = RestrictedEditingDocument(
            allowModification: false, allowAssembly: true, allowAnnotations: true);

        Assert.NotEmpty(new PdfIncrementalPageEditor(PdfDocument.Open(bytes, "user"))
            .RotateClockwise(0).Build());
        Assert.NotEmpty(new PdfIncrementalAnnotationEditor(PdfDocument.Open(bytes, "user"))
            .AddTextNote(0, 10, 10, "permitted").Build());
        Assert.Throws<InvalidOperationException>(() =>
            new PdfIncrementalPageEditor(PdfDocument.Open(bytes, "user"))
                .SetMediaBox(0, 0, 0, 300, 400).Build());
    }

    [Fact]
    public void UserPasswordPermissions_RestrictPageImportFromSourceWhileOwnerBypasses()
    {
        byte[] restrictedSource = new PdfDocumentBuilder()
            .SetPasswordEncryption(new PdfPasswordEncryptionOptions
            {
                UserPassword = "user",
                OwnerPassword = "owner",
                AllowContentCopying = false
            })
            .AddBlankPage()
            .Build();
        PdfDocument target = PdfDocument.Open(new PdfDocumentBuilder().Build());

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            new PdfIncrementalPageEditor(target)
                .AddImportedDocument(PdfDocument.Open(restrictedSource, "user")));
        byte[] imported = new PdfIncrementalPageEditor(target)
            .AddImportedDocument(PdfDocument.Open(restrictedSource, "owner"))
            .Build();

        Assert.Contains("copying page content", error.Message, StringComparison.Ordinal);
        Assert.NotEmpty(imported);
    }

    [Theory]
    [InlineData("user-password")]
    [InlineData("owner-password")]
    public void Open_AuthenticatesAndDecryptsRevision6Aes256Streams(string password)
    {
        PdfDocument document = PdfDocument.Open(Revision6Fixture(), password);
        PdfStream stream = Assert.IsType<PdfStream>(document.Resolve(4));

        Assert.True(document.IsEncrypted);
        Assert.True(document.IsDecrypted);
        Assert.Equal(
            "q\n0.9 0.2 0.4 rg\n72 72 200 100 re\nf\nQ\n",
            Encoding.ASCII.GetString(PdfStreamDecoder.Decode(stream)));
    }

    [Fact]
    public void Open_RejectsWrongRevision6Password()
    {
        Assert.ThrowsAny<CryptographicException>(() =>
            PdfDocument.Open(Revision6Fixture(), "wrong-password"));
    }

    [Theory]
    [InlineData("gcm-user")]
    [InlineData("gcm-owner")]
    public void AuthoredRevision7AesGcmDocumentRoundTrips(string password)
    {
        byte[] source = new PdfDocumentBuilder()
            .SetPasswordEncryption(new PdfPasswordEncryptionOptions
            {
                UserPassword = "gcm-user",
                OwnerPassword = "gcm-owner",
                Algorithm = PdfPasswordEncryptionAlgorithm.Aes256Gcm
            })
            .AddPage(300, 400, new PdfContentStreamBuilder().BeginText()
                .SetFont(PdfStandardFont.Helvetica, 12).MoveText(20, 30)
                .ShowLatin1Text("authenticated content").EndText())
            .Build();

        PdfDocument document = PdfDocument.Open(source, password);

        Assert.True(document.IsDecrypted);
        Assert.Equal("authenticated content",
            new PdfPageContentReader(document).Read(0).Text);
        PdfDictionary encryption = Assert.IsType<PdfDictionary>(document.Resolve(
            Assert.IsType<PdfIndirectReference>(
                document.Trailer[new PdfName("Encrypt"u8)])));
        Assert.Equal(6, Assert.IsType<PdfInteger>(encryption[new PdfName("V"u8)]).Value);
        Assert.Equal(7, Assert.IsType<PdfInteger>(encryption[new PdfName("R"u8)]).Value);
    }

    [Fact]
    public void AesGcmAuthenticationRejectsChangedCiphertext()
    {
        byte[] source = new PdfDocumentBuilder()
            .SetPasswordEncryption(new PdfPasswordEncryptionOptions
            {
                UserPassword = "gcm-user",
                OwnerPassword = "gcm-owner",
                Algorithm = PdfPasswordEncryptionAlgorithm.Aes256Gcm
            })
            .AddPage(300, 400, new PdfContentStreamBuilder()
                .MoveTo(10, 10).LineTo(20, 20).Stroke())
            .Build();
        PdfDocument raw = PdfDocument.Open(source);
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(raw.Resolve(
            Assert.IsType<PdfIndirectReference>(raw.Trailer[new PdfName("Root"u8)])));
        PdfIndirectReference pagesReference = Assert.IsType<PdfIndirectReference>(
            catalog[new PdfName("Pages"u8)]);
        PdfDictionary pages = Assert.IsType<PdfDictionary>(raw.Resolve(pagesReference));
        PdfIndirectReference pageReference = Assert.IsType<PdfIndirectReference>(
            Assert.IsType<PdfArray>(pages[new PdfName("Kids"u8)])[0]);
        PdfDictionary page = Assert.IsType<PdfDictionary>(raw.Resolve(pageReference));
        PdfIndirectReference contentsReference = Assert.IsType<PdfIndirectReference>(
            page[new PdfName("Contents"u8)]);
        PdfStream encryptedContents = Assert.IsType<PdfStream>(raw.Resolve(contentsReference));
        int streamOffset = source.AsSpan().IndexOf(encryptedContents.EncodedData.Span);
        Assert.True(streamOffset >= 0);
        byte[] tampered = source.ToArray();
        tampered[streamOffset + encryptedContents.EncodedData.Length - 1] ^= 1;

        PdfDocument reopened = PdfDocument.Open(tampered, "gcm-owner");
        Assert.ThrowsAny<CryptographicException>(() => reopened.Resolve(contentsReference));
    }

    [Theory]
    [InlineData("user")]
    [InlineData("owner")]
    public void OpenWithCompatibilityRecovery_IgnoresTrailingRevision6PasswordRecordBytes(
        string password)
    {
        (PdfDocument document, PdfIndirectReference encryptionReference,
            PdfDictionary encryption, _, _) = AuthoredEncryptionDictionary();
        var recoveredEncryption = new PdfDictionary(encryption.Select(entry =>
            entry.Key.Equals(new PdfName("O"u8)) || entry.Key.Equals(new PdfName("U"u8))
                ? new KeyValuePair<PdfName, PdfObject>(entry.Key,
                    new PdfString(
                        [.. Assert.IsType<PdfString>(entry.Value).Bytes.Span, 1, 2, 3],
                        PdfStringForm.Hexadecimal))
                : entry));
        byte[] source = new PdfIncrementalUpdateBuilder(document)
            .ReplaceObject(encryptionReference.ObjectNumber, recoveredEncryption)
            .Build();

        Assert.Throws<InvalidOperationException>(() => PdfDocument.Open(source, password));
        PdfDocument reopened = PdfDocument.OpenWithCompatibilityRecovery(source, password);

        Assert.True(reopened.IsDecrypted);
        Assert.NotEqual(PdfPasswordAuthenticationRole.None,
            reopened.PasswordAuthenticationRole);
    }

    [Fact]
    public void OpenWithCompatibilityRecovery_AcceptsUnsignedPermissionInteger()
    {
        PdfDocument document = PdfDocument.Open(Revision4Fixture(), "owner-password");
        PdfIndirectReference encryptionReference = Assert.IsType<PdfIndirectReference>(
            document.Trailer[new PdfName("Encrypt"u8)]);
        PdfDictionary encryption = Assert.IsType<PdfDictionary>(
            document.Resolve(encryptionReference));
        var recoveredEncryption = new PdfDictionary(encryption.Select(entry =>
            entry.Key.Equals(new PdfName("P"u8))
                ? new KeyValuePair<PdfName, PdfObject>(entry.Key,
                    new PdfInteger(uint.MaxValue - 3L))
                : entry));
        byte[] source = new PdfIncrementalUpdateBuilder(document)
            .ReplaceObject(encryptionReference.ObjectNumber, recoveredEncryption)
            .Build();

        Assert.Throws<OverflowException>(() =>
            PdfDocument.Open(source, "owner-password"));
        PdfDocument reopened = PdfDocument.OpenWithCompatibilityRecovery(
            source, "owner-password");

        Assert.True(reopened.IsDecrypted);
        Assert.Equal(PdfPasswordAuthenticationRole.Owner,
            reopened.PasswordAuthenticationRole);
    }

    [Fact]
    public void OpenWithCompatibilityRecovery_AcceptsShortRevision4UserRecord()
    {
        PdfDocument document = PdfDocument.Open(Revision4Fixture(), "owner-password");
        PdfIndirectReference encryptionReference = Assert.IsType<PdfIndirectReference>(
            document.Trailer[new PdfName("Encrypt"u8)]);
        PdfDictionary encryption = Assert.IsType<PdfDictionary>(
            document.Resolve(encryptionReference));
        var recoveredEncryption = new PdfDictionary(encryption.Select(entry =>
            entry.Key.Equals(new PdfName("U"u8))
                ? new KeyValuePair<PdfName, PdfObject>(entry.Key,
                    new PdfString(Assert.IsType<PdfString>(entry.Value).Bytes.Span[..16],
                        PdfStringForm.Hexadecimal))
                : entry));
        byte[] source = new PdfIncrementalUpdateBuilder(document)
            .ReplaceObject(encryptionReference.ObjectNumber, recoveredEncryption)
            .Build();

        Assert.Throws<InvalidOperationException>(() =>
            PdfDocument.Open(source, "owner-password"));
        Assert.True(PdfDocument.OpenWithCompatibilityRecovery(
            source, "owner-password").IsDecrypted);
    }

    [Fact]
    public void OpenWithCompatibilityRecovery_AcceptsMisspelledLegacyKeyLength()
    {
        PdfDocument document = PdfDocument.Open(Revision4Fixture(), "owner-password");
        PdfIndirectReference encryptionReference = Assert.IsType<PdfIndirectReference>(
            document.Trailer[new PdfName("Encrypt"u8)]);
        PdfDictionary encryption = Assert.IsType<PdfDictionary>(
            document.Resolve(encryptionReference));
        var recoveredEncryption = new PdfDictionary(encryption.Select(entry =>
            entry.Key.Equals(new PdfName("Length"u8))
                ? new KeyValuePair<PdfName, PdfObject>(new PdfName("Wength"u8), entry.Value)
                : entry));
        byte[] source = new PdfIncrementalUpdateBuilder(document)
            .ReplaceObject(encryptionReference.ObjectNumber, recoveredEncryption)
            .Build();

        Assert.ThrowsAny<CryptographicException>(() =>
            PdfDocument.Open(source, "owner-password"));
        Assert.True(PdfDocument.OpenWithCompatibilityRecovery(
            source, "owner-password").IsDecrypted);
    }

    [Fact]
    public void OpenWithCompatibilityRecovery_AcceptsDirectEncryptionDictionary()
    {
        string source = Encoding.Latin1.GetString(Revision6Fixture());
        int objectStart = source.IndexOf("5 0 obj\n", StringComparison.Ordinal);
        int dictionaryStart = source.IndexOf("<<", objectStart, StringComparison.Ordinal);
        int dictionaryEnd = source.IndexOf("\nendobj", dictionaryStart, StringComparison.Ordinal);
        string encryption = source[dictionaryStart..dictionaryEnd];
        source = source.Replace("/Encrypt 5 0 R", $"/Encrypt {encryption}",
            StringComparison.Ordinal);
        byte[] bytes = Encoding.Latin1.GetBytes(source);

        Assert.Throws<InvalidOperationException>(() =>
            PdfDocument.Open(bytes, "owner-password"));
        Assert.True(PdfDocument.OpenWithCompatibilityRecovery(
            bytes, "owner-password").IsDecrypted);
    }

    [Fact]
    public void Open_ResolvesMultiHopEncryptionDictionaryReferences()
    {
        byte[] source = AppendEncryptionReferenceRevision(
            Revision6Fixture(), "6 0 obj 7 0 R endobj\n7 0 obj 5 0 R endobj\n",
            "6 0 R", 8);

        PdfDocument document = PdfDocument.Open(source, "owner-password");

        Assert.True(document.IsDecrypted);
        Assert.Equal(PdfPasswordAuthenticationRole.Owner,
            document.PasswordAuthenticationRole);

        byte[] rewritten = PdfDocumentWriter.Write(document,
            new PdfDocumentWriteOptions
            {
                CrossReferenceFormat = PdfCrossReferenceFormat.Stream,
                UseObjectStreams = true,
                CompressStructuralStreams = true
            });
        PdfDocument reopened = PdfDocument.Open(rewritten, "user-password");
        Assert.True(reopened.IsDecrypted);
        Assert.Equal(PdfCrossReferenceEntryType.InUse,
            reopened.CrossReferences[6].Type);
        Assert.Equal(PdfCrossReferenceEntryType.InUse,
            reopened.CrossReferences[7].Type);

        byte[] incremented = new PdfIncrementalUpdateBuilder(document)
            .ReplaceObject(6, new PdfIndirectReference(5, 0))
            .Build(new PdfIncrementalUpdateWriteOptions
            {
                CrossReferenceFormat = PdfCrossReferenceFormat.Stream,
                UseObjectStreams = true,
                CompressObjectStreams = true,
                CompressCrossReferenceStream = true
            });
        PdfDocument incrementedDocument = PdfDocument.Open(
            incremented, "owner-password");
        Assert.True(incrementedDocument.IsDecrypted);
        Assert.Equal(PdfCrossReferenceEntryType.InUse,
            incrementedDocument.CrossReferences[6].Type);

        var invalidUpdate = new PdfIncrementalUpdateBuilder(document);
        invalidUpdate.FreeObject(7);
        InvalidOperationException freeError = Assert.Throws<InvalidOperationException>(
            () => invalidUpdate.Build());
        Assert.Contains("trailer /Encrypt", freeError.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Open_RejectsCyclicEncryptionDictionaryReferences()
    {
        byte[] source = AppendEncryptionReferenceRevision(
            Revision6Fixture(), "6 0 obj 7 0 R endobj\n7 0 obj 6 0 R endobj\n",
            "6 0 R", 8);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => PdfDocument.Open(source, "owner-password"));

        Assert.Contains("cycle", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Open_RejectsInvalidReservedPermissionBitsAcrossSecurityRevisions()
    {
        foreach ((byte[] fixture, string password) in new[]
                 {
                     (Revision2Fixture(), "owner-password"),
                     (Revision4Fixture(), "owner-password"),
                     (Revision6Fixture(), "owner-password")
                 })
        {
            int offset = fixture.AsSpan().IndexOf("/P -4"u8);
            Assert.True(offset >= 0);
            fixture[offset + 4] = (byte)'3';

            InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
                PdfDocument.Open(fixture, password));

            Assert.Contains("reserved permission bits", error.Message,
                StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("StrF")]
    [InlineData("StmF")]
    public void StandardSecurity_DefaultsOmittedCryptFilterSelectorsToIdentity(string selector)
    {
        byte[] source = new PdfDocumentBuilder()
            .SetPasswordEncryption(new PdfPasswordEncryptionOptions
            {
                UserPassword = "user",
                OwnerPassword = "owner"
            })
            .AddBlankPage()
            .Build();
        PdfDocument document = PdfDocument.Open(source, "owner");
        PdfIndirectReference encryptionReference = Assert.IsType<PdfIndirectReference>(
            document.Trailer[new PdfName("Encrypt"u8)]);
        PdfDictionary authored = Assert.IsType<PdfDictionary>(
            document.Resolve(encryptionReference));
        PdfName selectorName = new(Encoding.ASCII.GetBytes(selector));
        var omitted = new PdfDictionary(authored.Where(entry =>
            !entry.Key.Equals(selectorName)));
        byte[] updated = new PdfIncrementalUpdateBuilder(document)
            .ReplaceObject(encryptionReference.ObjectNumber, omitted)
            .Build();

        PdfDocument reopened = PdfDocument.Open(updated, "owner");

        Assert.True(reopened.IsDecrypted);
        Assert.IsType<PdfDictionary>(reopened.Resolve(
            Assert.IsType<PdfIndirectReference>(reopened.Trailer[new PdfName("Root"u8)])));
    }

    [Fact]
    public void LegacyStandardSecurity_DefaultsOmittedStringCryptFilterToIdentity()
    {
        PdfDocument document = PdfDocument.Open(Revision4Fixture(), "owner-password");
        PdfIndirectReference encryptionReference = Assert.IsType<PdfIndirectReference>(
            document.Trailer[new PdfName("Encrypt"u8)]);
        PdfDictionary encryption = Assert.IsType<PdfDictionary>(
            document.Resolve(encryptionReference));
        var omitted = new PdfDictionary(encryption.Where(entry =>
            !entry.Key.Equals(new PdfName("StrF"u8))));
        byte[] updated = new PdfIncrementalUpdateBuilder(document)
            .ReplaceObject(encryptionReference.ObjectNumber, omitted)
            .Build();

        PdfDocument reopened = PdfDocument.Open(updated, "owner-password");
        PdfStream stream = Assert.IsType<PdfStream>(reopened.Resolve(4));

        Assert.Equal(
            "q\n0.9 0.2 0.4 rg\n72 72 200 100 re\nf\nQ\n",
            Encoding.ASCII.GetString(PdfStreamDecoder.Decode(stream)));
    }

    [Theory]
    [InlineData("StrF")]
    [InlineData("StmF")]
    public void StandardSecurity_RejectsMalformedExplicitCryptFilterSelectors(string selector)
    {
        byte[] source = new PdfDocumentBuilder()
            .SetPasswordEncryption(new PdfPasswordEncryptionOptions
            {
                UserPassword = "user",
                OwnerPassword = "owner"
            })
            .AddBlankPage()
            .Build();
        PdfDocument document = PdfDocument.Open(source, "owner");
        PdfIndirectReference encryptionReference = Assert.IsType<PdfIndirectReference>(
            document.Trailer[new PdfName("Encrypt"u8)]);
        PdfDictionary authored = Assert.IsType<PdfDictionary>(
            document.Resolve(encryptionReference));
        PdfName selectorName = new(Encoding.ASCII.GetBytes(selector));
        var malformed = new PdfDictionary(authored.Select(entry =>
            entry.Key.Equals(selectorName)
                ? new KeyValuePair<PdfName, PdfObject>(entry.Key, new PdfInteger(1))
                : entry));
        byte[] updated = new PdfIncrementalUpdateBuilder(document)
            .ReplaceObject(encryptionReference.ObjectNumber, malformed)
            .Build();

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            PdfDocument.Open(updated, "owner"));

        Assert.Contains($"/{selector}", error.Message, StringComparison.Ordinal);
        Assert.Contains("not a name", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void StandardSecurity_DefaultsOmittedCryptFilterMethodToNone()
    {
        (PdfDocument document, PdfIndirectReference encryptionReference,
            PdfDictionary encryption, PdfName filterName, PdfDictionary filter) =
            AuthoredEncryptionDictionary();
        var defaultMethodFilter = new PdfDictionary(filter.Where(entry =>
            !entry.Key.Equals(new PdfName("CFM"u8))));
        PdfDictionary updatedEncryption = ReplaceCryptFilter(
            encryption, filterName, defaultMethodFilter);
        byte[] updated = new PdfIncrementalUpdateBuilder(document)
            .ReplaceObject(encryptionReference.ObjectNumber, updatedEncryption)
            .Build();

        Assert.True(PdfDocument.Open(updated, "owner").IsDecrypted);
    }

    [Fact]
    public void StandardSecurity_RejectsMalformedExplicitCryptFilterMethod()
    {
        (PdfDocument document, PdfIndirectReference encryptionReference,
            PdfDictionary encryption, PdfName filterName, PdfDictionary filter) =
            AuthoredEncryptionDictionary();
        var malformedFilter = new PdfDictionary(filter.Select(entry =>
            entry.Key.Equals(new PdfName("CFM"u8))
                ? new KeyValuePair<PdfName, PdfObject>(entry.Key, new PdfInteger(1))
                : entry));
        PdfDictionary updatedEncryption = ReplaceCryptFilter(
            encryption, filterName, malformedFilter);
        byte[] updated = new PdfIncrementalUpdateBuilder(document)
            .ReplaceObject(encryptionReference.ObjectNumber, updatedEncryption)
            .Build();

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            PdfDocument.Open(updated, "owner"));

        Assert.Contains("/CFM value is not a name", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Open_RejectsNonBooleanRevision6EncryptMetadataValue()
    {
        byte[] source = new PdfDocumentBuilder()
            .SetPasswordEncryption(new PdfPasswordEncryptionOptions
            {
                UserPassword = "user",
                OwnerPassword = "owner"
            })
            .AddBlankPage()
            .Build();
        PdfDocument document = PdfDocument.Open(source, "owner");
        PdfIndirectReference encryptionReference = Assert.IsType<PdfIndirectReference>(
            document.Trailer[new PdfName("Encrypt"u8)]);
        PdfDictionary encryption = Assert.IsType<PdfDictionary>(
            document.Resolve(encryptionReference));
        var malformed = new PdfDictionary(encryption.Select(entry =>
            entry.Key.Equals(new PdfName("EncryptMetadata"u8))
                ? new KeyValuePair<PdfName, PdfObject>(
                    entry.Key, new PdfName("Invalid"u8))
                : entry));
        var update = new PdfIncrementalUpdateBuilder(document)
            .ReplaceObject(encryptionReference.ObjectNumber, malformed);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            PdfDocument.Open(update.Build(), "owner"));

        Assert.Contains("not boolean", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Open_RejectsNonBooleanLegacyEncryptMetadataValue()
    {
        PdfDocument document = PdfDocument.Open(Revision4Fixture(), "owner-password");
        PdfIndirectReference encryptionReference = Assert.IsType<PdfIndirectReference>(
            document.Trailer[new PdfName("Encrypt"u8)]);
        PdfDictionary encryption = Assert.IsType<PdfDictionary>(
            document.Resolve(encryptionReference));
        var malformed = new PdfDictionary(encryption.Append(
            new KeyValuePair<PdfName, PdfObject>(
                new PdfName("EncryptMetadata"u8), new PdfName("Invalid"u8))));
        var update = new PdfIncrementalUpdateBuilder(document)
            .ReplaceObject(encryptionReference.ObjectNumber, malformed);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            PdfDocument.Open(update.Build(), "owner-password"));

        Assert.Contains("not boolean", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Open_RejectsInvalidAes256CryptFilterLength()
    {
        byte[] source = new PdfDocumentBuilder()
            .SetPasswordEncryption(new PdfPasswordEncryptionOptions
            {
                UserPassword = "user",
                OwnerPassword = "owner"
            })
            .AddBlankPage()
            .Build();
        PdfDocument document = PdfDocument.Open(source, "owner");
        PdfIndirectReference encryptionReference = Assert.IsType<PdfIndirectReference>(
            document.Trailer[new PdfName("Encrypt"u8)]);
        PdfDictionary encryption = Assert.IsType<PdfDictionary>(
            document.Resolve(encryptionReference));
        PdfDictionary filters = Assert.IsType<PdfDictionary>(
            encryption[new PdfName("CF"u8)]);
        PdfName filterName = Assert.IsType<PdfName>(encryption[new PdfName("StmF"u8)]);
        PdfDictionary filter = Assert.IsType<PdfDictionary>(filters[filterName]);
        var malformedFilter = new PdfDictionary(filter.Select(entry =>
            entry.Key.Equals(new PdfName("Length"u8))
                ? new KeyValuePair<PdfName, PdfObject>(entry.Key, new PdfInteger(31))
                : entry));
        var malformedFilters = new PdfDictionary(filters.Select(entry =>
            entry.Key.Equals(filterName)
                ? new KeyValuePair<PdfName, PdfObject>(entry.Key, malformedFilter)
                : entry));
        var malformedEncryption = new PdfDictionary(encryption.Select(entry =>
            entry.Key.Equals(new PdfName("CF"u8))
                ? new KeyValuePair<PdfName, PdfObject>(entry.Key, malformedFilters)
                : entry));
        var update = new PdfIncrementalUpdateBuilder(document)
            .ReplaceObject(encryptionReference.ObjectNumber, malformedEncryption);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            PdfDocument.Open(update.Build(), "owner"));

        Assert.Contains("invalid /Length", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Open_RejectsEmbeddedFileAuthenticationEventForStringAndStreamFilters()
    {
        byte[] source = new PdfDocumentBuilder()
            .SetPasswordEncryption(new PdfPasswordEncryptionOptions
            {
                UserPassword = "user",
                OwnerPassword = "owner"
            })
            .AddBlankPage()
            .Build();
        PdfDocument document = PdfDocument.Open(source, "owner");
        PdfIndirectReference encryptionReference = Assert.IsType<PdfIndirectReference>(
            document.Trailer[new PdfName("Encrypt"u8)]);
        PdfDictionary encryption = Assert.IsType<PdfDictionary>(
            document.Resolve(encryptionReference));
        PdfDictionary filters = Assert.IsType<PdfDictionary>(
            encryption[new PdfName("CF"u8)]);
        PdfName filterName = Assert.IsType<PdfName>(encryption[new PdfName("StmF"u8)]);
        PdfDictionary filter = Assert.IsType<PdfDictionary>(filters[filterName]);
        var malformedFilter = new PdfDictionary(filter.Select(entry =>
            entry.Key.Equals(new PdfName("AuthEvent"u8))
                ? new KeyValuePair<PdfName, PdfObject>(
                    entry.Key, new PdfName("EFOpen"u8))
                : entry));
        var malformedFilters = new PdfDictionary(filters.Select(entry =>
            entry.Key.Equals(filterName)
                ? new KeyValuePair<PdfName, PdfObject>(entry.Key, malformedFilter)
                : entry));
        var malformedEncryption = new PdfDictionary(encryption.Select(entry =>
            entry.Key.Equals(new PdfName("CF"u8))
                ? new KeyValuePair<PdfName, PdfObject>(entry.Key, malformedFilters)
                : entry));
        var update = new PdfIncrementalUpdateBuilder(document)
            .ReplaceObject(encryptionReference.ObjectNumber, malformedEncryption);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            PdfDocument.Open(update.Build(), "owner"));

        Assert.Contains("cannot use /EFOpen", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Open_AuthenticatesLegacyRc4AndAes128SecurityHandlers()
    {
        foreach (byte[] fixture in new[] { Revision2Fixture(), Revision4Fixture() })
        {
            foreach (string password in new[] { "user-password", "owner-password" })
            {
                PdfDocument document = PdfDocument.Open(fixture, password);
                PdfStream stream = Assert.IsType<PdfStream>(document.Resolve(4));
                Assert.Equal(password.StartsWith("owner", StringComparison.Ordinal)
                        ? PdfPasswordAuthenticationRole.Owner
                        : PdfPasswordAuthenticationRole.User,
                    document.PasswordAuthenticationRole);
                Assert.NotNull(document.DeclaredPermissions);
                Assert.Equal(
                    "q\n0.9 0.2 0.4 rg\n72 72 200 100 re\nf\nQ\n",
                    Encoding.ASCII.GetString(PdfStreamDecoder.Decode(stream)));
            }
        }
    }

    [Theory]
    [InlineData("user€")]
    [InlineData("owner€")]
    public void Open_AuthenticatesQpdfLegacyPasswordsUsingPdfDocEncoding(string password)
    {
        PdfDocument document = PdfDocument.Open(PdfDocEncodingRevision4Fixture(), password);
        PdfStream stream = Assert.IsType<PdfStream>(document.Resolve(4));

        Assert.True(document.IsDecrypted);
        Assert.Equal(
            "q\n0.9 0.2 0.4 rg\n72 72 200 100 re\nf\nQ\n",
            Encoding.ASCII.GetString(PdfStreamDecoder.Decode(stream)));
    }

    [Fact]
    public void Open_RejectsLegacyPasswordCharactersOutsidePdfDocEncoding()
    {
        ArgumentException error = Assert.Throws<ArgumentException>(() =>
            PdfDocument.Open(PdfDocEncodingRevision4Fixture(), "password😀"));

        Assert.Contains("PDFDocEncoding", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Open_RejectsNonByteAlignedLegacyEncryptionKeyLength()
    {
        byte[] fixture = Revision4Fixture();
        int lengthOffset = fixture.AsSpan().IndexOf("/Length 128"u8);
        Assert.True(lengthOffset >= 0);
        fixture[lengthOffset + "/Length 12".Length] = (byte)'7';

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            PdfDocument.Open(fixture, "owner-password"));

        Assert.Contains("byte-aligned", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Open_DefaultsOmittedLegacyEncryptionKeyLengthToFortyBits()
    {
        byte[] fixture = Revision4Fixture();
        int offset = fixture.AsSpan().IndexOf("/Length 128"u8);
        Assert.True(offset >= 0);
        fixture.AsSpan(offset, "/Length 128".Length).Fill((byte)' ');

        CryptographicException error = Assert.ThrowsAny<CryptographicException>(() =>
            PdfDocument.Open(fixture, "owner-password"));

        Assert.Contains("password is incorrect", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/V 4", "/V 1")]
    [InlineData("/R 4", "/R 3")]
    public void Open_RejectsIncompatibleLegacySecurityVersionAndRevision(
        string original,
        string replacement)
    {
        byte[] fixture = Revision4Fixture();
        int offset = fixture.AsSpan().IndexOf(Encoding.ASCII.GetBytes(original));
        Assert.True(offset >= 0);
        Encoding.ASCII.GetBytes(replacement).CopyTo(fixture.AsSpan(offset));

        NotSupportedException error = Assert.Throws<NotSupportedException>(() =>
            PdfDocument.Open(fixture, "owner-password"));

        Assert.Contains("not supported", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Open_ReportsEncryptedDocumentWhenNoPasswordWasSupplied()
    {
        PdfDocument document = PdfDocument.Open(Revision6Fixture());

        Assert.True(document.IsEncrypted);
        Assert.False(document.IsDecrypted);
    }

    [Fact]
    public void OpenWithCompatibilityRecovery_AuthenticatesMalformedEncryptedDocument()
    {
        byte[] bytes = Revision6Fixture();
        int size = bytes.AsSpan().IndexOf("/Size 6"u8);
        Assert.True(size >= 0);
        bytes[size + "/Size ".Length] = (byte)'0';

        Assert.Throws<PdfSyntaxException>(() =>
            PdfDocument.Open(bytes, "user-password"));
        PdfDocument document = PdfDocument.OpenWithCompatibilityRecovery(
            bytes, "user-password");

        Assert.True(document.IsDecrypted);
        Assert.Equal(PdfPasswordAuthenticationRole.User,
            document.PasswordAuthenticationRole);
        Assert.Single(PdfPageInformation.Read(document));
    }

    [Fact]
    public void IncrementalUpdate_EncryptsNewStringsAndStreams()
    {
        PdfDocument document = PdfDocument.Open(Revision6Fixture(), "user-password");
        var update = new PdfIncrementalUpdateBuilder(document);
        PdfIndirectReference stringReference = update.AddObject(
            new PdfString("secret"u8, PdfStringForm.Literal));
        PdfIndirectReference streamReference = update.AddObject(
            new PdfStream(new PdfDictionary([]), "payload"u8));

        byte[] bytes = update.Build();
        PdfDocument reopened = PdfDocument.Open(bytes, "owner-password");

        Assert.Equal("secret", Encoding.ASCII.GetString(
            Assert.IsType<PdfString>(reopened.Resolve(stringReference)).Bytes.Span));
        Assert.Equal("payload"u8.ToArray(),
            Assert.IsType<PdfStream>(reopened.Resolve(streamReference)).EncodedData.ToArray());
        Assert.Equal(-1, bytes.AsSpan().IndexOf("secret"u8));
        Assert.Equal(-1, bytes.AsSpan().IndexOf("payload"u8));
    }

    [Fact]
    public void IncrementalCrossReferenceStream_RemainsReadableAndEncryptsNewObjects()
    {
        PdfDocument document = PdfDocument.Open(Revision6Fixture(), "user-password");
        var update = new PdfIncrementalUpdateBuilder(document);
        PdfIndirectReference stringReference = update.AddObject(
            new PdfString("stream secret"u8, PdfStringForm.Literal));
        PdfIndirectReference streamReference = update.AddObject(
            new PdfStream(new PdfDictionary([]), "stream payload"u8));

        byte[] bytes = update.Build(new PdfIncrementalUpdateWriteOptions
        {
            CrossReferenceFormat = PdfCrossReferenceFormat.Stream,
            CompressCrossReferenceStream = true
        });
        PdfDocument reopened = PdfDocument.Open(bytes, "owner-password");

        Assert.True(reopened.CrossReferences.Sections[0].IsStream);
        Assert.Equal("stream secret", Encoding.ASCII.GetString(
            Assert.IsType<PdfString>(reopened.Resolve(stringReference)).Bytes.Span));
        Assert.Equal("stream payload"u8.ToArray(),
            Assert.IsType<PdfStream>(reopened.Resolve(streamReference)).EncodedData.ToArray());
        Assert.Equal(-1, bytes.AsSpan().IndexOf("stream secret"u8));
        Assert.Equal(-1, bytes.AsSpan().IndexOf("stream payload"u8));
    }

    [Fact]
    public void IncrementalEncryptedObjectStream_DecryptsPackedObjects()
    {
        PdfDocument document = PdfDocument.Open(Revision6Fixture(), "owner-password");
        var update = new PdfIncrementalUpdateBuilder(document);
        PdfIndirectReference reference = update.AddObject(
            new PdfString("packed secret"u8, PdfStringForm.Literal));

        byte[] bytes = update.Build(new PdfIncrementalUpdateWriteOptions
        {
            CrossReferenceFormat = PdfCrossReferenceFormat.Stream,
            UseObjectStreams = true,
            CompressObjectStreams = true,
            CompressCrossReferenceStream = true
        });
        PdfDocument reopened = PdfDocument.Open(bytes, "user-password");

        Assert.Equal(PdfCrossReferenceEntryType.Compressed,
            reopened.CrossReferences[reference.ObjectNumber].Type);
        Assert.Equal("packed secret", Encoding.ASCII.GetString(
            Assert.IsType<PdfString>(reopened.Resolve(reference)).Bytes.Span));
        Assert.Equal(-1, bytes.AsSpan().IndexOf("packed secret"u8));
    }

    [Fact]
    public void IncrementalEncryptedObjectStream_KeepsPendingEncryptionChainDirectAndClear()
    {
        PdfDocument document = PdfDocument.Open(Revision6Fixture(), "owner-password");
        PdfIndirectReference encryptionReference = Assert.IsType<PdfIndirectReference>(
            document.Trailer[new PdfName("Encrypt"u8)]);
        PdfDictionary encryption = Assert.IsType<PdfDictionary>(
            document.Resolve(encryptionReference));
        var update = new PdfIncrementalUpdateBuilder(document);
        PdfIndirectReference redirectedEncryption = update.AddObject(encryption);
        update.ReplaceObject(encryptionReference.ObjectNumber, redirectedEncryption);

        byte[] bytes = update.Build(new PdfIncrementalUpdateWriteOptions
        {
            CrossReferenceFormat = PdfCrossReferenceFormat.Stream,
            UseObjectStreams = true,
            CompressObjectStreams = true,
            CompressCrossReferenceStream = true
        });
        PdfDocument reopened = PdfDocument.Open(bytes, "user-password");

        Assert.True(reopened.IsDecrypted);
        Assert.Equal(PdfCrossReferenceEntryType.InUse,
            reopened.CrossReferences[redirectedEncryption.ObjectNumber].Type);
        Assert.IsType<PdfDictionary>(reopened.Resolve(redirectedEncryption));
    }

    [Theory]
    [InlineData("Identity", true, false)]
    [InlineData("StdCF", false, false)]
    [InlineData("Identity", true, true)]
    [InlineData("StdCF", false, true)]
    public void ExplicitCryptFilter_SelectsIdentityOrNamedEncryption(
        string cryptFilter, bool remainsCleartext, bool indirectMetadata)
    {
        PdfDocument document = PdfDocument.Open(Revision6Fixture(), "owner-password");
        var update = new PdfIncrementalUpdateBuilder(document);
        PdfObject filterValue = new PdfName("Crypt"u8);
        PdfObject filterName = new PdfName(Encoding.ASCII.GetBytes(cryptFilter));
        if (indirectMetadata)
        {
            filterValue = update.AddObject(update.AddObject(filterValue));
            filterName = update.AddObject(update.AddObject(filterName));
        }
        PdfObject decodeParameters = new PdfDictionary([
            new(new PdfName("Name"u8), filterName)
        ]);
        if (indirectMetadata)
            decodeParameters = update.AddObject(update.AddObject(decodeParameters));
        PdfIndirectReference reference = update.AddObject(new PdfStream(new PdfDictionary([
            new(new PdfName("Filter"u8), filterValue),
            new(new PdfName("DecodeParms"u8), decodeParameters)
        ]), "explicit crypt payload"u8));

        byte[] updated = update.Build();
        PdfDocument reopened = PdfDocument.Open(updated, "user-password");
        PdfStream stream = Assert.IsType<PdfStream>(reopened.Resolve(reference));
        byte[] rewritten = PdfDocumentWriter.Write(reopened);
        PdfDocument rewrittenDocument = PdfDocument.Open(rewritten, "owner-password");
        PdfStream rewrittenStream = Assert.IsType<PdfStream>(
            rewrittenDocument.Resolve(reference.ObjectNumber));

        Assert.Equal("explicit crypt payload",
            Encoding.ASCII.GetString(PdfStreamDecoder.Decode(stream, reopened.Resolve)));
        Assert.Equal("explicit crypt payload",
            Encoding.ASCII.GetString(PdfStreamDecoder.Decode(
                rewrittenStream, rewrittenDocument.Resolve)));
        Assert.Equal(remainsCleartext,
            updated.AsSpan().IndexOf("explicit crypt payload"u8) >= 0);
        Assert.Equal(remainsCleartext,
            rewritten.AsSpan().IndexOf("explicit crypt payload"u8) >= 0);
    }

    [Fact]
    public void ExplicitCryptFilter_MustBeFirstInStreamPipeline()
    {
        PdfDocument document = PdfDocument.Open(Revision6Fixture(), "owner-password");
        var update = new PdfIncrementalUpdateBuilder(document);
        update.AddObject(new PdfStream(new PdfDictionary([
            new(new PdfName("Filter"u8), new PdfArray([
                new PdfName("FlateDecode"u8), new PdfName("Crypt"u8)
            ]))
        ]), "payload"u8));

        Assert.Throws<InvalidOperationException>(() => update.Build());
    }

    [Fact]
    public void ExplicitCryptFilter_RejectsIndirectMetadataCycles()
    {
        PdfDocument document = PdfDocument.Open(Revision6Fixture(), "owner-password");
        var update = new PdfIncrementalUpdateBuilder(document);
        PdfIndirectReference first = update.ReserveObject();
        PdfIndirectReference second = update.ReserveObject();
        update.SetObject(first, second).SetObject(second, first);
        update.AddObject(new PdfStream(new PdfDictionary([
            new(new PdfName("Filter"u8), first)
        ]), "payload"u8));

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => update.Build());

        Assert.Contains("cycle", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitCryptFilter_RejectsNonNameFilterArrayEntries()
    {
        PdfDocument document = PdfDocument.Open(Revision6Fixture(), "owner-password");
        var update = new PdfIncrementalUpdateBuilder(document);
        update.AddObject(new PdfStream(new PdfDictionary([
            new(new PdfName("Filter"u8), new PdfArray([
                new PdfInteger(0), new PdfName("Crypt"u8)
            ]))
        ]), "payload"u8));

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            update.Build());

        Assert.Contains("entry must be a name", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitCryptFilter_RejectsMismatchedDecodeParameters()
    {
        PdfDocument document = PdfDocument.Open(Revision6Fixture(), "owner-password");
        var update = new PdfIncrementalUpdateBuilder(document);
        update.AddObject(new PdfStream(new PdfDictionary([
            new(new PdfName("Filter"u8), new PdfArray([
                new PdfName("Crypt"u8), new PdfName("FlateDecode"u8)
            ])),
            new(new PdfName("DecodeParms"u8), new PdfArray([PdfNull.Instance]))
        ]), "payload"u8));

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            update.Build());

        Assert.Contains("one entry per filter", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitCryptFilter_RejectsInvalidLaterDecodeParameterEntry()
    {
        PdfDocument document = PdfDocument.Open(Revision6Fixture(), "owner-password");
        var update = new PdfIncrementalUpdateBuilder(document);
        update.AddObject(new PdfStream(new PdfDictionary([
            new(new PdfName("Filter"u8), new PdfArray([
                new PdfName("Crypt"u8), new PdfName("FlateDecode"u8)
            ])),
            new(new PdfName("DecodeParms"u8), new PdfArray([
                PdfNull.Instance, new PdfInteger(0)
            ]))
        ]), "payload"u8));

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            update.Build());

        Assert.Contains("dictionary or null", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FullRewrite_PreservesAes256EncryptionAndDecryptedPageContent()
    {
        PdfDocument document = PdfDocument.Open(Revision6Fixture(), "owner-password");

        byte[] rewritten = PdfDocumentWriter.Write(document);
        PdfDocument reopened = PdfDocument.Open(rewritten, "user-password");
        PdfStream stream = Assert.IsType<PdfStream>(reopened.Resolve(4));

        Assert.True(reopened.IsEncrypted);
        Assert.Equal(
            "q\n0.9 0.2 0.4 rg\n72 72 200 100 re\nf\nQ\n",
            Encoding.ASCII.GetString(PdfStreamDecoder.Decode(stream)));
    }

    [Fact]
    public void FullRewrite_LeavesCrossReferenceStreamUnencrypted()
    {
        PdfDocument document = PdfDocument.Open(Revision6Fixture(), "owner-password");

        byte[] rewritten = PdfDocumentWriter.Write(document, new PdfDocumentWriteOptions
        {
            CrossReferenceFormat = PdfCrossReferenceFormat.Stream,
            UseObjectStreams = true,
            CompressStructuralStreams = true
        });
        PdfDocument reopened = PdfDocument.Open(rewritten, "user-password");
        PdfStream stream = Assert.IsType<PdfStream>(reopened.Resolve(4));

        Assert.True(reopened.IsDecrypted);
        Assert.Equal(
            "q\n0.9 0.2 0.4 rg\n72 72 200 100 re\nf\nQ\n",
            Encoding.ASCII.GetString(PdfStreamDecoder.Decode(stream)));
    }

    private static (PdfDocument Document, PdfIndirectReference EncryptionReference,
        PdfDictionary Encryption, PdfName FilterName, PdfDictionary Filter)
        AuthoredEncryptionDictionary()
    {
        byte[] source = new PdfDocumentBuilder()
            .SetPasswordEncryption(new PdfPasswordEncryptionOptions
            {
                UserPassword = "user",
                OwnerPassword = "owner"
            })
            .AddBlankPage()
            .Build();
        PdfDocument document = PdfDocument.Open(source, "owner");
        PdfIndirectReference encryptionReference = Assert.IsType<PdfIndirectReference>(
            document.Trailer[new PdfName("Encrypt"u8)]);
        PdfDictionary encryption = Assert.IsType<PdfDictionary>(
            document.Resolve(encryptionReference));
        PdfDictionary filters = Assert.IsType<PdfDictionary>(
            encryption[new PdfName("CF"u8)]);
        PdfName filterName = Assert.IsType<PdfName>(
            encryption[new PdfName("StmF"u8)]);
        PdfDictionary filter = Assert.IsType<PdfDictionary>(filters[filterName]);
        return (document, encryptionReference, encryption, filterName, filter);
    }

    private static byte[] RestrictedEditingDocument(
        bool allowModification, bool allowAssembly, bool allowAnnotations) =>
        new PdfDocumentBuilder()
            .SetPasswordEncryption(new PdfPasswordEncryptionOptions
            {
                UserPassword = "user",
                OwnerPassword = "owner",
                AllowDocumentModification = allowModification,
                AllowDocumentAssembly = allowAssembly,
                AllowAnnotationModification = allowAnnotations
            })
            .AddBlankPage()
            .Build();

    private static PdfDictionary ReplaceCryptFilter(
        PdfDictionary encryption, PdfName filterName, PdfDictionary replacement)
    {
        PdfDictionary filters = Assert.IsType<PdfDictionary>(
            encryption[new PdfName("CF"u8)]);
        var updatedFilters = new PdfDictionary(filters.Select(entry =>
            entry.Key.Equals(filterName)
                ? new KeyValuePair<PdfName, PdfObject>(entry.Key, replacement)
                : entry));
        return new PdfDictionary(encryption.Select(entry =>
            entry.Key.Equals(new PdfName("CF"u8))
                ? new KeyValuePair<PdfName, PdfObject>(entry.Key, updatedFilters)
                : entry));
    }

    private static byte[] AppendEncryptionReferenceRevision(
        byte[] source, string objectDefinitions, string encryptionReference, int size)
    {
        byte[] definitions = Encoding.ASCII.GetBytes(objectDefinitions);
        int secondObjectRelativeOffset = objectDefinitions.IndexOf(
            "7 0 obj", StringComparison.Ordinal);
        Assert.True(secondObjectRelativeOffset > 0);
        long previousXref = PdfStartXref.Find(source).Offset;
        using var output = new MemoryStream();
        output.Write(source);
        int firstObjectOffset = checked((int)output.Position);
        output.Write(definitions);
        int xrefOffset = checked((int)output.Position);
        Write($"xref\n6 2\n{firstObjectOffset:0000000000} 00000 n \n");
        Write($"{firstObjectOffset + secondObjectRelativeOffset:0000000000} 00000 n \n");
        Write($"trailer << /Size {size} /Prev {previousXref} /Encrypt {encryptionReference} >>\n");
        Write($"startxref\n{xrefOffset}\n%%EOF\n");
        return output.ToArray();

        void Write(string value) => output.Write(Encoding.ASCII.GetBytes(value));
    }

    internal static byte[] Revision6Fixture() => Convert.FromBase64String(
        "JVBERi0yLjAKJb/3ov4KMSAwIG9iago8PCAvUGFnZXMgMiAwIFIgL1R5cGUgL0NhdGFsb2cgPj4KZW5kb2JqCjIgMCBvYmoKPDwgL0NvdW50IDEgL0tpZHMgWyAzIDAgUiBdIC9UeXBlIC9QYWdlcyA+PgplbmRvYmoKMyAwIG9iago8PCAvQ29udGVudHMgNCAwIFIgL01lZGlhQm94IFsgMCAwIDYxMiA3OTIgXSAvUGFyZW50IDIgMCBSIC9SZXNvdXJjZXMgPDwgPj4gL1R5cGUgL1BhZ2UgPj4KZW5kb2JqCjQgMCBvYmoKPDwgL0xlbmd0aCA2NCAvRmlsdGVyIC9GbGF0ZURlY29kZSA+PgpzdHJlYW0KYboOYzqGzywU4FQmfTIt96Axp9pkyFnOR1NeFIbac5yT14ig3iLyXR73If8yc+G9ntENo2/UKApMYIblUlmey2VuZHN0cmVhbQplbmRvYmoKNSAwIG9iago8PCAvQ0YgPDwgL1N0ZENGIDw8IC9BdXRoRXZlbnQgL0RvY09wZW4gL0NGTSAvQUVTVjMgL0xlbmd0aCAzMiA+PiA+PiAvRmlsdGVyIC9TdGFuZGFyZCAvTGVuZ3RoIDI1NiAvTyA8ZjNkNjA4M2JlMDQyMDNlNTlkNWJjNjViZjhhNWU3ZWFhOGM5MzYxNmZkMDllNTY0MzRmMjdjYzdiZTdkNzlmZTUyZGVmYjg5MDE2ZjdmOGJhZTJlNmE3YmEwYTIwYjg4PiAvT0UgPGYxNDk0NTAzMzI1NWVjODAwYmE2Mjc2MWMwNDlmZmViYjc3NDViY2MxZWNjZTAyZjRiYWY4NWQ1YzU2OGUxMTk+IC9QIC00IC9QZXJtcyA8YzQ0NjEzMGQ5ZTFkOGRhODI4MTNkNTUyNTFlODI5Mjk+IC9SIDYgL1N0bUYgL1N0ZENGIC9TdHJGIC9TdGRDRiAvVSA8ZDM2NjE1MjIzZDhlOTMxODU4Yzg0NmIxZDkxNDNiNzY4YmI1M2FkZWJkNmIxZjE1MWZkYzY0ZjgzZmE1NzEzODgyYmEyNTY0YmU5M2U1MzcxOGI2NzllMzBmNTJiYjgwPiAvVUUgPGZhNTc1MWY2YTdhYjE3MzdjZjI5NTU3YWY4NjE4ZmY2NzA0Y2U1ZDFkMTYxOTUxNWYxODc0MWJmMjRjYmYyOTQ+IC9WIDUgPj4KZW5kb2JqCnhyZWYKMCA2CjAwMDAwMDAwMDAgNjU1MzUgZiAKMDAwMDAwMDAxNSAwMDAwMCBuIAowMDAwMDAwMDY0IDAwMDAwIG4gCjAwMDAwMDAxMjMgMDAwMDAgbiAKMDAwMDAwMDIyOSAwMDAwMCBuIAowMDAwMDAwMzYzIDAwMDAwIG4gCnRyYWlsZXIgPDwgL1Jvb3QgMSAwIFIgL1NpemUgNiAvSUQgWzw0YjhjNjg5ZWU5YTIxMmUwZWU5NGQxYzZhZGYxNmE1OT48ZDViMTI5YmM5ZjFhNzc1NmUwMjhkMzQxODY2MzNhMjQ+XSAvRW5jcnlwdCA1IDAgUiA+PgpzdGFydHhyZWYKOTEwCiUlRU9GCg==");

    private static byte[] Revision4Fixture() => Convert.FromBase64String(
        "JVBERi0yLjAKJb/3ov4KMSAwIG9iago8PCAvUGFnZXMgMiAwIFIgL1R5cGUgL0NhdGFsb2cgPj4KZW5kb2JqCjIgMCBvYmoKPDwgL0NvdW50IDEgL0tpZHMgWyAzIDAgUiBdIC9UeXBlIC9QYWdlcyA+PgplbmRvYmoKMyAwIG9iago8PCAvQ29udGVudHMgNCAwIFIgL01lZGlhQm94IFsgMCAwIDYxMiA3OTIgXSAvUGFyZW50IDIgMCBSIC9SZXNvdXJjZXMgPDwgPj4gL1R5cGUgL1BhZ2UgPj4KZW5kb2JqCjQgMCBvYmoKPDwgL0xlbmd0aCA2NCAvRmlsdGVyIC9GbGF0ZURlY29kZSA+PgpzdHJlYW0KSecG3kRpaOSia/ml8moYxg5UAbhcuHXETddfMkbVaiLNqBIvOAbsW/taa+++E9SDkYUAvilCEhW/u1gABF0i+2VuZHN0cmVhbQplbmRvYmoKNSAwIG9iago8PCAvQ0YgPDwgL1N0ZENGIDw8IC9BdXRoRXZlbnQgL0RvY09wZW4gL0NGTSAvQUVTVjIgL0xlbmd0aCAxNiA+PiA+PiAvRmlsdGVyIC9TdGFuZGFyZCAvTGVuZ3RoIDEyOCAvTyA8ZmQ0YmUyZjAyYWI2YzMzOTUyYTg2NDBlYzFlZmFkZjRlY2Y3MTM4NTgyZDE0MTIzMmY0MDdjYmNiYzhmZDMwMz4gL09FIDw+IC9QIC00IC9SIDQgL1N0bUYgL1N0ZENGIC9TdHJGIC9TdGRDRiAvVSA8MmI0YzdmMDg0ODJmMTRhNzI0ZWNmY2I2OTg4YjRhZTYwMDIxNDQ2OTkwYjllNDExNDA3MWE0ZDkxMDQ5ODRjMT4gL1VFIDw+IC9WIDQgPj4KZW5kb2JqCnhyZWYKMCA2CjAwMDAwMDAwMDAgNjU1MzUgZiAKMDAwMDAwMDAxNSAwMDAwMCBuIAowMDAwMDAwMDY0IDAwMDAwIG4gCjAwMDAwMDAxMjMgMDAwMDAgbiAKMDAwMDAwMDIyOSAwMDAwMCBuIAowMDAwMDAwMzYzIDAwMDAwIG4gCnRyYWlsZXIgPDwgL1Jvb3QgMSAwIFIgL1NpemUgNiAvSUQgWzw0YjhjNjg5ZWU5YTIxMmUwZWU5NGQxYzZhZGYxNmE1OT48OTI2YTkzNGFjNWM2MmEwYTA4MTgwMjdkOWQ5YTEyYzI+XSAvRW5jcnlwdCA1IDAgUiA+PgpzdGFydHhyZWYKNjc2CiUlRU9GCg==");

    private static byte[] PdfDocEncodingRevision4Fixture() => Convert.FromBase64String(
        "JVBERi0yLjAKJb/3ov4KMSAwIG9iago8PCAvUGFnZXMgMiAwIFIgL1R5cGUgL0NhdGFsb2cgPj4KZW5kb2JqCjIgMCBvYmoKPDwgL0NvdW50IDEgL0tpZHMgWyAzIDAgUiBdIC9UeXBlIC9QYWdlcyA+PgplbmRvYmoKMyAwIG9iago8PCAvQ29udGVudHMgNCAwIFIgL01lZGlhQm94IFsgMCAwIDYxMiA3OTIgXSAvUGFyZW50IDIgMCBSIC9SZXNvdXJjZXMgPDwgPj4gL1R5cGUgL1BhZ2UgPj4KZW5kb2JqCjQgMCBvYmoKPDwgL0xlbmd0aCA2NCAvRmlsdGVyIC9GbGF0ZURlY29kZSA+PgpzdHJlYW0Kgw0KuscQ6ntGAfOAWGBA9lB4R994D/eetGoaHzFLGmvo2W2nAimdor9VyeoVS9z1hwlIqPT3ViCgcKHm3W1VPGVuZHN0cmVhbQplbmRvYmoKNSAwIG9iago8PCAvQ0YgPDwgL1N0ZENGIDw8IC9BdXRoRXZlbnQgL0RvY09wZW4gL0NGTSAvQUVTVjIgL0xlbmd0aCAxNiA+PiA+PiAvRmlsdGVyIC9TdGFuZGFyZCAvTGVuZ3RoIDEyOCAvTyA8MjliNWEzNmExMzRkODA1ZDlkZThkNGQyZDNjOTQwMmRjZWQ5NGIyNTgyN2QyMzk1MTRkOGRiYmExN2IyMTFjMz4gL09FIDw+IC9QIC00IC9SIDQgL1N0bUYgL1N0ZENGIC9TdHJGIC9TdGRDRiAvVSA8NzYxNjQ2NmI2YTBjN2JkNzE2MjdiN2Q1YmQ2NWVmNTcwMDIxNDQ2OTkwYjllNDExNDA3MWE0ZDkxMDQ5ODRjMT4gL1VFIDw+IC9WIDQgPj4KZW5kb2JqCnhyZWYKMCA2CjAwMDAwMDAwMDAgNjU1MzUgZiAKMDAwMDAwMDAxNSAwMDAwMCBuIAowMDAwMDAwMDY0IDAwMDAwIG4gCjAwMDAwMDAxMjMgMDAwMDAgbiAKMDAwMDAwMDIyOSAwMDAwMCBuIAowMDAwMDAwMzYzIDAwMDAwIG4gCnRyYWlsZXIgPDwgL1Jvb3QgMSAwIFIgL1NpemUgNiAvSUQgWzw0YjhjNjg5ZWU5YTIxMmUwZWU5NGQxYzZhZGYxNmE1OT48YzU4YzA2ODA0MGRkZWZlNWFhODgzM2M1MzFmMTRlYmY+XSAvRW5jcnlwdCA1IDAgUiA+PgpzdGFydHhyZWYKNjc2CiUlRU9GCg==");

    private static byte[] Revision2Fixture() => Convert.FromBase64String(
        "JVBERi0yLjAKJb/3ov4KMSAwIG9iago8PCAvUGFnZXMgMiAwIFIgL1R5cGUgL0NhdGFsb2cgPj4KZW5kb2JqCjIgMCBvYmoKPDwgL0NvdW50IDEgL0tpZHMgWyAzIDAgUiBdIC9UeXBlIC9QYWdlcyA+PgplbmRvYmoKMyAwIG9iago8PCAvQ29udGVudHMgNCAwIFIgL01lZGlhQm94IFsgMCAwIDYxMiA3OTIgXSAvUGFyZW50IDIgMCBSIC9SZXNvdXJjZXMgPDwgPj4gL1R5cGUgL1BhZ2UgPj4KZW5kb2JqCjQgMCBvYmoKPDwgL0xlbmd0aCA0MSAvRmlsdGVyIC9GbGF0ZURlY29kZSA+PgpzdHJlYW0KZaBGAVdUcOQTTqKiDmfiA7SZnHAUZxFG/VQF8F5tnO4kjPqx1cmsYkdlbmRzdHJlYW0KZW5kb2JqCjUgMCBvYmoKPDwgL0ZpbHRlciAvU3RhbmRhcmQgL0xlbmd0aCA0MCAvTyA8M2Q0YzFmYjdlOWE3Nzc3ODI3ZTZmNmRjZDMxZmEwMzQ4ZTg1NDExODliYWYwMGZlZTJiMjZlNmNlN2QyNzIzZD4gL1AgLTQgL1IgMiAvVSA8ODhiOWI0NjdkNjkwODk1OTNmMjIxOWY5YTZlNWZiMTc3NTZhNjkwMGMxMDcyMjY4NDcxZDM1NDdmOTRhZDBkYz4gL1YgMSA+PgplbmRvYmoKeHJlZgowIDYKMDAwMDAwMDAwMCA2NTUzNSBmIAowMDAwMDAwMDE1IDAwMDAwIG4gCjAwMDAwMDAwNjQgMDAwMDAgbiAKMDAwMDAwMDEyMyAwMDAwMCBuIAowMDAwMDAwMjI5IDAwMDAwIG4gCjAwMDAwMDAzNDAgMDAwMDAgbiAKdHJhaWxlciA8PCAvUm9vdCAxIDAgUiAvU2l6ZSA2IC9JRCBbPDRiOGM2ODllZTlhMjEyZTBlZTk0ZDFjNmFkZjE2YTU5PjwyYWFiOTA2NjI2ODFlY2Y5NTAwZmY3ZGU5ZjFmMjM2Mz5dIC9FbmNyeXB0IDUgMCBSID4+CnN0YXJ0eHJlZgo1NDYKJSVFT0YK");
}
