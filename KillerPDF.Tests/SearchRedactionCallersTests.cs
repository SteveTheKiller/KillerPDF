using System.IO;
using System.IO.Compression;
using System.Text;
using KillerPdf.Engine.Documents;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

// What the callers of a document search do with the pages it could not read, the options it ran with
// and a search that has been overtaken. SearchController, the command line and the viewer are not
// linked into this project, so their wiring is pinned in their source; what they call is tested here.
public sealed class SearchRedactionCallersTests : IDisposable
{
    private const string Font = "<< /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >>";
    private const string Undecodable = "<< /Filter /FlateDecode /Length 12 >>\nstream\nnot-flate-xx\nendstream";

    private readonly string _path = Path.Combine(Path.GetTempPath(), "killerpdf-search-callers-" + Guid.NewGuid().ToString("N") + ".pdf");

    public void Dispose()
    {
        try { File.Delete(_path); } catch (IOException) { }
    }

    // ---- A word typeset with a ligature reads as one character -----------------------------------

    [Fact]
    public void Search_FindsAWordSetWithALigatureByItsLetters()
    {
        File.WriteAllBytes(_path, LigaturePage());

        Assert.Equal(1, SearchService.Search(_path, "Offner").TotalHits);
    }

    [Fact]
    public void FindRedactionTermAreas_CoversAWordSetWithALigature()
    {
        File.WriteAllBytes(_path, LigaturePage());

        Assert.Single(Assert.Single(SearchService.FindRedactionTermAreas(_path, "Offner")).Value);
    }

    // ---- A redaction never finishes past a page it could not search (R3C-001) ------------------

    [Fact]
    public void TermAreas_AFormFieldThatCannotBeRead_StopsTheRedactionAndNamesItsPage()
    {
        File.WriteAllBytes(_path, UnreadableWidget());

        var error = Assert.Throws<RedactionPagesSkippedException>(() => SearchService.FindRedactionTermAreas(_path, "SECRET"));

        Assert.Equal([0], error.FieldPages);
        Assert.Empty(error.ContentPages);
        Assert.Equal("Redaction stopped because the form fields or comments on these pages could not be read, "
            + "so a term there might be missed: 1", error.Message);
    }

    [Fact]
    public void TermAreas_APageThatLostItsContent_StopsTheRedactionAndNamesItsPage()
    {
        File.WriteAllBytes(_path, Pages(Text("Conventional"), Undecodable));

        var error = Assert.Throws<RedactionPagesSkippedException>(() => SearchService.FindRedactionTermAreas(_path, "Conventional"));

        Assert.Equal([1], error.ContentPages);
        Assert.Empty(error.FieldPages);
        Assert.Contains("2", error.Message);
    }

    [Fact]
    public void TermAreas_APageTheReaderRefuses_StopsTheRedactionAndNamesItsPage()
    {
        File.WriteAllBytes(_path, Pages(Text("Conventional"), Stream("BT BT ET ET")));

        var error = Assert.Throws<RedactionPagesSkippedException>(() => SearchService.FindRedactionTermAreas(_path, "Conventional"));

        Assert.Equal([1], error.ContentPages);
    }

    [Fact]
    public void TermAreas_NoTermsToLookFor_SearchNothingAndSoSkipNothing()
    {
        File.WriteAllBytes(_path, UnreadableWidget());

        Assert.Empty(SearchService.FindRedactionTermAreas(_path, []));
    }

    [Fact]
    public void TheMessageCanBeBuiltInTheInterfaceLanguageAndCoversBothKindsOfPage()
    {
        var error = new RedactionPagesSkippedException([0], [2, 3]);

        Assert.Equal("fields 1. content 3, 4.", error.Localized("fields {0}.", "content {0}."));
        Assert.Equal("fields 1.", new RedactionPagesSkippedException([0], []).Localized("fields {0}.", "content {0}."));
    }

