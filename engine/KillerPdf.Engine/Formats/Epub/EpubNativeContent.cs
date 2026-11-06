using System.Globalization;
using System.Xml;
using KillerPdf.Engine.Authoring;

namespace KillerPdf.Engine.Formats.Epub;

// Source nodes survive normalization so collapsed whitespace keeps its original inline style.
internal sealed class EpubNativeContent(double fallbackSize, double contentHeight)
{
    internal readonly record struct BackgroundColor(PdfRgbColor? Color);
    internal sealed record Style(double FontSize, double? LineHeight, PdfRgbColor? Color = null, BackgroundColor? Background = null)
    {
        internal double? LineHeightFactor { get; init; }
        internal EpubFontFamily[]? FontFamilies { get; init; }
        internal double FontWeight { get; init; } = 400;
        internal string TextAlign { get; init; } = "left";
        internal double TextIndent { get; init; }
        internal bool TextIndentPercentage { get; init; }
        internal bool PageBreakBefore { get; init; }
    }
    internal abstract class SourceNode;
    internal sealed class Element(string name, Element? parent, Style style, bool block, int offset) : SourceNode
    {
        internal string Name { get; } = name;
        internal Element? Parent { get; } = parent;
        internal Style Style { get; } = style;
        internal bool IsBlock { get; } = block;
        internal int Offset { get; } = offset;
        internal List<SourceNode> Children { get; } = new();
    }
    internal sealed class Text(string value, Element owner, int offset = 0) : SourceNode
    {
        internal string Value { get; } = value;
        internal Element Owner { get; } = owner;
        internal int Offset { get; } = offset;
        internal List<int> Positions { get; } = new();
    }
    internal sealed record InlineReference(int Offset, string? Source, Element Owner, bool Paragraph = false);
    internal sealed record SourceCharacter(Text Node, int Offset)
    {
        internal Style Style => Node.Owner.Style;
    }

