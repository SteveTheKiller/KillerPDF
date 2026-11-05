namespace KillerPdf.Engine.Documents;

/// <summary>A source UTF16 range with actual unrotated bounds in bottom-left PDF points.</summary>
public sealed record PdfReadTextRegion(int Start, int Length, PdfContentBounds Bounds);

/// <summary>Immutable original native page text and its available selectable regions.</summary>
public sealed class PdfReadTextPage
{
    internal PdfReadTextPage(string text, IEnumerable<PdfReadTextRegion> regions)
    {
        Text = text;
        Regions = Array.AsReadOnly(regions.ToArray());
    }
    /// <summary>Gets original page text, including source separators.</summary>
    public string Text { get; }
    /// <summary>Gets actual source regions without invented character subdivisions.</summary>
    public IReadOnlyList<PdfReadTextRegion> Regions { get; }
}

public sealed partial class PdfReadSnapshot
{
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
            if (source.Zones.Count == 0) return new PdfReadTextPage(source.Text, []);
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
            return new PdfReadTextPage(source.Text, regions);
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
        return new PdfReadTextPage(page.Text, regions);
    }
}
