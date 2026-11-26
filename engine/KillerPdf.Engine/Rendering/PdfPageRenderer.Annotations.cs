using System.Globalization;
using System.Text;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Writing;

namespace KillerPdf.Engine.Rendering;

public sealed partial class PdfPageRenderer
{
    // What a screen render draws for one annotation other than a link: a widget's saved appearance or the one
    // regenerated for its value, a markup annotation's saved appearance, or the one built for legacy markup that
    // has no /AP. A built appearance already sits in default page space; the others are placed in /Rect.
    internal PdfStream? ShownAppearance(int pageIndex, PdfDictionary annotation, PdfStream? saved, out bool inPageSpace)
    {
        inPageSpace = false;
        if (IsName(annotation, "Subtype", "Widget"))
        {
            try
            {
                return RequestedFieldAppearance(annotation, saved, _pageResources[pageIndex], [],
                    CancellationToken.None);
            }
            catch (Exception error) when (_document.UsesCompatibilityRecovery
                && error is FormatException or InvalidOperationException or NotSupportedException)
            {
                return saved;
            }
        }
        if (saved is not null) return saved;
        inPageSpace = true;
        return SynthesizedAppearance(annotation);
    }

    // Legacy annotations can omit /AP. Build their visible appearance from their own colors
    // and geometry in default page coordinates, including points outside an inaccurate /Rect.
    private PdfStream? SynthesizedAppearance(PdfDictionary annotation)
    {
        if (!annotation.TryGetValue(Name("Subtype"), out PdfObject? subtypeValue)
            || Resolve(subtypeValue) is not PdfName subtypeName) return null;
        string subtype = subtypeName.ValueAsLatin1();
        if (subtype is not ("Highlight" or "Underline" or "StrikeOut" or "Squiggly" or "Square" or "Circle" or "Ink" or "Text"))
            return null;
        double[]? rectangle = Numbers(annotation, "Rect");
        if (rectangle is not { Length: 4 }) return null;
        double left = Math.Min(rectangle[0], rectangle[2]), right = Math.Max(rectangle[0], rectangle[2]);
        double bottom = Math.Min(rectangle[1], rectangle[3]), top = Math.Max(rectangle[1], rectangle[3]);
        if (right <= left || top <= bottom) return null;

        string? stroke = ColorOperator(annotation, "C", stroking: true);
        string? fill = ColorOperator(annotation, "IC", stroking: false);
        double opacity = annotation.TryGetValue(Name("CA"), out PdfObject? opacityValue)
            && Resolve(opacityValue) is PdfInteger or PdfReal
                ? Math.Clamp(Number(Resolve(opacityValue)), 0, 1) : 1;
        bool highlight = subtype == "Highlight";
        var content = new StringBuilder();
        if (opacity < 1 || highlight) content.Append("/S gs\n");

        if (subtype == "Text")
        {
            // Small attachment-point rectangles must still yield a readable closed-note icon.
            right = Math.Max(right, left + 18);
            bottom = Math.Min(bottom, top - 20);
            string? color = ColorOperator(annotation, "C", stroking: false)
                ?? (annotation.ContainsKey(Name("C")) ? null : "1 1 0 rg");
            if (color is null) return null;
            double width = right - left, height = top - bottom;
            double lineWidth = Math.Min(1, Math.Min(width, height) / 8);
            double x0 = left + lineWidth / 2, x1 = right - lineWidth / 2;
            double y0 = bottom + lineWidth / 2, y1 = top - lineWidth / 2;
            double fold = Math.Min(width, height) / 4;
            // A closed text annotation is represented by a note marker, not its popup contents.
            content.Append(color).Append("\n0 G ").Append(Format(lineWidth)).Append(" w\n")
                .Append($"{Format(x0)} {Format(y0)} m {Format(x1)} {Format(y0)} l ")
                .Append($"{Format(x1)} {Format(y1 - fold)} l {Format(x1 - fold)} {Format(y1)} l ")
                .Append($"{Format(x0)} {Format(y1)} l h B\n")
                .Append($"{Format(x1 - fold)} {Format(y1)} m {Format(x1 - fold)} {Format(y1 - fold)} l ")
                .Append($"{Format(x1)} {Format(y1 - fold)} l S\n");
        }
        else if (subtype == "Ink")
        {
            if (!annotation.TryGetValue(Name("InkList"), out PdfObject? inkValue)
                || Resolve(inkValue) is not PdfArray paths) return null;
            stroke ??= annotation.ContainsKey(Name("C")) ? null : "0 G";
            if (stroke is null) return null;
            double lineWidth = BorderWidth(annotation);
            content.Append(stroke).Append('\n').Append(Format(lineWidth)).Append(" w 1 J 1 j\n");
            if (DashPattern(annotation) is string dash) content.Append(dash).Append('\n');
            foreach (PdfObject pathValue in paths)
            {
                if (Resolve(pathValue) is not PdfArray path || path.Count < 2 || path.Count % 2 != 0)
                    throw new FormatException("An ink annotation path must contain coordinate pairs.");
                for (int index = 0; index < path.Count; index += 2)
                {
                    double x = Number(Resolve(path[index])), y = Number(Resolve(path[index + 1]));
                    if (!double.IsFinite(x) || !double.IsFinite(y))
                        throw new FormatException("An ink annotation path contains an invalid coordinate.");
                    left = Math.Min(left, x - lineWidth / 2);
                    right = Math.Max(right, x + lineWidth / 2);
                    bottom = Math.Min(bottom, y - lineWidth / 2);
                    top = Math.Max(top, y + lineWidth / 2);
                    content.Append($"{Format(x)} {Format(y)} {(index == 0 ? "m" : "l")} ");
                    if (path.Count == 2) content.Append($"{Format(x)} {Format(y)} l ");
                }
                content.Append("S\n");
            }
        }
        else if (subtype is "Square" or "Circle")
        {
            double width = BorderWidth(annotation);
            if (stroke is null) width = 0;
            if (stroke is null && fill is null) return null;
            double inset = width / 2;
            double x0 = left + inset, y0 = bottom + inset, x1 = right - inset, y1 = top - inset;
            if (x1 <= x0 || y1 <= y0) return null;
            if (fill is not null) content.Append(fill).Append('\n');
            if (width > 0)
            {
                content.Append(stroke).Append('\n').Append(Format(width)).Append(" w\n");
                if (DashPattern(annotation) is string dash) content.Append(dash).Append('\n');
            }
            if (subtype == "Square")
                content.Append($"{Format(x0)} {Format(y0)} {Format(x1 - x0)} {Format(y1 - y0)} re\n");
            else
                AppendEllipse(content, x0, y0, x1, y1);
            content.Append(fill is not null && width > 0 ? "B\n" : fill is not null ? "f\n" : "S\n");
        }
        else
        {
            string? color = highlight ? ColorOperator(annotation, "C", stroking: false) : stroke;
            if (color is null) return null;
            content.Append(color).Append('\n');
            if (!highlight) content.Append("1 w\n");
            double[] quads = Numbers(annotation, "QuadPoints") is { Length: >= 8 } points
                ? points : [left, top, right, top, left, bottom, right, bottom];
            for (int index = 0; index + 1 < quads.Length; index += 2)
            {
                left = Math.Min(left, quads[index] - 1);
                right = Math.Max(right, quads[index] + 1);
                bottom = Math.Min(bottom, quads[index + 1] - 1);
                top = Math.Max(top, quads[index + 1] + 1);
            }
            for (int index = 0; index + 8 <= quads.Length; index += 8)
                AppendMarkup(content, subtype, quads.AsSpan(index, 8));
        }

        var entries = new List<KeyValuePair<PdfName, PdfObject>>
        {
            new(Name("Type"), Name("XObject")),
            new(Name("Subtype"), Name("Form")),
            new(Name("BBox"), new PdfArray([new PdfReal(left), new PdfReal(bottom), new PdfReal(right), new PdfReal(top)]))
        };
        if (opacity < 1 || highlight)
        {
            var state = new List<KeyValuePair<PdfName, PdfObject>>
            {
                new(Name("CA"), new PdfReal(opacity)),
                new(Name("ca"), new PdfReal(opacity))
            };
            if (highlight) state.Add(new(Name("BM"), Name("Multiply")));
            entries.Add(new(Name("Resources"), new PdfDictionary([
                new(Name("ExtGState"), new PdfDictionary([new(Name("S"), new PdfDictionary(state))]))])));
        }
        return new PdfStream(new PdfDictionary(entries), Encoding.ASCII.GetBytes(content.ToString()));
    }

