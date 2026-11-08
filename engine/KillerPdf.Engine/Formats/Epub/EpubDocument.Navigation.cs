using System.Xml.Linq;

namespace KillerPdf.Engine.Formats.Epub;

public sealed partial class EpubDocument
{
    private readonly string? _navigationTocId;
    /// <summary>Reads the declared publication table of contents without fetching external resources.</summary>
    public IReadOnlyList<EpubNavigationEntry> ReadNavigation(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Version == "2.0") return ReadNcx(cancellationToken);
        // EPUB 3 books usually carry an NCX for older readers; it stands in when the navigation document cannot be read.
        try { return ReadNavigationDocument(cancellationToken); }
        catch (InvalidDataException) when (_navigationTocId is not null)
        {
            return ReadNcx(cancellationToken);
        }
    }

    private IReadOnlyList<EpubNavigationEntry> ReadNavigationDocument(CancellationToken cancellationToken)
    {
        var declarations = Manifest.Where(x => Tokens(x.Properties).Contains("nav", StringComparer.Ordinal)).Take(2).ToArray();
        if (declarations.Length == 0)
            return _navigationTocId is null ? Array.Empty<EpubNavigationEntry>() : ReadNcx(cancellationToken);
        if (declarations.Length != 1 || declarations[0].MediaType != "application/xhtml+xml")
            throw new InvalidDataException("Invalid EPUB navigation declaration.");
        var declaration = declarations[0];
        if (Tokens(declaration.Properties).Contains("scripted", StringComparer.Ordinal))
            throw new NotSupportedException("Scripted EPUB navigation is unsupported.");
        XNamespace html = "http://www.w3.org/1999/xhtml";
        XNamespace epub = "http://www.idpf.org/2007/ops";
        var document = Xml(XhtmlWithoutCanonicalDeclaration(_resources[declaration.Href]));
        cancellationToken.ThrowIfCancellationRequested();
        if (document.Root?.Name != html + "html") throw new InvalidDataException("Invalid EPUB navigation document.");
        foreach (var element in document.Descendants())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (element.Name.LocalName == "script" || element.Attributes().Any(attribute =>
                    attribute.Name.LocalName.StartsWith("on", StringComparison.OrdinalIgnoreCase)))
                throw new NotSupportedException("Scripted EPUB navigation is unsupported.");
        }
        var navs = document.Descendants(html + "nav").Where(x => Tokens((string?)x.Attribute(epub + "type") ?? "").Contains("toc", StringComparer.Ordinal)).Take(2).ToArray();
        if (navs.Length != 1) throw new InvalidDataException("EPUB navigation requires one table of contents.");
        var lists = navs[0].Elements(html + "ol").Take(2).ToArray();
        if (lists.Length != 1) throw new InvalidDataException("Missing EPUB navigation list.");
        int nodeCount = 0;
        return ReadList(lists[0], 1);

        IReadOnlyList<EpubNavigationEntry> ReadList(XElement? list, int depth)
        {
            var result = new List<EpubNavigationEntry>();
            if (list is not null && !list.Elements(html + "li").Any())
                throw new InvalidDataException("Empty EPUB navigation list.");
            foreach (var item in list?.Elements(html + "li") ?? [])
            {
                cancellationToken.ThrowIfCancellationRequested();
                // The Complete Works of Shakespeare lists every act and scene (over 1,024 entries); 16,384 bounds work.
                if (++nodeCount > 16_384) throw new InvalidDataException("EPUB navigation exceeds the node limit.");
                if (depth > 32) throw new InvalidDataException("EPUB navigation exceeds the depth limit.");
                var link = item.Element(html + "a");
                if (item.Elements().Count(x => x.Name == html + "a" || x.Name == html + "span") != 1 ||
                    item.Elements(html + "ol").Take(2).Count() > 1 || (link is null && item.Element(html + "ol") is null))
                    throw new InvalidDataException("Invalid EPUB navigation list item.");
                var heading = link ?? item.Element(html + "span") ?? throw new InvalidDataException("Missing EPUB navigation label.");
                var label = NavigationLabel(heading);
                if (label.Length == 0) throw new InvalidDataException("Empty EPUB navigation label.");
                // An entry whose file is not in the book stays as an unlinked heading so its children keep their place.
                string? target = link is null || (string?)link.Attribute("href") is not { } href ? null : TryNavigationTarget(declaration.Href, href);
                result.Add(new EpubNavigationEntry(label, target ?? "", ReadList(item.Element(html + "ol"), depth + 1)));
            }
            return result.AsReadOnly();
        }
    }

    private IReadOnlyList<EpubNavigationEntry> ReadNcx(CancellationToken cancellationToken)
    {
        if (_navigationTocId is null) return Array.Empty<EpubNavigationEntry>();
        var declaration = Manifest.FirstOrDefault(x => x.Id == _navigationTocId);
        if (declaration is null || declaration.MediaType != "application/x-dtbncx+xml")
            throw new InvalidDataException("Invalid EPUB NCX declaration.");
        XNamespace ncx = "http://www.daisy.org/z3986/2005/ncx/";
        var document = NcxXml(_resources[declaration.Href]);
        cancellationToken.ThrowIfCancellationRequested();
        var map = document.Root?.Element(ncx + "navMap");
        if (document.Root?.Name != ncx + "ncx" || map is null || !map.Elements(ncx + "navPoint").Any())
            throw new InvalidDataException("Missing EPUB NCX navigation map.");
        int nodeCount = 0;
        return ReadPoints(map, 1);

        IReadOnlyList<EpubNavigationEntry> ReadPoints(XElement? parent, int depth)
        {
            var result = new List<EpubNavigationEntry>();
            foreach (var point in parent?.Elements(ncx + "navPoint") ?? [])
            {
                cancellationToken.ThrowIfCancellationRequested();
                // The Complete Works of Shakespeare lists every act and scene (over 1,024 entries); 16,384 bounds work.
                if (++nodeCount > 16_384) throw new InvalidDataException("EPUB navigation exceeds the node limit.");
                if (depth > 32) throw new InvalidDataException("EPUB navigation exceeds the depth limit.");
                var label = string.Join(' ', Tokens(point.Element(ncx + "navLabel")?.Element(ncx + "text")?.Value ?? ""));
                if (label.Length == 0) throw new InvalidDataException("Empty EPUB navigation label.");
                var content = point.Element(ncx + "content") ?? throw new InvalidDataException("Missing EPUB NCX target.");
                result.Add(new EpubNavigationEntry(label, TryNavigationTarget(declaration.Href,
                    Required(content, "src")) ?? "", ReadPoints(point, depth + 1)));
            }
            return result.AsReadOnly();
        }
    }

    private static string[] Tokens(string value) => value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    private static string NavigationLabel(XElement heading)
    {
        var text = new System.Text.StringBuilder();
        foreach (var node in heading.DescendantNodes())
        {
            if (node is XText value) text.Append(value.Value);
            else if (node is XElement element && !element.Nodes().Any())
                text.Append((string?)element.Attribute("alt") ?? (string?)element.Attribute("title") ?? "");
        }
        return string.Join(' ', Tokens(text.ToString()));
    }

    private static bool TryExternalHyperlink(string reference, out string target)
    {
        target = "";
        if (reference.Length > 4096 || reference.Any(char.IsControl) || reference.Contains('\\')
            || !Uri.TryCreate(reference, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https" or "mailto")) return false;
        target = uri.AbsoluteUri;
        return target.Length <= 4096;
    }

    private string? TryNavigationTarget(string navigationPath, string reference)
    {
        try { return NavigationTarget(navigationPath, reference); }
        catch (InvalidDataException) { return null; }
    }

    private string NavigationTarget(string navigationPath, string reference)
    {
        var fragmentStart = reference.IndexOf('#');
        var path = fragmentStart < 0 ? reference : reference[..fragmentStart];
        var fragment = fragmentStart < 0 ? "" : reference[fragmentStart..];
        if (Uri.UnescapeDataString(fragment).Any(char.IsControl))
            throw new InvalidDataException("Invalid EPUB navigation fragment.");
        var prefix = navigationPath.Contains('/') ? navigationPath[..(navigationPath.LastIndexOf('/') + 1)] : "";
        var resolved = path.Length == 0 ? navigationPath : ResolvePath(prefix, path);
        if (!_resources.ContainsKey(resolved)) throw new InvalidDataException("Missing EPUB navigation target.");
        return resolved + fragment;
    }
}

/// <summary>A table of contents label, internal resource target and ordered child entries. An empty target denotes an unlinked grouping heading.</summary>
public sealed record EpubNavigationEntry(string Label, string Target, IReadOnlyList<EpubNavigationEntry> Children);
