namespace KillerPDF.Services;

/// <summary>Holds the one renderer a page list's thumbnail window draws with. Drawing opens it when needed;
/// <see cref="Release"/> lets go of it between passes and when the list is replaced.</summary>
/// <remarks>Release runs from a cancellation callback on the UI thread, so it never waits for a draw: a renderer
/// that is drawing is closed by that draw as it finishes.</remarks>
internal sealed class ThumbnailSessionOwner<TSession> where TSession : class, IDisposable
{
    private readonly object _gate = new();
    private TSession? _session;
    private volatile bool _releaseRequested;

    /// <summary>Draws with the renderer, opening it first when none is held. Returns null once
    /// <paramref name="cancellationToken"/> is cancelled.</summary>
    internal TResult? Use<TResult>(Func<TSession> open, Func<TSession, TResult?> draw, CancellationToken cancellationToken)
        where TResult : class
    {
        try
        {
            lock (_gate)
            {
                if (cancellationToken.IsCancellationRequested) return null;
                _session ??= open();
                return draw(_session);
            }
        }
        finally
        {
            CloseIfReleased();
        }
    }

    /// <summary>Lets go of the renderer now, or as the draw using it finishes.</summary>
    internal void Release()
    {
        _releaseRequested = true;
        CloseIfReleased();
    }

    // Whoever holds the gate when a release is pending closes the renderer. A release that finds the gate taken
    // leaves it to the draw holding it, which checks again once it has let go.
    private void CloseIfReleased()
    {
        while (_releaseRequested && Monitor.TryEnter(_gate))
        {
            try
            {
                if (!_releaseRequested) continue;
                _releaseRequested = false;
                _session?.Dispose();
                _session = null;
            }
            finally
            {
                Monitor.Exit(_gate);
            }
        }
    }
}
