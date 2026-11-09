using System.Collections;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Filters;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Rendering;

namespace KillerPdf.Engine.Formats.Epub;

// A laid-out page keeps one value per character instead of three objects: placements, glyph paints and clusters are
// stored in struct arrays with small shared tables, and the records callers see are rebuilt equal on access.
// The Complete Works of Shakespeare (3,975 pages) held about 350 bytes per character as objects.

/// <summary>An image paint that names its decoded raster instead of holding it; pages decode it when drawn.</summary>
internal sealed record EpubDeferredImagePaint(EpubImageKey Key, double Left, double Top, double Width, double Height) : NativePagePaint;

/// <summary>
/// Identifies one decoded raster: the packaged path, the decode box, the quarter turn or flip an SVG transform applies
/// (signs of its linear part, before the crop), the crop, and for a rotation or skew off the axes the resampling warp.
/// </summary>
internal sealed record EpubImageKey(string Target, double BoxWidth, double BoxHeight,
    int CropLeft = 0, int CropTop = 0, int CropWidth = -1, int CropHeight = -1,
    int TurnA = 1, int TurnB = 0, int TurnC = 0, int TurnD = 1)
{
    /// <summary>The off-axis warp applied before the crop, or null for none.</summary>
    internal EpubImageWarp? Warp { get; init; }
}

/// <summary>
/// Resamples a raster through an affine map off the axes: source pixel coordinates (x, y) land at output pixel
/// coordinates u = A x + C y + E and v = B x + D y + F in a Width by Height raster. Only source points inside the clip
/// rectangle (source pixel coordinates) are drawn; everything else in the output is transparent.
/// </summary>
internal sealed record EpubImageWarp(double A, double B, double C, double D, double E, double F, int Width, int Height,
    double ClipLeft, double ClipTop, double ClipRight, double ClipBottom);

/// <summary>
/// Decodes book images on demand behind a bounded, thread-safe least-recently-used cache, so the decoded-image
/// budget applies to what is being laid out or drawn, never to the whole book.
/// </summary>
internal sealed class EpubImageStore(Func<EpubImageKey, PngDecodedImage> decode, long capacityBytes = 192L << 20)
{
    private readonly Lock _lock = new();
    private readonly LinkedList<(EpubImageKey Key, PngDecodedImage Image)> _order = new();
    private readonly Dictionary<EpubImageKey, LinkedListNode<(EpubImageKey Key, PngDecodedImage Image)>> _entries = new();
    private long _bytes;

    internal PngDecodedImage Get(EpubImageKey key)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(key, out var node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                return node.Value.Image;
            }
        }
        var image = decode(key);
        lock (_lock)
        {
            if (_entries.TryGetValue(key, out var raced)) return raced.Value.Image;
            long size = image.Samples.LongLength + (image.Alpha?.LongLength ?? 0);
            while (_bytes + size > capacityBytes && _order.Last is { } oldest)
            {
                _order.RemoveLast();
                _entries.Remove(oldest.Value.Key);
                _bytes -= oldest.Value.Image.Samples.LongLength + (oldest.Value.Image.Alpha?.LongLength ?? 0);
            }
            _entries[key] = _order.AddFirst((key, image));
            _bytes += size;
            return image;
        }
    }
}

internal sealed class EpubPageStorage
{
    private readonly struct Placement(EpubTextPlacement value, int color, int link)
    {
        internal readonly int SpineIndex = value.SpineIndex, Start = value.Start, Length = value.Length, Color = color, Link = link;
        internal readonly double Left = value.Left, Top = value.Top, Width = value.Width, Height = value.Height;
    }

    private readonly struct Paint(int item, int outline, int color, int sourceOffset, double left, double baseline, double scale)
    {
        // Item >= 0 names a non-glyph paint in _items; -1 is a glyph described by the other fields.
        internal readonly int Item = item, Outline = outline, Color = color, SourceOffset = sourceOffset;
        internal readonly double Left = left, Baseline = baseline, Scale = scale;
    }

    private readonly struct Cluster(int start, int length, int font, byte flags, int glyphStart, int glyphCount)
    {
        internal readonly int Start = start, Length = length, Font = font, GlyphStart = glyphStart, GlyphCount = glyphCount;
        internal readonly byte Flags = flags;
    }

