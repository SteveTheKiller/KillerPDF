namespace KillerPDF.Services;

/// <summary>Draws page thumbnails for the rows a page list asks for, one at a time on a background thread, newest
/// request first so the rows on screen fill before rows scrolled past. Only the most recently drawn
/// <c>retain</c> thumbnails are held; older ones are released and drawn again if their row is asked for again.
/// A long document no longer draws and holds a thumbnail for every page as it opens. <c>drained</c> runs each time no
/// row is waiting, so the caller can let go of its renderer between passes.</summary>
internal sealed class ThumbnailWindow<TImage> : IDisposable where TImage : class
{
    internal const int DefaultRetain = 256;

    private readonly int _pageCount;
    private readonly Func<int, CancellationToken, TImage?> _render;
    private readonly Action<int, TImage?> _publish;
    private readonly int _retain;
    private readonly Action? _drained;
    private readonly CancellationTokenSource _cancel = new();
    private readonly Lock _sync = new();
    private readonly List<int> _requests = [];           // newest last
    private readonly HashSet<int> _pending = [];
    // Pages whose draw failed in this window. A row clears its picture when its draw fails and then asks for it again;
    // without this the page would be drawn a second time. A new list makes a new window, which tries every page again.
    private readonly HashSet<int> _failed = [];
    // Pages whose draw threw. That can be a passing problem, such as a briefly locked file, so unlike a page the
    // renderer refused by returning nothing, these are tried again once the pass has drained.
    private readonly HashSet<int> _threw = [];
    // How many times each page's draw has thrown in this window. A second throw settles the page as failed, so a
    // page the engine can never draw does not reopen and parse the file after every pass.
    private readonly Dictionary<int, int> _throwCounts = [];
    private readonly LinkedList<int> _held = [];          // oldest first
    private readonly Dictionary<int, LinkedListNode<int>> _heldNodes = [];
    private bool _running;

    internal ThumbnailWindow(int pageCount, Func<int, CancellationToken, TImage?> render, Action<int, TImage?> publish,
        int retain = DefaultRetain, Action? drained = null)
    {
        ArgumentNullException.ThrowIfNull(render);
        ArgumentNullException.ThrowIfNull(publish);
        ArgumentOutOfRangeException.ThrowIfLessThan(retain, 1);
        _pageCount = pageCount;
        _render = render;
        _publish = publish;
        _retain = retain;
        _drained = drained;
    }

    /// <summary>Asks for a row's thumbnail. A row already drawn or waiting is left as it is.</summary>
    internal void Request(int page)
    {
        if ((uint)page >= (uint)_pageCount || _cancel.IsCancellationRequested) return;
        lock (_sync)
        {
            if (_heldNodes.ContainsKey(page) || _failed.Contains(page) || _threw.Contains(page)) return;
            if (!_pending.Add(page)) _requests.Remove(page);   // asked again: move it to the front of the queue
            _requests.Add(page);
            // A fast scroll can realize far more rows than are retained, and drawing the oldest of them would only
            // release them again. The oldest waiting requests are dropped, and their rows ask again when read.
            while (_requests.Count > _retain)
            {
                _pending.Remove(_requests[0]);
                _requests.RemoveAt(0);
            }
            if (_running) return;
            _running = true;
        }
        _ = Task.Run(Drain);
    }

    private void Drain()
    {
        CancellationToken token = _cancel.Token;
        while (true)
        {
            int page;
            lock (_sync)
            {
                if (_requests.Count == 0 || token.IsCancellationRequested)
                {
                    _running = false;
                    _threw.Clear();
                    // Called under the lock, so a request arriving now starts a new pass only after this returns.
                    if (!token.IsCancellationRequested) _drained?.Invoke();
                    return;
                }
                page = _requests[^1];
                _requests.RemoveAt(_requests.Count - 1);
            }
            TImage? image = null;
            bool outOfMemory = false, threw = false;
            try { image = _render(page, token); }
            // An out-of-memory error counts as a failed draw like any other: the pass goes on, and the row is no
            // longer marked as waiting, so asking for it again draws it.
            catch (OutOfMemoryException) { outOfMemory = true; }
            catch (Exception) { threw = true; /* the row has no picture of its own */ }
            var released = new List<int>();
            lock (_sync)
            {
                _pending.Remove(page);
                if (image is null && !outOfMemory)
                {
                    if (threw && _throwCounts.GetValueOrDefault(page) < 1)
                    {
                        _throwCounts[page] = 1;
                        _threw.Add(page);
                    }
                    else _failed.Add(page);
                }
                if (token.IsCancellationRequested) { _running = false; return; }
                // A failed draw holds nothing, but it is still published (as null) so a row showing a carried-over
                // picture from the list it replaced drops it.
                if (image is not null)
                {
                    _heldNodes[page] = _held.AddLast(page);
                    // A page that has drawn can be drawn, so an earlier throw no longer counts against it.
                    _throwCounts.Remove(page);
                }
                while (_held.Count > _retain && _held.First is { } oldest)
                {
                    _held.RemoveFirst();
                    _heldNodes.Remove(oldest.Value);
                    released.Add(oldest.Value);
                }
            }
            _publish(page, image);
            foreach (int old in released) _publish(old, null);
        }
    }

    public void Dispose() => _cancel.Cancel();
}
