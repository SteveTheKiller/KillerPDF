using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Editing;

namespace KillerPdf.Engine.Documents;

/// <summary>Writes clickable table-of-contents pages from a bookmark plan.</summary>
public static class PdfTableOfContentsWriter
{
    /// <summary>Inserts table-of-contents pages at the beginning of a document.</summary>
    public static PdfTableOfContentsWriteResult Write(
        PdfDocument document, PdfTableOfContentsWriteOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        options ??= new PdfTableOfContentsWriteOptions();
        Validate(options);

        PdfTableOfContentsPlan plan = PdfTableOfContentsPlanner.Plan(
            document, options.MaximumDepth);
        int rowsPerPage = (int)Math.Floor(
            (options.PageHeight - options.TopMargin - options.BottomMargin
             - options.TitleFontSize - options.TitleGap) / options.RowHeight);
        if (rowsPerPage < 1)
            throw new ArgumentException("The page layout has no room for table-of-contents rows.",
                nameof(options));
        int pageCount = Math.Max(1, (plan.Entries.Count + rowsPerPage - 1) / rowsPerPage);
        var pageEditor = new PdfIncrementalPageEditor(document);
        var links = new List<LinkPlacement>();

        for (int tocPageIndex = 0; tocPageIndex < pageCount; tocPageIndex++)
        {
            var content = new PdfContentStreamBuilder();
            DrawText(content, options.Title, options.LeftMargin,
                options.PageHeight - options.TopMargin - options.TitleFontSize,
                options.TitleFontSize, PdfStandardFont.HelveticaBold, null);
            int first = tocPageIndex * rowsPerPage;
            int last = Math.Min(plan.Entries.Count, first + rowsPerPage);
            for (int entryIndex = first; entryIndex < last; entryIndex++)
            {
                PdfTableOfContentsEntry entry = plan.Entries[entryIndex];
                int row = entryIndex - first;
                double baseline = options.PageHeight - options.TopMargin
                    - options.TitleFontSize - options.TitleGap
                    - row * options.RowHeight - options.EntryFontSize;
                double left = options.LeftMargin + entry.Depth * options.IndentWidth;
                string title = Latin1(entry.Title);
                string label = Latin1(entry.PageLabel ?? string.Empty);
                int leaderCount = Math.Max(2, (int)((options.PageWidth
                    - options.RightMargin - left) / (options.EntryFontSize * 0.52))
                    - title.Length - label.Length - 2);
                string line = string.Concat(title, " ", new string('.', leaderCount),
                    label.Length == 0 ? string.Empty : " " + label);
                DrawText(content, line, left, baseline, options.EntryFontSize,
                    Font(entry.Style), entry.Color);
                if (entry.PageIndex.HasValue)
                    links.Add(new LinkPlacement(tocPageIndex, left,
                        baseline - options.EntryFontSize * 0.2,
                        options.PageWidth - options.RightMargin - left,
                        options.RowHeight, entry.PageIndex.Value + pageCount,
                        entry.Destination, entry.Title));
            }
            pageEditor.InsertPage(tocPageIndex, options.PageWidth, options.PageHeight, content);
        }

        byte[] pages = pageEditor.Build();
        if (links.Count == 0)
            return new PdfTableOfContentsWriteResult(pages, pageCount,
                plan.Entries.Count, plan.UnresolvedCount);
        var annotations = new PdfIncrementalAnnotationEditor(PdfDocument.Open(pages));
        foreach (LinkPlacement link in links)
            annotations.AddPageLink(link.PageIndex, link.X, link.Y, link.Width,
                link.Height, link.DestinationPageIndex, destination: link.Destination,
                contents: link.Description);
        return new PdfTableOfContentsWriteResult(annotations.Build(), pageCount,
            plan.Entries.Count, plan.UnresolvedCount);
    }

    private static void DrawText(PdfContentStreamBuilder content, string text,
        double x, double y, double size, PdfStandardFont font, PdfRgbColor? color)
    {
        PdfRgbColor value = color ?? new PdfRgbColor(0, 0, 0);
        content.SetFillRgb(value.Red, value.Green, value.Blue)
            .BeginText().SetFont(font, size).MoveText(x, y)
            .ShowLatin1Text(Latin1(text)).EndText();
    }

