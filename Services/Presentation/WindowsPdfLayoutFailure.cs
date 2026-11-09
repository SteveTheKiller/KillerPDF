namespace KillerPDF.Presentation;

/// <summary>Notes a native layout step that failed and will be retried, once per kind of failure.</summary>
internal static class WindowsPdfLayoutFailure
{
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> Recorded = new();

    internal static void Record(string kind, Exception error)
    {
        if (Recorded.TryAdd(kind + ": " + error.GetType().Name, true))
            System.Diagnostics.Debug.WriteLine($"Native {kind} failed and will retry: {error.Message}");
    }
}