    // The macro hosts let this exception out of the Redact step, so the dispatcher's own failure path
    // is what stops the macro: no output, and the report carries the page.
    [Fact]
    public void AMacroRedactStepFailsNamingThePageAndProducesNoOutput()
    {
        File.WriteAllBytes(_path, UnreadableWidget());
        var dispatcher = new PdfMacroDispatcher([new PdfMacroOperationRegistration(PdfMacroOperation.Redact,
            (_, source, _, _) =>
            {
                SearchService.FindRedactionTermAreas(_path, ["SECRET"]);
                return new PdfMacroOperationResult(source);
            })]);

        PdfMacroRunReport report = dispatcher.RunReport(new PdfMacro("Redact", [new PdfMacroStep(PdfMacroOperation.Redact)]),
            [new ReadOnlyMemory<byte>(File.ReadAllBytes(_path))]);

        PdfMacroFileResult result = Assert.Single(report.Results);
        Assert.False(result.Succeeded);
        Assert.Null(result.Data);
        Assert.EndsWith("might be missed: 1", result.Error);
        Assert.Contains("might be missed: 1", report.ToText());
    }

    [Fact]
    public void TheWindowMacroTurnsTheStopIntoItsTranslatedMessage()
    {
        string body = Between(ReadSource("Shell", "Macros.cs"), "private PdfMacroOperationResult RedactMacroContent", "// Adds a searchable text layer");

        Assert.Contains("catch (RedactionPagesSkippedException unsearched)", body);
        Assert.Contains("Loc(\"Str_Macro_RedactSkippedPages\")", body);
        Assert.Contains("Loc(\"Str_Search_PagesSkipped\")", body);
        Assert.Contains("throw new InvalidOperationException(", body);
    }

    [Fact]
    public void TheCommandLineMacroLetsTheStopFailItsStep()
    {
        string source = ReadSource("Features", "Cli", "CliRunner.Macros.cs");

        Assert.DoesNotContain("catch (RedactionPagesSkippedException", source);
        Assert.Contains("SearchService.FindRedactionTermAreas(path, redaction.Terms)", source);
        Assert.Contains("could not be read", source[..source.IndexOf("private static int CliMacro(", StringComparison.Ordinal)]);
    }

    [Fact]
    public void TheCommandLineRedactExitsThreeWithNoOutputAndListsThePages()
    {
        string body = Between(ReadSource("Features", "Cli", "CliRunner.Redact.cs"), "private static int CliRedact(", "private static PdfEngineIntegration.SanitizeChoices? CliSanitizeChoices");

        Assert.Contains("SearchService.LoadPagesForReview(source, null)", body);
        Assert.Contains("unreadable.UnionWith(annotations.UnreadableAnnotationPages)", body);
        int stops = body.IndexOf("if (unreadable.Count > 0)", StringComparison.Ordinal);
        Assert.True(stops >= 0);
        string stop = body[stops..body.IndexOf("return 3;", stops, StringComparison.Ordinal)];
        Assert.Contains("unreadablePages", stop);
        // The stop comes before the dry run and before anything can write the output file.
        Assert.True(stops < body.IndexOf("options.ContainsKey(\"--dry-run\")", StringComparison.Ordinal));
        Assert.True(stops < body.IndexOf("CliWriteEditedCopy(", StringComparison.Ordinal));
    }

    // ---- A page that lost its content is not a searched page (R3C-010) --------------------------

    [Fact]
    public void Search_APageThatCannotInflate_IsListedAsUnreadableAndTheOtherPagesKeepTheirMatches()
    {
        File.WriteAllBytes(_path, Pages(Text("Conventional"), Undecodable));

        SearchResult result = SearchService.Search(_path, "Conventional");

        Assert.Equal(1, result.TotalHits);
        Assert.Equal([0], result.ResultPages);
        Assert.Equal([1], result.UnreadablePages);
        Assert.False(string.IsNullOrEmpty(result.UnreadableMessages[1]));
    }

    [Fact]
    public void Search_AnEmptyStream_IsAnEmptyPageNotAnUnreadableOne()
    {
        File.WriteAllBytes(_path, Pages(Text("Conventional"), Stream(""), CompressedEmptyStream()));

        SearchResult result = SearchService.Search(_path, "Conventional");

        Assert.Equal(1, result.TotalHits);
        Assert.Empty(result.UnreadablePages);
    }

