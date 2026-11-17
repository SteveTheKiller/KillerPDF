using System.IO;
using KillerPdf.Engine.Documents;

namespace KillerPDF.Services;

/// <summary>
/// The Redact step of a macro, shared by the app and the command line so they find, plan and
/// remove the same things. Only the question of which pages may become images differs: the app
/// asks the user and the command line follows --rasterize.
/// </summary>
internal static class MacroRedactionPass
{
    internal sealed record Outcome(byte[] Bytes, int Areas, int Pages, int Rasterized);

    /// <summary>Fills <paramref name="approved"/> and <paramref name="skipped"/> for the pages that
    /// would have to become images, or returns false to stop the step.</summary>
    internal delegate bool RasterDecision(IReadOnlyList<int> rasterPages, List<int> approved, HashSet<int> skipped);

    /// <summary>Null when nothing matched, so the step passes the document on unchanged.</summary>
    internal static Outcome? Run(ReadOnlyMemory<byte> source, IReadOnlyCollection<string> terms,
        IEnumerable<PdfRedactionRegion> marked, string workingPath, RasterDecision decide,
        Func<IReadOnlyList<int>, bool>? approveMore, CancellationToken token)
    {
        File.WriteAllBytes(workingPath, source.ToArray());
        List<PdfRedactionRegion> regions = [.. marked];
        token.ThrowIfCancellationRequested();
        // All terms in one pass, so the document is parsed once rather than twice per term. A page
        // that could not be searched throws, which fails the step with the pages named.
        foreach (var (page, rects) in SearchService.FindRedactionTermAreas(workingPath, terms))
            foreach (var (left, bottom, right, top) in rects)
                regions.Add(new PdfRedactionRegion(page, new PdfContentBounds(left, bottom, right, top)));
        if (regions.Count == 0) return null;

        PdfContentRedactionPlan plan = PdfEngineIntegration.AnalyzeRedaction(
            PdfEngineIntegration.OpenForReading(source), regions);
        var approved = new List<int>();
        var skipped = new HashSet<int>();
        if (plan.RasterPages.Count > 0 && !decide(plan.RasterPages, approved, skipped))
            throw new OperationCanceledException(token);
        regions.RemoveAll(region => skipped.Contains(region.PageIndex));
        if (regions.Count == 0) return null;

        token.ThrowIfCancellationRequested();
        PdfEngineIntegration.ApplyRedaction(workingPath, regions, approved, approveMore);
        return new Outcome(File.ReadAllBytes(workingPath), regions.Count,
            regions.Select(region => region.PageIndex).Distinct().Count(), approved.Count);
    }
}
