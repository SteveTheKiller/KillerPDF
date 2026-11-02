using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Syntax;

namespace KillerPdf.Engine.Security;

/// <summary>
/// What a document needs to be written again under the encryption it was opened with: the
/// encryption dictionary, the file identifier and the file key the authentication produced. It holds
/// none of the file's content, so a viewer can keep it for as long as a document stays open.
/// </summary>
public sealed class PdfEncryptionSnapshot
{
    private readonly bool _compatibilityRecovery;

    internal PdfEncryptionSnapshot(PdfStandardSecurityHandler handler, PdfDictionary dictionary,
        PdfArray? identifiers, PdfVersion minimumVersion, bool compatibilityRecovery)
    {
        Handler = handler;
        Dictionary = dictionary;
        Identifiers = identifiers;
        MinimumVersion = minimumVersion;
        _compatibilityRecovery = compatibilityRecovery;
    }

    internal PdfStandardSecurityHandler Handler { get; }
    internal PdfDictionary Dictionary { get; }
    internal PdfArray? Identifiers { get; }
    internal PdfVersion MinimumVersion { get; }

    /// <summary>The permissions the file declares, whoever opened it.</summary>
    public PdfDocumentPermissions Permissions => Handler.Permissions;

    /// <summary>The role that authenticated the document this was taken from.</summary>
    public PdfPasswordAuthenticationRole OpenedAs => Handler.AuthenticationRole;

    /// <summary>Whether certificate recipients hold the key, so no password applies.</summary>
    public bool IsCertificateEncryption =>
        Dictionary.TryGetValue(new PdfName("Filter"u8), out PdfObject? filter)
        && filter is PdfName name && name.ValueAsLatin1() == "Adobe.PubSec";

    /// <summary>The role a password authenticates as, or None when it opens nothing.</summary>
    public PdfPasswordAuthenticationRole RoleFor(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (IsCertificateEncryption) return PdfPasswordAuthenticationRole.None;
        ReadOnlyMemory<byte> identifier = Identifiers is { Count: > 0 } identifiers
            && identifiers[0] is PdfString first ? first.Bytes : ReadOnlyMemory<byte>.Empty;
        try
        {
            return PdfStandardSecurityHandler.Create(Dictionary, password, identifier, _compatibilityRecovery)
                .AuthenticationRole;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return PdfPasswordAuthenticationRole.None;
        }
    }
}