    private static PdfStandardFont Font(PdfBookmarkStyle style) => style switch
    {
        PdfBookmarkStyle.Bold | PdfBookmarkStyle.Italic => PdfStandardFont.HelveticaBoldOblique,
        PdfBookmarkStyle.Bold => PdfStandardFont.HelveticaBold,
        PdfBookmarkStyle.Italic => PdfStandardFont.HelveticaOblique,
        _ => PdfStandardFont.Helvetica
    };

    private static string Latin1(string value) =>
        new(value.Select(character => character <= 0xff ? character : '?').ToArray());

    private static void Validate(PdfTableOfContentsWriteOptions options)
    {
        if (options.MaximumDepth is < 1 or > 256)
            throw new ArgumentOutOfRangeException(nameof(options.MaximumDepth));
        Positive(options.PageWidth, nameof(options.PageWidth));
        Positive(options.PageHeight, nameof(options.PageHeight));
        Positive(options.LeftMargin, nameof(options.LeftMargin));
        Positive(options.RightMargin, nameof(options.RightMargin));
        Positive(options.TopMargin, nameof(options.TopMargin));
        Positive(options.BottomMargin, nameof(options.BottomMargin));
        Positive(options.TitleFontSize, nameof(options.TitleFontSize));
        Positive(options.EntryFontSize, nameof(options.EntryFontSize));
        Positive(options.RowHeight, nameof(options.RowHeight));
        Positive(options.TitleGap, nameof(options.TitleGap));
        Positive(options.IndentWidth, nameof(options.IndentWidth));
        if (options.LeftMargin + options.RightMargin >= options.PageWidth)
            throw new ArgumentException("The horizontal margins consume the page width.", nameof(options));
        if (string.IsNullOrWhiteSpace(options.Title))
            throw new ArgumentException("The table-of-contents title cannot be empty.", nameof(options));
    }

    private static void Positive(double value, string name)
    {
        if (!double.IsFinite(value) || value <= 0)
            throw new ArgumentOutOfRangeException(name);
    }

    private sealed record LinkPlacement(int PageIndex, double X, double Y,
        double Width, double Height, int DestinationPageIndex,
        PdfDestination? Destination, string Description);
}

/// <summary>Controls clickable table-of-contents page generation.</summary>
public sealed record PdfTableOfContentsWriteOptions
{
    /// <summary>Gets the generated heading.</summary>
    public string Title { get; init; } = "Table of Contents";
    /// <summary>Gets the maximum bookmark hierarchy depth.</summary>
    public int MaximumDepth { get; init; } = 6;
    /// <summary>Gets the generated page width in points.</summary>
    public double PageWidth { get; init; } = 612;
    /// <summary>Gets the generated page height in points.</summary>
    public double PageHeight { get; init; } = 792;
    /// <summary>Gets the left page margin in points.</summary>
    public double LeftMargin { get; init; } = 54;
    /// <summary>Gets the right page margin in points.</summary>
    public double RightMargin { get; init; } = 54;
    /// <summary>Gets the top page margin in points.</summary>
    public double TopMargin { get; init; } = 54;
    /// <summary>Gets the bottom page margin in points.</summary>
    public double BottomMargin { get; init; } = 54;
    /// <summary>Gets the heading font size.</summary>
    public double TitleFontSize { get; init; } = 18;
    /// <summary>Gets the entry font size.</summary>
    public double EntryFontSize { get; init; } = 11;
    /// <summary>Gets the distance between entry baselines.</summary>
    public double RowHeight { get; init; } = 16;
    /// <summary>Gets the distance after the heading.</summary>
    public double TitleGap { get; init; } = 22;
    /// <summary>Gets the indentation applied per hierarchy level.</summary>
    public double IndentWidth { get; init; } = 18;
}

/// <summary>Reports the generated document and table-of-contents coverage.</summary>
public sealed record PdfTableOfContentsWriteResult(
    byte[] Document, int InsertedPageCount, int EntryCount, int UnresolvedCount);
