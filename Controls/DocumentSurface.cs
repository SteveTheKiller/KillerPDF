using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Automation.Text;
using System.Windows.Controls;
using System.Windows.Input;
using KillerPDF.Services;

namespace KillerPDF.Controls
{
    /// <summary>What the reading surface needs from the viewer that shows the document.</summary>
    internal interface IDocumentReadingSource
    {
        int PageCount { get; }
        int CurrentPage { get; }
        /// <summary>Changes whenever the document or its text changes, so cached text is rebuilt.</summary>
        object Version { get; }
        PageReadingText PageText(int page);
        /// <summary>Screen rectangles (device pixels) for a caret range on a page; empty when it is not on screen.</summary>
        IReadOnlyList<Rect> ScreenRects(int page, int startCaret, int endCaret);
        (int Page, int Caret)? CaretFromScreenPoint(Point point);
        IReadOnlyList<(int Page, int Start, int End)> VisibleCaretRanges();
        void Select(int startPage, int startCaret, int endPage, int endCaret);
        /// <summary>The current text selection as page carets, or null.</summary>
        (int StartPage, int StartCaret, int EndPage, int EndCaret)? Selection { get; }
        void ScrollToPage(int page);
        void ScrollToCaret(int page, int caret, bool? alignToTop = null) => ScrollToPage(page);
        /// <summary>Raised when the document, its text or the selection changes.</summary>
        event Action? TextChanged;
        event Action? SelectionChanged;
    }

    /// <summary>
    /// The border around the document's pages. It gives the pages, which are images, a Document
    /// automation element with the UI Automation Text pattern over their extracted text, so
    /// Narrator and NVDA can read, move by character, word, line and page, find and select (BF-U001).
    /// </summary>
    internal sealed class DocumentSurface : Border
    {
        private IDocumentReadingSource? _source;
        private DocumentTextModel? _model;
        private object? _modelVersion;
        private (TextPosition Start, TextPosition End, object Version)? _logicalSelection;
        private bool _selectingRange;
        private TextPosition? _keyboardAnchor;
        private TextPosition? _keyboardCaret;

        public DocumentSurface()
        {
            Focusable = true;
            System.Windows.Input.KeyboardNavigation.SetIsTabStop(this, true);
        }

        internal IDocumentReadingSource? Source
        {
            get => _source;
            set
            {
                if (_source is not null) { _source.TextChanged -= Reset; _source.SelectionChanged -= OnSelection; }
                _source = value;
                if (_source is not null) { _source.TextChanged += Reset; _source.SelectionChanged += OnSelection; }
                Reset();
            }
        }

        internal DocumentTextModel? Model
        {
            get
            {
                if (_source is null) return null;
                object version = _source.Version;
                if (_model is null || !Equals(version, _modelVersion))
                {
                    _logicalSelection = null;
                    _keyboardAnchor = _keyboardCaret = null;
                    IDocumentReadingSource source = _source;
                    _model = new DocumentTextModel(source.PageCount, page => source.PageText(page).Text);
                    _modelVersion = version;
                }
                return _model;
            }
        }

        private void Reset()
        {
            _model = null;
            _logicalSelection = null;
            _keyboardAnchor = _keyboardCaret = null;
            if (UIElementAutomationPeer.FromElement(this) is DocumentPeer peer)
                peer.RaiseAutomationEvent(AutomationEvents.TextPatternOnTextChanged);
        }

        private void OnSelection()
        {
            if (!_selectingRange)
            {
                _logicalSelection = null;
                _keyboardAnchor = _keyboardCaret = null;
            }
            if (UIElementAutomationPeer.FromElement(this) is DocumentPeer peer)
                peer.RaiseAutomationEvent(AutomationEvents.TextPatternOnTextSelectionChanged);
        }

