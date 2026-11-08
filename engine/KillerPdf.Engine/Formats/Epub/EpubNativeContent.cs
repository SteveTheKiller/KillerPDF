using System.Globalization;
using System.Xml;
using System.Text;
using KillerPdf.Engine.Authoring;

namespace KillerPdf.Engine.Formats.Epub;

// Source nodes survive normalization so collapsed whitespace keeps its original inline style.
internal sealed class EpubNativeContent(double fallbackSize, double contentHeight, PdfRgbColor? fallbackTextColor = null)
{
    internal enum DisplayKind { Block, Inline, None, Table, TableRowGroup, TableRow, TableCell, ListItem }
    internal readonly record struct BackgroundColor(PdfRgbColor? Color);
    internal sealed record Style(double FontSize, double? LineHeight, PdfRgbColor? Color = null, BackgroundColor? Background = null)
    {
        internal double? LineHeightFactor { get; init; }
        internal EpubFontFamily[]? FontFamilies { get; init; }
        internal double FontWeight { get; init; } = 400;
        internal EpubFontStyle FontStyle { get; init; } = EpubFontStyle.Normal;
        internal bool SmallCaps { get; init; }
        internal bool PreserveWhitespace { get; init; }
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
        internal static bool IsParagraphName(string value) => value is "p" or "pre" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or "hr";
        internal bool IsParagraph => IsParagraphName(Name);
        internal bool HasBlockMargins => IsParagraph || IsGroupingContainer(Name);
        internal Element? Parent { get; } = parent;
        internal Element? TableCellOwner { get; } = parent?.Display == DisplayKind.TableCell ? parent : parent?.TableCellOwner;
        internal Style Style { get; } = style;
        internal DisplayKind Display { get; init; } = block ? DisplayKind.Block : DisplayKind.Inline;
        internal bool IsBlock => Display is DisplayKind.Block or DisplayKind.Table or DisplayKind.TableRowGroup or DisplayKind.TableRow or DisplayKind.TableCell or DisplayKind.ListItem;
        internal bool IsDisplayNone { get; init; }
        internal bool IsFirstElementChild { get; init; }
        internal int ElementChildren { get; set; }
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
    private static readonly CssProperties BlockquoteDefaults = CssProperties.FromConcrete(new ResolvedProperties(null, null,
        Margins: [new(1, true), new(40, false), new(1, true), new(40, false)]));
    private static readonly CssProperties UnorderedListDefaults = CssProperties.FromConcrete(new ResolvedProperties(null, null,
        Margins: [new(1, true), new(0, false), new(1, true), new(0, false)],
        Padding: [new(0, false), new(0, false), new(0, false), new(40, false)]));
    private static readonly CssProperties PreformattedDefaults = CssProperties.FromConcrete(new ResolvedProperties(null, null,
        FontFamilies: [new EpubFontFamily("monospace", true)],
        Margins: [new(1, true), new(0, false), new(1, true), new(0, false)]) { PreserveWhitespace = true });

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

    private const string XhtmlNamespace = "http://www.w3.org/1999/xhtml";

    // HTML elements whose user-agent display is block.
    private static bool IsGroupingContainer(string? name) => name is
        "div" or "header" or "footer" or "main" or "nav" or "section" or "article" or "aside" or "blockquote" or "ul" or "li"
        or "ol" or "dl" or "dt" or "dd" or "figure" or "figcaption" or "address" or "center" or "hgroup" or "details"
        or "summary" or "caption" or "fieldset" or "legend" or "menu" or "dir" or "noscript" or "search";

