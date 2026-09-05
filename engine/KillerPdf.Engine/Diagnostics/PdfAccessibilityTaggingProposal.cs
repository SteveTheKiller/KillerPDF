using KillerPdf.Engine.Documents;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KillerPdf.Engine.Diagnostics;

/// <summary>A proposed semantic role for untagged page content.</summary>
public enum PdfAccessibilityProposedRole
{
    /// <summary>A likely heading line.</summary>
    Heading,
    /// <summary>An ordinary text line.</summary>
    Paragraph,
    /// <summary>A text line with a common bullet or numbered-list prefix.</summary>
    ListItem,
    /// <summary>A link annotation that requires a meaningful description.</summary>
    Link,
    /// <summary>An image placement that requires alternate text.</summary>
    Figure,
    /// <summary>A repeated header or footer that is likely pagination furniture.</summary>
    Artifact,
    /// <summary>An interactive form field that requires a semantic label.</summary>
    FormField
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
public static partial class PdfAccessibilityTaggingProposal
{
    private static readonly PdfTaggingProposalCompactJsonContext CompactJson = new(
        JsonOptions(false));
    private static readonly PdfTaggingProposalIndentedJsonContext IndentedJson = new(
        JsonOptions(true));

    /// <summary>Infers review-required text and figure regions on untagged pages.</summary>
    public static IReadOnlyList<PdfAccessibilityTaggingProposalItem> Inspect(
        PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!document.IsDecrypted)
            throw new InvalidOperationException(
                "Authenticate the document before proposing accessibility tags.");
        var reader = new PdfPageContentReader(document);
        PdfPageContent[] pages = [.. Enumerable.Range(0, reader.PageCount)
            .Select(pageIndex => reader.Read(pageIndex))];
        HashSet<string> repeatedArtifacts = [.. pages
            .SelectMany((page, pageIndex) => page.Lines
                .Where(line => IsHeaderOrFooter(line.BoundingBox, page.Height))
                .Select(line => (PageIndex: pageIndex,
                    Key: ArtifactKey(line.Text, line.BoundingBox, page.Height))))
            .Where(item => item.Key is not null)
            .GroupBy(item => item.Key!, StringComparer.Ordinal)
            .Where(group => group.Select(item => item.PageIndex).Distinct().Count() > 1)
            .Select(group => group.Key)];
        var result = new List<PdfAccessibilityTaggingProposalItem>();
        for (int pageIndex = 0; pageIndex < reader.PageCount; pageIndex++)
        {
            PdfPageContent page = pages[pageIndex];
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
                    string text = line.Text.Trim();
                    bool artifact = repeatedArtifacts.Contains(
                        ArtifactKey(text, line.BoundingBox, page.Height) ?? string.Empty);
                    return (line.BoundingBox, Text: (string?)line.Text.Trim(),
                        Role: artifact ? PdfAccessibilityProposedRole.Artifact
                            : heading ? PdfAccessibilityProposedRole.Heading
                            : LooksLikeListItem(text) ? PdfAccessibilityProposedRole.ListItem
                            : PdfAccessibilityProposedRole.Paragraph,
                        Confidence: artifact ? 0.8
                            : heading ? 0.75 : LooksLikeListItem(text) ? 0.7 : 0.65);
                })
                .Concat(PdfLinkReader.ReadPage(document, pageIndex).Select(link =>
                    (BoundingBox: new PdfContentBounds(link.Left, link.Bottom, link.Right, link.Top),
                        Text: link.Description,
                        Role: PdfAccessibilityProposedRole.Link,
                        Confidence: string.IsNullOrWhiteSpace(link.Description) ? 0.55 : 0.9)))
                .Concat(PdfFormWidgetReader.ReadPage(document, pageIndex).Select(widget =>
                    (BoundingBox: new PdfContentBounds(
                            widget.Left, widget.Bottom, widget.Right, widget.Top),
                        Text: (string?)(string.IsNullOrWhiteSpace(widget.Tooltip)
                            ? widget.FieldName : widget.Tooltip),
                        Role: PdfAccessibilityProposedRole.FormField,
                        Confidence: string.IsNullOrWhiteSpace(widget.Tooltip) ? 0.55 : 0.9)))
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

    /// <summary>Exports semantic proposals as stable data without changing the document.</summary>
    public static string ToJson(PdfDocument document, bool indented = false)
    {
        var report = new PdfAccessibilityTaggingProposalJson(1, Inspect(document));
        return JsonSerializer.Serialize(report, indented
            ? IndentedJson.PdfAccessibilityTaggingProposalJson
            : CompactJson.PdfAccessibilityTaggingProposalJson);
    }

    private static bool LooksLikeListItem(string text)
    {
        if (text.StartsWith("- ", StringComparison.Ordinal)
            || text.StartsWith("* ", StringComparison.Ordinal)) return true;
        int index = 0;
        while (index < text.Length && char.IsAsciiDigit(text[index])) index++;
        return index > 0 && index + 1 < text.Length
            && text[index] is '.' or ')' && text[index + 1] == ' ';
    }

    private static bool IsHeaderOrFooter(PdfContentBounds bounds, double pageHeight) =>
        bounds.Top >= pageHeight * 0.9 || bounds.Bottom <= pageHeight * 0.1;

    private static string? ArtifactKey(
        string text, PdfContentBounds bounds, double pageHeight)
    {
        string normalized = string.Join(' ', text.Split(
            (char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (normalized.Length == 0 || !IsHeaderOrFooter(bounds, pageHeight)) return null;
        return (bounds.Top >= pageHeight * 0.9 ? "H:" : "F:") + normalized;
    }

    private static JsonSerializerOptions JsonOptions(bool indented)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = indented
        };
        options.Converters.Add(
            new JsonStringEnumConverter<PdfAccessibilityProposedRole>(JsonNamingPolicy.CamelCase));
        return options;
    }

    private sealed record PdfAccessibilityTaggingProposalJson(
        int SchemaVersion, IReadOnlyList<PdfAccessibilityTaggingProposalItem> Proposals);

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(PdfAccessibilityTaggingProposalJson))]
    private sealed partial class PdfTaggingProposalCompactJsonContext : JsonSerializerContext;

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        WriteIndented = true)]
    [JsonSerializable(typeof(PdfAccessibilityTaggingProposalJson))]
    private sealed partial class PdfTaggingProposalIndentedJsonContext : JsonSerializerContext;
}
