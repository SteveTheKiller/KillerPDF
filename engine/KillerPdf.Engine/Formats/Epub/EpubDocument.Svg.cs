using System.Globalization;
using System.Xml;

namespace KillerPdf.Engine.Formats.Epub;

public sealed partial class EpubDocument
{
    private (string Reference, double Width, double Height) ReadSvgCover(XmlReader source,
        string chapter, EpubNativeContent? native, ref int nodeCount, CancellationToken cancellationToken)
    {
        const string svg = "http://www.w3.org/2000/svg";
        const string xlink = "http://www.w3.org/1999/xlink";
        var outer = Attributes(source, false);
        if (outer.GetValueOrDefault("width") != "100%" || outer.GetValueOrDefault("height") != "100%"
            || outer.GetValueOrDefault("version", "1.1") != "1.1"
            || outer.GetValueOrDefault("preserveAspectRatio", "xMidYMid meet") != "xMidYMid meet")
            throw Unsupported();
        string[] box = outer.GetValueOrDefault("viewBox", "").Split([' ', '\t', '\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries);
        if (box.Length != 4 || Number(box[0]) != 0 || Number(box[1]) != 0) throw Unsupported();
        double width = Number(box[2]), height = Number(box[3]);
        if (width <= 0 || height <= 0) throw Unsupported();
        string? reference = null;
        using (var reader = source.ReadSubtree())
        {
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++nodeCount > 2_000_000 || reader.Depth > 2)
                    throw new InvalidDataException("EPUB SVG cover exceeds processing limits.");
                if (reader.Depth == 0) continue;
                if (reader.NodeType == XmlNodeType.EndElement) continue;
                if (reader.NodeType is XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace) continue;
                if (reader.NodeType != XmlNodeType.Element || reader.Depth != 1
                    || reader.NamespaceURI != svg || reader.LocalName != "image" || reference is not null)
                    throw Unsupported();
                var image = Attributes(reader, true);
                if (Number(image.GetValueOrDefault("width", "")) != width
                    || Number(image.GetValueOrDefault("height", "")) != height
                    || image.GetValueOrDefault("preserveAspectRatio", "xMidYMid meet") != "xMidYMid meet")
                    throw Unsupported();
                reference = image.GetValueOrDefault("href");
                if (string.IsNullOrWhiteSpace(reference)) throw Unsupported();
                try { NavigationTarget(chapter, reference.Trim(' ', '\t', '\r', '\n')); }
                catch (InvalidDataException exception)
                {
                    throw new NotSupportedException("Native EPUB SVG covers require a packaged image reference.", exception);
                }
            }
        }
        if (reference is null) throw Unsupported();
        return (reference, width, height);

        Dictionary<string, string> Attributes(XmlReader reader, bool image)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            while (reader.MoveToNextAttribute())
            {
                cancellationToken.ThrowIfCancellationRequested();
                native?.Count(reader.Value.Length);
                if (reader.NamespaceURI == "http://www.w3.org/2000/xmlns/") continue;
                bool allowed = reader.NamespaceURI == "" && (image
                    ? reader.LocalName is "width" or "height" or "preserveAspectRatio"
                    : reader.LocalName is "width" or "height" or "preserveAspectRatio" or "viewBox" or "version");
                if (image && reader.NamespaceURI is "" or xlink && reader.LocalName == "href") allowed = true;
                if (!allowed || !result.TryAdd(reader.LocalName, reader.Value)) throw Unsupported();
            }
            reader.MoveToElement();
            return result;
        }

        static double Number(string text)
        {
            if (!double.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out double value) || !double.IsFinite(value) || value < 0 || value > 32768)
                throw Unsupported();
            return value;
        }

        static NotSupportedException Unsupported() => new("Native EPUB SVG rendering currently requires an unstyled static single-image cover with a zero-origin viewBox and centered meet scaling.");
    }
}