    // HTML user-agent presentation for phrasing and grouping elements without dedicated layout.
    private static CssProperties? UserAgentDefaults(string name) => name switch
    {
        "sup" or "sub" or "small" => CssProperties.FromConcrete(new ResolvedProperties(new CssLength(1 / 1.2, true), null)),
        "big" => CssProperties.FromConcrete(new ResolvedProperties(new CssLength(1.2, true), null)),
        "u" or "ins" => CssProperties.FromConcrete(new ResolvedProperties(null, null, TextDecoration: "underline")),
        "code" or "kbd" or "samp" or "tt" => CssProperties.FromConcrete(new ResolvedProperties(null, null,
            FontFamilies: [new EpubFontFamily("monospace", true)])),
        "cite" or "dfn" or "var" or "address" => CssProperties.FromConcrete(new ResolvedProperties(null, null) { FontStyle = EpubFontStyle.Italic }),
        "th" => CssProperties.FromConcrete(new ResolvedProperties(null, null, TextAlign: "center", FontWeight: 700)),
        "center" => CssProperties.FromConcrete(new ResolvedProperties(null, null, TextAlign: "center")),
        "dd" => CssProperties.FromConcrete(new ResolvedProperties(null, null, Margins: [null, null, null, new(40, false)])),
        "dl" => CssProperties.FromConcrete(new ResolvedProperties(null, null, Margins: [new(1, true), null, new(1, true), null])),
        "figure" => CssProperties.FromConcrete(new ResolvedProperties(null, null,
            Margins: [new(1, true), new(40, false), new(1, true), new(40, false)])),
        _ => null
    };
    private static bool IsFlowContainer(string? name) => name == "body" || name != "ul" && IsGroupingContainer(name);

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
        // Every element is admitted. Elements the reader has no rendering for flow as inline content,
        // as HTML treats unknown elements, and keep their text; foreign-namespace content does the same.
        if (name == "html" && (parent is not null || xml.Depth != 0))
            throw new InvalidDataException("Native EPUB XHTML document structure is invalid.");
        bool root = xml.Depth == 1 && xml.NamespaceURI == XhtmlNamespace;
        // A nested html, head or body has no document role and flows as an unknown inline element.
        if (name is "html" or "head" or "body" && !(root || name == "html")) name = "span";
        if (name == "head" && _bodySeen && xml.Depth == 1)
            throw new InvalidDataException("Native EPUB head metadata must precede the body.");
        if (name == "body" && xml.Depth == 1) _bodySeen = true;
        bool empty = xml.IsEmptyElement;
        string? classNames = null, inlineStyle = null, widthHint = null;
        var attributes = new Dictionary<(string Namespace, string Name), string>();
        while (xml.MoveToNextAttribute())
        {
            Count(xml.Value.Length);
            attributes[(xml.NamespaceURI, xml.LocalName)] = xml.Value;
            // Only style and class change presentation here; id is captured by the structural reader and
            // every other attribute (epub:type, role, aria-*, dir, lang, title, name, data-*) is inert.
            if (xml.NamespaceURI == "" && xml.LocalName == "style") inlineStyle = xml.Value;
            else if (xml.NamespaceURI == "" && xml.LocalName == "class") classNames = xml.Value;
            else if (xml.NamespaceURI == "" && xml.LocalName == "width" && name == "img") widthHint = xml.Value;
        }
        xml.MoveToElement();
        Style inherited = parent?.Style ?? DefaultStyle;
        if (inherited.Background is not null) inherited = inherited with { Background = null };
        var identity = new SelectorIdentity(xml.NamespaceURI,
            classNames?.Split([' ', '\t', '\r', '\n', '\f'], StringSplitOptions.RemoveEmptyEntries) ?? [], attributes);
        bool firstElementChild = parent is null || parent.ElementChildren == 0;
        CssProperties declarations = ResolveRules(name, identity, firstElementChild);
        if (inlineStyle is not null) declarations = declarations.Override(ParseProperties(inlineStyle));
        if (name == "hr") declarations = HorizontalRuleDefaults.Override(declarations);
        if (name == "blockquote") declarations = BlockquoteDefaults.Override(declarations);
        if (name == "pre") declarations = PreformattedDefaults.Override(declarations);
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
        if (name is "ul" or "ol") declarations = UnorderedListDefaults.Override(declarations);
        if (UserAgentDefaults(name) is { } userAgent) declarations = userAgent.Override(declarations);
        // HTML width on img is a presentational hint: it applies only when no author rule sets width.
        if (widthHint is not null && declarations.Width is null && double.TryParse(widthHint.Trim().TrimEnd('x', 'p'),
                NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double hintPixels) && hintPixels > 0)
            declarations = declarations with { Width = new CssDeclaration<BoxWidth>(new BoxWidth(
                widthHint.Trim().EndsWith('%') ? null : new MarginLength(hintPixels, false))) };
        var properties = ResolveProperties(declarations, parent);
        DisplayKind? tableDisplay = name switch
        {
            "table" => DisplayKind.Table, "tbody" or "thead" or "tfoot" => DisplayKind.TableRowGroup,
            "tr" => DisplayKind.TableRow, "td" or "th" => DisplayKind.TableCell,
            _ => null
        };
        // Capabilities the layout does not implement for this element degrade by dropping the
        // affected property, as a user agent ignores what it cannot apply, instead of refusing the chapter.
        if (tableDisplay is { } required && properties.Display is { } specified
            && specified != DisplayKind.None && specified != required)
            properties = properties with { Display = null };
        bool suppressed = parent?.IsDisplayNone == true || properties.Display == DisplayKind.None;
        // Metadata and template elements in the body are never rendered (HTML display:none defaults).
        if (properties.Display is null && name is "style" or "template" or "title" or "meta" or "link" or "base" or "noembed")
            properties = properties with { Display = DisplayKind.None };
        suppressed = parent?.IsDisplayNone == true || properties.Display == DisplayKind.None;
        if (!suppressed && (name is "ul" or "ol" && properties.Display is not (null or DisplayKind.Block)
            || name == "li" && properties.Display is not (null or DisplayKind.Block or DisplayKind.Inline)))
            properties = properties with { Display = null };
        if (!suppressed)
        {
            if (name == "pre" && properties.Display is not (null or DisplayKind.Block)
                || inherited.PreserveWhitespace && properties.Display is not (null or DisplayKind.Inline))
                properties = properties with { Display = null };
            if (name == "pre" || inherited.PreserveWhitespace) properties = properties with { PreserveWhitespace = true };
            if (tableDisplay is not null and not DisplayKind.Table) properties = properties with { PageBreakBefore = null, PageBreakAfter = null };
            // Supported normal-flow containing blocks all have content-dependent height, so percentages use auto.
            if (name == "html") properties = properties with { Height = null };
            if (name is "img" or "svg" && properties.Margins?.Any(side => side is { Auto: true }) == true)
                properties = properties with { Margins = properties.Margins.Select(side => side is { Auto: true } ? null : side).ToArray() };
            // Visible borders are not painted yet; their space is ignored rather than refusing the chapter.
            if (properties.Border is { Visible: true, Nonzero: true }) properties = properties with { Border = null };
            if (properties.TopBorder is { Visible: true, Width.Value: > 0 }
                && (!paragraph || properties.Display == DisplayKind.Inline))
                properties = properties with { TopBorder = null };
            if (properties.Width is { Length: not null } && name != "img"
                && (!(paragraph || IsGroupingContainer(name) || tableDisplay is DisplayKind.Table or DisplayKind.TableCell) || properties.Display == DisplayKind.Inline))
                properties = properties with { Width = null };
            if (properties.MaximumWidth is { Length: not null } && name != "img")
                properties = properties with { MaximumWidth = null };
            if (name != "body" && properties.Margins?.Any(side => side is { Value: > 0 }) == true
                && (!(paragraph || IsGroupingContainer(name)) || properties.Display == DisplayKind.Inline))
                properties = properties with { Margins = null };
            if (name != "body" && properties.Padding?.Any(side => side is { Value: > 0 }) == true
                && (!paragraph || properties.Display == DisplayKind.Inline)
                && !(name == "ul" && properties.Display != DisplayKind.Inline
                    && properties.Padding[0] is not { Value: > 0 } && properties.Padding[2] is not { Value: > 0 }))
                properties = properties with { Padding = null };
            if (properties.Background is not null && !paragraph && !IsGroupingContainer(name) && !IsFlowContainer(name)
                && name is not ("span" or "strong" or "b"))
                properties = properties with { Background = null };
        }
        Style style = suppressed ? inherited : properties.Apply(inherited, contentHeight);
        if (!suppressed)
        {
            if (name == "body")
            {
                _bodyMargins = properties.Margins;
                _bodyPadding = properties.Padding;
                _bodyFontSize = style.FontSize;
            }
            if (style.Background is { } background && name == "body") CanvasBackground = background.Color;
        }
        bool block = properties.Display switch
        {
            DisplayKind.Block => true,
            DisplayKind.Inline or DisplayKind.None => false,
            _ => paragraph || IsGroupingContainer(name) || IsFlowContainer(name) || tableDisplay is not null
        };
        var element = new Element(name, parent, style, block, Characters.Count, identity)
        {
            Display = properties.Display ?? tableDisplay ?? name switch
            {
                // Only unordered lists draw markers; ordered-list items flow as indented blocks without numbers.
                "li" => parent?.Name == "ul" ? DisplayKind.ListItem : DisplayKind.Block,
                _ => block ? DisplayKind.Block : DisplayKind.Inline
            },
            Margins = suppressed ? null : properties.Margins, Padding = suppressed ? null : properties.Padding,
            TopBorder = suppressed ? null : properties.TopBorder, Width = suppressed ? null : properties.Width,
            MaximumWidth = suppressed ? null : properties.MaximumWidth,
            Border = suppressed ? null : properties.Border, HeightPercentage = suppressed ? null : properties.Height?.Percentage,
            IsDisplayNone = suppressed,
            IsFirstElementChild = firstElementChild
        };
        if (parent is not null) parent.ElementChildren++;
        CurrentElement = element;
        CurrentElementDisplayNone = element.IsDisplayNone;
        parent?.Children.Add(element);
        if (name == "body") Body = element;
        if (paragraph)
        {
            if (++_paragraphCount > 1_000_000)
                throw new InvalidDataException("Native EPUB paragraphs exceed their 1,000,000-event limit.");
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
        if (owner.Style.PreserveWhitespace && value.Contains('\t'))
            throw new NotSupportedException("Native EPUB preformatted tabs are not implemented.");
        if (owner.Style.PreserveWhitespace && value.Contains('\r'))
            throw new NotSupportedException("Native EPUB preformatted literal carriage returns are not implemented.");
        if (owner.Name == "ul" && !IsFormattingWhitespace(value))
            throw new NotSupportedException("Native EPUB unordered-list text requires a list item.");
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
        if (Characters.Count >= MaximumChapterCharacters)
            throw new InvalidDataException("Native EPUB text exceeds its 8,388,608 placement limit.");
        source?.Node.Positions.Add(Characters.Count);
        Characters.Add(source);
    }

