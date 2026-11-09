namespace KillerPdf.Engine.Formats.Epub;

/// <summary>
/// Keeps the full layout of only the few most recently used chapters. A book is laid out once to find its page
/// breaks; each page then keeps its chapter, its position in the chapter and its text, and a chapter whose
/// placements, paints and clusters are needed again is laid out again from its source, as e-readers do.
/// </summary>
/// <param name="relayout">Lays a chapter out again from its start, stopping once it has the given number of pages.</param>
/// <param name="readingOrder">Spine indices in reading order, for finding a chapter's neighbors.</param>
/// <param name="capacity">How many chapters stay laid out: the one being read and its two neighbors.</param>
internal sealed class EpubChapterPages(Func<int, int, CancellationToken, IReadOnlyList<EpubLayoutPage>> relayout,
    IReadOnlyList<int> readingOrder, int capacity = 3)
{
    // Background layout a reader's request stops, so a page turn never waits behind work done ahead.
    private CancellationTokenSource? _aheadCancel;
    // The chapter the reader is in; arriving in another one lays out its neighbors ahead.
    private int _current = -1;
    private int _readersWaiting;
    private readonly Lock _lock = new();
    // Layout shares the book's font, outline and image-box caches, so chapters are laid out one at a time.
    private readonly Lock _layoutLock = new();
    private readonly LinkedList<(int SpineIndex, IReadOnlyList<EpubLayoutPage> Pages)> _order = new();
    private readonly Dictionary<int, int> _pageCounts = new();
    // The chapters to lay out ahead, nearest first; the latest page turn replaces the list.
    private readonly List<int> _ahead = new();
    private bool _aheadQueued;
    private int _inFlight;

    /// <summary>How many chapter layouts readers have asked for since the book opened.</summary>
    internal int Layouts { get; private set; }

    /// <summary>Gets or sets whether a chapter laid out for a reader also lays out its neighbors in the background.</summary>
    internal bool PrefetchNeighbors { get; set; } = true;

    /// <summary>The number of the chapter's pages whose full layout is cached now.</summary>
    internal int ResidentPageCount(int spineIndex)
    {
        lock (_lock)
            foreach (var entry in _order)
                if (entry.SpineIndex == spineIndex) return entry.Pages.Count;
        return 0;
    }

    /// <summary>Records how many pages the chapter's first layout produced.</summary>
    internal void Register(int spineIndex, int pageCount)
    {
        lock (_lock) _pageCounts[spineIndex] = pageCount;
    }

    /// <summary>Returns the laid-out page for this index entry, laying its chapter out again when it is not cached.</summary>
    internal EpubLayoutPage Resident(EpubLayoutPage entry, int ordinal)
    {
        if (Cached(entry.SpineIndex, ordinal + 1) is { } hit)
        {
            Arrive(entry.SpineIndex);
            return hit[ordinal];
        }
        lock (_lock)
        {
            _readersWaiting++;
            _aheadCancel?.Cancel();
        }
        EpubLayoutPage page;
        lock (_layoutLock)
        {
            lock (_lock) _readersWaiting--;
            if (Cached(entry.SpineIndex, ordinal + 1) is { } raced)
            {
                Arrive(entry.SpineIndex);
                return raced[ordinal];
            }
            // A jump needs only this page, and later pages cannot change earlier ones, so layout stops there; reading on
            // past what a jump laid out lays out the whole chapter, so each page turn does not start it over.
            Layouts++;
            var pages = relayout(entry.SpineIndex, Cached(entry.SpineIndex, 1, touch: false) is null ? ordinal + 1 : int.MaxValue,
                CancellationToken.None);
            // Layout is deterministic; a chapter that lays out differently the second time is a reader defect.
            if (pages.Count <= ordinal || pages.Count > PageCount(entry.SpineIndex)
                || pages[ordinal].TextStart != entry.TextStart || pages[ordinal].Text != entry.Text)
                throw new InvalidOperationException("A native EPUB chapter laid out differently the second time.");
            Store(entry.SpineIndex, pages, first: true);
            page = pages[ordinal];
        }
        lock (_lock) _current = entry.SpineIndex;
        if (PrefetchNeighbors) Prefetch(entry.SpineIndex);
        return page;
    }

    // A page from a chapter laid out ahead: the reader has moved on, so the next neighbors are laid out ahead in turn.
    private void Arrive(int spineIndex)
    {
        lock (_lock)
        {
            if (_current == spineIndex) return;
            _current = spineIndex;
        }
        if (PrefetchNeighbors) Prefetch(spineIndex);
    }

    private int PageCount(int spineIndex)
    {
        lock (_lock) return _pageCounts[spineIndex];
    }

    // The cached pages when at least this many are laid out; a reader's use marks the chapter most recently used.
    private IReadOnlyList<EpubLayoutPage>? Cached(int spineIndex, int needed, bool touch = true)
    {
        lock (_lock)
            for (var node = _order.First; node is not null; node = node.Next)
            {
                if (node.Value.SpineIndex != spineIndex) continue;
                if (node.Value.Pages.Count < needed) return null;
                if (touch && !ReferenceEquals(node, _order.First))
                {
                    _order.Remove(node);
                    _order.AddFirst(node);
                }
                return node.Value.Pages;
            }
        return null;
    }

    // The chapter being read goes first; a chapter laid out ahead of the reader goes just behind it.
    private void Store(int spineIndex, IReadOnlyList<EpubLayoutPage> pages, bool first)
    {
        lock (_lock)
        {
            for (var node = _order.First; node is not null; node = node.Next)
                if (node.Value.SpineIndex == spineIndex)
                {
                    first |= ReferenceEquals(node, _order.First);
                    _order.Remove(node);
                    break;
                }
            if (first || _order.First is null) _order.AddFirst((spineIndex, pages));
            else _order.AddAfter(_order.First, (spineIndex, pages));
            while (_order.Count > capacity) _order.RemoveLast();
        }
    }

    // Reading on lays out the rest of this chapter, and turning past either end finds the neighbor ready.
    private void Prefetch(int spineIndex)
    {
        int position = -1;
        for (int at = 0; at < readingOrder.Count; at++)
            if (readingOrder[at] == spineIndex) { position = at; break; }
        lock (_lock)
        {
            _ahead.Clear();
            _ahead.Add(spineIndex);
            if (position >= 0 && position + 1 < readingOrder.Count) _ahead.Add(readingOrder[position + 1]);
            if (position > 0) _ahead.Add(readingOrder[position - 1]);
            if (_aheadQueued) return;
            _aheadQueued = true;
        }
        EpubLayoutAhead.Enqueue(LayOutAhead);
    }

    /// <summary>Does this book's pending layout ahead on the calling thread, as the background thread would.</summary>
    internal void LayOutAheadNow()
    {
        while (LayOutAhead()) { }
        // A chapter the background thread took before this call finishes too.
        SpinWait.SpinUntil(() => { lock (_lock) return _inFlight == 0; });
    }

    // Lays out one wanted chapter, then yields the background thread to other open books until its next turn.
    private bool LayOutAhead()
    {
        int chapter;
        lock (_lock)
        {
            if (_ahead.Count == 0)
            {
                _aheadQueued = false;
                return false;
            }
            chapter = _ahead[0];
            _ahead.RemoveAt(0);
            _inFlight++;
        }
        var cancel = new CancellationTokenSource();
        try
        {
            if (_pageCounts.ContainsKey(chapter))
                lock (_layoutLock)
                {
                    lock (_lock)
                    {
                        _aheadCancel = cancel;
                        // A reader already waiting for this lock goes first.
                        if (_readersWaiting > 0) cancel.Cancel();
                    }
                    if (Cached(chapter, PageCount(chapter), touch: false) is null)
                    {
                        var pages = relayout(chapter, int.MaxValue, cancel.Token);
                        if (pages.Count == PageCount(chapter)) Store(chapter, pages, first: false);
                    }
                }
        }
        // A reader's request stopped it; the chapter waits for the next turn unless a newer list replaced it.
        catch (OperationCanceledException)
        {
            lock (_lock) if (!_ahead.Contains(chapter)) _ahead.Add(chapter);
        }
        // Laying out ahead is only an optimization: a failure here surfaces when the page is really needed.
        catch (Exception) { }
        finally
        {
            lock (_lock)
            {
                if (ReferenceEquals(_aheadCancel, cancel)) _aheadCancel = null;
                _inFlight--;
            }
            cancel.Dispose();
        }
        return true;
    }
}

/// <summary>
/// One low-priority background thread lays chapters out ahead of the reader for every open book, so this work never
/// crowds the thread pool that drawing uses. Each book waits in line once and takes one chapter per turn.
/// </summary>
internal static class EpubLayoutAhead
{
    // A plain monitor object, since the worker waits on it.
    private static readonly object Gate = new();
    private static readonly Queue<Func<bool>> Books = new();
    private static Thread? _worker;

    /// <summary>Queues a book's next chapter; the function returns whether the book wants another turn.</summary>
    internal static void Enqueue(Func<bool> next)
    {
        lock (Gate)
        {
            Books.Enqueue(next);
            if (_worker is null)
            {
                _worker = new Thread(Loop) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "EPUB layout ahead" };
                _worker.Start();
            }
            Monitor.Pulse(Gate);
        }
    }

    private static void Loop()
    {
        while (true)
        {
            Func<bool> next;
            lock (Gate)
            {
                while (Books.Count == 0) Monitor.Wait(Gate);
                next = Books.Dequeue();
            }
            bool again;
            try { again = next(); }
            catch (Exception) { again = false; }
            if (again)
                lock (Gate) Books.Enqueue(next);
        }
    }
}

