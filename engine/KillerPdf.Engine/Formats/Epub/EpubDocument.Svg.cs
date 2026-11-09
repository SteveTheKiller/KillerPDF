using System.Globalization;
using System.Xml;

namespace KillerPdf.Engine.Formats.Epub;

/// <summary>SVG 1.1 section 7.8 preserveAspectRatio: no alignment (stretch), or an alignment with meet or slice.</summary>
internal readonly record struct EpubSvgAspect(bool None, double AlignX, double AlignY, bool Slice)
{
    internal static readonly EpubSvgAspect Default = new(false, .5, .5, false);

    internal static EpubSvgAspect Parse(string? value)
    {
        string[] parts = (value ?? "").Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        int at = parts.Length > 0 && parts[0] == "defer" ? 1 : 0;
        if (parts.Length <= at) return Default;
        if (parts[at] == "none") return new(true, 0, 0, false);
        if (parts[at].Length != 8 || !parts[at].StartsWith('x') || parts[at][4] != 'Y') return Default;
        double? Align(string name) => name switch { "Min" => 0, "Mid" => .5, "Max" => 1, _ => null };
        if (Align(parts[at][1..4]) is not { } x || Align(parts[at][5..8]) is not { } y) return Default;
        return new(false, x, y, parts.Length > at + 1 && parts[at + 1] == "slice");
    }

    /// <summary>Maps a source box into a target box: scale per axis and offset of the source origin.</summary>
    internal (double ScaleX, double ScaleY, double OffsetX, double OffsetY) Fit(double width, double height,
        double targetWidth, double targetHeight)
    {
        if (None) return (targetWidth / width, targetHeight / height, 0, 0);
        double scale = Slice ? Math.Max(targetWidth / width, targetHeight / height)
            : Math.Min(targetWidth / width, targetHeight / height);
        return (scale, scale, AlignX * (targetWidth - width * scale), AlignY * (targetHeight - height * scale));
    }
}

/// <summary>A single-image SVG cover: the viewBox and its aspect, and the image rectangle and its aspect, in user units.</summary>
internal sealed record EpubSvgCover(string Reference, double MinX, double MinY, double Width, double Height, EpubSvgAspect Aspect,
    double ImageX, double ImageY, double ImageWidth, double ImageHeight, EpubSvgAspect ImageAspect)
{
    /// <summary>The image element's transform from its own user units to the viewBox's.</summary>
    internal EpubSvgMatrix Transform { get; init; } = EpubSvgMatrix.Identity;
}

