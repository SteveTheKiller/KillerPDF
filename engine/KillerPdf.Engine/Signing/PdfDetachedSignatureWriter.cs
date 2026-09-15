using System.Globalization;
using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.CrossReference;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Security;
using KillerPdf.Engine.Writing;

namespace KillerPdf.Engine.Signing;

/// <summary>
/// Adds an approval-signature field in an incremental revision, optionally gives it a visible
/// appearance, and fills its PDF 2.0 byte range with detached CMS bytes supplied by the caller.
/// </summary>
public static class PdfDetachedSignatureWriter
{
    private static readonly PdfName AnnotsName = Name("Annots");
    private static readonly PdfName AcroFormName = Name("AcroForm");
    private static readonly PdfName FieldsName = Name("Fields");
    private static readonly PdfName SignatureFlagsName = Name("SigFlags");
    private static readonly PdfName StructureTreeRootName = Name("StructTreeRoot");
    private static readonly PdfName PermissionsName = Name("Perms");
    private static readonly PdfName DocMdpName = Name("DocMDP");
    private static readonly PdfName FieldNameName = Name("T");
    private static readonly PdfName KidsName = Name("Kids");
    private const long RangeSentinel1 = 1_111_111_111;
    private const long RangeSentinel2 = 2_222_222_222;
    private const long RangeSentinel3 = 3_333_333_333;
    private const long RangeSentinel4 = 4_444_444_444;
    private const int MaximumReservedSignatureSize = 1_048_576;

    /// <summary>Creates an incremental detached signature using caller-supplied CMS generation.</summary>
    public static byte[] Sign(
        PdfDocument document,
        Func<ReadOnlyMemory<byte>, byte[]> createDetachedCms,
        PdfSignatureOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(createDetachedCms);
        options ??= new PdfSignatureOptions();
        ValidateOptions(options);

        PdfPageTree tree = PdfPageTree.Read(document);
        ExistingFormField? existingField = FindField(document, tree, options.FieldName);
        if (existingField is not null)
        {
            if (!existingField.FieldType.Equals(Name("Sig")))
                throw new InvalidOperationException(
                    $"The AcroForm field '{options.FieldName}' is not a signature field.");
            if (existingField.Dictionary.TryGetValue(Name("V"), out PdfObject? existingValue))
            {
                PdfObject resolvedValue = Resolve(document, existingValue,
                    $"The signature field '{options.FieldName}' /V value");
                if (!IsUnsignedValue(resolvedValue))
                    throw new InvalidOperationException(
                        $"The signature field '{options.FieldName}' is already signed.");
            }
        }
        if (existingField is null && options.FieldName.Contains('.'))
            throw new ArgumentException(
                "A new signature field name cannot contain a period.", nameof(options));
        if (existingField is null
            && (options.PageIndex < 0 || options.PageIndex >= tree.Pages.Count))
            throw new ArgumentOutOfRangeException(nameof(options),
                "The signature page index is outside the document.");
        EnforcePasswordPermissions(document, existingField is not null);
        int? certificationPermission = ReadCertificationPermission(document);
        if (options.CertificationPermission.HasValue && certificationPermission.HasValue)
            throw new InvalidOperationException(
                "The document already contains a certification signature.");
        if (options.CertificationPermission.HasValue
            && HasSignedSignatureField(document, tree))
            throw new InvalidOperationException(
                "A certification signature must be the first signed field in the document.");
        if (certificationPermission == 1)
            throw new InvalidOperationException(
                "The document certification signature prohibits all subsequent changes.");
        if (certificationPermission.HasValue && existingField is null)
            throw new InvalidOperationException(
                "A certified document can only be signed through an existing signature field.");

        SeedEvidenceRequirements evidenceRequirements = existingField is not null
            ? EnforceSeedValue(document, existingField, options) : default;
        PdfDictionary? fieldMdpParameters = existingField is null
            ? null : ReadFieldMdpParameters(document, existingField);

        var update = new PdfIncrementalUpdateBuilder(document);
        PdfIndirectReference? appearanceReference = options.VisibleAppearance is null
            ? null : update.AddObject(BuildVisibleAppearance(options.VisibleAppearance));
        PdfIndirectReference signatureReference = update.ReserveObject();
        update.KeepObjectDirect(signatureReference);
        update.SetObject(signatureReference,
            BuildSignatureDictionary(options, fieldMdpParameters));
        PdfDictionary catalogReplacement = tree.Catalog;
        bool catalogChanged = false;
        if (existingField is not null)
        {
            if (existingField.Reference is not null)
                update.ReplaceObject(existingField.Reference.ObjectNumber,
                    FillExistingField(document, update, existingField.Dictionary,
                        signatureReference, appearanceReference));
            PdfDictionary? replacement = existingField.Reference is null
                ? UpdateDirectSignatureField(
                    document, tree, update, options.FieldName, signatureReference, appearanceReference)
                : UpdateSignatureFlags(document, tree, update);
            if (replacement is not null)
            {
                catalogReplacement = replacement;
                catalogChanged = true;
            }
        }
        else
        {
            PdfIndirectReference fieldReference = update.ReserveObject();
            PdfPageTreeEntry page = tree.Pages[options.PageIndex];
            PdfDictionary? formCatalogReplacement = AddSignatureField(
                document, tree, update, fieldReference, options.FieldName);
            if (formCatalogReplacement is not null)
            {
                catalogReplacement = formCatalogReplacement;
                catalogChanged = true;
            }
            TaggedSignatureAssociation association = tree.Catalog.ContainsKey(StructureTreeRootName)
                ? AddTaggedSignatureAssociation(document, tree, update,
                    catalogReplacement, fieldReference, page.Reference, options.FieldName)
                : default;
            if (association.CatalogChanged)
            {
                catalogReplacement = association.Catalog;
                catalogChanged = true;
            }
            var fieldEntries = new List<(string Name, PdfObject Value)>
            {
                ("Type", Name("Annot")),
                ("Subtype", Name("Widget")),
                ("FT", Name("Sig")),
                ("T", UnicodeString(options.FieldName)),
                ("V", signatureReference),
                ("Rect", new PdfArray([
                    new PdfInteger(0), new PdfInteger(0),
                    new PdfInteger(0), new PdfInteger(0)])),
                ("F", new PdfInteger(4)),
                ("P", page.Reference)
            };
            if (options.VisibleAppearance is { } appearance && appearanceReference is not null)
            {
                fieldEntries[5] = ("Rect", Rectangle(appearance.Left, appearance.Bottom,
                    appearance.Left + appearance.Width, appearance.Bottom + appearance.Height));
                fieldEntries.Add(("AP", Dictionary(("N", appearanceReference))));
            }
            if (association.StructureParentKey.HasValue)
                fieldEntries.Add(("StructParent",
                    new PdfInteger(association.StructureParentKey.Value)));
            update.SetObject(fieldReference, Dictionary([.. fieldEntries]));

            PdfArray annotations = page.Dictionary.TryGetValue(
                    AnnotsName, out PdfObject? annotationsValue)
                ? ResolveArray(document, annotationsValue, "The signature page /Annots value")
                : new PdfArray([]);
            update.ReplaceObject(page.Reference.ObjectNumber,
                ReplaceMany(page.Dictionary, new Dictionary<PdfName, PdfObject>
                {
                    [AnnotsName] = new PdfArray(annotations.Append(fieldReference))
                }));
        }
        if (options.CertificationPermission.HasValue)
        {
            PdfDictionary? replacement = AddCertificationPermission(
                document, tree, update, catalogReplacement, signatureReference);
            if (replacement is not null)
            {
                catalogReplacement = replacement;
                catalogChanged = true;
            }
        }
        if (catalogChanged)
            update.ReplaceObject(tree.CatalogReference.ObjectNumber, catalogReplacement);
        byte[] prepared = update.Build(options.IncrementalWriteOptions
            ?? new PdfIncrementalUpdateWriteOptions
            {
                CrossReferenceFormat = document.CrossReferences.Sections[0].IsStream
                    ? PdfCrossReferenceFormat.Stream : PdfCrossReferenceFormat.Table
            });
        FillSignature(prepared, document.Source.Length,
            signatureReference.ObjectNumber, options.ReservedSignatureSize,
            createDetachedCms, evidenceRequirements, options.SignerCertificate);
        return prepared;
    }

    private static void EnforcePasswordPermissions(
        PdfDocument document, bool fillsExistingField)
    {
        if (document.PasswordAuthenticationRole != PdfPasswordAuthenticationRole.User)
            return;
        PdfDocumentPermissions permissions = document.DeclaredPermissions
            ?? throw new InvalidOperationException(
                "The authenticated PDF has no declared permission state.");
        if (fillsExistingField)
        {
            if (!permissions.AllowFormFilling
                && !permissions.AllowAnnotationModification)
                throw new InvalidOperationException(
                    "The PDF user password does not permit signing an existing form field.");
            return;
        }
        if (!permissions.AllowDocumentModification
            || !permissions.AllowAnnotationModification)
            throw new InvalidOperationException(
                "The PDF user password does not permit creating a signature field.");
    }

    private static PdfDictionary BuildSignatureDictionary(
        PdfSignatureOptions options, PdfDictionary? fieldMdpParameters)
    {
        var entries = new List<(string Name, PdfObject Value)>
        {
            ("Type", Name("Sig")),
            ("Filter", Name("Adobe.PPKLite")),
            ("SubFilter", Name("ETSI.CAdES.detached")),
            ("ByteRange", new PdfArray([
                new PdfInteger(RangeSentinel1), new PdfInteger(RangeSentinel2),
                new PdfInteger(RangeSentinel3), new PdfInteger(RangeSentinel4)])),
            ("Contents", new PdfString(
                new byte[options.ReservedSignatureSize], PdfStringForm.Hexadecimal))
        };
        AddOptionalText(entries, "Name", options.SignerName);
        AddOptionalText(entries, "Reason", options.Reason);
        AddOptionalText(entries, "Location", options.Location);
        AddOptionalText(entries, "ContactInfo", options.ContactInformation);
        if (options.SigningTime.HasValue)
            entries.Add(("M", Latin1String(PdfDate(options.SigningTime.Value))));
        var references = new List<PdfObject>();
        if (options.CertificationPermission.HasValue)
            references.Add(Dictionary(
                    ("Type", Name("SigRef")),
                    ("TransformMethod", Name("DocMDP")),
                    ("TransformParams", Dictionary(
                        ("Type", Name("TransformParams")),
                        ("P", new PdfInteger((int)options.CertificationPermission.Value)),
                        ("V", Name("1.2"))))));
        if (fieldMdpParameters is not null)
            references.Add(Dictionary(
                ("Type", Name("SigRef")),
                ("TransformMethod", Name("FieldMDP")),
                ("TransformParams", fieldMdpParameters)));
        if (references.Count > 0)
            entries.Add(("Reference", new PdfArray(references)));
        return Dictionary([.. entries]);
    }

