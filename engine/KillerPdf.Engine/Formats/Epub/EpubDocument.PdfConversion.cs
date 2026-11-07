using KillerPdf.Engine.Authoring;
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
        const double pointsPerCssPixel = 72d / 96d;
        var layout = CreateLayout(new EpubLayoutOptions(
            options.PageWidthPoints / pointsPerCssPixel,
            options.PageHeightPoints / pointsPerCssPixel, options.DefaultFont)
        {
            Margin = options.MarginPoints / pointsPerCssPixel,
            FontSize = options.FontSizePoints / pointsPerCssPixel,
            FontFamilyResolver = options.FontFamilyResolver,
            FontFaceResolver = options.FontFaceResolver
        }, cancellationToken);
        var builder = new PdfDocumentBuilder();
        foreach (var page in layout.Pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (page.Glyphs.Any(paint => paint is not (NativeGlyphPaint or NativeRectanglePaint)
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
                        if (emittedText.Add(glyph.SourceOffset)) DrawText(placements[glyph.SourceOffset]);
                        break;
                }
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
            void DrawText(EpubTextPlacement placement)
            {
                cancellationToken.ThrowIfCancellationRequested();
                double size = placement.Height * pointsPerCssPixel;
                var cluster = page.Clusters[placement.Start];
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
        for (int pageIndex = 0; pageIndex < layout.Pages.Count; pageIndex++)
            foreach (var placement in layout.Pages[pageIndex].LinkRuns(cancellationToken))
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
                var destination = layout.ResolveNavigationTarget(target, cancellationToken);
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

        void AddBookmarks(IReadOnlyList<EpubNavigationEntry> entries, int level)
        {
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.Target.Length == 0)
                    builder.AddBookmarkHeading(entry.Label, level);
                else
                {
                    var destination = layout.ResolveNavigationTarget(entry.Target, cancellationToken);
                    builder.AddBookmark(entry.Label, destination.PageIndex, level, new PdfBookmarkOptions
                    {
                        Destination = PdfDestination.At(destination.Left * pointsPerCssPixel,
                            options.PageHeightPoints - destination.Top * pointsPerCssPixel)
                    });
                }
                AddBookmarks(entry.Children, level + 1);
            }
        }
    }
}