    // Quadrilaterals follow the common writer order: upper left, upper right, lower left, lower right.
    private static void AppendMarkup(StringBuilder content, string subtype, ReadOnlySpan<double> quad)
    {
        (double X, double Y) upperLeft = (quad[0], quad[1]), upperRight = (quad[2], quad[3]);
        (double X, double Y) lowerLeft = (quad[4], quad[5]), lowerRight = (quad[6], quad[7]);
        double upX = upperLeft.X - lowerLeft.X, upY = upperLeft.Y - lowerLeft.Y;
        double height = Math.Sqrt(upX * upX + upY * upY);
        if (height <= 0) return;
        double unitX = upX / height, unitY = upY / height;
        switch (subtype)
        {
            case "Highlight":
                content.Append($"{Format(upperLeft.X)} {Format(upperLeft.Y)} m {Format(upperRight.X)} {Format(upperRight.Y)} l ")
                    .Append($"{Format(lowerRight.X)} {Format(lowerRight.Y)} l {Format(lowerLeft.X)} {Format(lowerLeft.Y)} l h f\n");
                break;
            case "Underline":
                Line(1);
                break;
            case "StrikeOut":
                Line(height / 2);
                break;
            case "Squiggly":
            {
                double alongX = lowerRight.X - lowerLeft.X, alongY = lowerRight.Y - lowerLeft.Y;
                double length = Math.Sqrt(alongX * alongX + alongY * alongY);
                double step = Math.Max(height / 6, 0.5);
                if (length <= 0) return;
                int steps = Math.Max(1, (int)Math.Ceiling(length / step));
                for (int index = 0; index <= steps; index++)
                {
                    double t = Math.Min(1, index * step / length);
                    double rise = index % 2 == 0 ? 0 : step;
                    double x = lowerLeft.X + alongX * t + unitX * rise, y = lowerLeft.Y + alongY * t + unitY * rise;
                    content.Append($"{Format(x)} {Format(y)} {(index == 0 ? "m" : "l")} ");
                }
                content.Append("S\n");
                break;
            }
        }

        void Line(double offset)
        {
            content.Append($"{Format(lowerLeft.X + unitX * offset)} {Format(lowerLeft.Y + unitY * offset)} m ")
                .Append($"{Format(lowerRight.X + unitX * offset)} {Format(lowerRight.Y + unitY * offset)} l S\n");
        }
    }

