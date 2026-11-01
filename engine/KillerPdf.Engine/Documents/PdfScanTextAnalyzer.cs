using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Parsing;

namespace KillerPdf.Engine.Documents;

/// <summary>Settings that decide which recognized words on a scanned page are replaced with text.</summary>
public sealed record PdfScanTextOptions
{
    /// <summary>Gets the lowest recognition confidence, from 0 to 1, of a word that is replaced.</summary>
    public float MinimumConfidence { get; init; } = 0.8f;
    /// <summary>Gets the smallest gray-level difference between a word's ink and its background.</summary>
    public int MinimumContrast { get; init; } = 60;
    /// <summary>Gets the smallest x-height, in pixels, of a line whose words are replaced.</summary>
    public double MinimumXHeight { get; init; } = 5;
}

/// <summary>The measured appearance of one recognized word on a scanned page.</summary>
public sealed class PdfScanTextWord
{
    internal PdfScanTextWord(PdfOcrPixelWord word, int left, int top, int right, int bottom)
    {
        Word = word;
        Left = left;
        Top = top;
        Right = right;
        Bottom = bottom;
    }

    /// <summary>Gets the recognized word.</summary>
    public PdfOcrPixelWord Word { get; }
    /// <summary>Gets the left edge of the word's ink in pixels.</summary>
    public int Left { get; internal set; }
    /// <summary>Gets the top edge of the word's ink in pixels.</summary>
    public int Top { get; internal set; }
    /// <summary>Gets the right edge, exclusive, of the word's ink in pixels.</summary>
    public int Right { get; internal set; }
    /// <summary>Gets the bottom edge, exclusive, of the word's ink in pixels.</summary>
    public int Bottom { get; internal set; }
    /// <summary>Gets whether the word has clear dark ink on a lighter background.</summary>
    public bool HasClearInk { get; internal set; }
    /// <summary>Gets whether the word is confident and clear enough to replace with text.</summary>
    public bool Replaceable { get; internal set; }
    /// <summary>Gets the word's ink color as red, green and blue.</summary>
    public (byte Red, byte Green, byte Blue) InkColor { get; internal set; }
    /// <summary>Gets the color around the word's ink as red, green and blue.</summary>
    public (byte Red, byte Green, byte Blue) BackgroundColor { get; internal set; }
    /// <summary>Gets the baseline at the word's left edge, in pixels from the top of the page.</summary>
    public double Baseline { get; internal set; }
    /// <summary>Gets the slope of the word's line, in pixels down per pixel across.</summary>
    public double Slope { get; internal set; }
    /// <summary>Gets the x-height of the word's line in pixels.</summary>
    public double XHeight { get; internal set; }
    /// <summary>
    /// Gets whether the word's line has no lowercase letters, so its letters are sized by
    /// <see cref="CapHeight"/> rather than by the x-height.
    /// </summary>
    public bool CapitalsOnly { get; internal set; }
    /// <summary>Gets the height of the line's capitals and figures in pixels, when it has only those.</summary>
    public double CapHeight { get; internal set; }
    /// <summary>Gets the typical stroke thickness of the word's ink in pixels.</summary>
    public double StrokeWidth { get; internal set; }
    /// <summary>Gets whether the word's strokes are heavier than the page's usual text.</summary>
    public bool Bold { get; internal set; }
    /// <summary>Gets the index of the text line the word belongs to.</summary>
    public int Line { get; internal set; }
    /// <summary>
    /// Gets how closely the word set in the chosen face covers the scanned ink, from 0 to 1,
    /// once the page has been checked.
    /// </summary>
    public double Match { get; internal set; } = double.NaN;
    /// <summary>Gets how closely the recognized letters' shapes fit the ink, once the page has been checked.</summary>
    public double ShapeMatch { get; internal set; } = double.NaN;
    /// <summary>Gets the lookalike reading that fit the ink best, if any was tried.</summary>
    public string? Lookalike { get; internal set; }
    /// <summary>Gets how closely the best lookalike reading's shapes fit the ink.</summary>
    public double LookalikeMatch { get; internal set; } = double.NaN;

    /// <summary>Keeps the word in the page image, under invisible text, instead of setting it as text.</summary>
    public void KeepScanned() => Replaceable = false;

