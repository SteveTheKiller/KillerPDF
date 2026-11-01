using System.Buffers.Binary;
using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;

namespace KillerPdf.Engine.Documents;

/// <summary>A typeface offered for scanned-page text, with an optional bold face.</summary>
public sealed record PdfScanTextFace(string Family, TrueTypeFont Regular, TrueTypeFont? Bold = null)
{
    /// <summary>
    /// Gets the extra stroke added around the letters, as a fraction of the x-height, so the
    /// text carries the same weight as heavily inked or photocopied print.
    /// </summary>
    public double Weight { get; init; }
}

/// <summary>One scanned page rewritten as a cleaned page image with text over it.</summary>
public sealed record PdfScanTextPage(
    int PageIndex, PdfScanTextPageAnalysis Analysis, PdfImage Background, PdfScanTextFace Face);

/// <summary>A rewritten document and the words it turned into text.</summary>
public sealed record PdfScanTextResult(byte[] Document, int ReplacedWords, int SearchableWords);

/// <summary>
/// Rewrites scanned pages so recognized words become real text in a matching typeface, drawn
/// over the page image with those words erased from it. Words that were not recognized
/// confidently stay in the image and are added as invisible text so they remain searchable.
/// </summary>
public static class PdfScanTextWriter
{
    /// <summary>
    /// The serif, sans serif and monospaced faces bundled with the engine, metric-compatible with
    /// Times, Arial and Courier and free to embed, for use when no installed face suits.
    /// </summary>
    public static IReadOnlyList<PdfScanTextFace> BundledFaces() =>
    [
        new("Liberation Serif", PdfStandardFontSubstitutes.Find("Times-Roman"),
            PdfStandardFontSubstitutes.Find("Times-Bold")),
        new("Liberation Sans", PdfStandardFontSubstitutes.Find("Helvetica"),
            PdfStandardFontSubstitutes.Find("Helvetica-Bold")),
        new("Liberation Mono", PdfStandardFontSubstitutes.Find("Courier"),
            PdfStandardFontSubstitutes.Find("Courier-Bold"))
    ];

    /// <summary>Whether a word can be set in the face, every character having a glyph.</summary>
    public static bool CanSet(PdfScanTextFace face, PdfScanTextWord word)
    {
        ArgumentNullException.ThrowIfNull(face);
        ArgumentNullException.ThrowIfNull(word);
        return Covers(FaceFor(face, word), word.Word.Text);
    }

    /// <summary>
    /// Replaces the content of each listed page with its background image and text. The result
    /// is a compact rewrite of the document: the replaced page images are dropped, and a typeface
    /// used on many pages is embedded once. A document that cannot be rewritten, such as a
    /// signed one, keeps the replaced content as an incremental update instead.
    /// </summary>
    public static PdfScanTextResult Write(PdfDocument document, IReadOnlyList<PdfScanTextPage> pages,
        Func<string, TrueTypeFont?> searchableFont)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(searchableFont);
        IReadOnlyList<PdfPageInformation> information = PdfPageInformation.Read(document);
        int replaced = 0, searchable = 0;

        // All the new page content is authored as one document so its fonts are shared, then
        // each page's content replaces the scanned page's own. The text stands in for print at
        // the scan's size, so its faces leave out the hinting instructions, which only tune letters
        // to a low-resolution pixel grid.
        var authored = new PdfDocumentBuilder { OmitFontHinting = true };
        foreach (PdfScanTextPage page in pages)
        {
            if (page.PageIndex < 0 || page.PageIndex >= information.Count)
                throw new ArgumentOutOfRangeException(nameof(pages), "A scanned page is outside the document.");
            PdfPageInformation geometry = information[page.PageIndex];
            bool quarter = geometry.Rotation is 90 or 270;
            double displayWidth = quarter ? geometry.Height : geometry.Width;
            double displayHeight = quarter ? geometry.Width : geometry.Height;
            double scaleX = displayWidth / page.Analysis.PixelWidth;
            double scaleY = displayHeight / page.Analysis.PixelHeight;

            var content = new PdfContentStreamBuilder().SaveState();
            ApplyDisplayTransform(content, geometry);
            content.DrawImage(page.Background, 0, displayHeight, displayWidth, -displayHeight);
            content.BeginText();
            PdfTextRenderingMode? mode = null;
            var state = new TextState();
            List<PdfScanTextWord> set = [.. page.Analysis.Words
                .Where(word => word.Replaceable && CanSet(page.Face, word))];
            Dictionary<int, double> lineScales = LineScales(page.Face, set);
            var setWords = set.ToHashSet();
            foreach (PdfScanTextWord word in page.Analysis.Words)
            {
                if (string.IsNullOrWhiteSpace(word.Word.Text)) continue;
                if (setWords.Contains(word))
                {
                    SetMode(content, ref mode, page.Face.Weight > 0
                        ? PdfTextRenderingMode.FillAndStroke : PdfTextRenderingMode.Fill);
                    SetWord(content, state, page.Face, word, scaleX, scaleY, lineScales[word.Line], space: true);
                    replaced++;
                }
                // Words kept as scanned are searchable in the page's own face when it has their
                // letters, so no other font is embedded for them.
                else if ((Covers(page.Face.Regular, word.Word.Text) ? page.Face.Regular
                    : searchableFont(word.Word.Text)) is TrueTypeFont font)
                {
                    SetMode(content, ref mode, PdfTextRenderingMode.Invisible);
                    SetInvisibleWord(content, state, font, word.Word, scaleX, scaleY);
                    searchable++;
                }
            }
            content.EndText().RestoreState();
            authored.AddPage(geometry.Width, geometry.Height, content);
        }
        PdfDocument overlay = PdfDocument.Open(authored.Build());

