using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KillerPDF.Services;

namespace KillerPDF.Controls
{
    /// <summary>Which motif an element paints when the theme has one (Trash Polka).</summary>
    internal enum MotifPart { None, Window, Chrome, Split, Popup }

    /// <summary>
    /// Paints Trash Polka's motif into an app's own layout. An app marks its window root <c>Window</c>, each raised
    /// chrome pane (title bar, toolbar, sidebar) <c>Chrome</c>, and the second pane in split view <c>Split</c>, and
    /// names the brush key the element normally uses:
    /// <code>&lt;Border c:ThemeMotif.Part="Chrome" c:ThemeMotif.BaseBrush="PaneBrush"/&gt;</code>
    /// While the theme publishes <c>ThemeMotif</c> = true, the element paints its base color with its part of the motif:
    /// the window the full sleeve, the chrome panes ONE continuous piece mapped to the window so the bars and sidebar
    /// join up, the split pane its own corner cluster. Under every other theme the element keeps its base brush.
    /// <c>MotifBrush</c> names a different brush key to paint under the motif, for an element whose everyday brush is
    /// ink or transparent but which is a charcoal card under Trash Polka; <c>BaseBrush</c> is still what it restores.
    /// A <c>Window</c> element anywhere in the window shows its own stretch of the one sleeve, so a document card
    /// lines up with the window around it.
    /// </summary>
    internal static class ThemeMotif
    {
        public static readonly DependencyProperty PartProperty = DependencyProperty.RegisterAttached(
            "Part", typeof(MotifPart), typeof(ThemeMotif), new PropertyMetadata(MotifPart.None, OnPartChanged));

        public static readonly DependencyProperty BaseBrushProperty = DependencyProperty.RegisterAttached(
            "BaseBrush", typeof(string), typeof(ThemeMotif), new PropertyMetadata("BackgroundBrush", OnBaseBrushChanged));