    // One chapter can hold no more characters than its 8 MiB resource limit allows, so the per-chapter
    // caps match that bound; the longest corpus chapter (Ulysses, 249 KB of XHTML) is far below it.
    internal const int MaximumChapterCharacters = 8 * 1024 * 1024;

    internal void Trim(int length)
    {
        if (Characters.Count > length) Characters.RemoveRange(length, Characters.Count - length);
        for (int i = 0; i < Inlines.Count; i++)
            if (Inlines[i].Offset > length) Inlines[i] = Inlines[i] with { Offset = length };
    }

    internal void Count(int length)
    {
        _characters += length;
        if (_characters > MaximumChapterCharacters)
            throw new InvalidDataException("Native EPUB XHTML exceeds its character work limit.");
    }

    // Stylesheets are charged once per book against the layout's stylesheet budget, not per chapter text.
    internal void AddStylesheet(string css, CancellationToken cancellationToken)
    {
        css = WithoutComments(css);
        int position = 0;
        // @charset only labels the bytes, which were already decoded; it is skipped in any case or spelling.
        if (css.StartsWith("@charset", StringComparison.OrdinalIgnoreCase) && css.IndexOf(';') is int charsetEnd and >= 0)
            position = charsetEnd + 1;
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
                // A stray closing brace outside any block is ignored (CSS Syntax error recovery).
                if (mediaEnds.TryPeek(out int end) && position == end) mediaEnds.Pop();
                position++;
                continue;
            }
            if (css.AsSpan(position).StartsWith("@media", StringComparison.OrdinalIgnoreCase))
            {
                int mediaOpen = css.IndexOf('{', position + 6);
                if (mediaOpen < 0) break;
                if (!MediaMatches(css[(position + 6)..mediaOpen]))
                {
                    // Print, speech and reader-specific (amzn-kf8, amzn-mobi) media do not apply to this screen reader.
                    position = MediaEnd(mediaOpen) + 1;
                    continue;
                }
                if (mediaEnds.Count >= 16)
                    throw new InvalidDataException("Native EPUB CSS media nesting exceeds its 16-level limit.");
                int mediaEnd = MediaEnd(mediaOpen);
                if (mediaEnds.TryPeek(out int parentEnd) && mediaEnd >= parentEnd) mediaEnd = parentEnd;
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
            if (css[position] == '@')
            {
                // Unknown, print-only and font at-rules (@page, @font-face, @import, @supports, ...) are skipped
                // with their block or statement, as CSS Syntax requires.
                position = SkipAtRule(position);
                continue;
            }
            styleSeen = true;
            int open = css.IndexOf('{', position);
            int limit = mediaEnds.TryPeek(out int enclosing) ? enclosing : css.Length;
            if (open < 0 || open >= limit) break;
            int close = css.IndexOf('}', open + 1);
            // An unclosed block ends at the end of its stylesheet or enclosing block.
            if (close < 0 || close > limit) close = limit;
            CssProperties properties = ParseProperties(css[(open + 1)..close]);
            var parsedRules = new List<CssRule>();
            try
            {
            foreach (string group in css[position..open].Split(','))
            {
                if (++_selectorCount > 65_536)
                    throw new InvalidDataException("Native EPUB CSS selectors exceed their 65,536-selector limit.");
                string fullSelector = group.Trim(' ', '\t', '\r', '\n', '\f');
                if (fullSelector.Length is 0 or > 128 || fullSelector.Contains("::") || fullSelector.Contains(":first-letter")
                    || fullSelector.Contains(":first-line") || fullSelector.Contains(":before") || fullSelector.Contains(":after"))
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
                    bool firstChild = selector.EndsWith(":first-child", StringComparison.OrdinalIgnoreCase);
                    if (firstChild) selector = selector[..^12];
                    // The universal selector matches any element; *.class equals .class.
                    // *|type keeps its namespace meaning and is handled with the type below.
                    bool universal = selector.StartsWith('*') && !selector.StartsWith("*|", StringComparison.Ordinal);
                    if (universal) selector = selector[1..];
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
                    bool classOnly = selector.StartsWith(".", StringComparison.Ordinal) || selector.Length == 0 && (ids.Count > 0 || link || visited || hover || universal || firstChild);
                    if (classOnly && attribute is not null && !universal || selector.Length > 128 || selector.Length == 0 && ids.Count == 0 && !link && !visited && !hover && !universal && !firstChild || (classOnly ? parts.Skip(1) : parts).Any(part => part.Length == 0
                        || !char.IsAsciiLetter(part[0])
                        || part.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_'))))
                        throw new NotSupportedException("Native EPUB CSS selector is not implemented.");
                    chain.Add(new(namespaceName, parts[1..], attribute, properties, _selectorCount, classOnly ? 0 : 1, type, []) { Ids = ids.ToArray(), Link = link, Visited = visited, Hover = hover, DirectParent = directParent, FirstChild = firstChild });
                    directParent = false;
                }
                if (directParent || chain.Count == 0)
                    throw new NotSupportedException("Native EPUB CSS child selectors require selectors on both sides.");
                var target = chain[^1] with { Ancestors = chain.Take(chain.Count - 1).Reverse().ToArray() };
                // Index by the rightmost compound's most selective key so each element visits only its candidates.
                parsedRules.Add(target);
            }
            }
            catch (NotSupportedException)
            {
                // CSS Selectors: a rule whose selector list has an unsupported or invalid selector is dropped whole.
                parsedRules.Clear();
                IgnoredDeclarations++;
            }
            foreach (var target in parsedRules)
            {
                string key = target.Ids.Length > 0 ? "#" + target.Ids[0] : target.Classes.Length > 0 ? "." + target.Classes[0] : target.Type;
                if (!_typeRules.TryGetValue(key, out var rules))
                    _typeRules.Add(key, rules = new());
                rules.Add(target);
            }
            position = close + 1;
        }
        // Blocks still open at the end of the sheet are closed there (CSS Syntax).

        int SkipAtRule(int start)
        {
            for (int at = start; at < css.Length; at++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++mediaWork > 16_000_000)
                    throw new InvalidDataException("Native EPUB CSS media scanning exceeds its work limit.");
                if (css[at] == ';') return at + 1;
                if (css[at] == '{') return MediaEnd(at) + 1;
                if (css[at] == '}') return at;
            }
            return css.Length;
        }

