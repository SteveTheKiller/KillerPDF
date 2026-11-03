using System.Text;
using System.Xml;

namespace KillerPdf.Engine.Formats.Epub;

public sealed partial class EpubDocument
{
    /// <summary>Reads structural XHTML body text in source order without computing CSS layout or fetching resources.</summary>
    public string ReadText(int spineIndex, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resource = Spine[spineIndex].Item;
        if (resource.MediaType != "application/xhtml+xml" || Tokens(resource.Properties).Contains("scripted", StringComparer.Ordinal))
            throw new NotSupportedException("EPUB text requires an unscripted XHTML spine resource.");
        const string html = "http://www.w3.org/1999/xhtml";
        var result = new StringBuilder();
        bool pendingSpace = false;
        bool foundBody = false;
        int bodyDepth = -1;
        int styleDepth = -1;
        int nodeCount = 0;
        try
        {
            using var stream = new MemoryStream(XhtmlWithoutCanonicalDeclaration(_resources[resource.Href]), false);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                MaxCharactersInDocument = 8 * 1024 * 1024, MaxCharactersFromEntities = 1024
            });
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++nodeCount > 100000 || reader.Depth > 128)
                    throw new InvalidDataException("EPUB XHTML text exceeds processing limits.");
                if (reader.NodeType == XmlNodeType.ProcessingInstruction)
                    throw new NotSupportedException("EPUB XHTML processing instructions are unsupported.");
                if (reader.NodeType == XmlNodeType.Element)
                {
                    if (reader.LocalName == "script")
                        throw new NotSupportedException("Scripted EPUB XHTML text is unsupported.");
                    while (reader.MoveToNextAttribute())
                    {
                        if (reader.LocalName.StartsWith("on", StringComparison.OrdinalIgnoreCase))
                            throw new NotSupportedException("Scripted EPUB XHTML text is unsupported.");
                        if (reader.LocalName is "href" or "src" or "data" or "poster" or "action" or "formaction")
                        {
                            try { NavigationTarget(resource.Href, reader.Value.Trim(' ', '\t', '\r', '\n')); }
                            catch (InvalidDataException exception)
                            {
                                throw new NotSupportedException("External or unresolved EPUB XHTML references are unsupported.", exception);
                            }
                        }
                        if (reader.LocalName == "srcset" || (reader.LocalName == "base" && reader.NamespaceURI == "http://www.w3.org/XML/1998/namespace"))
                            throw new NotSupportedException("EPUB XHTML source sets and base overrides are unsupported.");
                    }
                    reader.MoveToElement();
                    if (reader.Depth == 0 && (reader.LocalName != "html" || reader.NamespaceURI != html))
                        throw new InvalidDataException("EPUB text requires an XHTML document.");
                    if (reader.NamespaceURI != html || !SupportsTextElement(reader.LocalName))
                        throw new NotSupportedException("Unsupported EPUB XHTML text element: " + reader.LocalName + ".");
                    if (reader.NamespaceURI == html && reader.LocalName == "body" && reader.Depth == 1)
                    {
                        if (foundBody) throw new InvalidDataException("Duplicate EPUB XHTML body.");
                        foundBody = true;
                        bodyDepth = reader.IsEmptyElement ? -1 : reader.Depth;
                        continue;
                    }
                    if (bodyDepth < 0 || reader.Depth <= bodyDepth) continue;
                    if (reader.LocalName == "style") styleDepth = reader.IsEmptyElement ? -1 : reader.Depth;
                    if (styleDepth >= 0) continue;
                    Separate(TextBlockLines(reader.LocalName));
                    if (reader.LocalName == "br")
                    {
                        pendingSpace = false;
                        if (result.Length > 0) result.Append('\n');
                    }
                }
                else if (reader.NodeType == XmlNodeType.EndElement)
                {
                    if (reader.Depth == styleDepth) styleDepth = -1;
                    if (reader.Depth == bodyDepth) bodyDepth = -1;
                    else if (bodyDepth >= 0 && styleDepth < 0) Separate(TextBlockLines(reader.LocalName));
                }
                else if (bodyDepth >= 0 && styleDepth < 0 && reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace)
                {
                    foreach (char character in reader.Value)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (character is ' ' or '\t' or '\r' or '\n') pendingSpace = result.Length > 0;
                        else
                        {
                            if (pendingSpace && result.Length > 0 && result[^1] != '\n') result.Append(' ');
                            pendingSpace = false;
                            result.Append(character);
                        }
                    }
                }
            }
        }
        catch (XmlException exception)
        {
            throw new InvalidDataException("Invalid EPUB XHTML text document.", exception);
        }
        if (!foundBody) throw new InvalidDataException("Missing EPUB XHTML body.");
        return result.ToString().TrimEnd(' ', '\n');

        void Separate(int lines)
        {
            if (lines == 0) return;
            pendingSpace = false;
            if (result.Length == 0) return;
            int existing = 0;
            for (int i = result.Length - 1; i >= 0 && result[i] == '\n'; i--) existing++;
            while (existing++ < lines) result.Append('\n');
        }
    }

    private static int TextBlockLines(string name) => name switch
    {
        "p" or "div" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or "section" or "article" or
            "blockquote" or "header" or "footer" or "aside" or "nav" or "address" or "figure" or "figcaption" or "main" => 2,
        "ul" or "ol" or "li" or "dl" or "dt" or "dd" or "hr" => 1,
        _ => 0
    };

    private static bool SupportsTextElement(string name) => TextBlockLines(name) > 0 || name is
        "html" or "head" or "body" or "title" or "meta" or "link" or "style" or "br" or "wbr" or
        "span" or "a" or "em" or "strong" or "b" or "i" or "u" or "s" or "small" or "sub" or "sup" or
        "q" or "abbr" or "cite" or "code" or "kbd" or "samp" or "var" or "mark" or "time" or "bdi" or "bdo" or "dfn";
}