    private static PdfDictionary? AddSignatureField(
        PdfDocument document,
        PdfPageTree tree,
        PdfIncrementalUpdateBuilder update,
        PdfIndirectReference fieldReference,
        string fieldName)
    {
        PdfDictionary form;
        PdfIndirectReference? formReference = null;
        if (tree.Catalog.TryGetValue(AcroFormName, out PdfObject? formValue))
        {
            ResolvedValue resolvedForm = ResolveWithIdentity(
                document, formValue, "The catalog /AcroForm value");
            formReference = resolvedForm.FinalReference;
            form = resolvedForm.Value as PdfDictionary
                ?? throw new InvalidOperationException(
                    "The catalog /AcroForm value is not a dictionary.");
        }
        else
        {
            form = new PdfDictionary([]);
        }

        PdfArray fields = form.TryGetValue(FieldsName, out PdfObject? fieldsValue)
            ? ResolveArray(document, fieldsValue, "The AcroForm /Fields value")
            : new PdfArray([]);
        EnsureFieldNameAvailable(document, fields, fieldName);
        long signatureFlags = 0;
        if (form.TryGetValue(SignatureFlagsName, out PdfObject? flagsValue))
        {
            signatureFlags = Resolve(document, flagsValue) is PdfInteger integer && integer.Value >= 0
                ? integer.Value
                : throw new InvalidOperationException("The AcroForm /SigFlags value is not a non-negative integer.");
        }
        PdfDictionary replacement = ReplaceMany(form, new Dictionary<PdfName, PdfObject>
        {
            [FieldsName] = new PdfArray(fields.Append(fieldReference)),
            [SignatureFlagsName] = new PdfInteger(signatureFlags | 3)
        });
        if (formReference is not null)
        {
            update.ReplaceObject(formReference.ObjectNumber, replacement);
            return null;
        }
        return ReplaceMany(tree.Catalog, new Dictionary<PdfName, PdfObject>
        {
            [AcroFormName] = replacement
        });
    }

    private static void EnsureFieldNameAvailable(
        PdfDocument document, PdfArray fields, string fieldName)
    {
        var active = new HashSet<(int ObjectNumber, int Generation)>();
        var visited = new HashSet<(int ObjectNumber, int Generation)>();
        int fieldCount = 0;
        foreach (PdfObject value in fields) Visit(value, null, 0);

        void Visit(PdfObject value, string? parentName, int depth)
        {
            if (depth >= PdfObjectWriter.MaximumNestingDepth)
                throw new InvalidOperationException("The AcroForm field tree is too deeply nested.");
            if (++fieldCount > 1_000_000)
                throw new NotSupportedException(
                    "The AcroForm field tree contains too many fields.");
            ResolvedValue resolvedField = ResolveWithIdentity(
                document, value, "An AcroForm field");
            PdfIndirectReference? reference = resolvedField.FinalReference;
            if (reference is not null)
            {
                var identity = (reference.ObjectNumber, reference.Generation);
                if (!active.Add(identity))
                    throw new InvalidOperationException("The AcroForm field tree contains a cycle.");
                if (!visited.Add(identity))
                    throw new InvalidOperationException(
                        "The AcroForm field tree references the same field more than once.");
            }
            PdfDictionary field = resolvedField.Value as PdfDictionary
                ?? throw new InvalidOperationException("An AcroForm field is not a dictionary.");
            string? fullName = parentName;
            bool definesName = false;
            if (field.TryGetValue(FieldNameName, out PdfObject? nameValue))
            {
                definesName = true;
                string partialName = Resolve(document, nameValue) is PdfString name
                    ? DecodeString(name)
                    : throw new InvalidOperationException("An AcroForm field /T value is not a string.");
                fullName = string.IsNullOrEmpty(parentName)
                    ? partialName : $"{parentName}.{partialName}";
            }
            if (definesName && fullName == fieldName)
                throw new InvalidOperationException($"The AcroForm already contains a field named '{fieldName}'.");
            if (field.TryGetValue(KidsName, out PdfObject? kidsValue))
            {
                PdfArray kids = ResolveArray(document, kidsValue, "An AcroForm field /Kids value");
                foreach (PdfObject kid in kids) Visit(kid, fullName, depth + 1);
            }
            if (reference is not null)
                active.Remove((reference.ObjectNumber, reference.Generation));
        }
    }

    private static ExistingFormField? FindField(
        PdfDocument document, PdfPageTree tree, string fieldName)
    {
        if (!tree.Catalog.TryGetValue(AcroFormName, out PdfObject? formValue)) return null;
        PdfDictionary form = ResolveDictionary(document, formValue, "The catalog /AcroForm value");
        if (!form.TryGetValue(FieldsName, out PdfObject? fieldsValue)) return null;
        PdfArray fields = ResolveArray(document, fieldsValue, "The AcroForm /Fields value");
        var active = new HashSet<(int ObjectNumber, int Generation)>();
        var visited = new HashSet<(int ObjectNumber, int Generation)>();
        int fieldCount = 0;
        ExistingFormField? match = null;
        foreach (PdfObject value in fields) Visit(value, null, null, 0);
        return match;

        void Visit(PdfObject value, string? parentName, PdfName? inheritedType, int depth)
        {
            if (depth >= PdfObjectWriter.MaximumNestingDepth)
                throw new InvalidOperationException("The AcroForm field tree is too deeply nested.");
            if (++fieldCount > 1_000_000)
                throw new NotSupportedException(
                    "The AcroForm field tree contains too many fields.");
            ResolvedValue resolvedField = ResolveWithIdentity(
                document, value, "An AcroForm field");
            PdfIndirectReference? reference = resolvedField.FinalReference;
            if (reference is not null)
            {
                var identity = (reference.ObjectNumber, reference.Generation);
                if (!active.Add(identity))
                    throw new InvalidOperationException("The AcroForm field tree contains a cycle.");
                if (!visited.Add(identity))
                    throw new InvalidOperationException(
                        "The AcroForm field tree references the same field more than once.");
            }
            PdfDictionary field = resolvedField.Value as PdfDictionary
                ?? throw new InvalidOperationException("An AcroForm field is not a dictionary.");
            PdfName? fieldType = inheritedType;
            if (field.TryGetValue(Name("FT"), out PdfObject? typeValue))
                fieldType = Resolve(document, typeValue) as PdfName
                    ?? throw new InvalidOperationException("An AcroForm field /FT value is not a name.");
            string? fullName = parentName;
            bool definesName = false;
            if (field.TryGetValue(FieldNameName, out PdfObject? nameValue))
            {
                definesName = true;
                string partialName = Resolve(document, nameValue) is PdfString name
                    ? DecodeString(name)
                    : throw new InvalidOperationException("An AcroForm field /T value is not a string.");
                fullName = string.IsNullOrEmpty(parentName)
                    ? partialName : $"{parentName}.{partialName}";
            }
            if (definesName && fullName == fieldName)
            {
                if (match is not null)
                    throw new InvalidOperationException(
                        $"The AcroForm contains more than one field named '{fieldName}'.");
                match = new ExistingFormField(reference, field, fieldType
                    ?? throw new InvalidOperationException(
                        $"The AcroForm field '{fieldName}' has no field type."));
            }
            if (field.TryGetValue(KidsName, out PdfObject? kidsValue))
            {
                PdfArray kids = ResolveArray(document, kidsValue, "An AcroForm field /Kids value");
                foreach (PdfObject kid in kids) Visit(kid, fullName, fieldType, depth + 1);
            }
            if (reference is not null)
                active.Remove((reference.ObjectNumber, reference.Generation));
        }
    }

    private static PdfDictionary? ReadFieldMdpParameters(
        PdfDocument document, ExistingFormField field)
    {
        if (!field.Dictionary.TryGetValue(Name("Lock"), out PdfObject? lockValue))
            return null;
        if (lockValue is not PdfIndirectReference)
            throw new InvalidOperationException(
                "A signature field /Lock value is not an indirect reference.");
        PdfDictionary fieldLock = ResolveDictionary(
            document, lockValue, "The signature field /Lock value");
        if (!fieldLock.TryGetValue(Name("Type"), out PdfObject? lockTypeValue)
            || Resolve(document, lockTypeValue) is not PdfName lockType
            || lockType.ValueAsLatin1() != "SigFieldLock")
            throw new InvalidOperationException(
                "A signature field lock /Type is not /SigFieldLock.");
        if (!fieldLock.TryGetValue(Name("Action"), out PdfObject? actionValue)
            || Resolve(document, actionValue) is not PdfName action
            || action.ValueAsLatin1() is not ("All" or "Include" or "Exclude"))
            throw new InvalidOperationException(
                "A signature field lock has no valid /Action value.");
        string actionName = action.ValueAsLatin1();
        PdfArray? fields = null;
        if (fieldLock.TryGetValue(Name("Fields"), out PdfObject? fieldsValue))
        {
            fields = ResolveArray(document, fieldsValue, "The signature field lock /Fields value");
            if (fields.Count == 0
                || fields.Any(value => Resolve(document, value) is not PdfString))
                throw new InvalidOperationException(
                    "A signature field lock /Fields array must contain field-name strings.");
        }
        if (actionName == "All" && fields is not null)
            throw new InvalidOperationException(
                "An all-fields signature lock cannot contain a /Fields array.");
        if (actionName != "All" && fields is null)
            throw new InvalidOperationException(
                "An include or exclude signature lock requires a /Fields array.");

        var entries = new List<(string Name, PdfObject Value)>
        {
            ("Type", Name("TransformParams")),
            ("Action", action),
            ("V", Name("1.2"))
        };
        if (fields is not null) entries.Add(("Fields", fields));
        if (fieldLock.TryGetValue(Name("P"), out PdfObject? permissionValue))
        {
            if (Resolve(document, permissionValue) is not PdfInteger permission
                || permission.Value is < 1 or > 3)
                throw new InvalidOperationException(
                    "A signature field lock /P value is not an integer from 1 through 3.");
            entries.Add(("P", permission));
        }
        return Dictionary([.. entries]);
    }

