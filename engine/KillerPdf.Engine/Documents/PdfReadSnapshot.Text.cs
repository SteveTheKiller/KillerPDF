namespace KillerPdf.Engine.Documents;

/// <summary>A source UTF16 range with actual unrotated bounds in bottom-left PDF points.</summary>
public sealed record PdfReadTextRegion(int Start, int Length, PdfContentBounds Bounds);

/// <summary>Immutable original native page text and its available selectable regions.</summary>
public sealed class PdfReadTextPage
{
    internal PdfReadTextPage(string text, IEnumerable<PdfReadTextRegion> regions,
        IReadOnlyList<Formats.Djvu.DjvuDiagnostic>? diagnostics = null)
    {
        Text = text;
        Regions = Array.AsReadOnly(regions.ToArray());
        Diagnostics = diagnostics ?? [];
    }
    /// <summary>Gets original page text, including source separators.</summary>
    public string Text { get; }
    /// <summary>Gets actual source regions without invented character subdivisions.</summary>
    public IReadOnlyList<PdfReadTextRegion> Regions { get; }
    /// <summary>
    /// Gets notices about how a DjVu page's text was read, such as TextEncodingInvalid for text that is not valid
    /// UTF-8, each naming its page; empty for other formats and for text read as stored.
    /// </summary>
    public IReadOnlyList<Formats.Djvu.DjvuDiagnostic> Diagnostics { get; }
}

public sealed partial class PdfReadSnapshot
{
    /// <summary>The native EPUB layout behind this snapshot, or null for other formats.</summary>
    internal Formats.Epub.EpubLayoutDocument? EpubLayout => _layout;

    /// <summary>Reads a page's text exactly as <see cref="ReadTextPage"/> returns it, without its geometry.</summary>
    /// <remarks>An EPUB page's text comes from the book's page index, so reading it never lays its chapter out again.</remarks>
    public string ReadPageText(int pageIndex, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_djvu is not null) return ReadTextPage(pageIndex, cancellationToken).Text;
        GetPageGeometry(pageIndex);
        return VisibleText(_layout!.Pages[pageIndex], null, cancellationToken);
    }

    // The one copy of the visible-text rule: hidden characters are left out, and offsets (when given, one per character
    // plus the end) map each page-text index to its index in the visible text.
    private static string VisibleText(Formats.Epub.EpubLayoutPage page, int[]? offsets, CancellationToken cancellationToken)
    {
        string text = page.Text;
        var hidden = page.HiddenTextRanges;
        if (hidden.Count == 0) return text;
        var visible = new System.Text.StringBuilder(text.Length);
        int rangeIndex = 0;
        for (int index = 0; index < text.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            while (rangeIndex < hidden.Count && index >= hidden[rangeIndex].Start + hidden[rangeIndex].Length)
                rangeIndex++;
            if (offsets is not null) offsets[index] = visible.Length;
            if (rangeIndex >= hidden.Count || index < hidden[rangeIndex].Start)
                visible.Append(text[index]);
        }
        if (offsets is not null) offsets[text.Length] = visible.Length;
        return visible.ToString();
    }

    /// <summary>Reads native text and unrotated point geometry. Intrinsic rotation remains in GetPageGeometry.</summary>
    public PdfReadTextPage ReadTextPage(int pageIndex, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var geometry = GetPageGeometry(pageIndex);
        double scaleX = 72 / geometry.DpiX, scaleY = 72 / geometry.DpiY;
        var regions = new List<PdfReadTextRegion>();
        if (_djvu is not null)
        {
            var source = _djvu.ReadTextPage(pageIndex, cancellationToken);
            if (source.Zones.Count == 0) return new PdfReadTextPage(source.Text, [], source.Diagnostics);
            // Prefix counts keep overlapping legacy ranges linear in text plus zones.
            var contentCounts = new int[source.Text.Length + 1];
            for (int index = 0; index < source.Text.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                char value = source.Text[index];
                contentCounts[index + 1] = contentCounts[index]
                    + (!char.IsWhiteSpace(value) && !char.IsControl(value) ? 1 : 0);
            }
            var hasChildren = new bool[source.Zones.Count];
            foreach (var zone in source.Zones)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (zone.ParentIndex >= 0) hasChildren[zone.ParentIndex] = true;
            }
            for (int index = 0; index < source.Zones.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var zone = source.Zones[index];
                if (hasChildren[index] || contentCounts[zone.Start + zone.Length] == contentCounts[zone.Start]) continue;
                regions.Add(new PdfReadTextRegion(zone.Start, zone.Length,
                    new PdfContentBounds(zone.Left * scaleX, zone.Bottom * scaleY,
                        (zone.Left + zone.Width) * scaleX, (zone.Bottom + zone.Height) * scaleY)));
            }
            cancellationToken.ThrowIfCancellationRequested();
            return new PdfReadTextPage(source.Text, regions, source.Diagnostics);
        }
        var page = _layout!.Pages[pageIndex];
        foreach (var placement in page.TextPlacements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            regions.Add(new PdfReadTextRegion(placement.Start - page.TextStart, placement.Length,
                new PdfContentBounds(placement.Left * scaleX,
                    (geometry.Height - placement.Top - placement.Height) * scaleY,
                    (placement.Left + placement.Width) * scaleX,
                    (geometry.Height - placement.Top) * scaleY)));
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (page.HiddenTextRanges.Count > 0)
        {
            var offsets = new int[page.Text.Length + 1];
            string visible = VisibleText(page, offsets, cancellationToken);
            for (int index = 0; index < regions.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var region = regions[index];
                regions[index] = region with
                {
                    Start = offsets[region.Start],
                    Length = offsets[region.Start + region.Length] - offsets[region.Start]
                };
            }
            return new PdfReadTextPage(visible, regions);
        }
        return new PdfReadTextPage(page.Text, regions);
    }
}
