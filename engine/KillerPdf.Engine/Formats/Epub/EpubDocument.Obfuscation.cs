using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace KillerPdf.Engine.Formats.Epub;

public sealed partial class EpubDocument
{
    private void RestoreObfuscatedFonts(CancellationToken cancellationToken)
    {
        if (!_resources.TryGetValue("META-INF/encryption.xml", out var data)) return;
        var root = Xml(data).Root;
        XNamespace container = "urn:oasis:names:tc:opendocument:xmlns:container";
        XNamespace encryption = "http://www.w3.org/2001/04/xmlenc#";
        if (root?.Name != container + "encryption" || !root.HasElements)
            throw new InvalidDataException("Encrypted EPUB publications are unsupported.");
        var targets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in root.Elements())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var method = entry.Element(encryption + "EncryptionMethod");
            var cipher = entry.Element(encryption + "CipherData");
            var reference = cipher?.Element(encryption + "CipherReference");
            if (entry.Name != encryption + "EncryptedData" || entry.Elements().Count() != 2 ||
                method is null || method.HasElements ||
                (string?)method.Attribute("Algorithm") != "http://www.idpf.org/2008/embedding" ||
                cipher is null || cipher.Elements().Count() != 1 || reference is null || reference.HasElements ||
                entry.DescendantsAndSelf().Any(x => x.Attribute(XNamespace.Xml + "base") is not null))
                throw new InvalidDataException("Encrypted EPUB publications are unsupported.");
            // References in META-INF documents are relative to the container root.
            var path = ResolvePath("", Required(reference, "URI"));
            if (!Manifest.Any(x => x.Href == path && IsObfuscationFontType(x.MediaType)) || !targets.Add(path))
                throw new InvalidDataException("Invalid EPUB obfuscated font reference.");
        }

        // EPUB requires SHA-1 here as a font obfuscation key, not a security primitive.
        var identifier = new string(Metadata.Identifier.Where(x => x is not (' ' or '\t' or '\r' or '\n')).ToArray());
        var key = SHA1.HashData(Encoding.UTF8.GetBytes(identifier));
        foreach (var path in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var font = _resources[path];
            for (var i = 0; i < Math.Min(1040, font.Length); i++) font[i] ^= key[i % key.Length];
        }
    }

    private static bool IsObfuscationFontType(string mediaType) => mediaType is
        "font/ttf" or "font/otf" or "font/woff" or "font/woff2" or
        "application/font-sfnt" or "application/font-woff" or "application/vnd.ms-opentype";
}