        internal static bool IsReadingNavigation(Key key, ModifierKeys modifiers) =>
            (modifiers & (ModifierKeys.Alt | ModifierKeys.Windows)) == 0
            && key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown
            && (!modifiers.HasFlag(ModifierKeys.Control) || key is not (Key.PageUp or Key.PageDown));

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            ModifierKeys modifiers = Keyboard.Modifiers;
            if (e.Handled || !IsKeyboardFocused || !IsReadingNavigation(e.Key, modifiers)
                || Source is not { PageCount: > 0 } source || Model is not { } model) return;
            var peer = (DocumentPeer)UIElementAutomationPeer.CreatePeerForElement(this);
            var current = (Range)peer.GetSelection()[0];
            TextPosition caret = _keyboardCaret ?? current.EndPosition;
            bool extend = modifiers.HasFlag(ModifierKeys.Shift);
            TextPosition anchor = extend ? _keyboardAnchor ?? current.StartPosition : caret;
            TextPosition target = caret;
            bool control = modifiers.HasFlag(ModifierKeys.Control);
            if (!extend && current.StartPosition != current.EndPosition && e.Key is Key.Left or Key.Right)
                target = e.Key == Key.Left ? current.StartPosition : current.EndPosition;
            else if (e.Key == Key.Home)
                target = control ? model.Start : model.Enclosing(caret, ReadingUnit.Line).Start;
            else if (e.Key == Key.End)
            {
                target = control ? model.End : model.Enclosing(caret, ReadingUnit.Line).End;
                if (!control && target != model.End) target = model.Previous(target, ReadingUnit.Character);
            }
            else
            {
                ReadingUnit unit = e.Key is Key.PageUp or Key.PageDown ? ReadingUnit.Page
                    : e.Key is Key.Up or Key.Down ? ReadingUnit.Line
                    : control ? ReadingUnit.Word : ReadingUnit.Character;
                if (unit is ReadingUnit.Line or ReadingUnit.Page) target = model.Enclosing(target, unit).Start;
                model.MoveEndpoint(ref target, unit, e.Key is Key.Left or Key.Up or Key.PageUp ? -1 : 1);
            }
            if (!extend) anchor = target;
            source.ScrollToCaret(target.Page, source.PageText(target.Page).CaretAt(target.Offset));
            new Range(peer, anchor <= target ? anchor : target, anchor <= target ? target : anchor).Select();
            _keyboardAnchor = anchor;
            _keyboardCaret = target;
            e.Handled = true;
        }

        protected override AutomationPeer OnCreateAutomationPeer() => new DocumentPeer(this);

        internal sealed class DocumentPeer(DocumentSurface owner) : FrameworkElementAutomationPeer(owner), ITextProvider
        {
            internal DocumentSurface Surface => (DocumentSurface)Owner;

            protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Document;
            protected override string GetClassNameCore() => "PdfDocument";
            protected override bool IsControlElementCore() => true;
            protected override bool IsContentElementCore() => true;

            public override object? GetPattern(PatternInterface patternInterface) =>
                patternInterface == PatternInterface.Text && Surface.Source is not null ? this : base.GetPattern(patternInterface);

            internal IRawElementProviderSimple Provider => ProviderFromPeer(this);

            private DocumentTextModel Model => Surface.Model ?? new DocumentTextModel(0, _ => string.Empty);

            public ITextRangeProvider DocumentRange => new Range(this, Model.Start, Model.End);

            public SupportedTextSelection SupportedTextSelection => SupportedTextSelection.Single;

            public ITextRangeProvider[] GetSelection()
            {
                DocumentTextModel model = Model;
                IDocumentReadingSource? source = Surface.Source;
                if (Surface._logicalSelection is { } selected && Equals(selected.Version, source?.Version))
                    return [new Range(this, model.Normalize(selected.Start), model.Normalize(selected.End))];
                if (source?.Selection is { } s)
                {
                    PageReadingText first = source.PageText(s.StartPage), last = source.PageText(s.EndPage);
                    var firstOffsets = first.SelectionOffsets(s.StartCaret,
                        s.StartPage == s.EndPage ? s.EndCaret : first.CaretCount);
                    var lastOffsets = s.StartPage == s.EndPage ? firstOffsets : last.SelectionOffsets(0, s.EndCaret);
                    return [new Range(this,
                        new TextPosition(s.StartPage, firstOffsets.Start),
                        new TextPosition(s.EndPage, lastOffsets.End))];
                }
                var caret = model.Normalize(new TextPosition(Math.Max(0, source?.CurrentPage ?? 0), 0));
                return [new Range(this, caret, caret)];
            }