    // Box-local ink mask over [MaskLeft, MaskLeft + MaskWidth) x [MaskTop, MaskTop + MaskHeight).
    internal int MaskLeft { get; set; }
    internal int MaskTop { get; set; }
    internal int MaskWidth { get; set; }
    internal int MaskHeight { get; set; }
    internal bool[] Mask { get; set; } = [];
    internal double RawBaseline { get; set; }
    internal double RawXHeight { get; set; }
    internal bool HasLowercaseBody { get; set; }
    // Bounding boxes of the word's separate ink shapes, mostly single letters, in page pixels.
    internal List<(int Left, int Top, int Right, int Bottom)> Glyphs { get; set; } = [];

    // Text whose width sets the word's horizontal scale, when it differs from the word's text.
    internal string? FitText { get; set; }

    // The same measured word read as different text, for comparing readings against the ink.
    // It keeps the original reading's width, so a reading with wider or narrower letters shows
    // up as a worse fit instead of being squeezed into place.
    internal PdfScanTextWord WithText(string text) => new(Word with { Text = text }, Left, Top, Right, Bottom)
    {
        FitText = FitText ?? Word.Text,
        HasClearInk = HasClearInk, Replaceable = Replaceable, InkColor = InkColor,
        BackgroundColor = BackgroundColor, Baseline = Baseline, Slope = Slope, XHeight = XHeight,
        CapitalsOnly = CapitalsOnly, CapHeight = CapHeight, StrokeWidth = StrokeWidth, Bold = Bold,
        Line = Line, MaskLeft = MaskLeft, MaskTop = MaskTop, MaskWidth = MaskWidth,
        MaskHeight = MaskHeight, Mask = Mask, RawBaseline = RawBaseline, RawXHeight = RawXHeight,
        HasLowercaseBody = HasLowercaseBody, Glyphs = Glyphs
    };
}

/// <summary>The typical look of one scanned line of text.</summary>
public sealed record PdfScanLineLook(
    (byte Red, byte Green, byte Blue) BackgroundColor, (byte Red, byte Green, byte Blue) InkColor,
    double XHeight, double CapHeight, bool CapitalsOnly, double Baseline, bool Bold,
    IReadOnlyList<PdfScanTextWord> Words);

/// <summary>The measured words of one scanned page.</summary>
public sealed record PdfScanTextPageAnalysis(
    int PixelWidth, int PixelHeight, bool Bitonal, IReadOnlyList<PdfScanTextWord> Words);

/// <summary>
/// Measures recognized words on a scanned page, ink, colors, baselines and stroke weight, and
/// erases the words that are replaced with text from the page image.
/// </summary>
public static class PdfScanTextAnalyzer
{
    /// <summary>Measures one line's colors and size from its words on an opaque BGRA image.</summary>
    public static PdfScanLineLook? MeasureLine(ReadOnlySpan<byte> bgra, int width, int height,
        IReadOnlyList<PdfOcrPixelWord> words, PdfScanTextOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(words);
        if (words.Count == 0) return null;
        PdfScanTextPageAnalysis analysis = Analyze(bgra, width, height, words, options);
        List<PdfScanTextWord> measured = [.. analysis.Words.Where(word => word.HasClearInk)];
        if (measured.Count == 0) return null;
        static byte Middle(IEnumerable<byte> values)
        {
            byte[] sorted = [.. values.Order()];
            return sorted[sorted.Length / 2];
        }
        static double MiddleOf(IEnumerable<double> values)
        {
            double[] sorted = [.. values.Order()];
            return sorted[sorted.Length / 2];
        }
        return new PdfScanLineLook(
            (Middle(measured.Select(word => word.BackgroundColor.Red)),
                Middle(measured.Select(word => word.BackgroundColor.Green)),
                Middle(measured.Select(word => word.BackgroundColor.Blue))),
            (Middle(measured.Select(word => word.InkColor.Red)),
                Middle(measured.Select(word => word.InkColor.Green)),
                Middle(measured.Select(word => word.InkColor.Blue))),
            MiddleOf(measured.Select(word => word.XHeight)),
            MiddleOf(measured.Select(word => word.CapHeight)),
            measured.All(word => word.CapitalsOnly),
            MiddleOf(measured.Select(word => word.Baseline)),
            measured.Count(word => word.Bold) * 2 > measured.Count,
            analysis.Words);
    }

