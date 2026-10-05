namespace KillerPdf.Engine.Rendering;

public sealed partial class PdfPageRenderer
{
    /// <summary>Applies coincident opaque process paints through their shared edge coverage once.</summary>
    private sealed class OpaqueFillSequence(RasterSurface pixels, RasterFrame frame,
        CancellationToken cancellationToken) : IDisposable
    {
        // Bound retained geometry independently of document complexity. Larger paths use
        // ordinary painting, and only one rented coverage mask can be pending at a time.
        private const int MaximumPoints = 4096;
        private CoverageMask? _mask;
        private Point[][]? _path;
        private Color _color;
        private bool _evenOdd;
        private RendererBlendMode _blendMode;
        private int _intent;
        private PdfColorTransform? _inputProfile;
        private (int Left, int Top, int Right, int Bottom) _bounds;
        private IReadOnlyList<ClipRegion> _clips = [];
        private bool _fractionalClips;

        internal bool TryPaint(IReadOnlyList<List<Point>> path, bool evenOdd, GraphicsState state)
        {
            Color color = state.PaintFill;
            if (pixels.Ink is null || pixels.GroupAlpha is not null || pixels.GroupShape is not null
                || state.FillAlpha != 1 || state.GraphicsSoftMask is not null || state.Knockout is not null
                || state.FillPattern is not null || state.AlphaIsShape
                || state.BlendMode is not (RendererBlendMode.Normal or RendererBlendMode.Compatible)
                || state.FillColorSpace is not { IsDeviceCmyk: true, Components: 4, IsIccBased: false, Palette: null,
                    Converter: null, MultiConverter: null }
                || color.SpotName is not null || (color.OverprintComponents & (32 | 64)) != 0)
                return false;

            int points = 0;
            foreach (List<Point> subpath in path)
            {
                if (subpath.Count > MaximumPoints - points) return false;
                points += subpath.Count;
            }
            var bounds = (pixels.Left, pixels.Top, pixels.Right, pixels.Bottom);
            bool fractionalClips = false;
            foreach (ClipRegion clip in state.Clips)
            {
                if (clip.Mask.Coverage is not null)
                {
                    // A parent render cannot release its clip until this stream finishes.
                    if (ClipScratchScope.Current?.IsTrackedByAncestor(clip.Mask) != true)
                        return false;
                    fractionalClips = true;
                }
                bounds.Left = Math.Max(bounds.Left, clip.Mask.Left);
                bounds.Top = Math.Max(bounds.Top, clip.Mask.Top);
                bounds.Right = Math.Min(bounds.Right, clip.Mask.Right);
                bounds.Bottom = Math.Min(bounds.Bottom, clip.Mask.Bottom);
            }

            // Resolve while the instruction's input-profile scope is active. Flushing
            // later, including after Q, must not reinterpret this paint's native ink.
            color = color with { Ink = pixels.GetInk(color), InkProfile = pixels.InkProfile };
            if (_mask is not null && _evenOdd == evenOdd && _bounds == bounds
                && _fractionalClips == fractionalClips
                && (!fractionalClips || SameClips(state.Clips))
                && _blendMode == state.BlendMode && _intent == state.RenderingIntent
                && ReferenceEquals(_inputProfile, pixels.InputProfile)
                && (_color.OverprintComponents & ~15) == (color.OverprintComponents & ~15)
                && SamePath(path))
            {
                int previousPreserve = PreservedChannels(_color), preserve = PreservedChannels(color);
                uint byteMask = 0;
                for (int channel = 0; channel < 4; channel++)
                    if ((preserve & (1 << channel)) != 0) byteMask |= 255u << (channel * 8);
                _color = color with
                {
                    Ink = (color.Ink!.Value & ~byteMask) | (_color.Ink!.Value & byteMask),
                    OverprintComponents = (byte)((color.OverprintComponents & ~15)
                        | (previousPreserve & preserve))
                };
                return true;
            }

            Flush();
            CoverageMask mask = RasterizeFill(path, evenOdd, frame, rent: true);
            try
            {
                // Integer rectangles already have exact coverage and need no pending state.
                if (mask.Coverage is null)
                {
                    PaintCoverage(pixels, frame.Width, mask, color, 1, state.BlendMode,
                        state.Clips, null, null, cancellationToken);
                    return true;
                }
                Point[][] savedPath = new Point[path.Count][];
                for (int index = 0; index < path.Count; index++) savedPath[index] = [.. path[index]];
                _path = savedPath;
                _color = color;
                _evenOdd = evenOdd;
                _blendMode = state.BlendMode;
                _intent = state.RenderingIntent;
                _inputProfile = pixels.InputProfile;
                _bounds = bounds;
                _fractionalClips = fractionalClips;
                _clips = fractionalClips ? state.Clips :
                    [new ClipRegion(CoverageMask.Rectangle(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom))];
                _mask = mask;
            }
            finally
            {
                if (!ReferenceEquals(_mask, mask)) mask.Return();
            }
            return true;
        }

        private static int PreservedChannels(Color color) =>
            (color.OverprintComponents & 16) != 0 ? color.OverprintComponents & 15 : 0;

        private bool SameClips(IReadOnlyList<ClipRegion> clips)
        {
            if (_clips.Count != clips.Count) return false;
            for (int index = 0; index < clips.Count; index++)
                if (!ReferenceEquals(_clips[index].Mask, clips[index].Mask)) return false;
            return true;
        }

        private bool SamePath(IReadOnlyList<List<Point>> path)
        {
            if (_path!.Length != path.Count) return false;
            for (int subpath = 0; subpath < path.Count; subpath++)
            {
                Point[] saved = _path[subpath];
                List<Point> current = path[subpath];
                if (saved.Length != current.Count) return false;
                for (int index = 0; index < saved.Length; index++)
                    if (saved[index] != current[index]) return false;
            }
            return true;
        }

        internal void Flush()
        {
            CoverageMask? mask = _mask;
            if (mask is null) return;
            _mask = null;
            _path = null;
            try
            {
                PaintCoverage(pixels, frame.Width, mask, _color, 1, _blendMode,
                    _clips, null, null, cancellationToken);
            }
            finally
            {
                mask.Return();
                _clips = [];
                _fractionalClips = false;
                _inputProfile = null;
            }
        }

        public void Dispose()
        {
            if (!cancellationToken.IsCancellationRequested) Flush();
            _mask?.Return();
            _mask = null;
        }
    }
}