            public ITextRangeProvider[] GetVisibleRanges()
            {
                IDocumentReadingSource? source = Surface.Source;
                if (source is null) return [];
                var result = new List<ITextRangeProvider>();
                foreach (var visible in source.VisibleCaretRanges())
                {
                    var offsets = source.PageText(visible.Page).SelectionOffsets(visible.Start, visible.End);
                    if (offsets.Start < offsets.End)
                        result.Add(new Range(this, new TextPosition(visible.Page, offsets.Start),
                            new TextPosition(visible.Page, offsets.End)));
                }
                return [.. result];
            }

            public ITextRangeProvider RangeFromChild(IRawElementProviderSimple childElement) => DocumentRange;

            public ITextRangeProvider RangeFromPoint(Point screenLocation)
            {
                IDocumentReadingSource? source = Surface.Source;
                var position = source?.CaretFromScreenPoint(screenLocation);
                var start = Model.Normalize(position is { } hit
                    ? new TextPosition(hit.Page, source!.PageText(hit.Page).OffsetOf(hit.Caret))
                    : new TextPosition(Math.Max(0, source?.CurrentPage ?? 0), 0));
                return new Range(this, start, start);
            }
        }

        /// <summary>A range of document text between two positions.</summary>
        private sealed class Range(DocumentPeer peer, TextPosition start, TextPosition end) : ITextRangeProvider
        {
            private TextPosition _start = start;
            private TextPosition _end = end;
            internal TextPosition StartPosition => _start;
            internal TextPosition EndPosition => _end;

            private DocumentTextModel Model => peer.Surface.Model ?? new DocumentTextModel(0, _ => string.Empty);
            private IDocumentReadingSource? Source => peer.Surface.Source;

            private static ReadingUnit Unit(TextUnit unit) => unit switch
            {
                TextUnit.Character => ReadingUnit.Character,
                TextUnit.Format => ReadingUnit.Character,
                TextUnit.Word => ReadingUnit.Word,
                TextUnit.Line => ReadingUnit.Line,
                TextUnit.Paragraph => ReadingUnit.Paragraph,
                TextUnit.Page => ReadingUnit.Page,
                _ => ReadingUnit.Document
            };

            private static Range Of(ITextRangeProvider other) =>
                other as Range ?? throw new ArgumentException("The range belongs to another text provider.");

            private TextPosition Endpoint(TextPatternRangeEndpoint endpoint) =>
                endpoint == TextPatternRangeEndpoint.Start ? _start : _end;

            private void SetEndpoint(TextPatternRangeEndpoint endpoint, TextPosition value)
            {
                value = Model.Normalize(value);
                if (endpoint == TextPatternRangeEndpoint.Start) { _start = value; if (_end < _start) _end = _start; }
                else { _end = value; if (_start > _end) _start = _end; }
            }

            public ITextRangeProvider Clone() => new Range(peer, _start, _end);

            public bool Compare(ITextRangeProvider range)
            {
                Range other = Of(range);
                return Model.Normalize(_start) == Model.Normalize(other._start)
                    && Model.Normalize(_end) == Model.Normalize(other._end);
            }

            public int CompareEndpoints(TextPatternRangeEndpoint endpoint, ITextRangeProvider targetRange,
                TextPatternRangeEndpoint targetEndpoint) =>
                Model.Normalize(Endpoint(endpoint)).CompareTo(Model.Normalize(Of(targetRange).Endpoint(targetEndpoint)));

            public void ExpandToEnclosingUnit(TextUnit unit) =>
                (_start, _end) = Model.Enclosing(_start, Unit(unit));

            public ITextRangeProvider? FindAttribute(int attribute, object value, bool backward) => null;

            public ITextRangeProvider? FindText(string text, bool backward, bool ignoreCase) =>
                Model.Find(_start, _end, text, backward, ignoreCase) is { } found ? new Range(peer, found.Start, found.End) : null;

