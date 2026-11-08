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
    double ImageX, double ImageY, double ImageWidth, double ImageHeight, EpubSvgAspect ImageAspect);

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
                    EpubSvgAspect.Parse(image.GetValueOrDefault("preserveAspectRatio")));
                if (cover.ImageWidth <= 0 || cover.ImageHeight <= 0) throw Unsupported();
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
                // A transform moves the image somewhere this cover model cannot place it, so it stays diagnosed.
                if (reader.NamespaceURI == "" && reader.LocalName == "transform") throw Unsupported();
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