    private readonly Placement[] _placements;
    private readonly int[]? _visible;
    private readonly Paint[] _paints;
    private readonly NativePagePaint[] _items;
    private readonly PdfGlyphOutline[] _outlines;
    private readonly PdfRgbColor[] _colors;
    private readonly string[] _links;
    private readonly Cluster[] _clusters;
    private readonly EpubClusterGlyph[] _clusterGlyphs;
    private readonly TrueTypeFont[] _fonts;
    private readonly EpubImageStore? _images;

    internal EpubPageStorage(IEnumerable<EpubTextPlacement> placements, IEnumerable<EpubTextPlacement>? navigationPlacements,
        IEnumerable<NativePagePaint> paints, IEnumerable<EpubGlyphCluster>? clusters, EpubImageStore? images)
    {
        _images = images;
        var colors = new Table<PdfRgbColor>();
        var links = new Table<string>();
        var visible = placements.ToArray();
        var all = navigationPlacements?.ToArray() ?? visible;
        _placements = all.Select(item => new Placement(item, item.Color is { } color ? colors.Index(color) : -1,
            item.LinkTarget is { } link ? links.Index(link) : -1)).ToArray();
        if (!ReferenceEquals(all, visible))
        {
            // Visible placements are an ordered subsequence of the navigation placements (hidden text is left out).
            var indices = new List<int>(visible.Length);
            for (int at = 0, next = 0; at < all.Length && next < visible.Length; at++)
                if (all[at] == visible[next]) { indices.Add(at); next++; }
            if (indices.Count != visible.Length)
                throw new InvalidOperationException("Native EPUB visible placements must follow navigation order.");
            _visible = indices.Count == all.Length ? null : indices.ToArray();
        }
        var outlines = new Table<PdfGlyphOutline>(ReferenceEqualityComparer.Instance);
        var items = new List<NativePagePaint>();
        _paints = paints.Select(paint => paint is NativeGlyphPaint glyph
            ? new Paint(-1, outlines.Index(glyph.Outline), glyph.Color is { } color ? colors.Index(color) : -1,
                glyph.SourceOffset, glyph.Left, glyph.Baseline, glyph.Scale)
            : new Paint(Add(items, paint), 0, 0, 0, 0, 0, 0)).ToArray();
        _items = items.ToArray();
        _outlines = outlines.ToArray();
        var fonts = new Table<TrueTypeFont>(ReferenceEqualityComparer.Instance);
        var glyphs = new List<EpubClusterGlyph>();
        _clusters = (clusters ?? []).OrderBy(cluster => cluster.Start).Select(cluster =>
        {
            int start = glyphs.Count;
            glyphs.AddRange(cluster.Glyphs);
            byte flags = (byte)((cluster.Substituted ? 1 : 0) | (cluster.Blank ? 2 : 0) | (cluster.Missing ? 4 : 0));
            return new Cluster(cluster.Start, cluster.Length, fonts.Index(cluster.Font), flags, start, cluster.Glyphs.Count);
        }).ToArray();
        for (int at = 1; at < _clusters.Length; at++)
            if (_clusters[at].Start == _clusters[at - 1].Start)
                throw new ArgumentException("Native EPUB page clusters must have distinct starts.");
        _clusterGlyphs = glyphs.ToArray();
        _fonts = fonts.ToArray();
        _colors = colors.ToArray();
        _links = links.ToArray();

        static int Add(List<NativePagePaint> list, NativePagePaint paint) { list.Add(paint); return list.Count - 1; }
    }

    internal IReadOnlyList<EpubTextPlacement> TextPlacements => new PlacementView(this, _visible);
    internal IReadOnlyList<EpubTextPlacement> NavigationPlacements => new PlacementView(this, null);
    internal int PaintCount => _paints.Length;
    internal IReadOnlyDictionary<int, EpubGlyphCluster> Clusters => new ClusterView(this);

    /// <summary>Rebuilds the paint list, decoding deferred images through the book's image store.</summary>
    internal IReadOnlyList<NativePagePaint> Paints(bool decodeImages = true)
    {
        var result = new List<NativePagePaint>(_paints.Length);
        foreach (var paint in _paints)
        {
            if (paint.Item < 0)
            {
                result.Add(new NativeGlyphPaint(_outlines[paint.Outline], paint.Left, paint.Baseline, paint.Scale,
                    paint.Color < 0 ? null : _colors[paint.Color]) { SourceOffset = paint.SourceOffset });
                continue;
            }
            var item = _items[paint.Item];
            if (item is EpubDeferredImagePaint deferred && decodeImages)
            {
                if (_images is null) continue;
                result.Add(new NativeImagePaint(_images.Get(deferred.Key), deferred.Left, deferred.Top)
                { Width = deferred.Width, Height = deferred.Height });
            }
            else result.Add(item);
        }
        return result.AsReadOnly();
    }

