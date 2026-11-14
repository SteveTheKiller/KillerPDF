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
internal sealed class ImageLaunchCollector(SingleInstanceChannel channel, TimeSpan quiet, TimeSpan limit)
{
    // '|' cannot appear in a Windows path, so one message carries every path of a launch.
    private const char Separator = '|';

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
            if (Encoding.UTF8.GetByteCount(message) < SingleInstanceChannel.MaximumMessageBytes
                && channel.TrySend(message, 2000))
                return null;
            // The collector stopped listening as this launch arrived. Wait for it, then gather anew;
            // if it never lets go, convert this launch's own files on their own.
            if (!TryTake(mutex, limit + TimeSpan.FromSeconds(10))) return [.. own];
        }
        try
        {
            collecting?.Invoke();
            var files = new List<string>(own);
            var elapsed = Stopwatch.StartNew();
            while (elapsed.Elapsed < limit)
            {
                using var server = channel.CreateServer();
                using (var wait = new CancellationTokenSource(quiet))
                {
                    try { server.WaitForConnectionAsync(wait.Token).GetAwaiter().GetResult(); }
                    catch (OperationCanceledException) { break; }
                    catch (IOException) { continue; }
                }
                string? received = SingleInstanceChannel.ReadMessage(server,
                    SingleInstanceChannel.MaximumMessageBytes, SingleInstanceChannel.MessageDeadline);
                if (received is not null)
                    files.AddRange(received.Split(Separator, StringSplitOptions.RemoveEmptyEntries));
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
