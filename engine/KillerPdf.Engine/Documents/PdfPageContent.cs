using KillerPdf.Engine.Authoring;

namespace KillerPdf.Engine.Documents;

/// <summary>An axis-aligned rectangle in PDF points.</summary>
public readonly record struct PdfContentBounds(double Left, double Bottom, double Right, double Top)
{
    /// <summary>Gets the rectangle width.</summary>
    public double Width => Right - Left;
    /// <summary>Gets the rectangle height.</summary>
    public double Height => Top - Bottom;
    internal static PdfContentBounds Union(IEnumerable<PdfContentBounds> boxes)
    {
        var items = boxes.ToArray();
        return items.Length == 0 ? default : new(items.Min(b => b.Left), items.Min(b => b.Bottom),
            items.Max(b => b.Right), items.Max(b => b.Top));
    }
}

/// <summary>Decoded text and its font and page geometry.</summary>
public sealed record PdfExtractedLetter(string Value, PdfContentBounds BoundingBox,
    string FontName, double FontSize, double PointSize, PdfPoint StartBaseLine, PdfPoint EndBaseLine)
{
    /// <summary>Gets the glyph bounds in PDF points.</summary>
    public PdfContentBounds GlyphRectangle => BoundingBox;
}

/// <summary>A geometrically contiguous word.</summary>
public sealed class PdfExtractedWord
{
    internal PdfExtractedWord(IEnumerable<PdfExtractedLetter> letters)
    {
        Letters = Array.AsReadOnly(letters.ToArray());
        Text = string.Concat(Letters.Select(l => l.Value));
        BoundingBox = PdfContentBounds.Union(Letters.Select(l => l.BoundingBox));
    }
    /// <summary>Gets the word text.</summary>
    public string Text { get; }
    /// <summary>Gets the word bounds.</summary>
    public PdfContentBounds BoundingBox { get; }
    /// <summary>Gets the constituent characters in content order.</summary>
    public IReadOnlyList<PdfExtractedLetter> Letters { get; }
}

/// <summary>An image placement in PDF coordinates.</summary>
public sealed record PdfExtractedImage(PdfContentBounds BoundingBox);

/// <summary>Text and image geometry extracted from an unrotated page.</summary>
public sealed class PdfPageContent
{
    internal PdfPageContent(double width, double height, IEnumerable<PdfExtractedLetter> letters,
        IEnumerable<PdfContentBounds> images, IEnumerable<string>? diagnostics = null)
    {
        Width = width;
        Height = height;
        Letters = Array.AsReadOnly(letters.ToArray());
        Images = Array.AsReadOnly(images.Select(b => new PdfExtractedImage(b)).ToArray());
        Diagnostics = Array.AsReadOnly((diagnostics ?? []).ToArray());
        Words = GroupWords(Letters);
        Text = string.Join(" ", Words.Select(w => w.Text));
    }
    /// <summary>Gets the page width in points.</summary>
    public double Width { get; }
    /// <summary>Gets the page height in points.</summary>
    public double Height { get; }
    /// <summary>Gets all decoded characters.</summary>
    public IReadOnlyList<PdfExtractedLetter> Letters { get; }
    /// <summary>Gets geometrically grouped words.</summary>
    public IReadOnlyList<PdfExtractedWord> Words { get; }
    /// <summary>Gets image placements.</summary>
    public IReadOnlyList<PdfExtractedImage> Images { get; }
    /// <summary>Gets compatibility recoveries encountered while extracting this page.</summary>
    public IReadOnlyList<string> Diagnostics { get; }
    /// <summary>Gets words separated by spaces in content order.</summary>
    public string Text { get; }
    /// <summary>Enumerates extracted words.</summary>
    public IEnumerable<PdfExtractedWord> GetWords() => Words;
    /// <summary>Enumerates image placements.</summary>
    public IEnumerable<PdfExtractedImage> GetImages() => Images;

    private static IReadOnlyList<PdfExtractedWord> GroupWords(IReadOnlyList<PdfExtractedLetter> letters)
    {
        var words = new List<PdfExtractedWord>();
        var current = new List<PdfExtractedLetter>();
        void Flush()
        {
            if (current.Count > 0) { words.Add(new PdfExtractedWord(current)); current.Clear(); }
        }
        foreach (var letter in letters)
        {
            if (string.IsNullOrWhiteSpace(letter.Value)) { Flush(); continue; }
            if (current.Count > 0)
            {
                var previous = current[^1];
                double dx = previous.EndBaseLine.X - previous.StartBaseLine.X;
                double dy = previous.EndBaseLine.Y - previous.StartBaseLine.Y;
                double length = Math.Sqrt(dx * dx + dy * dy);
                double ux = length > 0.001 ? dx / length : 1, uy = length > 0.001 ? dy / length : 0;
                double gapX = letter.StartBaseLine.X - previous.EndBaseLine.X;
                double gapY = letter.StartBaseLine.Y - previous.EndBaseLine.Y;
                double gap = gapX * ux + gapY * uy;
                double across = Math.Abs(gapY * ux - gapX * uy);
                double em = Math.Max(1, Math.Min(previous.PointSize, letter.PointSize));
                if (across > em * 0.35 || gap > em * 0.22 || gap < -Math.Max(length, em * 0.5)) Flush();
            }
            current.Add(letter);
        }
        Flush();
        return words.AsReadOnly();
    }
}
