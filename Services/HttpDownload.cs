using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace KillerPDF.Services;

/// <summary>Streams an HTTP response with a size limit and a whole-transfer deadline.</summary>
public static class HttpDownload
{
    public static async Task CopyToAsync(HttpClient http, Uri target, Stream output,
        TimeSpan deadline, long maximumBytes = long.MaxValue, CancellationToken cancellationToken = default)
    {
        if (maximumBytes < 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        if (deadline <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(deadline));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(deadline);
        var token = timeout.Token;
        using var response = await http.GetAsync(target, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long length && length > maximumBytes)
            throw new HttpDownloadSizeException();
        // The array overloads exist on every runtime the family builds for. The .NET Framework body stream
        // ignores the token on a read, so each read also races the token and a stalled body ends at the deadline.
#if NETFRAMEWORK
        using var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
#else
        using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
#endif
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await ReadAsync(input, buffer, token).ConfigureAwait(false)) > 0)
        {
            if (read > maximumBytes - total) throw new HttpDownloadSizeException();
            total += read;
            await output.WriteAsync(buffer, 0, read, token).ConfigureAwait(false);
        }
    }

    private static async Task<int> ReadAsync(Stream input, byte[] buffer, CancellationToken token)
    {
        var read = input.ReadAsync(buffer, 0, buffer.Length, token);
        if (read.IsCompleted) return await read.ConfigureAwait(false);
        var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (token.Register(() => cancelled.TrySetResult(true)))
        {
            if (await Task.WhenAny(read, cancelled.Task).ConfigureAwait(false) == read)
                return await read.ConfigureAwait(false);
        }
        // The abandoned read ends when the stream is disposed; its outcome is not observed.
        var ignored = read.ContinueWith(t => t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        throw new OperationCanceledException(token);
    }
}

/// <summary>The response exceeds the caller's permitted download size.</summary>
public sealed class HttpDownloadSizeException : IOException
{
    public HttpDownloadSizeException() : base("The download exceeds the permitted size.") { }
}
