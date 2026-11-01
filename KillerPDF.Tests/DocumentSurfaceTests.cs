using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Automation.Text;
using KillerPDF.Controls;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests
{
    // BF-U001: the page images get a Document element with the Text pattern over their text.
    public class DocumentSurfaceTests
    {
        private sealed class FakeSource(params string[][] pages) : IDocumentReadingSource
        {
            public PageReadingText? TaggedPage;
            public (int, int, int, int)? Selected;
            public int ScrolledTo = -1;
            public int PageCount => pages.Length;
            public int CurrentPage { get; set; }
            public object Version => PageCount;
            public PageReadingText PageText(int page) => TaggedPage ?? PageReadingText.Build(
                [.. pages[page].SelectMany((line, l) => line.Split(' ').SelectMany((word, w) =>
                    word.Select(c => (c.ToString(), l * 100 + w, l))))]);
            public IReadOnlyList<Rect> ScreenRects(int page, int startCaret, int endCaret) =>
                [new Rect(page * 1000 + startCaret, 0, endCaret - startCaret, 10)];
            public (int Page, int Caret)? CaretFromScreenPoint(Point point) =>
                ((int)(point.X / 1000), (int)(point.X % 1000));
            public IReadOnlyList<(int Page, int Start, int End)> VisibleCaretRanges() =>
                [(CurrentPage, 0, PageText(CurrentPage).CaretCount)];
            public void Select(int startPage, int startCaret, int endPage, int endCaret) =>
                Selected = (startPage, startCaret, endPage, endCaret);
            public (int StartPage, int StartCaret, int EndPage, int EndCaret)? Selection => Selected;
            public void ScrollToPage(int page) => ScrolledTo = page;
            public event Action? TextChanged { add { } remove { } }
            public event Action? SelectionChanged { add { } remove { } }
        }

        [Fact]
        public void TabTraversalEntersAndLeavesTheReadingDocument() => OnSta(() =>
        {
            var before = new System.Windows.Controls.Button { Content = "Before" };
            var surface = new DocumentSurface { Source = new FakeSource(["Reading order check"]), Height = 40 };
            var after = new System.Windows.Controls.Button { Content = "After" };
            var panel = new System.Windows.Controls.StackPanel();
            panel.Children.Add(before);
            panel.Children.Add(surface);
            panel.Children.Add(after);
            var window = new Window { Content = panel, Width = 200, Height = 150,
                Left = -10000, Top = -10000, ShowInTaskbar = false };
            try
            {
                window.Show();
                Assert.True(before.Focus(), "The traversal starting control could not receive focus.");
                Assert.True(before.MoveFocus(new(System.Windows.Input.FocusNavigationDirection.Next)));
                Assert.Same(surface, System.Windows.Input.Keyboard.FocusedElement);
                var peer = UIElementAutomationPeer.CreatePeerForElement(surface);
                Assert.True(peer.IsKeyboardFocusable());
                Assert.Equal("Reading order check", ((ITextProvider)peer.GetPattern(PatternInterface.Text)).DocumentRange.GetText(-1));
                Assert.True(surface.MoveFocus(new(System.Windows.Input.FocusNavigationDirection.Next)));
                Assert.Same(after, System.Windows.Input.Keyboard.FocusedElement);
            }
            finally { window.Close(); }
        });

        [Fact]
        public void ExposesADocumentWithTheTextPattern() => OnSta(() =>
        {
            var source = new FakeSource(["Invoice total", "due now"], ["Page two"]);
            var surface = new DocumentSurface { Source = source };
            AutomationPeer peer = UIElementAutomationPeer.CreatePeerForElement(surface);

            Assert.Equal(AutomationControlType.Document, peer.GetAutomationControlType());
            var text = Assert.IsAssignableFrom<ITextProvider>(peer.GetPattern(PatternInterface.Text));
            Assert.Equal("Invoice total\ndue now\nPage two", text.DocumentRange.GetText(-1));
        });

        [Theory]
        [InlineData("A")]
        [InlineData("😀")]
        public void CollapsedMoveReachesTheFinalInsertionPoint(string value) => OnSta(() =>
        {
            var text = (ITextProvider)UIElementAutomationPeer.CreatePeerForElement(
                new DocumentSurface { Source = new FakeSource([value]) }).GetPattern(PatternInterface.Text);
            var caret = text.GetSelection()[0];
            Assert.Equal(1, caret.Move(TextUnit.Character, 1));
            Assert.Equal(0, caret.CompareEndpoints(TextPatternRangeEndpoint.Start, text.DocumentRange,
                TextPatternRangeEndpoint.End));
            Assert.Equal("", caret.GetText(-1));
            Assert.Equal(-1, caret.Move(TextUnit.Character, -1));
            Assert.Equal(0, caret.CompareEndpoints(TextPatternRangeEndpoint.Start, text.DocumentRange,
                TextPatternRangeEndpoint.Start));
        });

        [Fact]
        public void ReadsLineByLineAndPageByPage() => OnSta(() =>
        {
            var source = new FakeSource(["Invoice total", "due now"], ["Page two"]);
            var text = (ITextProvider)UIElementAutomationPeer.CreatePeerForElement(
                new DocumentSurface { Source = source }).GetPattern(PatternInterface.Text);

            ITextRangeProvider range = text.GetSelection()[0];
            range.ExpandToEnclosingUnit(TextUnit.Line);
            Assert.Equal("Invoice total\n", range.GetText(-1));
            Assert.Equal(1, range.Move(TextUnit.Line, 1));
            Assert.Equal("due now\n", range.GetText(-1));
            Assert.Equal(1, range.Move(TextUnit.Page, 1));
            Assert.Equal("Page two", range.GetText(-1));
            range.ScrollIntoView(true);
            Assert.Equal(1, source.ScrolledTo);
        });

        [Theory]
        [InlineData(1)]
        [InlineData(-1)]
        public void FailedNonemptyMovePreservesTheRequestedText(int direction) => OnSta(() =>
        {
            var text = (ITextProvider)UIElementAutomationPeer.CreatePeerForElement(
                new DocumentSurface { Source = new FakeSource(["cat"]) }).GetPattern(PatternInterface.Text);
            var range = text.DocumentRange.FindText("at", false, false)!;
            Assert.Equal(0, range.Move(TextUnit.Word, direction));
            Assert.Equal("at", range.GetText(-1));
        });

        [Fact]
        public void AnEmptyCaretRangeStillReportsWhereItIs() => OnSta(() =>
        {
            var text = (ITextProvider)UIElementAutomationPeer.CreatePeerForElement(
                new DocumentSurface { Source = new FakeSource(["Invoice total"]) }).GetPattern(PatternInterface.Text);

            Assert.Equal([0, 0, 1, 10], text.GetSelection()[0].GetBoundingRectangles());
        });

        [Fact]
        public void SelectsAndOutlinesTheCharactersOfARange() => OnSta(() =>
        {
            var source = new FakeSource(["Invoice total"]);
            var text = (ITextProvider)UIElementAutomationPeer.CreatePeerForElement(
                new DocumentSurface { Source = source }).GetPattern(PatternInterface.Text);

            ITextRangeProvider word = text.DocumentRange.FindText("total", false, false)!;
            word.Select();
            double[] rects = word.GetBoundingRectangles();

            // "Invoice total": carets skip the inserted space, so "total" is carets 7..12.
            Assert.Equal((0, 7, 0, 12), source.Selected);
            Assert.Equal([7, 0, 5, 10], rects);
            Assert.Equal(true, word.GetAttributeValue(TextPattern.IsReadOnlyAttribute.Id));
        });

        [Fact]
        public void TaggedSelectionRoundTripsThroughTheProvider() => OnSta(() =>
        {
            var source = new FakeSource(["RiLe"])
            {
                TaggedPage = PageReadingText.BuildTagged(
                    [("R", 0, 0, 1), ("i", 0, 0, 1), ("L", 1, 0, 0), ("e", 1, 0, 0)], [0, 1])
            };
            var text = (ITextProvider)UIElementAutomationPeer.CreatePeerForElement(
                new DocumentSurface { Source = source }).GetPattern(PatternInterface.Text);
            foreach (string expected in new[] { "Le", "Ri", "Le\nRi" })
            {
                text.DocumentRange.FindText(expected, false, false)!.Select();
                Assert.Equal(expected, Assert.Single(text.GetSelection()).GetText(-1));
            }
        });

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void FindTextKeepsSourceBoundaries(bool backward, bool ignoreCase) => OnSta(() =>
        {
            var source = new FakeSource(["ab"]);
            var text = (ITextProvider)UIElementAutomationPeer.CreatePeerForElement(
                new DocumentSurface { Source = source }).GetPattern(PatternInterface.Text);
            Assert.Null(text.DocumentRange.FindText("a\u00adb", backward, ignoreCase));
            var found = text.DocumentRange.FindText(ignoreCase ? "AB" : "ab", backward, ignoreCase);
            Assert.NotNull(found);
            Assert.Equal("ab", found!.GetText(-1));
        });

        [Fact]
        public void LinksAreNamedInvokableHyperlinks() => OnSta(() =>
        {
            int followed = 0;
            var link = new LinkOverlay(() => followed++) { ToolTip = "https://example.test" };
            AutomationPeer peer = UIElementAutomationPeer.CreatePeerForElement(link);

            Assert.Equal(AutomationControlType.Hyperlink, peer.GetAutomationControlType());
            Assert.Equal("https://example.test", peer.GetName());
            Assert.True(peer.IsKeyboardFocusable());
            Assert.IsAssignableFrom<IInvokeProvider>(peer.GetPattern(PatternInterface.Invoke)).Invoke();
            Assert.Equal(1, followed);
        });

        private static void OnSta(Action test)
        {
            Exception? failure = null;
            var thread = new Thread(() =>
            {
                try { test(); }
                catch (Exception ex) { failure = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "The STA test timed out.");
            if (failure is not null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