        int MediaEnd(int open)
        {
            int depth = 1;
            char quote = '\0';
            for (int at = open + 1; at < css.Length; at++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++mediaWork > 16_000_000)
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
            return css.Length;
        }

        void SkipSpace() { while (position < css.Length && IsCssSpace(css[position])) position++; }
    }

    private static bool IsCssSpace(char value) => value is ' ' or '\t' or '\r' or '\n' or '\f';

    // Media Queries 4: a list matches when any query does; "only" is ignored, "not" negates the type,
    // and features are assumed to match a reflowable screen page.
    private static bool MediaMatches(string queries)
    {
        foreach (string query in queries.Split(','))
        {
            string[] words = query.Trim().ToLowerInvariant().Split([' ', '\t', '\r', '\n', '\f'], StringSplitOptions.RemoveEmptyEntries);
            int at = 0;
            bool negated = false;
            if (at < words.Length && words[at] is "only") at++;
            if (at < words.Length && words[at] is "not") { negated = true; at++; }
            string type = at < words.Length && !words[at].StartsWith('(') ? words[at] : "all";
            bool matches = type is "all" or "screen";
            if (matches != negated) return true;
        }
        return false;
    }

    // Comments are removed outside strings before tokenizing; each becomes one space.
    private static string WithoutComments(string css)
    {
        if (!css.Contains("/*")) return css;
        var result = new StringBuilder(css.Length);
        char quote = '\0';
        for (int at = 0; at < css.Length; at++)
        {
            char value = css[at];
            if (quote != '\0')
            {
                result.Append(value);
                if (value == '\\' && at + 1 < css.Length) result.Append(css[++at]);
                else if (value == quote) quote = '\0';
            }
            else if (value is '"' or '\'') { quote = value; result.Append(value); }
            else if (value == '/' && at + 1 < css.Length && css[at + 1] == '*')
            {
                int end = css.IndexOf("*/", at + 2, StringComparison.Ordinal);
                at = end < 0 ? css.Length : end + 1;
                result.Append(' ');
            }
            else result.Append(value);
        }
        return result.ToString();
    }
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
        internal bool FirstChild { get; init; }
        internal string[] Ids { get; init; } = [];
        private int Own => Ids.Length * 65536 + (Classes.Length + (Attribute is null ? 0 : 1) + (Link || Visited || Hover ? 1 : 0)
            + (FirstChild ? 1 : 0)) * 256 + TypeSpecificity;
        internal int Specificity => Own + Ancestors.Sum(rule => rule.Own);
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

