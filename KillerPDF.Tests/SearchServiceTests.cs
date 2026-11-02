using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests
{
    public class SearchServiceTests
    {
        private readonly SearchService _svc = new();

        [Fact]
        public void Search_EmptyQuery_ReturnsEmpty()
        {
            var result = SearchService.Search("irrelevant.pdf", "");
            Assert.Empty(result.ResultPages);
            Assert.Equal(0, result.TotalHits);
        }

        [Fact]
        public void Search_WhitespaceQuery_ReturnsEmpty()
        {
            var result = SearchService.Search("irrelevant.pdf", "   ");
            Assert.Empty(result.ResultPages);
            Assert.Equal(0, result.TotalHits);
        }

        [Fact]
        public void Search_EmptyFilePath_ReturnsEmpty()
        {
            var result = SearchService.Search("", "hello");
            Assert.Empty(result.ResultPages);
            Assert.Equal(0, result.TotalHits);
        }

        [Fact]
        public void Search_MissingFile_Throws()
        {
            // An empty result would tell the user the term is absent from a file that was never read.
            Assert.ThrowsAny<Exception>(() => SearchService.Search(@"C:\does\not\exist.pdf", "hello"));
        }

        [Fact]
        public void Search_UnreadableFile_ThrowsInsteadOfReportingNoHits()
        {
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid() + ".pdf");
            try
            {
                System.IO.File.WriteAllText(path, "this is not a pdf, so no text can be read from it");
                Assert.ThrowsAny<Exception>(() => SearchService.Search(path, "pdf"));
            }
            finally { System.IO.File.Delete(path); }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Search_FindsVisibleWordsInAFileOnlyRecoveryCanOpen(bool withoutMediaBox)
        {
            string text = System.Text.Encoding.ASCII.GetString(PdfTextSizeTests.BuildPdf());
            // The renamed key keeps every offset, and the wrong startxref leaves only recovery able to read it.
            if (withoutMediaBox) text = text.Replace("/MediaBox", "/MediaBoy");
            text = System.Text.RegularExpressions.Regex.Replace(text, @"startxref\n\d+", "startxref\n7");
            byte[] bytes = System.Text.Encoding.ASCII.GetBytes(text);
            Assert.ThrowsAny<Exception>(() => KillerPdf.Engine.Documents.PdfDocument.Open(bytes));
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid() + ".pdf");
            try
            {
                System.IO.File.WriteAllBytes(path, bytes);
                var result = SearchService.Search(path, "Conventional");
                Assert.Equal(1, result.TotalHits);
                Assert.Equal([0], result.ResultPages);
                Assert.IsType<PageTextRuns>(new TextRunService().GetPage(path, null, 0));
            }
            finally { System.IO.File.Delete(path); }
        }

        [Fact]
        public void SearchResult_PageRects_EmptyByDefault()
        {
            var result = new SearchResult();
            Assert.Empty(result.PageRects);
            Assert.Empty(result.ResultPages);
            Assert.Empty(result.UnreadablePages);
            Assert.Equal(0, result.TotalHits);
        }

        // One page the text reader refuses must not cost the matches on every other page (F3-001).
        [Fact]
        public void Search_OnePageTheReaderRefuses_KeepsTheOtherPagesAndNamesTheSkippedOne()
        {
            WithPdf([GoodPage, BadPage, GoodPage], path =>
            {
                var result = SearchService.Search(path, "Conventional");
                Assert.Equal(2, result.TotalHits);
                Assert.Equal([0, 2], result.ResultPages);
                Assert.Equal([1], result.UnreadablePages);
                Assert.Contains("Nested", result.UnreadableMessages[1]);
            });
        }

        [Fact]
        public void Search_EveryPageReadable_ReportsNoSkippedPages()
        {
            WithPdf([GoodPage, GoodPage], path =>
            {
                var result = SearchService.Search(path, "Conventional");
                Assert.Equal(2, result.TotalHits);
                Assert.Empty(result.UnreadablePages);
            });
        }

        // "No matches" for a file nothing could be read from would tell the user the term is absent.
        [Fact]
        public void Search_EveryPageUnreadable_ThrowsInsteadOfReportingNoHits()
        {
            WithPdf([BadPage, BadPage], path =>
                Assert.ThrowsAny<Exception>(() => SearchService.Search(path, "Conventional")));
        }

        [Fact]
        public void FindExactMatches_OnePageTheReaderRefuses_KeepsTheOtherPagesAndNamesTheSkippedOne()
        {
            WithPdf([GoodPage, BadPage, GoodPage], path =>
            {
                var matches = SearchService.FindExactMatches(path, "Conventional", out List<int> unreadable);
                Assert.Equal([0, 2], matches.Keys.OrderBy(page => page));
                Assert.Equal([1], unreadable);
            });
        }

        [Fact]
        public void FindExactMatches_EveryPageUnreadable_Throws()
        {
            WithPdf([BadPage, BadPage], path =>
                Assert.ThrowsAny<Exception>(() => SearchService.FindExactMatches(path, "Conventional", out _)));
        }

        // Find and Redact numbers pages by position, so dropping a page would mark the wrong pages.
        [Fact]
        public void LoadPagesForReview_AnUnreadablePageKeepsItsSlotSoLaterMarksLandOnTheRightPage()
        {
            WithPdf([GoodPage, BadPage, GoodPage], path =>
            {
                var (pages, unreadable) = SearchService.LoadPagesForReview(path, null);
                Assert.Equal(3, pages.Count);
                Assert.Null(pages[1]);
                Assert.Equal([1], unreadable);
                var review = KillerPdf.Engine.Documents.PdfRedactionSearch.Find(pages,
                    new KillerPdf.Engine.Documents.PdfRedactionSearchOptions
                    {
                        Kind = KillerPdf.Engine.Documents.PdfRedactionSearchKind.ExactText,
                        Query = "Conventional"
                    });
                Assert.Equal([0, 2], review.Matches.Select(match => match.PageIndex));
            });
        }

        [Fact]
        public void LoadPagesForReview_EveryPageUnreadable_Throws()
        {
            WithPdf([BadPage, BadPage], path =>
                Assert.ThrowsAny<Exception>(() => SearchService.LoadPagesForReview(path, null)));
        }

        // The strict form is for callers that must not proceed past a page they could not search.
        [Fact]
        public void FindExactMatches_WithoutAReport_StillThrowsOnAPageTheReaderRefuses()
        {
            WithPdf([GoodPage, BadPage], path =>
                Assert.ThrowsAny<Exception>(() => SearchService.FindExactMatches(path, "Conventional")));
        }

        [Theory]
        [InlineData("well-", 1)]
        [InlineData("well-known", 2)]
        [InlineData("wellknown", 2)]
        public void FindExactMatches_KeepsLiteralAndJoinedWrappedHyphens(string query, int areas)
        {
            WithPdf(["BT /F1 12 Tf 72 700 Td (well-) Tj 0 -20 Td (known) Tj ET"], path =>
            {
                var matches = SearchService.FindExactMatches(path, query);
                Assert.True(matches.TryGetValue(0, out var bounds), "Visible wrapped text must remain searchable.");
                Assert.Equal(areas, bounds!.Count);
            });
        }

        [Fact]
        public void SkippedPagesText_ListsPagesFromOneAndCapsALongList()
        {
            Assert.Equal("4", SearchService.SkippedPagesText([3]));
            Assert.Equal("2, 5, 9", SearchService.SkippedPagesText([1, 4, 8]));
            Assert.Equal("1, 2, 3, 4, 5, 6, 7, 8, 9, 10, ...", SearchService.SkippedPagesText(Enumerable.Range(0, 12)));
        }

        [Fact]
        public void Search_ArxivPaperWithAStrayMinusInANumber_ReadsEveryPage()
        {
            string path = CorpusFile("arxiv-cc-2025-01-batch2", "2411.07303v2.pdf");
            if (!System.IO.File.Exists(path)) return;
            var result = SearchService.Search(path, "the");
            Assert.Equal(900, result.TotalHits);
            Assert.Empty(result.UnreadablePages);
        }

        [Fact]
        public void Search_ArxivPaperWithABadShading_KeepsItsReadablePages()
        {
            string path = CorpusFile("arxiv-cc-2025-01-08-to-14-batch3", "2501.04289v1.pdf");
            if (!System.IO.File.Exists(path)) return;
            var result = SearchService.Search(path, "the");
            Assert.Equal(342, result.TotalHits);
            Assert.NotEmpty(result.UnreadablePages);
        }

        // SearchController, the CLI and the viewer are not linked into this project, so what they do with
        // the skipped pages is pinned in their source.
        [Fact]
        public void TheSearchBarReportsSkippedPagesAndNeverShowsPlainNoMatchesForThem()
        {
            string source = ReadSource("Features/Search/SearchController.cs");
            Assert.Contains("sr.UnreadablePages", source);
            Assert.Contains("Str_Search_PagesSkipped", source);
            Assert.Contains("Str_Search_NoMatchesPartial", source);
        }

        [Fact]
        public void SkippedPagesStayWithTheTabThatSearched()
        {
            string tabs = ReadSource("Controls/Viewer/PdfViewer.Tabs.cs");
            Assert.Contains("s.SearchUnreadablePages = Search.UnreadablePages", tabs);
            Assert.Contains("Search.UnreadablePages = s.SearchUnreadablePages", tabs);
        }

        [Fact]
        public void TheRedactFindDialogNamesThePagesItCouldNotSearch()
        {
            string dialog = ReadSource("Controls/RedactionFindDialog.cs");
            Assert.Contains("Str_Search_PagesSkipped", dialog);
            Assert.Contains("UnreadableAnnotationPages", dialog);
            Assert.Contains("SearchService.LoadPagesForReview(snapshot.Bytes, snapshot.Protection)", ReadSource("Controls/Viewer/PdfViewer.Redaction.cs"));
            Assert.Contains("SearchService.LoadPagesForReview(snapshot, protection)", ReadSource("Shell/GuidedTasks.cs"));
        }

        [Fact]
        public void TheCommandLineSearchReportsSkippedPagesAndExitsWithThree()
        {
            string source = ReadSource("Features/Cli/CliRunner.cs");
            int start = source.IndexOf("private static int CliSearchText", StringComparison.Ordinal);
            Assert.True(start >= 0);
            string body = source[start..Math.Min(source.Length, start + 2500)];
            Assert.Contains("unreadablePages", body);
            Assert.Contains("found.UnreadablePages.Count > 0 ? 3 : 0", body);
        }

        [Fact]
        public void MarkAllSearchResultsForRedactionWarnsAboutPagesItCouldNotSearch()
        {
            string source = ReadSource("Controls/Viewer/PdfViewer.Redaction.cs");
            int start = source.IndexOf("private void MarkSearchResultsForRedaction", StringComparison.Ordinal);
            Assert.True(start >= 0);
            string body = source[start..source.IndexOf("private void FindTextForRedaction", start, StringComparison.Ordinal)];
            Assert.DoesNotContain("catch (Exception) { matches = []; }", body);
            Assert.Contains("unreadable = [.. current.Unreadable]", body);
            Assert.Contains("Search.SkippedPagesMessage(unreadable)", body);
            Assert.Contains("KillerDialog.Show(", body);
        }

        private const string GoodPage = "BT /F1 12 Tf 72 700 Td (Conventional) Tj ET";

        // A content stream the strict text reader refuses while a renderer would draw around it.
        private const string BadPage = "BT BT ET ET";

        private static string CorpusFile(string batch, string name) =>
            System.IO.Path.Combine(@"C:\Users\steve\code\KillerPDF-Corpus\corpus\regression", batch, name);

        private static string ReadSource(string relativePath, [System.Runtime.CompilerServices.CallerFilePath] string testFile = "") =>
            System.IO.File.ReadAllText(System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(testFile))!, relativePath));

        private static void WithPdf(string[] pageContents, Action<string> check)
        {
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid() + ".pdf");
            try
            {
                System.IO.File.WriteAllBytes(path, BuildPdf(pageContents));
                check(path);
            }
            finally { System.IO.File.Delete(path); }
        }

        private static byte[] BuildPdf(string[] pageContents)
        {
            var objects = new List<string>
            {
                "<< /Type /Catalog /Pages 2 0 R >>",
                "<< /Type /Pages /Kids [" + string.Join(" ", pageContents.Select((_, i) => $"{4 + 2 * i} 0 R")) + $"] /Count {pageContents.Length} >>",
                "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>"
            };
            for (int i = 0; i < pageContents.Length; i++)
            {
                objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 3 0 R >> >> /Contents {5 + 2 * i} 0 R >>");
                objects.Add($"<< /Length {pageContents[i].Length} >>\nstream\n{pageContents[i]}\nendstream");
            }

            var pdf = new System.Text.StringBuilder("%PDF-1.4\n");
            var offsets = new List<int>();
            for (int i = 0; i < objects.Count; i++)
            {
                offsets.Add(pdf.Length);
                pdf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
            }
            int xref = pdf.Length;
            pdf.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
            foreach (int offset in offsets) pdf.Append($"{offset:D10} 00000 n \n");
            pdf.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
            return System.Text.Encoding.ASCII.GetBytes(pdf.ToString());
        }
    }
}
