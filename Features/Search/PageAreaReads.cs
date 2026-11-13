namespace KillerPDF.Features
{
    /// <summary>
    /// The native pages whose highlight areas are being read for the search results on show, and the token those
    /// readings run under. A page is read once at a time; a reading that lands for an older set of results is dropped.
    /// </summary>
    internal sealed class PageAreaReads
    {
        private readonly HashSet<int> _reading = [];
        private CancellationTokenSource? _cancellation;

        /// <summary>Which set of results the readings belong to. A reading started under an older one is dropped.</summary>
        internal int Generation { get; private set; }

        /// <summary>The token the readings running now use, or none when no reading has started.</summary>
        internal CancellationToken Token => _cancellation?.Token ?? default;

        /// <summary>Marks the page as being read. False when a reading for it is already running.</summary>
        internal bool TryStart(int page, out int generation, out CancellationToken token)
        {
            generation = Generation;
            token = default;
            if (!_reading.Add(page)) return false;
            _cancellation ??= new CancellationTokenSource();
            token = _cancellation.Token;
            return true;
        }

        /// <summary>A reading landed or stopped. False when it belongs to an older set of results.</summary>
        internal bool Finish(int page, int generation)
        {
            if (generation != Generation) return false;
            _reading.Remove(page);
            return true;
        }

        /// <summary>Stops every reading running now and drops what any of them would deliver. Their pages are no longer
        /// marked as being read, so a page on screen can be read again before a stopped reading lands.</summary>
        internal void Cancel()
        {
            _cancellation?.Cancel();
            _cancellation?.Dispose();
            _cancellation = null;
            _reading.Clear();
            Generation++;
        }
    }
}