    private readonly Stack<Element> _ancestors = new();
    private readonly Dictionary<string, List<CssRule>> _typeRules = new(StringComparer.Ordinal);
    private int _characters;
    private int _selectorWork;
    private int _selectorCount;
    private int _paragraphCount;
    private bool _bodySeen;
    private MarginLength?[]? _bodyMargins;
    private MarginLength?[]? _bodyPadding;
    private double _bodyFontSize = fallbackSize;
    internal List<SourceCharacter?> Characters { get; } = new();
    internal List<(string Id, int Offset)> Anchors { get; } = new();
    internal List<InlineReference> Inlines { get; } = new();
    internal Element? ImageElement { get; private set; }
    internal SourceCharacter? LineBreakSource { get; private set; }
    internal Style DefaultStyle { get; } = new(fallbackSize, null);
    internal PdfRgbColor? CanvasBackground { get; private set; }
    internal Element? Body { get; private set; }

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
            "span" or "br" or "img" => parent?.Name is "p" or "span",
            _ => false };
        if (xml.NamespaceURI != "http://www.w3.org/1999/xhtml" || !allowed)
            throw new NotSupportedException("Native EPUB XHTML element is not implemented: " + name + ".");
        if (name == "head" && _bodySeen)
            throw new InvalidDataException("Native EPUB head metadata must precede the body.");
        if (name == "body") _bodySeen = true;
        bool empty = xml.IsEmptyElement;
        string? classNames = null, inlineStyle = null;
        var attributes = new Dictionary<(string Namespace, string Name), string>();
        while (xml.MoveToNextAttribute())
        {
            Count(xml.Value.Length);
            attributes[(xml.NamespaceURI, xml.LocalName)] = xml.Value;
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
            else if (name == "img" && xml.NamespaceURI == "" && xml.LocalName is "src" or "alt")
            {
                // Image resources are resolved within the captured package.
            }
            else if (name is "p" or "span" && xml.LocalName == "type")
            {
                // Semantic type metadata is inert; selectors compare its expanded name.
            }
            else if (xml.NamespaceURI != "http://www.w3.org/2000/xmlns/"
                && !(xml.LocalName == "lang" && xml.NamespaceURI is "" or "http://www.w3.org/XML/1998/namespace"))
                throw new NotSupportedException("Native EPUB XHTML attributes and styling are not implemented.");
        }
        xml.MoveToElement();
        Style inherited = parent?.Style ?? DefaultStyle;
        if (inherited.Background is not null) inherited = inherited with { Background = null };
        CssProperties properties = ResolveRules(name, xml.NamespaceURI, classNames, attributes);
        if (inlineStyle is not null) properties = properties.Override(ParseProperties(inlineStyle));
        Style style = properties.Apply(inherited, contentHeight);
        if (properties.Border is { Visible: true, Nonzero: true })
            throw new NotSupportedException("Native EPUB visible borders are not implemented.");
        if (name == "body")
        {
            _bodyMargins = properties.Margins;
            _bodyPadding = properties.Padding;
            _bodyFontSize = style.FontSize;
        }
        else if (properties.Margins?.Any(side => side is { Value: > 0 }) == true)
            throw new NotSupportedException("Native EPUB nonzero margins are implemented only on the body.");
        if (name != "body" && properties.Padding?.Any(side => side is { Value: > 0 }) == true)
            throw new NotSupportedException("Native EPUB nonzero padding is implemented only on the body.");
        if (style.Background is { } background)
        {
            if (name is not ("body" or "p" or "span"))
                throw new NotSupportedException("Native EPUB element background painting is not implemented.");
            if (name == "body") CanvasBackground = background.Color;
        }
        var element = new Element(name, parent, style, properties.Block ?? name is "body" or "p", Characters.Count);
        parent?.Children.Add(element);
        if (name == "body") Body = element;
        if (name == "p")
        {
            if (++_paragraphCount > 100_000)
                throw new InvalidDataException("Native EPUB paragraphs exceed their 100,000-event limit.");
            // Share the inline event stream so empty blocks and image-only blocks keep document order.
            Inlines.Add(new(Characters.Count, null, element, Paragraph: true));
        }
        if (name == "img") ImageElement = element;
        if (name == "br") LineBreakSource = new(new Text("\n", element), 0);
        if (!empty) _ancestors.Push(element);
    }

    internal void Exit() => _ancestors.Pop();

    internal (double Top, double Right, double Bottom, double Left) ResolveBodyMargins(double width)
    {
        double Side(int index)
        {
            if (_bodyMargins?[index] is not { } length) return 0;
            double pixels = length.Percentage ? length.Value * width / 100
                : length.Em ? length.Value * _bodyFontSize : length.Value;
            if (!double.IsFinite(pixels) || pixels < 0 || pixels > 32768)
                throw new NotSupportedException("Native EPUB body margins exceed the supported page dimensions.");
            return pixels;
        }
        var margins = (Top: Side(0), Right: Side(1), Bottom: Side(2), Left: Side(3));
        if (margins.Left + margins.Right >= width || margins.Top >= contentHeight || margins.Bottom > contentHeight)
            throw new NotSupportedException("Native EPUB body margins require a positive content box within the page.");
        return margins;
    }

    internal Text TextNode(string value)
    {
        var owner = _ancestors.Peek();
        var node = new Text(value, owner, Characters.Count);
        owner.Children.Add(node);
        return node;
    }

    internal (double Top, double Right, double Bottom, double Left) ResolveBodyPadding(double width)
    {
        double Side(int index)
        {
            if (_bodyPadding?[index] is not { } length) return 0;
            double pixels = length.Percentage ? length.Value * width / 100
                : length.Em ? length.Value * _bodyFontSize : length.Value;
            if (!double.IsFinite(pixels) || pixels < 0 || pixels > 32768)
                throw new NotSupportedException("Native EPUB body padding exceeds the supported page dimensions.");
            return pixels;
        }
        return (Side(0), Side(1), Side(2), Side(3));
    }

    internal void Append(SourceCharacter? source)
    {
        if (Characters.Count >= 100_000)
            throw new InvalidDataException("Native EPUB text exceeds its 100,000 placement limit.");
        source?.Node.Positions.Add(Characters.Count);
        Characters.Add(source);
    }

    internal void Trim(int length)
    {
        if (Characters.Count > length) Characters.RemoveRange(length, Characters.Count - length);
        for (int i = 0; i < Inlines.Count; i++)
            if (Inlines[i].Offset > length) Inlines[i] = Inlines[i] with { Offset = length };
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
        var namespaces = new Dictionary<string, string>(StringComparer.Ordinal);
        int namespaceCount = 0;
        bool styleSeen = false;
        while (position < css.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            while (position < css.Length && char.IsWhiteSpace(css[position])) position++;
            if (position == css.Length) break;
            if (css.AsSpan(position).StartsWith("@namespace", StringComparison.OrdinalIgnoreCase))
            {
                if (styleSeen) throw NamespaceSyntax();
                if (++namespaceCount > 256)
                    throw new InvalidDataException("Native EPUB CSS namespace declarations exceed their 256-declaration limit.");
                position += 10;
                if (position == css.Length || !IsCssSpace(css[position])) throw NamespaceSyntax();
                SkipSpace();
                string prefix = "";
                if (position < css.Length && css[position] is not ('\'' or '"')
                    && !css.AsSpan(position).StartsWith("url(", StringComparison.OrdinalIgnoreCase))
                {
                    int start = position;
                    while (position < css.Length && !IsCssSpace(css[position])) position++;
                    prefix = css[start..position];
                    if (!IsIdentifier(prefix)) throw NamespaceSyntax();
                    SkipSpace();
                }
                bool url = css.AsSpan(position).StartsWith("url(", StringComparison.OrdinalIgnoreCase);
                if (url) { position += 4; SkipSpace(); }
                if (position == css.Length) throw NamespaceSyntax();
                char quote = css[position] is '\'' or '"' ? css[position++] : '\0';
                if (quote == '\0' && !url) throw NamespaceSyntax();
                int uriStart = position;
                while (position < css.Length && (quote != '\0' ? css[position] != quote : css[position] != ')' && !IsCssSpace(css[position])))
                {
                    if (char.IsControl(css[position]) || css[position] == '\\'
                        || quote == '\0' && css[position] is '"' or '\'' or '(') throw NamespaceSyntax();
                    if (position - uriStart >= 2048)
                        throw new InvalidDataException("Native EPUB CSS namespace name exceeds its 2,048-character limit.");
                    position++;
                }
                string uri = css[uriStart..position];
                if (quote != '\0')
                {
                    if (position == css.Length || css[position++] != quote) throw NamespaceSyntax();
                }
                SkipSpace();
                if (url)
                {
                    if (position == css.Length || css[position++] != ')') throw NamespaceSyntax();
                    SkipSpace();
                }
                if (position == css.Length || css[position++] != ';') throw NamespaceSyntax();
                namespaces[prefix] = uri;
                continue;
            }
            styleSeen = true;
            int open = css.IndexOf('{', position);
            int close = open < 0 ? -1 : css.IndexOf('}', open + 1);
            if (close < 0) throw new NotSupportedException("Native EPUB CSS rule syntax is not implemented.");
            CssProperties properties = ParseProperties(css[(open + 1)..close]);
            foreach (string group in css[position..open].Split(','))
            {
                if (++_selectorCount > 4096)
                    throw new InvalidDataException("Native EPUB CSS selectors exceed their 4,096-selector limit.");
                string selector = group.Trim();
                if (selector.Length is 0 or > 128)
                    throw new NotSupportedException("Native EPUB CSS selector is not implemented.");
                CssAttribute? attribute = null;
                int bracket = selector.IndexOf('[');
                if (bracket >= 0)
                {
                    if (!selector.EndsWith(']') || selector.IndexOf('[', bracket + 1) >= 0)
                        throw new NotSupportedException("Native EPUB CSS selector is not implemented.");
                    string condition = selector[(bracket + 1)..^1].Trim();
                    int operation = condition.IndexOf("~=", StringComparison.Ordinal);
                    string attributeName = operation < 0 ? condition : condition[..operation].Trim();
                    string? token = null;
                    if (operation >= 0)
                    {
                        token = condition[(operation + 2)..].Trim();
                        if (token.Length >= 2 && token[0] is '\'' or '"' && token[^1] == token[0])
                            token = token[1..^1];
                        else if (!IsIdentifier(token))
                            throw new NotSupportedException("Native EPUB CSS selector is not implemented.");
                        if (token.Any(character => character is '\\' or '\'' or '"' or '[' or ']'))
                            throw new NotSupportedException("Native EPUB CSS selector is not implemented.");
                    }
                    string attributeNamespace = "";
                    int attributeSeparator = attributeName.IndexOf('|');
                    if (attributeSeparator >= 0)
                    {
                        string prefix = attributeName[..attributeSeparator];
                        if (!IsIdentifier(prefix))
                            throw new NotSupportedException("Native EPUB CSS selector is not implemented.");
                        if (!namespaces.TryGetValue(prefix, out attributeNamespace!))
                            throw new NotSupportedException("Native EPUB CSS selector namespace prefix is not declared: " + prefix + ".");
                        attributeName = attributeName[(attributeSeparator + 1)..];
                    }
                    if (!IsIdentifier(attributeName))
                        throw new NotSupportedException("Native EPUB CSS selector is not implemented.");
                    attribute = new(attributeNamespace, attributeName, token);
                    selector = selector[..bracket];
                }
                string[] parts = selector.Split('.');
                string type = parts[0];
                namespaces.TryGetValue("", out string? namespaceName);
                int separator = type.IndexOf('|');
                if (separator >= 0)
                {
                    string prefix = type[..separator];
                    if (prefix == "*") namespaceName = null;
                    else if (prefix.Length == 0) namespaceName = "";
                    else
                    {
                        if (!IsIdentifier(prefix)) throw new NotSupportedException("Native EPUB CSS selector is not implemented.");
                        if (!namespaces.TryGetValue(prefix, out namespaceName))
                            throw new NotSupportedException("Native EPUB CSS selector namespace prefix is not declared: " + prefix + ".");
                    }
                    type = type[(separator + 1)..];
                    parts[0] = type;
                }
                bool classOnly = selector.StartsWith(".", StringComparison.Ordinal);
                if (classOnly && attribute is not null || selector.Length is 0 or > 128 || (classOnly ? parts.Skip(1) : parts).Any(part => part.Length == 0
                    || !char.IsAsciiLetter(part[0])
                    || part.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_'))))
                    throw new NotSupportedException("Native EPUB CSS selector is not implemented.");
                if (!_typeRules.TryGetValue(type, out var rules))
                    _typeRules.Add(type, rules = new());
                rules.Add(new(namespaceName, parts[1..], attribute, properties, _selectorCount, classOnly ? 0 : 1));
            }
            position = close + 1;
        }
        void SkipSpace() { while (position < css.Length && IsCssSpace(css[position])) position++; }
    }

    private static bool IsCssSpace(char value) => value is ' ' or '\t' or '\r' or '\n' or '\f';
    private static bool IsIdentifier(string value) => value.Length is > 0 and <= 128
        && (char.IsAsciiLetter(value[0]) || value[0] == '_')
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
    private static NotSupportedException NamespaceSyntax() =>
        new("Native EPUB CSS namespace syntax is not implemented.");

    private sealed record CssAttribute(string NamespaceName, string Name, string? Token);
    private sealed record CssRule(string? NamespaceName, string[] Classes, CssAttribute? Attribute, CssProperties Properties, int Order, int TypeSpecificity);

    private CssProperties ResolveRules(string name, string namespaceName, string? classNames,
        Dictionary<(string Namespace, string Name), string> attributes)
    {
        _typeRules.TryGetValue(name, out var typeRules);
        _typeRules.TryGetValue("", out var classRules);
        if (typeRules is null && classRules is null) return default;
        string[] classes = classNames?.Split([' ', '\t', '\r', '\n', '\f'], StringSplitOptions.RemoveEmptyEntries) ?? [];
        CssLength? size = null, height = null;
        PdfRgbColor? color = null;
        BackgroundColor? background = null;
        int sizeSpecificity = -1, heightSpecificity = -1, colorSpecificity = -1, backgroundSpecificity = -1;
        EpubFontFamily[]? families = null;
        int familySpecificity = -1;
        double? fontWeight = null;
        int weightSpecificity = -1;
        Border? border = null;
        int borderSpecificity = -1;
        string? textAlign = null;
        int alignmentSpecificity = -1;
        IndentLength? textIndent = null;
        int indentSpecificity = -1;
        bool? pageBreakBefore = null;
        int breakSpecificity = -1;
        bool? block = null;
        int displaySpecificity = -1;
        var padding = new MarginLength?[4];
        int[] paddingSpecificities = [-1, -1, -1, -1];
        var margins = new MarginLength?[4];
        int[] marginSpecificities = [-1, -1, -1, -1];
        int typeIndex = 0, classIndex = 0;
        while (typeIndex < (typeRules?.Count ?? 0) || classIndex < (classRules?.Count ?? 0))
        {
            var rule = classIndex >= (classRules?.Count ?? 0)
                || typeIndex < (typeRules?.Count ?? 0) && typeRules![typeIndex].Order < classRules![classIndex].Order
                ? typeRules![typeIndex++] : classRules![classIndex++];
            CountSelectorWork(1);
            if (rule.NamespaceName is { } requiredNamespace)
            {
                CountSelectorWork(1 + Math.Min(requiredNamespace.Length, namespaceName.Length));
                if (!string.Equals(requiredNamespace, namespaceName, StringComparison.Ordinal)) continue;
            }
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
            if (rule.Attribute is { } attribute)
            {
                CountSelectorWork(1 + attribute.NamespaceName.Length + attribute.Name.Length);
                if (!attributes.TryGetValue((attribute.NamespaceName, attribute.Name), out string? value)) continue;
                if (attribute.Token is { } token)
                {
                    CountSelectorWork(value.Length + token.Length);
                    if (token.Length == 0 || token.Any(IsCssSpace)
                    || !value.Split([' ', '\t', '\r', '\n', '\f'], StringSplitOptions.RemoveEmptyEntries)
                        .Contains(token, StringComparer.Ordinal)) continue;
                }
            }
            int specificity = (rule.Classes.Length + (rule.Attribute is null ? 0 : 1)) * 2 + rule.TypeSpecificity;
            if (rule.Properties.TextIndent is { } indent && specificity >= indentSpecificity)
            {
                textIndent = indent;
                indentSpecificity = specificity;
            }
            if (rule.Properties.FontWeight is { } weight && specificity >= weightSpecificity)
            {
                fontWeight = weight;
                weightSpecificity = specificity;
            }
            if (rule.Properties.Block is { } display && specificity >= displaySpecificity)
            {
                block = display;
                displaySpecificity = specificity;
            }
            if (rule.Properties.PageBreakBefore is { } forcedBreak && specificity >= breakSpecificity)
            {
                pageBreakBefore = forcedBreak;
                breakSpecificity = specificity;
            }
            if (rule.Properties.TextAlign is { } alignment && specificity >= alignmentSpecificity)
            {
                textAlign = alignment;
                alignmentSpecificity = specificity;
            }
            if (rule.Properties.Border is { } selectedBorder && specificity >= borderSpecificity)
            {
                border = selectedBorder;
                borderSpecificity = specificity;
            }
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
            if (rule.Properties.FontFamilies is { } fontFamilies && specificity >= familySpecificity)
            {
                families = fontFamilies;
                familySpecificity = specificity;
            }
            if (rule.Properties.Margins is { } ruleMargins)
                for (int side = 0; side < 4; side++)
                    if (ruleMargins[side] is { } margin && specificity >= marginSpecificities[side])
                    {
                        margins[side] = margin;
                        marginSpecificities[side] = specificity;
                    }
            if (rule.Properties.Padding is { } rulePadding)
                for (int side = 0; side < 4; side++)
                    if (rulePadding[side] is { } inset && specificity >= paddingSpecificities[side])
                    {
                        padding[side] = inset;
                        paddingSpecificities[side] = specificity;
                    }
        }
        return new CssProperties(size, height, color, background, families, margins, border, padding, textAlign, pageBreakBefore, block, fontWeight, textIndent);
    }

    private void CountSelectorWork(int amount)
    {
        if (amount > 1_000_000 - _selectorWork)
            throw new InvalidDataException("Native EPUB CSS selector matching exceeds its work limit.");
        _selectorWork += amount;
    }

    private readonly record struct CssLength(double Value, bool Relative, bool Unitless = false)
    {
        internal double Compute(double reference, double maximum)
        {
            double pixels = Relative ? Value * reference : Value;
            if (!double.IsFinite(pixels) || pixels <= 0 || pixels > maximum)
                throw new NotSupportedException("Native EPUB typography requires positive CSS lengths that fit the page.");
            return pixels;
        }
    }

    private readonly record struct MarginLength(double Value, bool Em, bool Percentage = false);
    private readonly record struct IndentLength(double Value, bool Em = false, bool Percentage = false, bool Inherit = false);
    private readonly record struct Border(bool Nonzero, bool Visible);

    private readonly record struct CssProperties(CssLength? FontSize, CssLength? LineHeight, PdfRgbColor? Color = null,
        BackgroundColor? Background = null, EpubFontFamily[]? FontFamilies = null, MarginLength?[]? Margins = null,
        Border? Border = null, MarginLength?[]? Padding = null, string? TextAlign = null, bool? PageBreakBefore = null,
        bool? Block = null, double? FontWeight = null, IndentLength? TextIndent = null)
    {
        internal CssProperties Override(CssProperties inline) =>
            new(inline.FontSize ?? FontSize, inline.LineHeight ?? LineHeight,
                inline.Color ?? Color, inline.Background ?? Background, inline.FontFamilies ?? FontFamilies,
                MergeMargins(Margins, inline.Margins), inline.Border ?? Border, MergeMargins(Padding, inline.Padding),
                inline.TextAlign ?? TextAlign, inline.PageBreakBefore ?? PageBreakBefore, inline.Block ?? Block,
                inline.FontWeight ?? FontWeight, inline.TextIndent ?? TextIndent);

        private static MarginLength?[]? MergeMargins(MarginLength?[]? earlier, MarginLength?[]? later) =>
            later is null ? earlier : Enumerable.Range(0, 4).Select(side => later[side] ?? earlier?[side]).ToArray();

        internal Style Apply(Style inherited, double maximum)
        {
            double size = FontSize?.Compute(inherited.FontSize, maximum) ?? inherited.FontSize;
            double indent = TextIndent is { Inherit: false } length ? (length.Em ? length.Value * size : length.Value) : inherited.TextIndent;
            bool indentPercentage = TextIndent is { Inherit: false } specified ? specified.Percentage : inherited.TextIndentPercentage;
            if (!double.IsFinite(indent) || Math.Abs(indent) > 32768)
                throw new NotSupportedException("Native EPUB text indentation exceeds the supported page dimensions.");
            double? factor = LineHeight is { Unitless: true } height ? height.Value
                : LineHeight is null ? inherited.LineHeightFactor : null;
            double? pixels = LineHeight?.Compute(size, maximum)
                ?? (factor is double multiplier ? new CssLength(multiplier, true).Compute(size, maximum) : inherited.LineHeight);
            return new(size, pixels, Color ?? inherited.Color, Background ?? inherited.Background)
            {
                LineHeightFactor = factor, FontFamilies = FontFamilies ?? inherited.FontFamilies,
                FontWeight = FontWeight ?? inherited.FontWeight,
                TextAlign = TextAlign is null or "inherit" ? inherited.TextAlign : TextAlign,
                TextIndent = indent,
                TextIndentPercentage = indentPercentage,
                PageBreakBefore = PageBreakBefore ?? false
            };
        }
    }

    private CssProperties ParseProperties(string declarations)
    {
        IndentLength? textIndent = null;
        CssLength? size = null, height = null;
        PdfRgbColor? color = null;
        BackgroundColor? background = null;
        EpubFontFamily[]? families = null;
        MarginLength?[]? margins = null;
        MarginLength?[]? padding = null;
        Border? border = null;
        string? textAlign = null;
        bool? pageBreakBefore = null;
        bool? block = null;
        double? fontWeight = null;
        foreach (string declaration in declarations.Split(';'))
        {
            string part = declaration.Trim();
            if (part.Length == 0) continue;
            int colon = part.IndexOf(':');
            if (colon < 0) throw new NotSupportedException("Native EPUB CSS declaration is unsupported.");
            string property = part[..colon].Trim().ToLowerInvariant();
            string value = part[(colon + 1)..].Trim();
            if (property == "text-indent")
            {
                if (value.Equals("inherit", StringComparison.OrdinalIgnoreCase) || value.Equals("unset", StringComparison.OrdinalIgnoreCase))
                {
                    textIndent = new IndentLength(0, Inherit: true);
                    continue;
                }
                if (value.Equals("initial", StringComparison.OrdinalIgnoreCase))
                {
                    textIndent = new IndentLength(0);
                    continue;
                }
                bool em = value.EndsWith("em", StringComparison.OrdinalIgnoreCase);
                bool px = value.EndsWith("px", StringComparison.OrdinalIgnoreCase);
                bool percentage = value.EndsWith('%');
                string number = em || px ? value[..^2] : percentage ? value[..^1] : value;
                if (!double.TryParse(number, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                        CultureInfo.InvariantCulture, out double indent) || !double.IsFinite(indent)
                    || Math.Abs(indent) > 32768 || number.EndsWith('.') || !(em || px || percentage) && indent != 0)
                    throw new NotSupportedException("Native EPUB text indentation requires supported CSS lengths.");
                textIndent = new IndentLength(indent, em, percentage);
                continue;
            }
            if (property == "font-weight")
            {
                if (value.Equals("normal", StringComparison.OrdinalIgnoreCase)) fontWeight = 400;
                else if (value.Equals("bold", StringComparison.OrdinalIgnoreCase)) fontWeight = 700;
                else if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double weight)
                    && double.IsFinite(weight) && weight is >= 1 and <= 1000) fontWeight = weight;
                else throw new NotSupportedException("Native EPUB font weight is not implemented: " + value + ".");
                continue;
            }
            if (property == "display")
            {
                block = value.ToLowerInvariant() switch
                {
                    "block" => true,
                    "inline" => false,
                    _ => throw new NotSupportedException("Native EPUB display value is not implemented: " + value + ".")
                };
                continue;
            }
            if (property == "page-break-before")
            {
                pageBreakBefore = value.ToLowerInvariant() switch
                {
                    "always" => true,
                    "auto" => false,
                    _ => throw new NotSupportedException("Native EPUB page-break-before value is not implemented: " + value + ".")
                };
                continue;
            }
            if (property == "text-align")
            {
                textAlign = value.ToLowerInvariant() switch
                {
                    "left" => "left", "center" => "center", "right" => "right", "justify" => "justify",
                    "inherit" => "inherit",
                    _ => throw new NotSupportedException("Native EPUB text alignment is not implemented: " + value + ".")
                };
                continue;
            }
            if (property == "border")
            {
                border = ParseBorder(value);
                continue;
            }
            if (property is "padding" or "padding-top" or "padding-right" or "padding-bottom" or "padding-left")
            {
                string[] values = value.Split([' ', '\t', '\r', '\n', '\f'], StringSplitOptions.RemoveEmptyEntries);
                if (values.Length == 0 || values.Length > (property == "padding" ? 4 : 1))
                    throw new NotSupportedException("Native EPUB padding syntax is not implemented.");
                MarginLength[] lengths;
                try { lengths = values.Select(ParseMarginLength).ToArray(); }
                catch (NotSupportedException exception)
                {
                    throw new NotSupportedException("Native EPUB padding requires supported nonnegative CSS lengths.", exception);
                }
                padding ??= new MarginLength?[4];
                if (property == "padding")
                {
                    padding[0] = lengths[0];
                    padding[1] = lengths.Length > 1 ? lengths[1] : lengths[0];
                    padding[2] = lengths.Length > 2 ? lengths[2] : lengths[0];
                    padding[3] = lengths.Length > 3 ? lengths[3] : padding[1];
                }
                else padding[property switch { "padding-top" => 0, "padding-right" => 1, "padding-bottom" => 2, _ => 3 }] = lengths[0];
                continue;
            }
            if (property is "margin" or "margin-top" or "margin-right" or "margin-bottom" or "margin-left")
            {
                string[] sides = value.Split([' ', '\t', '\r', '\n', '\f'], StringSplitOptions.RemoveEmptyEntries);
                if (sides.Length == 0 || sides.Length > (property == "margin" ? 4 : 1))
                    throw new NotSupportedException("Native EPUB body margin syntax is not implemented.");
                var lengths = sides.Select(ParseMarginLength).ToArray();
                margins ??= new MarginLength?[4];
                if (property == "margin")
                {
                    margins[0] = lengths[0];
                    margins[1] = lengths.Length > 1 ? lengths[1] : lengths[0];
                    margins[2] = lengths.Length > 2 ? lengths[2] : lengths[0];
                    margins[3] = lengths.Length > 3 ? lengths[3] : margins[1];
                }
                else margins[property switch { "margin-top" => 0, "margin-right" => 1, "margin-bottom" => 2, _ => 3 }] = lengths[0];
                continue;
            }
            if (property == "font-family")
            {
                families = ParseFontFamilies(value);
                continue;
            }
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
            if (property == "font-size")
            {
                if (value.Equals("larger", StringComparison.OrdinalIgnoreCase)
                    || value.Equals("smaller", StringComparison.OrdinalIgnoreCase))
                {
                    size = new CssLength(value.Equals("larger", StringComparison.OrdinalIgnoreCase) ? 1.2 : 1 / 1.2, true);
                    continue;
                }
                double? keywordSize = value.ToLowerInvariant() switch
                {
                    "xx-small" => DefaultStyle.FontSize * 3 / 5,
                    "x-small" => DefaultStyle.FontSize * 3 / 4,
                    "small" => DefaultStyle.FontSize * 8 / 9,
                    "medium" => DefaultStyle.FontSize,
                    "large" => DefaultStyle.FontSize * 6 / 5,
                    "x-large" => DefaultStyle.FontSize * 3 / 2,
                    "xx-large" => DefaultStyle.FontSize * 2,
                    "xxx-large" => DefaultStyle.FontSize * 3,
                    _ => null
                };
                if (keywordSize is double absoluteSize)
                {
                    size = new CssLength(absoluteSize, false);
                    continue;
                }
            }
            if (property is "font-size" or "line-height"
                && (value.EndsWith('%') || value.EndsWith("em", StringComparison.OrdinalIgnoreCase)))
            {
                bool percentage = value.EndsWith('%');
                if (!double.TryParse(value[..(value.Length - (percentage ? 1 : 2))], NumberStyles.AllowDecimalPoint,
                        CultureInfo.InvariantCulture, out double relative)
                    || !double.IsFinite(relative) || relative <= 0)
                    throw new NotSupportedException("Native EPUB typography requires positive CSS lengths that fit the page.");
                var length = new CssLength(percentage ? relative / 100 : relative, true);
                if (property == "font-size") size = length;
                else height = length;
                continue;
            }
            if (property == "line-height" && double.TryParse(value, NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture, out double factor) && double.IsFinite(factor) && factor > 0)
            {
                height = new CssLength(factor, true, Unitless: true);
                continue;
            }
            if (!value.EndsWith("px", StringComparison.OrdinalIgnoreCase)
                || !double.TryParse(value[..^2], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double pixels)
                || !double.IsFinite(pixels) || pixels <= 0 || pixels > contentHeight)
                throw new NotSupportedException("Native EPUB typography requires positive CSS pixel lengths that fit the page.");
            switch (property)
            {
                case "font-size": size = new CssLength(pixels, false); break;
                case "line-height": height = new CssLength(pixels, false); break;
                default: throw new NotSupportedException("Native EPUB CSS property is not implemented: " + property + ".");
            }
        }
        return new CssProperties(size, height, color, background, families, margins, border, padding, textAlign, pageBreakBefore, block, fontWeight, textIndent);
    }

    private static Border ParseBorder(string value)
    {
        bool? nonzero = null, visible = null;
        bool colorSeen = false;
        var tokens = System.Text.RegularExpressions.Regex.Matches(value, @"rgb\([^)]*\)|[^\s]+",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (tokens.Count == 0) throw new NotSupportedException("Native EPUB border syntax is not implemented.");
        foreach (System.Text.RegularExpressions.Match token in tokens)
        {
            string component = token.Value.ToLowerInvariant();
            if (component is "none" or "hidden" or "solid" or "dotted" or "dashed" or "double" or "groove" or "ridge" or "inset" or "outset")
            {
                if (visible is not null) throw new NotSupportedException("Native EPUB border syntax is not implemented.");
                visible = component is not ("none" or "hidden");
            }
            else if (component is "thin" or "medium" or "thick" || char.IsAsciiDigit(component[0]) || component[0] is '+' or '-' or '.')
            {
                if (nonzero is not null) throw new NotSupportedException("Native EPUB border syntax is not implemented.");
                if (component is "thin" or "medium" or "thick") nonzero = true;
                else
                {
                    var length = ParseMarginLength(component);
                    if (length.Percentage) throw new NotSupportedException("Native EPUB border percentages are invalid.");
                    nonzero = length.Value != 0;
                }
            }
            else
            {
                if (colorSeen) throw new NotSupportedException("Native EPUB border syntax is not implemented.");
                if (component != "transparent") _ = ParseColor(component);
                colorSeen = true;
            }
        }
        return new(nonzero ?? true, visible ?? false);
    }

    private static MarginLength ParseMarginLength(string value)
    {
        bool em = value.EndsWith("em", StringComparison.OrdinalIgnoreCase);
        bool px = value.EndsWith("px", StringComparison.OrdinalIgnoreCase);
        bool percentage = value.EndsWith('%');
        string number = em || px ? value[..^2] : percentage ? value[..^1] : value;
        if (!double.TryParse(number, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out double length) || !double.IsFinite(length) || length < 0
            || number.EndsWith('.') || !(em || px || percentage) && length != 0)
            throw new NotSupportedException("Native EPUB body margins require supported nonnegative CSS lengths; auto and negative margins are not implemented.");
        return new(length, em, percentage);
    }

    private static EpubFontFamily[] ParseFontFamilies(string value)
    {
        var result = new List<EpubFontFamily>();
        int at = 0;
        while (true)
        {
            while (at < value.Length && IsSpace(value[at])) at++;
            if (at == value.Length) throw Syntax();
            if (result.Count >= 32)
                throw new InvalidDataException("Native EPUB font-family exceeds its 32-family limit.");
            bool quoted = value[at] is '\'' or '"';
            string name;
            if (quoted)
            {
                char quote = value[at++];
                int end = value.IndexOf(quote, at);
                if (end < 0) throw Syntax();
                name = value[at..end];
                if (name.Any(character => char.IsControl(character) || character == '\\')) throw Syntax();
                at = end + 1;
                while (at < value.Length && IsSpace(value[at])) at++;
                if (at < value.Length && value[at] != ',') throw Syntax();
            }
            else
            {
                int end = value.IndexOf(',', at);
                if (end < 0) end = value.Length;
                string[] words = value[at..end].Split([' ', '\t', '\r', '\n', '\f'], StringSplitOptions.RemoveEmptyEntries);
                if (words.Length == 0 || words.Any(word => !char.IsLetter(word[0]) && word[0] != '_'
                    || word.Any(character => !char.IsLetterOrDigit(character) && character is not ('-' or '_'))))
                    throw Syntax();
                name = string.Join(' ', words);
                if (name.ToLowerInvariant() is "inherit" or "initial" or "unset" or "revert" or "revert-layer") throw Syntax();
                at = end;
            }
            if (name.Length is 0 or > 256) throw Syntax();
            bool generic = !quoted && name.ToLowerInvariant() is "serif" or "sans-serif" or "monospace" or "cursive" or "fantasy";
            result.Add(new(name, generic));
            if (at == value.Length) return result.ToArray();
            at++;
        }
        static bool IsSpace(char character) => character is ' ' or '\t' or '\r' or '\n' or '\f';
        static NotSupportedException Syntax() => new("Native EPUB font-family syntax is not implemented.");
    }

    private static PdfRgbColor ParseColor(string value)
    {
        if (value.StartsWith("rgb(", StringComparison.OrdinalIgnoreCase) && value.EndsWith(')'))
        {
            string[] components = value[4..^1].Split(',');
            if (components.Length == 3)
            {
                bool percentage = components[0].Trim(' ', '\t', '\r', '\n', '\f').EndsWith('%');
                double[] channels = new double[3];
                for (int index = 0; index < channels.Length; index++)
                {
                    string number = components[index].Trim(' ', '\t', '\r', '\n', '\f');
                    if (number.EndsWith('%') != percentage)
                        throw new NotSupportedException("Native EPUB CSS color is not implemented.");
                    if (percentage) number = number[..^1];
                    int firstDigit = number.Length > 0 && number[0] is '+' or '-' ? 1 : 0;
                    int decimalPoint = percentage ? number.IndexOf('.', firstDigit) : -1;
                    bool validDigits = decimalPoint < 0
                        ? number.Length > firstDigit && !number.AsSpan(firstDigit).ContainsAnyExceptInRange('0', '9')
                        : decimalPoint < number.Length - 1
                            && !number.AsSpan(firstDigit, decimalPoint - firstDigit).ContainsAnyExceptInRange('0', '9')
                            && !number.AsSpan(decimalPoint + 1).ContainsAnyExceptInRange('0', '9');
                    if (!validDigits || !double.TryParse(number, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                        CultureInfo.InvariantCulture, out double channel))
                        throw new NotSupportedException("Native EPUB CSS color is not implemented.");
                    channels[index] = Math.Clamp(channel / (percentage ? 100d : 255d), 0, 1);
                }
                return new(channels[0], channels[1], channels[2]);
            }
        }
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
