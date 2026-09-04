using KillerPdf.Engine.Documents;

namespace KillerPdf.Engine.Diagnostics;

/// <summary>A proposed semantic role for untagged page content.</summary>
public enum PdfAccessibilityProposedRole
{
    /// <summary>A likely heading line.</summary>
    Heading,
    /// <summary>An ordinary text line.</summary>
    Paragraph,
    /// <summary>An image placement that requires alternate text.</summary>
    Figure
}

/// <summary>One review-required semantic proposal in inferred visual reading order.</summary>
public sealed record PdfAccessibilityTaggingProposalItem(
    int Order,
    int PageIndex,
    PdfAccessibilityProposedRole Role,
    PdfContentBounds BoundingBox,
    string? Text,
    double Confidence,
    bool RequiresReview);

/// <summary>Produces conservative semantic proposals without changing the source document.</summary>
public static class PdfAccessibilityTaggingProposal
{
    /// <summary>Infers review-required text and figure regions on untagged pages.</summary>
    public static IReadOnlyList<PdfAccessibilityTaggingProposalItem> Inspect(
        PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!document.IsDecrypted)
            throw new InvalidOperationException(
                "Authenticate the document before proposing accessibility tags.");
        var reader = new PdfPageContentReader(document);
        var result = new List<PdfAccessibilityTaggingProposalItem>();
        for (int pageIndex = 0; pageIndex < reader.PageCount; pageIndex++)
        {
            PdfPageContent page = reader.Read(pageIndex);
            double[] sizes = [.. page.Lines.SelectMany(line => line.Runs)
                .Select(run => run.PointSize).Where(size => size > 0).Order()];
            double median = sizes.Length == 0 ? 0
                : sizes.Length % 2 == 1 ? sizes[sizes.Length / 2]
                : (sizes[sizes.Length / 2 - 1] + sizes[sizes.Length / 2]) / 2;
            var regions = page.Lines
                .Where(line => !string.IsNullOrWhiteSpace(line.Text))
                .Select(line =>
                {
                    double size = line.Runs.Max(run => run.PointSize);
                    bool heading = median > 0 && size >= median * 1.3
                        && line.Text.Trim().Length <= 120;
                    return (line.BoundingBox, Text: (string?)line.Text.Trim(),
                        Role: heading ? PdfAccessibilityProposedRole.Heading
                            : PdfAccessibilityProposedRole.Paragraph,
                        Confidence: heading ? 0.75 : 0.65);
                })
                .Concat(page.Images.Select(image => (image.BoundingBox, Text: (string?)null,
                    Role: PdfAccessibilityProposedRole.Figure, Confidence: 0.5)))
                .OrderByDescending(region => region.BoundingBox.Top)
                .ThenBy(region => region.BoundingBox.Left)
                .ThenBy(region => region.Role);
            foreach (var region in regions)
                result.Add(new PdfAccessibilityTaggingProposalItem(
                    result.Count, pageIndex, region.Role, region.BoundingBox,
                    region.Text, region.Confidence, RequiresReview: true));
        }
        return Array.AsReadOnly(result.ToArray());
    }
}
