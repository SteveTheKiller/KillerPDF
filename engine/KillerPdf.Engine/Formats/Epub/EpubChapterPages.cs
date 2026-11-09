namespace KillerPdf.Engine.Formats.Epub;

/// <summary>One layout of part of a chapter: its pages by position (null before the first one asked for) and how many
/// pages the layout produced, including any it laid out only to reach the first one asked for.</summary>
internal sealed record EpubChapterRun(IReadOnlyList<EpubLayoutPage?> Pages, int LaidOut);

/// <summary>
/// Keeps the full layout of only the few most recently used chapters. A book is laid out once to find its page
/// breaks; each page then keeps its chapter, its position in the chapter and its text, and a chapter whose
/// placements, paints and clusters are needed again is laid out again from its source, as e-readers do.
/// </summary>
/// <param name="relayout">
/// Lays a chapter out again from the given page (resuming where the page index recorded that page's layout state), and
/// stops once the chapter has the given number of pages; the pages before the first one asked for are null.
/// </param>
/// <param name="readingOrder">Spine indices in reading order, for finding a chapter's neighbors.</param>
/// <param name="capacity">How many chapters stay laid out: the one being read and its two neighbors.</param>
internal sealed class EpubChapterPages(Func<int, int, int, CancellationToken, EpubChapterRun> relayout,
    IReadOnlyList<int> readingOrder, int capacity = 3)
{
    // Background layout a reader's request stops, so a page turn never waits behind work done ahead.
    private CancellationTokenSource? _aheadCancel;
    // The chapter the reader is in; arriving in another one lays out its neighbors ahead.
    private int _current = -1;
    private int _readersWaiting;
    private int _aheadChapter = -1;
    private readonly Lock _lock = new();
    // Layout shares the book's font, outline and image-box caches, so chapters are laid out one at a time.
    private readonly Lock _layoutLock = new();
    // Each cached chapter holds one slot per page; a jump fills only the page it needed.
    private readonly LinkedList<(int SpineIndex, EpubLayoutPage?[] Pages)> _order = new();
    private readonly Dictionary<int, int> _pageCounts = new();
    // The chapters to lay out ahead, nearest first; the latest page turn replaces the list.
    private readonly List<int> _ahead = new();
    private bool _aheadQueued;
    private int _inFlight;
    private bool _closed;
    // The chapter pagination is laying out; a reader who needs the layout lock stops it, and pagination starts that
    // chapter over once the reader is done. Closing the book stops pagination for good.
    private CancellationTokenSource? _paginationStep;
    private readonly CancellationTokenSource _closing = new();

    /// <summary>How many chapter layouts readers have asked for since the book opened.</summary>
    internal int Layouts { get; private set; }

    /// <summary>How many chapters were laid out ahead of the reader since the book opened.</summary>
    internal int AheadLayouts { get; private set; }

    /// <summary>How many pages readers' layouts produced since the book opened, including pages laid out only to reach one.</summary>
    internal int PagesLaidOut { get; private set; }

    /// <summary>Gets or sets whether a chapter laid out for a reader also lays out its neighbors in the background.</summary>
    internal bool PrefetchNeighbors { get; set; } = true;

    /// <summary>The number of the chapter's pages whose full layout is cached now.</summary>
    internal int ResidentPageCount(int spineIndex)
    {
        lock (_lock)
            foreach (var entry in _order)
                if (entry.SpineIndex == spineIndex) return entry.Pages.Count(page => page is not null);
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
        if (Cached(entry.SpineIndex, ordinal) is { } hit)
        {
            Arrive(entry.SpineIndex);
            return hit[ordinal]!;
        }
        lock (_lock)
        {
            ThrowIfClosed();
            _readersWaiting++;
            // Work ahead on the very chapter the reader needs is finished rather than started over.
            if (_aheadChapter != entry.SpineIndex) _aheadCancel?.Cancel();
            // A chapter still being paginated is never the one a reader needs, since its pages are not published yet.
            _paginationStep?.Cancel();
        }
        EpubLayoutPage page;
        lock (_layoutLock)
        {
            lock (_lock)
            {
                _readersWaiting--;
                ThrowIfClosed();
            }
            if (Cached(entry.SpineIndex, ordinal) is { } raced)
            {
                Arrive(entry.SpineIndex);
                return raced[ordinal]!;
            }
            // A jump needs only this page: layout resumes from the state the page index recorded for it and stops there.
            // Reading on past what a jump laid out lays out the whole chapter, so each page turn does not start it over.
            Layouts++;
            bool jump = Find(entry.SpineIndex) is null;
            var run = jump ? relayout(entry.SpineIndex, ordinal, ordinal + 1, CancellationToken.None)
                : relayout(entry.SpineIndex, 0, int.MaxValue, CancellationToken.None);
            PagesLaidOut += run.LaidOut;
            var pages = run.Pages;
            // Layout is deterministic; a chapter that lays out differently the second time is a reader defect.
            if (pages.Count <= ordinal || pages.Count > PageCount(entry.SpineIndex) || pages[ordinal] is not { } laidOut
                || laidOut.TextStart != entry.TextStart || laidOut.Text != entry.Text)
                throw new InvalidOperationException("A native EPUB chapter laid out differently the second time.");
            Store(entry.SpineIndex, pages, first: true);
            page = laidOut;
        }
        lock (_lock) _current = entry.SpineIndex;
        if (PrefetchNeighbors) Prefetch(entry.SpineIndex);
        return page;
    }

    internal bool IsClosed
    {
        get { lock (_lock) return _closed; }
    }

    // Called under _lock.
    private void ThrowIfClosed()
    {
        if (_closed) throw new ObjectDisposedException(nameof(EpubLayoutDocument), "The native EPUB book was closed.");
    }

    /// <summary>
    /// Ends the book's layout: queued layout ahead is dropped and work in progress ahead is cancelled, cached chapters are
    /// let go, and a page that would need laying out again is refused. A reader already laying out finishes. Idempotent.
    /// </summary>
    internal void Close()
    {
        CancellationTokenSource? running;
        lock (_lock)
        {
            if (_closed) return;
            _closed = true;
            _ahead.Clear();
            _order.Clear();
            running = _aheadCancel;
        }
        // The running layout ahead may dispose its token source as it finishes; then there is nothing left to cancel.
        try { running?.Cancel(); }
        catch (ObjectDisposedException) { }
        // Pagination stops at its next check and its thread ends, so it no longer holds the book either.
        _closing.Cancel();
        EpubLayoutAhead.Remove(this);
    }

    /// <summary>Cancelled when the book closes.</summary>
    internal CancellationToken Closing => _closing.Token;

    /// <summary>
    /// Runs one pagination step under the layout lock, after any reader already waiting for it. A reader who arrives
    /// while the step runs cancels it; the step must then leave the book as it found it, and it runs again once the
    /// reader is done. That second run is not cancelled, so a reader who keeps laying chapters out cannot hold
    /// pagination back for good: each chapter is started over at most once. Throws
    /// <see cref="OperationCanceledException"/> once the book is closed.
    /// </summary>
    internal T Paginate<T>(Func<CancellationToken, T> step)
    {
        bool stoppedOnce = false;
        while (true)
        {
            // Readers go first: one waiting for the lock is let in before pagination takes it again.
            SpinWait.SpinUntil(() => { lock (_lock) return _readersWaiting == 0 || _closed; });
            _closing.Token.ThrowIfCancellationRequested();
            using var preempted = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
            lock (_layoutLock)
            {
                lock (_lock)
                {
                    _paginationStep = stoppedOnce ? null : preempted;
                    if (_readersWaiting > 0 && !stoppedOnce) preempted.Cancel();
                }
                stoppedOnce = true;
                try { return step(preempted.Token); }
                catch (OperationCanceledException) when (!_closing.IsCancellationRequested && preempted.IsCancellationRequested) { }
                finally
                {
                    lock (_lock) _paginationStep = null;
                }
            }
        }
    }

    /// <summary>
    /// Offers the full layout of a chapter pagination just finished, before its pages become index entries. The chapter
    /// is kept when the reader is in it or about to turn into it, so it is not laid out a second time for them.
    /// </summary>
    internal void Paginated(int spineIndex, IReadOnlyList<EpubLayoutPage> pages)
    {
        bool keep, first;
        lock (_lock)
        {
            if (_closed) return;
            // Before anyone reads, the reader starts at the beginning of the reading order.
            int reading = _current >= 0 ? _current : readingOrder[0];
            int position = IndexOf(reading);
            first = spineIndex == reading && _current < 0;
            keep = spineIndex == reading || position >= 0 && position + 1 < readingOrder.Count
                && readingOrder[position + 1] == spineIndex;
            if (keep) _ahead.Remove(spineIndex);
        }
        if (keep) Store(spineIndex, pages, first);
    }

    private int IndexOf(int spineIndex)
    {
        for (int at = 0; at < readingOrder.Count; at++)
            if (readingOrder[at] == spineIndex) return at;
        return -1;
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

    // The cached pages when this page is laid out; a reader's use marks the chapter most recently used.
    private EpubLayoutPage?[]? Cached(int spineIndex, int ordinal, bool touch = true)
    {
        lock (_lock)
            for (var node = _order.First; node is not null; node = node.Next)
            {
                if (node.Value.SpineIndex != spineIndex) continue;
                if (node.Value.Pages[ordinal] is null) return null;
                if (touch && !ReferenceEquals(node, _order.First))
                {
                    _order.Remove(node);
                    _order.AddFirst(node);
                }
                return node.Value.Pages;
            }
        return null;
    }

    // The chapter's cached pages, complete or not, without marking it used.
    private EpubLayoutPage?[]? Find(int spineIndex)
    {
        lock (_lock)
            foreach (var entry in _order)
                if (entry.SpineIndex == spineIndex) return entry.Pages;
        return null;
    }

    // The chapter being read goes first; a chapter laid out ahead of the reader goes just behind it. Pages laid out now
    // join the ones already cached for the chapter.
    private void Store(int spineIndex, IReadOnlyList<EpubLayoutPage?> pages, bool first)
    {
        lock (_lock)
        {
            // A reader that finished after the book closed still gets its page; the chapter is not kept.
            if (_closed) return;
            EpubLayoutPage?[]? slots = null;
            for (var node = _order.First; node is not null; node = node.Next)
                if (node.Value.SpineIndex == spineIndex)
                {
                    first |= ReferenceEquals(node, _order.First);
                    slots = node.Value.Pages;
                    _order.Remove(node);
                    break;
                }
            slots ??= new EpubLayoutPage?[_pageCounts[spineIndex]];
            for (int ordinal = 0; ordinal < pages.Count; ordinal++)
                if (pages[ordinal] is { } page) slots[ordinal] = page;
            if (first || _order.First is null) _order.AddFirst((spineIndex, slots));
            else _order.AddAfter(_order.First, (spineIndex, slots));
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
            if (_closed) return;
            _ahead.Clear();
            _ahead.Add(spineIndex);
            if (position >= 0 && position + 1 < readingOrder.Count) _ahead.Add(readingOrder[position + 1]);
            if (position > 0) _ahead.Add(readingOrder[position - 1]);
            if (_aheadQueued) return;
            _aheadQueued = true;
        }
        EpubLayoutAhead.Enqueue(this);
    }

    /// <summary>Does this book's pending layout ahead on the calling thread, as the background thread would.</summary>
    internal void LayOutAheadNow()
    {
        while (LayOutAhead()) { }
        // A chapter the background thread took before this call finishes too.
        SpinWait.SpinUntil(() => { lock (_lock) return _inFlight == 0; });
    }

    // Lays out one wanted chapter, then yields the background thread to other open books until its next turn.
    internal bool LayOutAhead()
    {
        int chapter;
        lock (_lock)
        {
            if (_closed || _ahead.Count == 0)
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
            bool registered;
            lock (_lock) registered = _pageCounts.ContainsKey(chapter);
            if (registered)
                lock (_layoutLock)
                {
                    lock (_lock)
                    {
                        _aheadCancel = cancel;
                        _aheadChapter = chapter;
                        // A reader already waiting for this lock goes first, and a closed book lays out nothing more.
                        if (_readersWaiting > 0 || _closed) cancel.Cancel();
                    }
                    if (Find(chapter) is not { } cached || Array.IndexOf(cached, null) >= 0)
                    {
                        AheadLayouts++;
                        var pages = relayout(chapter, 0, int.MaxValue, cancel.Token).Pages;
                        if (pages.Count == PageCount(chapter)) Store(chapter, pages, first: false);
                    }
                }
        }
        // A reader's request stopped it; the chapter waits for the next turn unless a newer list replaced it.
        catch (OperationCanceledException)
        {
            lock (_lock) if (!_closed && !_ahead.Contains(chapter)) _ahead.Add(chapter);
        }
        // Laying out ahead is only an optimization: a failure here surfaces when the page is really needed.
        catch (Exception) { }
        finally
        {
            lock (_lock)
            {
                if (ReferenceEquals(_aheadCancel, cancel))
                {
                    _aheadCancel = null;
                    _aheadChapter = -1;
                }
                _inFlight--;
            }
            cancel.Dispose();
        }
        return true;
    }
}

/// <summary>
/// One background thread lays chapters out ahead of the reader for every open book, so this work never
/// crowds the thread pool that drawing uses. Each book waits in line once and takes one chapter per turn.
/// </summary>
internal static class EpubLayoutAhead
{
    // A plain monitor object, since the worker waits on it.
    private static readonly object Gate = new();
    private static readonly Queue<EpubChapterPages> Books = new();
    private static Thread? _worker;
    private static int _held;

    /// <summary>Keeps the thread from starting another turn until the result is disposed (tests only).</summary>
    internal static IDisposable Hold()
    {
        lock (Gate) _held++;
        return new Release();
    }

    private sealed class Release : IDisposable
    {
        private int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) != 0) return;
            lock (Gate)
            {
                _held--;
                Monitor.PulseAll(Gate);
            }
        }
    }

    /// <summary>Queues a book for its next chapter; its turn returns whether it wants another.</summary>
    internal static void Enqueue(EpubChapterPages book)
    {
        lock (Gate)
        {
            Books.Enqueue(book);
            if (_worker is null)
            {
                // Normal priority: a reader waits for this thread to notice a cancel, which a starved thread would delay.
                _worker = new Thread(Loop) { IsBackground = true, Name = "EPUB layout ahead" };
                _worker.Start();
            }
            Monitor.Pulse(Gate);
        }
    }

    /// <summary>Takes a closed book out of the line, so the queue no longer keeps it in memory.</summary>
    internal static void Remove(EpubChapterPages book)
    {
        lock (Gate)
        {
            if (!Books.Contains(book)) return;
            var kept = Books.Where(queued => !ReferenceEquals(queued, book)).ToArray();
            Books.Clear();
            foreach (var queued in kept) Books.Enqueue(queued);
        }
    }

    private static void Loop()
    {
        while (true)
        {
            EpubChapterPages? next;
            lock (Gate)
            {
                while (Books.Count == 0 || _held > 0) Monitor.Wait(Gate);
                next = Books.Dequeue();
            }
            bool again;
            try { again = next.LayOutAhead(); }
            catch (Exception) { again = false; }
            if (again)
                lock (Gate)
                    if (!next.IsClosed) Books.Enqueue(next);
            // The waiting thread holds no book, so a closed one can be collected.
            next = null;
        }
    }
}