            public object GetAttributeValue(int attribute) =>
                attribute == TextPattern.IsReadOnlyAttribute.Id ? true : AutomationElementIdentifiers.NotSupported;

            public double[] GetBoundingRectangles()
            {
                IDocumentReadingSource? source = Source;
                if (source is null) return [];
                TextPosition from = Model.Normalize(_start), to = Model.Normalize(_end);
                // An empty range is the caret. Screen readers draw their highlight from these
                // rectangles, so report the character it sits on rather than nothing.
                if (from == to && from.Page < Model.PageCount)
                {
                    int length = source.PageText(from.Page).Text.Length;
                    if (from.Offset < length) to = new TextPosition(from.Page, from.Offset + 1);
                    else if (from.Offset > 0) from = new TextPosition(from.Page, from.Offset - 1);
                }
                var rects = new List<double>();
                for (int page = from.Page; page <= to.Page && page < Model.PageCount; page++)
                {
                    PageReadingText text = source.PageText(page);
                    foreach (var (startCaret, endCaret) in text.Segments(page == from.Page ? from.Offset : 0,
                                 page == to.Page ? to.Offset : text.Text.Length))
                        foreach (Rect rect in source.ScreenRects(page, startCaret, endCaret))
                            rects.AddRange([rect.X, rect.Y, rect.Width, rect.Height]);
                }
                return [.. rects];
            }

            public IRawElementProviderSimple GetEnclosingElement() => peer.Provider;

            public string GetText(int maxLength) => Model.GetText(_start, _end, maxLength);

            public int Move(TextUnit unit, int count)
            {
                if (count == 0) return 0;
                return Model.Move(ref _start, ref _end, Unit(unit), count);
            }

            public int MoveEndpointByUnit(TextPatternRangeEndpoint endpoint, TextUnit unit, int count)
            {
                TextPosition at = Endpoint(endpoint);
                int moved = Model.MoveEndpoint(ref at, Unit(unit), count);
                SetEndpoint(endpoint, at);
                return moved;
            }

            public void MoveEndpointByRange(TextPatternRangeEndpoint endpoint, ITextRangeProvider targetRange,
                TextPatternRangeEndpoint targetEndpoint) =>
                SetEndpoint(endpoint, Of(targetRange).Endpoint(targetEndpoint));

            public void Select()
            {
                IDocumentReadingSource? source = Source;
                if (source is null) return;
                // Selection on the page is one caret span per page boundary, so a structure-ordered
                // range selects from its first to its last character.
                TextPosition from = Model.Normalize(_start), to = Model.Normalize(_end);
                PageReadingText first = source.PageText(from.Page), last = source.PageText(to.Page);
                var firstRuns = first.Segments(from.Offset, from.Page == to.Page ? to.Offset : first.Text.Length);
                var lastRuns = from.Page == to.Page ? firstRuns : last.Segments(0, to.Offset);
                int startCaret = firstRuns.Count > 0 ? firstRuns.Min(r => r.Start) : first.CaretAt(from.Offset);
                int endCaret = lastRuns.Count > 0 ? lastRuns.Max(r => r.End) : last.CaretAt(to.Offset);
                peer.Surface._logicalSelection = (from, to, source.Version);
                peer.Surface._keyboardAnchor = peer.Surface._keyboardCaret = null;
                bool selecting = peer.Surface._selectingRange;
                peer.Surface._selectingRange = true;
                try { source.Select(from.Page, startCaret, to.Page, Math.Max(endCaret, from.Page == to.Page ? startCaret : 0)); }
                catch { peer.Surface._logicalSelection = null; throw; }
                finally { peer.Surface._selectingRange = selecting; }
            }

            public void AddToSelection() => throw new InvalidOperationException();
            public void RemoveFromSelection() => throw new InvalidOperationException();

            public void ScrollIntoView(bool alignToTop)
            {
                if (Source is not { } source) return;
                TextPosition position = Model.Normalize(alignToTop ? _start : _end);
                source.ScrollToCaret(position.Page, source.PageText(position.Page).CaretAt(position.Offset), alignToTop);
            }

            public IRawElementProviderSimple[] GetChildren() => [];
        }
    }
}
