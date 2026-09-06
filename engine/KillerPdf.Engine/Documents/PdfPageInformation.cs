using System.Text;
using KillerPdf.Engine.Objects;

namespace KillerPdf.Engine.Documents;

/// <summary>Describes the effective display geometry of one PDF page.</summary>
public sealed record PdfPageInformation
{
    /// <summary>Gets the left edge of the effective crop or media box.</summary>
    public double Left { get; init; }
    /// <summary>Gets the bottom edge of the effective crop or media box.</summary>
    public double Bottom { get; init; }
    /// <summary>Gets the page width in PDF points before rotation.</summary>
    public required double Width { get; init; }
    /// <summary>Gets the page height in PDF points before rotation.</summary>
    public required double Height { get; init; }
    /// <summary>Gets the clockwise page rotation in degrees.</summary>
    public required int Rotation { get; init; }

    /// <summary>Reads the effective crop or media geometry for every page.</summary>
    public static IReadOnlyList<PdfPageInformation> Read(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        PdfPageTree tree = PdfPageTree.Read(document);
        var result = new PdfPageInformation[tree.Pages.Count];
        for (int index = 0; index < tree.Pages.Count; index++)
        {
            PdfPageTreeEntry page = tree.Pages[index];
            PdfArray box = ResolveArray(document,
                page.InheritedValues.TryGetValue(Name("CropBox"), out PdfObject? crop)
                    ? crop
                    : page.InheritedValues.TryGetValue(Name("MediaBox"), out PdfObject? media)
                        ? media
                        : throw new InvalidOperationException(
                            $"Page {index + 1} has no effective media box."),
                $"Page {index + 1} effective page box");
            if (box.Count != 4)
                throw new InvalidOperationException(
                    $"Page {index + 1} effective page box does not contain four numbers.");
            double x1 = Number(document, box[0], index);
            double y1 = Number(document, box[1], index);
            double x2 = Number(document, box[2], index);
            double y2 = Number(document, box[3], index);
            double width = Math.Abs(x2 - x1);
            double height = Math.Abs(y2 - y1);
            if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
                throw new InvalidOperationException(
                    $"Page {index + 1} effective page box is degenerate.");
            int rotation = 0;
            if (page.InheritedValues.TryGetValue(Name("Rotate"), out PdfObject? rotationValue))
            {
                PdfObject resolved = Resolve(document, rotationValue,
                    $"Page {index + 1} rotation");
                long rawRotation = resolved is PdfInteger integer
                    ? integer.Value
                    : throw new InvalidOperationException(
                        $"Page {index + 1} rotation is not an integer.");
                rotation = (int)(((rawRotation % 360) + 360) % 360);
                if (rotation % 90 != 0)
                    throw new InvalidOperationException(
                        $"Page {index + 1} rotation is not a multiple of 90 degrees.");
            }
            result[index] = new PdfPageInformation
            {
                Left = Math.Min(x1, x2),
                Bottom = Math.Min(y1, y2),
                Width = width,
                Height = height,
                Rotation = rotation
            };
        }
        return result;
    }

    private static double Number(PdfDocument document, PdfObject value, int pageIndex) =>
        Resolve(document, value, $"Page {pageIndex + 1} page-box coordinate") switch
        {
            PdfInteger integer => integer.Value,
            PdfReal real => real.Value,
            _ => throw new InvalidOperationException(
                $"Page {pageIndex + 1} page box contains a nonnumeric coordinate.")
        };

    private static PdfArray ResolveArray(
        PdfDocument document, PdfObject value, string description) =>
        Resolve(document, value, description) as PdfArray
        ?? throw new InvalidOperationException($"{description} is not an array.");

    private static PdfObject Resolve(PdfDocument document, PdfObject value, string description)
    {
        var visited = new HashSet<(int, int)>();
        for (int depth = 0; value is PdfIndirectReference reference; depth++)
        {
            if (depth >= 32 || !visited.Add((reference.ObjectNumber, reference.Generation)))
                throw new InvalidOperationException($"{description} has an invalid reference chain.");
            value = document.Resolve(reference);
        }
        return value;
    }

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
}