/// <summary>An SVG 1.1 section 7.4 transform matrix: x' = A x + C y + E and y' = B x + D y + F.</summary>
internal readonly record struct EpubSvgMatrix(double A, double B, double C, double D, double E, double F)
{
    internal static readonly EpubSvgMatrix Identity = new(1, 0, 0, 1, 0, 0);

    /// <summary>This transform applied after <paramref name="inner"/>.</summary>
    internal EpubSvgMatrix Then(EpubSvgMatrix inner) => new(
        A * inner.A + C * inner.B, B * inner.A + D * inner.B,
        A * inner.C + C * inner.D, B * inner.C + D * inner.D,
        A * inner.E + C * inner.F + E, B * inner.E + D * inner.F + F);

    /// <summary>Whether the linear part keeps rectangles axis-aligned: scales, flips and quarter turns.</summary>
    internal bool KeepsAxes => B == 0 && C == 0 && A != 0 && D != 0 || A == 0 && D == 0 && B != 0 && C != 0;

    /// <summary>The bounding rectangle of a transformed rectangle.</summary>
    internal (double X, double Y, double Width, double Height) Bounds(double x, double y, double width, double height)
    {
        double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
        foreach (var (px, py) in new[] { (x, y), (x + width, y), (x, y + height), (x + width, y + height) })
        {
            double tx = A * px + C * py + E, ty = B * px + D * py + F;
            left = Math.Min(left, tx); right = Math.Max(right, tx);
            top = Math.Min(top, ty); bottom = Math.Max(bottom, ty);
        }
        return (left, top, right - left, bottom - top);
    }

    /// <summary>
    /// SVG 1.1 section 7.6 transform lists: matrix, translate, scale, rotate, skewX and skewY, separated by
    /// whitespace or commas and applied right to left. Returns null for a list that does not parse.
    /// </summary>
    internal static EpubSvgMatrix? Parse(string value)
    {
        var result = Identity;
        int at = 0;
        while (true)
        {
            while (at < value.Length && (char.IsWhiteSpace(value[at]) || value[at] == ',')) at++;
            if (at == value.Length) return result;
            int nameStart = at;
            while (at < value.Length && char.IsAsciiLetter(value[at])) at++;
            string name = value[nameStart..at];
            while (at < value.Length && char.IsWhiteSpace(value[at])) at++;
            if (at == value.Length || value[at] != '(') return null;
            int close = value.IndexOf(')', at);
            if (close < 0) return null;
            var numbers = new List<double>();
            foreach (string part in value[(at + 1)..close].Split([' ', '\t', '\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries))
            {
                if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) || !double.IsFinite(number)
                    || Math.Abs(number) > 1e6) return null;
                numbers.Add(number);
            }
            at = close + 1;
            EpubSvgMatrix? step = (name, numbers.Count) switch
            {
                ("matrix", 6) => new(numbers[0], numbers[1], numbers[2], numbers[3], numbers[4], numbers[5]),
                ("translate", 1 or 2) => new(1, 0, 0, 1, numbers[0], numbers.Count == 2 ? numbers[1] : 0),
                ("scale", 1 or 2) => new(numbers[0], 0, 0, numbers.Count == 2 ? numbers[1] : numbers[0], 0, 0),
                ("rotate", 1) => Rotation(numbers[0]),
                ("rotate", 3) => new EpubSvgMatrix(1, 0, 0, 1, numbers[1], numbers[2]).Then(Rotation(numbers[0]))
                    .Then(new(1, 0, 0, 1, -numbers[1], -numbers[2])),
                ("skewX", 1) => new(1, 0, Exact(Math.Tan(numbers[0] * Math.PI / 180)), 1, 0, 0),
                ("skewY", 1) => new(1, Exact(Math.Tan(numbers[0] * Math.PI / 180)), 0, 1, 0, 0),
                _ => null
            };
            if (step is not { } next) return null;
            result = result.Then(next);
        }

        // Quarter turns land exactly on the axes, so a rotated cover stays axis-aligned.
        static EpubSvgMatrix Rotation(double degrees)
        {
            double radians = degrees * Math.PI / 180;
            double cos = Exact(Math.Cos(radians)), sin = Exact(Math.Sin(radians));
            return new(cos, sin, -sin, cos, 0, 0);
        }

        static double Exact(double value) => Math.Abs(value) < 1e-12 ? 0
            : Math.Abs(value - 1) < 1e-12 ? 1 : Math.Abs(value + 1) < 1e-12 ? -1 : value;
    }
}

public sealed partial class EpubDocument
{
    // EP-BF-026: Calibre and Sigil covers vary the viewBox origin, aspect mode, outer size and inert attributes;
    // the cover always fills the body box as its viewport. Anything beyond one image stays diagnosed.
    private EpubSvgCover ReadSvgCover(XmlReader source,
        string chapter, EpubNativeContent? native, ref int nodeCount, CancellationToken cancellationToken)
    {
        const string svg = "http://www.w3.org/2000/svg";
        const string xlink = "http://www.w3.org/1999/xlink";
        var outer = Attributes(source);
        string[] box = outer.GetValueOrDefault("viewBox", "").Split([' ', '\t', '\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries);
        if (box.Length != 4) throw Unsupported();
        double minX = Number(box[0], true), minY = Number(box[1], true), width = Number(box[2]), height = Number(box[3]);
        if (width <= 0 || height <= 0) throw Unsupported();
        var aspect = EpubSvgAspect.Parse(outer.GetValueOrDefault("preserveAspectRatio"));
        EpubSvgCover? cover = null;
        using (var reader = source.ReadSubtree())
        {
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++nodeCount > 2_000_000 || reader.Depth > 2)
                    throw new InvalidDataException("EPUB SVG cover exceeds processing limits.");
                if (reader.Depth == 0) continue;
                if (reader.NodeType == XmlNodeType.EndElement) continue;
                if (reader.NodeType is XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace or XmlNodeType.Comment) continue;
                // Scripting keeps its chapter from showing, in SVG as in XHTML.
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "script")
                    throw new NotSupportedException("Scripted EPUB XHTML text is unsupported.");
                if (reader.NodeType != XmlNodeType.Element || reader.Depth != 1
                    || reader.NamespaceURI != svg || reader.LocalName != "image" || cover is not null)
                    throw Unsupported();
                var image = Attributes(reader);
                string? reference = image.GetValueOrDefault("href") ?? image.GetValueOrDefault(xlink + ":href");
                if (string.IsNullOrWhiteSpace(reference)) throw Unsupported();
                try { NavigationTarget(chapter, reference.Trim(' ', '\t', '\r', '\n')); }
                catch (InvalidDataException exception)
                {
                    throw new NotSupportedException("Native EPUB SVG covers require a packaged image reference.", exception);
                }
                cover = new(reference, minX, minY, width, height, aspect,
                    Length(image.GetValueOrDefault("x"), width, 0), Length(image.GetValueOrDefault("y"), height, 0),
                    Length(image.GetValueOrDefault("width"), width, width), Length(image.GetValueOrDefault("height"), height, height),
                    EpubSvgAspect.Parse(image.GetValueOrDefault("preserveAspectRatio")))
                {
                    Transform = image.TryGetValue("transform", out string? transform)
                        ? EpubSvgMatrix.Parse(transform) ?? throw Unsupported() : EpubSvgMatrix.Identity
                };
                if (cover.ImageWidth <= 0 || cover.ImageHeight <= 0) throw Unsupported();
                // A rotation or skew off the quarter turns would need the raster resampled, which is not implemented.
                if (!cover.Transform.KeepsAxes)
                    throw new NotSupportedException("Native EPUB SVG images rotated or skewed off the axes are not drawn yet.");
            }
        }
        return cover ?? throw Unsupported();

        // Presentation attributes the cover does not use (id, class, style, version, outer width and height) are inert.
        Dictionary<string, string> Attributes(XmlReader reader)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            while (reader.MoveToNextAttribute())
            {
                cancellationToken.ThrowIfCancellationRequested();
                native?.Count(reader.Value.Length);
                if (reader.LocalName.StartsWith("on", StringComparison.OrdinalIgnoreCase))
                    throw new NotSupportedException("Scripted EPUB XHTML text is unsupported.");
                if (reader.NamespaceURI == "") result.TryAdd(reader.LocalName, reader.Value);
                else if (reader.NamespaceURI == xlink) result.TryAdd(xlink + ":" + reader.LocalName, reader.Value);
            }
            reader.MoveToElement();
            return result;
        }

        // A user-unit number or a percentage of the viewBox extent; a missing value takes its default.
        static double Length(string? text, double extent, double fallback)
        {
            if (text is null) return fallback;
            text = text.Trim();
            if (text.EndsWith('%')) return Number(text[..^1], true) * extent / 100;
            return Number(text.EndsWith("px", StringComparison.Ordinal) ? text[..^2] : text, true);
        }

        static double Number(string text, bool signed = false)
        {
            if (!double.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out double value) || !double.IsFinite(value) || Math.Abs(value) > 32768
                || !signed && value < 0)
                throw Unsupported();
            return value;
        }

        static NotSupportedException Unsupported() => new("Native EPUB SVG rendering currently requires a static single-image cover with a viewBox.");
    }
}
