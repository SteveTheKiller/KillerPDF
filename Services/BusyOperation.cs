using System.Threading;
using System.Threading.Tasks;

namespace KillerPDF.Services;

/// <summary>Holds a page job still while the app asks whether to cancel it, so nothing more is
/// written behind the question.</summary>
internal sealed class OperationHold : IDisposable
{
    private readonly ManualResetEventSlim _running = new(true);

    internal void Hold() => _running.Reset();

    internal void Release() => _running.Set();

    /// <summary>Returns once the job may go on, or throws when it was canceled meanwhile.</summary>
    internal void WaitIfHeld(CancellationToken cancellation) => _running.Wait(cancellation);

    public void Dispose() => _running.Dispose();
}

/// <summary>Shows that work has started before work that blocks the window begins.</summary>
internal static class BusyCue
{
    /// <summary>Runs work once the cue has reached the screen.</summary>
    internal static async Task<T> ThenRun<T>(Action showCue, Func<Task> reachScreen, Func<T> work)
    {
        ArgumentNullException.ThrowIfNull(showCue);
        ArgumentNullException.ThrowIfNull(reachScreen);
        ArgumentNullException.ThrowIfNull(work);
        showCue();
        await reachScreen();
        return work();
    }
}
