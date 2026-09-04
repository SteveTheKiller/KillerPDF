using System.Text;
using System.Formats.Asn1;
using System.Globalization;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Syntax;
using KillerPdf.Engine.Writing;

namespace KillerPdf.Engine.Signing;

/// <summary>Inspects signature fields and structurally validates their signed byte ranges.</summary>
public static class PdfSignatureReader
{
    /// <summary>Reads the document certification permission registered in the catalog.</summary>
    public static PdfSignatureCertificationPermission? ReadCertificationPermission(
        PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!document.CrossReferences.TryGetTrailerValue(Name("Root"), out PdfObject rootValue))
            return null;
        PdfDictionary catalog = ResolveDictionary(document, rootValue, "The document catalog");
        if (!catalog.TryGetValue(Name("Perms"), out PdfObject permissionsValue))
            return null;
        PdfDictionary permissions = ResolveDictionary(
            document, permissionsValue, "The catalog /Perms value");
        if (!permissions.TryGetValue(Name("DocMDP"), out PdfObject signatureValue))
            return null;
        if (signatureValue is not PdfIndirectReference)
            throw new InvalidOperationException(
                "The catalog /Perms /DocMDP value is not an indirect reference.");
        PdfDictionary signature = ResolveDictionary(
            document, signatureValue, "The certification signature");
        ValidateSignatureDictionaryType(
            document, signature, "The certification signature");
        SignatureTransforms transforms = ReadTransforms(document, signature);
        return transforms.CertificationPermission
            ?? throw new InvalidOperationException(
                "The catalog certification signature has no DocMDP transform.");
    }

    private static readonly PdfName AcroFormName = Name("AcroForm");
    private static readonly PdfName FieldsName = Name("Fields");
    private static readonly PdfName FieldTypeName = Name("FT");
    private static readonly PdfName FieldNameName = Name("T");
    private static readonly PdfName KidsName = Name("Kids");
    private static readonly PdfName ValueName = Name("V");

    /// <summary>Discovers and structurally inspects every signature field in a document.</summary>
    public static IReadOnlyList<PdfSignatureInfo> Read(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        PdfDictionary catalog = ResolveDictionary(document,
            document.CrossReferences.TryGetTrailerValue(
                Name("Root"), out PdfObject? root)
                ? root : throw new InvalidOperationException("The PDF trailer has no /Root value."),
            "The trailer /Root value");
        if (!catalog.TryGetValue(AcroFormName, out PdfObject? formValue)) return [];
        PdfDictionary form = ResolveDictionary(document, formValue, "The catalog /AcroForm value");
        if (!form.TryGetValue(FieldsName, out PdfObject? fieldsValue)) return [];
        PdfArray fields = ResolveArray(document, fieldsValue, "The AcroForm /Fields value");
        PdfIndirectReference? certificationObject = CertificationObjectReference(document, catalog);
        var result = new List<PdfSignatureInfo>();
        var active = new HashSet<(int ObjectNumber, int Generation)>();
        var visited = new HashSet<(int ObjectNumber, int Generation)>();
        int fieldCount = 0;
        foreach (PdfObject field in fields) Visit(field, null, null, 0);
        return result;

        void Visit(PdfObject value, string? parentName, PdfName? inheritedType, int depth)
        {
            if (depth >= PdfObjectWriter.MaximumNestingDepth)
                throw new InvalidOperationException("The AcroForm field tree is too deeply nested.");
            if (++fieldCount > 1_000_000)
                throw new NotSupportedException(
                    "The AcroForm field tree contains too many fields.");
            ResolvedValue resolvedField = ResolveWithIdentity(
                document, value, "An AcroForm field");
            PdfIndirectReference? fieldReference = resolvedField.FinalReference;
            if (fieldReference is not null)
            {
                var identity = (fieldReference.ObjectNumber, fieldReference.Generation);
                if (!active.Add(identity))
                    throw new InvalidOperationException("The AcroForm field tree contains a cycle.");
                if (!visited.Add(identity))
                    throw new InvalidOperationException(
                        "The AcroForm field tree references the same field more than once.");
            }
            PdfDictionary field = resolvedField.Value as PdfDictionary
                ?? throw new InvalidOperationException(
                    "An AcroForm field is not a dictionary.");
            PdfName? fieldType = inheritedType;
            if (field.TryGetValue(FieldTypeName, out PdfObject? typeValue))
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
            bool hasKids = field.ContainsKey(KidsName);
            if (fieldType?.ValueAsLatin1() == "Sig"
                && (definesName || (parentName is null && !hasKids)))
                result.Add(ReadSignature(
                    document, field, fullName ?? string.Empty, certificationObject));
            if (field.TryGetValue(KidsName, out PdfObject? kidsValue))
            {
                PdfArray kids = ResolveArray(document, kidsValue, "An AcroForm field /Kids value");
                foreach (PdfObject kid in kids) Visit(kid, fullName, fieldType, depth + 1);
            }
            if (fieldReference is not null)
                active.Remove((fieldReference.ObjectNumber, fieldReference.Generation));
        }
    }

    /// <summary>Returns the exact discontiguous bytes covered by a signature's byte range.</summary>
    public static byte[] GetSignedContent(PdfDocument document, PdfSignatureInfo signature)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(signature);
        if (!signature.HasValidByteRange || signature.ByteRange is not { Count: 4 } range)
            throw new InvalidOperationException("The signature does not have a valid byte range.");
        int firstStart = checked((int)range[0]);
        int firstLength = checked((int)range[1]);
        int secondStart = checked((int)range[2]);
        int secondLength = checked((int)range[3]);
        byte[] content = new byte[checked(firstLength + secondLength)];
        document.Source.Span.Slice(firstStart, firstLength).CopyTo(content);
        document.Source.Span.Slice(secondStart, secondLength).CopyTo(content.AsSpan(firstLength));
        return content;
    }

    private static PdfSignatureInfo ReadSignature(
        PdfDocument document, PdfDictionary field, string fieldName,
        PdfIndirectReference? certificationObject)
    {
        if (!field.TryGetValue(ValueName, out PdfObject? value))
            return new PdfSignatureInfo { FieldName = fieldName };
        ResolvedValue resolvedSignatureValue = ResolveWithIdentity(document, value,
            $"The signature field '{fieldName}' /V value");
        PdfObject resolvedValue = resolvedSignatureValue.Value;
        if (IsUnsignedValue(resolvedValue))
            return new PdfSignatureInfo { FieldName = fieldName };
        PdfIndirectReference? signatureReference = resolvedSignatureValue.FinalReference;
        PdfDictionary signature = resolvedValue as PdfDictionary
            ?? throw new InvalidOperationException(
                $"The signature field '{fieldName}' /V value is not a dictionary.");
        ValidateSignatureDictionaryType(
            document, signature, $"The signature field '{fieldName}' /V dictionary",
            allowDocumentTimestamp: true);
        bool isDocumentTimestamp = OptionalName(document, signature, "Type") == "DocTimeStamp";
        string? filter = OptionalName(document, signature, "Filter");
        string? subFilter = OptionalName(document, signature, "SubFilter");
        long[]? range = null;
        bool valid = false;
        bool coversWholeDocument = false;
        if (signature.TryGetValue(Name("ByteRange"), out PdfObject? rangeValue))
        {
            PdfArray rangeArray = ResolveArray(document, rangeValue,
                $"The signature field '{fieldName}' /ByteRange value");
            PdfObject[] rangeValues = [.. rangeArray.Select(item => Resolve(document, item))];
            if (rangeValues.Length == 4 && rangeValues.All(item => item is PdfInteger))
            {
                range = [.. rangeValues.Cast<PdfInteger>().Select(item => item.Value)];
                long length = document.Source.Length;
                valid = range.All(item => item >= 0)
                    && range[0] == 0
                    && range[0] <= length && range[1] <= length - range[0]
                    && range[2] > range[0] + range[1] && range[2] <= length
                    && range[3] <= length - range[2];
            }
        }
        ReadOnlyMemory<byte> contents = signature.TryGetValue(Name("Contents"), out PdfObject? contentsValue)
            && contentsValue is PdfString contentsString ? contentsString.Bytes : ReadOnlyMemory<byte>.Empty;
        valid = valid && contentsValue is PdfString byteString
            && GapIsExactContentsString(document.Source, range!, byteString);
        coversWholeDocument = valid && range![2] + range[3] == document.Source.Length;
        (ReadOnlyMemory<byte> cms, bool validCms) = ReadCms(contents);
        SignatureTransforms transforms = ReadTransforms(document, signature);
        return new PdfSignatureInfo
        {
            FieldName = fieldName,
            IsSigned = true,
            IsDocumentTimestamp = isDocumentTimestamp,
            IsCertificationSignature = signatureReference is not null
                && certificationObject is not null
                && signatureReference.ObjectNumber == certificationObject.ObjectNumber
                && signatureReference.Generation == certificationObject.Generation,
            CertificationPermission = transforms.CertificationPermission,
            FieldLockAction = transforms.FieldLockAction,
            FieldLockPermission = transforms.FieldLockPermission,
            LockedFields = transforms.LockedFields,
            Filter = filter,
            SubFilter = subFilter,
            SignerName = OptionalText(document, signature, "Name"),
            Reason = OptionalText(document, signature, "Reason"),
            Location = OptionalText(document, signature, "Location"),
            ContactInformation = OptionalText(document, signature, "ContactInfo"),
            SigningTime = OptionalDate(document, signature, "M"),
            ByteRange = range,
            Contents = contents,
            Cms = cms,
            HasValidCmsEncoding = validCms,
            HasValidByteRange = valid,
            CoversWholeDocument = coversWholeDocument
        };
    }

    private static bool IsUnsignedValue(PdfObject value) =>
        value is PdfNull || value is PdfString { Bytes.Length: 0 };

    private static bool GapIsExactContentsString(
        ReadOnlyMemory<byte> source,
        long[] range,
        PdfString contents)
    {
        int gapStart = checked((int)(range[0] + range[1]));
        int gapLength = checked((int)(range[2] - gapStart));
        try
        {
            var tokenizer = new PdfTokenizer(source.Slice(gapStart, gapLength));
            PdfToken token = tokenizer.Read();
            bool formMatches = contents.Form switch
            {
                PdfStringForm.Literal => token.Kind == PdfTokenKind.LiteralString,
                PdfStringForm.Hexadecimal => token.Kind == PdfTokenKind.HexString,
                _ => false
            };
            return formMatches
                && token.Offset == 0
                && token.Length == gapLength
                && token.Value.Span.SequenceEqual(contents.Bytes.Span)
                && tokenizer.Read().Kind == PdfTokenKind.EndOfInput;
        }
        catch (PdfSyntaxException)
        {
            return false;
        }
    }

    private static SignatureTransforms ReadTransforms(
        PdfDocument document, PdfDictionary signature)
    {
        if (!signature.TryGetValue(Name("Reference"), out PdfObject? value)) return default;
        PdfArray references = ResolveArray(document, value, "The signature /Reference value");
        PdfSignatureCertificationPermission? certification = null;
        PdfSignatureLockAction? lockAction = null;
        PdfSignatureLockPermission? lockPermission = null;
        IReadOnlyList<string>? lockedFields = null;
        foreach (PdfObject referenceValue in references)
        {
            PdfDictionary reference = ResolveDictionary(
                document, referenceValue, "A signature reference");
            if (reference.TryGetValue(Name("Type"), out PdfObject? referenceTypeValue)
                && (Resolve(document, referenceTypeValue) is not PdfName referenceType
                    || referenceType.ValueAsLatin1() != "SigRef"))
                throw new InvalidOperationException(
                    "A signature reference has an invalid /Type; it must be /SigRef.");
            if (!reference.TryGetValue(Name("TransformMethod"), out PdfObject? methodValue)
                || Resolve(document, methodValue) is not PdfName method)
                throw new InvalidOperationException(
                    "A signature reference has no valid /TransformMethod.");
            if (!reference.TryGetValue(Name("TransformParams"), out PdfObject? parametersValue))
                throw new InvalidOperationException(
                    "A signature reference has no /TransformParams value.");
            PdfDictionary parameters = ResolveDictionary(
                document, parametersValue, "The signature transform parameters");
            if (method.ValueAsLatin1() == "DocMDP")
            {
                ValidateTransformParameters(document, parameters, "DocMDP");
                if (certification.HasValue)
                    throw new InvalidOperationException(
                        "The signature contains more than one DocMDP transform.");
                long permission = parameters.TryGetValue(Name("P"), out PdfObject? permissionValue)
                    && Resolve(document, permissionValue) is PdfInteger integer ? integer.Value : 2;
                if (permission is < 1 or > 3)
                    throw new InvalidOperationException(
                        "The DocMDP permission is not an integer from 1 through 3.");
                certification = (PdfSignatureCertificationPermission)permission;
            }
            else if (method.ValueAsLatin1() == "FieldMDP")
            {
                ValidateTransformParameters(document, parameters, "FieldMDP");
                if (lockAction.HasValue)
                    throw new InvalidOperationException(
                        "The signature contains more than one FieldMDP transform.");
                if (!parameters.TryGetValue(Name("Action"), out PdfObject? actionValue)
                    || Resolve(document, actionValue) is not PdfName action
                    || !Enum.TryParse(action.ValueAsLatin1(), out PdfSignatureLockAction parsedAction))
                    throw new InvalidOperationException(
                        "The FieldMDP transform has no valid lock action.");
                lockAction = parsedAction;
                if (parameters.TryGetValue(Name("P"), out PdfObject? lockPermissionValue))
                {
                    if (Resolve(document, lockPermissionValue) is not PdfInteger permission
                        || permission.Value is < 1 or > 3)
                        throw new InvalidOperationException(
                            "The FieldMDP permission is not an integer from 1 through 3.");
                    lockPermission = (PdfSignatureLockPermission)permission.Value;
                }
                if (parameters.TryGetValue(Name("Fields"), out PdfObject? fieldsValue))
                {
                    PdfArray fields = ResolveArray(
                        document, fieldsValue, "The FieldMDP /Fields value");
                    PdfObject[] fieldValues = [.. fields.Select(item => Resolve(document, item))];
                    if (fieldValues.Any(item => item is not PdfString))
                        throw new InvalidOperationException(
                            "The FieldMDP /Fields array contains a non-string value.");
                    lockedFields = [.. fieldValues.Cast<PdfString>().Select(DecodeString)];
                }
                if (lockAction is PdfSignatureLockAction.Include or PdfSignatureLockAction.Exclude
                    && lockedFields is null)
                    throw new InvalidOperationException(
                        "A FieldMDP Include or Exclude transform has no /Fields array.");
            }
        }
        return new SignatureTransforms(
            certification, lockAction, lockPermission, lockedFields);
    }

    private static void ValidateTransformParameters(
        PdfDocument document, PdfDictionary parameters, string method)
    {
        if (parameters.TryGetValue(Name("Type"), out PdfObject? typeValue)
            && (Resolve(document, typeValue) is not PdfName type
                || type.ValueAsLatin1() != "TransformParams"))
            throw new InvalidOperationException(
                $"The {method} transform parameters have an invalid /Type.");
        if (parameters.TryGetValue(Name("V"), out PdfObject? versionValue)
            && (Resolve(document, versionValue) is not PdfName version
                || version.ValueAsLatin1() != "1.2"))
            throw new InvalidOperationException(
                $"The {method} transform parameters have an invalid version; /V must be /1.2.");
    }

    private static (ReadOnlyMemory<byte> Cms, bool IsValid) ReadCms(ReadOnlyMemory<byte> contents)
    {
        if (contents.IsEmpty) return (ReadOnlyMemory<byte>.Empty, false);
        try
        {
            var reader = new AsnReader(contents, AsnEncodingRules.BER);
            ReadOnlyMemory<byte> cms = reader.ReadEncodedValue();
            ReadOnlySpan<byte> padding = contents.Span[cms.Length..];
            return padding.IndexOfAnyExcept((byte)0) < 0
                ? (cms, true) : (ReadOnlyMemory<byte>.Empty, false);
        }
        catch (AsnContentException)
        {
            return (ReadOnlyMemory<byte>.Empty, false);
        }
    }

    private static PdfIndirectReference? CertificationObjectReference(
        PdfDocument document, PdfDictionary catalog)
    {
        if (!catalog.TryGetValue(Name("Perms"), out PdfObject? permissionsValue)) return null;
        PdfDictionary permissions = ResolveDictionary(document, permissionsValue,
            "The catalog /Perms value");
        if (!permissions.TryGetValue(Name("DocMDP"), out PdfObject? docMdpValue)) return null;
        PdfIndirectReference reference = docMdpValue as PdfIndirectReference
            ?? throw new InvalidOperationException(
                "The catalog /Perms /DocMDP value is not indirect.");
        ResolvedValue resolvedSignature = ResolveWithIdentity(
            document, reference, "The catalog certification signature");
        PdfDictionary signature = resolvedSignature.Value as PdfDictionary
            ?? throw new InvalidOperationException(
                "The catalog certification signature is not a dictionary.");
        ValidateSignatureDictionaryType(
            document, signature, "The catalog certification signature");
        return resolvedSignature.FinalReference;
    }

    private static void ValidateSignatureDictionaryType(
        PdfDocument document, PdfDictionary signature, string description,
        bool allowDocumentTimestamp = false)
    {
        if (!signature.TryGetValue(Name("Type"), out PdfObject? signatureTypeValue)
            || Resolve(document, signatureTypeValue) is not PdfName signatureType
            || (signatureType.ValueAsLatin1() != "Sig"
                && (!allowDocumentTimestamp
                    || signatureType.ValueAsLatin1() != "DocTimeStamp")))
            throw new InvalidOperationException(
                $"{description} does not declare /Type /Sig.");
    }

    private static string? OptionalName(
        PdfDocument document, PdfDictionary dictionary, string key)
    {
        if (!dictionary.TryGetValue(Name(key), out PdfObject? value)) return null;
        return Resolve(document, value) is PdfName name ? name.ValueAsLatin1()
            : throw new InvalidOperationException($"The signature /{key} value is not a name.");
    }

    private static string? OptionalText(
        PdfDocument document, PdfDictionary dictionary, string key)
    {
        if (!dictionary.TryGetValue(Name(key), out PdfObject? value)) return null;
        return Resolve(document, value) is PdfString text ? DecodeString(text)
            : throw new InvalidOperationException($"The signature /{key} value is not a string.");
    }

    private static DateTimeOffset? OptionalDate(
        PdfDocument document, PdfDictionary dictionary, string key)
    {
        string? value = OptionalText(document, dictionary, key);
        if (value is null) return null;
        try
        {
            if (!value.StartsWith("D:", StringComparison.Ordinal) || value.Length < 6)
                throw new FormatException();
            string digits = new([.. value.Skip(2).TakeWhile(char.IsAsciiDigit)]);
            if (digits.Length is not (4 or 6 or 8 or 10 or 12 or 14)) throw new FormatException();
            int Part(int offset, int length, int fallback) => digits.Length >= offset + length
                ? int.Parse(digits.AsSpan(offset, length), CultureInfo.InvariantCulture) : fallback;
            int year = Part(0, 4, 1), month = Part(4, 2, 1), day = Part(6, 2, 1);
            int hour = Part(8, 2, 0), minute = Part(10, 2, 0), second = Part(12, 2, 0);
            string suffix = value[(2 + digits.Length)..];
            TimeSpan zone = TimeSpan.Zero;
            if (suffix.Length > 0 && suffix != "Z")
            {
                char sign = suffix[0];
                if (sign is not ('+' or '-')) throw new FormatException();
                string compact = suffix[1..].Replace("'", string.Empty, StringComparison.Ordinal);
                if (compact.Length != 4 || !int.TryParse(compact[..2], out int zoneHour)
                    || !int.TryParse(compact[2..], out int zoneMinute)) throw new FormatException();
                zone = new TimeSpan(zoneHour, zoneMinute, 0) * (sign == '-' ? -1 : 1);
            }
            return new DateTimeOffset(year, month, day, hour, minute, second, zone);
        }
        catch (Exception error) when (error is FormatException or ArgumentOutOfRangeException)
        {
            throw new InvalidOperationException(
                $"The signature /{key} value is not a valid PDF date.", error);
        }
    }

    private static PdfDictionary ResolveDictionary(
        PdfDocument document, PdfObject value, string description)
    {
        PdfObject resolved = Resolve(document, value, description);
        return resolved as PdfDictionary
            ?? throw new InvalidOperationException($"{description} is not a dictionary.");
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

    private sealed record ResolvedValue(
        PdfObject Value, PdfIndirectReference? FinalReference);

    private static PdfArray ResolveArray(PdfDocument document, PdfObject value, string description)
    {
        PdfObject resolved = Resolve(document, value, description);
        return resolved as PdfArray
            ?? throw new InvalidOperationException($"{description} is not an array.");
    }

    private static string DecodeString(PdfString value)
        => PdfUnicodeEncoding.DecodeTextString(
            value.Bytes.Span, "A signature text string");

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));

    private readonly record struct SignatureTransforms(
        PdfSignatureCertificationPermission? CertificationPermission,
        PdfSignatureLockAction? FieldLockAction,
        PdfSignatureLockPermission? FieldLockPermission,
        IReadOnlyList<string>? LockedFields);
}
