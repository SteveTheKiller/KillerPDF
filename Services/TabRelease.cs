namespace KillerPDF.Services;

/// <summary>What a tab gives up when it closes, by any route: Close, Close All Tabs, or its window
/// closing.</summary>
internal static class TabRelease
{
    /// <summary>Closes the tab's document and drops every parse its key decrypted, so no key outlives
    /// its tab. A blank tab has no protection to forget.</summary>
    internal static void Release(PdfWorkingDocument? document, OpenedProtection? protection)
    {
        try { document?.Close(); }
        catch (Exception error) when (error is not OutOfMemoryException) { }
        if (protection is not null) PdfPageRenderSession.ForgetKey(protection);
    }
}
