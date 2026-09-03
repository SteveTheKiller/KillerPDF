namespace KillerPdf.Engine.Documents;

/// <summary>The isolated content-extraction result for one page.</summary>
public sealed record PdfPageContentBatchResult(
    int PageIndex, PdfPageContent? Content, string? Error, bool WasCanceled)
{
    /// <summary>Gets whether extraction completed successfully.</summary>
    public bool Succeeded => Content is not null && Error is null && !WasCanceled;
}

/// <summary>Extracts a complete document while isolating malformed pages.</summary>
public static class PdfPageContentBatch
{
    /// <summary>Reads pages in order, recording recoverable failures without aborting later pages.</summary>
    public static IReadOnlyList<PdfPageContentBatchResult> Read(
        PdfDocument document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        var reader = new PdfPageContentReader(document);
        var results = new List<PdfPageContentBatchResult>(reader.PageCount);
        for (int pageIndex = 0; pageIndex < reader.PageCount; pageIndex++)
        {
            if (cancellationToken.IsCancellationRequested) break;
            try
            {
                results.Add(new PdfPageContentBatchResult(
                    pageIndex, reader.Read(pageIndex, cancellationToken), null, false));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                results.Add(new PdfPageContentBatchResult(pageIndex, null, null, true));
                break;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException
                and not StackOverflowException and not AccessViolationException)
            {
                results.Add(new PdfPageContentBatchResult(pageIndex, null, exception.Message, false));
            }
        }
        return Array.AsReadOnly(results.ToArray());
    }
}
