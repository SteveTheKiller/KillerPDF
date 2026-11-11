namespace KillerPDF.Controls
{
    /// <summary>
    /// What a page's cached link list was read for: the bitmap size its rects are measured in, the document,
    /// and the list itself. The list is part of the key because other code clears or refills the list map
    /// without going through the key map; a key whose list is no longer the one in the map is stale. The
    /// document is held weakly so a closed document is not kept alive by a page nobody draws again.
    /// </summary>
    internal readonly record struct LinkCacheKey(int W, int H, WeakReference<object>? Owner, object? Entry)
    {
        internal static LinkCacheKey For(int w, int h, object? owner, object entry)
            => new(w, h, owner is null ? null : new WeakReference<object>(owner), entry);

        /// <summary>True when <paramref name="entry"/> is the very list this key was made for, at the same
        /// size, for the same document.</summary>
        internal bool Matches(object? entry, int w, int h, object? owner)
            => W == w && H == h
               && ReferenceEquals(Entry, entry)
               && (Owner is null ? owner is null : Owner.TryGetTarget(out var held) && ReferenceEquals(held, owner));
    }
}
