using System.Globalization;
using System.Text;
using KillerPdf.Engine.Objects;

namespace KillerPdf.Engine.Authoring;

internal static class PdfLinkAnnotationFactory
{
    internal static string ValidateUri(string uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        PdfUnicodeEncoding.EncodeUtf8(uri);
        if (!Uri.TryCreate(uri, UriKind.Absolute, out Uri? parsed)
            || parsed.Scheme is not ("http" or "https" or "mailto"))
            throw new ArgumentException(
                "A link URI must use http, https, or mailto.", nameof(uri));
        return parsed.AbsoluteUri;
    }

    internal static PdfTextQuad[] ValidateQuads(IReadOnlyList<PdfTextQuad> quads)
    {
        ArgumentNullException.ThrowIfNull(quads);
        if (quads.Count == 0)
            throw new ArgumentException(
                "At least one link quad is required.", nameof(quads));
        foreach (PdfTextQuad quad in quads) quad.Validate();
        return [.. quads];
    }

    internal static (double X, double Y, double Width, double Height) Bounds(
        IReadOnlyList<PdfTextQuad> quads)
    {
        PdfPoint[] points = [.. quads.SelectMany(quad => new[]
        {
            quad.UpperLeft, quad.UpperRight, quad.LowerLeft, quad.LowerRight
        })];
        double minX = points.Min(point => point.X);
        double minY = points.Min(point => point.Y);
        double maxX = points.Max(point => point.X);
        double maxY = points.Max(point => point.Y);
        return (minX, minY, maxX - minX, maxY - minY);
    }

    internal static PdfDictionary Create(
        double x, double y, double width, double height,
        PdfIndirectReference page, PdfIndirectReference annotation,
        PdfLinkAppearance appearance, PdfObject destinationOrAction,
        bool isAction, IReadOnlyList<PdfTextQuad>? quads,
        PdfAnnotationMetadata? metadata, string? contents)
    {
        var entries = new List<(string Name, PdfObject Value)>
        {
            ("Type", Name("Annot")), ("Subtype", Name("Link")),
            ("Rect", new PdfArray([
                Number(x), Number(y), Number(x + width), Number(y + height)])),
            ("P", page),
            ("F", new PdfInteger((int)(metadata?.Flags ?? PdfAnnotationFlags.Print))),
            ("NM", Latin1String($"KillerPDF-Link-{annotation.ObjectNumber}")),
            ("Border", new PdfArray([
                Number(appearance.HorizontalCornerRadius),
                Number(appearance.VerticalCornerRadius),
                Number(appearance.BorderWidth)])),
            ("H", Name(HighlightModeName(appearance.HighlightMode))),
            (isAction ? "A" : "Dest", destinationOrAction)
        };
        if (appearance.BorderWidth > 0)
        {
            var border = new List<(string Name, PdfObject Value)>
            {
                ("W", Number(appearance.BorderWidth)),
                ("S", Name(BorderStyleName(appearance.BorderStyle)))
            };
            if (appearance.BorderStyle == PdfLinkBorderStyle.Dashed)
                border.Add(("D", new PdfArray(
                    appearance.DashPattern.Select(Number))));
            entries.Add(("BS", Dictionary([.. border])));
        }
        if (appearance.Color.HasValue)
            entries.Add(("C", new PdfArray([
                Number(appearance.Color.Value.Red),
                Number(appearance.Color.Value.Green),
                Number(appearance.Color.Value.Blue)])));
        if (quads is not null)
            entries.Add(("QuadPoints", new PdfArray(quads.SelectMany(quad =>
                new PdfObject[]
                {
                    Number(quad.UpperLeft.X), Number(quad.UpperLeft.Y),
                    Number(quad.UpperRight.X), Number(quad.UpperRight.Y),
                    Number(quad.LowerLeft.X), Number(quad.LowerLeft.Y),
                    Number(quad.LowerRight.X), Number(quad.LowerRight.Y)
                }))));
        if (!string.IsNullOrEmpty(contents))
            entries.Add(("Contents", UnicodeString(contents)));
        AddMetadata(entries, metadata);
        return Dictionary([.. entries]);
    }

    internal static PdfDictionary UriAction(string uri) => Dictionary(
        ("S", Name("URI")),
        ("URI", new PdfString(PdfUnicodeEncoding.EncodeUtf8(uri),
            PdfStringForm.Literal)));

    internal static void AddMetadata(
        ICollection<(string Name, PdfObject Value)> entries,
        PdfAnnotationMetadata? metadata)
    {
        if (metadata is null) return;
        if (!string.IsNullOrEmpty(metadata.Author))
            entries.Add(("T", UnicodeString(metadata.Author)));
        if (!string.IsNullOrEmpty(metadata.Subject))
            entries.Add(("Subj", UnicodeString(metadata.Subject)));
        if (metadata.CreationDate.HasValue)
            entries.Add(("CreationDate", Latin1String(PdfDate(metadata.CreationDate.Value))));
        if (metadata.ModificationDate.HasValue)
            entries.Add(("M", Latin1String(PdfDate(metadata.ModificationDate.Value))));
    }

    private static string PdfDate(DateTimeOffset value)
    {
        TimeSpan offset = value.Offset;
        char sign = offset < TimeSpan.Zero ? '-' : '+';
        offset = offset.Duration();
        return string.Create(CultureInfo.InvariantCulture,
            $"D:{value:yyyyMMddHHmmss}{sign}{offset.Hours:00}'{offset.Minutes:00}'");
    }

    private static string BorderStyleName(PdfLinkBorderStyle style) => style switch
    {
        PdfLinkBorderStyle.Solid => "S",
        PdfLinkBorderStyle.Dashed => "D",
        PdfLinkBorderStyle.Beveled => "B",
        PdfLinkBorderStyle.Inset => "I",
        PdfLinkBorderStyle.Underline => "U",
        _ => throw new ArgumentOutOfRangeException(nameof(style))
    };

    private static string HighlightModeName(PdfLinkHighlightMode mode) => mode switch
    {
        PdfLinkHighlightMode.None => "N",
        PdfLinkHighlightMode.Invert => "I",
        PdfLinkHighlightMode.Outline => "O",
        PdfLinkHighlightMode.Push => "P",
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    private static PdfObject Number(double value) => value == Math.Truncate(value)
        ? new PdfInteger(checked((long)value)) : new PdfReal(value);
    private static PdfString Latin1String(string value) =>
        new(Encoding.Latin1.GetBytes(value), PdfStringForm.Literal);
    private static PdfString UnicodeString(string value) =>
        new([0xFE, 0xFF, .. PdfUnicodeEncoding.EncodeBigEndian(value)],
            PdfStringForm.Hexadecimal);
    private static PdfDictionary Dictionary(
        params (string Name, PdfObject Value)[] entries) =>
        new(entries.Select(entry =>
            new KeyValuePair<PdfName, PdfObject>(Name(entry.Name), entry.Value)));
    private static PdfName Name(string value) =>
        new(Encoding.ASCII.GetBytes(value));
}
