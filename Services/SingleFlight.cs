namespace KillerPDF.Services;

/// <summary>
/// Lets one asynchronous UI action run at a time: a call that arrives while another is still out is skipped, the way
/// a second Enter during a slow folder probe should do nothing. Used from the UI thread only.
/// </summary>
internal sealed class SingleFlight
{
    private bool _busy;

    /// <summary>Runs <paramref name="action"/> and returns true, or returns false at once when a run is still out.</summary>
    public async Task<bool> RunAsync(Func<Task> action)
    {
        if (_busy) return false;
        _busy = true;
        try { await action(); return true; }
        finally { _busy = false; }
    }
}