    /// <summary>Measures every recognized word on an opaque BGRA page.</summary>
    public static PdfScanTextPageAnalysis Analyze(ReadOnlySpan<byte> bgra, int width, int height,
        IReadOnlyList<PdfOcrPixelWord> words, PdfScanTextOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(words);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (bgra.Length != checked(width * height * 4))
            throw new ArgumentException("The page must hold four bytes per pixel.", nameof(bgra));
        options ??= new PdfScanTextOptions();

        byte[] gray = new byte[width * height];
        bool bitonal = true;
        for (int i = 0, p = 0; i < gray.Length; i++, p += 4)
        {
            int value = (bgra[p + 2] * 299 + bgra[p + 1] * 587 + bgra[p] * 114 + 500) / 1000;
            gray[i] = (byte)value;
            if (bitonal && value is > 8 and < 247) bitonal = false;
        }

        var measured = new List<PdfScanTextWord>(words.Count);
        var cores = new List<(int Left, int Top, int Right, int Bottom)>(words.Count);
        foreach (PdfOcrPixelWord word in words)
        {
            int left = Math.Clamp(word.Left, 0, width);
            int top = Math.Clamp(word.Top, 0, height);
            int right = Math.Clamp(word.Right, 0, width);
            int bottom = Math.Clamp(word.Bottom, 0, height);
            cores.Add((left, top, right, bottom));
            measured.Add(new PdfScanTextWord(word, left, top, right, bottom));
        }

        for (int index = 0; index < measured.Count; index++)
            MeasureInk(measured[index], gray, bgra, width, height, cores, index, options);

        GroupLines(measured);
        MarkBold(measured);
        foreach (PdfScanTextWord word in measured)
        {
            word.Replaceable = word.HasClearInk
                && word.Word.Confidence >= options.MinimumConfidence
                && word.XHeight >= options.MinimumXHeight
                && !string.IsNullOrWhiteSpace(word.Word.Text)
                && !HasStrayMark(word.Word.Text);
        }
        return new PdfScanTextPageAnalysis(width, height, bitonal, measured);
    }

    /// <summary>
    /// Identifies scanned pages: pages painted mostly by images, without visible text, vector
    /// drawing or shadings, which can be rewritten from their rendered appearance without losing
    /// anything. Invisible text, such as an earlier OCR layer, is allowed.
    /// </summary>
    public static IReadOnlyList<bool> ReadScannedPages(PdfDocument document,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        var reader = new PdfPageContentReader(document);
        var result = new bool[reader.PageCount];
        for (int index = 0; index < result.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { result[index] = IsScanned(reader.Read(index, cancellationToken)); }
            catch (Exception exception) when (exception is FormatException or NotSupportedException)
            {
                result[index] = false;
            }
        }
        return result;
    }

    /// <summary>Frees the words' ink masks once the page has been checked and erased.</summary>
    public static void ReleaseMasks(PdfScanTextPageAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        foreach (PdfScanTextWord word in analysis.Words)
        {
            word.Mask = [];
            word.Glyphs = [];
        }
    }

    private static bool IsScanned(PdfPageContent content)
    {
        if (content.Images.Count == 0 || content.Shadings.Count > 0 || content.Paths.Count > 4)
            return false;
        double area = content.Width * content.Height;
        double covered = content.Images.Sum(image =>
            Math.Max(0, image.BoundingBox.Width) * Math.Max(0, image.BoundingBox.Height));
        if (area <= 0 || covered < area * 0.5) return false;

        // Text drawn in rendering mode 3 or 7 is invisible; anything else shows on the page.
        int mode = 0, shown = 0;
        var saved = new Stack<int>();
        foreach (PdfContentInstruction instruction in content.Instructions)
        {
            switch (instruction.Operator)
            {
                case "q": saved.Push(mode); break;
                case "Q": if (saved.Count > 0) mode = saved.Pop(); break;
                case "Tr":
                    IReadOnlyList<PdfObject> operands = instruction;
                    if (operands.Count == 1 && operands[0] is PdfInteger value)
                        mode = (int)value.Value;
                    break;
                case "Tj" or "TJ" or "'" or "\"":
                    if (mode is not (3 or 7)) return false;
                    shown++;
                    break;
            }
        }
        // Text that reaches the page only through a nested form was not checked above, so such
        // a page is left alone.
        return content.Letters.Count == 0 || shown > 0;
    }