    private static void AppendEllipse(StringBuilder content, double x0, double y0, double x1, double y1)
    {
        const double k = 0.5522847498;
        double cx = (x0 + x1) / 2, cy = (y0 + y1) / 2, rx = (x1 - x0) / 2, ry = (y1 - y0) / 2;
        content.Append($"{Format(cx + rx)} {Format(cy)} m\n")
            .Append($"{Format(cx + rx)} {Format(cy + ry * k)} {Format(cx + rx * k)} {Format(cy + ry)} {Format(cx)} {Format(cy + ry)} c\n")
            .Append($"{Format(cx - rx * k)} {Format(cy + ry)} {Format(cx - rx)} {Format(cy + ry * k)} {Format(cx - rx)} {Format(cy)} c\n")
            .Append($"{Format(cx - rx)} {Format(cy - ry * k)} {Format(cx - rx * k)} {Format(cy - ry)} {Format(cx)} {Format(cy - ry)} c\n")
            .Append($"{Format(cx + rx * k)} {Format(cy - ry)} {Format(cx + rx)} {Format(cy - ry * k)} {Format(cx + rx)} {Format(cy)} c h\n");
    }

    private double BorderWidth(PdfDictionary annotation)
    {
        if (annotation.TryGetValue(Name("BS"), out PdfObject? styleValue)
            && Resolve(styleValue) is PdfDictionary style
            && style.TryGetValue(Name("W"), out PdfObject? widthValue)
            && Resolve(widthValue) is PdfInteger or PdfReal)
            return Math.Max(0, Number(Resolve(widthValue)));
        if (Numbers(annotation, "Border") is { Length: >= 3 } border)
            return Math.Max(0, border[2]);
        return 1;
    }

    private string? DashPattern(PdfDictionary annotation)
    {
        if (!annotation.TryGetValue(Name("BS"), out PdfObject? styleValue)
            || Resolve(styleValue) is not PdfDictionary style
            || !IsName(style, "S", "D")) return null;
        double[] dashes = Numbers(style, "D") is { Length: > 0 } values && values.All(value => value >= 0)
            && values.Any(value => value > 0) ? values : [3];
        return $"[{string.Join(' ', dashes.Select(Format))}] 0 d";
    }

    private string? ColorOperator(PdfDictionary annotation, string key, bool stroking)
    {
        double[]? components = Numbers(annotation, key);
        if (components is null) return null;
        string values = string.Join(' ', components.Select(value =>
            Encoding.ASCII.GetString(PdfObjectWriter.Write(new PdfReal(Math.Clamp(value, 0, 1))))));
        return components.Length switch
        {
            1 => values + (stroking ? " G" : " g"),
            3 => values + (stroking ? " RG" : " rg"),
            4 => values + (stroking ? " K" : " k"),
            _ => null
        };
    }

    private double[]? Numbers(PdfDictionary dictionary, string key)
    {
        if (!dictionary.TryGetValue(Name(key), out PdfObject? value)
            || Resolve(value) is not PdfArray array) return null;
        var numbers = new double[array.Count];
        for (int index = 0; index < array.Count; index++)
        {
            PdfObject item = Resolve(array[index]);
            if (item is not (PdfInteger or PdfReal)) return null;
            numbers[index] = Number(item);
            if (!double.IsFinite(numbers[index])) return null;
        }
        return numbers;
    }

    private static string Format(double value) =>
        value.ToString("0.####", CultureInfo.InvariantCulture);
}
