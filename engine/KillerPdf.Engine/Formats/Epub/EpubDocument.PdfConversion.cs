using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Rendering;

namespace KillerPdf.Engine.Formats.Epub;

public sealed partial class EpubDocument
{
    /// <summary>Explicitly converts supported native EPUB content into an independent PDF.</summary>
    public byte[] ConvertToPdf(EpubPdfConversionOptions options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.DefaultFont);
        if (!EpubPdfConversionOptions.FitsPdfPageLimit(options.PageWidthPoints, options.PageHeightPoints))
            throw new ArgumentOutOfRangeException(nameof(options),
                "PDF pages must be 3 to 14,400 points (200 inches) on each side.");
        const double pointsPerCssPixel = 72d / 96d;
        var builder = new PdfDocumentBuilder();
        // Each page is written while its chapter's layout is in memory, so the book is laid out only once;
        // its links are kept until every page exists, since a link may lead to a later page.
        var linkRuns = new List<(int PageIndex, EpubTextPlacement Placement)>();
        var layout = CreateLayout(new EpubLayoutOptions(
            options.PageWidthPoints / pointsPerCssPixel,
            options.PageHeightPoints / pointsPerCssPixel, options.DefaultFont)
        {
            Margin = options.MarginPoints / pointsPerCssPixel,
            FontSize = options.FontSizePoints / pointsPerCssPixel,
            FontFamilyResolver = options.FontFamilyResolver,
            FontFaceResolver = options.FontFaceResolver,
            IsolateChapterFailures = options.IsolateChapterFailures,
            UnavailableChapterLabel = options.UnavailableChapterLabel
        }, AddPage, cancellationToken);
        foreach (var (pageIndex, placement) in linkRuns)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (placement.LinkTarget is not { } target || placement.Width <= 0 || placement.Height <= 0) continue;
            if (TryExternalHyperlink(target, out string external))
            {
                builder.AddUriLink(pageIndex, placement.Left * pointsPerCssPixel,
                    options.PageHeightPoints - (placement.Top + placement.Height) * pointsPerCssPixel,
                    placement.Width * pointsPerCssPixel, placement.Height * pointsPerCssPixel, external);
                continue;
            }
            // A link whose anchor is not in the book is left out; every other link is kept.
            if (TryResolve(target) is not { } destination) continue;
            builder.AddPageLink(pageIndex, placement.Left * pointsPerCssPixel,
                options.PageHeightPoints - (placement.Top + placement.Height) * pointsPerCssPixel,
                placement.Width * pointsPerCssPixel, placement.Height * pointsPerCssPixel,
                destination.PageIndex, destination: PdfDestination.At(destination.Left * pointsPerCssPixel,
                    options.PageHeightPoints - destination.Top * pointsPerCssPixel));
        }
        AddBookmarks(ReadNavigation(cancellationToken), 0);
        cancellationToken.ThrowIfCancellationRequested();
        byte[] result = builder.Build();
        cancellationToken.ThrowIfCancellationRequested();
        return result;

        void AddPage(EpubLayoutPage page, int pageIndex)
        {
            foreach (var run in page.LinkRuns(cancellationToken)) linkRuns.Add((pageIndex, run));
            cancellationToken.ThrowIfCancellationRequested();
            if (page.Glyphs.Any(paint => paint is not (NativeGlyphPaint or NativeRectanglePaint or NativeMarkerPaint)
                    && paint is not NativeImagePaint { Image: { Components: 3, Bits: 8 } }))
                throw new NotSupportedException(
                    "EPUB PDF conversion currently supports text, RGB PNG images, and solid backgrounds.");
            var content = new PdfContentStreamBuilder();
            if (page.CanvasBackground is { } background)
                content.SetFillRgb(background.Red, background.Green, background.Blue)
                    .Rectangle(0, 0, options.PageWidthPoints, options.PageHeightPoints)
                    .Fill();
            var placements = page.TextPlacements.ToDictionary(placement => placement.Start);
            var emittedText = new HashSet<int>();
            foreach (var paint in page.Glyphs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                switch (paint)
                {
                    case NativeMarkerPaint marker:
                        content.SetFillRgb(marker.Color.Red, marker.Color.Green, marker.Color.Blue)
                            .Circle(marker.CenterX * pointsPerCssPixel,
                                options.PageHeightPoints - marker.CenterY * pointsPerCssPixel,
                                marker.Radius * pointsPerCssPixel).Fill();
                        break;
                    case NativeRectanglePaint rectangle:
                        content.SetFillRgb(rectangle.Color.Red, rectangle.Color.Green, rectangle.Color.Blue)
                            .Rectangle(rectangle.Left * pointsPerCssPixel,
                                options.PageHeightPoints - (rectangle.Top + rectangle.Height) * pointsPerCssPixel,
                                rectangle.Width * pointsPerCssPixel, rectangle.Height * pointsPerCssPixel)
                            .Fill();
                        break;
                    case NativeImagePaint image:
                        DrawImage(image);
                        break;
                    case NativeGlyphPaint glyph:
                        // List-number glyphs have no source text; their markers are written below.
                        if (glyph.SourceOffset >= 0 && page.Clusters.TryGetValue(glyph.SourceOffset, out var drawn) && drawn.Missing)
                            DrawOutline(glyph, placements[glyph.SourceOffset].Color ?? new PdfRgbColor(0, 0, 0));
                        else if (glyph.SourceOffset >= 0 && emittedText.Add(glyph.SourceOffset)) DrawText(placements[glyph.SourceOffset]);
                        break;
                }
            }
            foreach (var marker in page.Markers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // A number the font cannot encode keeps its native drawing only.
                if (marker.Text.Any(character => marker.Font.GetGlyphId(character) == 0)) continue;
                var color = marker.Color ?? new PdfRgbColor(0, 0, 0);
                content.SetFillRgb(color.Red, color.Green, color.Blue)
                    .BeginText()
                    .SetFont(marker.Font, marker.Size * pointsPerCssPixel)
                    .SetTextMatrix(1, 0, 0, 1, marker.Left * pointsPerCssPixel,
                        options.PageHeightPoints - marker.Baseline * pointsPerCssPixel)
                    .ShowUnicodeText(marker.Text)
                    .EndText();
            }
            builder.AddPage(options.PageWidthPoints, options.PageHeightPoints, content);

            void DrawImage(NativeImagePaint paint)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var image = paint.Image;
                PdfImage pdfImage;
                if (image.Alpha is { } alpha)
                {
                    byte[] rgba = new byte[checked(alpha.Length * 4)];
                    for (int pixel = 0; pixel < alpha.Length; pixel++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        image.Samples.AsSpan(pixel * 3, 3).CopyTo(rgba.AsSpan(pixel * 4, 3));
                        rgba[pixel * 4 + 3] = alpha[pixel];
                    }
                    pdfImage = PdfImage.FromRgba(image.Width, image.Height, rgba);
                }
                else
                    pdfImage = PdfImage.FromRgb(image.Width, image.Height, image.Samples);
                content.DrawImage(pdfImage,
                    paint.Left * pointsPerCssPixel,
                    options.PageHeightPoints - (paint.Top + paint.Height) * pointsPerCssPixel,
                    paint.Width * pointsPerCssPixel, paint.Height * pointsPerCssPixel);
            }
            // A glyph PDF text cannot name (the font's .notdef box) is filled as its outline, so the PDF shows what the
            // reader shows. TrueType contours mix on-curve points with quadratic controls whose midpoints are implied.
            void DrawOutline(NativeGlyphPaint glyph, PdfRgbColor color)
            {
                double X(double x) => (glyph.Left + x * glyph.Scale) * pointsPerCssPixel;
                double Y(double y) => options.PageHeightPoints - (glyph.Baseline - y * glyph.Scale) * pointsPerCssPixel;
                static PdfGlyphPoint Mid(PdfGlyphPoint a, PdfGlyphPoint b) => new((a.X + b.X) / 2, (a.Y + b.Y) / 2, true);
                bool drew = false;
                foreach (var contour in glyph.Outline.Contours)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var points = contour.Points;
                    if (points.Count < 2) continue;
                    int first = -1;
                    for (int index = 0; index < points.Count && first < 0; index++) if (points[index].OnCurve) first = index;
                    var start = first >= 0 ? points[first] : Mid(points[^1], points[0]);
                    if (!drew) content.SetFillRgb(color.Red, color.Green, color.Blue);
                    drew = true;
                    content.MoveTo(X(start.X), Y(start.Y));
                    var current = start;
                    PdfGlyphPoint? quadratic = null;
                    var cubic = new List<PdfGlyphPoint>(2);
                    int count = first >= 0 ? points.Count - 1 : points.Count;
                    for (int step = 1; step <= count + 1; step++)
                    {
                        // The last step returns to the start point, which closes any pending curve.
                        var point = step > count ? start : points[((first >= 0 ? first : -1) + step + points.Count) % points.Count];
                        if (point.OnCurve)
                        {
                            if (cubic.Count == 2) content.CurveTo(X(cubic[0].X), Y(cubic[0].Y), X(cubic[1].X), Y(cubic[1].Y), X(point.X), Y(point.Y));
                            else if (quadratic is { } control) Quadratic(current, control, point);
                            else content.LineTo(X(point.X), Y(point.Y));
                            quadratic = null;
                            cubic.Clear();
                            current = point;
                        }
                        else if (point.IsCubicControl) cubic.Add(point);
                        else if (quadratic is { } control)
                        {
                            var middle = Mid(control, point);
                            Quadratic(current, control, middle);
                            current = middle;
                            quadratic = point;
                        }
                        else quadratic = point;
                    }
                    content.ClosePath();
                }
                if (drew) content.Fill();

                // A quadratic Bezier as the cubic with controls two thirds of the way to the quadratic control.
                void Quadratic(PdfGlyphPoint from, PdfGlyphPoint control, PdfGlyphPoint to) => content.CurveTo(
                    X(from.X + 2 * (control.X - from.X) / 3), Y(from.Y + 2 * (control.Y - from.Y) / 3),
                    X(to.X + 2 * (control.X - to.X) / 3), Y(to.Y + 2 * (control.Y - to.Y) / 3), X(to.X), Y(to.Y));
            }

            void DrawText(EpubTextPlacement placement)
            {
                cancellationToken.ThrowIfCancellationRequested();
                double size = placement.Height * pointsPerCssPixel;
                var cluster = page.Clusters[placement.Start];
                // A missing-glyph box has no character the embedded font can encode, so it adds no PDF text.
                if (cluster.Missing) return;
                double baseline = (placement.Top + cluster.Font.Ascender
                    * placement.Height / cluster.Font.UnitsPerEm) * pointsPerCssPixel;
                var color = placement.Color ?? new PdfRgbColor(0, 0, 0);
                if (cluster.Substituted)
                {
                    double advance = 0;
                    var positioned = new List<PdfPositionedGlyph>();
                    foreach (var glyph in cluster.Glyphs)
                    {
                        positioned.Add(new(glyph.GlyphId,
                            placement.Left * pointsPerCssPixel + (advance + glyph.OffsetX) * size,
                            options.PageHeightPoints - baseline + glyph.OffsetY * size));
                        advance += glyph.Advance;
                    }
                    content.SetFillRgb(color.Red, color.Green, color.Blue)
                        .DrawGlyphCluster(cluster.Font, size,
                            page.Text.Substring(cluster.Start - page.TextStart, cluster.Length), positioned);
                    return;
                }
                content.SetFillRgb(color.Red, color.Green, color.Blue)
                    .BeginText()
                    .SetFont(cluster.Font, size)
                    .SetTextMatrix(1, 0, 0, 1, placement.Left * pointsPerCssPixel,
                        options.PageHeightPoints - baseline)
                    .ShowUnicodeText(page.Text.Substring(placement.Start - page.TextStart, placement.Length))
                    .EndText();
            }
        }

        void AddBookmarks(IReadOnlyList<EpubNavigationEntry> entries, int level)
        {
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // A bookmark whose anchor is missing opens its chapter; one with no chapter stays a heading.
                var destination = entry.Target.Length == 0 ? null : TryResolve(entry.Target)
                    ?? (entry.Target.Contains('#') ? TryResolve(entry.Target[..entry.Target.IndexOf('#')]) : null);
                if (destination is null)
                    builder.AddBookmarkHeading(entry.Label, level);
                else
                {
                    builder.AddBookmark(entry.Label, destination.PageIndex, level, new PdfBookmarkOptions
                    {
                        Destination = PdfDestination.At(destination.Left * pointsPerCssPixel,
                            options.PageHeightPoints - destination.Top * pointsPerCssPixel)
                    });
                }
                AddBookmarks(entry.Children, level + 1);
            }
        }

        EpubNavigationDestination? TryResolve(string target)
        {
            try { return layout.ResolveNavigationTarget(target, cancellationToken); }
            catch (ArgumentException) { return null; }
        }
    }
}