    private static SeedEvidenceRequirements EnforceSeedValue(
        PdfDocument document, ExistingFormField field, PdfSignatureOptions options)
    {
        if (!field.Dictionary.TryGetValue(Name("SV"), out PdfObject? seedValue)) return default;
        if (seedValue is not PdfIndirectReference)
            throw new InvalidOperationException(
                "A signature field /SV value is not an indirect reference.");
        PdfDictionary seed = ResolveDictionary(document, seedValue, "The signature field /SV value");
        if (!seed.TryGetValue(Name("Type"), out PdfObject? typeValue)
            || Resolve(document, typeValue) is not PdfName type
            || type.ValueAsLatin1() != "SV")
            throw new InvalidOperationException("A signature seed-value /Type is not /SV.");
        long flags = 0;
        if (seed.TryGetValue(Name("Ff"), out PdfObject? flagsValue))
            flags = Resolve(document, flagsValue) is PdfInteger integer && integer.Value >= 0
                ? integer.Value
                : throw new InvalidOperationException(
                    "A signature seed-value /Ff value is not a non-negative integer.");

        if ((flags & 1) != 0)
            RequireName(seed, document, "Filter", "Adobe.PPKLite",
                "The signature seed value requires an unsupported signing handler.");
        if ((flags & (1 << 1)) != 0)
            RequireNameInArray(seed, document, "SubFilter", "ETSI.CAdES.detached",
                "The signature seed value requires an unsupported signature encoding.");
        if ((flags & (1 << 2)) != 0)
        {
            if (!seed.TryGetValue(Name("V"), out PdfObject? versionValue)
                || Resolve(document, versionValue) is not PdfReal version
                || version.Value is < 1 or > 3)
                throw new InvalidOperationException(
                    "The signature seed value requires an unsupported parser version.");
        }
        if ((flags & (1 << 3)) != 0)
            RequireStringInArray(seed, document, "Reasons", options.Reason,
                "The signing reason does not satisfy the signature seed value.");
        if ((flags & (1 << 4)) != 0)
            RequireStringInArray(seed, document, "LegalAttestation",
                options.LegalAttestation,
                "The legal attestation does not satisfy the signature seed value.");
        if ((flags & (1 << 5)) != 0)
        {
            if (!seed.TryGetValue(Name("AddRevInfo"), out PdfObject? revocationValue)
                || Resolve(document, revocationValue) is not PdfBoolean { Value: true }
                || !options.IncludesRevocationInformation)
                throw new InvalidOperationException(
                    "The signature seed value requires embedded revocation information.");
        }
        if ((flags & (1 << 6)) != 0)
            RequireNameInArray(seed, document, "DigestMethod",
                options.DigestMethod switch
                {
                    PdfSignatureDigestMethod.Sha256 => "SHA256",
                    PdfSignatureDigestMethod.Sha384 => "SHA384",
                    PdfSignatureDigestMethod.Sha512 => "SHA512",
                    _ => throw new ArgumentOutOfRangeException(nameof(options))
                }, "The selected digest method does not satisfy the signature seed value.");
        if ((flags & (1 << 7)) != 0)
        {
            string required = options.DocumentLockIntent switch
            {
                PdfSignatureDocumentLockIntent.Automatic => "auto",
                PdfSignatureDocumentLockIntent.Lock => "true",
                PdfSignatureDocumentLockIntent.DoNotLock => "false",
                _ => string.Empty
            };
            RequireName(seed, document, "LockDocument", required,
                "The document-lock intent does not satisfy the signature seed value.");
        }
        if ((flags & (1 << 8)) != 0)
        {
            if (!seed.TryGetValue(Name("AppearanceFilter"), out PdfObject? appearanceValue)
                || Resolve(document, appearanceValue) is not PdfString appearance
                || options.AppearanceName is null
                || DecodeString(appearance) != options.AppearanceName)
                throw new InvalidOperationException(
                    "The signing appearance does not satisfy the signature seed value.");
        }

        if (seed.TryGetValue(Name("MDP"), out PdfObject? mdpValue))
        {
            PdfDictionary mdp = ResolveDictionary(document, mdpValue,
                "The signature seed-value /MDP value");
            if (!mdp.TryGetValue(Name("P"), out PdfObject? permissionValue)
                || Resolve(document, permissionValue) is not PdfInteger permission
                || permission.Value is < 0 or > 3
                || (int?)permission.Value != (int?)(options.CertificationPermission
                    ?? PdfSignatureCertificationPermission.ApprovalSignature))
                throw new InvalidOperationException(
                    "The signature type does not satisfy the signature seed value.");
        }
        bool requireTimestamp = false;
        bool requireCertificate = false;
        if (seed.TryGetValue(Name("TimeStamp"), out PdfObject? timestampValue))
        {
            PdfDictionary timestamp = ResolveDictionary(document, timestampValue,
                "The signature seed-value /TimeStamp value");
            if (timestamp.TryGetValue(Name("Ff"), out PdfObject? timestampFlags))
            {
                if (Resolve(document, timestampFlags)
                    is not PdfInteger { Value: >= 0 } timestampFlagInteger)
                    throw new InvalidOperationException(
                        "The signature timestamp /Ff value is not a non-negative integer.");
                if ((timestampFlagInteger.Value & 1) != 0)
                {
                    if (!timestamp.TryGetValue(Name("URL"), out PdfObject? timestampUrlValue)
                        || Resolve(document, timestampUrlValue) is not PdfString timestampUrl
                        || options.TimestampServerUrl is null
                        || DecodeString(timestampUrl) != options.TimestampServerUrl)
                        throw new InvalidOperationException(
                            "The timestamp server does not satisfy the signature seed value.");
                    requireTimestamp = true;
                }
            }
        }
        if (seed.TryGetValue(Name("Cert"), out PdfObject? certificateValue))
        {
            PdfDictionary certificate = ResolveDictionary(document, certificateValue,
                "The signature seed-value /Cert value");
            if (!certificate.TryGetValue(Name("Type"), out PdfObject? certificateTypeValue)
                || Resolve(document, certificateTypeValue) is not PdfName certificateType
                || certificateType.ValueAsLatin1() != "SVCert")
                throw new InvalidOperationException(
                    "A certificate seed-value /Type is not /SVCert.");
            if (certificate.TryGetValue(Name("Ff"), out PdfObject? certificateFlags))
            {
                if (Resolve(document, certificateFlags)
                    is not PdfInteger { Value: >= 0 } certificateFlagInteger)
                    throw new InvalidOperationException(
                        "The certificate seed-value /Ff value is not a non-negative integer.");
                if (certificateFlagInteger.Value != 0)
                {
                    EnforceCertificateSeed(
                        document, certificate, certificateFlagInteger.Value, options);
                    requireCertificate = true;
                }
            }
        }
        return new SeedEvidenceRequirements(requireTimestamp, requireCertificate);
    }

    private static void EnforceCertificateSeed(
        PdfDocument document, PdfDictionary seed, long flags, PdfSignatureOptions options)
    {
        if ((flags & (1 << 6)) != 0)
        {
            if (!seed.TryGetValue(Name("URL"), out PdfObject? urlValue)
                || Resolve(document, urlValue) is not PdfString url
                || options.CertificateAcquisitionUrl is null
                || DecodeString(url) != options.CertificateAcquisitionUrl)
                throw new InvalidOperationException(
                    "The certificate-acquisition URL does not satisfy the signature seed value.");
        }
        long certificateFlags = flags & ((1 << 6) - 1);
        if (certificateFlags == 0) return;
        if (options.SignerCertificate.IsEmpty)
            throw new InvalidOperationException(
                "The signature seed value requires signer-certificate evidence.");
        X509Certificate2 signer;
        try
        {
            signer = X509CertificateLoader.LoadCertificate(options.SignerCertificate.Span);
        }
        catch (CryptographicException exception)
        {
            throw new ArgumentException(
                "The signer certificate is not valid DER-encoded X.509 data.",
                nameof(options), exception);
        }
        using (signer)
        {
            if ((flags & 1) != 0
                && !CertificateArrayContains(
                    document, seed, "Subject", options.SignerCertificate))
                throw new InvalidOperationException(
                    "The signer certificate does not satisfy the acceptable-subject constraint.");
            if ((flags & (1 << 1)) != 0)
            {
                IReadOnlyList<ReadOnlyMemory<byte>> chain = options.CertificateChain ?? [];
                if (!IssuerConstraintMatches(document, seed, signer, chain))
                    throw new InvalidOperationException(
                        "The signer certificate chain does not satisfy the acceptable-issuer constraint.");
            }
            if ((flags & (1 << 2)) != 0
                && !CertificatePolicyMatches(document, seed, signer))
                throw new InvalidOperationException(
                    "The signer certificate does not satisfy the certificate-policy constraint.");
            if ((flags & (1 << 3)) != 0
                && !SubjectDistinguishedNameMatches(document, seed, signer))
                throw new InvalidOperationException(
                    "The signer certificate does not satisfy the subject-name constraint.");
            if ((flags & (1 << 5)) != 0
                && !KeyUsageMatches(document, seed, signer))
                throw new InvalidOperationException(
                    "The signer certificate does not satisfy the key-usage constraint.");
        }
    }

