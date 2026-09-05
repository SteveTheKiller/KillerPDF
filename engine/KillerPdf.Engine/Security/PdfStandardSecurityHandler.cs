using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using KillerPdf.Engine.Objects;
using System.Security.Cryptography.X509Certificates;

namespace KillerPdf.Engine.Security;

internal sealed class PdfPasswordAuthenticationException(string message)
    : CryptographicException(message);

internal sealed class PdfStandardSecurityHandler
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private static readonly PdfName MetadataName = Name("Metadata");
    private static readonly PdfName EmbeddedFileName = Name("EmbeddedFile");
    private static readonly PdfName CrossReferenceName = Name("XRef");
    private static readonly PdfName SignatureName = Name("Sig");
    private static readonly PdfName ContentsName = Name("Contents");
    private static readonly PdfName ByteRangeName = Name("ByteRange");
    private static readonly PdfName TypeName = Name("Type");
    private static readonly byte[] PasswordPadding =
    [
        0x28, 0xBF, 0x4E, 0x5E, 0x4E, 0x75, 0x8A, 0x41,
        0x64, 0x00, 0x4E, 0x56, 0xFF, 0xFA, 0x01, 0x08,
        0x2E, 0x2E, 0x00, 0xB6, 0xD0, 0x68, 0x3E, 0x80,
        0x2F, 0x0C, 0xA9, 0xFE, 0x64, 0x53, 0x69, 0x7A
    ];
    private readonly byte[] _fileKey;
    private readonly CryptMethod _stringMethod;
    private readonly CryptMethod _streamMethod;
    private readonly CryptMethod _embeddedFileMethod;
    private readonly IReadOnlyDictionary<string, CryptMethod> _cryptFilters;
    private readonly bool _encryptMetadata;

    internal PdfPasswordAuthenticationRole AuthenticationRole { get; }
    internal PdfDocumentPermissions Permissions { get; }

    private PdfStandardSecurityHandler(
        byte[] fileKey, CryptMethod stringMethod, CryptMethod streamMethod,
        CryptMethod embeddedFileMethod, bool encryptMetadata,
        int permissions, long revision, PdfPasswordAuthenticationRole authenticationRole,
        IReadOnlyDictionary<string, CryptMethod>? cryptFilters = null)
    {
        _fileKey = fileKey;
        _stringMethod = stringMethod;
        _streamMethod = streamMethod;
        _embeddedFileMethod = embeddedFileMethod;
        _cryptFilters = cryptFilters ?? new Dictionary<string, CryptMethod>();
        _encryptMetadata = encryptMetadata;
        Permissions = PdfDocumentPermissions.FromFlags(permissions, revision);
        AuthenticationRole = authenticationRole;
    }

    internal static PdfStandardSecurityHandler Create(
        PdfDictionary encryption, string password, ReadOnlyMemory<byte> permanentIdentifier,
        bool compatibilityRecovery = false)
    {
        ArgumentNullException.ThrowIfNull(encryption);
        ArgumentNullException.ThrowIfNull(password);
        RequireName(encryption, "Filter", "Standard");
        long version = RequireInteger(encryption, "V");
        long revision = RequireInteger(encryption, "R");
        if ((version, revision) is (1, 2) or (2, 3) or (4, 4)
            || compatibilityRecovery && (version, revision) == (1, 3))
            return CreateLegacy(
                encryption, password, permanentIdentifier.Span, version, revision,
                compatibilityRecovery);
        bool aesGcm = (version, revision) == (6, 7);
        if (!aesGcm && (version != 5 || revision is not (5 or 6)))
            throw new NotSupportedException(
                $"Standard security handler V={version}, R={revision} is not supported.");
        if (RequireInteger(encryption, "Length") != 256)
            throw new InvalidOperationException(
                "AES-256 encryption requires a 256-bit encryption key.");
        byte[] owner = RequireBytes(encryption, "O", 48, compatibilityRecovery);
        byte[] user = RequireBytes(encryption, "U", 48, compatibilityRecovery);
        byte[] ownerEncryptedKey = RequireBytes(encryption, "OE", 32);
        byte[] userEncryptedKey = RequireBytes(encryption, "UE", 32);
        byte[] passwordBytes = PasswordBytes(password, revision >= 6);
        byte[]? fileKey = TryOwnerPassword(
            passwordBytes, owner, user, ownerEncryptedKey, revision);
        PdfPasswordAuthenticationRole authenticationRole = fileKey is null
            ? PdfPasswordAuthenticationRole.User
            : PdfPasswordAuthenticationRole.Owner;
        fileKey ??= TryUserPassword(passwordBytes, user, userEncryptedKey, revision);
        if (fileKey is null)
            throw new PdfPasswordAuthenticationException("The PDF password is incorrect.");
        byte[] permissions = DecryptEcb(fileKey, RequireBytes(encryption, "Perms", 16));
        if (permissions.AsSpan(4, 4).IndexOfAnyExcept((byte)0xFF) >= 0)
            throw new CryptographicException(
                "The PDF encryption permission block has invalid reserved bytes.");
        if (!permissions.AsSpan(9, 3).SequenceEqual("adb"u8))
            throw new CryptographicException("The PDF encryption permission block is invalid.");
        int declaredPermissions = ReadPermissionFlags(encryption, compatibilityRecovery);
        if (!compatibilityRecovery)
            ValidatePermissionFlags(declaredPermissions, revision);
        if (BinaryPrimitives.ReadInt32LittleEndian(permissions) != declaredPermissions)
            throw new CryptographicException("The PDF encryption permissions do not authenticate.");
        bool encryptMetadata = ReadEncryptMetadata(encryption);
        if (permissions[8] != (encryptMetadata ? (byte)'T' : (byte)'F'))
            throw new CryptographicException("The PDF metadata-encryption setting does not authenticate.");
        string requiredMethod = aesGcm ? "AESV4" : "AESV3";
        CryptMethod stringMethod = ReadModernCryptFilter(encryption, "StrF", requiredMethod);
        CryptMethod streamMethod = ReadModernCryptFilter(encryption, "StmF", requiredMethod);
        CryptMethod embeddedFileMethod = encryption.ContainsKey(Name("EFF"))
            ? ReadModernCryptFilter(encryption, "EFF", requiredMethod) : streamMethod;
        return new PdfStandardSecurityHandler(
            fileKey, stringMethod, streamMethod, embeddedFileMethod, encryptMetadata,
            declaredPermissions, revision, authenticationRole,
            ReadCryptFilters(encryption, requiredMethod));
    }

    internal static PdfStandardSecurityHandler CreateCertificate(
        PdfDictionary encryption, X509Certificate2 recipient)
    {
        ArgumentNullException.ThrowIfNull(encryption);
        ArgumentNullException.ThrowIfNull(recipient);
        RequireName(encryption, "Filter", "Adobe.PubSec");
        string subFilter = RequireNameValue(encryption, "SubFilter");
        if (subFilter is not ("adbe.pkcs7.s4" or "adbe.pkcs7.s5"))
            throw new NotSupportedException(
                $"Certificate security subfilter /{subFilter} is not supported.");
        long version = RequireInteger(encryption, "V");
        if (version is not (2 or 4 or 5))
            throw new NotSupportedException(
                $"Certificate security V={version} is not supported.");
        PdfDictionary? defaultCryptFilter = version >= 4
            ? ReadSelectedCryptFilter(encryption, "StmF") : null;
        long lengthBits = ReadCertificateKeyLength(encryption, defaultCryptFilter);
        if (lengthBits is < 40 or > 256 || lengthBits % 8 != 0
            || version == 5 && lengthBits != 256)
            throw new InvalidOperationException(
                "The certificate encryption key length is invalid.");
        int fileKeyLength = checked((int)(lengthBits / 8));
        PdfArray recipients = ReadCertificateRecipients(encryption, defaultCryptFilter);
        ReadOnlyMemory<byte>[] blocks = [.. recipients.Cast<PdfString>()
            .Select(value => value.Bytes)];
        bool encryptMetadata = defaultCryptFilter is not null
            && defaultCryptFilter.ContainsKey(Name("EncryptMetadata"))
            ? ReadEncryptMetadata(defaultCryptFilter)
            : ReadEncryptMetadata(encryption);
        PdfCertificateRecipientMaterial material =
            PdfCertificateRecipientEncryption.Open(
                blocks, recipient, fileKeyLength, encryptMetadata);
        CryptMethod stringMethod, streamMethod, embeddedFileMethod;
        IReadOnlyDictionary<string, CryptMethod>? cryptFilters;
        if (version == 2)
        {
            stringMethod = streamMethod = embeddedFileMethod = CryptMethod.Rc4;
            cryptFilters = null;
        }
        else
        {
            stringMethod = ReadModernCryptFilter(encryption, "StrF", null);
            streamMethod = ReadModernCryptFilter(encryption, "StmF", null);
            embeddedFileMethod = encryption.ContainsKey(Name("EFF"))
                ? ReadModernCryptFilter(encryption, "EFF", null) : streamMethod;
            cryptFilters = ReadCryptFilters(encryption, null);
        }
        return new PdfStandardSecurityHandler(material.FileKey.ToArray(), stringMethod,
            streamMethod, embeddedFileMethod, encryptMetadata,
            material.PermissionFlags, 4, PdfPasswordAuthenticationRole.None,
            cryptFilters);
    }

    private static long ReadCertificateKeyLength(
        PdfDictionary encryption, PdfDictionary? defaultCryptFilter)
    {
        PdfObject? value = null;
        if (!encryption.TryGetValue(Name("Length"), out value))
            defaultCryptFilter?.TryGetValue(Name("Length"), out value);
        if (value is null) return 40;
        if (value is not PdfInteger length)
            throw new InvalidOperationException(
                "The certificate encryption /Length value is not an integer.");
        return length.Value is >= 5 and <= 32 ? length.Value * 8 : length.Value;
    }

    private static PdfDictionary ReadSelectedCryptFilter(
        PdfDictionary encryption, string key)
    {
        if (!encryption.TryGetValue(Name(key), out PdfObject? filterValue)
            || filterValue is not PdfName filter
            || !encryption.TryGetValue(Name("CF"), out PdfObject? filtersValue)
            || filtersValue is not PdfDictionary filters
            || !filters.TryGetValue(filter, out PdfObject? selectedValue)
            || selectedValue is not PdfDictionary selected)
            throw new InvalidOperationException(
                $"The certificate encryption crypt filter for /{key} is missing.");
        return selected;
    }

    private static PdfArray ReadCertificateRecipients(
        PdfDictionary encryption, PdfDictionary? defaultCryptFilter)
    {
        PdfObject? recipientsValue = null;
        if (!encryption.TryGetValue(Name("Recipients"), out recipientsValue))
            defaultCryptFilter?.TryGetValue(Name("Recipients"), out recipientsValue);
        if (recipientsValue is not PdfArray recipients || recipients.Count == 0
            || recipients.Any(value => value is not PdfString))
            throw new InvalidOperationException(
                "The certificate encryption /Recipients value is invalid.");
        return recipients;
    }

    internal static (PdfStandardSecurityHandler Handler, PdfDictionary Dictionary)
        CreateCertificate(PdfCertificateEncryptionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Recipients);
        if (options.AllowHighQualityPrinting && !options.AllowLowQualityPrinting)
            throw new ArgumentException(
                "High-quality printing cannot be allowed when printing is disabled.",
                nameof(options));
        int permissions = -4;
        SetPermission(3, options.AllowLowQualityPrinting);
        SetPermission(4, options.AllowDocumentModification);
        SetPermission(5, options.AllowContentCopying);
        SetPermission(6, options.AllowAnnotationModification);
        SetPermission(9, options.AllowFormFilling);
        SetPermission(10, options.AllowAccessibilityExtraction);
        SetPermission(11, options.AllowDocumentAssembly);
        SetPermission(12, options.AllowHighQualityPrinting);
        PdfCertificateRecipientMaterial material =
            PdfCertificateRecipientEncryption.Create(
                options.Recipients, permissions, options.EncryptMetadata);
        PdfName filter = Name("DefaultCryptFilter");
        PdfArray recipients = new(material.RecipientBlocks.Select(block =>
            (PdfObject)new PdfString(block.Span, PdfStringForm.Hexadecimal)));
        var dictionary = new PdfDictionary([
            Pair("Filter", Name("Adobe.PubSec")),
            Pair("SubFilter", Name("adbe.pkcs7.s5")),
            Pair("V", new PdfInteger(5)), Pair("Length", new PdfInteger(256)),
            Pair("Recipients", recipients),
            Pair("EncryptMetadata", new PdfBoolean(options.EncryptMetadata)),
            Pair("CF", new PdfDictionary([new KeyValuePair<PdfName, PdfObject>(filter,
                new PdfDictionary([Pair("AuthEvent", Name("DocOpen")),
                    Pair("CFM", Name("AESV3")), Pair("Length", new PdfInteger(32)),
                    Pair("Recipients", recipients)]))])),
            Pair("StmF", filter), Pair("StrF", filter), Pair("EFF", filter)
        ]);
        return (new PdfStandardSecurityHandler(material.FileKey.ToArray(),
            CryptMethod.Aes256, CryptMethod.Aes256, CryptMethod.Aes256,
            options.EncryptMetadata, permissions, 4,
            PdfPasswordAuthenticationRole.None,
            new Dictionary<string, CryptMethod> { ["DefaultCryptFilter"] = CryptMethod.Aes256 }),
            dictionary);

        static KeyValuePair<PdfName, PdfObject> Pair(string key, PdfObject value) =>
            new(Name(key), value);
        void SetPermission(int bit, bool allowed)
        {
            int mask = 1 << (bit - 1);
            permissions = allowed ? permissions | mask : permissions & ~mask;
        }
    }

    internal static (PdfStandardSecurityHandler Handler, PdfDictionary Dictionary)
        CreateModernPassword(PdfPasswordEncryptionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.UserPassword);
        ArgumentNullException.ThrowIfNull(options.OwnerPassword);
        if (options.AllowHighQualityPrinting && !options.AllowLowQualityPrinting)
            throw new ArgumentException(
                "High-quality printing cannot be allowed when printing is disabled.",
                nameof(options));
        byte[] userPassword = PasswordBytes(options.UserPassword, normalize: true);
        byte[] ownerPassword = PasswordBytes(options.OwnerPassword, normalize: true);
        bool aesGcm = options.Algorithm == PdfPasswordEncryptionAlgorithm.Aes256Gcm;
        long version = aesGcm ? 6 : 5;
        long revision = aesGcm ? 7 : 6;
        string methodName = aesGcm ? "AESV4" : "AESV3";
        CryptMethod method = aesGcm ? CryptMethod.Aes256Gcm : CryptMethod.Aes256;
        byte[] fileKey = RandomNumberGenerator.GetBytes(32);
        byte[] userValidationSalt = RandomNumberGenerator.GetBytes(8);
        byte[] userKeySalt = RandomNumberGenerator.GetBytes(8);
        byte[] userHash = HashPassword(userPassword, userValidationSalt, null, revision);
        byte[] user = [.. userHash, .. userValidationSalt, .. userKeySalt];
        byte[] userKey = HashPassword(userPassword, userKeySalt, null, revision);
        byte[] userEncryptedKey = EncryptKey(userKey, fileKey);
        byte[] ownerValidationSalt = RandomNumberGenerator.GetBytes(8);
        byte[] ownerKeySalt = RandomNumberGenerator.GetBytes(8);
        byte[] ownerHash = HashPassword(ownerPassword, ownerValidationSalt, user, revision);
        byte[] owner = [.. ownerHash, .. ownerValidationSalt, .. ownerKeySalt];
        byte[] ownerKey = HashPassword(ownerPassword, ownerKeySalt, user, revision);
        byte[] ownerEncryptedKey = EncryptKey(ownerKey, fileKey);
        int permissions = -4;
        SetPermission(3, options.AllowLowQualityPrinting);
        SetPermission(4, options.AllowDocumentModification);
        SetPermission(5, options.AllowContentCopying);
        SetPermission(6, options.AllowAnnotationModification);
        SetPermission(9, options.AllowFormFilling);
        SetPermission(10, options.AllowAccessibilityExtraction);
        SetPermission(11, options.AllowDocumentAssembly);
        SetPermission(12, options.AllowHighQualityPrinting);
        byte[] permissionBlock = RandomNumberGenerator.GetBytes(16);
        BinaryPrimitives.WriteInt32LittleEndian(permissionBlock, permissions);
        permissionBlock.AsSpan(4, 4).Fill(0xFF);
        permissionBlock[8] = options.EncryptMetadata ? (byte)'T' : (byte)'F';
        "adb"u8.CopyTo(permissionBlock.AsSpan(9));
        byte[] encryptedPermissions = EncryptEcb(fileKey, permissionBlock);
        PdfName standardFilter = Name("StdCF");
        var dictionary = new PdfDictionary([
            Pair("Filter", Name("Standard")), Pair("V", new PdfInteger(version)),
            Pair("Length", new PdfInteger(256)), Pair("R", new PdfInteger(revision)),
            Pair("O", Hex(owner)), Pair("U", Hex(user)), Pair("OE", Hex(ownerEncryptedKey)),
            Pair("UE", Hex(userEncryptedKey)), Pair("P", new PdfInteger(permissions)),
            Pair("Perms", Hex(encryptedPermissions)),
            Pair("EncryptMetadata", new PdfBoolean(options.EncryptMetadata)),
            Pair("CF", new PdfDictionary([new KeyValuePair<PdfName, PdfObject>(standardFilter,
                new PdfDictionary([Pair("AuthEvent", Name("DocOpen")), Pair("CFM", Name(methodName)),
                    Pair("Length", new PdfInteger(32))]))])),
            Pair("StmF", standardFilter), Pair("StrF", standardFilter), Pair("EFF", standardFilter)
        ]);
        return (new PdfStandardSecurityHandler(fileKey, method,
            method, method, options.EncryptMetadata,
            permissions, revision, PdfPasswordAuthenticationRole.Owner,
            new Dictionary<string, CryptMethod> { ["StdCF"] = method }), dictionary);

        static KeyValuePair<PdfName, PdfObject> Pair(string key, PdfObject value) =>
            new(Name(key), value);
        static PdfString Hex(byte[] value) => new(value, PdfStringForm.Hexadecimal);
        void SetPermission(int bit, bool allowed)
        {
            int mask = 1 << (bit - 1);
            permissions = allowed ? permissions | mask : permissions & ~mask;
        }
    }

    internal PdfObject Decrypt(
        PdfObject value, int objectNumber, int generation,
        Func<PdfIndirectReference, PdfObject>? resolve = null)
    {
        return value switch
        {
            PdfString text when _stringMethod != CryptMethod.Identity =>
                new PdfString(DecryptBytes(
                    text.Bytes.Span, _stringMethod, objectNumber, generation), text.Form),
            PdfArray array => new PdfArray(array.Select(item =>
                Decrypt(item, objectNumber, generation, resolve))),
            PdfDictionary dictionary => TransformDictionary(
                dictionary, objectNumber, generation, decrypt: true, resolve),
            PdfStream stream => DecryptStream(
                stream, objectNumber, generation, resolve),
            _ => value
        };
    }

    internal PdfObject Encrypt(
        PdfObject value, int objectNumber, int generation,
        Func<PdfIndirectReference, PdfObject>? resolve = null)
    {
        return value switch
        {
            PdfString text when _stringMethod != CryptMethod.Identity =>
                new PdfString(EncryptBytes(
                    text.Bytes.Span, _stringMethod, objectNumber, generation),
                    PdfStringForm.Hexadecimal),
            PdfArray array => new PdfArray(array.Select(item =>
                Encrypt(item, objectNumber, generation, resolve))),
            PdfDictionary dictionary => TransformDictionary(
                dictionary, objectNumber, generation, decrypt: false, resolve),
            PdfStream stream => EncryptStream(
                stream, objectNumber, generation, resolve),
            _ => value
        };
    }

    private PdfDictionary TransformDictionary(
        PdfDictionary dictionary, int objectNumber, int generation, bool decrypt,
        Func<PdfIndirectReference, PdfObject>? resolve)
    {
        bool isSignature = dictionary.TryGetValue(TypeName, out PdfObject? type)
                && ResolveStreamValue(type, resolve, "signature dictionary /Type")
                    is PdfName name && name.Equals(SignatureName)
            || dictionary.ContainsKey(ByteRangeName) && dictionary.ContainsKey(ContentsName);
        return new PdfDictionary(dictionary.Select(entry =>
            new KeyValuePair<PdfName, PdfObject>(entry.Key,
                isSignature && entry.Key.Equals(ContentsName)
                    ? entry.Value
                    : decrypt
                        ? Decrypt(entry.Value, objectNumber, generation, resolve)
                        : Encrypt(entry.Value, objectNumber, generation, resolve))));
    }

    private PdfStream DecryptStream(
        PdfStream stream, int objectNumber, int generation,
        Func<PdfIndirectReference, PdfObject>? resolve)
    {
        if (stream.Dictionary.TryGetValue(TypeName, out PdfObject? rawType)
            && ResolveStreamValue(rawType, resolve, "stream /Type")
                is PdfName rawName && rawName.Equals(CrossReferenceName))
            return stream;
        PdfDictionary dictionary = (PdfDictionary)Decrypt(
            stream.Dictionary, objectNumber, generation, resolve);
        PdfName? type = dictionary.TryGetValue(TypeName, out PdfObject? typeValue)
            ? ResolveStreamValue(typeValue, resolve, "stream /Type") as PdfName
            : null;
        bool isMetadata = type?.Equals(MetadataName) == true;
        bool isEmbeddedFile = type?.Equals(EmbeddedFileName) == true;
        CryptMethod method = ExplicitStreamMethod(stream.Dictionary, resolve)
            ?? (isEmbeddedFile ? _embeddedFileMethod : _streamMethod);
        ReadOnlySpan<byte> data = stream.EncodedData.Span;
        return new PdfStream(dictionary,
            method != CryptMethod.Identity && (_encryptMetadata || !isMetadata)
                ? DecryptBytes(data, method, objectNumber, generation) : data);
    }

    private PdfStream EncryptStream(
        PdfStream stream, int objectNumber, int generation,
        Func<PdfIndirectReference, PdfObject>? resolve)
    {
        PdfDictionary dictionary = (PdfDictionary)Encrypt(
            stream.Dictionary, objectNumber, generation, resolve);
        PdfName? type = dictionary.TryGetValue(TypeName, out PdfObject? typeValue)
            ? ResolveStreamValue(typeValue, resolve, "stream /Type") as PdfName
            : null;
        bool isMetadata = type?.Equals(MetadataName) == true;
        bool isEmbeddedFile = type?.Equals(EmbeddedFileName) == true;
        CryptMethod method = ExplicitStreamMethod(stream.Dictionary, resolve)
            ?? (isEmbeddedFile ? _embeddedFileMethod : _streamMethod);
        ReadOnlySpan<byte> data = stream.EncodedData.Span;
        return new PdfStream(dictionary,
            method != CryptMethod.Identity && (_encryptMetadata || !isMetadata)
                ? EncryptBytes(data, method, objectNumber, generation) : data);
    }

    private CryptMethod? ExplicitStreamMethod(
        PdfDictionary dictionary, Func<PdfIndirectReference, PdfObject>? resolve)
    {
        if (!dictionary.TryGetValue(Name("Filter"), out PdfObject? filterValue)) return null;
        filterValue = ResolveStreamValue(filterValue, resolve, "stream /Filter");
        PdfName[] filters = filterValue switch
        {
            PdfName name => [name],
            PdfArray array => [.. array.Select(item =>
                ResolveStreamValue(item, resolve, "stream /Filter array entry") as PdfName
                ?? throw new InvalidOperationException(
                    "Every stream /Filter array entry must be a name."))],
            _ => throw new InvalidOperationException(
                "A stream /Filter value must be a name or an array of names.")
        };
        if (filters.Length == 0) return null;
        if (filters[0].ValueAsLatin1() != "Crypt")
        {
            if (filters.Any(name => name.ValueAsLatin1() == "Crypt"))
                throw new InvalidOperationException("A stream /Crypt filter must be first in its filter pipeline.");
            return null;
        }
        PdfDictionary? parameters = null;
        if (dictionary.TryGetValue(Name("DecodeParms"), out PdfObject? parameterValue))
        {
            parameterValue = ResolveStreamValue(
                parameterValue, resolve, "stream /DecodeParms");
            if (parameterValue is PdfArray parameterArray
                && parameterArray.Any(item => ResolveStreamValue(item, resolve,
                    "stream /DecodeParms array entry") is not (PdfDictionary or PdfNull)))
                throw new InvalidOperationException(
                    "Each stream /DecodeParms entry must be a dictionary or null.");
            parameters = parameterValue switch
            {
                PdfDictionary single when filters.Length == 1 => single,
                PdfDictionary => throw new InvalidOperationException(
                    "A single stream /DecodeParms dictionary requires exactly one filter."),
                PdfArray array when array.Count != filters.Length =>
                    throw new InvalidOperationException(
                        "A stream /DecodeParms array must have one entry per filter."),
                PdfArray { Count: > 0 } array => ResolveStreamValue(
                    array[0], resolve, "stream /DecodeParms array entry") switch
                {
                    PdfDictionary first => first,
                    PdfNull => null,
                    _ => throw new InvalidOperationException(
                        "Each stream /DecodeParms entry must be a dictionary or null.")
                },
                PdfNull => null,
                _ => throw new InvalidOperationException(
                    "A stream /DecodeParms value does not match its /Crypt filter.")
            };
        }
        string filterName = parameters is not null
            && parameters.TryGetValue(Name("Name"), out PdfObject? nameValue)
            ? (ResolveStreamValue(nameValue, resolve,
                    "/Crypt decode parameter /Name") as PdfName)?.ValueAsLatin1()
                ?? throw new InvalidOperationException("A /Crypt decode parameter /Name is not a name.")
            : "Identity";
        if (filterName == "Identity") return CryptMethod.Identity;
        return _cryptFilters.TryGetValue(filterName, out CryptMethod method)
            ? method : throw new InvalidOperationException(
                $"The stream selects missing crypt filter /{filterName}.");
    }

    private static PdfObject ResolveStreamValue(
        PdfObject value, Func<PdfIndirectReference, PdfObject>? resolve, string description)
    {
        if (resolve is null) return value;
        var visited = new HashSet<(int ObjectNumber, int Generation)>();
        for (int depth = 0; value is PdfIndirectReference reference; depth++)
        {
            if (depth >= 32)
                throw new InvalidOperationException(
                    $"The {description} indirect chain is too deep.");
            if (!visited.Add((reference.ObjectNumber, reference.Generation)))
                throw new InvalidOperationException(
                    $"The {description} indirect chain contains a cycle.");
            value = resolve(reference);
        }
        return value;
    }

    private static PdfStandardSecurityHandler CreateLegacy(
        PdfDictionary encryption,
        string password,
        ReadOnlySpan<byte> permanentIdentifier,
        long version,
        long revision,
        bool compatibilityRecovery)
    {
        if (permanentIdentifier.IsEmpty && !compatibilityRecovery)
            throw new InvalidOperationException(
                "Legacy Standard security requires a permanent document identifier.");
        long keyLengthBits = revision == 2
            ? 40
            : encryption.TryGetValue(Name("Length"), out PdfObject? lengthValue)
                ? lengthValue is PdfInteger length
                    ? length.Value
                    : throw new InvalidOperationException(
                        "The encryption dictionary /Length value is not an integer.")
                : compatibilityRecovery
                    && encryption.TryGetValue(Name("Wength"), out PdfObject? misspelledLength)
                    && misspelledLength is PdfInteger recoveredLength
                        ? recoveredLength.Value : 40;
        if (keyLengthBits is < 40 or > 128 || keyLengthBits % 8 != 0)
            throw new InvalidOperationException(
                "Legacy Standard security requires a byte-aligned 40-bit through 128-bit key.");
        int keyLength = checked((int)(keyLengthBits / 8));
        byte[] owner = RequireBytes(encryption, "O", 32);
        byte[] user = compatibilityRecovery && revision >= 3
            ? RequireLegacyUserBytes(encryption) : RequireBytes(encryption, "U", 32);
        int permissions = ReadPermissionFlags(encryption, compatibilityRecovery);
        if (!compatibilityRecovery)
            ValidatePermissionFlags(permissions, revision);
        bool encryptMetadata = ReadEncryptMetadata(encryption);
        byte[] supplied = PadLegacyPassword(password);
        byte[] recoveredUserPassword = RecoverLegacyUserPassword(
            supplied, owner, keyLength, revision);
        byte[]? fileKey = TryLegacyUserPassword(
            recoveredUserPassword, owner, user, permissions, permanentIdentifier,
            keyLength, revision, encryptMetadata);
        PdfPasswordAuthenticationRole authenticationRole = PdfPasswordAuthenticationRole.Owner;
        if (fileKey is null)
        {
            fileKey = TryLegacyUserPassword(
                supplied, owner, user, permissions, permanentIdentifier,
                keyLength, revision, encryptMetadata);
            authenticationRole = PdfPasswordAuthenticationRole.User;
        }
        if (fileKey is null)
            throw new PdfPasswordAuthenticationException("The PDF password is incorrect.");
        CryptMethod stringMethod;
        CryptMethod streamMethod;
        CryptMethod embeddedFileMethod;
        if (version < 4)
            stringMethod = streamMethod = embeddedFileMethod = CryptMethod.Rc4;
        else
        {
            stringMethod = ReadModernCryptFilter(encryption, "StrF", null);
            streamMethod = ReadModernCryptFilter(encryption, "StmF", null);
            embeddedFileMethod = encryption.ContainsKey(Name("EFF"))
                ? ReadModernCryptFilter(encryption, "EFF", null) : streamMethod;
        }
        return new PdfStandardSecurityHandler(
            fileKey, stringMethod, streamMethod, embeddedFileMethod, encryptMetadata,
            permissions, revision, authenticationRole,
            version == 4 ? ReadCryptFilters(encryption, null) : null);
    }

    private static byte[]? TryLegacyUserPassword(
        byte[] paddedPassword,
        byte[] owner,
        byte[] user,
        int permissions,
        ReadOnlySpan<byte> permanentIdentifier,
        int keyLength,
        long revision,
        bool encryptMetadata)
    {
        byte[] key = LegacyFileKey(
            paddedPassword, owner, permissions, permanentIdentifier,
            keyLength, revision, encryptMetadata);
        byte[] candidate;
        if (revision == 2)
            candidate = Rc4(key, PasswordPadding);
        else
        {
            byte[] input = [.. PasswordPadding, .. permanentIdentifier];
            candidate = MD5.HashData(input);
            candidate = Rc4(key, candidate);
            for (int round = 1; round <= 19; round++)
                candidate = Rc4(XorKey(key, round), candidate);
        }
        int comparedLength = revision == 2 ? 32 : 16;
        return CryptographicOperations.FixedTimeEquals(
            candidate.AsSpan(0, comparedLength), user.AsSpan(0, comparedLength)) ? key : null;
    }

    private static byte[] LegacyFileKey(
        byte[] paddedPassword,
        byte[] owner,
        int permissions,
        ReadOnlySpan<byte> permanentIdentifier,
        int keyLength,
        long revision,
        bool encryptMetadata)
    {
        byte[] input = new byte[paddedPassword.Length + owner.Length + 4
            + permanentIdentifier.Length + (revision >= 4 && !encryptMetadata ? 4 : 0)];
        int offset = 0;
        paddedPassword.CopyTo(input, offset);
        offset += paddedPassword.Length;
        owner.CopyTo(input, offset);
        offset += owner.Length;
        BinaryPrimitives.WriteInt32LittleEndian(input.AsSpan(offset, 4), permissions);
        offset += 4;
        permanentIdentifier.CopyTo(input.AsSpan(offset));
        offset += permanentIdentifier.Length;
        if (revision >= 4 && !encryptMetadata)
            input.AsSpan(offset, 4).Fill(0xFF);
        byte[] hash = MD5.HashData(input);
        if (revision >= 3)
            for (int round = 0; round < 50; round++)
                hash = MD5.HashData(hash.AsSpan(0, keyLength));
        return hash[..keyLength];
    }

    private static byte[] RecoverLegacyUserPassword(
        byte[] paddedOwnerPassword, byte[] owner, int keyLength, long revision)
    {
        byte[] hash = MD5.HashData(paddedOwnerPassword);
        if (revision >= 3)
            for (int round = 0; round < 50; round++) hash = MD5.HashData(hash);
        byte[] key = hash[..keyLength];
        byte[] result = [.. owner];
        if (revision == 2) return Rc4(key, result);
        for (int round = 19; round >= 0; round--)
            result = Rc4(XorKey(key, round), result);
        return result;
    }

    private static byte[] PadLegacyPassword(string password)
    {
        byte[] encoded = EncodePdfDocPassword(password);
        byte[] result = new byte[32];
        int copied = Math.Min(encoded.Length, result.Length);
        encoded.AsSpan(0, copied).CopyTo(result);
        PasswordPadding.AsSpan(0, result.Length - copied).CopyTo(result.AsSpan(copied));
        return result;
    }

    private static byte[] EncodePdfDocPassword(string password)
    {
        var encoded = new List<byte>(password.Length);
        foreach (char character in password)
        {
            int value = character switch
            {
                <= '\u0017' => character,
                >= '\u0020' and <= '\u007E' => character,
                >= '\u00A1' and <= '\u00FF' => character,
                '\u02D8' => 0x18, '\u02C7' => 0x19, '\u02C6' => 0x1A,
                '\u02D9' => 0x1B, '\u02DD' => 0x1C, '\u02DB' => 0x1D,
                '\u02DA' => 0x1E, '\u02DC' => 0x1F,
                '\u2022' => 0x80, '\u2020' => 0x81, '\u2021' => 0x82,
                '\u2026' => 0x83, '\u2014' => 0x84, '\u2013' => 0x85,
                '\u0192' => 0x86, '\u2044' => 0x87, '\u2039' => 0x88,
                '\u203A' => 0x89, '\u2212' => 0x8A, '\u2030' => 0x8B,
                '\u201E' => 0x8C, '\u201C' => 0x8D, '\u201D' => 0x8E,
                '\u2018' => 0x8F, '\u2019' => 0x90, '\u201A' => 0x91,
                '\u2122' => 0x92, '\uFB01' => 0x93, '\uFB02' => 0x94,
                '\u0141' => 0x95, '\u0152' => 0x96, '\u0160' => 0x97,
                '\u0178' => 0x98, '\u017D' => 0x99, '\u0131' => 0x9A,
                '\u0142' => 0x9B, '\u0153' => 0x9C, '\u0161' => 0x9D,
                '\u017E' => 0x9E, '\u20AC' => 0xA0,
                _ => -1
            };
            if (value < 0)
                throw new ArgumentException(
                    $"Legacy PDF passwords cannot represent character U+{(int)character:X4} in PDFDocEncoding.",
                    nameof(password));
            encoded.Add((byte)value);
        }
        return [.. encoded];
    }

    private static byte[]? TryOwnerPassword(
        byte[] password, byte[] owner, byte[] user, byte[] encryptedKey, long revision)
    {
        byte[] validation = HashPassword(password, owner.AsSpan(32, 8), user, revision);
        if (!CryptographicOperations.FixedTimeEquals(validation, owner.AsSpan(0, 32))) return null;
        byte[] key = HashPassword(password, owner.AsSpan(40, 8), user, revision);
        return DecryptKey(key, encryptedKey);
    }

    private static byte[]? TryUserPassword(
        byte[] password, byte[] user, byte[] encryptedKey, long revision)
    {
        byte[] validation = HashPassword(password, user.AsSpan(32, 8), null, revision);
        if (!CryptographicOperations.FixedTimeEquals(validation, user.AsSpan(0, 32))) return null;
        byte[] key = HashPassword(password, user.AsSpan(40, 8), null, revision);
        return DecryptKey(key, encryptedKey);
    }

    private static byte[] HashPassword(
        byte[] password, ReadOnlySpan<byte> salt, byte[]? user, long revision)
    {
        byte[] input = [.. password, .. salt, .. user ?? []];
        if (revision == 5) return SHA256.HashData(input);
        byte[] key = SHA256.HashData(input);
        byte[] encrypted = [];
        int round = 0;
        do
        {
            byte[] block = [.. password, .. key, .. user ?? []];
            byte[] repeated = new byte[checked(block.Length * 64)];
            for (int index = 0; index < 64; index++)
                block.CopyTo(repeated, index * block.Length);
            using Aes aes = Aes.Create();
            aes.KeySize = 128;
            aes.BlockSize = 128;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.None;
            aes.Key = key[..16];
            aes.IV = key[16..32];
            encrypted = aes.EncryptCbc(repeated, aes.IV, PaddingMode.None);
            int selector = 0;
            for (int index = 0; index < 16; index++)
                selector = (selector * 256 + encrypted[index]) % 3;
            key = selector switch
            {
                0 => SHA256.HashData(encrypted),
                1 => SHA384.HashData(encrypted),
                _ => SHA512.HashData(encrypted)
            };
            round++;
        }
        while (round < 64 || encrypted[^1] > round - 32);
        return key[..32];
    }

    private static byte[] PasswordBytes(string password, bool normalize)
    {
        string value;
        if (normalize)
        {
            string mapped = MapSaslPrep(password);
            PdfSaslPrepTables.ValidateAssigned(mapped);
            try
            {
                value = mapped.Normalize(NormalizationForm.FormKC);
            }
            catch (ArgumentException exception)
            {
                throw new ArgumentException(
                    "A PDF password must contain only valid Unicode scalar values.",
                    nameof(password), exception);
            }
        }
        else
            value = password;
        if (normalize)
        {
            ValidateSaslPrepOutput(value);
            PdfSaslPrepTables.Validate(value);
        }
        try
        {
            byte[] bytes = StrictUtf8.GetBytes(value);
            return bytes.Length <= 127 ? bytes : bytes[..127];
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException(
                "A PDF password must contain only valid Unicode scalar values.",
                nameof(password), exception);
        }
    }

    private static string MapSaslPrep(string value)
    {
        var mapped = new StringBuilder(value.Length);
        foreach (char character in value)
        {
            if (character is '\u00AD' or '\u034F' or '\u1806'
                or >= '\u180B' and <= '\u180D'
                or >= '\u200B' and <= '\u200D'
                or '\u2060'
                or >= '\uFE00' and <= '\uFE0F'
                or '\uFEFF')
                continue;
            mapped.Append(character is '\u00A0' or '\u1680'
                or >= '\u2000' and <= '\u200A'
                or '\u202F' or '\u205F' or '\u3000'
                ? ' ' : character);
        }
        return mapped.ToString();
    }

    private static void ValidateSaslPrepOutput(string value)
    {
        foreach (Rune rune in value.EnumerateRunes())
        {
            int scalar = rune.Value;
            UnicodeCategory category = Rune.GetUnicodeCategory(rune);
            bool prohibited = category is UnicodeCategory.Control
                or UnicodeCategory.Format
                or UnicodeCategory.PrivateUse
                or UnicodeCategory.Surrogate
                or UnicodeCategory.LineSeparator
                or UnicodeCategory.ParagraphSeparator
                or UnicodeCategory.OtherNotAssigned
                || scalar is >= 0x2FF0 and <= 0x2FFB
                || scalar is >= 0xFFF9 and <= 0xFFFD
                || scalar is 0x0340 or 0x0341 or 0x200E or 0x200F
                || scalar is >= 0x202A and <= 0x202E
                || scalar is >= 0x206A and <= 0x206F
                || scalar == 0xE0001
                || scalar is >= 0xE0020 and <= 0xE007F
                || scalar is >= 0xFDD0 and <= 0xFDEF
                || (scalar & 0xFFFF) is 0xFFFE or 0xFFFF;
            if (prohibited)
                throw new ArgumentException(
                    $"A PDF revision 6 password contains prohibited SASLprep character U+{scalar:X4}.",
                    nameof(value));
        }
    }

    private static byte[] DecryptKey(byte[] key, byte[] encrypted)
    {
        using Aes aes = Aes.Create();
        aes.KeySize = 256;
        aes.BlockSize = 128;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        aes.Key = key;
        aes.IV = new byte[16];
        return aes.DecryptCbc(encrypted, aes.IV, PaddingMode.None);
    }

    private static byte[] EncryptKey(byte[] key, byte[] cleartext)
    {
        using Aes aes = Aes.Create();
        aes.KeySize = 256;
        aes.BlockSize = 128;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        aes.Key = key;
        aes.IV = new byte[16];
        return aes.EncryptCbc(cleartext, aes.IV, PaddingMode.None);
    }

    private static byte[] EncryptEcb(byte[] key, byte[] cleartext)
    {
        using Aes aes = Aes.Create();
        aes.KeySize = 256;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.Key = key;
        return aes.EncryptEcb(cleartext, PaddingMode.None);
    }

    private static byte[] DecryptEcb(byte[] key, byte[] encrypted)
    {
        using Aes aes = Aes.Create();
        aes.KeySize = 256;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.Key = key;
        return aes.DecryptEcb(encrypted, PaddingMode.None);
    }

    private byte[] DecryptBytes(
        ReadOnlySpan<byte> encrypted, CryptMethod method, int objectNumber, int generation) =>
        method switch
        {
            CryptMethod.Rc4 => Rc4(ObjectKey(objectNumber, generation, aes: false), encrypted),
            CryptMethod.Aes128 => DecryptAes(
                encrypted, ObjectKey(objectNumber, generation, aes: true)),
            CryptMethod.Aes256 => DecryptAes(encrypted, _fileKey),
            CryptMethod.Aes256Gcm => DecryptAesGcm(encrypted, _fileKey),
            _ => encrypted.ToArray()
        };

    private byte[] EncryptBytes(
        ReadOnlySpan<byte> cleartext, CryptMethod method, int objectNumber, int generation) =>
        method switch
        {
            CryptMethod.Rc4 => Rc4(ObjectKey(objectNumber, generation, aes: false), cleartext),
            CryptMethod.Aes128 => EncryptAes(
                cleartext, ObjectKey(objectNumber, generation, aes: true)),
            CryptMethod.Aes256 => EncryptAes(cleartext, _fileKey),
            CryptMethod.Aes256Gcm => EncryptAesGcm(cleartext, _fileKey),
            _ => cleartext.ToArray()
        };

    private byte[] ObjectKey(int objectNumber, int generation, bool aes)
    {
        byte[] input = new byte[_fileKey.Length + 5 + (aes ? 4 : 0)];
        _fileKey.CopyTo(input, 0);
        int offset = _fileKey.Length;
        input[offset++] = (byte)objectNumber;
        input[offset++] = (byte)(objectNumber >> 8);
        input[offset++] = (byte)(objectNumber >> 16);
        input[offset++] = (byte)generation;
        input[offset++] = (byte)(generation >> 8);
        if (aes) "sAlT"u8.CopyTo(input.AsSpan(offset));
        byte[] hash = MD5.HashData(input);
        return hash[..Math.Min(_fileKey.Length + 5, 16)];
    }

    private static byte[] DecryptAes(ReadOnlySpan<byte> encrypted, byte[] key)
    {
        if (encrypted.Length < 32 || encrypted.Length % 16 != 0)
            throw new CryptographicException("An AES-256 encrypted PDF value has an invalid length.");
        using Aes aes = Aes.Create();
        aes.KeySize = key.Length * 8;
        aes.BlockSize = 128;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = key;
        return aes.DecryptCbc(encrypted[16..], encrypted[..16], PaddingMode.PKCS7);
    }

    private static byte[] EncryptAes(ReadOnlySpan<byte> cleartext, byte[] key)
    {
        byte[] iv = RandomNumberGenerator.GetBytes(16);
        using Aes aes = Aes.Create();
        aes.KeySize = key.Length * 8;
        aes.BlockSize = 128;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = key;
        byte[] encrypted = aes.EncryptCbc(cleartext, iv, PaddingMode.PKCS7);
        return [.. iv, .. encrypted];
    }

    private static byte[] DecryptAesGcm(ReadOnlySpan<byte> encrypted, byte[] key)
    {
        const int nonceLength = 12;
        const int tagLength = 16;
        if (encrypted.Length < nonceLength + tagLength)
            throw new CryptographicException(
                "An AES-GCM encrypted PDF value has an invalid length.");
        int ciphertextLength = encrypted.Length - nonceLength - tagLength;
        byte[] cleartext = new byte[ciphertextLength];
        using var aes = new AesGcm(key, tagLength);
        aes.Decrypt(encrypted[..nonceLength],
            encrypted.Slice(nonceLength, ciphertextLength), encrypted[^tagLength..], cleartext);
        return cleartext;
    }

    private static byte[] EncryptAesGcm(ReadOnlySpan<byte> cleartext, byte[] key)
    {
        const int nonceLength = 12;
        const int tagLength = 16;
        byte[] nonce = RandomNumberGenerator.GetBytes(nonceLength);
        byte[] ciphertext = new byte[cleartext.Length];
        byte[] tag = new byte[tagLength];
        using var aes = new AesGcm(key, tagLength);
        aes.Encrypt(nonce, cleartext, ciphertext, tag);
        return [.. nonce, .. ciphertext, .. tag];
    }

    private static CryptMethod ReadModernCryptFilter(
        PdfDictionary encryption, string key, string? requiredMethod)
    {
        if (!encryption.TryGetValue(Name(key), out PdfObject? filterValue))
            return CryptMethod.Identity;
        if (filterValue is not PdfName filter)
            throw new InvalidOperationException($"The encryption dictionary /{key} value is not a name.");
        if (filter.ValueAsLatin1() == "Identity") return CryptMethod.Identity;
        if (!encryption.TryGetValue(Name("CF"), out PdfObject? filtersValue)
            || filtersValue is not PdfDictionary filters
            || !filters.TryGetValue(filter, out PdfObject? selectedValue)
            || selectedValue is not PdfDictionary selected)
            throw new InvalidOperationException($"The encryption crypt filter /{filter.ValueAsLatin1()} is missing.");
        if (key != "EFF"
            && selected.TryGetValue(Name("AuthEvent"), out PdfObject? eventValue)
            && eventValue is PdfName authenticationEvent
            && authenticationEvent.ValueAsLatin1() == "EFOpen")
            throw new InvalidOperationException(
                $"The encryption crypt filter /{filter.ValueAsLatin1()} cannot use /EFOpen for /{key}.");
        return ReadCryptFilterMethod(selected, filter.ValueAsLatin1(), requiredMethod);
    }

    private static Dictionary<string, CryptMethod> ReadCryptFilters(
        PdfDictionary encryption, string? requiredMethod)
    {
        if (!encryption.TryGetValue(Name("CF"), out PdfObject? filtersValue))
            return [];
        PdfDictionary filters = filtersValue as PdfDictionary
            ?? throw new InvalidOperationException("The encryption /CF value is not a dictionary.");
        return filters.ToDictionary(entry => entry.Key.ValueAsLatin1(), entry =>
            ReadCryptFilterMethod(entry.Value as PdfDictionary
                ?? throw new InvalidOperationException(
                    $"Encryption crypt filter /{entry.Key.ValueAsLatin1()} is not a dictionary."),
                entry.Key.ValueAsLatin1(), requiredMethod), StringComparer.Ordinal);
    }

    private static CryptMethod ReadCryptFilterMethod(
        PdfDictionary selected, string filterName, string? requiredMethod)
    {
        string methodName;
        if (!selected.TryGetValue(Name("CFM"), out PdfObject? methodValue))
            methodName = "None";
        else if (methodValue is PdfName method)
            methodName = method.ValueAsLatin1();
        else
            throw new InvalidOperationException(
                $"The encryption crypt filter /{filterName} /CFM value is not a name.");
        if (requiredMethod is not null && methodName != requiredMethod && methodName != "None")
            throw new NotSupportedException(
                $"The encryption crypt filter method /{methodName} is not /{requiredMethod}.");
        if (selected.TryGetValue(Name("Length"), out PdfObject? lengthValue))
        {
            if (lengthValue is not PdfInteger length)
                throw new InvalidOperationException(
                    $"The encryption crypt filter /{filterName} /Length value is not an integer.");
            bool validLength = methodName switch
            {
                "V2" => length.Value is >= 5 and <= 16,
                "AESV2" => length.Value is 16 or 128,
                "AESV3" => length.Value is 32 or 256,
                "AESV4" => length.Value is 32 or 256,
                "None" => length.Value >= 0,
                _ => true
            };
            if (!validLength)
                throw new InvalidOperationException(
                    $"The encryption crypt filter /{filterName} has an invalid /Length for /{methodName}.");
        }
        if (selected.TryGetValue(Name("AuthEvent"), out PdfObject? eventValue)
            && (eventValue is not PdfName authenticationEvent
                || authenticationEvent.ValueAsLatin1() is not ("DocOpen" or "EFOpen")))
            throw new InvalidOperationException(
                $"The encryption crypt filter /{filterName} has an invalid /AuthEvent.");
        return methodName switch
        {
            "V2" => CryptMethod.Rc4,
            "AESV2" => CryptMethod.Aes128,
            "AESV3" => CryptMethod.Aes256,
            "AESV4" => CryptMethod.Aes256Gcm,
            "None" => CryptMethod.Identity,
            _ => throw new NotSupportedException(
                $"Encryption crypt filter method /{methodName} is not supported.")
        };
    }

    private static byte[] XorKey(byte[] key, int value) =>
        [.. key.Select(item => (byte)(item ^ value))];

    private static byte[] Rc4(ReadOnlySpan<byte> key, ReadOnlySpan<byte> input)
    {
        Span<byte> state = stackalloc byte[256];
        for (int index = 0; index < state.Length; index++) state[index] = (byte)index;
        int j = 0;
        for (int index = 0; index < state.Length; index++)
        {
            j = (j + state[index] + key[index % key.Length]) & 255;
            (state[index], state[j]) = (state[j], state[index]);
        }
        byte[] output = new byte[input.Length];
        int i = 0;
        j = 0;
        for (int index = 0; index < input.Length; index++)
        {
            i = (i + 1) & 255;
            j = (j + state[i]) & 255;
            (state[i], state[j]) = (state[j], state[i]);
            output[index] = (byte)(input[index] ^ state[(state[i] + state[j]) & 255]);
        }
        return output;
    }

    private static byte[] RequireBytes(
        PdfDictionary dictionary, string key, int length, bool allowTrailingBytes = false)
    {
        if (!dictionary.TryGetValue(Name(key), out PdfObject? value)
            || value is not PdfString text
            || (allowTrailingBytes ? text.Bytes.Length < length : text.Bytes.Length != length))
            throw new InvalidOperationException(
                $"The encryption dictionary /{key} value is not a {length}-byte string.");
        return text.Bytes.Slice(0, length).ToArray();
    }

    private static byte[] RequireLegacyUserBytes(PdfDictionary encryption)
    {
        if (!encryption.TryGetValue(Name("U"), out PdfObject? value)
            || value is not PdfString text || text.Bytes.Length is < 16 or > 32)
            throw new InvalidOperationException(
                "The encryption dictionary /U value is not a 16-byte through 32-byte string.");
        byte[] user = new byte[32];
        text.Bytes.Span.CopyTo(user);
        return user;
    }

    private static bool ReadEncryptMetadata(PdfDictionary encryption)
    {
        if (!encryption.TryGetValue(Name("EncryptMetadata"), out PdfObject? value))
            return true;
        return value is PdfBoolean flag
            ? flag.Value
            : throw new InvalidOperationException(
                "The encryption /EncryptMetadata value is not boolean.");
    }

    private static void ValidatePermissionFlags(int permissions, long revision)
    {
        int requiredOneBits = revision == 2
            ? unchecked((int)0xFFFFFFC0)
            : unchecked((int)0xFFFFF0C0);
        if ((permissions & 0x3) != 0
            || (permissions & requiredOneBits) != requiredOneBits)
            throw new InvalidOperationException(
                "The encryption /P value has invalid reserved permission bits.");
    }

    private static long RequireInteger(PdfDictionary dictionary, string key)
    {
        if (!dictionary.TryGetValue(Name(key), out PdfObject? value) || value is not PdfInteger integer)
            throw new InvalidOperationException($"The encryption dictionary /{key} value is not an integer.");
        return integer.Value;
    }

    private static int ReadPermissionFlags(
        PdfDictionary encryption, bool compatibilityRecovery)
    {
        long value = RequireInteger(encryption, "P");
        if (compatibilityRecovery && value is > int.MaxValue and <= uint.MaxValue)
            return unchecked((int)(uint)value);
        return checked((int)value);
    }

    private static void RequireName(PdfDictionary dictionary, string key, string expected)
    {
        if (!dictionary.TryGetValue(Name(key), out PdfObject? value)
            || value is not PdfName name || name.ValueAsLatin1() != expected)
            throw new InvalidOperationException(
                $"The encryption dictionary /{key} value is not /{expected}.");
    }

    private static string RequireNameValue(PdfDictionary dictionary, string key)
    {
        if (!dictionary.TryGetValue(Name(key), out PdfObject? value)
            || value is not PdfName name)
            throw new InvalidOperationException(
                $"The encryption dictionary /{key} value is not a name.");
        return name.ValueAsLatin1();
    }

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));

    private enum CryptMethod
    {
        Identity,
        Rc4,
        Aes128,
        Aes256,
        Aes256Gcm
    }
}
