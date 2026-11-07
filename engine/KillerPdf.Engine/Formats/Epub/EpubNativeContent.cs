using System.Globalization;
using System.Xml;
using System.Text;
using KillerPdf.Engine.Authoring;

namespace KillerPdf.Engine.Formats.Epub;

// Source nodes survive normalization so collapsed whitespace keeps its original inline style.
internal sealed class EpubNativeContent(double fallbackSize, double contentHeight, PdfRgbColor? fallbackTextColor = null)
{
    internal enum DisplayKind { Block, Inline, None, Table, TableRowGroup, TableRow, TableCell }
    internal readonly record struct BackgroundColor(PdfRgbColor? Color);
    internal sealed record Style(double FontSize, double? LineHeight, PdfRgbColor? Color = null, BackgroundColor? Background = null)
    {
        internal double? LineHeightFactor { get; init; }
        internal EpubFontFamily[]? FontFamilies { get; init; }
        internal double FontWeight { get; init; } = 400;
        internal EpubFontStyle FontStyle { get; init; } = EpubFontStyle.Normal;
        internal bool SmallCaps { get; init; }
        internal string TextAlign { get; init; } = "left";
        internal double TextIndent { get; init; }
        internal double LetterSpacing { get; init; }
        internal double WordSpacing { get; init; }
        internal bool TextIndentPercentage { get; init; }
        internal bool PageBreakBefore { get; init; }
        internal bool PageBreakAfter { get; init; }
        internal bool Hidden { get; init; }
        internal bool Underline { get; init; }
    }
    internal abstract class SourceNode;
    internal sealed record SelectorIdentity(string NamespaceName, string[] Classes,
        Dictionary<(string Namespace, string Name), string> Attributes);
    internal sealed class Element(string name, Element? parent, Style style, bool block, int offset, SelectorIdentity identity) : SourceNode
    {
        internal SelectorIdentity Identity { get; } = identity;
        internal string? LinkReference { get; } = name == "a" && identity.Attributes.TryGetValue(("", "href"), out string? href)
            ? href : parent?.LinkReference;
        internal bool HasUnderline { get; } = style.Underline || parent?.HasUnderline == true;
        internal string Name { get; } = name;
        internal static bool IsParagraphName(string value) => value is "p" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or "hr";
        internal bool IsParagraph => IsParagraphName(Name);
        internal Element? Parent { get; } = parent;
        internal Element? TableCellOwner { get; } = parent?.Name == "td" ? parent : parent?.TableCellOwner;
        internal Style Style { get; } = style;
        internal DisplayKind Display { get; init; } = block ? DisplayKind.Block : DisplayKind.Inline;
        internal bool IsBlock => Display is DisplayKind.Block or DisplayKind.Table or DisplayKind.TableRowGroup or DisplayKind.TableRow or DisplayKind.TableCell;
        internal bool IsDisplayNone { get; init; }
        internal int Offset { get; } = offset;
        internal int EndOffset { get; set; } = offset;
        internal MarginLength?[]? Margins { get; init; }
        internal MarginLength?[]? Padding { get; init; }
        internal BorderSide? TopBorder { get; init; }
        internal BoxWidth? Width { get; init; }
        internal BoxWidth? MaximumWidth { get; init; }
        internal Border? Border { get; init; }
        internal double? HeightPercentage { get; init; }
        internal (double Width, double Height)? SvgViewBox { get; init; }
        internal List<SourceNode> Children { get; } = new();

        internal double VerticalMargin(int side, double width, double fallback, bool padding = false)
        {
            var sides = padding ? Padding : Margins;
            if (sides?[side] is not { } length) return fallback;
            double value = length.Percentage ? length.Value * width / 100
                : length.Em ? length.Value * Style.FontSize : length.Value;
            if (!double.IsFinite(value) || value < 0 || value > 32768)
                throw new NotSupportedException("Native EPUB paragraph margins exceed the supported dimensions.");
            return value;
        }

        internal (double Left, double Right) HorizontalInsets(double width, bool padding = false)
        {
            var sides = padding ? Padding : Margins;
            double Side(int index)
            {
                if (sides?[index] is not { } length) return 0;
                double value = length.Percentage ? length.Value * width / 100
                    : length.Em ? length.Value * Style.FontSize : length.Value;
                if (!double.IsFinite(value) || value < 0 || value > 32768)
                    throw new NotSupportedException("Native EPUB paragraph insets exceed the supported dimensions.");
                return value;
            }
            return (Side(3), Side(1));
        }
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

    private static readonly CssProperties HorizontalRuleDefaults = CssProperties.FromConcrete(new ResolvedProperties(null, null,
        Margins: [new(.5, true), new(0, false, Auto: true), new(.5, true), new(0, false, Auto: true)],
        TopBorder: new BorderSide(new MarginLength(1, false), true, null)));

