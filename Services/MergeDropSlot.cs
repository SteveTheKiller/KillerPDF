using System.Windows;

namespace KillerPDF.Services;

/// <summary>Where a drop in the Merge dialog's page view goes, and where its marker line sits.</summary>
internal readonly record struct MergeDropTarget(int Index, double X, double Top, double Bottom);

/// <summary>
/// Finds the drop slot in the page view's wrapped rows of tiles from their bounds, not from a hit test, so a drop in
/// the space between tiles lands between them instead of at the end (#495). The pointer picks the row it is level
/// with, or the nearest row above it, and goes before the first tile in that row whose middle is right of it.
/// Below the last row is the end.
/// </summary>
internal static class MergeDropSlot
{
    internal static MergeDropTarget Find(IReadOnlyList<Rect> tiles, Point at)
    {
        if (tiles.Count == 0) return new MergeDropTarget(0, 0, 0, 0);
        // Tiles in one row share a top edge; a row ends where the next row's first tile starts.
        var rows = new List<(int Start, int End)>();
        int start = 0;
        for (int index = 1; index <= tiles.Count; index++)
            if (index == tiles.Count || Math.Abs(tiles[index].Top - tiles[start].Top) > 0.5)
            {
                rows.Add((start, index));
                start = index;
            }

        // A row reaches down to where the next one starts, so the gap under a row belongs to it.
        int row = rows.Count - 1;
        for (int candidate = 0; candidate < rows.Count - 1; candidate++)
            if (at.Y < tiles[rows[candidate + 1].Start].Top) { row = candidate; break; }
        var (first, end) = rows[row];
        Rect last = tiles[end - 1];
        if (at.Y > last.Bottom && row == rows.Count - 1)
            return new MergeDropTarget(tiles.Count, last.Right, last.Top, last.Bottom);

        for (int index = first; index < end; index++)
        {
            Rect tile = tiles[index];
            if (at.X < tile.Left + tile.Width / 2)
                return new MergeDropTarget(index, tile.Left, tile.Top, tile.Bottom);
        }
        return new MergeDropTarget(end, last.Right, last.Top, last.Bottom);
    }
}
