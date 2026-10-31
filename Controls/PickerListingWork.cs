using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace KillerPDF.Controls
{
    public sealed class PickerListingWork : IDisposable
    {
        private readonly SemaphoreSlim _workers = new SemaphoreSlim(2);
        private CancellationTokenSource? _pending;
        private bool _closed;

        public void Cancel() { _pending?.Cancel(); _pending = null; }
        public void Dispose() { _closed = true; Cancel(); }

        public Task RunAsync<T>(Func<CancellationToken, T> read, Func<T, CancellationToken, Task> publish, Action<Exception> failed)
        {
            if (_closed) return Task.CompletedTask;
            Cancel();
            var request = _pending = new CancellationTokenSource();
            return CompleteAsync(request, read, publish, failed);
        }

        private async Task CompleteAsync<T>(CancellationTokenSource request, Func<CancellationToken, T> read,
            Func<T, CancellationToken, Task> publish, Action<Exception> failed)
        {
            var token = request.Token;
            request.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                await _workers.WaitAsync(token);
                // A read that hangs on a dead network path cannot be interrupted, so the slot goes
                // back when the request is abandoned, not only when the read returns.
                int released = 0;
                void ReleaseSlot() { if (Interlocked.Exchange(ref released, 1) == 0) _workers.Release(); }
                Task<T> reading;
                try
                {
                    reading = Task.Run(() =>
                    {
                        try { token.ThrowIfCancellationRequested(); return read(token); }
                        finally { ReleaseSlot(); }
                    });
                }
                catch { ReleaseSlot(); throw; }
                var finished = await Task.WhenAny(reading, Task.Delay(Timeout.Infinite, token));
                if (!ReferenceEquals(finished, reading)) ReleaseSlot();
                token.ThrowIfCancellationRequested();
                T result = await reading;
                if (!_closed && ReferenceEquals(_pending, request)) await publish(result, token);
            }
            catch (OperationCanceledException)
            {
                if (!_closed && ReferenceEquals(_pending, request)) failed(new TimeoutException(new Win32Exception(1460).Message));
            }
            catch (Exception error)
            {
                if (!_closed && !token.IsCancellationRequested && ReferenceEquals(_pending, request)) failed(error);
            }
            finally
            {
                if (ReferenceEquals(_pending, request)) _pending = null;
                request.Dispose();
            }
        }

        public static async Task PublishAsync<T>(IReadOnlyList<T> rows, Action clear, Action<T> add,
            Func<Task> yield, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            clear();
            for (int offset = 0; offset < rows.Count; offset += 128)
            {
                token.ThrowIfCancellationRequested();
                int end = Math.Min(offset + 128, rows.Count);
                for (int index = offset; index < end; index++) { token.ThrowIfCancellationRequested(); add(rows[index]); }
                if (end < rows.Count) await yield();
            }
        }
    }

    public sealed class PickerListingResult
    {
        public PickerListingResult(IReadOnlyList<PickerEntry> entries, bool exists, bool complete, string? error = null)
        { Entries = entries; Exists = exists; Complete = complete; Error = error; }
        public IReadOnlyList<PickerEntry> Entries { get; }
        public bool Exists { get; }
        public bool Complete { get; }
        public string? Error { get; }
    }

    public static class PickerDirectoryListing
    {
        public const int MaximumEntries = 10000;

        public static PickerListingResult Read(string directory, bool showHidden, CancellationToken token)
        {
            var entries = new List<PickerEntry>();
            bool complete = true;
            string? failure = null;
            try
            {
                token.ThrowIfCancellationRequested();
                var source = new DirectoryInfo(directory);
                if (!source.Exists) return new PickerListingResult(entries, false, false);
                int visited = 0;
                var clock = Stopwatch.StartNew();
                foreach (var info in source.EnumerateFileSystemInfos())
                {
                    token.ThrowIfCancellationRequested();
                    if (++visited > MaximumEntries || clock.Elapsed > TimeSpan.FromSeconds(10))
                        return new PickerListingResult(entries, true, false, new Win32Exception(234).Message);
                    try
                    {
                        if (!showHidden && ((info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0 || info.Name.StartsWith(".", StringComparison.Ordinal))) continue;
                        bool folder = (info.Attributes & FileAttributes.Directory) != 0;
                        entries.Add(new PickerEntry(info.Name, info.FullName, folder, folder ? 0 : ((FileInfo)info).Length, info.LastWriteTime));
                    }
                    catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                    { complete = false; failure = error.Message; }
                }
                return new PickerListingResult(entries, true, complete, failure);
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is System.Security.SecurityException)
            { return new PickerListingResult(entries, true, false, error.Message); }
        }
    }
}
