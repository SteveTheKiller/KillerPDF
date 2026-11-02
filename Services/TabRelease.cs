namespace KillerPDF.Services;

/// <summary>What a tab gives up when it closes, by any route: Close, Close All Tabs, or its window
/// closing.</summary>
internal static class TabRelease
{
    /// <summary>Closes the tab's document.</summary>
    internal static void Release(PdfWorkingDocument? document, OpenedProtection? protection)
    {
        try { document?.Close(); }
        catch (Exception error) when (error is not OutOfMemoryException) { }
    }
}