    // A bracket, bar or brace pressed against a letter, as in "CORRECT[ON", is nearly always a
    // recognizer's reading of a broken or faint letter, not something printed.
    private static bool HasStrayMark(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] is not ('[' or ']' or '{' or '}' or '|' or '\\' or '<' or '>' or '~' or '^' or '`'))
                continue;
            if (i > 0 && char.IsLetter(text[i - 1]) || i + 1 < text.Length && char.IsLetter(text[i + 1]))
                return true;
        }
        return false;
    }

    /// <summary>Paints the background color over the ink of the given words.</summary>
    public static void Erase(Span<byte> bgra, int width, int height,
        IEnumerable<PdfScanTextWord> words)
    {
        ArgumentNullException.ThrowIfNull(words);
        if (bgra.Length != checked(width * height * 4))
            throw new ArgumentException("The page must hold four bytes per pixel.", nameof(bgra));
        foreach (PdfScanTextWord word in words)
        {
            if (word.Mask.Length == 0) continue;
            // Antialiased glyph edges sit just outside the thresholded ink; growing the mask by
            // a stroke-relative margin removes them so no halo is left behind the new text.
            int grow = Math.Max(2, (int)Math.Round(word.XHeight / 8));
            (byte red, byte green, byte blue) = word.BackgroundColor;
            int w = word.MaskWidth, h = word.MaskHeight;
            bool[] grown = Dilate(word.Mask, w, h, grow);
            for (int y = 0; y < h; y++)
            {
                int py = word.MaskTop + y;
                if ((uint)py >= (uint)height) continue;
                for (int x = 0; x < w; x++)
                {
                    if (!grown[y * w + x]) continue;
                    int px = word.MaskLeft + x;
                    if ((uint)px >= (uint)width) continue;
                    int p = (py * width + px) * 4;
                    bgra[p] = blue;
                    bgra[p + 1] = green;
                    bgra[p + 2] = red;
                    bgra[p + 3] = 255;
                }
            }
        }
    }

    private static void MeasureInk(PdfScanTextWord word, byte[] gray, ReadOnlySpan<byte> bgra,
        int width, int height, List<(int Left, int Top, int Right, int Bottom)> cores, int self,
        PdfScanTextOptions options)
    {
        (int left, int top, int right, int bottom) = cores[self];
        int boxWidth = right - left, boxHeight = bottom - top;
        if (boxWidth < 2 || boxHeight < 4) return;

        // Recognizer boxes can clip ascenders, descenders and the ends of a word, so ink is
        // searched in a margin around the box and kept only where it belongs to this word.
        int marginX = Math.Max(2, boxHeight / 4), marginY = Math.Max(2, boxHeight / 3);
        int ml = Math.Max(0, left - marginX), mt = Math.Max(0, top - marginY);
        int mr = Math.Min(width, right + marginX), mb = Math.Min(height, bottom + marginY);
        int w = mr - ml, h = mb - mt;

        int threshold = Otsu(gray, width, left, top, right, bottom, out int darkMean, out int lightMean,
            out int darkCount, out int lightCount);
        if (lightMean - darkMean < options.MinimumContrast || darkCount == 0 || darkCount >= lightCount)
            return;

        bool[] mask = new bool[w * h];
        for (int y = 0; y < h; y++)
        {
            int row = (mt + y) * width;
            for (int x = 0; x < w; x++)
                mask[y * w + x] = gray[row + ml + x] <= threshold;
        }
        RemoveRules(mask, w, h, boxHeight);
        KeepOwnComponents(mask, w, h, ml, mt, cores, self);
        if (!word.Word.Text.Contains('_')) DropUnderscores(mask, w, h);

        int inkLeft = int.MaxValue, inkTop = int.MaxValue, inkRight = -1, inkBottom = -1, ink = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                if (!mask[y * w + x]) continue;
                ink++;
                inkLeft = Math.Min(inkLeft, x); inkRight = Math.Max(inkRight, x);
                inkTop = Math.Min(inkTop, y); inkBottom = Math.Max(inkBottom, y);
            }
        if (ink < 4) return;

        word.MaskLeft = ml;
        word.MaskTop = mt;
        word.MaskWidth = w;
        word.MaskHeight = h;
        word.Mask = mask;
        word.Glyphs = [.. Components(mask, w, h).Select(pixels =>
        {
            int left = int.MaxValue, top = int.MaxValue, right = -1, bottom = -1;
            foreach (int at in pixels)
            {
                int x = at % w, y = at / w;
                left = Math.Min(left, x); right = Math.Max(right, x);
                top = Math.Min(top, y); bottom = Math.Max(bottom, y);
            }
            return (ml + left, mt + top, ml + right + 1, mt + bottom + 1);
        })];
        word.Left = ml + inkLeft;
        word.Top = mt + inkTop;
        word.Right = ml + inkRight + 1;
        word.Bottom = mt + inkBottom + 1;
        word.HasClearInk = true;

        // Ink color from the solid core of the strokes, background from clearly light pixels.
        int core = darkMean + (threshold - darkMean) / 2;
        int paper = threshold + (lightMean - threshold) / 2;
        var inkSamples = new List<int>(ink);
        var paperSamples = new List<int>(w * h - ink);
        for (int y = 0; y < h; y++)
        {
            int py = mt + y;
            for (int x = 0; x < w; x++)
            {
                int px = ml + x;
                int value = gray[py * width + px];
                if (mask[y * w + x] && value <= core) inkSamples.Add(py * width + px);
                else if (!mask[y * w + x] && value >= paper) paperSamples.Add(py * width + px);
            }
        }
        word.InkColor = MedianColor(bgra, inkSamples.Count > 0 ? inkSamples : AllMasked(mask, w, h, ml, mt, width));
        word.BackgroundColor = paperSamples.Count > 0
            ? MedianColor(bgra, paperSamples) : ((byte)255, (byte)255, (byte)255);

        // Row profile: the x-height band is where most rows carry dense ink; ascenders and
        // descenders add thinner rows above and below it.
        int[] rows = new int[h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (mask[y * w + x]) rows[y]++;
        int peak = rows.Max();
        int bandTop = inkTop, bandBottom = inkBottom;
        int dense = Math.Max(1, (int)(peak * 0.4));
        while (bandTop < inkBottom && rows[bandTop] < dense) bandTop++;
        while (bandBottom > bandTop && rows[bandBottom] < dense) bandBottom--;
        word.RawBaseline = mt + bandBottom + 1;
        word.RawXHeight = bandBottom - bandTop + 1;
        word.HasLowercaseBody = word.Word.Text.Any(char.IsLower)
            && !word.Word.Text.All(c => !char.IsLetter(c) || "bdfhklt".Contains(c));

        // Stroke thickness: mean horizontal run of ink inside the band.
        long runs = 0, runLength = 0;
        for (int y = bandTop; y <= bandBottom; y++)
        {
            int run = 0;
            for (int x = 0; x <= w; x++)
            {
                bool on = x < w && mask[y * w + x];
                if (on) run++;
                else if (run > 0) { runs++; runLength += run; run = 0; }
            }
        }
        word.StrokeWidth = runs == 0 ? 0 : runLength / (double)runs;
    }

    // Underlines and table rules touching a word run far longer than any letter stroke; they
    // stay part of the page image, and dropping them keeps letters from merging with them.
    private static void RemoveRules(bool[] mask, int w, int h, int boxHeight)
    {
        int longest = Math.Max(8, boxHeight * 3 / 2);
        for (int y = 0; y < h; y++)
        {
            int run = 0;
            for (int x = 0; x <= w; x++)
            {
                if (x < w && mask[y * w + x]) { run++; continue; }
                if (run > longest)
                    for (int k = x - run; k < x; k++) mask[y * w + k] = false;
                run = 0;
            }
        }
    }

    // Typed underscores and underlines lie flat on or below the baseline. They belong to the
    // page image, not to the word, and would otherwise pull the baseline down.
    private static void DropUnderscores(bool[] mask, int w, int h)
    {
        List<List<int>> components = Components(mask, w, h);
        var boxes = components.Select(pixels =>
        {
            int left = int.MaxValue, top = int.MaxValue, right = -1, bottom = -1;
            foreach (int at in pixels)
            {
                int x = at % w, y = at / w;
                left = Math.Min(left, x); right = Math.Max(right, x);
                top = Math.Min(top, y); bottom = Math.Max(bottom, y);
            }
            return (Left: left, Top: top, Width: right - left + 1, Height: bottom - top + 1);
        }).ToList();
        var upright = boxes.Where(box => box.Width < box.Height * 3).Select(box => (double)(box.Top + box.Height)).ToList();
        if (upright.Count == 0) return;
        double baseline = Median(upright);
        for (int i = 0; i < components.Count; i++)
        {
            (_, int top, int width, int height) = boxes[i];
            bool flat = width >= height * 3 && height <= Math.Max(4, h / 6);
            if (flat && top >= baseline - 2)
                foreach (int at in components[i]) mask[at] = false;
        }
    }

    private static List<List<int>> Components(bool[] mask, int w, int h)
    {
        var seen = new bool[mask.Length];
        var result = new List<List<int>>();
        var stack = new Stack<int>();
        for (int start = 0; start < mask.Length; start++)
        {
            if (!mask[start] || seen[start]) continue;
            var pixels = new List<int>();
            stack.Push(start);
            seen[start] = true;
            while (stack.Count > 0)
            {
                int at = stack.Pop();
                pixels.Add(at);
                int x = at % w, y = at / w;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if ((uint)nx >= (uint)w || (uint)ny >= (uint)h) continue;
                        int n = ny * w + nx;
                        if (!mask[n] || seen[n]) continue;
                        seen[n] = true;
                        stack.Push(n);
                    }
            }
            result.Add(pixels);
        }
        return result;
    }

    // Keeps the connected ink components that lie mostly inside this word's own box, so a
    // neighboring word's letters reaching into the search margin are not claimed.
    private static void KeepOwnComponents(bool[] mask, int w, int h, int originX, int originY,
        List<(int Left, int Top, int Right, int Bottom)> cores, int self)
    {
        (int left, int top, int right, int bottom) = cores[self];
        int[] label = new int[mask.Length];
        var stack = new Stack<int>();
        var pixels = new List<int>();
        int next = 0;
        for (int start = 0; start < mask.Length; start++)
        {
            if (!mask[start] || label[start] != 0) continue;
            next++;
            pixels.Clear();
            stack.Push(start);
            label[start] = next;
            while (stack.Count > 0)
            {
                int at = stack.Pop();
                pixels.Add(at);
                int x = at % w, y = at / w;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if ((uint)nx >= (uint)w || (uint)ny >= (uint)h) continue;
                        int n = ny * w + nx;
                        if (!mask[n] || label[n] != 0) continue;
                        label[n] = next;
                        stack.Push(n);
                    }
            }

            int inside = 0, otherWord = 0;
            foreach (int at in pixels)
            {
                int px = originX + at % w, py = originY + at / w;
                if (px >= left && px < right && py >= top && py < bottom) inside++;
                else if (InOtherCore(px, py, cores, self)) otherWord++;
            }
            // A component that never touches the box, or that mostly lives in another word, is
            // not part of this word. So is a large shape such as a rule line or a frame.
            bool keep = inside > 0 && inside >= otherWord
                && inside * 2 >= pixels.Count - otherWord;
            if (!keep)
                foreach (int at in pixels) mask[at] = false;
        }
    }

    private static bool InOtherCore(int x, int y, List<(int Left, int Top, int Right, int Bottom)> cores,
        int self)
    {
        for (int i = 0; i < cores.Count; i++)
        {
            if (i == self) continue;
            (int left, int top, int right, int bottom) = cores[i];
            if (x >= left && x < right && y >= top && y < bottom) return true;
        }
        return false;
    }

    private static int Otsu(byte[] gray, int stride, int left, int top, int right, int bottom,
        out int darkMean, out int lightMean, out int darkCount, out int lightCount)
    {
        Span<int> histogram = stackalloc int[256];
        int total = 0;
        for (int y = top; y < bottom; y++)
            for (int x = left; x < right; x++) { histogram[gray[y * stride + x]]++; total++; }
        long sum = 0;
        for (int i = 0; i < 256; i++) sum += (long)i * histogram[i];
        long sumBack = 0;
        int weightBack = 0, best = 127;
        double bestVariance = -1;
        for (int t = 0; t < 256; t++)
        {
            weightBack += histogram[t];
            if (weightBack == 0) continue;
            int weightFore = total - weightBack;
            if (weightFore == 0) break;
            sumBack += (long)t * histogram[t];
            double meanBack = sumBack / (double)weightBack;
            double meanFore = (sum - sumBack) / (double)weightFore;
            double between = (double)weightBack * weightFore * (meanBack - meanFore) * (meanBack - meanFore);
            if (between > bestVariance) { bestVariance = between; best = t; }
        }
        long darkSum = 0, lightSum = 0;
        darkCount = 0; lightCount = 0;
        for (int i = 0; i < 256; i++)
        {
            if (i <= best) { darkCount += histogram[i]; darkSum += (long)i * histogram[i]; }
            else { lightCount += histogram[i]; lightSum += (long)i * histogram[i]; }
        }
        darkMean = darkCount == 0 ? 0 : (int)(darkSum / darkCount);
        lightMean = lightCount == 0 ? 255 : (int)(lightSum / lightCount);
        return best;
    }

    private static List<int> AllMasked(bool[] mask, int w, int h, int originX, int originY, int stride)
    {
        var all = new List<int>();
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (mask[y * w + x]) all.Add((originY + y) * stride + originX + x);
        return all;
    }

    private static (byte, byte, byte) MedianColor(ReadOnlySpan<byte> bgra, List<int> pixels)
    {
        if (pixels.Count == 0) return (0, 0, 0);
        Span<int> red = stackalloc int[256];
        Span<int> green = stackalloc int[256];
        Span<int> blue = stackalloc int[256];
        foreach (int pixel in pixels)
        {
            int p = pixel * 4;
            blue[bgra[p]]++;
            green[bgra[p + 1]]++;
            red[bgra[p + 2]]++;
        }
        return (Median(red, pixels.Count), Median(green, pixels.Count), Median(blue, pixels.Count));
    }

    private static byte Median(Span<int> histogram, int count)
    {
        int half = (count + 1) / 2, seen = 0;
        for (int i = 0; i < 256; i++)
        {
            seen += histogram[i];
            if (seen >= half) return (byte)i;
        }
        return 255;
    }

    // Words whose bands overlap form a line; the line's baseline is fitted across its words
    // so a slightly skewed scan still gets one smooth baseline, and its x-height is the
    // median of the words that have lowercase letters without ascenders setting the band.
    private static void GroupLines(List<PdfScanTextWord> words)
    {
        // Words are taken left to right and each joins the line whose last word sits on the
        // nearest baseline, so a line on a skewed scan is followed across the page.
        var clear = words.Where(word => word.HasClearInk)
            .OrderBy(word => word.Left).ThenBy(word => word.RawBaseline).ToList();
        var lines = new List<List<PdfScanTextWord>>();
        foreach (PdfScanTextWord word in clear)
        {
            double height = Math.Max(4, word.Bottom - word.Top);
            List<PdfScanTextWord>? line = null;
            double nearest = double.MaxValue;
            foreach (List<PdfScanTextWord> candidate in lines)
            {
                PdfScanTextWord last = candidate[^1];
                if (last.Right > word.Left + height) continue;
                double size = Math.Max(height, last.Bottom - last.Top);
                double distance = Math.Abs(last.RawBaseline - word.RawBaseline);
                if (distance <= 0.35 * size && distance < nearest) { nearest = distance; line = candidate; }
            }
            if (line is null) lines.Add(line = []);
            line.Add(word);
        }
        lines.Sort((a, b) => a[0].RawBaseline.CompareTo(b[0].RawBaseline));

        for (int index = 0; index < lines.Count; index++)
        {
            List<PdfScanTextWord> line = lines[index];
            // Least-squares baseline over word centers, used only when the words agree on it.
            double slope = FitSlope(line);
            if (Math.Abs(slope) > 0.002)
            {
                // On a skewed line each word's bands are measured along the slant, or the
                // tilt would smear them and inflate the x-height.
                foreach (PdfScanTextWord word in line) ProfileAlong(word, slope);
                slope = FitSlope(line);
            }
            double meanX = line.Average(word => (word.Left + word.Right) / 2.0);
            double meanY = line.Average(word => word.RawBaseline);

            // Letter heights: the shapes standing on the baseline are mostly x-height letters in
            // lowercase text, with ascenders and capitals taller, so a low percentile of their
            // heights is the x-height. A line of only capitals and figures is sized by their height.
            double lineSize = Median(line.Select(word => (double)(word.Bottom - word.Top)));
            var heights = new List<double>();
            foreach (PdfScanTextWord word in line)
                foreach ((int left, int top, int right, int bottom) in word.Glyphs)
                {
                    double baselineAt = meanY + slope * ((left + right) / 2.0 - meanX);
                    if (Math.Abs(bottom - baselineAt) <= Math.Max(2, lineSize * 0.12))
                        heights.Add(bottom - top);
                }
            if (heights.Count > 0)
            {
                double tallest = heights.Max();
                heights.RemoveAll(height => height < tallest * 0.3);
            }
            bool lowercase = line.Any(word => word.Word.Text.Any(char.IsLower));
            double xHeight, capHeight = 0;
            if (!lowercase && heights.Count > 0)
            {
                capHeight = Median(heights);
                xHeight = capHeight * 0.68;
            }
            else if (heights.Count >= 3)
                xHeight = Percentile(heights, 0.35);
            else
            {
                var bodies = line.Where(word => word.HasLowercaseBody).Select(word => word.RawXHeight).ToList();
                xHeight = bodies.Count > 0 ? Median(bodies) : 0.7 * Median(line.Select(word => word.RawXHeight));
            }
            foreach (PdfScanTextWord word in line)
            {
                double fitted = meanY + slope * (word.Left - meanX);
                // Brackets, digits and capitals have no x-height band of their own, so their
                // measured baseline is rough; only a clear superscript or subscript keeps it.
                double center = meanY + slope * ((word.Left + word.Right) / 2.0 - meanX);
                word.Baseline = Math.Abs(center - word.RawBaseline) <= Math.Max(2, xHeight * 0.5)
                    ? fitted : word.RawBaseline - slope * (word.Right - word.Left) / 2.0;
                word.Slope = slope;
                word.XHeight = xHeight;
                word.CapitalsOnly = capHeight > 0;
                word.CapHeight = capHeight;
                word.Line = index;
            }
        }
    }

    private static double FitSlope(List<PdfScanTextWord> line)
    {
        if (line.Count < 3) return 0;
        double meanX = line.Average(word => (word.Left + word.Right) / 2.0);
        double meanY = line.Average(word => word.RawBaseline);
        double sxx = line.Sum(word => Math.Pow((word.Left + word.Right) / 2.0 - meanX, 2));
        double sxy = line.Sum(word => ((word.Left + word.Right) / 2.0 - meanX) * (word.RawBaseline - meanY));
        return sxx > 0 ? Math.Clamp(sxy / sxx, -0.05, 0.05) : 0;
    }

    // Re-measures the word's x-height band along a slanted line. The baseline is reported at
    // the horizontal center of the word's ink.
    private static void ProfileAlong(PdfScanTextWord word, double slope)
    {
        int w = word.MaskWidth, h = word.MaskHeight;
        double center = (word.Left + word.Right) / 2.0 - word.MaskLeft;
        int pad = (int)Math.Ceiling(Math.Abs(slope) * w) + 1;
        int[] rows = new int[h + 2 * pad];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                if (!word.Mask[y * w + x]) continue;
                int row = (int)Math.Round(y - slope * (x - center)) + pad;
                if ((uint)row < (uint)rows.Length) rows[row]++;
            }
        int first = Array.FindIndex(rows, count => count > 0);
        int last = Array.FindLastIndex(rows, count => count > 0);
        if (first < 0) return;
        int dense = Math.Max(1, (int)(rows.Max() * 0.4));
        int top = first, bottom = last;
        while (top < last && rows[top] < dense) top++;
        while (bottom > top && rows[bottom] < dense) bottom--;
        word.RawBaseline = word.MaskTop + bottom - pad + 1;
        word.RawXHeight = bottom - top + 1;
    }

    // A word is bold when its strokes are clearly thicker, relative to its x-height, than the
    // page's typical text.
    private static void MarkBold(List<PdfScanTextWord> words)
    {
        var ratios = words.Where(word => word.HasClearInk && word.XHeight > 0 && word.StrokeWidth > 0
                && word.Word.Text.Count(char.IsLetter) >= 3)
            .Select(word => word.StrokeWidth / word.XHeight).ToList();
        if (ratios.Count < 5) return;
        double typical = Median(ratios);
        foreach (PdfScanTextWord word in words)
            word.Bold = word.HasClearInk && word.XHeight > 0
                && word.StrokeWidth / word.XHeight > typical * 1.35;
    }

    private static double Percentile(List<double> values, double fraction)
    {
        double[] sorted = [.. values.Order()];
        return sorted[Math.Clamp((int)Math.Round(fraction * (sorted.Length - 1)), 0, sorted.Length - 1)];
    }

    private static double Median(IEnumerable<double> values)
    {
        double[] sorted = [.. values.OrderBy(value => value)];
        if (sorted.Length == 0) return 0;
        int middle = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }

    internal static bool[] Grow(bool[] mask, int w, int h, int radius) => Dilate(mask, w, h, radius);

    private static bool[] Dilate(bool[] mask, int w, int h, int radius)
    {
        bool[] result = (bool[])mask.Clone();
        for (int step = 0; step < radius; step++)
        {
            bool[] source = (bool[])result.Clone();
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    if (source[y * w + x]) continue;
                    if ((x > 0 && source[y * w + x - 1]) || (x + 1 < w && source[y * w + x + 1])
                        || (y > 0 && source[(y - 1) * w + x]) || (y + 1 < h && source[(y + 1) * w + x]))
                        result[y * w + x] = true;
                }
        }
        return result;
    }
}