    private static CssProperties? HeadingDefaults(string name)
    {
        (double Size, double Margin) values = name switch
        {
            "h1" => (2, .67),
            "h2" => (1.5, .83),
            "h3" => (1.17, 1),
            "h4" => (1, 1.33),
            "h5" => (.83, 1.67),
            "h6" => (.67, 2.33),
            _ => (0, 0)
        };
        return values.Size == 0 ? null : CssProperties.FromConcrete(new ResolvedProperties(new CssLength(values.Size, true), null,
            FontWeight: 700, Margins: [new(values.Margin, true), new(0, false), new(values.Margin, true), new(0, false)]));
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
    internal List<(string Id, int Offset, Element Owner)> Anchors { get; } = new();
    internal Element? CurrentElement { get; private set; }
    internal List<InlineReference> Inlines { get; } = new();
    internal Element? ImageElement { get; private set; }
    internal SourceCharacter? LineBreakSource { get; private set; }
    internal Style DefaultStyle { get; } = new(fallbackSize, null);
    internal PdfRgbColor? CanvasBackground { get; private set; }
    internal Element? Body { get; private set; }
    internal bool CurrentElementDisplayNone { get; private set; }
    internal bool InsideDisplayNone => _ancestors.TryPeek(out var element) && element.IsDisplayNone;

    private static bool IsGroupingContainer(string? name) => name == "div";
    private static bool IsFlowContainer(string? name) => name == "body" || IsGroupingContainer(name);

    internal void AddSvgImage(string reference, double width, double height, int offset)
    {
        if (!_ancestors.TryPeek(out var parent) || !IsFlowContainer(parent.Name)
            || !parent.IsBlock && !parent.IsDisplayNone)
            throw new NotSupportedException("Native EPUB SVG covers require a block body or container parent.");
        var identity = new SelectorIdentity("http://www.w3.org/2000/svg", [], new());
        var properties = ResolveRules("svg", identity);
        if ((properties with { Margins = null, Padding = null }) != default
            || properties.Margins?.Any(value => value is not null) == true
            || properties.Padding?.Any(value => value is not null) == true)
            throw new NotSupportedException("Native EPUB SVG cover CSS is not implemented.");
        var element = new Element("svg", parent, parent.Style with { Background = null }, true, offset, identity)
        {
            SvgViewBox = (width, height), IsDisplayNone = parent.IsDisplayNone
        };
        parent.Children.Add(element);
        if (!element.IsDisplayNone) Inlines.Add(new(offset, reference, element));
    }

    internal void Enter(XmlReader xml)
    {
        string name = xml.LocalName;
        bool paragraph = Element.IsParagraphName(name);
        Element? parent = _ancestors.TryPeek(out var ancestor) ? ancestor : null;
        if (parent?.Name == "hr")
            throw new NotSupportedException("Native EPUB horizontal rules cannot contain elements.");
        if (name == "a" && _ancestors.Any(element => element.Name == "a"))
            throw new NotSupportedException("Native EPUB nested anchors are invalid.");
        if (name == "img" && parent?.LinkReference is not null)
            throw new NotSupportedException("Native EPUB linked image regions are not implemented.");
        bool allowed = name switch {
            "html" => parent is null && xml.Depth == 0,
            "head" or "body" => parent?.Name == "html",
            "title" => parent?.Name == "head",
            "meta" => parent?.Name == "head" && (xml.GetAttribute("http-equiv") is null
                || string.Equals(xml.GetAttribute("http-equiv"), "Content-Style-Type", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(xml.GetAttribute("content"), "text/css", StringComparison.OrdinalIgnoreCase)),
            "link" => parent?.Name == "head" && xml.GetAttribute("rel") is "icon" or "stylesheet"
                && !string.IsNullOrWhiteSpace(xml.GetAttribute("href")),
            _ when IsGroupingContainer(name) => IsFlowContainer(parent?.Name),
            "table" => IsFlowContainer(parent?.Name),
            "tbody" => parent?.Name == "table",
            "tr" => parent?.Name is "table" or "tbody",
            "td" => parent?.Name == "tr",
            _ when paragraph => IsFlowContainer(parent?.Name),
            "strong" or "b" or "i" or "em" => parent?.IsParagraph == true || IsFlowContainer(parent?.Name) || parent?.Name is "span" or "a" or "strong" or "b" or "i" or "em",
            "span" or "br" => parent?.IsParagraph == true || IsFlowContainer(parent?.Name) || parent?.Name is "span" or "a" or "strong" or "b" or "i" or "em",
            "a" => parent?.IsParagraph == true || IsFlowContainer(parent?.Name) || parent?.Name is "span" or "strong" or "b" or "i" or "em" or "td",
            "img" => parent?.IsParagraph == true || IsFlowContainer(parent?.Name) || parent?.Name is "span" or "a" or "strong" or "b" or "i" or "em",
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
            if (xml.NamespaceURI == "" && xml.LocalName == "style" && (paragraph || IsFlowContainer(name) || name is "span" or "img" or "a" or "strong" or "b" or "i" or "em" or "table" or "tbody" or "tr" or "td"))
                inlineStyle = xml.Value;
            else if (xml.NamespaceURI == "" && xml.LocalName == "class")
                classNames = xml.Value;
            else if (xml.NamespaceURI == "" && xml.LocalName == "id" && (paragraph || IsFlowContainer(name) || name is "span" or "br" or "a" or "strong" or "b" or "i" or "em"))
            {
                // Navigation identity is captured by the structural text reader.
            }
            else if (name == "link" && xml.NamespaceURI == "" && xml.LocalName is "href" or "rel" or "type" or "id")
            {
                // Packaged references are validated separately from body painting.
            }
            else if (name == "meta" && xml.NamespaceURI == "" && xml.LocalName is "name" or "content" or "http-equiv")
            {
                // Named metadata and the CSS content-style declaration do not paint page content.
            }
            else if (name == "meta" && xml.NamespaceURI == "" && xml.LocalName == "charset"
                && string.Equals(xml.Value, "utf-8", StringComparison.OrdinalIgnoreCase))
            {
                // XML byte decoding is authoritative; this head declaration does not change it.
            }
            else if (name == "a" && xml.NamespaceURI == "" && xml.LocalName == "href")
            {
                // Structural reading validates packaged destinations and supported external hyperlinks.
            }
            else if (name == "img" && xml.NamespaceURI == "" && xml.LocalName is "src" or "alt" or "title")
            {
                // Image resources are resolved within the captured package.
            }
            else if ((paragraph || name is "span" or "strong" or "b" or "i" or "em") && xml.LocalName == "type")
            {
                // Semantic type metadata is inert; selectors compare its expanded name.
            }
            else if (xml.NamespaceURI == "" && xml.Value.Length == 0
                && (xml.LocalName == "title" || name == "table" && xml.LocalName == "summary"))
            {
                // Empty advisory metadata has no displayed content.
            }
            else if (xml.NamespaceURI != "http://www.w3.org/2000/xmlns/"
                && !(xml.LocalName == "lang" && xml.NamespaceURI is "" or "http://www.w3.org/XML/1998/namespace"))
                throw new NotSupportedException("Native EPUB XHTML attributes and styling are not implemented.");
        }
        xml.MoveToElement();
        Style inherited = parent?.Style ?? DefaultStyle;
        if (inherited.Background is not null) inherited = inherited with { Background = null };
        var identity = new SelectorIdentity(xml.NamespaceURI,
            classNames?.Split([' ', '\t', '\r', '\n', '\f'], StringSplitOptions.RemoveEmptyEntries) ?? [], attributes);
        CssProperties declarations = ResolveRules(name, identity);
        if (inlineStyle is not null) declarations = declarations.Override(ParseProperties(inlineStyle));
        if (name == "hr") declarations = HorizontalRuleDefaults.Override(declarations);
        if (name is "strong" or "b")
        {
            double parentWeight = parent?.Style.FontWeight ?? DefaultStyle.FontWeight;
            double bolderWeight = parentWeight switch
            {
                < 350 => 400,
                < 550 => 700,
                < 900 => 900,
                _ => parentWeight
            };
            declarations = CssProperties.FromConcrete(new ResolvedProperties(null, null, FontWeight: bolderWeight))
                .Override(declarations);
        }
        if (name is "i" or "em")
            declarations = CssProperties.FromConcrete(new ResolvedProperties(null, null) { FontStyle = EpubFontStyle.Italic })
                .Override(declarations);
        if (HeadingDefaults(name) is { } headingDefaults)
        {
            var defaults = ResolveProperties(declarations, parent).Display == DisplayKind.Inline
                ? headingDefaults with { Margins = null } : headingDefaults;
            declarations = defaults.Override(declarations);
        }
        var properties = ResolveProperties(declarations, parent);
        DisplayKind? tableDisplay = name switch
        {
            "table" => DisplayKind.Table, "tbody" => DisplayKind.TableRowGroup,
            "tr" => DisplayKind.TableRow, "td" => DisplayKind.TableCell,
            _ => null
        };
        if (tableDisplay is { } required && properties.Display is { } specified
            && specified != DisplayKind.None && specified != required)
            throw new NotSupportedException("Native EPUB table display overrides are not implemented.");
        bool suppressed = parent?.IsDisplayNone == true || properties.Display == DisplayKind.None;
        Style style = suppressed ? inherited : properties.Apply(inherited, contentHeight);
        if (!suppressed)
        {
            if (name == "tbody" && (style.PageBreakBefore || style.PageBreakAfter))
                throw new NotSupportedException("Native EPUB row-group page breaks are not implemented.");
            if (name is "tr" or "td" && (style.PageBreakBefore || style.PageBreakAfter))
                throw new NotSupportedException("Native EPUB row and cell page breaks are not implemented.");
            if (name == "html" && properties.Height is { Percentage: not null })
                throw new NotSupportedException("Native EPUB root percentage heights are not implemented.");
            // Supported normal-flow containing blocks all have content-dependent height.
            // Nonroot percentages therefore use auto; definite heights and positioning remain unsupported.
            if (name is "img" or "svg" && properties.Margins?.Any(side => side is { Auto: true }) == true)
                throw new NotSupportedException("Native EPUB automatic replaced-image margins are not implemented.");
            if (properties.Border is { Visible: true, Nonzero: true })
                throw new NotSupportedException("Native EPUB visible borders are not implemented.");
            if (properties.TopBorder is { Visible: true, Width.Value: > 0 }
                && (!paragraph || properties.Display == DisplayKind.Inline))
                throw new NotSupportedException("Native EPUB visible top borders require block paragraphs.");
            if (properties.Width is { Length: not null } && (!(paragraph || IsGroupingContainer(name) || name is "table" or "td") || properties.Display == DisplayKind.Inline))
                throw new NotSupportedException("Native EPUB explicit widths require block paragraphs or containers.");
            if (properties.MaximumWidth is { Length: not null } && name != "img")
                throw new NotSupportedException("Native EPUB maximum widths currently require images.");
            if (name == "body")
            {
                _bodyMargins = properties.Margins;
                _bodyPadding = properties.Padding;
                _bodyFontSize = style.FontSize;
            }
            else if (properties.Margins?.Any(side => side is { Value: > 0 }) == true
                && (!(paragraph || IsGroupingContainer(name)) || properties.Display == DisplayKind.Inline))
                throw new NotSupportedException("Native EPUB nonzero margins are implemented only on the body.");
            if (name != "body" && properties.Padding?.Any(side => side is { Value: > 0 }) == true
                && (!paragraph || properties.Display == DisplayKind.Inline))
                throw new NotSupportedException("Native EPUB nonzero padding is implemented only on the body.");
            if (style.Background is { } background)
            {
                if (!paragraph && !IsFlowContainer(name) && name is not ("span" or "strong" or "b"))
                    throw new NotSupportedException("Native EPUB element background painting is not implemented.");
                if (name == "body") CanvasBackground = background.Color;
            }
        }
        bool block = properties.Display switch
        {
            DisplayKind.Block => true,
            DisplayKind.Inline or DisplayKind.None => false,
            _ => paragraph || IsFlowContainer(name) || name is "table" or "tbody" or "tr" or "td"
        };
        var element = new Element(name, parent, style, block, Characters.Count, identity)
        {
            Display = properties.Display ?? name switch
            {
                "table" => DisplayKind.Table, "tbody" => DisplayKind.TableRowGroup,
                "tr" => DisplayKind.TableRow, "td" => DisplayKind.TableCell,
                _ => block ? DisplayKind.Block : DisplayKind.Inline
            },
            Margins = suppressed ? null : properties.Margins, Padding = suppressed ? null : properties.Padding,
            TopBorder = suppressed ? null : properties.TopBorder, Width = suppressed ? null : properties.Width,
            MaximumWidth = suppressed ? null : properties.MaximumWidth,
            Border = suppressed ? null : properties.Border, HeightPercentage = suppressed ? null : properties.Height?.Percentage,
            IsDisplayNone = suppressed
        };
        CurrentElement = element;
        CurrentElementDisplayNone = element.IsDisplayNone;
        parent?.Children.Add(element);
        if (name == "body") Body = element;
        if (paragraph)
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

    internal void Exit() => _ancestors.Pop().EndOffset = Characters.Count;

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
        if (owner.Name == "hr")
            throw new NotSupportedException("Native EPUB horizontal rules cannot contain text.");
        if (owner.Display is DisplayKind.Table or DisplayKind.TableRowGroup or DisplayKind.TableRow && !IsFormattingWhitespace(value))
            throw new NotSupportedException("Native EPUB table text requires a cell.");
        var node = new Text(value, owner, Characters.Count);
        owner.Children.Add(node);
        return node;
    }

    internal static bool IsFormattingWhitespace(string value) =>
        value.All(character => character is ' ' or '\t' or '\r' or '\n');

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
        var mediaEnds = new Stack<int>();
        int mediaWork = 0;
        while (position < css.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            while (position < css.Length && IsCssSpace(css[position])) position++;
            if (position == css.Length) break;
            if (css[position] == '}')
            {
                if (!mediaEnds.TryPeek(out int end) || position != end)
                    throw new NotSupportedException("Native EPUB CSS media blocks require balanced rules.");
                mediaEnds.Pop();
                position++;
                continue;
            }
            if (css.AsSpan(position).StartsWith("@media", StringComparison.OrdinalIgnoreCase))
            {
                int mediaOpen = css.IndexOf('{', position + 6);
                if (position + 6 >= css.Length || !IsCssSpace(css[position + 6]) || mediaOpen < 0
                    || !css[(position + 6)..mediaOpen].Trim(' ', '\t', '\r', '\n', '\f').Equals("screen", StringComparison.OrdinalIgnoreCase))
                    throw new NotSupportedException("Native EPUB media queries currently require screen.");
                if (mediaEnds.Count >= 16)
                    throw new InvalidDataException("Native EPUB CSS media nesting exceeds its 16-level limit.");
                int mediaEnd = MediaEnd(mediaOpen);
                if (mediaEnds.TryPeek(out int parentEnd) && mediaEnd >= parentEnd)
                    throw new NotSupportedException("Native EPUB CSS media blocks require balanced rules.");
                mediaEnds.Push(mediaEnd);
                styleSeen = true;
                position = mediaOpen + 1;
                continue;
            }
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
            if (close < 0 || mediaEnds.TryPeek(out int ruleEnd) && close >= ruleEnd)
                throw new NotSupportedException("Native EPUB CSS rule syntax is not implemented.");
            CssProperties properties = ParseProperties(css[(open + 1)..close]);
            foreach (string group in css[position..open].Split(','))
            {
                if (++_selectorCount > 4096)
                    throw new InvalidDataException("Native EPUB CSS selectors exceed their 4,096-selector limit.");
                string fullSelector = group.Trim(' ', '\t', '\r', '\n', '\f');
                if (fullSelector.Length is 0 or > 128)
                    throw new NotSupportedException("Native EPUB CSS selector is not implemented.");
                var chain = new List<CssRule>();
                bool directParent = false;
                foreach (string selectorPart in DescendantParts(fullSelector))
                {
                    if (selectorPart == ">")
                    {
                        if (chain.Count == 0 || directParent)
                            throw new NotSupportedException("Native EPUB CSS child selectors require selectors on both sides.");
                        directParent = true;
                        continue;
                    }
                    string selector = selectorPart;
                    bool link = selector.EndsWith(":link", StringComparison.OrdinalIgnoreCase);
                    bool visited = selector.EndsWith(":visited", StringComparison.OrdinalIgnoreCase);
                    bool hover = selector.EndsWith(":hover", StringComparison.OrdinalIgnoreCase);
                    if (link) selector = selector[..^5];
                    else if (visited) selector = selector[..^8];
                    else if (hover) selector = selector[..^6];
                    CssAttribute? attribute = null;
                    int bracket = selector.IndexOf('[');
                    if (bracket >= 0)
                    {
                        if (!selector.EndsWith(']') || selector.IndexOf('[', bracket + 1) >= 0)
                            throw new NotSupportedException("Native EPUB CSS selector is not implemented.");
                        string condition = selector[(bracket + 1)..^1].Trim(' ', '\t', '\r', '\n', '\f');
                        int equal = condition.IndexOf('=');
                        bool exact = equal >= 0 && (equal == 0 || condition[equal - 1] != '~');
                        int operation = equal >= 0 && !exact ? equal - 1 : equal;
                        string attributeName = operation < 0 ? condition : condition[..operation].Trim(' ', '\t', '\r', '\n', '\f');
                        string? token = null;
                        if (operation >= 0)
                        {
                            token = condition[(equal + 1)..].Trim(' ', '\t', '\r', '\n', '\f');
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
                        attribute = new(attributeNamespace, attributeName, token, exact);
                        selector = selector[..bracket];
                    }
                    int firstId = selector.IndexOf('#');
                    if (firstId >= 0 && selector.IndexOf('|') > firstId)
                        throw new NotSupportedException("Native EPUB CSS selector is not implemented.");
                    var ids = new List<string>();
                    var withoutIds = new StringBuilder();
                    for (int at = 0; at < selector.Length;)
                    {
                        if (selector[at] != '#')
                        {
                            withoutIds.Append(selector[at++]);
                            continue;
                        }
                        int start = ++at;
                        while (at < selector.Length && (char.IsAsciiLetterOrDigit(selector[at]) || selector[at] is '-' or '_')) at++;
                        string id = selector[start..at];
                        if (!IsIdentifier(id)) throw new NotSupportedException("Native EPUB CSS selector is not implemented.");
                        ids.Add(id);
                    }
                    selector = withoutIds.ToString();
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
                    bool classOnly = selector.StartsWith(".", StringComparison.Ordinal) || selector.Length == 0 && (ids.Count > 0 || link || visited || hover);
                    if (classOnly && attribute is not null || selector.Length > 128 || selector.Length == 0 && ids.Count == 0 && !link && !visited && !hover || (classOnly ? parts.Skip(1) : parts).Any(part => part.Length == 0
                        || !char.IsAsciiLetter(part[0])
                        || part.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_'))))
                        throw new NotSupportedException("Native EPUB CSS selector is not implemented.");
                    chain.Add(new(namespaceName, parts[1..], attribute, properties, _selectorCount, classOnly ? 0 : 1, type, []) { Ids = ids.ToArray(), Link = link, Visited = visited, Hover = hover, DirectParent = directParent });
                    directParent = false;
                }
                if (directParent || chain.Count == 0)
                    throw new NotSupportedException("Native EPUB CSS child selectors require selectors on both sides.");
                var target = chain[^1] with { Ancestors = chain.Take(chain.Count - 1).Reverse().ToArray() };
                if (!_typeRules.TryGetValue(target.Type, out var rules))
                    _typeRules.Add(target.Type, rules = new());
                rules.Add(target);
            }
            position = close + 1;
        }
        if (mediaEnds.Count != 0)
            throw new NotSupportedException("Native EPUB CSS media blocks require balanced rules.");

        int MediaEnd(int open)
        {
            int depth = 1;
            char quote = '\0';
            for (int at = open + 1; at < css.Length; at++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++mediaWork > 1_000_000)
                    throw new InvalidDataException("Native EPUB CSS media scanning exceeds its work limit.");
                char current = css[at];
                if (current == '\\') { at++; continue; }
                if (quote != '\0')
                {
                    if (current == quote) quote = '\0';
                    continue;
                }
                if (current is '\'' or '"') { quote = current; continue; }
                if (current == '{') depth++;
                else if (current == '}' && --depth == 0) return at;
            }
            throw new NotSupportedException("Native EPUB CSS media blocks require balanced rules.");
        }

        void SkipSpace() { while (position < css.Length && IsCssSpace(css[position])) position++; }
    }

    private static bool IsCssSpace(char value) => value is ' ' or '\t' or '\r' or '\n' or '\f';
    private static bool IsIdentifier(string value) => value.Length is > 0 and <= 128
        && (char.IsAsciiLetter(value[0]) || value[0] == '_')
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
    private static NotSupportedException NamespaceSyntax() =>
        new("Native EPUB CSS namespace syntax is not implemented.");

    private sealed record CssAttribute(string NamespaceName, string Name, string? Token, bool Exact = false);
    private sealed record CssRule(string? NamespaceName, string[] Classes, CssAttribute? Attribute,
        CssProperties Properties, int Order, int TypeSpecificity, string Type, CssRule[] Ancestors)
    {
        internal bool Link { get; init; }
        internal bool Visited { get; init; }
        internal bool Hover { get; init; }
        internal bool DirectParent { get; init; }
        internal string[] Ids { get; init; } = [];
        internal int Specificity => Ids.Length * 65536 + (Classes.Length + (Attribute is null ? 0 : 1) + (Link || Visited || Hover ? 1 : 0)) * 256 + TypeSpecificity
            + Ancestors.Sum(rule => rule.Ids.Length * 65536 + (rule.Classes.Length + (rule.Attribute is null ? 0 : 1) + (rule.Link || rule.Visited || rule.Hover ? 1 : 0)) * 256 + rule.TypeSpecificity);
    }

    private static IEnumerable<string> DescendantParts(string selector)
    {
        int start = 0;
        bool bracket = false;
        char quote = '\0';
        for (int index = 0; index < selector.Length; index++)
        {
            char value = selector[index];
            if (quote != '\0') { if (value == quote) quote = '\0'; }
            else if (bracket && value is '\'' or '"') quote = value;
            else if (value == '[') bracket = true;
            else if (value == ']') bracket = false;
            else if (!bracket && (IsCssSpace(value) || value == '>'))
            {
                if (index > start) yield return selector[start..index];
                if (value == '>') yield return ">";
                start = index + 1;
            }
        }
        if (start < selector.Length) yield return selector[start..];
    }

    private bool Matches(CssRule rule, string name, SelectorIdentity identity)
    {
        if (rule.Hover)
        {
            CountSelectorWork(1);
            // Captured native layout has no active pointing-device designation.
            return false;
        }
        if (rule.Visited)
        {
            CountSelectorWork(1);
            // Native reading treats links as unvisited without consulting browsing history.
            return false;
        }
        if (rule.Link)
        {
            CountSelectorWork(2);
            if (name != "a" || identity.NamespaceName != "http://www.w3.org/1999/xhtml"
                || !identity.Attributes.ContainsKey(("", "href"))) return false;
        }
        CountSelectorWork(1);
        foreach (string required in rule.Ids)
        {
            if (!identity.Attributes.TryGetValue(("", "id"), out string? actual)) return false;
            CountSelectorWork(1 + Math.Min(required.Length, actual.Length));
            if (actual != required) return false;
        }
        if (rule.Type.Length > 0)
        {
            CountSelectorWork(1 + Math.Min(rule.Type.Length, name.Length));
            if (!string.Equals(rule.Type, name, StringComparison.Ordinal)) return false;
        }
        if (rule.NamespaceName is { } requiredNamespace)
        {
            CountSelectorWork(1 + Math.Min(requiredNamespace.Length, identity.NamespaceName.Length));
            if (!string.Equals(requiredNamespace, identity.NamespaceName, StringComparison.Ordinal)) return false;
        }
        foreach (string required in rule.Classes)
        {
            bool found = false;
            foreach (string candidate in identity.Classes)
            {
                CountSelectorWork(1 + Math.Min(candidate.Length, required.Length));
                if (!string.Equals(candidate, required, StringComparison.Ordinal)) continue;
                found = true;
                break;
            }
            if (!found) return false;
        }
        if (rule.Attribute is { } attribute)
        {
            CountSelectorWork(1 + attribute.NamespaceName.Length + attribute.Name.Length);
            if (!identity.Attributes.TryGetValue((attribute.NamespaceName, attribute.Name), out string? value)) return false;
            if (attribute.Token is { } token)
            {
                CountSelectorWork(value.Length + token.Length);
                if (attribute.Exact)
                {
                    if (!string.Equals(value, token, StringComparison.Ordinal)) return false;
                }
                else if (token.Length == 0 || token.Any(IsCssSpace)
                    || !value.Split([' ', '\t', '\r', '\n', '\f'], StringSplitOptions.RemoveEmptyEntries)
                        .Contains(token, StringComparer.Ordinal)) return false;
            }
        }
        return true;
    }

    private bool MatchesAncestors(CssRule rule, Element? ancestor)
    {
        if (rule.Ancestors.Length == 0) return true;
        var memo = new Dictionary<(int Index, Element Ancestor), bool>();
        return Match(0, ancestor);

        bool Match(int index, Element? current)
        {
            if (index == rule.Ancestors.Length) return true;
            if (current is null) return false;
            var key = (index, current);
            if (memo.TryGetValue(key, out bool cached)) return cached;
            CountSelectorWork(1);
            var required = rule.Ancestors[index];
            bool direct = index == 0 ? rule.DirectParent : rule.Ancestors[index - 1].DirectParent;
            bool matched = Matches(required, current.Name, current.Identity) && Match(index + 1, current.Parent);
            if (!matched && !direct) matched = Match(index, current.Parent);
            memo.Add(key, matched);
            return matched;
        }
    }

    private CssProperties ResolveRules(string name, SelectorIdentity identity)
    {
        _typeRules.TryGetValue(name, out var typeRules);
        _typeRules.TryGetValue("", out var classRules);
        if (typeRules is null && classRules is null) return default;
        CssDeclaration<CssLength>? size = null, height = null;
        CssDeclaration<PdfRgbColor>? color = null;
        CssDeclaration<BackgroundColor>? background = null;
        int sizeSpecificity = -1, heightSpecificity = -1, colorSpecificity = -1, backgroundSpecificity = -1;
        CssDeclaration<EpubFontFamily[]>? families = null;
        int familySpecificity = -1;
        CssDeclaration<double>? fontWeight = null;
        int weightSpecificity = -1;
        CssDeclaration<EpubFontStyle>? fontStyle = null;
        int styleSpecificity = -1;
        CssDeclaration<string>? fontVariant = null;
        int variantSpecificity = -1;
        CssDeclaration<Border>? border = null;
        int borderSpecificity = -1;
        CssDeclaration<BorderSide>? topBorder = null;
        int topBorderSpecificity = -1;
        CssDeclaration<BoxWidth>? width = null;
        int widthSpecificity = -1;
        CssDeclaration<BoxHeight>? boxHeight = null;
        int boxHeightSpecificity = -1;
        CssDeclaration<BoxWidth>? maximumWidth = null;
        int maximumWidthSpecificity = -1;
        CssDeclaration<string>? textAlign = null;
        int alignmentSpecificity = -1;
        CssDeclaration<IndentLength>? textIndent = null;
        int indentSpecificity = -1;
        CssDeclaration<bool>? pageBreakBefore = null;
        int breakSpecificity = -1;
        CssDeclaration<bool>? pageBreakAfter = null;
        int breakAfterSpecificity = -1;
        CssDeclaration<DisplayKind>? block = null;
        int displaySpecificity = -1;
        CssDeclaration<string>? visibility = null;
        int visibilitySpecificity = -1;
        CssDeclaration<string>? decoration = null;
        int decorationSpecificity = -1;
        CssDeclaration<MarginLength>? letterSpacing = null;
        int letterSpacingSpecificity = -1;
        CssDeclaration<MarginLength>? wordSpacing = null;
        int wordSpacingSpecificity = -1;
        var padding = new CssDeclaration<MarginLength>?[4];
        int[] paddingSpecificities = [-1, -1, -1, -1];
        var margins = new CssDeclaration<MarginLength>?[4];
        int[] marginSpecificities = [-1, -1, -1, -1];
        int typeIndex = 0, classIndex = 0;
        while (typeIndex < (typeRules?.Count ?? 0) || classIndex < (classRules?.Count ?? 0))
        {
            var rule = classIndex >= (classRules?.Count ?? 0)
                || typeIndex < (typeRules?.Count ?? 0) && typeRules![typeIndex].Order < classRules![classIndex].Order
                ? typeRules![typeIndex++] : classRules![classIndex++];
            if (!Matches(rule, name, identity)) continue;
            Element? ancestor = _ancestors.TryPeek(out var parent) ? parent : null;
            if (!MatchesAncestors(rule, ancestor)) continue;
            int specificity = rule.Specificity;
            if (rule.Properties.WordSpacing is { } selectedWordSpacing && specificity >= wordSpacingSpecificity)
            {
                wordSpacing = selectedWordSpacing;
                wordSpacingSpecificity = specificity;
            }
            if (rule.Properties.LetterSpacing is { } tracking && specificity >= letterSpacingSpecificity)
            {
                letterSpacing = tracking;
                letterSpacingSpecificity = specificity;
            }
            if (rule.Properties.TextDecoration is { } selectedDecoration && specificity >= decorationSpecificity)
            {
                decoration = selectedDecoration;
                decorationSpecificity = specificity;
            }
            if (rule.Properties.Visibility is { } selectedVisibility && specificity >= visibilitySpecificity)
            {
                visibility = selectedVisibility;
                visibilitySpecificity = specificity;
            }
            if (rule.Properties.FontVariant is { } variant && specificity >= variantSpecificity)
            {
                fontVariant = variant;
                variantSpecificity = specificity;
            }
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
            if (rule.Properties.Display is { } display && specificity >= displaySpecificity)
            {
                block = display;
                displaySpecificity = specificity;
            }
            if (rule.Properties.FontStyle is { } selectedStyle && specificity >= styleSpecificity)
            {
                fontStyle = selectedStyle;
                styleSpecificity = specificity;
            }
            if (rule.Properties.PageBreakBefore is { } forcedBreak && specificity >= breakSpecificity)
            {
                pageBreakBefore = forcedBreak;
                breakSpecificity = specificity;
            }
            if (rule.Properties.PageBreakAfter is { } forcedBreakAfter && specificity >= breakAfterSpecificity)
            {
                pageBreakAfter = forcedBreakAfter;
                breakAfterSpecificity = specificity;
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
            if (rule.Properties.TopBorder is { } selectedTopBorder && specificity >= topBorderSpecificity)
            {
                topBorder = selectedTopBorder;
                topBorderSpecificity = specificity;
            }
            if (rule.Properties.Width is { } selectedWidth && specificity >= widthSpecificity)
            {
                width = selectedWidth;
                widthSpecificity = specificity;
            }
            if (rule.Properties.Height is { } selectedHeight && specificity >= boxHeightSpecificity)
            {
                boxHeight = selectedHeight;
                boxHeightSpecificity = specificity;
            }
            if (rule.Properties.MaximumWidth is { } selectedMaximumWidth && specificity >= maximumWidthSpecificity)
            {
                maximumWidth = selectedMaximumWidth;
                maximumWidthSpecificity = specificity;
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
        return new CssProperties(size, height, color, background, families, margins, border, padding, textAlign, pageBreakBefore, block, fontWeight, textIndent, fontVariant, topBorder, width, visibility, decoration, maximumWidth, pageBreakAfter) { LetterSpacing = letterSpacing, WordSpacing = wordSpacing, Height = boxHeight, FontStyle = fontStyle };
    }

    private void CountSelectorWork(int amount)
    {
        if (amount > 1_000_000 - _selectorWork)
            throw new InvalidDataException("Native EPUB CSS selector matching exceeds its work limit.");
        _selectorWork += amount;
    }

    private readonly record struct CssLength(double Value, bool Relative, bool Unitless = false, bool Normal = false)
    {
        internal double Compute(double reference, double maximum)
        {
            if (Normal) throw new InvalidOperationException("Normal line height is not a numeric CSS length.");
            double pixels = Relative ? Value * reference : Value;
            if (!double.IsFinite(pixels) || pixels <= 0 || pixels > maximum)
                throw new NotSupportedException("Native EPUB typography requires positive CSS lengths that fit the page.");
            return pixels;
        }
    }

    internal readonly record struct MarginLength(double Value, bool Em, bool Percentage = false, bool Auto = false);
    private readonly record struct IndentLength(double Value, bool Em = false, bool Percentage = false, bool Inherit = false);
    internal readonly record struct Border(bool Nonzero, bool Visible);
    internal readonly record struct BorderSide(MarginLength Width, bool Visible, BackgroundColor? Color);
    internal readonly record struct BoxWidth(MarginLength? Length);
    private readonly record struct BoxHeight(double? Percentage);

    private enum CssWideKeyword { Concrete, Inherit, Initial }
    private readonly record struct CssDeclaration<T>(T Value, CssWideKeyword Keyword = CssWideKeyword.Concrete)
    {
        internal T Resolve(T initial, T inherited) => Keyword switch
        {
            CssWideKeyword.Inherit => inherited,
            CssWideKeyword.Initial => initial,
            _ => Value
        };
        internal static CssDeclaration<T> Marker(CssWideKeyword keyword) => new(default!, keyword);
    }

    private readonly record struct CssProperties(
        CssDeclaration<CssLength>? FontSize,
        CssDeclaration<CssLength>? LineHeight,
        CssDeclaration<PdfRgbColor>? Color = null,
        CssDeclaration<BackgroundColor>? Background = null,
        CssDeclaration<EpubFontFamily[]>? FontFamilies = null,
        CssDeclaration<MarginLength>?[]? Margins = null,
        CssDeclaration<Border>? Border = null,
        CssDeclaration<MarginLength>?[]? Padding = null,
        CssDeclaration<string>? TextAlign = null,
        CssDeclaration<bool>? PageBreakBefore = null,
        CssDeclaration<DisplayKind>? Display = null,
        CssDeclaration<double>? FontWeight = null,
        CssDeclaration<IndentLength>? TextIndent = null,
        CssDeclaration<string>? FontVariant = null,
        CssDeclaration<BorderSide>? TopBorder = null,
        CssDeclaration<BoxWidth>? Width = null,
        CssDeclaration<string>? Visibility = null,
        CssDeclaration<string>? TextDecoration = null,
        CssDeclaration<BoxWidth>? MaximumWidth = null,
        CssDeclaration<bool>? PageBreakAfter = null)
    {
        internal CssDeclaration<MarginLength>? LetterSpacing { get; init; }
        internal CssDeclaration<EpubFontStyle>? FontStyle { get; init; }
        internal CssDeclaration<MarginLength>? WordSpacing { get; init; }
        internal CssDeclaration<BoxHeight>? Height { get; init; }
        internal CssProperties Override(CssProperties later) => new(
            later.FontSize ?? FontSize,
            later.LineHeight ?? LineHeight,
            later.Color ?? Color,
            later.Background ?? Background,
            later.FontFamilies ?? FontFamilies,
            Merge(Margins, later.Margins),
            later.Border ?? Border,
            Merge(Padding, later.Padding),
            later.TextAlign ?? TextAlign,
            later.PageBreakBefore ?? PageBreakBefore,
            later.Display ?? Display,
            later.FontWeight ?? FontWeight,
            later.TextIndent ?? TextIndent,
            later.FontVariant ?? FontVariant,
            later.TopBorder ?? TopBorder,
            later.Width ?? Width,
            later.Visibility ?? Visibility,
            later.TextDecoration ?? TextDecoration,
            later.MaximumWidth ?? MaximumWidth,
            later.PageBreakAfter ?? PageBreakAfter)
        { LetterSpacing = later.LetterSpacing ?? LetterSpacing, WordSpacing = later.WordSpacing ?? WordSpacing, Height = later.Height ?? Height, FontStyle = later.FontStyle ?? FontStyle };
        private static CssDeclaration<MarginLength>?[]? Merge(CssDeclaration<MarginLength>?[]? earlier, CssDeclaration<MarginLength>?[]? later) =>
            later is null ? earlier : Enumerable.Range(0, 4).Select(side => later[side] ?? earlier?[side]).ToArray();
        internal static CssProperties FromConcrete(ResolvedProperties value) => new(
            value.FontSize is { } vFontSize ? new CssDeclaration<CssLength>(vFontSize) : null,
            value.LineHeight is { } vLineHeight ? new CssDeclaration<CssLength>(vLineHeight) : null,
            value.Color is { } vColor ? new CssDeclaration<PdfRgbColor>(vColor) : null,
            value.Background is { } vBackground ? new CssDeclaration<BackgroundColor>(vBackground) : null,
            value.FontFamilies is { } vFontFamilies ? new CssDeclaration<EpubFontFamily[]>(vFontFamilies) : null,
            value.Margins?.Select(side => side is { } length ? (CssDeclaration<MarginLength>?)new CssDeclaration<MarginLength>(length) : null).ToArray(),
            value.Border is { } vBorder ? new CssDeclaration<Border>(vBorder) : null,
            value.Padding?.Select(side => side is { } length ? (CssDeclaration<MarginLength>?)new CssDeclaration<MarginLength>(length) : null).ToArray(),
            value.TextAlign is { } vTextAlign ? new CssDeclaration<string>(vTextAlign) : null,
            value.PageBreakBefore is { } vPageBreakBefore ? new CssDeclaration<bool>(vPageBreakBefore) : null,
            value.Display is { } vDisplay ? new CssDeclaration<DisplayKind>(vDisplay) : null,
            value.FontWeight is { } vFontWeight ? new CssDeclaration<double>(vFontWeight) : null,
            value.TextIndent is { } vTextIndent ? new CssDeclaration<IndentLength>(vTextIndent) : null,
            value.FontVariant is { } vFontVariant ? new CssDeclaration<string>(vFontVariant) : null,
            value.TopBorder is { } vTopBorder ? new CssDeclaration<BorderSide>(vTopBorder) : null,
            value.Width is { } vWidth ? new CssDeclaration<BoxWidth>(vWidth) : null,
            value.Visibility is { } vVisibility ? new CssDeclaration<string>(vVisibility) : null,
            value.TextDecoration is { } vTextDecoration ? new CssDeclaration<string>(vTextDecoration) : null,
            value.MaximumWidth is { } vMaximumWidth ? new CssDeclaration<BoxWidth>(vMaximumWidth) : null,
            value.PageBreakAfter is { } vPageBreakAfter ? new CssDeclaration<bool>(vPageBreakAfter) : null)
        { LetterSpacing = value.LetterSpacing is { } vLetterSpacing ? new CssDeclaration<MarginLength>(vLetterSpacing) : null, WordSpacing = value.WordSpacing is { } vWordSpacing ? new CssDeclaration<MarginLength>(vWordSpacing) : null, Height = value.Height is { } vHeight ? new CssDeclaration<BoxHeight>(vHeight) : null, FontStyle = value.FontStyle is { } vStyle ? new CssDeclaration<EpubFontStyle>(vStyle) : null };
        internal static CssProperties ForKeyword(CssWideKeyword keyword) => new(
            CssDeclaration<CssLength>.Marker(keyword),
            CssDeclaration<CssLength>.Marker(keyword),
            CssDeclaration<PdfRgbColor>.Marker(keyword),
            CssDeclaration<BackgroundColor>.Marker(keyword),
            CssDeclaration<EpubFontFamily[]>.Marker(keyword),
            Enumerable.Repeat((CssDeclaration<MarginLength>?)CssDeclaration<MarginLength>.Marker(keyword), 4).ToArray(),
            CssDeclaration<Border>.Marker(keyword),
            Enumerable.Repeat((CssDeclaration<MarginLength>?)CssDeclaration<MarginLength>.Marker(keyword), 4).ToArray(),
            CssDeclaration<string>.Marker(keyword),
            CssDeclaration<bool>.Marker(keyword),
            CssDeclaration<DisplayKind>.Marker(keyword),
            CssDeclaration<double>.Marker(keyword),
            CssDeclaration<IndentLength>.Marker(keyword),
            CssDeclaration<string>.Marker(keyword),
            CssDeclaration<BorderSide>.Marker(keyword),
            CssDeclaration<BoxWidth>.Marker(keyword),
            CssDeclaration<string>.Marker(keyword),
            CssDeclaration<string>.Marker(keyword),
            CssDeclaration<BoxWidth>.Marker(keyword),
            CssDeclaration<bool>.Marker(keyword))
        { LetterSpacing = CssDeclaration<MarginLength>.Marker(keyword), WordSpacing = CssDeclaration<MarginLength>.Marker(keyword), Height = CssDeclaration<BoxHeight>.Marker(keyword), FontStyle = CssDeclaration<EpubFontStyle>.Marker(keyword) };
    }

    private ResolvedProperties ResolveProperties(CssProperties declarations, Element? parent)
    {
        var result = new ResolvedProperties(
            declarations.FontSize?.Resolve(new CssLength(DefaultStyle.FontSize, false), parent is null ? new CssLength(DefaultStyle.FontSize, false) : new CssLength(parent.Style.FontSize, false)),
            declarations.LineHeight?.Resolve(new CssLength(0, false, Normal: true), parent is null ? new CssLength(0, false, Normal: true) : ParentLineHeight(parent)),
            declarations.Color?.Resolve(new PdfRgbColor(0, 0, 0), parent is null ? new PdfRgbColor(0, 0, 0) : parent.Style.Color ?? fallbackTextColor ?? new PdfRgbColor(0, 0, 0)),
            declarations.Background?.Resolve(new BackgroundColor(null), parent is null ? new BackgroundColor(null) : parent.Style.Background ?? new BackgroundColor(null)),
            declarations.FontFamilies?.Resolve(Array.Empty<EpubFontFamily>(), parent is null ? Array.Empty<EpubFontFamily>() : parent.Style.FontFamilies ?? Array.Empty<EpubFontFamily>()),
            ResolveSides(declarations.Margins, parent?.Margins, parent),
            declarations.Border?.Resolve(new Border(false, false), parent is null ? new Border(false, false) : parent.Border ?? new Border(false, false)),
            ResolveSides(declarations.Padding, parent?.Padding, parent),
            declarations.TextAlign?.Resolve("left", parent is null ? "left" : parent.Style.TextAlign),
            declarations.PageBreakBefore?.Resolve(false, parent is null ? false : parent.Style.PageBreakBefore),
            declarations.Display?.Resolve(DisplayKind.Inline, parent is null ? DisplayKind.Inline : parent.IsDisplayNone ? DisplayKind.None : parent.Name == "html" ? DisplayKind.Block : parent.Display),
            declarations.FontWeight?.Resolve(400, parent is null ? 400 : parent.Style.FontWeight),
            declarations.TextIndent?.Resolve(new IndentLength(0), parent is null ? new IndentLength(0) : new IndentLength(parent.Style.TextIndent, Percentage: parent.Style.TextIndentPercentage)),
            declarations.FontVariant?.Resolve("normal", parent is null ? "normal" : parent.Style.SmallCaps ? "small-caps" : "normal"),
            declarations.TopBorder?.Resolve(new BorderSide(new MarginLength(0, false), false, null), parent is null ? new BorderSide(new MarginLength(0, false), false, null) : ParentTopBorder(parent)),
            declarations.Width?.Resolve(new BoxWidth(null), parent is null ? new BoxWidth(null) : new BoxWidth(ParentLength(parent.Width?.Length, parent))),
            declarations.Visibility?.Resolve("visible", parent is null ? "visible" : parent.Style.Hidden ? "hidden" : "visible"),
            declarations.TextDecoration?.Resolve("none", parent is null ? "none" : parent.Style.Underline ? "underline" : "none"),
            declarations.MaximumWidth?.Resolve(new BoxWidth(null), parent is null ? new BoxWidth(null) : new BoxWidth(ParentLength(parent.MaximumWidth?.Length, parent))),
            declarations.PageBreakAfter?.Resolve(false, parent is null ? false : parent.Style.PageBreakAfter))
        {
            LetterSpacing = declarations.LetterSpacing?.Resolve(new MarginLength(0, false), parent is null ? new MarginLength(0, false) : new MarginLength(parent.Style.LetterSpacing, false)),
            FontStyle = declarations.FontStyle?.Resolve(EpubFontStyle.Normal, parent?.Style.FontStyle ?? EpubFontStyle.Normal),
            WordSpacing = declarations.WordSpacing?.Resolve(new MarginLength(0, false), parent is null ? new MarginLength(0, false) : new MarginLength(parent.Style.WordSpacing, false)),
            Height = declarations.Height?.Resolve(new BoxHeight(null), parent is null ? new BoxHeight(null) : new BoxHeight(parent.HeightPercentage))
        };
        // A winning shorthand transparent background has no paint requirement of its own.
        if (declarations.Background is { Keyword: not CssWideKeyword.Concrete } && result.Background is { Color: null })
            result = result with { Background = null };
        return result;
    }

    private static MarginLength?[]? ResolveSides(CssDeclaration<MarginLength>?[]? declarations,
        MarginLength?[]? inherited, Element? parent) => declarations?.Select((side, index) =>
            side?.Resolve(new MarginLength(0, false), ParentLength(inherited?[index], parent) ?? new MarginLength(0, false))).ToArray();

    private static MarginLength? ParentLength(MarginLength? length, Element? parent) =>
        length is { Em: true } em && parent is not null
            ? em with { Value = em.Value * parent.Style.FontSize, Em = false } : length;

    private static CssLength ParentLineHeight(Element parent) => parent.Style.LineHeightFactor is { } factor
        ? new CssLength(factor, true, Unitless: true)
        : parent.Style.LineHeight is { } pixels ? new CssLength(pixels, false) : new CssLength(0, false, Normal: true);

    private BorderSide ParentTopBorder(Element parent) => parent.TopBorder is { } border
        ? border with { Width = ParentLength(border.Width, parent)!.Value,
            Color = border.Color ?? new BackgroundColor(parent.Style.Color ?? fallbackTextColor ?? new PdfRgbColor(0, 0, 0)) }
        : new BorderSide(new MarginLength(0, false), false, null);

    private readonly record struct ResolvedProperties(CssLength? FontSize, CssLength? LineHeight, PdfRgbColor? Color = null,
        BackgroundColor? Background = null, EpubFontFamily[]? FontFamilies = null, MarginLength?[]? Margins = null,
        Border? Border = null, MarginLength?[]? Padding = null, string? TextAlign = null, bool? PageBreakBefore = null,
        DisplayKind? Display = null, double? FontWeight = null, IndentLength? TextIndent = null, string? FontVariant = null,
        BorderSide? TopBorder = null, BoxWidth? Width = null, string? Visibility = null, string? TextDecoration = null,
        BoxWidth? MaximumWidth = null, bool? PageBreakAfter = null)
    {
        internal MarginLength? LetterSpacing { get; init; }
        internal EpubFontStyle? FontStyle { get; init; }
        internal MarginLength? WordSpacing { get; init; }
        internal BoxHeight? Height { get; init; }
        internal ResolvedProperties Override(ResolvedProperties inline) =>
            new(inline.FontSize ?? FontSize, inline.LineHeight ?? LineHeight,
                inline.Color ?? Color, inline.Background ?? Background, inline.FontFamilies ?? FontFamilies,
                MergeMargins(Margins, inline.Margins), inline.Border ?? Border, MergeMargins(Padding, inline.Padding),
                inline.TextAlign ?? TextAlign, inline.PageBreakBefore ?? PageBreakBefore, inline.Display ?? Display,
                inline.FontWeight ?? FontWeight, inline.TextIndent ?? TextIndent, inline.FontVariant ?? FontVariant,
                inline.TopBorder ?? TopBorder, inline.Width ?? Width, inline.Visibility ?? Visibility,
                inline.TextDecoration ?? TextDecoration, inline.MaximumWidth ?? MaximumWidth,
                inline.PageBreakAfter ?? PageBreakAfter) { LetterSpacing = inline.LetterSpacing ?? LetterSpacing, WordSpacing = inline.WordSpacing ?? WordSpacing, Height = inline.Height ?? Height, FontStyle = inline.FontStyle ?? FontStyle };

        private static MarginLength?[]? MergeMargins(MarginLength?[]? earlier, MarginLength?[]? later) =>
            later is null ? earlier : Enumerable.Range(0, 4).Select(side => later[side] ?? earlier?[side]).ToArray();

        internal Style Apply(Style inherited, double maximum)
        {
            double size = FontSize?.Compute(inherited.FontSize, maximum) ?? inherited.FontSize;
            double wordSpacing = WordSpacing is { } separator ? separator.Value * (separator.Em ? size : 1) : inherited.WordSpacing;
            if (!double.IsFinite(wordSpacing) || wordSpacing < 0 || wordSpacing > 32768)
                throw new NotSupportedException("Native EPUB word spacing exceeds the supported dimensions.");
            double tracking = LetterSpacing is { } spacing ? spacing.Value * (spacing.Em ? size : 1) : inherited.LetterSpacing;
            if (!double.IsFinite(tracking) || tracking < 0 || tracking > 32768)
                throw new NotSupportedException("Native EPUB letter spacing exceeds the supported dimensions.");
            double indent = TextIndent is { Inherit: false } length ? (length.Em ? length.Value * size : length.Value) : inherited.TextIndent;
            bool indentPercentage = TextIndent is { Inherit: false } specified ? specified.Percentage : inherited.TextIndentPercentage;
            if (!double.IsFinite(indent) || Math.Abs(indent) > 32768)
                throw new NotSupportedException("Native EPUB text indentation exceeds the supported page dimensions.");
            double? factor = LineHeight is { Normal: true } ? null : LineHeight is { Unitless: true } height ? height.Value
                : LineHeight is null ? inherited.LineHeightFactor : null;
            double? pixels = LineHeight is { Normal: true } ? null : LineHeight?.Compute(size, maximum)
                ?? (factor is double multiplier ? new CssLength(multiplier, true).Compute(size, maximum) : inherited.LineHeight);
            return new(size, pixels, Color ?? inherited.Color, Background ?? inherited.Background)
            {
                LineHeightFactor = factor, FontFamilies = FontFamilies ?? inherited.FontFamilies,
                FontWeight = FontWeight is 0 ? inherited.FontWeight : FontWeight ?? inherited.FontWeight,
                FontStyle = FontStyle ?? inherited.FontStyle,
                LetterSpacing = tracking,
                WordSpacing = wordSpacing,
                Hidden = Visibility is null or "inherit" or "unset" ? inherited.Hidden : Visibility == "hidden",
                Underline = TextDecoration == "inherit" ? inherited.Underline : TextDecoration == "underline",
                SmallCaps = FontVariant is null or "inherit" or "unset" ? inherited.SmallCaps : FontVariant == "small-caps",
                TextAlign = TextAlign is null or "inherit" ? inherited.TextAlign : TextAlign,
                TextIndent = indent,
                TextIndentPercentage = indentPercentage,
                PageBreakBefore = PageBreakBefore ?? false,
                PageBreakAfter = PageBreakAfter ?? false
            };
        }
    }

    private CssProperties ParseProperties(string declarations)
    {
        CssProperties result = default;
        foreach (string declaration in declarations.Split(';'))
        {
            string part = declaration.Trim();
            if (part.Length == 0) continue;
            int colon = part.IndexOf(':');
            if (colon < 0) throw new NotSupportedException("Native EPUB CSS declaration is unsupported.");
            if (part[..colon].Trim().Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                var keyword = part[(colon + 1)..].Trim().ToLowerInvariant() switch
                {
                    "inherit" => CssWideKeyword.Inherit,
                    "initial" => CssWideKeyword.Initial,
                    _ => throw new NotSupportedException("Native EPUB all value is not implemented.")
                };
                result = result.Override(CssProperties.ForKeyword(keyword));
            }
            else if (part[..colon].Trim().Equals("font-style", StringComparison.OrdinalIgnoreCase)
                && part[(colon + 1)..].Trim().ToLowerInvariant() is "inherit" or "initial" or "unset")
            {
                var keyword = part[(colon + 1)..].Trim().Equals("initial", StringComparison.OrdinalIgnoreCase)
                    ? CssWideKeyword.Initial : CssWideKeyword.Inherit;
                result = result.Override(new(null, null)
                    { FontStyle = CssDeclaration<EpubFontStyle>.Marker(keyword) });
            }
            else if (part[..colon].Trim().Equals("background", StringComparison.OrdinalIgnoreCase)
                && part[(colon + 1)..].Trim().Equals("initial", StringComparison.OrdinalIgnoreCase))
                result = result.Override(new(null, null,
                    Background: CssDeclaration<BackgroundColor>.Marker(CssWideKeyword.Initial)));
            else result = result.Override(CssProperties.FromConcrete(ParseConcreteProperties(part)));
        }
        return result;
    }

    private ResolvedProperties ParseConcreteProperties(string declarations)
    {
        string? decoration = null;
        string? visibility = null;
        string? fontVariant = null;
        IndentLength? textIndent = null;
        CssLength? size = null, height = null;
        PdfRgbColor? color = null;
        BackgroundColor? background = null;
        EpubFontFamily[]? families = null;
        MarginLength?[]? margins = null;
        MarginLength?[]? padding = null;
        Border? border = null;
        BorderSide? topBorder = null;
        BoxWidth? width = null;
        BoxHeight? boxHeight = null;
        BoxWidth? maximumWidth = null;
        string? textAlign = null;
        bool? pageBreakBefore = null;
        DisplayKind? block = null;
        bool? pageBreakAfter = null;
        double? fontWeight = null;
        EpubFontStyle? fontStyle = null;
        MarginLength? letterSpacing = null;
        MarginLength? wordSpacing = null;
        foreach (string declaration in declarations.Split(';'))
        {
            string part = declaration.Trim();
            if (part.Length == 0) continue;
            int colon = part.IndexOf(':');
            if (colon < 0) throw new NotSupportedException("Native EPUB CSS declaration is unsupported.");
            string property = part[..colon].Trim().ToLowerInvariant();
            string value = part[(colon + 1)..].Trim();
            if (property == "word-spacing")
            {
                wordSpacing = value.Equals("normal", StringComparison.OrdinalIgnoreCase)
                    ? new MarginLength(0, false) : ParseMarginLength(value);
                if (wordSpacing.Value.Percentage)
                    throw new NotSupportedException("Native EPUB word spacing requires px or em lengths.");
                continue;
            }
            if (property == "letter-spacing")
            {
                letterSpacing = value.Equals("normal", StringComparison.OrdinalIgnoreCase)
                    ? new MarginLength(0, false) : ParseMarginLength(value);
                if (letterSpacing.Value.Percentage)
                    throw new NotSupportedException("Native EPUB letter spacing requires px or em lengths.");
                continue;
            }
            if (property == "font-variant")
            {
                fontVariant = value.ToLowerInvariant();
                if (fontVariant is not ("normal" or "small-caps" or "inherit" or "initial" or "unset"))
                    throw new NotSupportedException("Native EPUB font variants other than normal and small-caps are not implemented.");
                continue;
            }
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
                if (value.Equals("inherit", StringComparison.OrdinalIgnoreCase)
                    || value.Equals("unset", StringComparison.OrdinalIgnoreCase)) fontWeight = 0;
                else if (value.Equals("normal", StringComparison.OrdinalIgnoreCase)
                    || value.Equals("initial", StringComparison.OrdinalIgnoreCase)) fontWeight = 400;
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
                    "block" => DisplayKind.Block,
                    "inline" => DisplayKind.Inline,
                    "none" => DisplayKind.None,
                    "table" => DisplayKind.Table,
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
            if (property == "page-break-after")
            {
                pageBreakAfter = value.ToLowerInvariant() switch
                {
                    "always" => true,
                    "auto" => false,
                    _ => throw new NotSupportedException("Native EPUB page-break-after value is not implemented: " + value + ".")
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
                topBorder = new(new MarginLength(0, false), false, null);
                continue;
            }
            if (property == "border-top")
            {
                topBorder = ParseTopBorder(value);
                continue;
            }
            if (property == "width")
            {
                width = new(value.Equals("auto", StringComparison.OrdinalIgnoreCase) ? null : ParseMarginLength(value));
                continue;
            }
            if (property == "height")
            {
                if (value.Equals("auto", StringComparison.OrdinalIgnoreCase)) boxHeight = new(null);
                else if (value.EndsWith('%')
                    && double.TryParse(value[..^1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double percentage)
                    && double.IsFinite(percentage) && percentage >= 0 && percentage <= 32768)
                    boxHeight = new(percentage);
                else throw new NotSupportedException("Native EPUB CSS height value is not implemented: " + value + ".");
                continue;
            }
            if (property == "max-width")
            {
                maximumWidth = new(value.Equals("none", StringComparison.OrdinalIgnoreCase) ? null : ParseMarginLength(value));
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
                var lengths = sides.Select(side => side.Equals("auto", StringComparison.OrdinalIgnoreCase)
                    ? new MarginLength(0, false, Auto: true) : ParseMarginLength(side)).ToArray();
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
            if (property == "font-style")
            {
                fontStyle = value.ToLowerInvariant() switch
                {
                    "normal" => EpubFontStyle.Normal,
                    "italic" => EpubFontStyle.Italic,
                    _ => throw new NotSupportedException("Native EPUB font-style currently requires normal or italic.")
                };
                continue;
            }
            if (property == "color")
            {
                color = value.Equals("initial", StringComparison.OrdinalIgnoreCase)
                    ? new PdfRgbColor(0, 0, 0) : ParseColor(value);
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
                if (value.Equals("inherit", StringComparison.OrdinalIgnoreCase)
                    || value.Equals("unset", StringComparison.OrdinalIgnoreCase))
                {
                    size = new CssLength(1, true);
                    continue;
                }
                if (value.Equals("initial", StringComparison.OrdinalIgnoreCase))
                {
                    size = new CssLength(DefaultStyle.FontSize, false);
                    continue;
                }
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
            if (property == "text-decoration")
            {
                decoration = value.ToLowerInvariant() switch
                {
                    "underline" or "none" or "inherit" or "initial" or "unset" => value.ToLowerInvariant(),
                    _ => throw new NotSupportedException("Native EPUB text decoration is not implemented: " + value + ".")
                };
                continue;
            }
            if (property == "visibility")
            {
                visibility = value.ToLowerInvariant() switch
                {
                    "visible" or "hidden" or "inherit" or "initial" or "unset" => value.ToLowerInvariant(),
                    _ => throw new NotSupportedException("Native EPUB visibility value is not implemented: " + value + ".")
                };
                continue;
            }
            if (property == "border-spacing")
            {
                if (value is not ("0" or "0px"))
                    throw new NotSupportedException("Native EPUB nonzero table border spacing is not implemented.");
                continue;
            }
            if (!value.EndsWith("px", StringComparison.OrdinalIgnoreCase)
                || !double.TryParse(value[..^2], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double pixels)
                || !double.IsFinite(pixels) || pixels <= 0 || pixels > 32768)
                throw new NotSupportedException("Native EPUB typography requires positive CSS pixel lengths that fit the page.");
            switch (property)
            {
                case "font-size": size = new CssLength(pixels, false); break;
                case "line-height": height = new CssLength(pixels, false); break;
                default: throw new NotSupportedException("Native EPUB CSS property is not implemented: " + property + ".");
            }
        }
        return new ResolvedProperties(size, height, color, background, families, margins, border, padding, textAlign, pageBreakBefore, block, fontWeight, textIndent, fontVariant, topBorder, width, visibility, decoration, maximumWidth, pageBreakAfter) { LetterSpacing = letterSpacing, WordSpacing = wordSpacing, Height = boxHeight, FontStyle = fontStyle };
    }

    private static BorderSide ParseTopBorder(string value)
    {
        _ = ParseBorder(value);
        MarginLength width = new(3, false);
        bool visible = false;
        BackgroundColor? color = null;
        foreach (System.Text.RegularExpressions.Match token in System.Text.RegularExpressions.Regex.Matches(
            value, @"rgb\([^)]*\)|[^\s]+", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            string component = token.Value.ToLowerInvariant();
            if (component is "solid" or "none" or "hidden") visible = component == "solid";
            else if (component is "dotted" or "dashed" or "double" or "groove" or "ridge" or "inset" or "outset")
                throw new NotSupportedException("Native EPUB top border style is not implemented: " + component + ".");
            else if (component is "thin" or "medium" or "thick")
                width = new(component == "thin" ? 1 : component == "thick" ? 5 : 3, false);
            else if (char.IsAsciiDigit(component[0]) || component[0] is '+' or '-' or '.')
                width = ParseMarginLength(component);
            else color = new(component == "transparent" ? null : ParseColor(component));
        }
        return new(width, visible, color);
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
            "black" => 0x000000, "silver" => 0xc0c0c0, "gray" or "grey" => 0x808080, "white" => 0xffffff,
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
