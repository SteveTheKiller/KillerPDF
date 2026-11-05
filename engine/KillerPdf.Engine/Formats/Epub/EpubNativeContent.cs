using System.Globalization;
using System.Xml;
using KillerPdf.Engine.Authoring;

namespace KillerPdf.Engine.Formats.Epub;

// Source nodes survive normalization so collapsed whitespace keeps its original inline style.
internal sealed class EpubNativeContent(double fallbackSize, double contentHeight)
{
    internal readonly record struct BackgroundColor(PdfRgbColor? Color);
    internal sealed record Style(double FontSize, double? LineHeight, PdfRgbColor? Color = null, BackgroundColor? Background = null);
    internal sealed record Element(string Name, Element? Parent, Style Style, Style BlockStyle);
    internal sealed record Text(string Value, Element Owner);
    internal sealed record SourceCharacter(Text Node, int Offset)
    {
        internal Style Style => Node.Owner.Style;
        internal Style BlockStyle => Node.Owner.BlockStyle;
    }

    private readonly Stack<Element> _ancestors = new();
    private readonly Dictionary<string, List<CssRule>> _typeRules = new(StringComparer.Ordinal);
    private int _characters;
    private int _selectorWork;
    private bool _bodySeen;
    internal List<SourceCharacter?> Characters { get; } = new();
    internal List<(string Id, int Offset)> Anchors { get; } = new();
    internal SourceCharacter? LineBreakSource { get; private set; }
    internal Style DefaultStyle { get; } = new(fallbackSize, null);
    internal PdfRgbColor? CanvasBackground { get; private set; }

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
            "span" or "br" => parent?.Name is "p" or "span",
            _ => false };
        if (xml.NamespaceURI != "http://www.w3.org/1999/xhtml" || !allowed)
            throw new NotSupportedException("Native EPUB XHTML element is not implemented: " + name + ".");
        if (name == "head" && _bodySeen)
            throw new InvalidDataException("Native EPUB head metadata must precede the body.");
        if (name == "body") _bodySeen = true;
        bool empty = xml.IsEmptyElement;
        string? classNames = null, inlineStyle = null;
        while (xml.MoveToNextAttribute())
        {
            Count(xml.Value.Length);
            if (xml.NamespaceURI == "" && xml.LocalName == "style" && name is "body" or "p" or "span")
                inlineStyle = xml.Value;
            else if (xml.NamespaceURI == "" && xml.LocalName == "class")
                classNames = xml.Value;
            else if (xml.NamespaceURI == "" && xml.LocalName == "id" && name is "body" or "p" or "span" or "br")
            {
                // Navigation identity is captured by the structural text reader.
            }
            else if (name == "link" && xml.NamespaceURI == "" && xml.LocalName is "href" or "rel" or "type" or "id")
            {
                // Packaged references are validated separately from body painting.
            }
            else if (xml.NamespaceURI != "http://www.w3.org/2000/xmlns/"
                && !(xml.LocalName == "lang" && xml.NamespaceURI is "" or "http://www.w3.org/XML/1998/namespace"))
                throw new NotSupportedException("Native EPUB XHTML attributes and styling are not implemented.");
        }
        xml.MoveToElement();
        Style inherited = parent?.Style ?? DefaultStyle;
        if (inherited.Background is not null) inherited = inherited with { Background = null };
        Style style = ApplyRules(name, classNames, inherited);
        if (inlineStyle is not null) style = ParseStyle(inlineStyle, style);
        if (style.Background is { } background)
        {
            if (name != "body")
                throw new NotSupportedException("Native EPUB element background painting is not implemented.");
            CanvasBackground = background.Color;
        }
        var element = new Element(name, parent, style, name == "p" ? style : parent?.BlockStyle ?? style);
        if (name == "br") LineBreakSource = new(new Text("\n", element), 0);
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
        const string utf8Declaration = "@charset \"utf-8\";";
        int position = css.StartsWith(utf8Declaration, StringComparison.Ordinal) ? utf8Declaration.Length : 0;
        while (position < css.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            while (position < css.Length && char.IsWhiteSpace(css[position])) position++;
            if (position == css.Length) break;
            int open = css.IndexOf('{', position);
            int close = open < 0 ? -1 : css.IndexOf('}', open + 1);
            if (close < 0) throw new NotSupportedException("Native EPUB CSS rule syntax is not implemented.");
            CssProperties properties = ParseProperties(css[(open + 1)..close]);
            foreach (string group in css[position..open].Split(','))
            {
                string selector = group.Trim();
                string[] parts = selector.Split('.');
                if (selector.Length is 0 or > 128 || parts.Any(part => part.Length == 0
                    || !char.IsAsciiLetter(part[0])
                    || part.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_'))))
                    throw new NotSupportedException("Native EPUB CSS selector is not implemented.");
                if (!_typeRules.TryGetValue(parts[0], out var rules))
                    _typeRules.Add(parts[0], rules = new());
                rules.Add(new(parts[1..], properties));
            }
            position = close + 1;
        }
    }

    private sealed record CssRule(string[] Classes, CssProperties Properties);

    private Style ApplyRules(string name, string? classNames, Style inherited)
    {
        if (!_typeRules.TryGetValue(name, out var rules)) return inherited;
        string[] classes = classNames?.Split([' ', '\t', '\r', '\n', '\f'], StringSplitOptions.RemoveEmptyEntries) ?? [];
        double? size = null, height = null;
        PdfRgbColor? color = null;
        BackgroundColor? background = null;
        int sizeSpecificity = -1, heightSpecificity = -1, colorSpecificity = -1, backgroundSpecificity = -1;
        foreach (var rule in rules)
        {
            CountSelectorWork(1);
            bool matches = true;
            foreach (string required in rule.Classes)
            {
                bool found = false;
                foreach (string candidate in classes)
                {
                    CountSelectorWork(1 + Math.Min(candidate.Length, required.Length));
                    if (!string.Equals(candidate, required, StringComparison.Ordinal)) continue;
                    found = true;
                    break;
                }
                if (!found)
                {
                    matches = false;
                    break;
                }
            }
            if (!matches) continue;
            int specificity = rule.Classes.Length;
            if (rule.Properties.FontSize is { } fontSize && specificity >= sizeSpecificity)
            {
                size = fontSize;
                sizeSpecificity = specificity;
            }
            if (rule.Properties.LineHeight is { } lineHeight && specificity >= heightSpecificity)
            {
                height = lineHeight;
                heightSpecificity = specificity;
            }
            if (rule.Properties.Color is { } foreground && specificity >= colorSpecificity)
            {
                color = foreground;
                colorSpecificity = specificity;
            }
            if (rule.Properties.Background is { } fill && specificity >= backgroundSpecificity)
            {
                background = fill;
                backgroundSpecificity = specificity;
            }
        }
        return new CssProperties(size, height, color, background).Apply(inherited);
    }

    private void CountSelectorWork(int amount)
    {
        if (amount > 1_000_000 - _selectorWork)
            throw new InvalidDataException("Native EPUB CSS selector matching exceeds its work limit.");
        _selectorWork += amount;
    }

    private readonly record struct CssProperties(double? FontSize, double? LineHeight, PdfRgbColor? Color = null, BackgroundColor? Background = null)
    {
        internal Style Apply(Style inherited) =>
            new(FontSize ?? inherited.FontSize, LineHeight ?? inherited.LineHeight, Color ?? inherited.Color, Background ?? inherited.Background);
    }

    private Style ParseStyle(string declarations, Style inherited) => ParseProperties(declarations).Apply(inherited);

    private CssProperties ParseProperties(string declarations)
    {
        double? size = null, height = null;
        PdfRgbColor? color = null;
        BackgroundColor? background = null;
        foreach (string declaration in declarations.Split(';'))
        {
            string part = declaration.Trim();
            if (part.Length == 0) continue;
            int colon = part.IndexOf(':');
            if (colon < 0) throw new NotSupportedException("Native EPUB CSS declaration is unsupported.");
            string property = part[..colon].Trim().ToLowerInvariant();
            string value = part[(colon + 1)..].Trim();
            if (property == "color")
            {
                color = ParseColor(value);
                continue;
            }
            if (property == "background-color")
            {
                background = new BackgroundColor(string.Equals(value, "transparent", StringComparison.OrdinalIgnoreCase)
                    ? null : ParseColor(value));
                continue;
            }
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
        return new CssProperties(size, height, color, background);
    }

    private static PdfRgbColor ParseColor(string value)
    {
        int rgb = value.ToLowerInvariant() switch {
            "black" => 0x000000, "silver" => 0xc0c0c0, "gray" => 0x808080, "white" => 0xffffff,
            "maroon" => 0x800000, "red" => 0xff0000, "purple" => 0x800080, "fuchsia" => 0xff00ff,
            "green" => 0x008000, "lime" => 0x00ff00, "olive" => 0x808000, "yellow" => 0xffff00,
            "navy" => 0x000080, "blue" => 0x0000ff, "teal" => 0x008080, "aqua" => 0x00ffff,
            _ => -1 };
        if (rgb < 0 && !(value.Length == 7 && value[0] == '#'
            && int.TryParse(value.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out rgb)))
            throw new NotSupportedException("Native EPUB CSS color is not implemented.");
        return new((rgb >> 16) / 255d, ((rgb >> 8) & 255) / 255d, (rgb & 255) / 255d);
    }
}