    private static bool CertificateArrayContains(
        PdfDocument document, PdfDictionary seed, string key, ReadOnlyMemory<byte> expected)
    {
        if (!seed.TryGetValue(Name(key), out PdfObject? value)) return false;
        PdfArray certificates = ResolveArray(document, value,
            $"The certificate seed-value /{key} value");
        PdfObject[] certificateValues = [.. certificates.Select(item => Resolve(document, item))];
        if (certificateValues.Any(item => item is not PdfString))
            throw new InvalidOperationException(
                $"The certificate seed-value /{key} array contains a non-string value.");
        return certificateValues.Cast<PdfString>()
            .Any(item => item.Bytes.Span.SequenceEqual(expected.Span));
    }

    private static bool IssuerConstraintMatches(
        PdfDocument document,
        PdfDictionary seed,
        X509Certificate2 signer,
        IReadOnlyList<ReadOnlyMemory<byte>> chain)
    {
        if (!seed.TryGetValue(Name("Issuer"), out PdfObject? value)) return false;
        PdfArray acceptable = ResolveArray(document, value,
            "The certificate seed-value /Issuer value");
        PdfObject[] acceptableValues = [.. acceptable.Select(item => Resolve(document, item))];
        if (acceptableValues.Any(item => item is not PdfString))
            throw new InvalidOperationException(
                "The certificate seed-value /Issuer array contains a non-string value.");
        foreach (ReadOnlyMemory<byte> candidateBytes in chain)
        {
            if (!acceptableValues.Cast<PdfString>().Any(item =>
                item.Bytes.Span.SequenceEqual(candidateBytes.Span))) continue;
            try
            {
                using X509Certificate2 candidate =
                    X509CertificateLoader.LoadCertificate(candidateBytes.Span);
                if (candidate.SubjectName.RawData.AsSpan()
                    .SequenceEqual(signer.IssuerName.RawData)) return true;
            }
            catch (CryptographicException exception)
            {
                throw new ArgumentException(
                    "The signer certificate chain contains invalid DER-encoded X.509 data.",
                    nameof(chain), exception);
            }
        }
        return false;
    }

    private static bool CertificatePolicyMatches(
        PdfDocument document, PdfDictionary seed, X509Certificate2 signer)
    {
        if (!seed.TryGetValue(Name("OID"), out PdfObject? value)) return false;
        PdfArray required = ResolveArray(document, value, "The certificate seed-value /OID value");
        PdfObject[] requiredValues = [.. required.Select(item => Resolve(document, item))];
        if (requiredValues.Any(item => item is not PdfString))
            throw new InvalidOperationException(
                "The certificate seed-value /OID array contains a non-string value.");
        var policies = new HashSet<string>(StringComparer.Ordinal);
        X509Extension? extension = signer.Extensions["2.5.29.32"];
        if (extension is not null)
        {
            try
            {
                var reader = new AsnReader(extension.RawData, AsnEncodingRules.DER);
                AsnReader sequence = reader.ReadSequence();
                while (sequence.HasData)
                {
                    AsnReader policy = sequence.ReadSequence();
                    policies.Add(policy.ReadObjectIdentifier());
                }
            }
            catch (AsnContentException)
            {
                return false;
            }
        }
        return requiredValues.Cast<PdfString>()
            .Any(item => policies.Contains(Encoding.Latin1.GetString(item.Bytes.Span)));
    }

    private static bool SubjectDistinguishedNameMatches(
        PdfDocument document, PdfDictionary seed, X509Certificate2 signer)
    {
        if (!seed.TryGetValue(Name("SubjectDN"), out PdfObject? value)) return false;
        PdfArray alternatives = ResolveArray(
            document, value, "The certificate seed-value /SubjectDN value");
        Dictionary<string, List<string>> subject = ReadDistinguishedName(signer.SubjectName.RawData);
        foreach (PdfObject alternativeValue in alternatives)
        {
            PdfDictionary alternative = ResolveDictionary(
                document, alternativeValue, "A certificate subject-name constraint");
            bool matches = true;
            foreach ((PdfName key, PdfObject expectedValue) in alternative)
            {
                if (Resolve(document, expectedValue) is not PdfString expected
                    || !subject.TryGetValue(key.ValueAsLatin1(), out List<string>? actual)
                    || !actual.Contains(DecodeString(expected), StringComparer.OrdinalIgnoreCase))
                {
                    matches = false;
                    break;
                }
            }
            if (matches) return true;
        }
        return false;
    }

