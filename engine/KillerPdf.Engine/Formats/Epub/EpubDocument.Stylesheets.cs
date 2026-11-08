using System.Text;
using System.Xml;

namespace KillerPdf.Engine.Formats.Epub;

public sealed partial class EpubDocument
{
    private void LoadNativeStylesheets(string documentPath, EpubNativeContent content,
        ref int remainingBytes, ref int remainingReferences, HashSet<string> charged, CancellationToken cancellationToken)
    {
        // Discover head rules before computing any element's inherited style, including html.
        using var stream = new MemoryStream(XhtmlWithoutCanonicalDeclaration(_resources[documentPath]), false);
        using var xml = XmlReader.Create(stream, new XmlReaderSettings {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
            MaxCharactersInDocument = MaxEntryBytes, MaxCharactersFromEntities = 1024 });
        int nodes = 0;
        bool inHead = false;
        try
        {
            while (xml.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++nodes > 2_000_000 || xml.Depth > 128)
                    throw new InvalidDataException("Native EPUB XHTML exceeds its processing limits.");
                if (xml.NamespaceURI != "http://www.w3.org/1999/xhtml") continue;
                if (xml.NodeType == XmlNodeType.EndElement && xml.LocalName == "head" && xml.Depth == 1)
                    inHead = false;
                if (xml.NodeType != XmlNodeType.Element) continue;
                if (xml.LocalName == "body" && xml.Depth == 1) break;
                if (xml.LocalName == "head" && xml.Depth == 1) inHead = !xml.IsEmptyElement;
                // A head style element is an embedded stylesheet in document order with the linked sheets.
                if (inHead && xml.Depth == 2 && xml.LocalName == "style" && !xml.IsEmptyElement
                    && xml.GetAttribute("type") is null or "text/css" or "")
                {
                    var embedded = new StringBuilder();
                    using (var styleText = xml.ReadSubtree())
                        while (styleText.Read())
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (styleText.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace
                                or XmlNodeType.SignificantWhitespace) embedded.Append(styleText.Value);
                        }
                    if (embedded.Length > remainingBytes)
                        throw new InvalidDataException("Native EPUB stylesheet work exceeds its layout limit.");
                    remainingBytes -= embedded.Length;
                    content.AddStylesheet(embedded.ToString(), cancellationToken);
                    continue;
                }
                if (!inHead || xml.Depth != 2 || xml.LocalName != "link"
                    || (xml.GetAttribute("rel") ?? "").ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries)
                        is var relations && (!relations.Contains("stylesheet") || relations.Contains("alternate")))
                    continue;
                string? reference = xml.GetAttribute("href");
                // A browser ignores a stylesheet link it cannot use: another type, nothing packaged, or encrypted.
                if (string.IsNullOrWhiteSpace(reference) || xml.GetAttribute("type") is not (null or "" or "text/css"))
                    continue;
                if (remainingReferences == 0)
                    throw new InvalidDataException("Native EPUB stylesheet work exceeds its 65,536-reference layout limit.");
                remainingReferences--;
                string target;
                try { target = NavigationTarget(documentPath, reference.Trim(' ', '\t', '\r', '\n')); }
                catch (InvalidDataException) { continue; }
                if (target.Contains('#') || !Manifest.Any(item => item.Href == target && item.MediaType == "text/css")
                    || _encryptedResources.Contains(target))
                    continue;
                byte[] bytes = _resources[target];
                // A sheet shared by every chapter is charged once per layout, not once per chapter.
                if (charged.Add(target))
                {
                    if (bytes.Length > remainingBytes)
                        throw new InvalidDataException("Native EPUB stylesheet work exceeds its 8 MiB layout limit.");
                    remainingBytes -= bytes.Length;
                }
                string css;
                try { css = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF'); }
                catch (DecoderFallbackException exception)
                {
                    throw new NotSupportedException("Native EPUB stylesheet encoding is not implemented.", exception);
                }
                content.AddStylesheet(css, cancellationToken);
            }
        }
        catch (XmlException exception)
        {
            throw new InvalidDataException("Invalid native EPUB XHTML document.", exception);
        }
    }
}
