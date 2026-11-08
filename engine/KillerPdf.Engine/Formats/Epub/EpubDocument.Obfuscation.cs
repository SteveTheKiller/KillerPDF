using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace KillerPdf.Engine.Formats.Epub;

public sealed partial class EpubDocument
{
    // Resources protected by real encryption; each is refused where it is used, never the whole book.
    private readonly HashSet<string> _encryptedResources = new(StringComparer.Ordinal);

    private void RestoreObfuscatedFonts(CancellationToken cancellationToken)
    {
        if (!_resources.TryGetValue("META-INF/encryption.xml", out var data)) return;
        var root = Xml(data).Root;
        XNamespace container = "urn:oasis:names:tc:opendocument:xmlns:container";
        XNamespace encryption = "http://www.w3.org/2001/04/xmlenc#";
        if (root?.Name != container + "encryption")
            throw new InvalidDataException("Encrypted EPUB publications are unsupported.");
        var idpfTargets = new HashSet<string>(StringComparer.Ordinal);
        var adobeTargets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in root.Elements())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var method = entry.Element(encryption + "EncryptionMethod");
            var reference = entry.Element(encryption + "CipherData")?.Element(encryption + "CipherReference");
            string? uri = (string?)reference?.Attribute("URI");
            // An entry that names no packaged resource protects nothing the reader opens.
            if (string.IsNullOrWhiteSpace(uri)
                || entry.DescendantsAndSelf().Any(x => x.Attribute(XNamespace.Xml + "base") is not null)) continue;
            string path;
            // References in META-INF documents are relative to the container root.
            try { path = ResolvePath("", uri); }
            catch (InvalidDataException) { continue; }
            if (!_resources.ContainsKey(path)) continue;
            string? algorithm = (string?)method?.Attribute("Algorithm");
            // Key material or transforms mean a real cipher, never the fixed-key obfuscation scheme.
            bool plain = entry.Elements().All(x => x.Name == encryption + "EncryptionMethod" || x.Name == encryption + "CipherData")
                && reference!.Elements().Any() == false;
            bool font = plain && Manifest.Any(x => x.Href == path && IsObfuscationFontType(x.MediaType));
            // Font obfuscation (IDPF and Adobe) only scrambles a font's first bytes; it is not DRM. Anything
            // else is real encryption, refused where that resource is used instead of for the whole book.
            if (font && algorithm == "http://www.idpf.org/2008/embedding") idpfTargets.Add(path);
            else if (font && algorithm == "http://ns.adobe.com/pdf/enc#RC") adobeTargets.Add(path);
            else _encryptedResources.Add(path);
        }

        // EPUB requires SHA-1 here as a font obfuscation key, not a security primitive.
        var identifier = new string(Metadata.Identifier.Where(x => x is not (' ' or '\t' or '\r' or '\n')).ToArray());
        var key = SHA1.HashData(Encoding.UTF8.GetBytes(identifier));
        foreach (var path in idpfTargets.Except(adobeTargets))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = _resources[path];
            for (var i = 0; i < Math.Min(1040, bytes.Length); i++) bytes[i] ^= key[i % key.Length];
        }
        // Adobe obfuscation XORs the first 1024 bytes with the 16 bytes of the identifier's UUID.
        // Without a UUID identifier the font stays scrambled and text uses installed fonts instead.
        string hex = new(Metadata.Identifier.Replace("urn:uuid:", "", StringComparison.OrdinalIgnoreCase)
            .Where(char.IsAsciiHexDigit).ToArray());
        if (hex.Length != 32) return;
        byte[] adobeKey = Convert.FromHexString(hex);
        foreach (var path in adobeTargets.Except(idpfTargets))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = _resources[path];
            for (var i = 0; i < Math.Min(1024, bytes.Length); i++) bytes[i] ^= adobeKey[i % adobeKey.Length];
        }
    }

    private static bool IsObfuscationFontType(string mediaType) => mediaType is
        "font/ttf" or "font/otf" or "font/woff" or "font/woff2" or "font/sfnt" or
        "application/font-sfnt" or "application/font-woff" or "application/vnd.ms-opentype" or
        "application/x-font-ttf" or "application/x-font-otf" or "application/x-font-truetype" or
        "application/x-font-opentype" or "application/x-truetype-font";

    private static NotSupportedException EncryptedChapter(string href) =>
        new("Native EPUB chapter is encrypted and cannot be read: " + href + ".");
}