    private bool Matches(CssRule rule, string name, SelectorIdentity identity, bool firstElementChild = false)
    {
        if (rule.FirstChild && !firstElementChild) return false;
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
            bool matched = Matches(required, current.Name, current.Identity, current.IsFirstElementChild) && Match(index + 1, current.Parent);
            if (!matched && !direct) matched = Match(index, current.Parent);
            memo.Add(key, matched);
            return matched;
        }
    }

    private CssProperties ResolveRules(string name, SelectorIdentity identity, bool firstElementChild = false)
    {
        var candidates = new List<CssRule>();
        if (_typeRules.TryGetValue(name, out var typed)) candidates.AddRange(typed);
        if (_typeRules.TryGetValue("", out var universal)) candidates.AddRange(universal);
        foreach (string className in identity.Classes.Distinct(StringComparer.Ordinal))
            if (_typeRules.TryGetValue("." + className, out var classed)) candidates.AddRange(classed);
        if (identity.Attributes.TryGetValue(("", "id"), out string? elementId) && _typeRules.TryGetValue("#" + elementId, out var identified))
            candidates.AddRange(identified);
        if (candidates.Count == 0) return default;
        candidates.Sort((left, right) => left.Order.CompareTo(right.Order));
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
        foreach (var rule in candidates)
        {
            if (!Matches(rule, name, identity, firstElementChild)) continue;
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
        // Rules are indexed by their rightmost id, class or type, so this bounds candidate checks, not rules times elements.
        if (amount > 5_000_000 - _selectorWork)
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
        internal CssDeclaration<bool>? PreserveWhitespace { get; init; }
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
        { LetterSpacing = later.LetterSpacing ?? LetterSpacing, WordSpacing = later.WordSpacing ?? WordSpacing, Height = later.Height ?? Height, FontStyle = later.FontStyle ?? FontStyle, PreserveWhitespace = later.PreserveWhitespace ?? PreserveWhitespace };
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
        { LetterSpacing = value.LetterSpacing is { } vLetterSpacing ? new CssDeclaration<MarginLength>(vLetterSpacing) : null, WordSpacing = value.WordSpacing is { } vWordSpacing ? new CssDeclaration<MarginLength>(vWordSpacing) : null, Height = value.Height is { } vHeight ? new CssDeclaration<BoxHeight>(vHeight) : null, FontStyle = value.FontStyle is { } vStyle ? new CssDeclaration<EpubFontStyle>(vStyle) : null, PreserveWhitespace = value.PreserveWhitespace is { } preserve ? new CssDeclaration<bool>(preserve) : null };
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
        { LetterSpacing = CssDeclaration<MarginLength>.Marker(keyword), WordSpacing = CssDeclaration<MarginLength>.Marker(keyword), Height = CssDeclaration<BoxHeight>.Marker(keyword), FontStyle = CssDeclaration<EpubFontStyle>.Marker(keyword), PreserveWhitespace = CssDeclaration<bool>.Marker(keyword) };
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
            PreserveWhitespace = declarations.PreserveWhitespace?.Resolve(false, parent?.Style.PreserveWhitespace ?? false),
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
        internal bool? PreserveWhitespace { get; init; }
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
                inline.PageBreakAfter ?? PageBreakAfter) { LetterSpacing = inline.LetterSpacing ?? LetterSpacing, WordSpacing = inline.WordSpacing ?? WordSpacing, Height = inline.Height ?? Height, FontStyle = inline.FontStyle ?? FontStyle, PreserveWhitespace = inline.PreserveWhitespace ?? PreserveWhitespace };

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
                // CSS Fonts 4 relative weight table.
                FontWeight = FontWeight switch
                {
                    0 or null => inherited.FontWeight,
                    BolderWeight => inherited.FontWeight switch { < 350 => 400, < 550 => 700, < 900 => 900, var same => same },
                    LighterWeight => inherited.FontWeight switch { < 100 => inherited.FontWeight, < 550 => 100, < 750 => 400, _ => 700 },
                    { } weight => weight
                },
                FontStyle = FontStyle ?? inherited.FontStyle,
                PreserveWhitespace = PreserveWhitespace ?? inherited.PreserveWhitespace,
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
        foreach (string declaration in SplitDeclarations(declarations))
        {
            string part = WithoutImportant(declaration.Trim());
            if (part.Length == 0) continue;
            int colon = part.IndexOf(':');
            // CSS Syntax: a malformed, unknown or unsupported declaration is ignored and parsing continues.
            if (colon < 0) continue;
            try { result = ParseDeclaration(result, part, colon); }
            catch (NotSupportedException) { IgnoredDeclarations++; }
        }
        return result;
    }

    private const double BolderWeight = -1, LighterWeight = -2;

    /// <summary>Counts declarations dropped because they are invalid or not implemented.</summary>
    internal int IgnoredDeclarations { get; private set; }

    // Splits on semicolons outside strings, parentheses and brackets so url(data:...;base64,...) stays whole.
    private static IEnumerable<string> SplitDeclarations(string declarations)
    {
        int start = 0, depth = 0;
        char quote = '\0';
        for (int index = 0; index < declarations.Length; index++)
        {
            char value = declarations[index];
            if (quote != '\0')
            {
                if (value == '\\') index++;
                else if (value == quote) quote = '\0';
            }
            else if (value is '"' or '\'') quote = value;
            else if (value is '(' or '[') depth++;
            else if (value is ')' or ']' && depth > 0) depth--;
            else if (value == ';' && depth == 0)
            {
                yield return declarations[start..index];
                start = index + 1;
            }
        }
        if (start < declarations.Length) yield return declarations[start..];
    }

    // !important raises cascade priority; it is accepted and applied at normal priority.
    private static string WithoutImportant(string declaration)
    {
        int bang = declaration.LastIndexOf('!');
        return bang >= 0 && declaration[(bang + 1)..].Trim().Equals("important", StringComparison.OrdinalIgnoreCase)
            ? declaration[..bang].TrimEnd() : declaration;
    }

    private CssProperties ParseDeclaration(CssProperties result, string part, int colon)
    {
        {
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
            else if (part[..colon].Trim().ToLowerInvariant() is "margin" or "margin-top" or "margin-right" or "margin-bottom" or "margin-left"
                && part[(colon + 1)..].Trim().ToLowerInvariant() is "initial" or "unset")
            {
                string property = part[..colon].Trim().ToLowerInvariant();
                var margins = new CssDeclaration<MarginLength>?[4];
                var reset = CssDeclaration<MarginLength>.Marker(CssWideKeyword.Initial);
                if (property == "margin") Array.Fill(margins, reset);
                else margins[property switch { "margin-top" => 0, "margin-right" => 1, "margin-bottom" => 2, _ => 3 }] = reset;
                result = result.Override(new(null, null, Margins: margins));
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
        foreach (string declaration in new[] { declarations })
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
                if (!TryLength(value, out double indent, out bool em, out bool percentage) || Math.Abs(indent) > 32768)
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
                // Relative weights resolve against the inherited weight when the style is applied.
                else if (value.Equals("bolder", StringComparison.OrdinalIgnoreCase)) fontWeight = BolderWeight;
                else if (value.Equals("lighter", StringComparison.OrdinalIgnoreCase)) fontWeight = LighterWeight;
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
                    // Atomic inline boxes flow as inline content; list items without markers flow as blocks.
                    "inline-block" => DisplayKind.Inline,
                    "list-item" => DisplayKind.Block,
                    _ => throw new NotSupportedException("Native EPUB display value is not implemented: " + value + ".")
                };
                continue;
            }
            if (property is "page-break-before" or "break-before")
            {
                // avoid is a hint the paginator may ignore; it falls through as an ignored value.
                pageBreakBefore = value.ToLowerInvariant() switch
                {
                    "always" or "page" or "left" or "right" or "recto" or "verso" => true,
                    "auto" => false,
                    _ => throw new NotSupportedException("Native EPUB page-break-before value is not implemented: " + value + ".")
                };
                continue;
            }
            if (property is "page-break-after" or "break-after")
            {
                pageBreakAfter = value.ToLowerInvariant() switch
                {
                    "always" or "page" or "left" or "right" or "recto" or "verso" => true,
                    "auto" => false,
                    _ => throw new NotSupportedException("Native EPUB page-break-after value is not implemented: " + value + ".")
                };
                continue;
            }
            if (property == "text-align")
            {
                textAlign = value.ToLowerInvariant() switch
                {
                    // Left-to-right content maps start to left and end to right.
                    "left" or "start" => "left", "center" => "center", "right" or "end" => "right", "justify" => "justify",
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
                    // Oblique uses the italic face, as user agents do when no oblique face exists.
                    "italic" or "oblique" => EpubFontStyle.Italic,
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
                if (string.Equals(value, "transparent", StringComparison.OrdinalIgnoreCase))
                    background = new BackgroundColor(null);
                else if (TryParseColor(value, out var fill, out double alpha))
                    background = new BackgroundColor(alpha == 0 ? null : fill);
                else throw new NotSupportedException("Native EPUB CSS color is not implemented.");
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
            if (property == "line-height" && value.Equals("normal", StringComparison.OrdinalIgnoreCase))
            {
                height = new CssLength(0, false, Normal: true);
                continue;
            }
            if (property == "line-height" && double.TryParse(value, NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture, out double factor) && double.IsFinite(factor) && factor > 0)
            {
                height = new CssLength(factor, true, Unitless: true);
                continue;
            }
            if (property is "font-size" or "line-height")
            {
                if (!TryLength(value, out double number, out bool relativeEm, out bool percentage) || number <= 0
                    || !relativeEm && !percentage && number > 32768)
                    throw new NotSupportedException("Native EPUB typography requires positive CSS lengths that fit the page.");
                var length = relativeEm ? new CssLength(number, true)
                    : percentage ? new CssLength(number / 100, true) : new CssLength(number, false);
                if (property == "font-size") size = length;
                else height = length;
                continue;
            }
            if (property == "background")
            {
                // The shorthand resets the color to transparent unless a color component is present; images are not painted.
                PdfRgbColor? fill = null;
                foreach (System.Text.RegularExpressions.Match token in System.Text.RegularExpressions.Regex.Matches(
                    value, @"(?:rgba?|hsla?|url)\([^)]*\)|[^\s]+", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    if (TryParseColor(token.Value, out var candidate, out double alpha)) fill = alpha == 0 ? null : candidate;
                background = new BackgroundColor(fill);
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
            throw new NotSupportedException("Native EPUB CSS property is not implemented: " + property + ".");
        }
        return new ResolvedProperties(size, height, color, background, families, margins, border, padding, textAlign, pageBreakBefore, block, fontWeight, textIndent, fontVariant, topBorder, width, visibility, decoration, maximumWidth, pageBreakAfter) { LetterSpacing = letterSpacing, WordSpacing = wordSpacing, Height = boxHeight, FontStyle = fontStyle };
    }

    private BorderSide ParseTopBorder(string value)
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

    private Border ParseBorder(string value)
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

    private MarginLength ParseMarginLength(string value)
    {
        if (!TryLength(value, out double length, out bool em, out bool percentage) || length < 0)
            throw new NotSupportedException("Native EPUB body margins require supported nonnegative CSS lengths; auto and negative margins are not implemented.");
        return new(length, em, percentage);
    }

    /// <summary>
    /// Parses a CSS length. Absolute units resolve to CSS pixels at 96 per inch (CSS Values 4);
    /// em stays relative, ex is 0.5em because x-height is not measured, and rem uses the fallback
    /// root size because the root element's font size is the reading size here.
    /// </summary>
    private bool TryLength(string value, out double number, out bool em, out bool percentage)
    {
        em = percentage = false;
        number = 0;
        value = value.Trim();
        int end = value.Length;
        while (end > 0 && (char.IsAsciiLetter(value[end - 1]) || value[end - 1] == '%')) end--;
        string unit = value[end..].ToLowerInvariant(), digits = value[..end];
        if (digits.Length == 0 || digits.EndsWith('.') || !double.TryParse(digits,
                NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double parsed)
            || !double.IsFinite(parsed)) return false;
        double? pixels = unit switch
        {
            "px" => 1, "pt" => 96d / 72, "pc" => 16, "in" => 96, "cm" => 96 / 2.54, "mm" => 9.6 / 2.54, "q" => 2.4 / 2.54,
            "rem" => DefaultStyle.FontSize, "" when parsed == 0 => 0, _ => null
        };
        if (unit is "em" or "ex")
        {
            em = true;
            number = unit == "ex" ? parsed / 2 : parsed;
            return true;
        }
        if (unit == "%")
        {
            percentage = true;
            number = parsed;
            return true;
        }
        if (pixels is not { } scale) return false;
        number = parsed * scale;
        return double.IsFinite(number);
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

    // Text and borders have no compositing here, so a partly transparent color paints opaque;
    // a background with zero alpha is transparent.
    private static PdfRgbColor ParseColor(string value) => TryParseColor(value, out var color, out double alpha) && alpha > 0
        ? color : throw new NotSupportedException("Native EPUB CSS color is not implemented.");

    /// <summary>Parses CSS Color 4 hex, named, rgb(a) and hsl(a) colors, returning the opaque color and its alpha.</summary>
    private static bool TryParseColor(string value, out PdfRgbColor color, out double alpha)
    {
        color = default;
        alpha = 1;
        value = value.Trim();
        if (value.Length == 0) return false;
        if (value[0] == '#')
        {
            string hex = value[1..];
            if (hex.Length is not (3 or 4 or 6 or 8) || !uint.TryParse(hex, NumberStyles.AllowHexSpecifier,
                    CultureInfo.InvariantCulture, out uint packed)) return false;
            double Digit(int index, int width) => Convert.ToInt32(width == 1 ? new string(hex[index], 2) : hex.Substring(index, 2), 16) / 255d;
            int step = hex.Length <= 4 ? 1 : 2;
            color = new(Digit(0, step), Digit(step, step), Digit(2 * step, step));
            if (hex.Length is 4 or 8) alpha = Digit(3 * step, step);
            return true;
        }
        int open = value.IndexOf('(');
        if (open > 0 && value.EndsWith(')') && value.IndexOf(')') == value.Length - 1)
        {
            string function = value[..open].ToLowerInvariant();
            string inner = value[(open + 1)..^1].Trim();
            string[] parts;
            if (inner.Contains(','))
            {
                // Legacy syntax: comma-separated components, an optional comma alpha.
                parts = inner.Split(',').Select(part => part.Trim()).ToArray();
                if (parts.Any(part => part.Length == 0 || part.Any(IsCssSpace) || part.Contains('/'))) return false;
            }
            else
            {
                // Modern syntax: space-separated components, an optional slash alpha.
                string[] halves = inner.Split('/');
                if (halves.Length > 2) return false;
                parts = halves[0].Split([' ', '\t', '\r', '\n', '\f'], StringSplitOptions.RemoveEmptyEntries);
                if (halves.Length == 2)
                {
                    string[] alphaPart = halves[1].Split([' ', '\t', '\r', '\n', '\f'], StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length != 3 || alphaPart.Length != 1) return false;
                    parts = [.. parts, alphaPart[0]];
                }
            }
            if (parts.Length is not (3 or 4) || parts.Any(part => !CssNumber().IsMatch(part) && !(part == parts[0] && function.StartsWith("hsl")))) return false;
            if (parts.Length == 4 && !TryComponent(parts[3], 1, out alpha)) return false;
            alpha = Math.Clamp(alpha, 0, 1);
            if (function is "rgb" or "rgba")
            {
                if (parts.Take(3).Select(part => part.EndsWith('%')).Distinct().Count() != 1) return false;
                if (!TryComponent(parts[0], 255, out double r) || !TryComponent(parts[1], 255, out double g)
                    || !TryComponent(parts[2], 255, out double b)) return false;
                color = new(Math.Clamp(r, 0, 1), Math.Clamp(g, 0, 1), Math.Clamp(b, 0, 1));
                return true;
            }
            if (function is "hsl" or "hsla")
            {
                string hueText = parts[0].ToLowerInvariant().EndsWith("deg") ? parts[0][..^3] : parts[0];
                if (!double.TryParse(hueText, NumberStyles.Float, CultureInfo.InvariantCulture, out double hue)
                    || !parts[1].EndsWith('%') || !parts[2].EndsWith('%')
                    || !TryComponent(parts[1], 1, out double saturation) || !TryComponent(parts[2], 1, out double lightness)
                    || !double.IsFinite(hue)) return false;
                saturation = Math.Clamp(saturation, 0, 1);
                lightness = Math.Clamp(lightness, 0, 1);
                hue = ((hue % 360) + 360) % 360;
                // CSS Color 4 hsl-to-rgb.
                double Channel(double n)
                {
                    double k = (n + hue / 30) % 12;
                    return lightness - saturation * Math.Min(lightness, 1 - lightness) * Math.Max(-1, Math.Min(Math.Min(k - 3, 9 - k), 1));
                }
                color = new(Channel(0), Channel(8), Channel(4));
                return true;
            }
            return false;
        }
        if (value.Equals("transparent", StringComparison.OrdinalIgnoreCase))
        {
            alpha = 0;
            return true;
        }
        if (!NamedColors.Value.TryGetValue(value, out int rgb)) return false;
        color = new((rgb >> 16) / 255d, ((rgb >> 8) & 255) / 255d, (rgb & 255) / 255d);
        return true;

        static bool TryComponent(string text, double scale, out double result)
        {
            bool percent = text.EndsWith('%');
            result = 0;
            if (!CssNumber().IsMatch(text) || !double.TryParse(percent ? text[..^1] : text, NumberStyles.Float,
                CultureInfo.InvariantCulture, out double number) || !double.IsFinite(number)) return false;
            result = percent ? number / 100 : number / scale;
            return true;
        }
    }

    // CSS Syntax number token with an optional percent sign.
    private static readonly System.Text.RegularExpressions.Regex CssNumberPattern = new(
        @"^[+-]?(?:[0-9]+(?:\.[0-9]+)?|\.[0-9]+)(?:[eE][+-]?[0-9]+)?%?$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private static System.Text.RegularExpressions.Regex CssNumber() => CssNumberPattern;

    // CSS Color 4 named colors.
    private static readonly Lazy<Dictionary<string, int>> NamedColors = new(() =>
    {
        const string table = "aliceblue f0f8ff antiquewhite faebd7 aqua 00ffff aquamarine 7fffd4 azure f0ffff beige f5f5dc bisque ffe4c4 black 000000 blanchedalmond ffebcd blue 0000ff blueviolet 8a2be2 brown a52a2a burlywood deb887 cadetblue 5f9ea0 chartreuse 7fff00 chocolate d2691e coral ff7f50 cornflowerblue 6495ed cornsilk fff8dc crimson dc143c cyan 00ffff darkblue 00008b darkcyan 008b8b darkgoldenrod b8860b darkgray a9a9a9 darkgreen 006400 darkgrey a9a9a9 darkkhaki bdb76b darkmagenta 8b008b darkolivegreen 556b2f darkorange ff8c00 darkorchid 9932cc darkred 8b0000 darksalmon e9967a darkseagreen 8fbc8f darkslateblue 483d8b darkslategray 2f4f4f darkslategrey 2f4f4f darkturquoise 00ced1 darkviolet 9400d3 deeppink ff1493 deepskyblue 00bfff dimgray 696969 dimgrey 696969 dodgerblue 1e90ff firebrick b22222 floralwhite fffaf0 forestgreen 228b22 fuchsia ff00ff gainsboro dcdcdc ghostwhite f8f8ff gold ffd700 goldenrod daa520 gray 808080 green 008000 greenyellow adff2f grey 808080 honeydew f0fff0 hotpink ff69b4 indianred cd5c5c indigo 4b0082 ivory fffff0 khaki f0e68c lavender e6e6fa lavenderblush fff0f5 lawngreen 7cfc00 lemonchiffon fffacd lightblue add8e6 lightcoral f08080 lightcyan e0ffff lightgoldenrodyellow fafad2 lightgray d3d3d3 lightgreen 90ee90 lightgrey d3d3d3 lightpink ffb6c1 lightsalmon ffa07a lightseagreen 20b2aa lightskyblue 87cefa lightslategray 778899 lightslategrey 778899 lightsteelblue b0c4de lightyellow ffffe0 lime 00ff00 limegreen 32cd32 linen faf0e6 magenta ff00ff maroon 800000 mediumaquamarine 66cdaa mediumblue 0000cd mediumorchid ba55d3 mediumpurple 9370db mediumseagreen 3cb371 mediumslateblue 7b68ee mediumspringgreen 00fa9a mediumturquoise 48d1cc mediumvioletred c71585 midnightblue 191970 mintcream f5fffa mistyrose ffe4e1 moccasin ffe4b5 navajowhite ffdead navy 000080 oldlace fdf5e6 olive 808000 olivedrab 6b8e23 orange ffa500 orangered ff4500 orchid da70d6 palegoldenrod eee8aa palegreen 98fb98 paleturquoise afeeee palevioletred db7093 papayawhip ffefd5 peachpuff ffdab9 peru cd853f pink ffc0cb plum dda0dd powderblue b0e0e6 purple 800080 rebeccapurple 663399 red ff0000 rosybrown bc8f8f royalblue 4169e1 saddlebrown 8b4513 salmon fa8072 sandybrown f4a460 seagreen 2e8b57 seashell fff5ee sienna a0522d silver c0c0c0 skyblue 87ceeb slateblue 6a5acd slategray 708090 slategrey 708090 snow fffafa springgreen 00ff7f steelblue 4682b4 tan d2b48c teal 008080 thistle d8bfd8 tomato ff6347 turquoise 40e0d0 violet ee82ee wheat f5deb3 white ffffff whitesmoke f5f5f5 yellow ffff00 yellowgreen 9acd32";
        string[] words = table.Split(' ');
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < words.Length; index += 2)
            result.Add(words[index], Convert.ToInt32(words[index + 1], 16));
        return result;
    });
}
