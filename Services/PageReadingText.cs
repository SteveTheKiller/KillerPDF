using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace KillerPDF.Services
{
    /// <summary>
    /// One page's readable text built from its reading-order characters: a space between words,
    /// a newline between lines, and a map from each text offset back to the character (caret)
    /// it belongs to, so a screen reader's range can be drawn and selected on the page.
    /// </summary>
    internal sealed class PageReadingText
    {
        private readonly (int Start, int Length)[]? _nativeRanges;

        private PageReadingText(string text, int[] carets, int caretCount,
            (int Start, int Length)[]? nativeRanges = null)
        {
            Text = text;
            Carets = carets;
            CaretCount = caretCount;
            _nativeRanges = nativeRanges;
        }

        /// <summary>The number of on-page characters; carets at or past it have no page geometry.</summary>
        internal int CaretCount { get; }

        internal string Text { get; }

        /// <summary>The caret for each text offset, plus one entry for the end of the text.</summary>
        internal int[] Carets { get; }

        internal static readonly PageReadingText Empty = new(string.Empty, [0], 0);

        internal static PageReadingText Build(PageTextRuns runs)
        {
            if (runs.NativeSourceText is { } source)
            {
                var carets = new int[source.Length + 1];
                int covered = 0;
                for (int index = 0; index < runs.Chars.Count; index++)
                {
                    var character = runs.Chars[index];
                    int start = Math.Max(covered, character.SourceStart);
                    int end = character.SourceStart + character.SourceLength;
                    if (end > start)
                    {
                        Array.Fill(carets, index, start, end - start);
                        covered = end;
                    }
                }
                carets[^1] = runs.Chars.Count;
                return new PageReadingText(source, carets, runs.Chars.Count,
                    [.. runs.Chars.Select(character => (character.SourceStart, character.SourceLength))]);
            }
            return Build(runs.Chars.Select(character => (character.Value, character.Word, character.Line)).ToArray());
        }

        /// <summary>Reads the characters in the order given (the viewer's visual reading order).</summary>
        internal static PageReadingText Build(IReadOnlyList<(string Value, int Word, int Line)> chars) =>
            BuildOrdered([.. chars.Select((c, caret) => (caret, c.Value, c.Word, c.Line, 0))], chars.Count);

        /// <summary>
        /// Reads a tagged page in its structure order: characters follow the order their marked
        /// content appears in the tag tree, and anything the tree does not reach follows in visual
        /// order so no text is dropped. Each new structure item starts a new line.
        /// </summary>
        internal static PageReadingText BuildTagged(IReadOnlyList<(string Value, int Word, int Line, int? Mcid)> chars,
            IReadOnlyList<int> structureOrder)
        {
            var rank = new Dictionary<int, int>();
            foreach (int mcid in structureOrder) rank.TryAdd(mcid, rank.Count);
            var ordered = chars
                .Select((c, caret) => (Caret: caret, c.Value, c.Word, c.Line,
                    Group: c.Mcid is int id && rank.TryGetValue(id, out int r) ? r : rank.Count))
                .OrderBy(c => c.Group).ThenBy(c => c.Caret)
                .Select(c => (c.Caret, c.Value, c.Word, c.Line, c.Group))
                .ToList();
            return BuildOrdered(ordered, chars.Count);
        }

        private static PageReadingText BuildOrdered(
            IReadOnlyList<(int Caret, string Value, int Word, int Line, int Group)> chars, int caretCount)
        {
            if (chars.Count == 0) return Empty;
            var text = new StringBuilder();
            var carets = new List<int>();
            for (int i = 0; i < chars.Count; i++)
            {
                var c = chars[i];
                if (i > 0 && (c.Line != chars[i - 1].Line || c.Group != chars[i - 1].Group)) { text.Append('\n'); carets.Add(c.Caret); }
                else if (i > 0 && c.Word != chars[i - 1].Word) { text.Append(' '); carets.Add(c.Caret); }
                foreach (char ch in c.Value) { text.Append(ch); carets.Add(c.Caret); }
            }
            carets.Add(caretCount);
            return new PageReadingText(text.ToString(), [.. carets], caretCount);
        }

        /// <summary>
        /// Adds lines read after the page body, such as its comments. They have no place in the
        /// page text, so they map to the end caret and draw no outline.
        /// </summary>
        internal PageReadingText WithAppendix(IReadOnlyList<string> lines)
        {
            if (lines.Count == 0) return this;
            var text = new StringBuilder(Text);
            var carets = new List<int>(Carets[..^1]);
            foreach (string line in lines)
            {
                if (text.Length > 0) { text.Append('\n'); carets.Add(CaretCount); }
                foreach (char c in line.Replace('\n', ' ')) { text.Append(c); carets.Add(CaretCount); }
            }
            carets.Add(CaretCount);
            return new PageReadingText(text.ToString(), [.. carets], CaretCount, _nativeRanges);
        }

        /// <summary>The caret at a text offset.</summary>
        internal int CaretAt(int offset) => Carets[Math.Clamp(offset, 0, Carets.Length - 1)];

        /// <summary>The first character belonging to a visual caret.</summary>
        internal int OffsetOf(int caret)
        {
            if (_nativeRanges is { } native && caret >= 0 && caret < native.Length)
            {
                var range = native[caret];
                for (int offset = range.Start; offset < range.Start + range.Length; offset++)
                    if (!char.IsWhiteSpace(Text[offset])) return offset;
                return Text.Length;
            }
            for (int offset = 0; offset < Text.Length; offset++)
                if (Carets[offset] == caret && !char.IsWhiteSpace(Text[offset])) return offset;
            return Text.Length;
        }

        /// <summary>The reading-order envelope of a visual selection, excluding inserted separators at its edges.</summary>
        internal (int Start, int End) SelectionOffsets(int startCaret, int endCaret)
        {
            if (_nativeRanges is { } native)
            {
                int nativeStart = Text.Length, nativeEnd = -1;
                for (int caret = Math.Max(0, startCaret); caret < Math.Min(endCaret, native.Length); caret++)
                {
                    nativeStart = Math.Min(nativeStart, native[caret].Start);
                    nativeEnd = Math.Max(nativeEnd, native[caret].Start + native[caret].Length);
                }
                return nativeEnd < 0 ? (OffsetOf(startCaret), OffsetOf(startCaret)) : (nativeStart, nativeEnd);
            }
            int start = Text.Length, end = -1;
            for (int offset = 0; offset < Text.Length; offset++)
                if (Carets[offset] >= startCaret && Carets[offset] < endCaret
                    && Carets[offset] < CaretCount && !char.IsWhiteSpace(Text[offset]))
                {
                    start = Math.Min(start, offset);
                    end = offset + 1;
                }
            return end < 0 ? (OffsetOf(startCaret), OffsetOf(startCaret)) : (start, end);
        }

        /// <summary>
        /// The characters a text range covers, as contiguous caret runs [start, end). Structure
        /// order can make one range span several separate places on the page.
        /// </summary>
        internal List<(int Start, int End)> Segments(int startOffset, int endOffset)
        {
            var result = new List<(int Start, int End)>();
            startOffset = Math.Clamp(startOffset, 0, Text.Length);
            endOffset = Math.Clamp(endOffset, startOffset, Text.Length);
            if (_nativeRanges is { } native)
            {
                var letters = Enumerable.Range(startOffset, endOffset - startOffset)
                    .Where(offset => !char.IsWhiteSpace(Text[offset])).ToArray();
                for (int caret = 0; caret < native.Length; caret++)
                {
                    int first = Array.BinarySearch(letters, native[caret].Start);
                    if (first < 0) first = ~first;
                    if (first >= letters.Length || letters[first] >= native[caret].Start + native[caret].Length) continue;
                    if (result.Count > 0 && result[^1].End == caret)
                        result[^1] = (result[^1].Start, caret + 1);
                    else result.Add((caret, caret + 1));
                }
                return result;
            }
            foreach (int caret in Enumerable.Range(startOffset, endOffset - startOffset)
                         .Where(offset => !char.IsWhiteSpace(Text[offset]) && Carets[offset] < CaretCount)
                         .Select(offset => Carets[offset]).Distinct().Order())
            {
                if (result.Count > 0 && result[^1].End == caret) result[^1] = (result[^1].Start, caret + 1);
                else result.Add((caret, caret + 1));
            }
            return result;
        }
    }
}
