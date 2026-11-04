using System.Text;
using System.Xml;

namespace KillerPdf.Engine.Formats.Epub;

public sealed partial class EpubDocument
{
    private void LoadNativeStylesheets(string documentPath, EpubNativeContent content,
        ref int remainingBytes, ref int remainingReferences, CancellationToken cancellationToken)
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
                if (++nodes > 100_000 || xml.Depth > 128)
                    throw new InvalidDataException("Native EPUB XHTML exceeds its processing limits.");
                if (xml.NamespaceURI != "http://www.w3.org/1999/xhtml") continue;
                if (xml.NodeType == XmlNodeType.EndElement && xml.LocalName == "head" && xml.Depth == 1)
                    inHead = false;
                if (xml.NodeType != XmlNodeType.Element) continue;
                if (xml.LocalName == "body" && xml.Depth == 1) break;
                if (xml.LocalName == "head" && xml.Depth == 1) inHead = !xml.IsEmptyElement;
                if (!inHead || xml.Depth != 2 || xml.LocalName != "link" || xml.GetAttribute("rel") != "stylesheet")
                    continue;
                string? reference = xml.GetAttribute("href");
                if (string.IsNullOrWhiteSpace(reference) || xml.GetAttribute("type") is not (null or "text/css"))
                    throw new NotSupportedException("Native EPUB stylesheet declaration is not implemented.");
                if (remainingReferences == 0)
                    throw new InvalidDataException("Native EPUB stylesheet work exceeds its 1024-reference layout limit.");
                remainingReferences--;
                string target;
                try { target = NavigationTarget(documentPath, reference.Trim(' ', '\t', '\r', '\n')); }
                catch (InvalidDataException exception)
                {
                    throw new NotSupportedException("External or unresolved EPUB stylesheets are unsupported.", exception);
                }
                if (target.Contains('#') || !Manifest.Any(item => item.Href == target && item.MediaType == "text/css"))
                    throw new NotSupportedException("Native EPUB stylesheets require a packaged CSS manifest resource.");
                byte[] bytes = _resources[target];
                if (bytes.Length > remainingBytes)
                    throw new InvalidDataException("Native EPUB stylesheet work exceeds its 1,000,000-byte layout limit.");
                remainingBytes -= bytes.Length;
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