        // A template trigger can flip BaseBrush with Part (the active document tab); repaint on the new base.
        private static void OnBaseBrushChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not FrameworkElement element || GetPart(element) == MotifPart.None || element.GetValue(PaintedProperty) is null) return;
            element.SetValue(PaintedProperty, Unstamped);
            if (element.IsLoaded) Paint(element);
        }

        // What the element was last painted with (a PaintKey), or Unstamped to force the next paint; null when it shows
        // its own background.
        private static readonly DependencyProperty PaintedProperty = DependencyProperty.RegisterAttached(
            "Painted", typeof(object), typeof(ThemeMotif), new PropertyMetadata(null));

        private static readonly object Unstamped = new();

        // Everything a painted brush depends on. The window size is not in it: the pinned piece never reads it, so a
        // pane whose own place is unchanged keeps its brush through a window resize.
        private readonly record struct PaintKey(MotifPart Part, Color Accent, Color Base, Rect Pane, Size Local, double Opacity, Drawing Piece);

        /// <summary>
        /// Inherited: set on the second document pane in split view. Its <c>Window</c> parts (the card and its active
        /// tab) then draw the second pane's own piece, pinned to the window like the first, so each pane's image runs up
        /// into its own active tab.
        /// </summary>
        public static readonly DependencyProperty SecondPaneProperty = DependencyProperty.RegisterAttached(
            "SecondPane", typeof(bool), typeof(ThemeMotif), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

        public static bool GetSecondPane(DependencyObject element) => (bool)element.GetValue(SecondPaneProperty);
        public static void SetSecondPane(DependencyObject element, bool value) => element.SetValue(SecondPaneProperty, value);

        // Whether the element's Background was a local value before the first paint. Template- and trigger-driven
        // backgrounds (the document tabs) are not, and Restore clears back to them instead of pinning a brush key.
        private static readonly DependencyProperty HadLocalBackgroundProperty = DependencyProperty.RegisterAttached(
            "HadLocalBackground", typeof(bool), typeof(ThemeMotif), new PropertyMetadata(true));

        public static MotifPart GetPart(DependencyObject element) => (MotifPart)element.GetValue(PartProperty);
        public static void SetPart(DependencyObject element, MotifPart value) => element.SetValue(PartProperty, value);
        public static string GetBaseBrush(DependencyObject element) => (string)element.GetValue(BaseBrushProperty);
        public static void SetBaseBrush(DependencyObject element, string value) => element.SetValue(BaseBrushProperty, value);

        public static readonly DependencyProperty MotifBrushProperty = DependencyProperty.RegisterAttached(
            "MotifBrush", typeof(string), typeof(ThemeMotif), new PropertyMetadata(null));

        public static string? GetMotifBrush(DependencyObject element) => (string?)element.GetValue(MotifBrushProperty);
        public static void SetMotifBrush(DependencyObject element, string? value) => element.SetValue(MotifBrushProperty, value);

        /// <summary>
        /// A working screen (a terminal, the text editor, a monitor tile, a tool's table): the element shows its stretch of
        /// the sleeve at ScreenOpacity, fainter than a pane, because it is read line by line.
        /// </summary>
        public static readonly DependencyProperty ScreenProperty = DependencyProperty.RegisterAttached(
            "Screen", typeof(bool), typeof(ThemeMotif), new PropertyMetadata(false, OnBaseBrushChanged));

        public static bool GetScreen(DependencyObject element) => (bool)element.GetValue(ScreenProperty);
        public static void SetScreen(DependencyObject element, bool value) => element.SetValue(ScreenProperty, value);

        /// <summary>
        /// A tile on a dashboard (a monitor tile and the graph well inside it): the darker face sits on the lighter page,
        /// so its ink shows at CellOpacity, fainter again than a screen's. Takes precedence over Screen.
        /// </summary>
        public static readonly DependencyProperty CellProperty = DependencyProperty.RegisterAttached(
            "Cell", typeof(bool), typeof(ThemeMotif), new PropertyMetadata(false, OnBaseBrushChanged));

        public static bool GetCell(DependencyObject element) => (bool)element.GetValue(CellProperty);
        public static void SetCell(DependencyObject element, bool value) => element.SetValue(CellProperty, value);

        /// <summary>
        /// The page a document is written on (the note): the element keeps its part's place and piece, but shows it at
        /// TrashPolkaInk.PageOpacity. Takes precedence over the part's own opacity, Screen and Cell.
        /// </summary>
        public static readonly DependencyProperty PageProperty = DependencyProperty.RegisterAttached(
            "Page", typeof(bool), typeof(ThemeMotif), new PropertyMetadata(false, OnBaseBrushChanged));

        public static bool GetPage(DependencyObject element) => (bool)element.GetValue(PageProperty);
        public static void SetPage(DependencyObject element, bool value) => element.SetValue(PageProperty, value);

        /// <summary>The rendered view of a document, like Page but at TrashPolkaInk.PreviewOpacity, so the user can set
        /// the two apart.</summary>
        public static readonly DependencyProperty PreviewProperty = DependencyProperty.RegisterAttached(
            "Preview", typeof(bool), typeof(ThemeMotif), new PropertyMetadata(false, OnBaseBrushChanged));

        public static bool GetPreview(DependencyObject element) => (bool)element.GetValue(PreviewProperty);
        public static void SetPreview(DependencyObject element, bool value) => element.SetValue(PreviewProperty, value);

        private static readonly Dictionary<MotifPart, (Color Accent, Size Size, Drawing Piece)> Pieces = [];

        // The bitmap pieces, one per set of layers, tinted for the current accent only. A new accent is composed on a
        // worker (TrashPolkaRaster.TintAsync); the element keeps its brush until the new piece lands.
        private sealed class TintedPiece
        {
            internal Color Accent;
            internal Drawing? Piece;
            internal ComposeJob? Pending;
            internal bool Failed;
        }

        private sealed class ComposeJob(TintedPiece target, TrashPolkaRaster.Layers layers, Size size, Color accent, TrashPolkaRaster.Tinting work)
        {
            internal TintedPiece Target { get; } = target;
            internal TrashPolkaRaster.Layers Layers { get; } = layers;
            internal Size Size { get; } = size;
            internal Color Accent { get; } = accent;
            internal TrashPolkaRaster.Tinting Work { get; } = work;
            internal bool Done;
        }

        // Replaced rather than cleared on release: ConditionalWeakTable.Clear is not public on net48.
        private static System.Runtime.CompilerServices.ConditionalWeakTable<TrashPolkaRaster.Layers, TintedPiece> RasterPieces = new();
        private static readonly List<ComposeJob> Jobs = [];

        private static bool _releaseHooked;

        // Once any element takes a part: after every theme change, when the app's theme carries no motif, every element
        // has already restored its plain background (Paint), so the pictures behind them are dropped too.
        private static void HookRelease()
        {
            if (_releaseHooked) return;
            _releaseHooked = true;
            ThemeManager.ThemeChanged += () => System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.ApplicationIdle, () =>
                {
                    if (Application.Current?.TryFindResource("ThemeMotif") is true) return;
                    ReleaseAll();
                });
        }

        /// <summary>Drops every picture: the vector pieces, the tinted bitmaps and the layers (a later motif theme
        /// decodes them again from the disk cache).</summary>
        internal static void ReleaseAll()
        {
            Pieces.Clear();
            RasterPieces = new();
            TrashPolkaRaster.ReleaseAll();
            Releases++;
        }

        internal static int Releases;

        /// <summary>Accent pieces still being composed (the self-test waits on them).</summary>
        internal static int ComposesPending => Jobs.Count;

        // The piece for this accent, or null while a worker composes it.
        private static Drawing? Tinted(TrashPolkaRaster.Layers layers, Size size, Color tint)
        {
            var state = RasterPieces.GetValue(layers, _ => new TintedPiece());
            if (state.Piece is not null && state.Accent == tint && state.Pending is null) return state.Piece;
            if (state.Pending is { } running && running.Accent == tint) return null;
            if (state.Piece is not null && state.Accent == tint) { state.Pending = null; return state.Piece; }
            if (state.Failed) return null;
            var job = new ComposeJob(state, layers, size, tint, TrashPolkaRaster.TintAsync(layers, tint));
            state.Pending = job;
            Jobs.Add(job);
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            job.Work.Work.ContinueWith(_ => dispatcher.BeginInvoke(() => { if (Land(job)) Retune(); }), TaskScheduler.Default);
            return null;
        }

        /// <summary>Waits on the composes until all finish or <paramref name="budgetMs"/> runs out for the lot, and says
        /// which finished. One deadline covers every job, so hung composes never add their bounds together.</summary>
        internal static bool[] WaitWithin(Task[] work, int budgetMs)
        {
            var finished = new bool[work.Length];
            var timer = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < work.Length; i++)
            {
                int left = Math.Max(0, budgetMs - (int)timer.ElapsedMilliseconds);
                try { finished[i] = work[i].Wait(left); } catch (AggregateException) { finished[i] = true; }
            }
            return finished;
        }

        /// <summary>The longest the crossfade waits on the pieces' composes, all together, in milliseconds.</summary>
        internal const int ComposeWaitMs = 1000;

        // Swaps a finished piece in, unless a newer accent has replaced the job; the previous tint is dropped with it.
        private static bool Land(ComposeJob job)
        {
            if (job.Done) return false;
            job.Done = true;
            Jobs.Remove(job);
            var state = job.Target;
            if (!ReferenceEquals(state.Pending, job))
            {
                TrashPolkaRaster.Discard(job.Work);
                return false;
            }
            state.Pending = null;
            if (job.Work.Work.Status != TaskStatus.RanToCompletion) { TrashPolkaRaster.Discard(job.Work); state.Failed = true; return true; }
            state.Accent = job.Accent;
            var timer = System.Diagnostics.Stopwatch.StartNew();
            state.Piece = TrashPolkaRaster.ToDrawing(job.Size, job.Work);
            LandMs += timer.Elapsed.TotalMilliseconds;
            ComposeCalls++;
            return true;
        }

        /// <summary>
        /// Waits for the accent pieces the last theme change started and paints them. The accent crossfade calls it
        /// between applying the accent and taking its incoming snapshot, so the snapshot already shows the new ink.
        /// The pieces compose in parallel, so the wait is one piece's pass rather than every piece's in turn.
        /// </summary>
        internal static void FinishComposes()
        {
            if (Jobs.Count == 0) return;
            var timer = System.Diagnostics.Stopwatch.StartNew();
            bool landed = false;
            var jobs = Jobs.ToArray();
            // A compose that hangs never freezes the window: past the bound the snapshot takes the old ink and the job
            // lands on its own when it finishes.
            var finished = WaitWithin(Array.ConvertAll(jobs, j => j.Work.Work), ComposeWaitMs);
            for (int i = 0; i < jobs.Length; i++)
                if (finished[i]) landed |= Land(jobs[i]);
            ComposeMs += timer.Elapsed.TotalMilliseconds;
            if (landed) Retune();
        }

        // Loaded motif elements, for the layer trim. An element leaves on Unloaded or when its part goes to None.
        private static readonly HashSet<FrameworkElement> Live = [];

        private static readonly DependencyProperty LayersKeyProperty = DependencyProperty.RegisterAttached(
            "LayersKey", typeof(object), typeof(ThemeMotif), new PropertyMetadata(null));

        private static bool _sweepQueued;

        private static void QueueSweep()
        {
            if (_sweepQueued) return;
            _sweepQueued = true;
            System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, () =>
            {
                _sweepQueued = false;
                var inUse = new HashSet<TrashPolkaRaster.LayerKey>();
                foreach (var element in Live)
                    if (element.GetValue(LayersKeyProperty) is TrashPolkaRaster.LayerKey key) inUse.Add(key);
                TrashPolkaRaster.Trim(inUse);
            });
        }

        // Subscriptions run only while the element is loaded, and every path out (Unloaded, Part = None) removes all of
        // them: the static ThemeChanged and Retuned events and the property watchers would otherwise keep every popup
        // and menu ever built alive.
        private static void OnPartChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not FrameworkElement element) return;
            HookRelease();
            element.Loaded -= OnLoaded;
            element.SizeChanged -= Refresh;
            element.IsVisibleChanged -= OnVisibleChanged;
            element.Unloaded -= OnUnloaded;
            Detach(element);
            if ((MotifPart)e.NewValue == MotifPart.None) { Restore(element); return; }
            element.Loaded += OnLoaded;
            element.SizeChanged += Refresh;
            element.IsVisibleChanged += OnVisibleChanged;
            element.Unloaded += OnUnloaded;
            if (element.IsLoaded)
            {
                Attach(element);
                Paint(element);
            }
        }

        // A repaint that lands while the element is collapsed has no size to paint and leaves the plain background. WPF
        // raises no SizeChanged when the element comes back at the size it had before it was collapsed, so showing it
        // again is the cue to paint, once layout has given it its size.
        private static void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (sender is not FrameworkElement element || e.NewValue is not true) return;
            element.Dispatcher.BeginInvoke(() => Paint(element), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private static void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement element) return;
            Attach(element);
            Paint(element);
        }

        private static void OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement element) return;
            Detach(element);
            // The vector piece is live (its accent brushes recolor), so a brush built on it stays reachable from the
            // shared piece; hand the element its own brush back until it loads again. Frozen bitmap brushes stay.
            if (element.GetValue(PaintedBrushProperty) is Brush { IsFrozen: false }) Restore(element);
        }

        private static void Attach(FrameworkElement element)
        {
            var handler = Rebind(element);
            ThemeManager.ThemeChanged -= handler;
            ThemeManager.ThemeChanged += handler;
            Retuned -= handler;
            Retuned += handler;
            WatchBackground(element);
            WatchHighContrast(element);
            Live.Add(element);
        }

        private static void Detach(FrameworkElement element)
        {
            if (Handlers.TryGetValue(element, out var handler))
            {
                ThemeManager.ThemeChanged -= handler;
                Retuned -= handler;
            }
            UnwatchBackground(element);
            UnwatchHighContrast(element);
            Live.Remove(element);
        }

        // High contrast swaps every palette brush for a system brush; the motif steps aside so the element shows it, and
        // comes back when high contrast turns off.
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<FrameworkElement, object> WatchedContrast = new();

        private static void WatchHighContrast(FrameworkElement element)
        {
            if (WatchedContrast.TryGetValue(element, out _)) return;
            WatchedContrast.Add(element, element);
            System.ComponentModel.DependencyPropertyDescriptor
                .FromProperty(KillerFamily.Accessibility.HighContrastResources.IsActiveProperty, element.GetType())
                ?.AddValueChanged(element, OnHighContrastChanged);
        }

        private static void UnwatchHighContrast(FrameworkElement element)
        {
            if (!WatchedContrast.TryGetValue(element, out _)) return;
            WatchedContrast.Remove(element);
            System.ComponentModel.DependencyPropertyDescriptor
                .FromProperty(KillerFamily.Accessibility.HighContrastResources.IsActiveProperty, element.GetType())
                ?.RemoveValueChanged(element, OnHighContrastChanged);
        }

        private static void OnHighContrastChanged(object? sender, EventArgs e)
        {
            if (sender is FrameworkElement element) Paint(element);
        }

        // Apps restyle their own panes (KillerPDF resets the document card's Background when a pane takes focus), which
        // would wipe the motif and leave the stamp claiming it is still there. Any outside Background change while the
        // motif is painted repaints it.
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<FrameworkElement, object> Watched = new();

        private static void WatchBackground(FrameworkElement element)
        {
            if (BackgroundProperty(element) is not { } property || Watched.TryGetValue(element, out _)) return;
            Watched.Add(element, property);
            System.ComponentModel.DependencyPropertyDescriptor.FromProperty(property, element.GetType())
                ?.AddValueChanged(element, OnBackgroundChanged);
        }

        private static void UnwatchBackground(FrameworkElement element)
        {
            if (!Watched.TryGetValue(element, out var property)) return;
            Watched.Remove(element);
            System.ComponentModel.DependencyPropertyDescriptor.FromProperty((DependencyProperty)property, element.GetType())
                ?.RemoveValueChanged(element, OnBackgroundChanged);
        }

        private static void OnBackgroundChanged(object? sender, EventArgs e)
        {
            if (sender is not FrameworkElement element || element.GetValue(PaintedProperty) is null) return;
            if (BackgroundProperty(element) is { } property && ReferenceEquals(element.GetValue(property), element.GetValue(PaintedBrushProperty))) return;
            element.ClearValue(PaintedProperty);
            // Repaint after the change that raised this finishes: setting the background again inside it threw
            // "not a context for this Freezable" (full screen sets the document pane's backdrop).
            element.Dispatcher.BeginInvoke(() => Paint(element), System.Windows.Threading.DispatcherPriority.Render);
        }

        private static DependencyProperty? BackgroundProperty(FrameworkElement element) => element switch
        {
            Border => Border.BackgroundProperty,
            Panel => Panel.BackgroundProperty,
            Control => Control.BackgroundProperty,
            _ => null,
        };

        private static readonly DependencyProperty PaintedBrushProperty = DependencyProperty.RegisterAttached(
            "PaintedBrush", typeof(Brush), typeof(ThemeMotif), new PropertyMetadata(null));

        // One handler per element, kept so Detach can remove exactly it.
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<FrameworkElement, Action> Handlers = new();

        // ---- Every other window --------------------------------------------------------------------------------------

        // Which part a surface painting a palette brush becomes, so every window mirrors the app: dark surfaces carry the
        // background piece, canvas-gray preview frames the pane piece, menus and bars the popup piece.
        private static readonly (string Key, MotifPart Part)[] SurfaceParts =
        [
            ("BackgroundBrush", MotifPart.Chrome), ("TitleBarBrush", MotifPart.Chrome),
            ("BgCanvas", MotifPart.Window), ("ContentPaneBrush", MotifPart.Window),
            ("MenuBackgroundBrush", MotifPart.Popup), ("AnnotationBarBrush", MotifPart.Popup),
            // Dropdown lists: without the popup piece an open list was flat gray and blended into the panel under it.
            ("ComboPopupBrush", MotifPart.Popup),
        ];

        private static (string Key, MotifPart Part)? SurfaceOf(FrameworkElement el)
        {
            if (!el.IsHitTestVisible || GetPart(el) != MotifPart.None) return null;
            var brush = el switch { Border b => b.Background, Panel p => p.Background, Control c => c.Background, _ => null };
            if (brush is not SolidColorBrush { Color.A: 255 }) return null;
            foreach (var s in SurfaceParts)
                if (ReferenceEquals(el.TryFindResource(s.Key), brush)) return s;
            return null;
        }

        private static void Mark(FrameworkElement el, (string Key, MotifPart Part) s)
        {
            SetBaseBrush(el, s.Key);
            SetPart(el, s.Part);
        }

        // Every opaque palette surface in a tree. Stacked opaque surfaces each paint the same pinned piece, and the top
        // one hides the rest, so marking them all never doubles the ink.
        private static void MarkTree(FrameworkElement root, int budget = 6000)
        {
            var queue = new Queue<DependencyObject>();
            queue.Enqueue(root);
            while (queue.Count > 0 && budget-- > 0)
            {
                var node = queue.Dequeue();
                if (node is FrameworkElement el && SurfaceOf(el) is { } s) Mark(el, s);
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) queue.Enqueue(VisualTreeHelper.GetChild(node, i));
            }
        }

        private static bool _allWindows;

        /// <summary>
        /// Gives every window the app opens (dialogs, print preview, pickers) the background piece, without marking each
        /// one: when a window loads, its outermost opaque Border or Panel painting a palette brush is marked
        /// <c>Chrome</c> with that brush as its base. A window whose root is already marked (the main window) is left
        /// alone. Call once at startup.
        /// </summary>
        public static void EnableForAllWindows()
        {
            if (_allWindows) return;
            _allWindows = true;
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnWindowLoaded));
            // Flyouts and context menus live in popups, not windows: the top element of each popup gets its own piece.
            EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnPopupContentLoaded));
        }

        private static void OnPopupContentLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement element || element is Window) return;
            if (IsPopupTop(element)) { MarkTree(element); return; }
            // Bars built at runtime inside a window (annotation tool bars) paint the menu brush: mark them as they load.
            if (SurfaceOf(element) is { Part: MotifPart.Popup } s) Mark(element, s);
        }

        // True for the element a popup hosts: its visual parent (or the one above) is WPF's internal PopupRoot.
        private static bool IsPopupTop(FrameworkElement element)
        {
            DependencyObject? up = VisualTreeHelper.GetParent(element);
            for (int i = 0; i < 2 && up is not null; i++, up = VisualTreeHelper.GetParent(up))
                if (up.GetType().Name == "PopupRoot") return true;
            return false;
        }

        private static void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is not Window window || window.Content is not FrameworkElement root) return;
            if (GetPart(root) != MotifPart.None) return;
            MarkTree(root);
        }

        private static bool _prewarmQueued;

        /// <summary>The hidden argument that runs this exe as the cache builder (<see cref="TryRunCacheBuilder"/>).</summary>
        internal const string CacheBuilderArgument = "--build-motif-cache";

        /// <summary>
        /// Once the window is shown and the dispatcher has gone idle, and two seconds more, checks the disk cache for every
        /// piece this window would paint at its screen size and scale. If any is missing it starts this exe again as a
        /// windowless child at idle priority (<see cref="TryRunCacheBuilder"/>), which renders and saves them and exits,
        /// so the render memory dies with it and this process does no work beyond the check. Runs once per process.
        /// </summary>
        internal static void PrewarmWhenIdle(Window window)
        {
            if (_prewarmQueued || MotifDiskCache.Root is not { } root || CurrentExe() is not { } exe) return;
            _prewarmQueued = true;
            window.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, async () =>
            {
                await Task.Delay(2000);
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                if (window.Content is not FrameworkElement content || !content.IsLoaded) { _prewarmQueued = false; return; }
                var size = InkSize(content);
                double dpi = VisualTreeHelper.GetDpi(content).DpiScaleX;
                PrewarmTask = Task.Run(async () =>
                {
                    var keys = PrewarmSeeds.Select(seed => new TrashPolkaRaster.LayerKey(seed, size, TrashPolkaInk.Scale, dpi)).ToArray();
                    if (keys.All(TrashPolkaRaster.IsCached)) return;
                    var start = new System.Diagnostics.ProcessStartInfo(exe)
                    {
                        UseShellExecute = false, CreateNoWindow = true,
                        WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
                    };
                    start.Arguments = string.Join(" ", new[] { CacheBuilderArgument, root,
                        size.Width.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        size.Height.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        dpi.ToString("R", System.Globalization.CultureInfo.InvariantCulture) }.Select(QuoteArgument));
                    using var child = new System.Diagnostics.Process { StartInfo = start, EnableRaisingEvents = true };
                    var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    child.Exited += (_, _) => exited.TrySetResult(true);
                    if (!child.Start()) return;
                    try { child.PriorityClass = System.Diagnostics.ProcessPriorityClass.Idle; } catch (Exception) { }
                    if (child.HasExited) exited.TrySetResult(true);
                    await exited.Task;
                });
            });
        }

        /// <summary>The running exe's full path, or null when it cannot be read.</summary>
        private static string? CurrentExe()
        {
            try { using var self = System.Diagnostics.Process.GetCurrentProcess(); return self.MainModule?.FileName; }
            catch (Exception) { return null; }
        }

        /// <summary>One argument quoted for a Windows command line by the CommandLineToArgvW rules: a backslash run
        /// is doubled before a quote or the closing quote, and an argument with no space, tab, newline or quote is left
        /// bare, so the child reads back exactly what was passed.</summary>
        internal static string QuoteArgument(string arg)
        {
            if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0) return arg;
            var sb = new System.Text.StringBuilder("\"");
            int slashes = 0;
            foreach (char c in arg)
            {
                if (c == '\\') { slashes++; continue; }
                sb.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
                slashes = 0;
                sb.Append(c);
            }
            sb.Append('\\', slashes * 2).Append('"');
            return sb.ToString();
        }

        private static readonly int[] PrewarmSeeds = [TrashPolkaInk.WindowSeed, TrashPolkaInk.SplitSeed, TrashPolkaInk.BackgroundSeed];

        /// <summary>
        /// The child side of <see cref="PrewarmWhenIdle"/>: <c>--build-motif-cache &lt;root&gt; &lt;width&gt; &lt;height&gt; &lt;dpi&gt;</c>
        /// builds and saves the missing pieces with no window and returns the exit code, or null for any other command line.
        /// A named mutex keeps two builders from working at once; the second one just exits, and the atomic writes cover
        /// a builder that dies halfway.
        /// </summary>
        internal static int? TryRunCacheBuilder(string[] args, string appName)
        {
            if (args.Length != 5 || args[0] != CacheBuilderArgument) return null;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            if (!double.TryParse(args[2], System.Globalization.NumberStyles.Float, inv, out double w)
                || !double.TryParse(args[3], System.Globalization.NumberStyles.Float, inv, out double h)
                || !double.TryParse(args[4], System.Globalization.NumberStyles.Float, inv, out double dpi)) return 2;
            using var gate = new System.Threading.Mutex(false, "Local\\" + appName + "-motif-cache-builder");
            try { if (!gate.WaitOne(0)) return 0; }
            catch (System.Threading.AbandonedMutexException) { }
            try
            {
                MotifDiskCache.Root = System.IO.Path.GetFullPath(args[1]);
                var keys = PrewarmSeeds.Select(seed => new TrashPolkaRaster.LayerKey(seed, new Size(w, h), TrashPolkaInk.Scale, dpi)).ToArray();
                TrashPolkaRaster.Prewarm(keys).Wait();
                return 0;
            }
            finally { gate.ReleaseMutex(); }
        }

        /// <summary>Test hook: the running prewarm, or null before it is queued.</summary>
        internal static Task? PrewarmTask;

        /// <summary>Test hook: the screen size, in DIPs, the ink is drawn for.</summary>
        internal static Size? InkScreenOverride { get; set; }

        // The largest screen any motif window has covered, in DIPs, starting at the primary screen. It only grows, so
        // moving a window to a smaller monitor never redraws the art, and a larger secondary monitor or a window
        // spanning several never shows a bare right or bottom edge.
        private static Size _inkScreen;

        private static Size InkSize(FrameworkElement element)
        {
            Size screen;
            if (InkScreenOverride is { } forced) screen = forced;
            else
            {
                if (_inkScreen.IsEmpty || _inkScreen.Width == 0)
                    _inkScreen = new Size(SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);
                if (CoveredMonitors(element) is { } covered)
                    _inkScreen = new Size(Math.Max(_inkScreen.Width, covered.Width), Math.Max(_inkScreen.Height, covered.Height));
                screen = _inkScreen;
            }
            return new Size(Math.Ceiling(screen.Width / TrashPolkaInk.Scale), Math.Ceiling(screen.Height / TrashPolkaInk.Scale));
        }

        // The bounds of every monitor the element's window touches, in the element's DIPs.
        private static Size? CoveredMonitors(FrameworkElement element)
        {
            if (PresentationSource.FromVisual(element) is not System.Windows.Interop.HwndSource { Handle: var hwnd } || hwnd == IntPtr.Zero
                || !GetWindowRect(hwnd, out var window)) return null;
            _covering = window;
            _covered = default;
            _anyCovered = false;
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, CollectMonitor, IntPtr.Zero);
            if (!_anyCovered) return null;
            var scale = VisualTreeHelper.GetDpi(element);
            return new Size((_covered.Right - _covered.Left) / scale.DpiScaleX, (_covered.Bottom - _covered.Top) / scale.DpiScaleY);
        }

        private static NativeRect _covering, _covered;
        private static bool _anyCovered;
        private static readonly MonitorEnumProc CollectMonitor = (IntPtr monitor, IntPtr hdc, ref NativeRect rc, IntPtr data) =>
        {
            if (rc.Left >= _covering.Right || rc.Right <= _covering.Left || rc.Top >= _covering.Bottom || rc.Bottom <= _covering.Top) return true;
            _covered = _anyCovered
                ? new NativeRect { Left = Math.Min(_covered.Left, rc.Left), Top = Math.Min(_covered.Top, rc.Top), Right = Math.Max(_covered.Right, rc.Right), Bottom = Math.Max(_covered.Bottom, rc.Bottom) }
                : rc;
            _anyCovered = true;
            return true;
        };

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct NativeRect { public int Left, Top, Right, Bottom; }

        private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, ref NativeRect rc, IntPtr data);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

        // Test counters (Shell/SelfTestThemeMotif.cs).
        internal static int ComposeCalls, RasterPaints, VectorPaints;
        internal static double ComposeMs, LandMs;

        private static event Action? Retuned;

        /// <summary>Repaints every motif element after TrashPolkaInk's opacities change.</summary>
        internal static void Retune() => Retuned?.Invoke();

        private static Action Rebind(FrameworkElement element)
        {
            var handler = Handlers.GetValue(element, e => () => Paint(e));
            return handler;
        }

        private static void Refresh(object sender, EventArgs e)
        {
            if (sender is FrameworkElement element) Paint(element);
        }

        private static void Paint(FrameworkElement element)
        {
            var part = GetPart(element);
            if (part == MotifPart.Window && GetSecondPane(element)) part = MotifPart.Split;
            if (part == MotifPart.None || element.TryFindResource("ThemeMotif") is not true
                || KillerFamily.Accessibility.HighContrastResources.GetIsActive(element) || SystemParameters.HighContrast
                || element.TryFindResource(GetMotifBrush(element) ?? GetBaseBrush(element)) is not SolidColorBrush baseBrush
                || element.TryFindResource("PrimaryBrush") is not SolidColorBrush accent
                || element.ActualWidth <= 0 || element.ActualHeight <= 0)
            {
                Restore(element);
                return;
            }

            Rect pane;
            Size window;
            Transform? unflip = null;
            // Popups float free of the window, so they draw from their own corner; everything else is pinned to it.
            // A pane surface inside a popup (a gray swatch in a flyout) has the window as its owner but is not under the
            // window's root, so it has no place there and draws from its own corner too; pinning it threw.
            var host = Window.GetWindow(element);
            if (part is MotifPart.Popup || host is { Content: FrameworkElement outside } && !outside.IsAncestorOf(element))
            {
                pane = new Rect(0, 0, element.ActualWidth, element.ActualHeight);
                window = pane.Size;
            }
            else
            {
                if (host is not { Content: FrameworkElement root }) return;
                (pane, unflip) = MotifPlacement.PlaceInWindow(element, root);
                window = new Size(root.ActualWidth, root.ActualHeight);
            }

            double opacity = part switch
            {
                _ when GetPreview(element) => TrashPolkaInk.PreviewOpacity,
                _ when GetPage(element) => TrashPolkaInk.PageOpacity,
                MotifPart.Chrome => TrashPolkaInk.BackgroundOpacity,
                MotifPart.Popup => TrashPolkaInk.PopupOpacity,
                _ when GetCell(element) => TrashPolkaInk.CellOpacity,
                _ when GetScreen(element) => TrashPolkaInk.ScreenOpacity,
                _ => TrashPolkaInk.PaneOpacity,
            };
            var size = InkSize(element);
            int seed = part switch { MotifPart.Window => TrashPolkaInk.WindowSeed, MotifPart.Split => TrashPolkaInk.SplitSeed, _ => TrashPolkaInk.BackgroundSeed };
            // The bitmap layers (TrashPolkaRaster) are built in the background at this monitor's scale; until they land the
            // element shows the vector piece, and Retune repaints every element once they do.
            double dpi = VisualTreeHelper.GetDpi(element).DpiScaleX;
            var layerKey = new TrashPolkaRaster.LayerKey(seed, size, TrashPolkaInk.Scale, dpi);
            if (element.GetValue(LayersKeyProperty) is not TrashPolkaRaster.LayerKey lastKey || lastKey != layerKey)
            {
                element.SetValue(LayersKeyProperty, layerKey);
                QueueSweep();
            }
            var layers = TrashPolkaRaster.Get(layerKey, Retune);

            // Every part carries a full ink piece (TrashPolkaInk): the document canvas and the split pane their own, the
            // window background and its chrome (title bar, toolbar, sidebar, frame) a third one, so the raised panes never
            // continue the background's image. Each is drawn ONCE at screen size and pinned to the window, so resizing
            // only shows more or less of the same image (Steve, 2026-11-16: "the images scrunch up and change").
            // An accent switch only recolors the shared accent brushes; the pieces are redrawn only for a new screen size.
            Drawing piece;
            if (layers is not null && !(RasterPieces.TryGetValue(layers, out var state) && state.Failed))
            {
                // A new accent's piece composes on a worker: keep what is painted until it lands (Land, then Retune).
                if (Tinted(layers, size, Color.FromRgb(accent.Color.R, accent.Color.G, accent.Color.B)) is not { } tinted) return;
                piece = tinted;
                RasterPaints++;
            }
            else if (layers is null && TrashPolkaRaster.LoadingFromDisk(layerKey))
            {
                // The cached picture is decoding; the element keeps its plain background until Retune repaints it.
                return;
            }
            else
            {
                TrashPolkaInk.SetAccent(accent.Color);
                if (!Pieces.TryGetValue(part, out var cached) || cached.Size != size)
                {
                    cached = (accent.Color, size, TrashPolkaInk.Draw(accent.Color, size, seed));
                    Pieces[part] = cached;
                }
                piece = cached.Piece;
                VectorPaints++;
            }
            // The element's own size is in the stamp too: under a new app size an element that fills its window keeps the
            // same place in root units but not in its own, and its old brush would stay at the old scale.
            var stamp = new PaintKey(part, accent.Color, baseBrush.Color, pane, element.RenderSize, opacity, piece);
            if (element.GetValue(PaintedProperty) is PaintKey painted && painted == stamp) return;
            // The brush is drawn in the element's own units, but its place in the window is in the root's. Under the app
            // size (ScaleHost's LayoutTransform) one element unit covers several root units, so the place and the piece's
            // scale are taken back into element units; read in root units, every element showed its own shifted stretch
            // of the image and an active tab broke from its page at the join.
            double units = element.ActualWidth > 0 ? pane.Width / element.ActualWidth : 1;
            var local = Math.Abs(units - 1) < 1e-9 ? pane
                : new Rect(pane.X / units, pane.Y / units, element.ActualWidth, element.ActualHeight);
            var brush = TrashPolkaMotif.Compose(baseBrush.Color, piece, local, window, scale: TrashPolkaInk.Scale / units, opacity: opacity);
            if (unflip is not null)
            {
                if (brush.IsFrozen) brush = brush.Clone();
                brush.Transform = unflip;
            }
            element.SetValue(PaintedProperty, stamp);
            // First paint since the last restore: remember whether the background we are about to replace was local.
            if (BackgroundProperty(element) is { } bg && element.ReadLocalValue(PaintedBrushProperty) == DependencyProperty.UnsetValue)
                element.SetValue(HadLocalBackgroundProperty, element.ReadLocalValue(bg) != DependencyProperty.UnsetValue);
            element.SetValue(PaintedBrushProperty, brush);
            SetBackground(element, brush);
        }

        private static void Restore(FrameworkElement element)
        {
            if (element.GetValue(PaintedProperty) is null) return;
            element.ClearValue(PaintedProperty);
            element.ClearValue(PaintedBrushProperty);
            if (!(bool)element.GetValue(HadLocalBackgroundProperty))
            {
                if (BackgroundProperty(element) is { } bg) element.ClearValue(bg);
                return;
            }
            string key = GetBaseBrush(element);
            switch (element)
            {
                case Border border: border.SetResourceReference(Border.BackgroundProperty, key); break;
                case Panel panel: panel.SetResourceReference(Panel.BackgroundProperty, key); break;
                case Control control: control.SetResourceReference(Control.BackgroundProperty, key); break;
            }
        }

        private static void SetBackground(FrameworkElement element, Brush brush)
        {
            switch (element)
            {
                case Border border: border.Background = brush; break;
                case Panel panel: panel.Background = brush; break;
                case Control control: control.Background = brush; break;
            }
        }
    }
}
