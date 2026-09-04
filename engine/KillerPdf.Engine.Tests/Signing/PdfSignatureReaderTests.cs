using System.Text;
using System.Globalization;
using System.Text.RegularExpressions;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.CrossReference;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Signing;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Signing;

public sealed class PdfSignatureReaderTests
{
#pragma warning disable SYSLIB1045 // Cached test patterns avoid a Visual Studio design-time generator failure.
    private static readonly Regex RootReferenceRegex =
        new(@"/Root \d+ \d+ R", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ByteRangeRegex =
        new(@"/ByteRange \[(\d{10}) (\d{10}) (\d{10}) (\d{10})\]",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
#pragma warning restore SYSLIB1045

    [Fact]
    public void Read_DecodesPdf20Utf8SignatureFieldNames()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage()
            .AddSignatureField(0, "approval", 20, 20, 160, 40)
            .Build());
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(source.Resolve(
            Assert.IsType<PdfIndirectReference>(source.Trailer[new PdfName("Root"u8)])));
        PdfObject formValue = catalog[new PdfName("AcroForm"u8)];
        PdfDictionary form = Assert.IsType<PdfDictionary>(
            formValue is PdfIndirectReference formReference
                ? source.Resolve(formReference) : formValue);
        PdfIndirectReference fieldReference = Assert.IsType<PdfIndirectReference>(
            Assert.IsType<PdfArray>(form[new PdfName("Fields"u8)])[0]);
        PdfDictionary field = Assert.IsType<PdfDictionary>(source.Resolve(fieldReference));
        byte[] utf8Name = [0xEF, 0xBB, 0xBF, .. "approval"u8.ToArray()];
        PdfDocument reopened = PdfDocument.Open(new PdfIncrementalUpdateBuilder(source)
            .ReplaceObject(fieldReference.ObjectNumber, new PdfDictionary(field
                .Where(entry => !entry.Key.Equals(new PdfName("T"u8)))
                .Append(new KeyValuePair<PdfName, PdfObject>(new PdfName("T"u8),
                    new PdfString(utf8Name, PdfStringForm.Hexadecimal)))))
            .Build());

        PdfSignatureInfo signature = Assert.Single(PdfSignatureReader.Read(reopened));

        Assert.Equal("approval", signature.FieldName);
    }

