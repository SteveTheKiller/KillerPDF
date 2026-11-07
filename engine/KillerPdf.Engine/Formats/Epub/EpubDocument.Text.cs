using System.Text;
using System.Xml;

namespace KillerPdf.Engine.Formats.Epub;

public sealed partial class EpubDocument
{
    /// <summary>Reads structural XHTML body text in source order without computing CSS layout or fetching resources.</summary>
    public string ReadText(int spineIndex, CancellationToken cancellationToken = default)
        => ReadStructuralText(spineIndex, cancellationToken, null);

    private string ReadStructuralText(int spineIndex, CancellationToken cancellationToken, EpubNativeContent? native)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resource = Spine[spineIndex].Item;
        if (resource.MediaType != "application/xhtml+xml" || Tokens(resource.Properties).Contains("scripted", StringComparer.Ordinal))
            throw new NotSupportedException("EPUB text requires an unscripted XHTML spine resource.");
        const string html = "http://www.w3.org/1999/xhtml";
        var result = new StringBuilder();
        bool pendingSpace = false;
        EpubNativeContent.SourceCharacter? pendingSource = null;
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
                if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace)
                    native?.Count(reader.Value.Length);
                if (reader.NodeType == XmlNodeType.Element)
                {
                    if (reader.NamespaceURI == "http://www.w3.org/2000/svg" && reader.LocalName == "svg")
                    {
                        if (bodyDepth < 0 || reader.Depth <= bodyDepth)
                            throw new NotSupportedException("Native EPUB SVG covers require body content.");
                        var cover = ReadSvgCover(reader, resource.Href, native, ref nodeCount, cancellationToken);
                        pendingSpace = false;
                        native?.AddSvgImage(cover.Reference, cover.Width, cover.Height, result.Length);
                        continue;
                    }
                    native?.Enter(reader);
                    if (reader.LocalName == "script")
                        throw new NotSupportedException("Scripted EPUB XHTML text is unsupported.");
                    bool hyperlink = reader.NamespaceURI == html && reader.LocalName == "a";
                    while (reader.MoveToNextAttribute())
                    {
                        if (reader.LocalName.StartsWith("on", StringComparison.OrdinalIgnoreCase))
                            throw new NotSupportedException("Scripted EPUB XHTML text is unsupported.");
                        if (reader.LocalName is "href" or "src" or "data" or "poster" or "action" or "formaction")
                        {
                            string reference = reader.Value.Trim(' ', '\t', '\r', '\n');
                            try
                            {
                                if (!(hyperlink && reader.NamespaceURI.Length == 0 && reader.LocalName == "href"
                                    && TryExternalHyperlink(reference, out _)))
                                    NavigationTarget(resource.Href, reference);
                            }
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
                        if (native is not null && reader.GetAttribute("id") is { } bodyId)
                            native.Anchors.Add((bodyId, 0));
                        continue;
                    }
                    if (bodyDepth < 0 || reader.Depth <= bodyDepth) continue;
                    if (reader.LocalName == "style") styleDepth = reader.IsEmptyElement ? -1 : reader.Depth;
                    if (styleDepth >= 0) continue;
                    if (native?.CurrentElementDisplayNone == true) continue;
                    Separate(TextBlockLines(reader.LocalName));
                    if (reader.LocalName == "img")
                    {
                        string? reference = reader.GetAttribute("src");
                        if (string.IsNullOrWhiteSpace(reference))
                            throw new NotSupportedException("Native EPUB images require a packaged source.");
                        if (pendingSpace && result.Length > 0 && result[^1] != '\n')
                            Append(' ', pendingSource);
                        pendingSpace = false;
                        if (native is not null)
                            native.Inlines.Add(new(result.Length, reference, native.ImageElement!));
                    }
                    if (native is not null && reader.GetAttribute("id") is { } id)
                        native.Anchors.Add((id, result.Length + (pendingSpace && result.Length > 0 && result[^1] != '\n' ? 1 : 0)));
                    if (reader.LocalName == "br")
                    {
                        pendingSpace = false;
                        if (native is not null)
                            native.Inlines.Add(new(result.Length, null, native.LineBreakSource!.Node.Owner));
                        if (result.Length > 0) Append('\n', native?.LineBreakSource);
                    }
                }
                else if (reader.NodeType == XmlNodeType.EndElement)
                {
                    if (reader.Depth == styleDepth) styleDepth = -1;
                    if (reader.Depth == bodyDepth) bodyDepth = -1;
                    else if (bodyDepth >= 0 && styleDepth < 0 && native?.InsideDisplayNone != true) Separate(TextBlockLines(reader.LocalName));
                    native?.Exit();
                }
                else if (bodyDepth >= 0 && styleDepth < 0 && reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace)
                {
                    if (native?.InsideDisplayNone == true) continue;
                    var source = native?.TextNode(reader.Value);
                    for (int offset = 0; offset < reader.Value.Length; offset++)
                    {
                        char character = reader.Value[offset];
                        cancellationToken.ThrowIfCancellationRequested();
                        if (character is ' ' or '\t' or '\r' or '\n')
                        {
                            if (!pendingSpace && result.Length > 0)
                                pendingSource = source is null ? null : new(source, offset);
                            pendingSpace = result.Length > 0;
                        }
                        else
                        {
                            if (pendingSpace && result.Length > 0 && result[^1] != '\n') Append(' ', pendingSource);
                            pendingSpace = false;
                            Append(character, source is null ? null : new(source, offset));
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
        string text = result.ToString().TrimEnd(' ', '\n');
        native?.Trim(text.Length);
        return text;

        void Append(char character, EpubNativeContent.SourceCharacter? source = null)
        {
            result.Append(character);
            native?.Append(source);
        }

        void Separate(int lines)
        {
            if (lines == 0) return;
            pendingSpace = false;
            if (result.Length == 0) return;
            int existing = 0;
            for (int i = result.Length - 1; i >= 0 && result[i] == '\n'; i--) existing++;
            while (existing++ < lines) Append('\n');
        }
    }

    private static int TextBlockLines(string name) => name switch
    {
        "p" or "div" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or "section" or "article" or
            "blockquote" or "header" or "footer" or "aside" or "nav" or "address" or "figure" or "figcaption" or "main" => 2,
        "ul" or "ol" or "li" or "dl" or "dt" or "dd" or "hr" or "table" or "tr" or "td" => 1,
        _ => 0
    };

    private static bool SupportsTextElement(string name) => TextBlockLines(name) > 0 || name is
        "html" or "head" or "body" or "title" or "meta" or "link" or "style" or "br" or "wbr" or "img" or
        "span" or "a" or "em" or "strong" or "b" or "i" or "u" or "s" or "small" or "sub" or "sup" or
        "q" or "abbr" or "cite" or "code" or "kbd" or "samp" or "var" or "mark" or "time" or "bdi" or "bdo" or "dfn";
}