    private static Dictionary<string, List<string>> ReadDistinguishedName(ReadOnlyMemory<byte> raw)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var reader = new AsnReader(raw, AsnEncodingRules.DER);
            AsnReader name = reader.ReadSequence();
            while (name.HasData)
            {
                AsnReader set = name.ReadSetOf();
                while (set.HasData)
                {
                    AsnReader attribute = set.ReadSequence();
                    string oid = attribute.ReadObjectIdentifier();
                    string key = oid switch
                    {
                        "2.5.4.3" => "cn",
                        "2.5.4.5" => "serialnumber",
                        "2.5.4.6" => "c",
                        "2.5.4.7" => "l",
                        "2.5.4.8" => "st",
                        "2.5.4.10" => "o",
                        "2.5.4.11" => "ou",
                        "1.2.840.113549.1.9.1" => "emailaddress",
                        _ => oid
                    };
                    Asn1Tag tag = attribute.PeekTag();
                    UniversalTagNumber stringType = (UniversalTagNumber)tag.TagValue;
                    string text = attribute.ReadCharacterString(stringType);
                    if (!result.TryGetValue(key, out List<string>? values))
                        result.Add(key, values = []);
                    values.Add(text);
                }
            }
        }
        catch (AsnContentException)
        {
            return [];
        }
        return result;
    }

    private static bool KeyUsageMatches(
        PdfDocument document, PdfDictionary seed, X509Certificate2 signer)
    {
        if (!seed.TryGetValue(Name("KeyUsage"), out PdfObject? value)) return false;
        PdfArray patterns = ResolveArray(document, value,
            "The certificate seed-value /KeyUsage value");
        PdfObject[] patternValues = [.. patterns.Select(item => Resolve(document, item))];
        if (patternValues.Any(item => item is not PdfString))
            throw new InvalidOperationException(
                "The certificate seed-value /KeyUsage array contains a non-string value.");
        X509KeyUsageFlags actual = signer.Extensions.OfType<X509KeyUsageExtension>()
            .Select(extension => extension.KeyUsages).FirstOrDefault();
        X509KeyUsageFlags[] bits =
        [
            X509KeyUsageFlags.DigitalSignature,
            X509KeyUsageFlags.NonRepudiation,
            X509KeyUsageFlags.KeyEncipherment,
            X509KeyUsageFlags.DataEncipherment,
            X509KeyUsageFlags.KeyAgreement,
            X509KeyUsageFlags.KeyCertSign,
            X509KeyUsageFlags.CrlSign,
            X509KeyUsageFlags.EncipherOnly,
            X509KeyUsageFlags.DecipherOnly
        ];
        return patternValues.Cast<PdfString>().Any(patternValue =>
        {
            string pattern = Encoding.Latin1.GetString(patternValue.Bytes.Span);
            return pattern.Length == bits.Length && pattern.Select((constraint, index) =>
            {
                bool present = (actual & bits[index]) != 0;
                return constraint == 'X' || constraint == '1' && present
                    || constraint == '0' && !present;
            }).All(matches => matches);
        });
    }

    private static void RequireName(
        PdfDictionary dictionary, PdfDocument document,
        string key, string expected, string message)
    {
        if (expected.Length == 0
            || !dictionary.TryGetValue(Name(key), out PdfObject? value)
            || Resolve(document, value) is not PdfName name
            || name.ValueAsLatin1() != expected)
            throw new InvalidOperationException(message);
    }

    private static void RequireNameInArray(
        PdfDictionary dictionary, PdfDocument document, string key,
        string expected, string message)
    {
        if (!dictionary.TryGetValue(Name(key), out PdfObject? value))
            throw new InvalidOperationException(message);
        PdfArray values = ResolveArray(document, value, $"The signature seed-value /{key} value");
        PdfObject[] resolved = [.. values.Select(item => Resolve(document, item))];
        if (!resolved.OfType<PdfName>().Any(name => name.ValueAsLatin1() == expected)
            || resolved.Any(item => item is not PdfName))
            throw new InvalidOperationException(message);
    }

    private static void RequireStringInArray(
        PdfDictionary dictionary, PdfDocument document, string key,
        string? expected, string message)
    {
        if (expected is null || !dictionary.TryGetValue(Name(key), out PdfObject? value))
            throw new InvalidOperationException(message);
        PdfArray values = ResolveArray(document, value, $"The signature seed-value /{key} value");
        PdfObject[] resolved = [.. values.Select(item => Resolve(document, item))];
        if (!resolved.OfType<PdfString>().Any(item => DecodeString(item) == expected)
            || resolved.Any(item => item is not PdfString))
            throw new InvalidOperationException(message);
    }

    private static int? ReadCertificationPermission(PdfDocument document) =>
        PdfSignatureReader.ReadCertificationPermission(document) is { } permission
            ? (int)permission : null;

    private static bool HasSignedSignatureField(PdfDocument document, PdfPageTree tree)
    {
        if (!tree.Catalog.TryGetValue(AcroFormName, out PdfObject? formValue)) return false;
        PdfDictionary form = ResolveDictionary(document, formValue, "The catalog /AcroForm value");
        if (!form.TryGetValue(FieldsName, out PdfObject? fieldsValue)) return false;
        PdfArray fields = ResolveArray(document, fieldsValue, "The AcroForm /Fields value");
        var active = new HashSet<(int ObjectNumber, int Generation)>();
        var visited = new HashSet<(int ObjectNumber, int Generation)>();
        int fieldCount = 0;
        foreach (PdfObject field in fields)
            if (Visit(field, null, 0)) return true;
        return false;

        bool Visit(PdfObject value, PdfName? inheritedType, int depth)
        {
            if (depth >= PdfObjectWriter.MaximumNestingDepth)
                throw new InvalidOperationException("The AcroForm field tree is too deeply nested.");
            if (++fieldCount > 1_000_000)
                throw new NotSupportedException(
                    "The AcroForm field tree contains too many fields.");
            ResolvedValue resolvedField = ResolveWithIdentity(
                document, value, "An AcroForm field");
            PdfIndirectReference? reference = resolvedField.FinalReference;
            if (reference is not null)
            {
                var identity = (reference.ObjectNumber, reference.Generation);
                if (!active.Add(identity))
                    throw new InvalidOperationException("The AcroForm field tree contains a cycle.");
                if (!visited.Add(identity))
                    throw new InvalidOperationException(
                        "The AcroForm field tree references the same field more than once.");
            }
            PdfDictionary field = resolvedField.Value as PdfDictionary
                ?? throw new InvalidOperationException("An AcroForm field is not a dictionary.");
            PdfName? fieldType = inheritedType;
            if (field.TryGetValue(Name("FT"), out PdfObject? typeValue))
                fieldType = Resolve(document, typeValue) as PdfName
                    ?? throw new InvalidOperationException("An AcroForm field /FT value is not a name.");
            if (fieldType?.Equals(Name("Sig")) == true
                && field.TryGetValue(Name("V"), out PdfObject? signatureValue))
            {
                PdfObject resolved = Resolve(document, signatureValue,
                    "A signed signature field /V value");
                if (!IsUnsignedValue(resolved))
                {
                    PdfDictionary signature = resolved as PdfDictionary
                        ?? throw new InvalidOperationException(
                            "A signed signature field /V value is not a dictionary.");
                    if (!signature.TryGetValue(Name("Type"), out PdfObject? signatureTypeValue)
                        || Resolve(document, signatureTypeValue) is not PdfName signatureType
                        || !signatureType.Equals(Name("Sig")))
                        throw new InvalidOperationException(
                            "A signed signature field /V dictionary does not declare /Type /Sig.");
                    return true;
                }
            }
            if (field.TryGetValue(KidsName, out PdfObject? kidsValue))
            {
                PdfArray kids = ResolveArray(document, kidsValue, "An AcroForm field /Kids value");
                foreach (PdfObject kid in kids)
                    if (Visit(kid, fieldType, depth + 1)) return true;
            }
            if (reference is not null)
                active.Remove((reference.ObjectNumber, reference.Generation));
            return false;
        }
    }

    private static bool IsUnsignedValue(PdfObject value) =>
        value is PdfNull || value is PdfString { Bytes.Length: 0 };

    private static PdfDictionary? AddCertificationPermission(
        PdfDocument document,
        PdfPageTree tree,
        PdfIncrementalUpdateBuilder update,
        PdfDictionary catalog,
        PdfIndirectReference signatureReference)
    {
        if (!tree.Catalog.TryGetValue(PermissionsName, out PdfObject? permissionsValue))
            return ReplaceMany(catalog, new Dictionary<PdfName, PdfObject>
            {
                [PermissionsName] = Dictionary(("DocMDP", signatureReference))
            });

        ResolvedValue resolvedPermissions = ResolveWithIdentity(
            document, permissionsValue, "The catalog /Perms value");
        PdfIndirectReference? permissionsReference = resolvedPermissions.FinalReference;
        PdfDictionary permissions = resolvedPermissions.Value as PdfDictionary
            ?? throw new InvalidOperationException(
                "The catalog /Perms value is not a dictionary.");
        if (permissions.ContainsKey(DocMdpName))
            throw new InvalidOperationException(
                "The document already contains a certification signature.");
        PdfDictionary replacement = ReplaceMany(permissions, new Dictionary<PdfName, PdfObject>
        {
            [DocMdpName] = signatureReference
        });
        if (permissionsReference is not null)
        {
            update.ReplaceObject(permissionsReference.ObjectNumber, replacement);
            return null;
        }
        return ReplaceMany(catalog, new Dictionary<PdfName, PdfObject>
        {
            [PermissionsName] = replacement
        });
    }

    private static TaggedSignatureAssociation AddTaggedSignatureAssociation(
        PdfDocument document,
        PdfPageTree tree,
        PdfIncrementalUpdateBuilder update,
        PdfDictionary catalog,
        PdfIndirectReference fieldReference,
        PdfIndirectReference pageReference,
        string fieldName)
    {
        PdfObject rootValue = tree.Catalog[StructureTreeRootName];
        ResolvedValue resolvedRoot = ResolveWithIdentity(
            document, rootValue, "The catalog /StructTreeRoot value");
        PdfIndirectReference? existingRootReference = resolvedRoot.FinalReference;
        PdfDictionary root = resolvedRoot.Value as PdfDictionary
            ?? throw new InvalidOperationException(
                "The catalog /StructTreeRoot value is not a dictionary.");
        if (!root.TryGetValue(Name("ParentTree"), out PdfObject? parentTreeValue))
            throw new InvalidOperationException(
                "The tagged PDF structure root has no /ParentTree value.");
        IReadOnlyList<PdfNumberTreeEntry> entries = PdfNumberTree.Read(document, parentTreeValue);
        long minimumNextKey = entries.Count == 0 ? 0 : checked(entries.Max(entry => entry.Key) + 1);
        long structureParentKey = minimumNextKey;
        if (root.TryGetValue(Name("ParentTreeNextKey"), out PdfObject? nextKeyValue))
        {
            if (Resolve(document, nextKeyValue) is not PdfInteger nextKey
                || nextKey.Value < minimumNextKey)
                throw new InvalidOperationException(
                    "The structure root /ParentTreeNextKey value is not a valid next key.");
            structureParentKey = nextKey.Value;
        }
        PdfIndirectReference structureElementReference = update.ReserveObject();
        PdfIndirectReference structureRootReference = existingRootReference ?? update.ReserveObject();
        var parentNumbers = new List<PdfObject>((entries.Count + 1) * 2);
        foreach (PdfNumberTreeEntry entry in entries.Append(
                     new PdfNumberTreeEntry(structureParentKey, structureElementReference))
                     .OrderBy(entry => entry.Key))
        {
            parentNumbers.Add(new PdfInteger(entry.Key));
            parentNumbers.Add(entry.Value);
        }
        PdfDictionary parentTreeReplacement = Dictionary(
            ("Nums", new PdfArray(parentNumbers)));
        PdfDictionary rootReplacement = root;
        ResolvedValue resolvedParentTree = ResolveWithIdentity(
            document, parentTreeValue, "The structure root /ParentTree value");
        if (resolvedParentTree.Value is not PdfDictionary)
            throw new InvalidOperationException(
                "The structure root /ParentTree value is not a dictionary.");
        if (resolvedParentTree.FinalReference is PdfIndirectReference parentTreeReference)
            update.ReplaceObject(parentTreeReference.ObjectNumber, parentTreeReplacement);
        else
            rootReplacement = ReplaceMany(rootReplacement, new Dictionary<PdfName, PdfObject>
            {
                [Name("ParentTree")] = parentTreeReplacement
            });
        PdfObject structureKids = structureElementReference;
        if (rootReplacement.TryGetValue(Name("K"), out PdfObject? kidsValue))
        {
            ResolvedValue resolvedKids = ResolveWithIdentity(
                document, kidsValue, "The structure root /K value");
            if (resolvedKids.Value is PdfArray kids)
            {
                var appendedKids = new PdfArray(kids.Append(structureElementReference));
                if (resolvedKids.FinalReference is PdfIndirectReference kidsReference)
                {
                    update.ReplaceObject(kidsReference.ObjectNumber, appendedKids);
                    structureKids = kidsValue;
                }
                else
                    structureKids = appendedKids;
            }
            else
                structureKids = new PdfArray([kidsValue, structureElementReference]);
        }
        rootReplacement = ReplaceMany(rootReplacement, new Dictionary<PdfName, PdfObject>
        {
            [Name("K")] = structureKids,
            [Name("ParentTreeNextKey")] = new PdfInteger(checked(structureParentKey + 1))
        });
        update.SetObject(structureElementReference, Dictionary(
            ("Type", Name("StructElem")),
            ("S", Name("Form")),
            ("P", structureRootReference),
            ("Pg", pageReference),
            ("Alt", UnicodeString(fieldName)),
            ("K", Dictionary(
                ("Type", Name("OBJR")),
                ("Obj", fieldReference),
                ("Pg", pageReference)))));
        if (existingRootReference is not null)
        {
            update.ReplaceObject(existingRootReference.ObjectNumber, rootReplacement);
            return new TaggedSignatureAssociation(structureParentKey, catalog, false);
        }
        if (root.TryGetValue(Name("K"), out PdfObject? existingKids))
        {
            PdfObject resolvedExistingKids = Resolve(
                document, existingKids, "The structure root /K value");
            IEnumerable<PdfObject> topLevelKids = resolvedExistingKids is PdfArray array
                ? array : [existingKids];
            foreach (PdfObject kidValue in topLevelKids)
            {
                ResolvedValue resolvedKid = ResolveWithIdentity(
                    document, kidValue, "A top-level structure element");
                PdfIndirectReference kidReference = resolvedKid.FinalReference
                    ?? throw new InvalidOperationException(
                        "A direct structure root contains a non-indirect top-level element.");
                PdfDictionary kid = resolvedKid.Value as PdfDictionary
                    ?? throw new InvalidOperationException(
                        "A top-level structure element is not a dictionary.");
                update.ReplaceObject(kidReference.ObjectNumber,
                    ReplaceMany(kid, new Dictionary<PdfName, PdfObject>
                    {
                        [Name("P")] = structureRootReference
                    }));
            }
        }
        update.SetObject(structureRootReference, rootReplacement);
        return new TaggedSignatureAssociation(structureParentKey,
            ReplaceMany(catalog, new Dictionary<PdfName, PdfObject>
            {
                [StructureTreeRootName] = structureRootReference
            }), true);
    }

    private static PdfDictionary? UpdateSignatureFlags(
        PdfDocument document, PdfPageTree tree, PdfIncrementalUpdateBuilder update)
    {
        PdfObject formValue = tree.Catalog[AcroFormName];
        ResolvedValue resolvedForm = ResolveWithIdentity(
            document, formValue, "The catalog /AcroForm value");
        PdfIndirectReference? formReference = resolvedForm.FinalReference;
        PdfDictionary form = resolvedForm.Value as PdfDictionary
            ?? throw new InvalidOperationException(
                "The catalog /AcroForm value is not a dictionary.");
        long flags = 0;
        if (form.TryGetValue(SignatureFlagsName, out PdfObject? flagsValue))
            flags = Resolve(document, flagsValue) is PdfInteger integer && integer.Value >= 0
                ? integer.Value
                : throw new InvalidOperationException(
                    "The AcroForm /SigFlags value is not a non-negative integer.");
        PdfDictionary replacement = ReplaceMany(form, new Dictionary<PdfName, PdfObject>
        {
            [SignatureFlagsName] = new PdfInteger(flags | 3)
        });
        if (formReference is not null)
        {
            update.ReplaceObject(formReference.ObjectNumber, replacement);
            return null;
        }
        return ReplaceMany(tree.Catalog, new Dictionary<PdfName, PdfObject>
        {
            [AcroFormName] = replacement
        });
    }

    private static PdfDictionary FillExistingField(
        PdfDocument document,
        PdfIncrementalUpdateBuilder update,
        PdfDictionary field,
        PdfIndirectReference signatureReference,
        PdfIndirectReference? appearanceReference)
    {
        PdfDictionary replacement = ReplaceMany(field, new Dictionary<PdfName, PdfObject>
        {
            [Name("V")] = signatureReference
        });
        if (appearanceReference is null) return replacement;

        if (field.TryGetValue(Name("Subtype"), out PdfObject? subtype)
            && Resolve(document, subtype) is PdfName widgetType
            && widgetType.Equals(Name("Widget")))
            replacement = WithAppearance(replacement);

        if (field.TryGetValue(KidsName, out PdfObject? kidsValue))
        {
            ResolvedValue resolvedKids = ResolveWithIdentity(document, kidsValue,
                "The signature field /Kids value");
            PdfArray kids = resolvedKids.Value as PdfArray
                ?? throw new InvalidOperationException("The signature field /Kids value is not an array.");
            var rewritten = new List<PdfObject>(kids.Count);
            bool changed = false;
            foreach (PdfObject kid in kids)
            {
                ResolvedValue resolved = ResolveWithIdentity(document, kid,
                    "A signature widget");
                PdfDictionary widget = resolved.Value as PdfDictionary
                    ?? throw new InvalidOperationException("A signature widget is not a dictionary.");
                if (!widget.ContainsKey(FieldNameName)
                    && widget.TryGetValue(Name("Subtype"), out PdfObject? type)
                    && Resolve(document, type) is PdfName name && name.Equals(Name("Widget")))
                {
                    PdfDictionary updated = WithAppearance(widget);
                    if (resolved.FinalReference is { } reference)
                        update.ReplaceObject(reference.ObjectNumber, updated);
                    else
                    {
                        rewritten.Add(updated);
                        changed = true;
                        continue;
                    }
                }
                rewritten.Add(kid);
            }
            if (changed)
            {
                var updatedKids = new PdfArray(rewritten);
                if (resolvedKids.FinalReference is { } reference)
                    update.ReplaceObject(reference.ObjectNumber, updatedKids);
                else
                    replacement = ReplaceMany(replacement, new Dictionary<PdfName, PdfObject>
                    {
                        [KidsName] = updatedKids
                    });
            }
        }
        return replacement;

        PdfDictionary WithAppearance(PdfDictionary widget)
        {
            PdfDictionary appearances = widget.TryGetValue(Name("AP"), out PdfObject? value)
                ? ResolveDictionary(document, value, "The signature widget /AP value")
                : new PdfDictionary([]);
            return ReplaceMany(widget, new Dictionary<PdfName, PdfObject>
            {
                [Name("AP")] = ReplaceMany(appearances, new Dictionary<PdfName, PdfObject>
                {
                    [Name("N")] = appearanceReference
                })
            });
        }
    }

    private static PdfDictionary? UpdateDirectSignatureField(
        PdfDocument document,
        PdfPageTree tree,
        PdfIncrementalUpdateBuilder update,
        string targetName,
        PdfIndirectReference signatureReference,
        PdfIndirectReference? appearanceReference)
    {
        PdfObject formValue = tree.Catalog[AcroFormName];
        ResolvedValue resolvedForm = ResolveWithIdentity(
            document, formValue, "The catalog /AcroForm value");
        PdfIndirectReference? formReference = resolvedForm.FinalReference;
        PdfDictionary form = resolvedForm.Value as PdfDictionary
            ?? throw new InvalidOperationException(
                "The catalog /AcroForm value is not a dictionary.");
        PdfObject fieldsValue = form[FieldsName];
        int fieldCount = 0;
        RewriteResult rewrittenFields = RewriteArray(fieldsValue, null, null, 0);
        if (!rewrittenFields.Found)
            throw new InvalidOperationException(
                $"The AcroForm field '{targetName}' could not be rewritten.");
        PdfDictionary rewrittenForm = rewrittenFields.ContainerChanged
            ? ReplaceMany(form, new Dictionary<PdfName, PdfObject>
            {
                [FieldsName] = rewrittenFields.Value
            }) : form;
        rewrittenForm = WithSignatureFlags(document, rewrittenForm);
        if (formReference is not null)
        {
            update.ReplaceObject(formReference.ObjectNumber, rewrittenForm);
            return null;
        }
        return ReplaceMany(tree.Catalog, new Dictionary<PdfName, PdfObject>
        {
            [AcroFormName] = rewrittenForm
        });

        RewriteResult RewriteArray(
            PdfObject value, string? parentName, PdfName? inheritedType, int depth)
        {
            ResolvedValue resolvedArray = ResolveWithIdentity(
                document, value, "An AcroForm field array");
            PdfIndirectReference? reference = resolvedArray.FinalReference;
            PdfArray array = resolvedArray.Value as PdfArray
                ?? throw new InvalidOperationException(
                    "An AcroForm field array is not an array.");
            var rewritten = new List<PdfObject>(array.Count);
            bool changed = false;
            bool found = false;
            foreach (PdfObject item in array)
            {
                RewriteResult result = RewriteField(item, parentName, inheritedType, depth);
                rewritten.Add(result.Value);
                changed |= result.ContainerChanged;
                found |= result.Found;
            }
            if (!changed) return new RewriteResult(value, false, found);
            var replacement = new PdfArray(rewritten);
            if (reference is not null)
            {
                update.ReplaceObject(reference.ObjectNumber, replacement);
                return new RewriteResult(value, false, found);
            }
            return new RewriteResult(replacement, true, found);
        }

        RewriteResult RewriteField(
            PdfObject value, string? parentName, PdfName? inheritedType, int depth)
        {
            if (depth >= PdfObjectWriter.MaximumNestingDepth)
                throw new InvalidOperationException("The AcroForm field tree is too deeply nested.");
            if (++fieldCount > 1_000_000)
                throw new NotSupportedException(
                    "The AcroForm field tree contains too many fields.");
            ResolvedValue resolvedField = ResolveWithIdentity(
                document, value, "An AcroForm field");
            PdfIndirectReference? reference = resolvedField.FinalReference;
            PdfDictionary field = resolvedField.Value as PdfDictionary
                ?? throw new InvalidOperationException("An AcroForm field is not a dictionary.");
            PdfName? fieldType = inheritedType;
            if (field.TryGetValue(Name("FT"), out PdfObject? typeValue))
                fieldType = Resolve(document, typeValue) as PdfName
                    ?? throw new InvalidOperationException("An AcroForm field /FT value is not a name.");
            string? fullName = parentName;
            bool definesName = false;
            if (field.TryGetValue(FieldNameName, out PdfObject? nameValue))
            {
                definesName = true;
                string partialName = Resolve(document, nameValue) is PdfString name
                    ? DecodeString(name)
                    : throw new InvalidOperationException("An AcroForm field /T value is not a string.");
                fullName = string.IsNullOrEmpty(parentName)
                    ? partialName : $"{parentName}.{partialName}";
            }
            bool found = reference is null && definesName && fullName == targetName;
            bool changed = found;
            PdfDictionary replacement = found
                ? FillExistingField(document, update, field, signatureReference, appearanceReference)
                : field;
            if (field.TryGetValue(KidsName, out PdfObject? kidsValue))
            {
                RewriteResult kids = RewriteArray(kidsValue, fullName, fieldType, depth + 1);
                found |= kids.Found;
                if (kids.ContainerChanged)
                {
                    replacement = ReplaceMany(replacement, new Dictionary<PdfName, PdfObject>
                    {
                        [KidsName] = kids.Value
                    });
                    changed = true;
                }
            }
            if (!changed) return new RewriteResult(value, false, found);
            if (reference is not null)
            {
                update.ReplaceObject(reference.ObjectNumber, replacement);
                return new RewriteResult(value, false, found);
            }
            return new RewriteResult(replacement, true, found);
        }
    }

    private static PdfDictionary WithSignatureFlags(
        PdfDocument document, PdfDictionary form)
    {
        long flags = 0;
        if (form.TryGetValue(SignatureFlagsName, out PdfObject? flagsValue))
            flags = Resolve(document, flagsValue) is PdfInteger integer && integer.Value >= 0
                ? integer.Value
                : throw new InvalidOperationException(
                    "The AcroForm /SigFlags value is not a non-negative integer.");
        return ReplaceMany(form, new Dictionary<PdfName, PdfObject>
        {
            [SignatureFlagsName] = new PdfInteger(flags | 3)
        });
    }

    private static void FillSignature(
        byte[] prepared,
        int sourceLength,
        int signatureObjectNumber,
        int reservedSize,
        Func<ReadOnlyMemory<byte>, byte[]> createDetachedCms,
        SeedEvidenceRequirements evidenceRequirements,
        ReadOnlyMemory<byte> signerCertificate)
    {
        byte[] rangeMarker = Encoding.ASCII.GetBytes(
            $"/ByteRange [{RangeSentinel1} {RangeSentinel2} {RangeSentinel3} {RangeSentinel4}]");
        PdfCrossReferenceTable crossReferences = PdfCrossReferenceTable.Read(prepared);
        if (!crossReferences.TryGetValue(
                signatureObjectNumber, out PdfCrossReferenceEntry signatureEntry)
            || signatureEntry.Type != PdfCrossReferenceEntryType.InUse
            || signatureEntry.Field2 != 0
            || signatureEntry.Field1 < sourceLength
            || signatureEntry.Field1 > int.MaxValue)
            throw new InvalidOperationException(
                "The appended signature object has no valid cross-reference entry.");
        int signatureObjectIndex = checked((int)signatureEntry.Field1);
        byte[] objectHeader = Encoding.ASCII.GetBytes(
            $"{signatureObjectNumber} 0 obj\n");
        if (!prepared.AsSpan(signatureObjectIndex).StartsWith(objectHeader))
            throw new InvalidOperationException(
                "The appended signature object header does not match its cross-reference entry.");
        int relativeObjectEnd = prepared.AsSpan(signatureObjectIndex).IndexOf("\nendobj\n"u8);
        if (relativeObjectEnd < 0)
            throw new InvalidOperationException(
                "The appended signature object has no terminator.");
        ReadOnlySpan<byte> signatureObject = prepared.AsSpan(
            signatureObjectIndex, relativeObjectEnd);
        int relativeRangeIndex = signatureObject.IndexOf(rangeMarker);
        if (relativeRangeIndex < 0)
            throw new InvalidOperationException("The signature byte-range placeholder was not found.");
        int rangeMarkerIndex = checked(signatureObjectIndex + relativeRangeIndex);
        ReadOnlySpan<byte> contentsMarker = "/Contents <"u8;
        int relativeContentsIndex = prepared.AsSpan(
            rangeMarkerIndex, relativeObjectEnd - relativeRangeIndex)
            .IndexOf(contentsMarker);
        if (relativeContentsIndex < 0)
            throw new InvalidOperationException("The signature contents placeholder was not found.");
        int contentsValueStart = rangeMarkerIndex + relativeContentsIndex
            + contentsMarker.Length - 1;
        int contentsHexStart = contentsValueStart + 1;
        int contentsHexEnd = checked(contentsHexStart + reservedSize * 2);
        if (contentsHexEnd >= prepared.Length || prepared[contentsHexEnd] != (byte)'>')
            throw new InvalidOperationException("The signature contents placeholder has an unexpected length.");
        int secondRangeStart = contentsHexEnd + 1;
        long[] ranges = [0, contentsValueStart, secondRangeStart, prepared.Length - secondRangeStart];
        long[] sentinels = [RangeSentinel1, RangeSentinel2, RangeSentinel3, RangeSentinel4];
        for (int index = 0; index < ranges.Length; index++)
        {
            byte[] marker = Encoding.ASCII.GetBytes(
                sentinels[index].ToString(CultureInfo.InvariantCulture));
            int relative = prepared.AsSpan(rangeMarkerIndex, rangeMarker.Length).IndexOf(marker);
            if (relative < 0)
                throw new InvalidOperationException("A signature byte-range placeholder was not found.");
            WriteFixedDecimal(prepared.AsSpan(rangeMarkerIndex + relative, 10), ranges[index]);
        }

        byte[] signedBytes = new byte[checked((int)(ranges[1] + ranges[3]))];
        prepared.AsSpan(0, checked((int)ranges[1])).CopyTo(signedBytes);
        prepared.AsSpan(checked((int)ranges[2]), checked((int)ranges[3]))
            .CopyTo(signedBytes.AsSpan(checked((int)ranges[1])));
        byte[] cms = createDetachedCms(signedBytes)
            ?? throw new InvalidOperationException("The detached CMS signer returned null.");
        if (cms.Length == 0)
            throw new InvalidOperationException("The detached CMS signer returned an empty signature.");
        if (cms.Length > reservedSize)
            throw new InvalidOperationException(
                $"The detached CMS signature requires {cms.Length} bytes but only {reservedSize} were reserved.");
        if (evidenceRequirements.Timestamp && !ContainsRfc3161TimestampToken(cms))
            throw new InvalidOperationException(
                "The detached CMS signature does not contain the required RFC 3161 timestamp token.");
        if (evidenceRequirements.Certificate
            && !CmsUsesCertificate(cms, signerCertificate))
            throw new InvalidOperationException(
                "The detached CMS signature was not created by the constrained signer certificate.");
        WriteHex(prepared.AsSpan(contentsHexStart, reservedSize * 2), cms);
    }

    private static void WriteFixedDecimal(Span<byte> destination, long value)
    {
        string text = value.ToString("D10", CultureInfo.InvariantCulture);
        if (text.Length != destination.Length)
            throw new NotSupportedException("Signed PDF byte offsets cannot exceed ten digits.");
        Encoding.ASCII.GetBytes(text, destination);
    }

    private static bool ContainsRfc3161TimestampToken(ReadOnlyMemory<byte> cms)
    {
        const string signedDataOid = "1.2.840.113549.1.7.2";
        const string timestampTokenOid = "1.2.840.113549.1.9.16.2.14";
        try
        {
            var reader = new AsnReader(cms, AsnEncodingRules.BER);
            AsnReader contentInfo = reader.ReadSequence();
            if (contentInfo.ReadObjectIdentifier() != signedDataOid) return false;
            AsnReader explicitContent = contentInfo.ReadSequence(
                new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true));
            AsnReader signedData = explicitContent.ReadSequence();
            signedData.ReadInteger();
            signedData.ReadSetOf();
            signedData.ReadSequence();
            if (signedData.HasData
                && signedData.PeekTag().HasSameClassAndValue(
                    new Asn1Tag(TagClass.ContextSpecific, 0)))
                signedData.ReadEncodedValue();
            if (signedData.HasData
                && signedData.PeekTag().HasSameClassAndValue(
                    new Asn1Tag(TagClass.ContextSpecific, 1)))
                signedData.ReadEncodedValue();
            AsnReader signerInfos = signedData.ReadSetOf();
            while (signerInfos.HasData)
            {
                AsnReader signerInfo = signerInfos.ReadSequence();
                signerInfo.ReadInteger();
                signerInfo.ReadEncodedValue();
                signerInfo.ReadSequence();
                if (signerInfo.HasData
                    && signerInfo.PeekTag().HasSameClassAndValue(
                        new Asn1Tag(TagClass.ContextSpecific, 0)))
                    signerInfo.ReadEncodedValue();
                signerInfo.ReadSequence();
                signerInfo.ReadOctetString();
                if (!signerInfo.HasData
                    || !signerInfo.PeekTag().HasSameClassAndValue(
                        new Asn1Tag(TagClass.ContextSpecific, 1)))
                    continue;
                AsnReader unsignedAttributes = signerInfo.ReadSetOf(
                    new Asn1Tag(TagClass.ContextSpecific, 1, isConstructed: true));
                while (unsignedAttributes.HasData)
                {
                    AsnReader attribute = unsignedAttributes.ReadSequence();
                    if (attribute.ReadObjectIdentifier() == timestampTokenOid)
                        return true;
                    attribute.ReadEncodedValue();
                }
            }
            return false;
        }
        catch (AsnContentException)
        {
            return false;
        }
    }

    private static bool CmsUsesCertificate(
        ReadOnlyMemory<byte> cms, ReadOnlyMemory<byte> certificate)
    {
        if (certificate.IsEmpty) return false;
        try
        {
            (ReadOnlyMemory<byte> issuer, ReadOnlyMemory<byte> serial) =
                ReadCertificateIssuerAndSerial(certificate);
            byte[]? subjectKeyIdentifier = null;
            using (X509Certificate2 parsedCertificate =
                X509CertificateLoader.LoadCertificate(certificate.Span))
            {
                string? identifier = parsedCertificate.Extensions
                    .OfType<X509SubjectKeyIdentifierExtension>()
                    .Select(extension => extension.SubjectKeyIdentifier)
                    .FirstOrDefault(value => value is not null);
                if (identifier is not null)
                    subjectKeyIdentifier = Convert.FromHexString(identifier);
            }
            var reader = new AsnReader(cms, AsnEncodingRules.BER);
            AsnReader contentInfo = reader.ReadSequence();
            if (contentInfo.ReadObjectIdentifier() != "1.2.840.113549.1.7.2") return false;
            AsnReader explicitContent = contentInfo.ReadSequence(
                new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true));
            AsnReader signedData = explicitContent.ReadSequence();
            signedData.ReadInteger();
            signedData.ReadSetOf();
            signedData.ReadSequence();
            if (!signedData.HasData
                || !signedData.PeekTag().HasSameClassAndValue(
                    new Asn1Tag(TagClass.ContextSpecific, 0)))
                return false;
            AsnReader certificates = signedData.ReadSetOf(
                new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true));
            bool containsCertificate = false;
            while (certificates.HasData)
            {
                ReadOnlyMemory<byte> encoded = certificates.ReadEncodedValue();
                if (encoded.Span.SequenceEqual(certificate.Span)) containsCertificate = true;
            }
            if (!containsCertificate) return false;
            if (signedData.HasData
                && signedData.PeekTag().HasSameClassAndValue(
                    new Asn1Tag(TagClass.ContextSpecific, 1)))
                signedData.ReadEncodedValue();
            AsnReader signerInfos = signedData.ReadSetOf();
            while (signerInfos.HasData)
            {
                AsnReader signerInfo = signerInfos.ReadSequence();
                signerInfo.ReadInteger();
                if (signerInfo.PeekTag().HasSameClassAndValue(
                    new Asn1Tag(TagClass.ContextSpecific, 0)))
                {
                    byte[] signerKeyIdentifier = signerInfo.ReadOctetString(
                        new Asn1Tag(TagClass.ContextSpecific, 0));
                    if (subjectKeyIdentifier is not null
                        && signerKeyIdentifier.AsSpan().SequenceEqual(subjectKeyIdentifier))
                        return true;
                    continue;
                }
                if (!signerInfo.PeekTag().HasSameClassAndValue(Asn1Tag.Sequence))
                {
                    signerInfo.ReadEncodedValue();
                    continue;
                }
                AsnReader signerIdentifier = signerInfo.ReadSequence();
                ReadOnlyMemory<byte> signerIssuer = signerIdentifier.ReadEncodedValue();
                ReadOnlyMemory<byte> signerSerial = signerIdentifier.ReadIntegerBytes();
                if (signerIssuer.Span.SequenceEqual(issuer.Span)
                    && NormalizeInteger(signerSerial.Span)
                        .SequenceEqual(NormalizeInteger(serial.Span)))
                    return true;
            }
            return false;
        }
        catch (AsnContentException)
        {
            return false;
        }
    }

    private static (ReadOnlyMemory<byte> Issuer, ReadOnlyMemory<byte> Serial)
        ReadCertificateIssuerAndSerial(ReadOnlyMemory<byte> certificate)
    {
        var reader = new AsnReader(certificate, AsnEncodingRules.DER);
        AsnReader certificateSequence = reader.ReadSequence();
        AsnReader tbsCertificate = certificateSequence.ReadSequence();
        if (tbsCertificate.PeekTag().HasSameClassAndValue(
            new Asn1Tag(TagClass.ContextSpecific, 0)))
            tbsCertificate.ReadEncodedValue();
        ReadOnlyMemory<byte> serial = tbsCertificate.ReadIntegerBytes();
        tbsCertificate.ReadSequence();
        ReadOnlyMemory<byte> issuer = tbsCertificate.ReadEncodedValue();
        return (issuer, serial);
    }

    private static ReadOnlySpan<byte> NormalizeInteger(ReadOnlySpan<byte> value)
    {
        while (value.Length > 1 && value[0] == 0) value = value[1..];
        return value;
    }

    private static void WriteHex(Span<byte> destination, ReadOnlySpan<byte> value)
    {
        const string digits = "0123456789ABCDEF";
        int offset = 0;
        foreach (byte item in value)
        {
            destination[offset++] = (byte)digits[item >> 4];
            destination[offset++] = (byte)digits[item & 0x0F];
        }
        destination[offset..].Fill((byte)'0');
    }

    private static void ValidateOptions(PdfSignatureOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.FieldName))
            throw new ArgumentException("A signature field name must be non-empty.", nameof(options));
        if (options.ReservedSignatureSize is <= 0 or > MaximumReservedSignatureSize)
            throw new ArgumentOutOfRangeException(nameof(options),
                $"The reserved signature size must be between 1 and {MaximumReservedSignatureSize} bytes.");
        if (!Enum.IsDefined(options.DigestMethod))
            throw new ArgumentOutOfRangeException(nameof(options),
                "The signature digest method is not supported.");
        if (options.DocumentLockIntent.HasValue
            && !Enum.IsDefined(options.DocumentLockIntent.Value))
            throw new ArgumentOutOfRangeException(nameof(options),
                "The document-lock intent is not supported.");
        if (options.CertificationPermission.HasValue
            && options.CertificationPermission.Value is not
                (PdfSignatureCertificationPermission.NoChanges
                or PdfSignatureCertificationPermission.FormFillingAndSignatures
                or PdfSignatureCertificationPermission.FormFillingSignaturesAndAnnotations))
            throw new ArgumentOutOfRangeException(nameof(options),
                "A certification permission must be one of the three DocMDP permission levels.");
        if (options.VisibleAppearance is { } appearance)
        {
            if (!double.IsFinite(appearance.Left) || !double.IsFinite(appearance.Bottom)
                || !double.IsFinite(appearance.Width) || !double.IsFinite(appearance.Height)
                || appearance.Width <= 0 || appearance.Height <= 0)
                throw new ArgumentOutOfRangeException(nameof(options),
                    "A visible signature appearance must have finite coordinates and positive dimensions.");
            if (!double.IsFinite(appearance.FontSize) || appearance.FontSize is < 6 or > 72)
                throw new ArgumentOutOfRangeException(nameof(options),
                    "A visible signature font size must be between 6 and 72 points.");
            ArgumentNullException.ThrowIfNull(appearance.Text);
        }
    }

    private static PdfStream BuildVisibleAppearance(PdfSignatureAppearance appearance)
    {
        string text = ToWinAnsi(appearance.Text);
        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n').Split('\n');
        double lineHeight = appearance.FontSize * 1.25;
        double y = appearance.Height - appearance.FontSize - 8;
        var content = new StringBuilder();
        content.Append("q 1 1 1 rg 0 0 ")
            .Append(Number(appearance.Width)).Append(' ')
            .Append(Number(appearance.Height)).Append(" re f ")
            .Append("0.16 0.68 0.38 RG 1.5 w 0.75 0.75 ")
            .Append(Number(Math.Max(0, appearance.Width - 1.5))).Append(' ')
            .Append(Number(Math.Max(0, appearance.Height - 1.5))).Append(" re S ")
            .Append("0.1 0.1 0.1 rg BT /F1 ")
            .Append(Number(appearance.FontSize)).Append(" Tf ");
        foreach (string line in lines)
        {
            if (y < 4) break;
            content.Append("1 0 0 1 8 ").Append(Number(y)).Append(" Tm (")
                .Append(EscapeLiteral(line)).Append(") Tj ");
            y -= lineHeight;
        }
        content.Append("ET Q\n");

        PdfDictionary font = Dictionary(
            ("Type", Name("Font")),
            ("Subtype", Name("Type1")),
            ("BaseFont", Name("Helvetica")),
            ("Encoding", Name("WinAnsiEncoding")));
        return new PdfStream(Dictionary(
            ("Type", Name("XObject")),
            ("Subtype", Name("Form")),
            ("FormType", new PdfInteger(1)),
            ("BBox", Rectangle(0, 0, appearance.Width, appearance.Height)),
            ("Resources", Dictionary(("Font", Dictionary(("F1", font)))))),
            Encoding.Latin1.GetBytes(content.ToString()));
    }

    private static PdfArray Rectangle(double left, double bottom, double right, double top) =>
        new([new PdfReal(left), new PdfReal(bottom), new PdfReal(right), new PdfReal(top)]);

    private static string Number(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string ToWinAnsi(string value) =>
        Encoding.Latin1.GetString(Encoding.Latin1.GetBytes(value));

    private static string EscapeLiteral(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("(", "\\(", StringComparison.Ordinal)
        .Replace(")", "\\)", StringComparison.Ordinal);

    private static PdfDictionary ResolveDictionary(
        PdfDocument document, PdfObject value, string description)
    {
        PdfObject resolved = Resolve(document, value, description);
        return resolved as PdfDictionary
            ?? throw new InvalidOperationException($"{description} is not a dictionary.");
    }

    private static PdfArray ResolveArray(
        PdfDocument document, PdfObject value, string description)
    {
        PdfObject resolved = Resolve(document, value, description);
        return resolved as PdfArray
            ?? throw new InvalidOperationException($"{description} is not an array.");
    }

    private static PdfObject Resolve(
        PdfDocument document, PdfObject value, string description = "A signature value")
        => ResolveWithIdentity(document, value, description).Value;

    private static ResolvedValue ResolveWithIdentity(
        PdfDocument document, PdfObject value, string description)
    {
        var visited = new HashSet<(int ObjectNumber, int Generation)>();
        PdfIndirectReference? finalReference = null;
        for (int depth = 0; value is PdfIndirectReference reference; depth++)
        {
            if (depth >= 32)
                throw new InvalidOperationException(
                    $"{description} is too deeply indirect.");
            if (!visited.Add((reference.ObjectNumber, reference.Generation)))
                throw new InvalidOperationException(
                    $"{description} contains an indirect-reference cycle.");
            finalReference = reference;
            value = document.Resolve(reference);
        }
        return new ResolvedValue(value, finalReference);
    }

    private static PdfDictionary ReplaceMany(
        PdfDictionary source, Dictionary<PdfName, PdfObject> replacements) =>
        new(source.Where(entry => !replacements.ContainsKey(entry.Key)).Concat(replacements));

    private static PdfDictionary Dictionary(params (string Name, PdfObject Value)[] entries) =>
        new(entries.Select(entry =>
            new KeyValuePair<PdfName, PdfObject>(Name(entry.Name), entry.Value)));
    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
    private static PdfString Latin1String(string value) =>
        new(Encoding.Latin1.GetBytes(value), PdfStringForm.Literal);
    private static PdfString UnicodeString(string value)
    {
        byte[] text = PdfUnicodeEncoding.EncodeBigEndian(value);
        byte[] bytes = new byte[text.Length + 2];
        bytes[0] = 0xFE;
        bytes[1] = 0xFF;
        text.CopyTo(bytes, 2);
        return new PdfString(bytes, PdfStringForm.Hexadecimal);
    }
    private static string DecodeString(PdfString value)
        => PdfUnicodeEncoding.DecodeTextString(
            value.Bytes.Span, "A signature text string");
    private static string PdfDate(DateTimeOffset value)
    {
        TimeSpan offset = value.Offset;
        char sign = offset < TimeSpan.Zero ? '-' : '+';
        offset = offset.Duration();
        return $"D:{value:yyyyMMddHHmmss}{sign}{offset.Hours:00}'{offset.Minutes:00}'";
    }
    private static void AddOptionalText(
        List<(string Name, PdfObject Value)> entries, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value)) entries.Add((name, UnicodeString(value)));
    }

    private sealed record ExistingFormField(
        PdfIndirectReference? Reference, PdfDictionary Dictionary, PdfName FieldType);
    private sealed record ResolvedValue(
        PdfObject Value, PdfIndirectReference? FinalReference);
    private readonly record struct RewriteResult(
        PdfObject Value, bool ContainerChanged, bool Found);
    private readonly record struct TaggedSignatureAssociation(
        long? StructureParentKey, PdfDictionary Catalog, bool CatalogChanged);
    private readonly record struct SeedEvidenceRequirements(bool Timestamp, bool Certificate);
}