    [Fact]
    public void Read_DecodesPdfDocEncodingSignatureFieldNames()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage()
            .AddSignatureField(0, "approval", 20, 20, 160, 40)
            .Build());
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(source.Resolve(
            Assert.IsType<PdfIndirectReference>(source.Trailer[Name("Root")])));
        PdfObject formValue = catalog[Name("AcroForm")];
        PdfDictionary form = formValue is PdfIndirectReference formReference
            ? Assert.IsType<PdfDictionary>(source.Resolve(formReference))
            : Assert.IsType<PdfDictionary>(formValue);
        PdfIndirectReference fieldReference = Assert.IsType<PdfIndirectReference>(
            Assert.IsType<PdfArray>(form[Name("Fields")])[0]);
        PdfDictionary field = Assert.IsType<PdfDictionary>(source.Resolve(fieldReference));
        PdfDocument reopened = PdfDocument.Open(new PdfIncrementalUpdateBuilder(source)
            .ReplaceObject(fieldReference.ObjectNumber, new PdfDictionary(field
                .Where(entry => !entry.Key.Equals(Name("T")))
                .Append(new KeyValuePair<PdfName, PdfObject>(Name("T"),
                    new PdfString([0x80, 0xA0], PdfStringForm.Literal)))))
            .Build());

        PdfSignatureInfo signature = Assert.Single(PdfSignatureReader.Read(reopened));

        Assert.Equal("•€", signature.FieldName);
    }

    [Fact]
    public void Read_ResolvesIndirectSignatureFieldTypeAndPartialName()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage()
            .AddSignatureField(0, "approval", 20, 20, 160, 40)
            .Build());
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(source.Resolve(
            Assert.IsType<PdfIndirectReference>(source.Trailer[Name("Root")])));
        PdfObject formValue = catalog[Name("AcroForm")];
        PdfDictionary form = formValue is PdfIndirectReference formReference
            ? Assert.IsType<PdfDictionary>(source.Resolve(formReference))
            : Assert.IsType<PdfDictionary>(formValue);
        PdfIndirectReference fieldReference = Assert.IsType<PdfIndirectReference>(
            Assert.IsType<PdfArray>(form[Name("Fields")])[0]);
        PdfDictionary field = Assert.IsType<PdfDictionary>(source.Resolve(fieldReference));
        var update = new PdfIncrementalUpdateBuilder(source);
        PdfIndirectReference type = update.AddObject(Name("Sig"));
        PdfIndirectReference name = update.AddObject(
            new PdfString("approval"u8, PdfStringForm.Literal));
        update.ReplaceObject(fieldReference.ObjectNumber, new PdfDictionary(field.Select(entry =>
            entry.Key.Equals(Name("FT"))
                ? new KeyValuePair<PdfName, PdfObject>(entry.Key, type)
                : entry.Key.Equals(Name("T"))
                    ? new KeyValuePair<PdfName, PdfObject>(entry.Key, name)
                    : entry)));

        PdfSignatureInfo signature = Assert.Single(PdfSignatureReader.Read(
            PdfDocument.Open(update.Build())));

        Assert.Equal("approval", signature.FieldName);
    }

    [Theory]
    [InlineData(0x7F)]
    [InlineData(0x9F)]
    [InlineData(0xAD)]
    public void Read_RejectsUndefinedPdfDocEncodingSignatureFieldNames(int undefinedByte)
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage()
            .AddSignatureField(0, "approval", 20, 20, 160, 40)
            .Build());
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(source.Resolve(
            Assert.IsType<PdfIndirectReference>(source.Trailer[Name("Root")])));
        PdfObject formValue = catalog[Name("AcroForm")];
        PdfDictionary form = formValue is PdfIndirectReference formReference
            ? Assert.IsType<PdfDictionary>(source.Resolve(formReference))
            : Assert.IsType<PdfDictionary>(formValue);
        PdfIndirectReference fieldReference = Assert.IsType<PdfIndirectReference>(
            Assert.IsType<PdfArray>(form[Name("Fields")])[0]);
        PdfDictionary field = Assert.IsType<PdfDictionary>(source.Resolve(fieldReference));
        PdfDocument reopened = PdfDocument.Open(new PdfIncrementalUpdateBuilder(source)
            .ReplaceObject(fieldReference.ObjectNumber, new PdfDictionary(field
                .Where(entry => !entry.Key.Equals(Name("T")))
                .Append(new KeyValuePair<PdfName, PdfObject>(Name("T"),
                    new PdfString([(byte)undefinedByte], PdfStringForm.Literal)))))
            .Build());

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => PdfSignatureReader.Read(reopened));

        Assert.Contains($"0x{undefinedByte:X2}", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_UsesRootInheritedFromAnOlderTrailerRevision()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage()
            .AddSignatureField(0, "approval", 20, 20, 160, 40)
            .Build());
        var update = new PdfIncrementalUpdateBuilder(source);
        update.AddObject(new PdfInteger(1));
        string revision = Encoding.Latin1.GetString(update.Build());
        Match root = RootReferenceRegex.Matches(revision).Last();
        revision = revision.Remove(root.Index, root.Length)
            .Insert(root.Index, new string(' ', root.Length));

        PdfSignatureInfo signature = Assert.Single(PdfSignatureReader.Read(
            PdfDocument.Open(Encoding.Latin1.GetBytes(revision))));

        Assert.Equal("approval", signature.FieldName);
        Assert.False(signature.IsSigned);
    }

    [Fact]
    public void Read_TreatsEmptySignatureStringAsUnsigned()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage()
            .AddSignatureField(0, "approval", 20, 20, 160, 40)
            .Build());
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(source.Resolve(
            Assert.IsType<PdfIndirectReference>(source.Trailer[Name("Root")])));
        PdfDictionary form = Assert.IsType<PdfDictionary>(catalog[Name("AcroForm")]);
        PdfIndirectReference fieldReference = Assert.IsType<PdfIndirectReference>(
            Assert.Single(Assert.IsType<PdfArray>(form[Name("Fields")])));
        PdfDictionary field = Assert.IsType<PdfDictionary>(source.Resolve(fieldReference));
        PdfDocument reopened = PdfDocument.Open(new PdfIncrementalUpdateBuilder(source)
            .ReplaceObject(fieldReference.ObjectNumber, new PdfDictionary(field.Append(
                new KeyValuePair<PdfName, PdfObject>(Name("V"),
                    new PdfString([], PdfStringForm.Literal)))))
            .Build());

        PdfSignatureInfo signature = Assert.Single(PdfSignatureReader.Read(reopened));

        Assert.False(signature.IsSigned);
    }

    [Fact]
    public void Read_ReportsUnsignedAndCertificationSignaturesAndExtractsSignedContent()
    {
        byte[] source = new PdfDocumentBuilder()
            .AddBlankPage()
            .AddSignatureField(0, "approval", 20, 80, 160, 40)
            .AddSignatureField(0, "certification", 20, 20, 160, 40,
                fieldLock: new PdfSignatureFieldLock(
                    PdfSignatureLockAction.Include, ["approval"],
                    PdfSignatureLockPermission.FormFillingAndSignatures))
            .Build();
        byte[]? callbackContent = null;
        byte[] signedBytes = PdfDetachedSignatureWriter.Sign(
            PdfDocument.Open(source), content =>
            {
                callbackContent = content.ToArray();
                return [0x30, 0x01, 0x00];
            }, new PdfSignatureOptions
            {
                FieldName = "certification",
                SignerName = "Steve",
                Reason = "Approval",
                Location = "Seattle",
                ContactInformation = "thekiller.net",
                SigningTime = new DateTimeOffset(2026, 9, 3, 12, 34, 56,
                    TimeSpan.FromHours(-7)),
                CertificationPermission =
                    PdfSignatureCertificationPermission.FormFillingAndSignatures,
                ReservedSignatureSize = 16
            });
        PdfDocument document = PdfDocument.Open(signedBytes);

        IReadOnlyList<PdfSignatureInfo> signatures = PdfSignatureReader.Read(document);
        PdfSignatureInfo certification = Assert.Single(signatures,
            item => item.FieldName == "certification");
        PdfSignatureInfo approval = Assert.Single(signatures,
            item => item.FieldName == "approval");

        Assert.True(certification.IsSigned);
        Assert.True(certification.IsCertificationSignature);
        Assert.Equal(PdfSignatureCertificationPermission.FormFillingAndSignatures,
            certification.CertificationPermission);
        Assert.Equal(PdfSignatureLockAction.Include, certification.FieldLockAction);
        Assert.Equal(PdfSignatureLockPermission.FormFillingAndSignatures,
            certification.FieldLockPermission);
        Assert.Equal(["approval"], certification.LockedFields);
        Assert.True(certification.HasValidByteRange);
        Assert.True(certification.CoversWholeDocument);
        Assert.Equal("Adobe.PPKLite", certification.Filter);
        Assert.Equal("ETSI.CAdES.detached", certification.SubFilter);
        Assert.Equal("Steve", certification.SignerName);
        Assert.Equal("Approval", certification.Reason);
        Assert.Equal("Seattle", certification.Location);
        Assert.Equal("thekiller.net", certification.ContactInformation);
        Assert.Equal(new DateTimeOffset(2026, 9, 3, 12, 34, 56,
            TimeSpan.FromHours(-7)), certification.SigningTime);
        Assert.Equal(16, certification.Contents.Length);
        Assert.True(certification.HasValidCmsEncoding);
        Assert.Equal([0x30, 0x01, 0x00], certification.Cms.ToArray());
        Assert.Equal(callbackContent, PdfSignatureReader.GetSignedContent(
            document, certification));
        Assert.Equal(PdfSignedRevisionPermissionAssessment.NoLaterChanges,
            PdfSignedRevisionAnalyzer.Analyze(document, certification).PermissionAssessment);
        Assert.False(approval.IsSigned);
        Assert.False(approval.IsCertificationSignature);
        Assert.Null(approval.ByteRange);
    }

    [Fact]
    public void Read_ResolvesIndirectSignatureTransformScalars()
    {
        byte[] source = new PdfDocumentBuilder()
            .AddBlankPage()
            .AddSignatureField(0, "approval", 20, 80, 160, 40)
            .AddSignatureField(0, "certification", 20, 20, 160, 40,
                fieldLock: new PdfSignatureFieldLock(
                    PdfSignatureLockAction.Include, ["approval"],
                    PdfSignatureLockPermission.FormFillingAndSignatures))
            .Build();
        PdfDocument signed = PdfDocument.Open(PdfDetachedSignatureWriter.Sign(
            PdfDocument.Open(source), _ => [0x30, 0x01, 0x00],
            new PdfSignatureOptions
            {
                FieldName = "certification",
                CertificationPermission =
                    PdfSignatureCertificationPermission.FormFillingAndSignatures,
                ReservedSignatureSize = 16
            }));
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(signed.Resolve(
            Assert.IsType<PdfIndirectReference>(signed.Trailer[Name("Root")])));
        PdfDictionary permissions = Assert.IsType<PdfDictionary>(catalog[Name("Perms")]);
        PdfIndirectReference signatureReference = Assert.IsType<PdfIndirectReference>(
            permissions[Name("DocMDP")]);
        PdfDictionary signature = Assert.IsType<PdfDictionary>(
            signed.Resolve(signatureReference));
        var update = new PdfIncrementalUpdateBuilder(signed);
        PdfIndirectReference signatureAlias = update.AddObject(signatureReference);
        PdfIndirectReference signatureSecondAlias = update.AddObject(signatureAlias);
        PdfDictionary aliasedPermissions = new(permissions.Select(entry =>
            entry.Key.Equals(Name("DocMDP"))
                ? new KeyValuePair<PdfName, PdfObject>(entry.Key, signatureSecondAlias)
                : entry));
        PdfIndirectReference catalogReference = Assert.IsType<PdfIndirectReference>(
            signed.Trailer[Name("Root")]);
        update.ReplaceObject(catalogReference.ObjectNumber,
            new PdfDictionary(catalog.Select(entry => entry.Key.Equals(Name("Perms"))
                ? new KeyValuePair<PdfName, PdfObject>(entry.Key, aliasedPermissions)
                : entry)));
        PdfObject formValue = catalog[Name("AcroForm")];
        PdfDictionary form = formValue is PdfIndirectReference formReference
            ? Assert.IsType<PdfDictionary>(signed.Resolve(formReference))
            : Assert.IsType<PdfDictionary>(formValue);
        PdfArray fields = Assert.IsType<PdfArray>(form[Name("Fields")]);
        PdfIndirectReference certificationFieldReference = fields
            .Select(Assert.IsType<PdfIndirectReference>)
            .Single(reference =>
            {
                PdfDictionary field = Assert.IsType<PdfDictionary>(signed.Resolve(reference));
                return field.TryGetValue(Name("T"), out PdfObject? nameValue)
                    && nameValue is PdfString name
                    && Encoding.BigEndianUnicode.GetString(name.Bytes.Span[2..]) == "certification";
            });
        PdfDictionary certificationField = Assert.IsType<PdfDictionary>(
            signed.Resolve(certificationFieldReference));
        update.ReplaceObject(certificationFieldReference.ObjectNumber,
            new PdfDictionary(certificationField.Select(entry => entry.Key.Equals(Name("V"))
                ? new KeyValuePair<PdfName, PdfObject>(entry.Key, signatureSecondAlias)
                : entry)));
        PdfIndirectReference Indirect(PdfObject value)
        {
            PdfIndirectReference terminal = update.AddObject(value);
            return update.AddObject(terminal);
        }
        PdfArray references = Assert.IsType<PdfArray>(signature[Name("Reference")]);
        var rewrittenReferences = new PdfArray(references.Select(item =>
        {
            PdfDictionary reference = Assert.IsType<PdfDictionary>(item);
            return (PdfObject)new PdfDictionary(reference.Select(entry =>
            {
                if (entry.Key.Equals(Name("Type"))
                    || entry.Key.Equals(Name("TransformMethod")))
                    return new KeyValuePair<PdfName, PdfObject>(entry.Key, Indirect(entry.Value));
                if (!entry.Key.Equals(Name("TransformParams"))) return entry;
                PdfDictionary parameters = Assert.IsType<PdfDictionary>(entry.Value);
                var rewrittenParameters = new PdfDictionary(parameters.Select(parameter =>
                {
                    if (parameter.Key.Equals(Name("Fields")))
                    {
                        PdfArray fields = Assert.IsType<PdfArray>(parameter.Value);
                        return new KeyValuePair<PdfName, PdfObject>(parameter.Key,
                            new PdfArray(fields.Select(field => (PdfObject)Indirect(field))));
                    }
                    return parameter.Value is PdfName or PdfInteger or PdfString
                        ? new KeyValuePair<PdfName, PdfObject>(
                            parameter.Key, Indirect(parameter.Value))
                        : parameter;
                }));
                return new KeyValuePair<PdfName, PdfObject>(
                    entry.Key, rewrittenParameters);
            }));
        }));
        PdfArray byteRange = Assert.IsType<PdfArray>(signature[Name("ByteRange")]);
        PdfDictionary rewrittenSignature = new(signature.Select(entry =>
            entry.Key.Equals(Name("Reference"))
                ? new KeyValuePair<PdfName, PdfObject>(entry.Key, rewrittenReferences)
                : entry.Key.Equals(Name("ByteRange"))
                    ? new KeyValuePair<PdfName, PdfObject>(entry.Key,
                        new PdfArray(byteRange.Select(value => (PdfObject)Indirect(value))))
                    : entry.Key.Equals(Name("Type")) || entry.Key.Equals(Name("Filter"))
                        || entry.Key.Equals(Name("SubFilter"))
                        ? new KeyValuePair<PdfName, PdfObject>(entry.Key, Indirect(entry.Value))
                        : entry));
        update.ReplaceObject(signatureReference.ObjectNumber, rewrittenSignature);

        PdfSignatureInfo result = Assert.Single(PdfSignatureReader.Read(
            PdfDocument.Open(update.Build())), item => item.FieldName == "certification");

        Assert.Equal("Adobe.PPKLite", result.Filter);
        Assert.Equal("ETSI.CAdES.detached", result.SubFilter);
        Assert.Equal(PdfSignatureCertificationPermission.FormFillingAndSignatures,
            result.CertificationPermission);
        Assert.Equal(PdfSignatureLockAction.Include, result.FieldLockAction);
        Assert.Equal(PdfSignatureLockPermission.FormFillingAndSignatures,
            result.FieldLockPermission);
        Assert.Equal(["approval"], result.LockedFields);
        Assert.NotNull(result.ByteRange);
        Assert.True(result.IsCertificationSignature);
    }

    [Fact]
    public void Read_ValidatesSignatureReferenceTypeAndRequiredFieldMdpFields()
    {
        byte[] source = new PdfDocumentBuilder()
            .AddBlankPage()
            .AddSignatureField(0, "approval", 20, 80, 160, 40)
            .AddSignatureField(0, "certification", 20, 20, 160, 40,
                fieldLock: new PdfSignatureFieldLock(
                    PdfSignatureLockAction.Include, ["approval"]))
            .Build();
        PdfDocument signed = PdfDocument.Open(PdfDetachedSignatureWriter.Sign(
            PdfDocument.Open(source), _ => [1], new PdfSignatureOptions
            {
                FieldName = "certification",
                CertificationPermission = PdfSignatureCertificationPermission.NoChanges,
                ReservedSignatureSize = 8
            }));
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(signed.Resolve(
            Assert.IsType<PdfIndirectReference>(signed.Trailer[Name("Root")])));
        PdfDictionary permissions = Assert.IsType<PdfDictionary>(catalog[Name("Perms")]);
        PdfIndirectReference signatureReference = Assert.IsType<PdfIndirectReference>(
            permissions[Name("DocMDP")]);
        PdfDictionary signature = Assert.IsType<PdfDictionary>(
            signed.Resolve(signatureReference));
        PdfArray references = Assert.IsType<PdfArray>(signature[Name("Reference")]);

        PdfArray missingFieldsReferences = new(references.Select(item =>
        {
            PdfDictionary reference = Assert.IsType<PdfDictionary>(item);
            if (Assert.IsType<PdfName>(reference[Name("TransformMethod")]).ValueAsLatin1()
                != "FieldMDP") return item;
            PdfDictionary parameters = Assert.IsType<PdfDictionary>(
                reference[Name("TransformParams")]);
            return (PdfObject)new PdfDictionary(reference.Select(entry =>
                entry.Key.Equals(Name("TransformParams"))
                    ? new KeyValuePair<PdfName, PdfObject>(entry.Key,
                        new PdfDictionary(parameters.Where(parameter =>
                            !parameter.Key.Equals(Name("Fields")))))
                    : entry));
        }));
        PdfDocument missingFields = PdfDocument.Open(new PdfIncrementalUpdateBuilder(signed)
            .ReplaceObject(signatureReference.ObjectNumber,
                new PdfDictionary(signature.Select(entry =>
                    entry.Key.Equals(Name("Reference"))
                        ? new KeyValuePair<PdfName, PdfObject>(entry.Key, missingFieldsReferences)
                        : entry)))
            .Build());
        InvalidOperationException fieldsError = Assert.Throws<InvalidOperationException>(() =>
            PdfSignatureReader.Read(missingFields));
        Assert.Contains("has no /Fields array", fieldsError.Message, StringComparison.Ordinal);

        PdfDictionary firstReference = Assert.IsType<PdfDictionary>(references[0]);
        PdfArray wrongTypeReferences = new([
            new PdfDictionary(firstReference.Select(entry => entry.Key.Equals(Name("Type"))
                ? new KeyValuePair<PdfName, PdfObject>(entry.Key, Name("Wrong"))
                : entry)),
            .. references.Skip(1)
        ]);
        PdfDocument wrongType = PdfDocument.Open(new PdfIncrementalUpdateBuilder(signed)
            .ReplaceObject(signatureReference.ObjectNumber,
                new PdfDictionary(signature.Select(entry =>
                    entry.Key.Equals(Name("Reference"))
                        ? new KeyValuePair<PdfName, PdfObject>(entry.Key, wrongTypeReferences)
                        : entry)))
            .Build());
        InvalidOperationException typeError = Assert.Throws<InvalidOperationException>(() =>
            PdfSignatureReader.Read(wrongType));
        Assert.Contains("must be /SigRef", typeError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_RejectsSignedValueWithoutSignatureDictionaryType()
    {
        byte[] source = new PdfDocumentBuilder()
            .AddBlankPage()
            .AddSignatureField(0, "approval", 20, 20, 160, 40)
            .Build();
        PdfDocument signed = PdfDocument.Open(PdfDetachedSignatureWriter.Sign(
            PdfDocument.Open(source), _ => [1], new PdfSignatureOptions
            {
                FieldName = "approval",
                ReservedSignatureSize = 8
            }));
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(signed.Resolve(
            Assert.IsType<PdfIndirectReference>(signed.Trailer[Name("Root")])));
        PdfObject formValue = catalog[Name("AcroForm")];
        PdfDictionary form = Assert.IsType<PdfDictionary>(
            formValue is PdfIndirectReference formReference
                ? signed.Resolve(formReference) : formValue);
        PdfDictionary field = Assert.IsType<PdfDictionary>(signed.Resolve(
            Assert.IsType<PdfIndirectReference>(
                Assert.IsType<PdfArray>(form[Name("Fields")])[0])));
        PdfIndirectReference signatureReference = Assert.IsType<PdfIndirectReference>(
            field[Name("V")]);
        PdfDictionary signature = Assert.IsType<PdfDictionary>(
            signed.Resolve(signatureReference));
        PdfDocument malformed = PdfDocument.Open(new PdfIncrementalUpdateBuilder(signed)
            .ReplaceObject(signatureReference.ObjectNumber,
                new PdfDictionary(signature.Where(entry => !entry.Key.Equals(Name("Type")))))
            .Build());

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            PdfSignatureReader.Read(malformed));

        Assert.Contains("does not declare /Type /Sig", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_IdentifiesDocumentTimestampDictionaries()
    {
        byte[] source = new PdfDocumentBuilder()
            .AddBlankPage()
            .AddSignatureField(0, "timestamp", 20, 20, 160, 40)
            .Build();
        PdfDocument signed = PdfDocument.Open(PdfDetachedSignatureWriter.Sign(
            PdfDocument.Open(source), _ => [1], new PdfSignatureOptions
            {
                FieldName = "timestamp",
                ReservedSignatureSize = 8
            }));
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(signed.Resolve(
            Assert.IsType<PdfIndirectReference>(signed.Trailer[Name("Root")])));
        PdfObject formValue = catalog[Name("AcroForm")];
        PdfDictionary form = formValue is PdfIndirectReference formReference
            ? Assert.IsType<PdfDictionary>(signed.Resolve(formReference))
            : Assert.IsType<PdfDictionary>(formValue);
        PdfDictionary field = Assert.IsType<PdfDictionary>(signed.Resolve(
            Assert.IsType<PdfIndirectReference>(
                Assert.Single(Assert.IsType<PdfArray>(form[Name("Fields")])))));
        PdfIndirectReference signatureReference = Assert.IsType<PdfIndirectReference>(
            field[Name("V")]);
        PdfDictionary signature = Assert.IsType<PdfDictionary>(
            signed.Resolve(signatureReference));
        PdfDictionary timestamp = new(signature.Select(entry =>
            entry.Key.Equals(Name("Type"))
                ? new KeyValuePair<PdfName, PdfObject>(entry.Key, Name("DocTimeStamp"))
                : entry.Key.Equals(Name("SubFilter"))
                    ? new KeyValuePair<PdfName, PdfObject>(entry.Key, Name("ETSI.RFC3161"))
                    : entry));
        PdfDocument document = PdfDocument.Open(new PdfIncrementalUpdateBuilder(signed)
            .ReplaceObject(signatureReference.ObjectNumber, timestamp).Build());

        PdfSignatureInfo result = Assert.Single(PdfSignatureReader.Read(document));
        PdfSignatureInspectionReport report = PdfSignatureInspection.Inspect(document);

        Assert.True(result.IsDocumentTimestamp);
        Assert.Equal("ETSI.RFC3161", result.SubFilter);
        Assert.Contains("Document timestamp: Yes", report.ToText());
        Assert.Contains("RFC 3161 document timestamp cryptographic verification is not supported",
            Assert.Single(report.Entries).Verification.Error, StringComparison.Ordinal);
        Assert.Contains("\"isDocumentTimestamp\":true", report.ToJson());
    }

    [Fact]
    public void Read_RejectsCertificationTargetWithoutSignatureDictionaryType()
    {
        byte[] source = new PdfDocumentBuilder()
            .AddBlankPage()
            .AddSignatureField(0, "certification", 20, 20, 160, 40)
            .Build();
        PdfDocument signed = PdfDocument.Open(PdfDetachedSignatureWriter.Sign(
            PdfDocument.Open(source), _ => [1], new PdfSignatureOptions
            {
                FieldName = "certification",
                CertificationPermission = PdfSignatureCertificationPermission.NoChanges,
                ReservedSignatureSize = 8
            }));
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(signed.Resolve(
            Assert.IsType<PdfIndirectReference>(signed.Trailer[Name("Root")])));
        PdfDictionary permissions = Assert.IsType<PdfDictionary>(catalog[Name("Perms")]);
        PdfIndirectReference signatureReference = Assert.IsType<PdfIndirectReference>(
            permissions[Name("DocMDP")]);
        PdfDictionary signature = Assert.IsType<PdfDictionary>(
            signed.Resolve(signatureReference));
        PdfDocument malformed = PdfDocument.Open(new PdfIncrementalUpdateBuilder(signed)
            .ReplaceObject(signatureReference.ObjectNumber,
                new PdfDictionary(signature.Where(entry => !entry.Key.Equals(Name("Type")))))
            .Build());

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            PdfSignatureReader.Read(malformed));

        Assert.Contains("catalog certification signature does not declare /Type /Sig",
            error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Read_DistinguishesValidEarlierRevisionCoverageFromWholeDocumentCoverage()
    {
        byte[] initial = new PdfDocumentBuilder().AddBlankPage().Build();
        var seed = new PdfIncrementalUpdateBuilder(PdfDocument.Open(initial));
        PdfIndirectReference freedObject = seed.AddObject(new PdfInteger(99));
        byte[] source = seed.Build();
        byte[] signed = PdfDetachedSignatureWriter.Sign(
            PdfDocument.Open(source), _ => [1], new PdfSignatureOptions
            {
                ReservedSignatureSize = 8
            });
        PdfDocument signedDocument = PdfDocument.Open(signed);
        var update = new PdfIncrementalUpdateBuilder(signedDocument);
        PdfIndirectReference addedObject = update.AddObject(new PdfInteger(1));
        PdfIndirectReference catalogReference = Assert.IsType<PdfIndirectReference>(
            signedDocument.Trailer[new PdfName("Root"u8)]);
        update.ReplaceObject(catalogReference.ObjectNumber,
            signedDocument.Resolve(catalogReference));
        update.FreeObject(freedObject.ObjectNumber);
        byte[] withLaterBytes = update.Build(new PdfIncrementalUpdateWriteOptions
        {
            CrossReferenceFormat = PdfCrossReferenceFormat.Stream,
            UseObjectStreams = true,
            CompressObjectStreams = true,
            CompressCrossReferenceStream = true
        });
        PdfDocument document = PdfDocument.Open(withLaterBytes);
        PdfSignatureInfo signature = Assert.Single(PdfSignatureReader.Read(document));
        PdfSignedRevisionAnalysis analysis =
            PdfSignedRevisionAnalyzer.Analyze(document, signature);

        Assert.True(signature.HasValidByteRange);
        Assert.False(signature.CoversWholeDocument);
        Assert.True(analysis.SignedRevisionIsValidPdf);
        Assert.True(analysis.HasLaterChanges);
        Assert.Equal(1, analysis.LaterRevisionCount);
        Assert.Contains(addedObject.ObjectNumber, analysis.ChangedObjectNumbers);
        Assert.Contains(addedObject.ObjectNumber, analysis.AddedObjectNumbers);
        Assert.Contains(catalogReference.ObjectNumber, analysis.UpdatedObjectNumbers);
        Assert.Contains(freedObject.ObjectNumber, analysis.FreedObjectNumbers);
        Assert.Equal(PdfCrossReferenceEntryType.Compressed,
            document.CrossReferences[addedObject.ObjectNumber].Type);
        Assert.Equal(PdfSignedRevisionPermissionAssessment.NotCertified,
            analysis.PermissionAssessment);
    }

    [Fact]
    public void Analyze_ReportsLaterChangesAsProhibitedByNoChangesCertification()
    {
        byte[] source = new PdfDocumentBuilder().AddBlankPage().Build();
        byte[] signed = PdfDetachedSignatureWriter.Sign(
            PdfDocument.Open(source), _ => [0x30, 0x00], new PdfSignatureOptions
            {
                CertificationPermission = PdfSignatureCertificationPermission.NoChanges,
                ReservedSignatureSize = 8
            });
        var update = new PdfIncrementalUpdateBuilder(PdfDocument.Open(signed));
        update.AddObject(new PdfInteger(1));
        PdfDocument changed = PdfDocument.Open(update.Build());
        PdfSignatureInfo signature = Assert.Single(PdfSignatureReader.Read(changed));

        PdfSignedRevisionAnalysis analysis =
            PdfSignedRevisionAnalyzer.Analyze(changed, signature);

        Assert.Equal(PdfSignedRevisionPermissionAssessment.Prohibited,
            analysis.PermissionAssessment);
    }

    [Fact]
    public void Analyze_ReportsMalformedFilteredSignedRevisionWithoutThrowing()
    {
        var source = new StringBuilder("%PDF-2.0\n");
        int malformedXrefOffset = source.Length;
        source.Append("1 0 obj\n")
            .Append("<< /Type /XRef /Size 1 /W [1 1 1] /Filter /Bogus /Length 1 >>\n")
            .Append("stream\nx\nendstream\nendobj\n")
            .Append($"startxref\n{malformedXrefOffset}\n%%EOF\n");
        int signedLength = Encoding.ASCII.GetByteCount(source.ToString());
        int catalogOffset = source.Length;
        source.Append("2 0 obj << /Type /Catalog >> endobj\n");
        int currentXrefOffset = source.Length;
        source.Append("xref\n0 3\n")
            .Append("0000000000 65535 f\n")
            .Append("0000000000 00000 f\n")
            .Append($"{catalogOffset:0000000000} 00000 n\n")
            .Append("trailer << /Size 3 /Root 2 0 R >>\n")
            .Append($"startxref\n{currentXrefOffset}\n%%EOF\n");
        PdfDocument document = PdfDocument.Open(
            Encoding.ASCII.GetBytes(source.ToString()));
        var signature = new PdfSignatureInfo
        {
            FieldName = "malformed-history",
            IsSigned = true,
            HasValidByteRange = true,
            ByteRange = [0, 0, 1, signedLength - 1]
        };

        PdfSignedRevisionAnalysis analysis =
            PdfSignedRevisionAnalyzer.Analyze(document, signature);

        Assert.False(analysis.SignedRevisionIsValidPdf);
        Assert.True(analysis.HasLaterChanges);
        Assert.Equal(1, analysis.LaterRevisionCount);
    }

    [Fact]
    public void Read_ReportsInvalidByteRangeWithoutReadingOutsideTheDocument()
    {
        byte[] source = new PdfDocumentBuilder().AddBlankPage().Build();
        byte[] signed = PdfDetachedSignatureWriter.Sign(
            PdfDocument.Open(source), _ => [1], new PdfSignatureOptions
            {
                ReservedSignatureSize = 8
            });
        byte[] marker = Encoding.ASCII.GetBytes("/ByteRange [0000000000");
        int markerOffset = signed.AsSpan().IndexOf(marker);
        Assert.True(markerOffset >= 0);
        signed[markerOffset + marker.Length - 1] = (byte)'1';

        PdfSignatureInfo signature = Assert.Single(
            PdfSignatureReader.Read(PdfDocument.Open(signed)));

        Assert.True(signature.IsSigned);
        Assert.False(signature.HasValidByteRange);
        Assert.False(signature.CoversWholeDocument);
        Assert.Throws<InvalidOperationException>(() =>
            PdfSignatureReader.GetSignedContent(PdfDocument.Open(signed), signature));
    }

    [Fact]
    public void Read_RejectsByteRangeGapThatIncludesBytesBeyondContents()
    {
        byte[] source = new PdfDocumentBuilder().AddBlankPage().Build();
        byte[] signed = PdfDetachedSignatureWriter.Sign(
            PdfDocument.Open(source), _ => [1], new PdfSignatureOptions
            {
                ReservedSignatureSize = 8
            });
        string text = Encoding.ASCII.GetString(signed);
        Match match = ByteRangeRegex.Match(text);
        Assert.True(match.Success);
        int secondStart = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
        int secondLength = int.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture);
        Encoding.ASCII.GetBytes($"{secondStart + 1:0000000000}")
            .CopyTo(signed.AsSpan(match.Groups[3].Index, 10));
        Encoding.ASCII.GetBytes($"{secondLength - 1:0000000000}")
            .CopyTo(signed.AsSpan(match.Groups[4].Index, 10));

        PdfSignatureInfo signature = Assert.Single(
            PdfSignatureReader.Read(PdfDocument.Open(signed)));

        Assert.False(signature.HasValidByteRange);
        Assert.False(signature.CoversWholeDocument);
    }

    [Fact]
    public void Read_RejectsStaleCertificationSignatureReference()
    {
        byte[] source = new PdfDocumentBuilder().AddBlankPage().Build();
        byte[] signed = PdfDetachedSignatureWriter.Sign(
            PdfDocument.Open(source), _ => [1], new PdfSignatureOptions
            {
                CertificationPermission = PdfSignatureCertificationPermission.NoChanges,
                ReservedSignatureSize = 8
            });
        PdfDocument document = PdfDocument.Open(signed);
        PdfIndirectReference catalogReference = Assert.IsType<PdfIndirectReference>(
            document.Trailer[Name("Root")]);
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(document.Resolve(catalogReference));
        PdfDictionary permissions = Assert.IsType<PdfDictionary>(catalog[Name("Perms")]);
        PdfIndirectReference certification = Assert.IsType<PdfIndirectReference>(
            permissions[Name("DocMDP")]);
        var stalePermissions = new PdfDictionary(permissions.Select(entry =>
            entry.Key.Equals(Name("DocMDP"))
                ? new KeyValuePair<PdfName, PdfObject>(entry.Key,
                    new PdfIndirectReference(
                        certification.ObjectNumber, certification.Generation + 1))
                : entry));
        var staleCatalog = new PdfDictionary(catalog.Select(entry =>
            entry.Key.Equals(Name("Perms"))
                ? new KeyValuePair<PdfName, PdfObject>(entry.Key, stalePermissions)
                : entry));
        var update = new PdfIncrementalUpdateBuilder(document)
            .ReplaceObject(catalogReference.ObjectNumber, staleCatalog);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            PdfSignatureReader.Read(PdfDocument.Open(update.Build())));

        Assert.Contains("certification signature", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_DoesNotMisclassifyStaleFieldGenerationAsDuplicateField()
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage()
            .AddSignatureField(0, "approval", 20, 20, 160, 40)
            .Build());
        PdfIndirectReference catalogReference = Assert.IsType<PdfIndirectReference>(
            document.Trailer[Name("Root")]);
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(document.Resolve(catalogReference));
        PdfObject formValue = catalog[Name("AcroForm")];
        PdfDictionary form = formValue is PdfIndirectReference formReference
            ? Assert.IsType<PdfDictionary>(document.Resolve(formReference))
            : Assert.IsType<PdfDictionary>(formValue);
        PdfIndirectReference fieldReference = Assert.IsType<PdfIndirectReference>(
            Assert.Single(Assert.IsType<PdfArray>(form[Name("Fields")])));
        PdfDictionary malformedForm = new(form
            .Where(entry => !entry.Key.Equals(Name("Fields")))
            .Append(new KeyValuePair<PdfName, PdfObject>(Name("Fields"), new PdfArray([
                fieldReference,
                new PdfIndirectReference(
                    fieldReference.ObjectNumber, fieldReference.Generation + 1)
            ]))));
        var update = new PdfIncrementalUpdateBuilder(document);
        if (formValue is PdfIndirectReference indirectForm)
            update.ReplaceObject(indirectForm.ObjectNumber, malformedForm);
        else
            update.ReplaceObject(catalogReference.ObjectNumber, new PdfDictionary(catalog
                .Where(entry => !entry.Key.Equals(Name("AcroForm")))
                .Append(new KeyValuePair<PdfName, PdfObject>(
                    Name("AcroForm"), malformedForm))));

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            PdfSignatureReader.Read(PdfDocument.Open(update.Build())));

        Assert.Contains("not a dictionary", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("same field more than once", error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Read_RejectsSeparateAliasesToTheSameSignatureField()
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage()
            .AddSignatureField(0, "approval", 20, 20, 160, 40)
            .Build());
        PdfIndirectReference catalogReference = Assert.IsType<PdfIndirectReference>(
            document.Trailer[Name("Root")]);
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(
            document.Resolve(catalogReference));
        PdfObject formValue = catalog[Name("AcroForm")];
        PdfDictionary form = formValue is PdfIndirectReference formReference
            ? Assert.IsType<PdfDictionary>(document.Resolve(formReference))
            : Assert.IsType<PdfDictionary>(formValue);
        PdfIndirectReference fieldReference = Assert.IsType<PdfIndirectReference>(
            Assert.Single(Assert.IsType<PdfArray>(form[Name("Fields")])));
        var update = new PdfIncrementalUpdateBuilder(document);
        PdfIndirectReference firstAlias = update.AddObject(fieldReference);
        PdfIndirectReference secondAlias = update.AddObject(fieldReference);
        PdfDictionary malformedForm = new(form
            .Where(entry => !entry.Key.Equals(Name("Fields")))
            .Append(new KeyValuePair<PdfName, PdfObject>(Name("Fields"),
                new PdfArray([firstAlias, secondAlias]))));
        if (formValue is PdfIndirectReference indirectForm)
            update.ReplaceObject(indirectForm.ObjectNumber, malformedForm);
        else
            update.ReplaceObject(catalogReference.ObjectNumber,
                new PdfDictionary(catalog
                    .Where(entry => !entry.Key.Equals(Name("AcroForm")))
                    .Append(new KeyValuePair<PdfName, PdfObject>(
                        Name("AcroForm"), malformedForm))));

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            PdfSignatureReader.Read(PdfDocument.Open(update.Build())));

        Assert.Contains("same field more than once", error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Read_RejectsInvalidCertificationTransformVersion()
    {
        byte[] source = new PdfDocumentBuilder().AddBlankPage().Build();
        byte[] signed = PdfDetachedSignatureWriter.Sign(
            PdfDocument.Open(source), _ => [1], new PdfSignatureOptions
            {
                CertificationPermission = PdfSignatureCertificationPermission.NoChanges,
                ReservedSignatureSize = 8
            });
        PdfDocument document = PdfDocument.Open(signed);
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(
            document.Resolve(Assert.IsType<PdfIndirectReference>(document.Trailer[Name("Root")])));
        PdfDictionary permissions = Assert.IsType<PdfDictionary>(catalog[Name("Perms")]);
        PdfIndirectReference signatureReference = Assert.IsType<PdfIndirectReference>(
            permissions[Name("DocMDP")]);
        PdfDictionary signature = Assert.IsType<PdfDictionary>(document.Resolve(signatureReference));
        PdfArray references = Assert.IsType<PdfArray>(signature[Name("Reference")]);
        PdfDictionary reference = Assert.IsType<PdfDictionary>(Assert.Single(references));
        PdfDictionary parameters = Assert.IsType<PdfDictionary>(reference[Name("TransformParams")]);
        var malformedParameters = new PdfDictionary(parameters.Select(entry =>
            entry.Key.Equals(Name("V"))
                ? new KeyValuePair<PdfName, PdfObject>(entry.Key, Name("2.0"))
                : entry));
        var malformedReference = new PdfDictionary(reference.Select(entry =>
            entry.Key.Equals(Name("TransformParams"))
                ? new KeyValuePair<PdfName, PdfObject>(entry.Key, malformedParameters)
                : entry));
        var malformedSignature = new PdfDictionary(signature.Select(entry =>
            entry.Key.Equals(Name("Reference"))
                ? new KeyValuePair<PdfName, PdfObject>(
                    entry.Key, new PdfArray([malformedReference]))
                : entry));
        var update = new PdfIncrementalUpdateBuilder(document)
            .ReplaceObject(signatureReference.ObjectNumber, malformedSignature);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            PdfSignatureReader.Read(PdfDocument.Open(update.Build())));

        Assert.Contains("/1.2", error.Message, StringComparison.Ordinal);
    }

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
}
