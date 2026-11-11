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
        if (_oversizedResources.Contains(resource.Href)) throw UnavailableResource(resource.Href);
        if (resource.MediaType != "application/xhtml+xml" || Tokens(resource.Properties).Contains("scripted", StringComparer.Ordinal))
            throw new NotSupportedException("EPUB text requires an unscripted XHTML spine resource.");
        if (_encryptedResources.Contains(resource.Href)) throw EncryptedChapter(resource.Href);
        const string html = "http://www.w3.org/1999/xhtml";
        var result = new StringBuilder();
        bool pendingSpace = false;
        EpubNativeContent.SourceCharacter? pendingSource = null;
        bool foundBody = false;
        int bodyDepth = -1;
        int styleDepth = -1;
        int preDepth = -1;
        int preservedTextEnd = 0;
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
                // Two million nodes is what an 8 MiB chapter of four-byte empty elements could hold.
                if (++nodeCount > 2_000_000 || reader.Depth > 128)
                    throw new InvalidDataException("EPUB XHTML text exceeds processing limits.");
                if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace)
                    native?.Count(reader.Value.Length);
                if (reader.NodeType == XmlNodeType.Element)
                {
                    if (reader.NamespaceURI == "http://www.w3.org/2000/svg" && reader.LocalName == "svg")
                    {
                        if (bodyDepth < 0 || reader.Depth <= bodyDepth)
                            throw new NotSupportedException("Native EPUB SVG covers require body content.");
                        // A drawing the reader cannot show is left out as one item and reading goes on after it;
                        // scripting still keeps the chapter from showing. Closing the subtree ends on the svg's end.
                        EpubSvgCover? cover = null;
                        string? omitted = null;
                        using (var drawing = reader.ReadSubtree())
                        {
                            drawing.Read();
                            try { cover = ReadSvgCover(drawing, resource.Href, native, ref nodeCount, cancellationToken); }
                            catch (NotSupportedException error) when (!error.Message.Contains("Scripted", StringComparison.Ordinal))
                            {
                                omitted = error.Message;
                            }
                        }
                        pendingSpace = false;
                        if (cover is not null) native?.AddSvgImage(cover, result.Length);
                        else native?.OmittedDrawings.Add(omitted!);
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
                        // A link to a file the book does not contain stays inert and is reported; an image that is
                        // missing is omitted when it is laid out. Nothing is ever fetched from outside the package.
                        if (hyperlink && reader.NamespaceURI.Length == 0 && reader.LocalName == "href" && native is not null)
                        {
                            string reference = reader.Value.Trim(' ', '\t', '\r', '\n');
                            if (!TryExternalHyperlink(reference, out _) && TryNavigationTarget(resource.Href, reference) is null)
                                native.UnresolvedLinks.Add(reference);
                        }
                        // A source set is an optional alternative to src, which is used instead; xml:base would
                        // change how every reference resolves, so it stays refused for this chapter.
                        if (reader.LocalName == "base" && reader.NamespaceURI == "http://www.w3.org/XML/1998/namespace")
                            throw new NotSupportedException("EPUB XHTML base overrides are unsupported.");
                    }
                    reader.MoveToElement();
                    if (reader.Depth == 0 && (reader.LocalName != "html" || reader.NamespaceURI != html))
                        throw new InvalidDataException("EPUB text requires an XHTML document.");
                    // Unknown and foreign-namespace elements contribute their text as inline content, as HTML requires.
                    if (reader.NamespaceURI == html && reader.LocalName == "body" && reader.Depth == 1)
                    {
                        if (foundBody) throw new InvalidDataException("Duplicate EPUB XHTML body.");
                        foundBody = true;
                        bodyDepth = reader.IsEmptyElement ? -1 : reader.Depth;
                        if (native is not null && reader.GetAttribute("id") is { } bodyId)
                            native.Anchors.Add((bodyId, 0, native.Body!));
                        continue;
                    }
                    if (bodyDepth < 0 || reader.Depth <= bodyDepth) continue;
                    if (reader.LocalName == "style") styleDepth = reader.IsEmptyElement ? -1 : reader.Depth;
                    if (styleDepth >= 0) continue;
                    if (native?.CurrentElementDisplayNone == true)
                    {
                        // An anchor inside a display:none subtree is still a destination; it lands where the
                        // following visible text starts.
                        if (reader.GetAttribute("id") is { } hiddenId) native.Anchors.Add((hiddenId, result.Length, native.CurrentElement!));
                        continue;
                    }
                    if (reader.LocalName == "pre")
                    {
                        if (preDepth < 0 && !reader.IsEmptyElement) preDepth = reader.Depth;
                    }
                    Separate(TextBlockLines(reader.LocalName));
                    if (reader.LocalName == "img")
                    {
                        string reference = reader.GetAttribute("src") ?? "";
                        if (pendingSpace && result.Length > 0 && result[^1] != '\n')
                            Append(' ', pendingSource);
                        pendingSpace = false;
                        if (native is not null)
                            native.Inlines.Add(new(result.Length, reference, native.ImageElement!));
                    }
                    if (native is not null && reader.GetAttribute("id") is { } id)
                        native.Anchors.Add((id, result.Length + (pendingSpace && result.Length > 0 && result[^1] != '\n' ? 1 : 0), native.CurrentElement!));
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
                    if (reader.Depth == preDepth) preDepth = -1;
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
                        // U+2028 and U+2029 are hard breaks in any text; the formatting tree turns them into line ends.
                        if (character is '\u2028' or '\u2029')
                        {
                            pendingSpace = false;
                            Append('\n', source is null ? null : new(source, offset));
                            if (preDepth >= 0) preservedTextEnd = result.Length;
                            continue;
                        }
                        if (preDepth >= 0)
                        {
                            pendingSpace = false;
                            // A CR written as &#13; survives parsing; like XML end-of-line handling, CR LF and a
                            // lone CR end the line once, as a line feed.
                            if (character == '\r')
                            {
                                if (offset + 1 < reader.Value.Length && reader.Value[offset + 1] == '\n') continue;
                                character = '\n';
                            }
                            Append(character, source is null ? null : new(source, offset));
                            preservedTextEnd = result.Length;
                        }
                        else if (character is ' ' or '\t' or '\r' or '\n')
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
        int textLength = result.Length;
        while (textLength > preservedTextEnd && result[textLength - 1] is ' ' or '\n') textLength--;
        string text = result.ToString(0, textLength);
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
        "p" or "pre" or "div" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or "section" or "article" or
            "blockquote" or "header" or "footer" or "aside" or "nav" or "address" or "figure" or "figcaption" or "main"
            or "center" or "hgroup" or "details" or "summary" or "fieldset" or "legend" or "menu" or "dir" or "noscript" or "search" => 2,
        "ul" or "ol" or "li" or "dl" or "dt" or "dd" or "hr" or "table" or "tbody" or "thead" or "tfoot" or "tr" or "td" or "th"
            or "caption" => 1,
        _ => 0
    };
}