    private EpubTextPlacement PlacementAt(int index)
    {
        var value = _placements[index];
        return new(value.SpineIndex, value.Start, value.Length, value.Left, value.Top, value.Width, value.Height)
        {
            Color = value.Color < 0 ? null : _colors[value.Color],
            LinkTarget = value.Link < 0 ? null : _links[value.Link]
        };
    }

    private EpubGlyphCluster ClusterAt(int index)
    {
        var value = _clusters[index];
        return new(value.Start, value.Length, _fonts[value.Font],
            new ArraySegment<EpubClusterGlyph>(_clusterGlyphs, value.GlyphStart, value.GlyphCount), (value.Flags & 1) != 0)
        {
            Blank = (value.Flags & 2) != 0, Missing = (value.Flags & 4) != 0
        };
    }

    private int ClusterIndex(int start)
    {
        int low = 0, high = _clusters.Length - 1;
        while (low <= high)
        {
            int middle = (low + high) >>> 1;
            int value = _clusters[middle].Start;
            if (value == start) return middle;
            if (value < start) low = middle + 1; else high = middle - 1;
        }
        return -1;
    }

    private sealed class Table<T>(IEqualityComparer<T>? comparer = null) where T : notnull
    {
        private readonly Dictionary<T, int> _index = new(comparer);
        private readonly List<T> _values = new();
        internal int Index(T value)
        {
            if (!_index.TryGetValue(value, out int at)) { _index.Add(value, at = _values.Count); _values.Add(value); }
            return at;
        }
        internal T[] ToArray() => _values.ToArray();
    }

    // Read-only like the arrays it replaces: every IList mutator throws NotSupportedException.
    private sealed class PlacementView(EpubPageStorage storage, int[]? indices) : IReadOnlyList<EpubTextPlacement>, IList<EpubTextPlacement>
    {
        public bool IsReadOnly => true;
        EpubTextPlacement IList<EpubTextPlacement>.this[int index] { get => this[index]; set => throw new NotSupportedException(); }
        public int IndexOf(EpubTextPlacement item)
        {
            for (int index = 0; index < Count; index++) if (this[index] == item) return index;
            return -1;
        }
        public bool Contains(EpubTextPlacement item) => IndexOf(item) >= 0;
        public void CopyTo(EpubTextPlacement[] array, int arrayIndex)
        {
            for (int index = 0; index < Count; index++) array[arrayIndex + index] = this[index];
        }
        public void Add(EpubTextPlacement item) => throw new NotSupportedException();
        public void Clear() => throw new NotSupportedException();
        public void Insert(int index, EpubTextPlacement item) => throw new NotSupportedException();
        public bool Remove(EpubTextPlacement item) => throw new NotSupportedException();
        public void RemoveAt(int index) => throw new NotSupportedException();
        public int Count => indices?.Length ?? storage._placements.Length;
        public EpubTextPlacement this[int index]
        {
            get
            {
                if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
                return storage.PlacementAt(indices is null ? index : indices[index]);
            }
        }
        public IEnumerator<EpubTextPlacement> GetEnumerator()
        {
            for (int index = 0; index < Count; index++) yield return this[index];
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class ClusterView(EpubPageStorage storage) : IReadOnlyDictionary<int, EpubGlyphCluster>
    {
        public EpubGlyphCluster this[int key] => TryGetValue(key, out var value) ? value : throw new KeyNotFoundException();
        public IEnumerable<int> Keys => storage._clusters.Select(cluster => cluster.Start);
        public IEnumerable<EpubGlyphCluster> Values => Enumerable.Range(0, Count).Select(storage.ClusterAt);
        public int Count => storage._clusters.Length;
        public bool ContainsKey(int key) => storage.ClusterIndex(key) >= 0;
        public bool TryGetValue(int key, out EpubGlyphCluster value)
        {
            int index = storage.ClusterIndex(key);
            value = index < 0 ? null! : storage.ClusterAt(index);
            return index >= 0;
        }
        public IEnumerator<KeyValuePair<int, EpubGlyphCluster>> GetEnumerator()
        {
            for (int index = 0; index < Count; index++)
                yield return new(storage._clusters[index].Start, storage.ClusterAt(index));
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