        var editor = new PdfIncrementalPageEditor(document);
        for (int index = 0; index < pages.Count; index++)
        {
            PdfPageInformation geometry = information[pages[index].PageIndex];
            editor.SetPageContentAndPruneResources(pages[index].PageIndex, []);
            editor.AppendImportedPageContent(pages[index].PageIndex, overlay, index,
                1, 0, 0, 1, geometry.Left, geometry.Bottom);
        }
        byte[] result = editor.Build();
        try
        {
            PdfDocument updated = PdfDocument.Open(result);
            ReadOnlyMemory<byte> compact = PdfOptimizer.CreatePlan(updated,
                PdfOptimizationOptions.ForDocument(updated)).Apply().Data;
            if (compact.Length < result.Length) result = compact.ToArray();
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or NotSupportedException or FormatException)
        {
            // The incremental update stays as written.
        }
        return new PdfScanTextResult(result, replaced, searchable);
    }

    /// <summary>
    /// Picks the face whose letters best cover the ink of a sample of the page's words, by
    /// setting the sample in each face and comparing the result with the scan pixel for pixel.
    /// </summary>
    public static PdfScanTextFace ChooseFace(PdfScanTextPageAnalysis analysis,
        IReadOnlyList<PdfScanTextFace> faces, int sampleSize = 40,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(faces);
        if (faces.Count == 0) throw new ArgumentException("At least one face is required.", nameof(faces));
        if (faces.Count == 1) return faces[0];
        IReadOnlyList<(PdfScanTextFace Face, double Score)> ranked =
            RankFaces(analysis, faces, sampleSize, cancellationToken);
        return ranked.Count == 0 ? faces[0] : ranked[0].Face;
    }

    /// <summary>Scores each face against a sample of the page's words, best first.</summary>
    public static IReadOnlyList<(PdfScanTextFace Face, double Score)> RankFaces(
        PdfScanTextPageAnalysis analysis, IReadOnlyList<PdfScanTextFace> faces, int sampleSize = 40,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(faces);

        List<PdfScanTextWord> candidates = [.. analysis.Words.Where(word => word.Replaceable && !word.Bold
            && word.Word.Text.Count(char.IsLetter) >= 3)];
        if (candidates.Count == 0) return [];
        int step = Math.Max(1, candidates.Count / sampleSize);
        List<PdfScanTextWord> sample = [.. candidates.Where((_, index) => index % step == 0).Take(sampleSize)];

        var scores = new List<(PdfScanTextFace Face, double Score)>(faces.Count);
        foreach (PdfScanTextFace face in faces)
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<PdfScanTextWord> settable = [.. sample.Where(word => Covers(face.Regular, word.Word.Text))];
            if (settable.Count < sample.Count * 0.9) continue;
            scores.Add(Score(analysis, face, settable));
        }
        return [.. scores.OrderByDescending(item => item.Score)];
    }

    /// <summary>
    /// Sets each replaceable word in the face and compares it with the scanned ink, keeping in
    /// the page image any word whose letters do not match it closely, such as a misread word.
    /// </summary>
    public static int Verify(PdfScanTextPageAnalysis analysis, PdfScanTextFace face,
        double minimumMatch = 0.75, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(face);
        List<PdfScanTextWord> words = [.. analysis.Words.Where(word => word.Replaceable && CanSet(face, word))];
        if (words.Count == 0) return 0;
        cancellationToken.ThrowIfCancellationRequested();
        double[] matches = MatchWords(analysis, face, words, out _, out _);
        double typical = matches.Order().ElementAt(matches.Length / 2);

        // Each reading is also set with single swaps of letters a recognizer often confuses.
        // When a swap fits the ink clearly better than what was recognized, the recognized text
        // is probably wrong, and the word stays as it was scanned. Near-identical pairs such as
        // l and I differ by less than the margin, so they do not reject a correct reading. A swap
        // between letters that look plainly different, such as T and l, that fits the ink as well
        // as a reading the recognizer itself was unsure of means neither reading is supported, and
        // the word stays as scanned too. One letter counts for less in a longer word, so that tie
        // margin shrinks with the word's length.
        var probes = new List<PdfScanTextWord>(words.Count * 2);
        var owners = new List<int>(words.Count * 2);
        var distinct = new List<bool>(words.Count * 2);
        var ownProbe = new int[words.Count];
        for (int i = 0; i < words.Count; i++)
        {
            ownProbe[i] = probes.Count;
            probes.Add(words[i]);
            owners.Add(i);
            distinct.Add(false);
            foreach ((string variant, bool plain) in Variants(words[i].Word.Text).Take(6))
            {
                PdfScanTextWord probe = words[i].WithText(variant);
                if (!CanSet(face, probe)) continue;
                probes.Add(probe);
                owners.Add(i);
                distinct.Add(plain);
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        // Each swap is also judged where it happens: the letters around the swapped position,
        // set both ways, are compared with the ink there. A whole-word score dilutes one wrong
        // letter among the right ones, which let t and f, i and l, or rn and m pass on a face
        // that matches the print only roughly.
        var windows = new SwapSpan[probes.Count];
        for (int k = 0; k < probes.Count; k++)
            windows[k] = k == ownProbe[owners[k]] ? SwapSpan.None
                : SwapWindow(face, words[owners[k]], probes[k], ownProbe[owners[k]]);
        double[] shapes = MatchWords(analysis, face, probes, out _, out _, strict: true,
            windows, out double[] localGain);
        double[] own = new double[words.Count], rival = new double[words.Count];
        double[] distinctRival = new double[words.Count];
        string?[] distinctText = new string?[words.Count];
        double[] localRival = new double[words.Count];
        string?[] localText = new string?[words.Count];
        for (int k = 0; k < probes.Count; k++)
        {
            PdfScanTextWord owner = words[owners[k]];
            if (ReferenceEquals(probes[k], owner)) { own[owners[k]] = shapes[k]; continue; }
            if (localGain[k] > localRival[owners[k]])
            {
                localRival[owners[k]] = localGain[k];
                localText[owners[k]] = probes[k].Word.Text;
            }
            if (distinct[k] && shapes[k] > distinctRival[owners[k]])
            {
                distinctRival[owners[k]] = shapes[k];
                distinctText[owners[k]] = probes[k].Word.Text;
            }
            if (shapes[k] > rival[owners[k]])
            {
                rival[owners[k]] = shapes[k];
                owner.Lookalike = probes[k].Word.Text;
                owner.LookalikeMatch = shapes[k];
            }
        }
        for (int i = 0; i < words.Count; i++) words[i].ShapeMatch = own[i];
        // Letter shapes that fit clearly worse than the page's usual words point to a misread
        // even when no single lookalike swap explains it.
        double typicalShape = own.Order().ElementAt(own.Length / 2);
        float typicalConfidence = words.Select(word => word.Word.Confidence).Order().ElementAt(words.Count / 2);

        int rejected = 0;
        for (int i = 0; i < words.Count; i++)
        {
            words[i].Match = matches[i];
            bool tied = words[i].Word.Confidence < typicalConfidence - 0.05
                && distinctRival[i] >= own[i] - 0.01 / Math.Max(1, words[i].Word.Text.Count(char.IsLetterOrDigit));
            // At the swapped letters themselves, a lookalike that covers the ink clearly better
            // than the recognized letters means the recognizer most likely misread them.
            bool locallyBeaten = localRival[i] > LocalSwapMargin;
            if (matches[i] >= minimumMatch && matches[i] >= typical - 0.15
                && own[i] >= typicalShape - 0.12 && rival[i] <= own[i] + 0.05 && !tied && !locallyBeaten)
                continue;
            if (locallyBeaten && rival[i] <= own[i] + 0.05 && !tied)
            {
                words[i].Lookalike = localText[i];
                words[i].LookalikeMatch = own[i] + localRival[i];
            }
            if (tied && rival[i] <= own[i] + 0.05)
            {
                words[i].Lookalike = distinctText[i];
                words[i].LookalikeMatch = distinctRival[i];
            }
            words[i].Replaceable = false;
            rejected++;
        }
        return rejected;
    }

    // How much better, as an F1 score over the swapped letters, a lookalike must cover the ink
    // there before the recognized reading is doubted. Correct readings stay well below it even
    // on noisy scans, where the letters' edges wander by a pixel or two.
    private const double LocalSwapMargin = 0.04;

    // The columns, in the word's mask, of the letters a swap changes, as the lookalike sets them
    // and as the recognized reading (the probe Against) sets its own letters there, each widened
    // by a little of the x-height so the letters' edges count. The two readings place the letters
    // after a swap differently when their widths differ, so each may slide a quarter of the
    // x-height to meet the ink.
    private static SwapSpan SwapWindow(PdfScanTextFace face,
        PdfScanTextWord word, PdfScanTextWord probe, int against)
    {
        string a = word.Word.Text, b = probe.Word.Text;
        int prefix = 0;
        while (prefix < a.Length && prefix < b.Length && a[prefix] == b[prefix]) prefix++;
        int suffix = 0;
        while (suffix < a.Length - prefix && suffix < b.Length - prefix
               && a[a.Length - 1 - suffix] == b[b.Length - 1 - suffix]) suffix++;
        (double aFrom, double aTo) = LetterSpan(face, word, a, prefix, a.Length - suffix);
        (double bFrom, double bTo) = LetterSpan(face, probe, b, prefix, b.Length - suffix);
        double margin = Math.Max(1, word.XHeight * 0.08);
        int Lo(double x) => Math.Clamp((int)Math.Floor(x - margin), 0, word.MaskWidth);
        int Hi(double x) => Math.Clamp((int)Math.Ceiling(x + margin), 0, word.MaskWidth);
        int reach = Math.Max(1, (int)Math.Round(word.XHeight * 0.25));
        return Hi(bTo) - Lo(bFrom) >= 2 && Hi(aTo) - Lo(aFrom) >= 2
            ? new SwapSpan(Lo(bFrom), Hi(bTo), against, Lo(aFrom), Hi(aTo), reach)
            : SwapSpan.None;
    }

    // The columns of a reading's swapped letters, the reading it is compared against and that
    // reading's columns for the letters it has there, and how far either may slide to meet the ink.
    private readonly record struct SwapSpan(int From, int To, int Against, int AgainstFrom, int AgainstTo,
        int Reach)
    {
        public static SwapSpan None => new(0, 0, -1, 0, 0, 0);
    }

    // Where the letters [first, last) of a word's text fall, in mask columns, as SetWord sets them
    // on its own (the line's shared scale is close to the word's own fit, so this is near enough).
    private static (double From, double To) LetterSpan(PdfScanTextFace face, PdfScanTextWord word,
        string text, int first, int last)
    {
        TrueTypeFont font = FaceFor(face, word);
        double size = PixelSize(font, word);
        double width = InkSpan(font, word.FitText ?? text).Ink * size / 1000;
        double scale = width > 0 ? Math.Clamp((word.Right - word.Left) / width, 0.5, 2) : 1;
        double unit = size / 1000 * scale;
        double origin = word.Left - word.MaskLeft - InkSpan(font, text).Lead * unit;
        return (origin + Advance(font, text[..first]) * unit, origin + Advance(font, text[..last]) * unit);
    }

    // Letters and pairs a recognizer commonly takes for one another, most telling first, and
    // whether the two look plainly different in print rather than nearly the same.
    private static readonly (string A, string B, bool Distinct)[] Confusions =
    [
        ("E", "F", true), ("I", "l", false), ("I", "1", false), ("l", "1", false), ("I", "[", false),
        ("l", "T", true), ("I", "T", true), ("O", "0", false), ("m", "rn", true), ("d", "cl", true),
        ("h", "li", true), ("w", "vv", true), ("S", "5", true), ("B", "8", true), ("c", "e", true),
        ("n", "h", true), ("i", "l", false), ("f", "t", false), ("C", "G", true), ("p", "v", true),
        ("u", "n", true)
    ];

    private static IEnumerable<(string Text, bool Distinct)> Variants(string text)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { text };
        foreach ((string a, string b, bool plain) in Confusions)
            foreach ((string from, string to) in new[] { (a, b), (b, a) })
                for (int at = text.IndexOf(from, StringComparison.Ordinal); at >= 0;
                     at = text.IndexOf(from, at + 1, StringComparison.Ordinal))
                {
                    string variant = string.Concat(text.AsSpan(0, at), to, text.AsSpan(at + from.Length));
                    if (seen.Add(variant)) yield return (variant, plain);
                }
    }

    private static (PdfScanTextFace Face, double Score) Score(PdfScanTextPageAnalysis analysis,
        PdfScanTextFace face, List<PdfScanTextWord> words)
    {
        PdfScanTextFace regular = face with { Bold = null, Weight = 0 };
        double[] matches = MatchWords(analysis, regular, words,
            out Dictionary<int, double> lineScales, out double[] strokes, strict: true);
        // Heavy print is matched by thickening the face to the scan's stroke weight before the
        // shapes are compared, so a bold-looking scan does not simply favor a heavy face.
        double extra = Median(words.Select((word, i) => word.StrokeWidth - strokes[i]));
        double xHeight = Median(words.Select(word => word.XHeight));
        if (extra > 0.5 && xHeight > 0)
        {
            regular = regular with { Weight = Math.Min(0.15, extra / xHeight) };
            matches = MatchWords(analysis, regular, words, out lineScales, out _, strict: true);
        }
        // A face that has to be squeezed or stretched far to fit the scan is the wrong design
        // even when its letters happen to overlap the ink.
        double distortion = lineScales.Values.Average(scale => Math.Abs(Math.Log(scale / 100)));
        // The text itself is set at the face's own weight. A scan's extra stroke is mostly ink
        // spread by the scanner or copier, and drawing it back in made clean print look bold.
        return (face with { Weight = 0 }, matches.Average() - 0.5 * distortion);
    }

    private static double Median(IEnumerable<double> values)
    {
        double[] sorted = [.. values.Order()];
        return sorted.Length == 0 ? 0 : sorted[sorted.Length / 2];
    }

    // Renders the words as they would be written and measures, per word, the overlap of the
    // rendered letters with the scanned ink as intersection over union.
    private static double[] MatchWords(PdfScanTextPageAnalysis analysis, PdfScanTextFace face,
        List<PdfScanTextWord> words, out Dictionary<int, double> lineScales, out double[] strokes,
        bool strict = false) =>
        MatchWords(analysis, face, words, out lineScales, out strokes, strict, null, out _);

    // windows, when given, holds for each word the columns of its swapped letters and the word
    // whose setting it is compared against there; localGain receives how much better, as an F1
    // score over those letters, the word covers the scanned ink than that other word.
    private static double[] MatchWords(PdfScanTextPageAnalysis analysis, PdfScanTextFace face,
        List<PdfScanTextWord> words, out Dictionary<int, double> lineScales, out double[] strokes,
        bool strict, SwapSpan[]? windows, out double[] localGain)
    {
        // The words are packed side by side onto a small canvas, each with a margin, instead of
        // rendering the page area they span.
        int width = Math.Max(words.Max(word => word.MaskWidth), Math.Min(analysis.PixelWidth, 2048));
        var places = new (int X, int Y)[words.Count];
        int cursorX = 0, cursorY = 0, shelf = 0;
        for (int i = 0; i < words.Count; i++)
        {
            PdfScanTextWord word = words[i];
            int gap = Math.Max(4, word.MaskHeight / 2);
            if (cursorX > 0 && cursorX + word.MaskWidth > width)
            {
                cursorX = 0;
                cursorY += shelf;
                shelf = 0;
            }
            places[i] = (cursorX, cursorY);
            cursorX += word.MaskWidth + gap;
            shelf = Math.Max(shelf, word.MaskHeight + gap);
        }
        int height = Math.Max(1, cursorY + shelf);
        lineScales = LineScales(face, words);
        var content = new PdfContentStreamBuilder().Transform(1, 0, 0, -1, 0, height).BeginText();
        if (face.Weight > 0) content.SetTextRenderingMode(PdfTextRenderingMode.FillAndStroke);
        var state = new TextState();
        for (int i = 0; i < words.Count; i++)
        {
            PdfScanTextWord word = words[i];
            SetWord(content, state, face, word, 1, 1, lineScales[word.Line], space: false,
                offsetX: word.MaskLeft - places[i].X, offsetY: word.MaskTop - places[i].Y,
                inkColor: false);
        }
        content.EndText();
        byte[] pdf = new PdfDocumentBuilder().AddPage(width, height, content).Build();
        PdfRenderedPage rendered = new PdfPageRenderer(PdfDocument.Open(pdf))
            .Render(0, new PdfRenderOptions(width, height) { CacheResult = false });
        ReadOnlySpan<byte> pixels = rendered.Pixels.Span;

        // Overlap is judged with a tolerance of about a stroke width, so a slightly lighter or
        // heavier face still matches while a wrong or misplaced letter does not.
        double[] matches = new double[words.Count];
        strokes = new double[words.Count];
        localGain = new double[words.Count];
        bool[]?[] settings = windows is null ? [] : new bool[]?[words.Count];
        for (int i = 0; i < words.Count; i++)
        {
            PdfScanTextWord word = words[i];
            int w = word.MaskWidth, h = word.MaskHeight;
            bool[] set = new bool[w * h];
            if (windows is not null) settings[i] = set;
            for (int y = 0; y < h; y++)
            {
                int py = places[i].Y + y;
                if ((uint)py >= (uint)height) continue;
                for (int x = 0; x < w; x++)
                {
                    int px = places[i].X + x;
                    if ((uint)px >= (uint)width) continue;
                    set[y * w + x] = pixels[(py * width + px) * 4 + 1] < 128;
                }
            }
            // Choosing a face needs its letter shapes, so that comparison allows only a pixel.
            int tolerance = strict ? 1 : Math.Max(1, (int)Math.Round(word.XHeight / 10));
            bool[] nearScan = PdfScanTextAnalyzer.Grow(word.Mask, w, h, tolerance);
            bool[] nearSet = PdfScanTextAnalyzer.Grow(set, w, h, tolerance);
            int setCount = 0, setHits = 0, scanCount = 0, scanHits = 0;
            for (int k = 0; k < set.Length; k++)
            {
                if (set[k]) { setCount++; if (nearScan[k]) setHits++; }
                if (word.Mask[k]) { scanCount++; if (nearSet[k]) scanHits++; }
            }
            double precision = Ratio(setHits, setCount);
            double recall = Ratio(scanHits, scanCount);
            if (strict)
                matches[i] = precision + recall > 0 ? 2 * precision * recall / (precision + recall) : 0;
            else
                // The weaker side decides: a letter missing from the setting, or one the scan
                // does not have, is a misread even when the rest of the word lines up. Letter by
                // letter, scanned ink with no set letter anywhere near it marks a wrong letter
                // that the whole-word count would average away.
                matches[i] = Math.Min(Math.Min(precision, recall), UnexplainedInk(word, set, w, h));
            strokes[i] = StrokeWidth(set, w, h,
                (int)Math.Round(word.Baseline - word.XHeight) - word.MaskTop,
                (int)Math.Round(word.Baseline) - 1 - word.MaskTop);
        }
        if (windows is not null)
        {
            var nearScans = new Dictionary<bool[], bool[]>(ReferenceEqualityComparer.Instance);
            for (int i = 0; i < words.Count; i++)
            {
                SwapSpan span = windows[i];
                if (span.Against < 0 || settings[i] is not bool[] mine || settings[span.Against] is not bool[] theirs) continue;
                PdfScanTextWord word = words[i];
                if (!nearScans.TryGetValue(word.Mask, out bool[]? nearScan))
                    nearScans[word.Mask] = nearScan = PdfScanTextAnalyzer.Grow(word.Mask, word.MaskWidth, word.MaskHeight, 1);
                localGain[i] = LocalCover(word, nearScan, mine, span.From, span.To, span.Reach)
                    - LocalCover(word, nearScan, theirs, span.AgainstFrom, span.AgainstTo, span.Reach);
            }
        }
        return matches;
    }

    // The best F1 of a setting's mask columns [from, to) against the word's scanned ink, the
    // setting slid up to reach pixels either way, each side allowed a pixel as the face choice is.
    private static double LocalCover(PdfScanTextWord word, bool[] nearScan, bool[] set, int from, int to, int reach)
    {
        int w = word.MaskWidth, h = word.MaskHeight;
        bool[] nearSet = PdfScanTextAnalyzer.Grow(set, w, h, 1);
        double best = 0;
        for (int shift = -reach; shift <= reach; shift++)
        {
            int setCount = 0, setHits = 0, scanCount = 0, scanHits = 0;
            for (int y = 0; y < h; y++)
                for (int x = from; x < to; x++)
                {
                    int sx = x + shift;
                    if ((uint)sx >= (uint)w) continue;
                    int k = y * w + x, s = y * w + sx;
                    if (set[k]) { setCount++; if (nearScan[s]) setHits++; }
                    if (word.Mask[s]) { scanCount++; if (nearSet[k]) scanHits++; }
                }
            double precision = Ratio(setHits, setCount), recall = Ratio(scanHits, scanCount);
            if (precision + recall > 0) best = Math.Max(best, 2 * precision * recall / (precision + recall));
        }
        return best;
    }

    private static double Ratio(int part, int whole) => whole == 0 ? 0 : part / (double)whole;

    // The lowest share, over letter-wide columns, of the scanned ink that lies within a quarter
    // of the x-height of the set letters. Different typefaces stay well inside that distance;
    // a letter that is not there at all does not.
    private static double UnexplainedInk(PdfScanTextWord word, bool[] set, int w, int h)
    {
        int reach = Math.Max(2, (int)Math.Round(word.XHeight / 4));
        bool[] near = PdfScanTextAnalyzer.Grow(set, w, h, reach);
        int columnWidth = Math.Max(2, (int)Math.Round(word.XHeight * 1.5));
        int columns = (w + columnWidth - 1) / columnWidth;
        int[] ink = new int[columns], explained = new int[columns];
        for (int k = 0; k < set.Length; k++)
        {
            if (!word.Mask[k]) continue;
            int column = k % w / columnWidth;
            ink[column]++;
            if (near[k]) explained[column]++;
        }
        int busy = Math.Max(1, ink.Sum() / Math.Max(1, columns) / 3);
        double lowest = 1;
        for (int column = 0; column < columns; column++)
            if (ink[column] >= busy) lowest = Math.Min(lowest, Ratio(explained[column], ink[column]));
        return lowest;
    }

    // Mean horizontal run of ink across the x-height band.
    private static double StrokeWidth(bool[] mask, int w, int h, int bandTop, int bandBottom)
    {
        long runs = 0, length = 0;
        for (int y = Math.Max(0, bandTop); y <= Math.Min(h - 1, bandBottom); y++)
        {
            int run = 0;
            for (int x = 0; x <= w; x++)
            {
                if (x < w && mask[y * w + x]) { run++; continue; }
                if (run > 0) { runs++; length += run; run = 0; }
            }
        }
        return runs == 0 ? 0 : length / (double)runs;
    }

    // One horizontal scale per line, so every word on it is set at the same width.
    private static Dictionary<int, double> LineScales(PdfScanTextFace face, IEnumerable<PdfScanTextWord> words)
    {
        var scales = new Dictionary<int, double>();
        foreach (IGrouping<int, PdfScanTextWord> line in words.GroupBy(word => word.Line))
        {
            double target = 0, natural = 0;
            foreach (PdfScanTextWord word in line)
            {
                TrueTypeFont font = FaceFor(face, word);
                double size = PixelSize(font, word);
                target += word.Right - word.Left;
                natural += InkSpan(font, word.FitText ?? word.Word.Text).Ink * size / 1000;
            }
            scales[line.Key] = natural > 0 ? Math.Clamp(target / natural * 100, 50, 200) : 100;
        }
        return scales;
    }

    private static void SetWord(PdfContentStreamBuilder content, TextState state, PdfScanTextFace face,
        PdfScanTextWord word, double scaleX, double scaleY, double lineScale, bool space,
        double offsetX = 0, double offsetY = 0, bool inkColor = true)
    {
        TrueTypeFont font = FaceFor(face, word);
        // The scan's x-height runs tall by the ink spread on the letters' tops and bottoms, while
        // its word widths hardly move, so the line's width fit sets the size, within a band, and
        // the letters keep their own proportions instead of being set tall and squeezed.
        double fit = Math.Clamp(lineScale / 100, 0.85, 1.05);
        double size = PixelSize(font, word) * scaleY * fit;
        lineScale /= fit;
        // The scan's word box runs from the first letter's ink to the last one's, so the fit is
        // against the setting's ink too, without the side bearings outside it.
        double inkWidth = InkSpan(font, word.FitText ?? word.Word.Text).Ink * size / 1000;
        double target = (word.Right - word.Left) * scaleX;
        double own = inkWidth > 0 ? Math.Clamp(target / inkWidth * 100, 50, 200) : 100;
        // The line's scale keeps the words uniform; staying near the word's own fit keeps a
        // word from running into its neighbor when the line mixes wide and narrow words.
        double scale = Math.Clamp(lineScale, own * 0.9, own * 1.1);
        (byte red, byte green, byte blue) = inkColor ? word.InkColor : ((byte)0, (byte)0, (byte)0);
        // A slightly skewed scan keeps its lines' slant; the text runs along each line.
        double angle = Math.Atan(word.Slope * scaleY / scaleX);
        double cos = Math.Cos(angle), sin = Math.Sin(angle);
        int color = red << 16 | green << 8 | blue;
        if (state.Fill != color)
        {
            content.SetFillRgb(red / 255.0, green / 255.0, blue / 255.0);
            state.Fill = color;
        }
        if (face.Weight > 0)
        {
            if (state.Stroke != color)
            {
                content.SetStrokeRgb(red / 255.0, green / 255.0, blue / 255.0);
                state.Stroke = color;
            }
            double lineWidth = Round(face.Weight * word.XHeight * scaleY, 2);
            if (lineWidth != state.LineWidth)
            {
                content.SetLineWidth(lineWidth);
                state.LineWidth = lineWidth;
            }
        }
        SetFontAndScale(content, state, font, size, scale);
        // The first letter's ink starts its left side bearing after the text origin, so the
        // origin steps back by that much along the line to put the ink where the scan's begins.
        double lead = InkSpan(font, word.Word.Text).Lead * size / 1000 * scale / 100;
        content.SetTextMatrix(Round(cos, 5), Round(sin, 5), Round(sin, 5), Round(-cos, 5),
                Round((word.Left - offsetX) * scaleX - lead * cos, 3),
                Round((word.Baseline - offsetY) * scaleY - lead * sin, 3))
            .ShowUnicodeText(space ? word.Word.Text + " " : word.Word.Text);
    }

    private static void SetInvisibleWord(PdfContentStreamBuilder content, TextState state, TrueTypeFont font,
        PdfOcrPixelWord word, double scaleX, double scaleY)
    {
        double height = (word.Bottom - (double)word.Top) * scaleY;
        double width = (word.Right - (double)word.Left) * scaleX;
        double natural = Advance(font, word.Text) * height / 1000;
        double scale = natural > 0 ? Math.Clamp(width / natural * 100, 10, 1000) : 100;
        SetFontAndScale(content, state, font, height, scale);
        content.SetTextMatrix(1, 0, 0, -1, Round(word.Left * scaleX, 3), Round(word.Top * scaleY + height * 0.8, 3))
            .ShowUnicodeText(word.Text + " ");
    }

    // The text state last written to a page's content, so each word writes only what changes.
    private sealed class TextState
    {
        public int Fill = -1, Stroke = -1;
        public double LineWidth = double.NaN, Size = double.NaN, Scale = double.NaN;
        public TrueTypeFont? Font;
    }

    private static void SetFontAndScale(PdfContentStreamBuilder content, TextState state,
        TrueTypeFont font, double size, double scale)
    {
        size = Round(size, 3);
        scale = Round(scale, 2);
        if (!ReferenceEquals(font, state.Font) || size != state.Size)
        {
            content.SetFont(font, size);
            state.Font = font;
            state.Size = size;
        }
        if (scale != state.Scale)
        {
            content.SetHorizontalTextScale(scale);
            state.Scale = scale;
        }
    }

    // Rounds a content value to the precision a page needs, without a negative zero.
    private static double Round(double value, int digits) => Math.Round(value, digits) + 0.0;

    private static void SetMode(PdfContentStreamBuilder content, ref PdfTextRenderingMode? current,
        PdfTextRenderingMode mode)
    {
        if (current == mode) return;
        content.SetTextRenderingMode(mode);
        current = mode;
    }

    private static TrueTypeFont FaceFor(PdfScanTextFace face, PdfScanTextWord word) =>
        word.Bold && face.Bold is not null && Covers(face.Bold, word.Word.Text) ? face.Bold : face.Regular;

    private static double Advance(TrueTypeFont font, string text) =>
        text.EnumerateRunes().Sum(rune => (double)font.GetPdfAdvanceWidth(font.GetGlyphId(rune.Value)));

    // Where a word's ink starts after its text origin and how wide the ink is, in 1,000 PDF text
    // units: the advance without the first letter's left side bearing and the last letter's right
    // one. A face whose outlines are not measured here counts its whole advance as ink.
    private static (double Lead, double Ink) InkSpan(TrueTypeFont font, string text)
    {
        double advance = Advance(font, text);
        Rune[] runes = [.. text.EnumerateRunes()];
        if (runes.Length == 0) return (0, advance);
        double unit = 1000d / font.UnitsPerEm;
        ushort first = font.GetGlyphId(runes[0].Value), last = font.GetGlyphId(runes[^1].Value);
        if (!font.TryGetGlyphHorizontalExtent(first, out int firstMin, out _)
            || !font.TryGetGlyphHorizontalExtent(last, out _, out int lastMax))
            return (0, advance);
        double lead = firstMin * unit;
        double trail = font.GetPdfAdvanceWidth(last) - lastMax * unit;
        double ink = advance - lead - trail;
        return ink > 0 ? (lead, ink) : (0, advance);
    }

    private static bool Covers(TrueTypeFont font, string text) =>
        text.EnumerateRunes().All(rune => Rune.IsWhiteSpace(rune) || font.GetGlyphId(rune.Value) != 0);

    // The font size, in pixels, that gives the face the word's measured letter height.
    private static double PixelSize(TrueTypeFont font, PdfScanTextWord word) =>
        word.CapitalsOnly ? word.CapHeight / CapHeightRatio(font) : word.XHeight / XHeightRatio(font);

    /// <summary>The face's x-height as a fraction of its em, from its OS/2 table when present.</summary>
    /// <summary>The point size at which a face matches a line's measured letter height.</summary>
    public static double FontSizeFor(PdfScanTextFace face, double xHeightPixels, double capHeightPixels,
        bool capitalsOnly, double pointsPerPixel)
    {
        ArgumentNullException.ThrowIfNull(face);
        return (capitalsOnly ? capHeightPixels / CapHeightRatio(face.Regular)
            : xHeightPixels / XHeightRatio(face.Regular)) * pointsPerPixel;
    }

    internal static double XHeightRatio(TrueTypeFont font) => Os2Ratio(font, 86, 0.5, 0.3, 0.7);

    /// <summary>The face's capital height as a fraction of its em, from its OS/2 table when present.</summary>
    internal static double CapHeightRatio(TrueTypeFont font) => Os2Ratio(font, 88, 0.7, 0.5, 0.9);

    private static double Os2Ratio(TrueTypeFont font, int field, double fallback, double low, double high)
    {
        ReadOnlySpan<byte> data = font.FontData.Span;
        if (data.Length >= 12)
        {
            int tables = BinaryPrimitives.ReadUInt16BigEndian(data[4..]);
            for (int i = 0; i < tables && 12 + i * 16 + 16 <= data.Length; i++)
            {
                ReadOnlySpan<byte> record = data.Slice(12 + i * 16, 16);
                if (!record[..4].SequenceEqual("OS/2"u8)) continue;
                int offset = (int)BinaryPrimitives.ReadUInt32BigEndian(record[8..]);
                if (offset < 0 || offset + 90 > data.Length) break;
                ReadOnlySpan<byte> os2 = data[offset..];
                int version = BinaryPrimitives.ReadUInt16BigEndian(os2);
                short value = BinaryPrimitives.ReadInt16BigEndian(os2[field..]);
                if (version >= 2 && value > 0 && font.UnitsPerEm > 0)
                    return Math.Clamp(value / (double)font.UnitsPerEm, low, high);
                break;
            }
        }
        return fallback;
    }

    private static void ApplyDisplayTransform(PdfContentStreamBuilder content, PdfPageInformation page)
    {
        switch (page.Rotation)
        {
            case 0: content.Transform(1, 0, 0, -1, 0, page.Height); break;
            case 90: content.Transform(0, 1, 1, 0, 0, 0); break;
            case 180: content.Transform(-1, 0, 0, 1, page.Width, 0); break;
            case 270: content.Transform(0, -1, -1, 0, page.Width, page.Height); break;
            default: throw new InvalidOperationException("The PDF page rotation is unsupported.");
        }
    }
}