    // What did decode on such a page is still real text, and redaction has to be able to find it.
    [Fact]
    public void Search_APageThatLostOneOfItsStreams_KeepsTheMatchesInTheOther()
    {
        File.WriteAllBytes(_path, TwoStreamPage(Text("Conventional"), Undecodable));

        SearchResult result = SearchService.Search(_path, "Conventional");

        Assert.Equal(1, result.TotalHits);
        Assert.Equal([0], result.UnreadablePages);
    }

    // A filter that throws even under recovery ends the page at that stream, and what came after it is gone too.
    [Fact]
    public void Search_APageCutShortByAStreamNothingCanDecode_IsListedAndKeepsWhatItRead()
    {
        File.WriteAllBytes(_path, TwoStreamPage(Text("Conventional"), "<< /Filter /JPXDecode /Length 4 >>\nstream\nabcd\nendstream"));

        SearchResult result = SearchService.Search(_path, "Conventional");

        Assert.Equal(1, result.TotalHits);
        Assert.Equal([0], result.UnreadablePages);
        Assert.Contains("truncated", result.UnreadableMessages[0]);
    }

    // The search-text exit code rests on this: a document with nothing readable is 1, never 3 or 0.
    [Fact]
    public void Search_EveryPageLost_ThrowsInsteadOfReportingNoHits()
    {
        File.WriteAllBytes(_path, Pages(Undecodable, Undecodable));

        Assert.ThrowsAny<InvalidDataException>(() => SearchService.Search(_path, "Conventional"));
    }

    [Fact]
    public void LoadPagesForReview_ListsAPageThatCannotInflateAndKeepsItsSlot()
    {
        File.WriteAllBytes(_path, Pages(Undecodable, Text("Conventional")));

        var (pages, unreadable) = SearchService.LoadPagesForReview(_path, null);

        Assert.Equal(2, pages.Count);
        Assert.Equal([0], unreadable);
        var review = PdfRedactionSearch.Find(pages, new PdfRedactionSearchOptions
        {
            Kind = PdfRedactionSearchKind.ExactText,
            Query = "Conventional"
        });
        Assert.Equal(1, Assert.Single(review.Matches).PageIndex);
    }

    [Fact]
    public void LoadPagesForReview_DoesNotListAnEmptyStream()
    {
        File.WriteAllBytes(_path, Pages(Text("Conventional"), Stream(""), CompressedEmptyStream()));

        Assert.Empty(SearchService.LoadPagesForReview(_path, null).Unreadable);
    }

    [Fact]
    public void LoadPagesForReview_EveryPageLost_Throws()
    {
        File.WriteAllBytes(_path, Pages(Undecodable, Undecodable));

        Assert.ThrowsAny<InvalidDataException>(() => SearchService.LoadPagesForReview(_path, null));
    }

    [Fact]
    public void FindExactMatches_NamesAPageThatCannotInflate_AndTheStrictFormRefusesIt()
    {
        File.WriteAllBytes(_path, Pages(Text("Conventional"), Undecodable));

        var matches = SearchService.FindExactMatches(_path, "Conventional", out List<int> unreadable);

        Assert.Equal([0], matches.Keys);
        Assert.Equal([1], unreadable);
        Assert.ThrowsAny<InvalidDataException>(() => SearchService.FindExactMatches(_path, "Conventional"));
    }

    // ---- Mark search results marks what the search found (R3C-019) ------------------------------

    [Theory]
    [InlineData(false, false, 4)]
    [InlineData(true, false, 3)]
    [InlineData(false, true, 2)]
    [InlineData(true, true, 1)]
    public void FindExactMatches_HonorsMatchCaseAndWholeWord(bool matchCase, bool wholeWord, int expected)
    {
        File.WriteAllBytes(_path, Pages(Text("art ART party cart")));

        var matches = SearchService.FindExactMatches(_path, "art", matchCase, wholeWord, out List<int> unreadable);

        Assert.Equal(expected, matches[0].Count);
        Assert.Equal(SearchService.Search(_path, "art", matchCase, wholeWord).TotalHits, matches[0].Count);
        Assert.Empty(unreadable);
    }

