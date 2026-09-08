namespace KillerPdf.Engine.Rendering;

public sealed partial class PdfPageRenderer
{
    private sealed class ClipScratchScope : IDisposable
    {
        [ThreadStatic]
        private static ClipScratchScope? _current;
        private readonly ClipScratchScope? _parent = _current;
        private readonly List<CoverageMask?> _masks = [];
        private readonly Stack<int> _saved = [];

        internal ClipScratchScope() => _current = this;
        internal static ClipScratchScope? Current => _current;
        internal int Save()
        {
            _saved.Push(_masks.Count);
            return _masks.Count;
        }

        internal void Restore(int index)
        {
            ReturnFrom(index);
            _saved.Pop();
        }

        internal void Track(CoverageMask mask) => _masks.Add(mask);

        internal void ReleaseTemporary(CoverageMask mask)
        {
            int start = _saved.Count == 0 ? 0 : _saved.Peek();
            int index = _masks.IndexOf(mask, start);
            if (index < 0) return;
            _masks[index] = null;
            mask.Return();
        }

        internal void ReturnFrom(int index)
        {
            for (int i = _masks.Count - 1; i >= index; i--) _masks[i]?.Return();
            _masks.RemoveRange(index, _masks.Count - index);
        }

        public void Dispose()
        {
            ReturnFrom(0);
            _current = _parent;
        }
    }

    private static CoverageMask RasterizeClip(IReadOnlyList<List<Point>> paths, bool evenOdd,
        RasterFrame frame)
    {
        ClipScratchScope? scope = ClipScratchScope.Current;
        CoverageMask mask = RasterizeFill(paths, evenOdd, frame, rent: scope is not null);
        scope?.Track(mask);
        return mask;
    }
}
