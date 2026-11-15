using System.Diagnostics;
using System.IO;
using System.Text;

namespace KillerPDF.Services;

/// <summary>
/// Gathers the images of separate Save as KillerPDF launches into one batch (#479). Explorer starts one
/// process per selected file for a command-line verb, so the first process to take the channel's mutex
/// collects: it listens on the channel's pipe until no launch has arrived for the quiet period (or the
/// limit passes), then converts everything. Every later process sends its paths and exits.
/// The channel is the verb's own, never the open window's, so a running KillerPDF is not involved.
/// </summary>
internal sealed class ImageLaunchCollector(SingleInstanceChannel channel, TimeSpan quiet, TimeSpan limit,
    Func<System.IO.Pipes.NamedPipeServerStream>? createServer = null)
{
    private readonly Func<System.IO.Pipes.NamedPipeServerStream> _createServer = createServer ?? channel.CreateServer;

    // '|' cannot appear in a Windows path, so one message carries every path of a launch.
    private const char Separator = '|';
    private static readonly TimeSpan JoinGrace = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Returns every gathered path, starting with <paramref name="own"/>, when this launch is the collector,
    /// or null when another launch took the paths. <paramref name="collecting"/> runs once this launch
    /// owns the batch, before it starts listening.
    /// </summary>
    internal IReadOnlyList<string>? Collect(IReadOnlyList<string> own, Action? collecting = null)
    {
        using var mutex = new Mutex(false, channel.MutexName);
        if (!TryTake(mutex, TimeSpan.Zero))
        {
            string message = string.Join(Separator, own);
            if (Encoding.UTF8.GetByteCount(message) >= SingleInstanceChannel.MaximumMessageBytes)
            {
                if (!TryTake(mutex, limit + JoinGrace)) return [.. own];
            }
            else
            {
                // The collector may be between listeners as this launch arrives, so keep trying to hand
                // the paths over for as long as it can still be collecting. This launch becomes the
                // collector only once the previous one lets go; if that never happens, it converts
                // its own files on their own.
                var waited = Stopwatch.StartNew();
                while (true)
                {
                    if (channel.TrySend(message, 500)) return null;
                    if (TryTake(mutex, TimeSpan.FromMilliseconds(100))) break;
                    if (waited.Elapsed > limit + JoinGrace) return [.. own];
                }
            }
        }
        try
        {
            collecting?.Invoke();
            var files = new List<string>(own);
            var elapsed = Stopwatch.StartNew();
            while (elapsed.Elapsed < limit)
            {
                try
                {
                    using var server = _createServer();
                    using (var wait = new CancellationTokenSource(quiet))
                    {
                        try { server.WaitForConnectionAsync(wait.Token).GetAwaiter().GetResult(); }
                        catch (OperationCanceledException) { break; }
                    }
                    string? received = SingleInstanceChannel.ReadMessage(server,
                        SingleInstanceChannel.MaximumMessageBytes, SingleInstanceChannel.MessageDeadline);
                    if (received is not null)
                        files.AddRange(received.Split(Separator, StringSplitOptions.RemoveEmptyEntries));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    break;   // the pipe failed: stop listening, but keep every path already handed over
                }
            }
            return files;
        }
        finally { mutex.ReleaseMutex(); }
    }

    private static bool TryTake(Mutex mutex, TimeSpan timeout)
    {
        try { return mutex.WaitOne(timeout); }
        catch (AbandonedMutexException) { return true; }   // a collector that crashed: this launch takes over
    }
}
