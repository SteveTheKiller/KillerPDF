using System.Globalization;
using System.Xml;

namespace KillerPdf.Engine.Formats.Epub;

// Source nodes survive normalization so collapsed whitespace keeps its original inline style.
internal sealed class EpubNativeContent(double fallbackSize, double contentHeight)
{
    internal sealed record Style(double FontSize, double? LineHeight);
    internal sealed record Element(string Name, Element? Parent, Style Style, Style BlockStyle);
    internal sealed record Text(string Value, Element Owner);
    internal sealed record SourceCharacter(Text Node, int Offset)
    {
        internal Style Style => Node.Owner.Style;
        internal Style BlockStyle => Node.Owner.BlockStyle;
    }

    private readonly Stack<Element> _ancestors = new();
    private readonly Dictionary<string, CssProperties> _typeRules = new(StringComparer.Ordinal);
    private int _characters;
    internal List<SourceCharacter?> Characters { get; } = new();
    internal Style DefaultStyle { get; } = new(fallbackSize, null);

    internal void Enter(XmlReader xml)
    {
        string name = xml.LocalName;
        Element? parent = _ancestors.TryPeek(out var ancestor) ? ancestor : null;
        bool allowed = name switch {
            "html" => parent is null && xml.Depth == 0,
            "head" or "body" => parent?.Name == "html",
            "title" => parent?.Name == "head",
            "link" => parent?.Name == "head" && xml.GetAttribute("rel") is "icon" or "stylesheet"
                && !string.IsNullOrWhiteSpace(xml.GetAttribute("href")),
            "p" => parent?.Name == "body",
            "span" => parent?.Name is "p" or "span",
            _ => false };
        if (xml.NamespaceURI != "http://www.w3.org/1999/xhtml" || !allowed)
            throw new NotSupportedException("Native EPUB XHTML element is not implemented: " + name + ".");
        bool empty = xml.IsEmptyElement;
        Style style = parent?.Style ?? DefaultStyle;
        if (_typeRules.TryGetValue(name, out var rule)) style = rule.Apply(style);
        while (xml.MoveToNextAttribute())
        {
            Count(xml.Value.Length);
            if (xml.NamespaceURI == "" && xml.LocalName == "style" && name is "p" or "span")
                style = ParseStyle(xml.Value, style);
            else if (name == "link" && xml.NamespaceURI == "" && xml.LocalName is "href" or "rel" or "type" or "id")
            {
                // Packaged references are validated separately from body painting.
            }
            else if (xml.NamespaceURI != "http://www.w3.org/2000/xmlns/"
                && !(xml.LocalName == "lang" && xml.NamespaceURI is "" or "http://www.w3.org/XML/1998/namespace"))
                throw new NotSupportedException("Native EPUB XHTML attributes and styling are not implemented.");
        }
        xml.MoveToElement();
        var element = new Element(name, parent, style, name == "p" ? style : parent?.BlockStyle ?? style);
        if (!empty) _ancestors.Push(element);
    }

    internal void Exit() => _ancestors.Pop();

    internal Text TextNode(string value)
    {
        return new Text(value, _ancestors.Peek());
    }

    internal void Append(SourceCharacter? source)
    {
        if (Characters.Count >= 100_000)
            throw new InvalidDataException("Native EPUB text exceeds its 100,000 placement limit.");
        Characters.Add(source);
    }

    internal void Trim(int length)
    {
        if (Characters.Count > length) Characters.RemoveRange(length, Characters.Count - length);
    }

    internal void Count(int length)
    {
        _characters += length;
        if (_characters > 100_000)
            throw new InvalidDataException("Native EPUB XHTML exceeds its character work limit.");
    }

    internal void AddStylesheet(string css, CancellationToken cancellationToken)
    {
        Count(css.Length);
        int position = 0;
        while (position < css.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            while (position < css.Length && char.IsWhiteSpace(css[position])) position++;
            if (position == css.Length) break;
            int open = css.IndexOf('{', position);
            int close = open < 0 ? -1 : css.IndexOf('}', open + 1);
            if (close < 0) throw new NotSupportedException("Native EPUB CSS rule syntax is not implemented.");
            string selector = css[position..open].Trim();
            if (selector.Length is 0 or > 128 || !char.IsAsciiLetter(selector[0])
                || selector.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')))
                throw new NotSupportedException("Native EPUB CSS selector is not implemented.");
            CssProperties properties = ParseProperties(css[(open + 1)..close]);
            if (_typeRules.TryGetValue(selector, out var previous))
                properties = new(properties.FontSize ?? previous.FontSize, properties.LineHeight ?? previous.LineHeight);
            _typeRules[selector] = properties;
            position = close + 1;
        }
    }

    private readonly record struct CssProperties(double? FontSize, double? LineHeight)
    {
        internal Style Apply(Style inherited) =>
            new(FontSize ?? inherited.FontSize, LineHeight ?? inherited.LineHeight);
    }

    private Style ParseStyle(string declarations, Style inherited) => ParseProperties(declarations).Apply(inherited);

    private CssProperties ParseProperties(string declarations)
    {
        double? size = null, height = null;
        foreach (string declaration in declarations.Split(';'))
        {
            string part = declaration.Trim();
            if (part.Length == 0) continue;
            int colon = part.IndexOf(':');
            if (colon < 0) throw new NotSupportedException("Native EPUB CSS declaration is unsupported.");
            string property = part[..colon].Trim().ToLowerInvariant();
            string value = part[(colon + 1)..].Trim();
            if (!value.EndsWith("px", StringComparison.OrdinalIgnoreCase)
                || !double.TryParse(value[..^2], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double pixels)
                || !double.IsFinite(pixels) || pixels <= 0 || pixels > contentHeight)
                throw new NotSupportedException("Native EPUB typography requires positive CSS pixel lengths that fit the page.");
            switch (property)
            {
                case "font-size": size = pixels; break;
                case "line-height": height = pixels; break;
                default: throw new NotSupportedException("Native EPUB CSS property is not implemented: " + property + ".");
            }
        }
        return new CssProperties(size, height);
    }
}