    // A whole-word phrase keeps its letter-exact box, so a redaction still takes only those letters.
    [Fact]
    public void FindExactMatches_WholeWordSpansWordsLikeTheSearchDoes()
    {
        File.WriteAllBytes(_path, Pages(Text("art party art partying")));

        var matches = SearchService.FindExactMatches(_path, "art party", matchCase: false, wholeWord: true, out _);

        Assert.Equal(2, matches[0].Count);   // one box per word of the single whole-word match
    }

    [Fact]
    public void MarkSearchResultsPassesTheOptionsTheResultsWereFoundWith()
    {
        string body = Between(ReadSource("Controls", "Viewer", "PdfViewer.Redaction.cs"), "private void MarkSearchResultsForRedaction", "private void FindTextForRedaction");

        Assert.Contains("PdfSearchText.Fold(Search.LastQuery)", body);
        Assert.Contains("Search.LastWholeWord", body);
        Assert.Contains("MatchCase = Search.LastMatchCase", body);
        Assert.Contains("SearchService.FindCurrentRedaction(snapshot, options)", body);
    }

    // ---- A search belongs to the tab that ran it (R3C-020) --------------------------------------

    [Fact]
    public void ATabParksItsQueryMatchesAndCursorWithItsOtherSearchState()
    {
        string tabs = ReadSource("Controls", "Viewer", "PdfViewer.Tabs.cs");

        Assert.Contains("s.SearchMatches = Search.Capture();", tabs);
        Assert.Contains("Search.Restore(s.SearchMatches);", tabs);
    }

    // The controller is the window's, so an unfocused pane capturing must not take the focused pane's results.
    [Fact]
    public void OnlyTheFocusedPaneParksTheWindowsSearch()
    {
        string capture = Between(ReadSource("Controls", "Viewer", "PdfViewer.Tabs.cs"), "private void CaptureSessionState(DocumentSession s)", "private void ApplySessionState(DocumentSession s)");
        int guard = capture.IndexOf("if (Host!.IsViewerFocused(this))", StringComparison.Ordinal);

        Assert.True(guard >= 0);
        foreach (string line in new[] { "s.SearchPageCursor = Search.PageCursor", "s.AllSearchRects   = Search.AllSearchRects",
            "s.SearchResultPages = Search.ResultPages", "s.SearchUnreadablePages = Search.UnreadablePages", "s.SearchMatches = Search.Capture()" })
            Assert.True(capture.IndexOf(line, StringComparison.Ordinal) > guard, line);
    }

    [Fact]
    public void CaptureAndRestoreCoverTheQueryItsOptionsTheMatchesAndTheCursor()
    {
        string controller = ReadSource("Features", "Search", "SearchController.cs");
        string capture = Between(controller, "internal ParkedSearch Capture()", "internal void Restore(");
        string restore = Between(controller, "internal void Restore(", "/// <summary>The query-too-short reset");

        foreach (string part in new[] { "LastQuery", "LastMatchCase", "LastWholeWord", "_matches", "_matchCursor" })
        {
            Assert.Contains(part, capture);
            Assert.Contains(part, restore);
        }
    }

    // ---- Search runs on a worker (R3C-021) --------------------------------------------------------

