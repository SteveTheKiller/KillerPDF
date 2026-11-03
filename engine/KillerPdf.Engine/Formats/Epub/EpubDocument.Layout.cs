using System.Xml;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Rendering;

namespace KillerPdf.Engine.Formats.Epub;

public sealed partial class EpubDocument
{
    /// <summary>Lays out native EPUB pages in CSS pixels using an explicit fallback font.</summary>
    public EpubLayoutDocument CreateLayout(EpubLayoutOptions options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.DefaultFont);
        if (!double.IsFinite(options.PageWidth) || options.PageWidth is <= 0 or > 32768)
            throw new ArgumentOutOfRangeException(nameof(options), "Native page width must be in (0,32768].");
        if (!double.IsFinite(options.PageHeight) || options.PageHeight is <= 0 or > 32768)
            throw new ArgumentOutOfRangeException(nameof(options), "Native page height must be in (0,32768].");
        if (!double.IsFinite(options.Margin) || options.Margin < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Native page margin must be finite and nonnegative.");
        double contentWidth = options.PageWidth - 2 * options.Margin;
        double contentHeight = options.PageHeight - 2 * options.Margin;
        if (contentWidth <= 0 || contentHeight <= 0 || !double.IsFinite(options.FontSize)
            || options.FontSize <= 0 || options.FontSize > contentHeight)
            throw new ArgumentOutOfRangeException(nameof(options), "Native typography must fit a positive content box.");
        if ((long)Math.Ceiling(options.PageWidth) * (long)Math.Ceiling(options.PageHeight)
            > 128L * 1024 * 1024 / (2 * 4 * sizeof(float)))
            throw new InvalidDataException("The native page exceeds its combined 128 MiB pixel storage limit.");
        if (Layout != "reflowable" || PageProgressionDirection is not ("default" or "ltr")
            || Spine.Count != 1 || !Spine[0].IsLinear
            || !string.IsNullOrWhiteSpace(Spine[0].Properties))
            throw new NotSupportedException("Native EPUB layout requires one linear, reflowable, left-to-right spine item.");
        var resource = Spine[0].Item;
        if (resource.MediaType != "application/xhtml+xml" || !string.IsNullOrWhiteSpace(resource.Properties))
            throw new NotSupportedException("Native EPUB layout requires plain unscripted XHTML.");
        PreflightNativeXhtml(_resources[resource.Href], cancellationToken);
        string text = ReadText(0, cancellationToken);
        if (text.Length > 100_000)
            throw new InvalidDataException("Native EPUB text exceeds its 100,000 placement limit.");
        var font = options.DefaultFont;
        var reader = new TrueTypeOutlineReader(font);
        var placements = new List<EpubTextPlacement>(text.Length);
        var glyphs = new List<NativeGlyphPaint>(text.Length);
        double left = options.Margin;
        double top = options.Margin;
        double baseline = top + font.Ascender * options.FontSize / font.UnitsPerEm;
        for (int index = 0; index < text.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            char character = text[index];
            if (character == '\n')
            {
                if (index + 1 >= text.Length || text[index + 1] != '\n')
                    throw new NotSupportedException("Native EPUB line breaking is not implemented.");
                // Structural paragraph separators retain their source indices but have no placement.
                top += 2 * options.FontSize;
                if (top + options.FontSize > options.PageHeight - options.Margin)
                    throw new NotSupportedException("Native EPUB pagination is not implemented.");
                left = options.Margin;
                baseline = top + font.Ascender * options.FontSize / font.UnitsPerEm;
                index++;
                continue;
            }
            if (character is < ' ' or > '~')
                throw new NotSupportedException("Native EPUB layout currently supports only printable ASCII text.");
            ushort glyph = font.GetGlyphId(character);
            if (glyph == 0 && character != ' ')
                throw new NotSupportedException($"The native EPUB font has no glyph for U+{(int)character:X4}.");
            PdfGlyphOutline outline = reader.Outline(glyph)
                ?? throw new NotSupportedException("The native EPUB glyph outline format is not implemented.");
            // The public advance API rounds to 1000 units; finer font-metric precision is a later slice.
            double width = font.GetPdfAdvanceWidth(glyph) / 1000d * options.FontSize;
            if (character == ' ')
            {
                // Each collapsed separator scans only the following word, keeping lookahead linear.
                double wordWidth = 0;
                for (int next = index + 1; next < text.Length && text[next] is not (' ' or '\n'); next++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    char nextCharacter = text[next];
                    if (nextCharacter is < ' ' or > '~')
                        throw new NotSupportedException("Native EPUB layout currently supports only printable ASCII text.");
                    ushort nextGlyph = font.GetGlyphId(nextCharacter);
                    if (nextGlyph == 0)
                        throw new NotSupportedException($"The native EPUB font has no glyph for U+{(int)nextCharacter:X4}.");
                    wordWidth += font.GetPdfAdvanceWidth(nextGlyph) / 1000d * options.FontSize;
                }
                if (wordWidth > contentWidth)
                    throw new NotSupportedException("The native EPUB word exceeds the available line width.");
                if (left + width + wordWidth > options.PageWidth - options.Margin)
                {
                    top += options.FontSize;
                    if (top + options.FontSize > options.PageHeight - options.Margin)
                        throw new NotSupportedException("Native EPUB pagination is not implemented.");
                    left = options.Margin;
                    baseline = top + font.Ascender * options.FontSize / font.UnitsPerEm;
                    continue;
                }
            }
            if (left + width > options.PageWidth - options.Margin)
                throw new NotSupportedException("The native EPUB word exceeds the available line width.");
            placements.Add(new EpubTextPlacement(0, index, 1, left, top, width, options.FontSize));
            if (outline.Contours.Count != 0)
                glyphs.Add(new NativeGlyphPaint(outline, left, baseline, options.FontSize / 1000));
            left += width;
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new EpubLayoutDocument(options, [new EpubLayoutPage(text, placements, glyphs)]);
    }

    private static void PreflightNativeXhtml(byte[] bytes, CancellationToken cancellationToken)
    {
        if (bytes.Length > MaxEntryBytes)
            throw new InvalidDataException("Native EPUB XHTML exceeds its 8 MiB resource limit.");
        using var stream = new MemoryStream(bytes, false);
        using var xml = XmlReader.Create(stream, new XmlReaderSettings {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
            MaxCharactersInDocument = MaxEntryBytes, MaxCharactersFromEntities = 1024 });
        var ancestors = new Stack<string>();
        int nodes = 0, characters = 0;
        try
        {
            while (xml.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++nodes > 100_000 || xml.Depth > 128)
                    throw new InvalidDataException("Native EPUB XHTML exceeds its processing limits.");
                if (xml.NodeType == XmlNodeType.ProcessingInstruction)
                    throw new NotSupportedException("Native EPUB processing instructions are not implemented.");
                if (xml.NodeType == XmlNodeType.Element)
                {
                    string name = xml.LocalName;
                    string? parent = ancestors.TryPeek(out string? ancestor) ? ancestor : null;
                    bool allowed = name switch {
                        "html" => parent is null && xml.Depth == 0,
                        "head" or "body" => parent == "html",
                        "title" => parent == "head",
                        "p" => parent == "body",
                        _ => false };
                    if (xml.NamespaceURI != "http://www.w3.org/1999/xhtml" || !allowed)
                        throw new NotSupportedException("Native EPUB XHTML element is not implemented: " + name + ".");
                    bool empty = xml.IsEmptyElement;
                    while (xml.MoveToNextAttribute())
                    {
                        if (xml.NamespaceURI != "http://www.w3.org/2000/xmlns/"
                            && !(xml.LocalName == "lang" && xml.NamespaceURI is "" or "http://www.w3.org/XML/1998/namespace"))
                            throw new NotSupportedException("Native EPUB XHTML attributes and styling are not implemented.");
                        characters += xml.Value.Length;
                        if (characters > 100_000)
                            throw new InvalidDataException("Native EPUB XHTML exceeds its character work limit.");
                    }
                    xml.MoveToElement();
                    if (!empty) ancestors.Push(name);
                }
                else if (xml.NodeType == XmlNodeType.EndElement)
                    ancestors.Pop();
                else if (xml.NodeType is XmlNodeType.Text or XmlNodeType.CDATA
                    or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace)
                {
                    characters += xml.Value.Length;
                    if (characters > 100_000)
                        throw new InvalidDataException("Native EPUB XHTML exceeds its character work limit.");
                }
            }
        }
        catch (XmlException exception)
        {
            throw new InvalidDataException("Invalid native EPUB XHTML document.", exception);
        }
    }
}