    [Fact]
    public void Search_ACancelledRunThrowsInsteadOfReturningWhatItHad()
    {
        byte[] source = Pages(Text("Conventional"), Text("Conventional"));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => SearchService.Search(source, null, "Conventional", false, false, cancelled.Token));
    }

    [Fact]
    public void Search_OverBytesFindsWhatTheFileSearchFinds()
    {
        byte[] source = Pages(Text("Conventional"), Undecodable);
        File.WriteAllBytes(_path, source);

        SearchResult bytes = SearchService.Search(source, null, "Conventional", false, false, CancellationToken.None);

        Assert.Equal(SearchService.Search(_path, "Conventional").TotalHits, bytes.TotalHits);
        Assert.Equal([1], bytes.UnreadablePages);
    }

    [Fact]
    public async Task ANewerSearchCancelsTheOlderOneAndOnlyItsResultArrives()
    {
        var runner = new LatestSearchRunner();
        using var older = new SemaphoreSlim(0);
        Task<string?> first = runner.RunAsync(token => { older.Release(); WaitForCancel(token); return "old"; }, () => true);
        await older.WaitAsync();

        Task<string?> second = runner.RunAsync(_ => "new", () => true);

        Assert.Null(await first);
        Assert.Equal("new", await second);
        Assert.False(runner.IsRunning);
    }

    // A run past its last cancellation check still finishes, and what it brings back must not land.
    [Fact]
    public async Task AnOlderResultThatFinishesAnywayIsDropped()
    {
        var runner = new LatestSearchRunner();
        using var release = new SemaphoreSlim(0);
        Task<string?> first = runner.RunAsync(_ => { release.Wait(); return "old"; }, () => true);

        Task<string?> second = runner.RunAsync(_ => "new", () => true);
        Assert.Equal("new", await second);
        release.Release();

        Assert.Null(await first);
    }

    [Fact]
    public async Task CancellingLeavesNoResultAndNothingRunning()
    {
        var runner = new LatestSearchRunner();
        using var started = new SemaphoreSlim(0);
        Task<string?> search = runner.RunAsync(token => { started.Release(); WaitForCancel(token); return "found"; }, () => true);
        await started.WaitAsync();
        Assert.True(runner.IsRunning);

        runner.Cancel();

        Assert.Null(await search);
        Assert.False(runner.IsRunning);
    }

    [Fact]
    public async Task AResultForADocumentThatChangedIsDropped()
    {
        var runner = new LatestSearchRunner();

        Assert.Null(await runner.RunAsync(_ => "found", () => false));
        Assert.False(runner.IsRunning);
    }

    [Fact]
    public async Task AFailedSearchThatIsStillCurrentReportsItsFailure()
    {
        var runner = new LatestSearchRunner();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            runner.RunAsync<string>(_ => throw new InvalidDataException(), () => true));
    }

    [Fact]
    public async Task AFailedSearchThatWasOvertakenSaysNothing()
    {
        var runner = new LatestSearchRunner();
        using var release = new SemaphoreSlim(0);
        Task<string?> first = runner.RunAsync<string>(_ => { release.Wait(); throw new InvalidDataException(); }, () => true);

        await runner.RunAsync(_ => "new", () => true);
        release.Release();

        Assert.Null(await first);
    }

    [Fact]
    public void TheControllerRunsTheSearchOnTheWorkerFromACopyOfTheFile()
    {
        string run = Between(ReadSource("Features", "Search", "SearchController.cs"), "private async Task RunAsync(string query)", "// Which version of the file");

        Assert.Contains("SearchService.Search(source, query, matchCase, wholeWord, token)", run);
        Assert.Contains("_runner.RunAsync(", run);
        Assert.Contains("_host.CurrentFile == file", run);
        // A running search leaves the results on show until its own arrive, and a dropped one never touches them.
        int awaited = run.IndexOf("await _runner.RunAsync(", StringComparison.Ordinal);
        int dropped = run.IndexOf("if (found is null) return;", StringComparison.Ordinal);
        Assert.InRange(dropped, awaited, run.IndexOf("ShowResults(", StringComparison.Ordinal));
        Assert.DoesNotContain("ClearResults(", run[(run.IndexOf("try", StringComparison.Ordinal))..dropped]);
    }

    [Fact]
    public void EveryResetStopsTheSearchThatWouldOverwriteIt()
    {
        string controller = ReadSource("Features", "Search", "SearchController.cs");

        Assert.Contains("CancelPending();", Between(controller, "internal void ClearMatches()", "internal void ClearPageResults()"));
        Assert.Contains("ClearMatches();", Between(controller, "internal void ClearPageResults()", "internal void Run(string query)"));
        Assert.Contains("CancelPending();", Between(controller, "internal void Restore(", "/// <summary>The query-too-short reset"));
        Assert.Contains("CancelPending();", Between(controller, "private async Task RunAsync(string query)", "(bool matchCase, bool wholeWord)"));
    }

    [Fact]
    public void TheSearchBarStopsASearchWhenItCloses_WhenTheTextChanges_AndHoldsReplaceUntilItEnds()
    {
        string bar = ReadSource("Shell", "Search.cs");

        string close = Between(bar, "private void CloseSearchBar()", "private void SearchBox_KeyDown");
        Assert.Contains("Search.CancelPending();", close);
        Assert.Contains("_searchDebounce?.Stop();", close);
        Assert.Contains("Search.CancelPending();", Between(bar, "private void SearchBox_TextChanged", "// Debounce:"));
        Assert.Contains("if (Search.IsSearching) return;", Between(bar, "private void ReplaceCurrentMatch()", "private void ReplaceAllMatches()"));
    }

    // ---- Fixtures -----------------------------------------------------------------------------

    private static void WaitForCancel(CancellationToken token)
    {
        token.WaitHandle.WaitOne(TimeSpan.FromSeconds(30));
        token.ThrowIfCancellationRequested();
    }

    // One readable widget holding the term, and a sibling whose /T makes the page's fields unreadable.
    private static byte[] UnreadableWidget() => Pdf(
        "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R 6 0 R] >> >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R /Resources {Font} /Annots [5 0 R 6 0 R] >>",
        Stream("BT /F1 12 Tf 20 100 Td (nothing) Tj ET"),
        "<< /Type /Annot /Subtype /Widget /FT /Tx /T (good) /V (SECRET) /Rect [10 10 90 30] /P 3 0 R >>",
        "<< /Type /Annot /Subtype /Widget /FT /Tx /T 5 /V (x) /Rect [10 40 90 60] /P 3 0 R >>");

    // Pages built from one content stream object each.
    private static byte[] Pages(params string[] contents)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Kids [{string.Join(" ", contents.Select((_, index) => $"{3 + 2 * index} 0 R"))}] /Count {contents.Length} >>"
        };
        for (int index = 0; index < contents.Length; index++)
        {
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources {Font} /Contents {4 + 2 * index} 0 R >>");
            objects.Add(contents[index]);
        }
        return Pdf([.. objects]);
    }

    // Byte 0x46 shows the U+FB00 ligature, so the page reads as the name with one character for "ff".
    private static byte[] LigaturePage() => Pdf(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 4 0 R /Resources "
            + "<< /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica /ToUnicode 5 0 R >> >> >> >>",
        Text("David OFner"),
        Stream("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n"
            + "1 begincodespacerange\n<00> <FF>\nendcodespacerange\n1 beginbfchar\n<46> <FB00>\nendbfchar\nendcmap\nend\nend\n"));

    private static byte[] TwoStreamPage(string first, string second) => Pdf(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources {Font} /Contents [4 0 R 5 0 R] >>",
        first,
        second);

    private static string Text(string text) => Stream($"BT /F1 12 Tf 72 700 Td ({text}) Tj ET");

    private static string Stream(string content) =>
        $"<< /Length {Encoding.Latin1.GetByteCount(content)} >>\nstream\n{content}\nendstream";

    // A compressed empty stream is valid, which is why a page's byte count cannot tell it from a lost one.
    private static string CompressedEmptyStream()
    {
        using var compressed = new MemoryStream();
        using (new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true)) { }
        byte[] bytes = compressed.ToArray();
        return $"<< /Filter /FlateDecode /Length {bytes.Length} >>\nstream\n{Encoding.Latin1.GetString(bytes)}\nendstream";
    }

    private static byte[] Pdf(params string[] objects)
    {
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int index = 0; index < objects.Length; index++)
        {
            offsets.Add(pdf.Length);
            pdf.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        int xref = pdf.Length;
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) pdf.Append($"{offset:0000000000} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(pdf.ToString());
    }

    private static string Between(string source, string start, string end)
    {
        int from = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, "Not found: " + start);
        int to = source.IndexOf(end, from, StringComparison.Ordinal);
        Assert.True(to > from, "Not found: " + end);
        return source[from..to];
    }

    private static string ReadSource(params string[] parts) =>
        File.ReadAllText(Path.Combine([RepositoryRoot(), .. parts]));

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "KillerPDF.csproj"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new InvalidOperationException("KillerPDF.csproj was not found above the test output.");
    }
}
