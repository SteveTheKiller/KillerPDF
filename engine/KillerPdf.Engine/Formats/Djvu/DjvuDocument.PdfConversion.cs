using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Fonts;

namespace KillerPdf.Engine.Formats.Djvu;

public sealed partial class DjvuDocument
{
    /// <summary>Converts supported bitonal, grayscale, color photo and compound pages, completely located text, and local bookmarks to a separate PDF.</summary>
    public byte[] ConvertToPdf(CancellationToken cancellationToken = default)
        => ConvertToPdf(new List<DjvuDiagnostic>(), cancellationToken);

    /// <summary>Converts as <see cref="ConvertToPdf(CancellationToken)"/> does and adds a notice for each page whose content was kept only in part.</summary>
    public byte[] ConvertToPdf(ICollection<DjvuDiagnostic> diagnostics, CancellationToken cancellationToken = default)
        => ConvertToPdf(new DjvuPdfConversionOptions(), diagnostics, cancellationToken);

    /// <summary>Converts pages using the selected platform image encoder and reports omitted content.</summary>
    public byte[] ConvertToPdf(DjvuPdfConversionOptions options, ICollection<DjvuDiagnostic> diagnostics,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(diagnostics);
        cancellationToken.ThrowIfCancellationRequested();
        options.Validate();
        IReadOnlyList<DjvuNavigationEntry> navigation;
        try { navigation = ReadNavigation(cancellationToken); }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException or ArgumentException
            or OverflowException)
        {
            navigation = [];
            diagnostics.Add(new("BookmarksNotConverted", "Bookmarks were left out of the PDF: " + error.Message));
        }
        if (Pages.Count == 0)
            throw new NotSupportedException("DjVu PDF conversion requires supported native pages.");
        var builder = new PdfDocumentBuilder { CompressPageContent = true };
        for (int pageIndex = 0; pageIndex < Pages.Count; pageIndex++)
        {
            try
            {
                DjvuTextPage text;
                try
                {
                    text = ReadTextPage(pageIndex, cancellationToken);
                    foreach (var notice in text.Diagnostics) diagnostics.Add(notice);
                }
                catch (Exception error) when (error is InvalidDataException or NotSupportedException)
                {
                    // A malformed or unsupported text layer costs only that page's text; the page image still converts.
                    text = new DjvuTextPage(string.Empty, []);
                    diagnostics.Add(TextLeftOut(pageIndex, error.Message));
                }
                var (page, chunks) = ReadPageChunks(pageIndex, cancellationToken);
                if (Classify(page, chunks) == PageLayers.Compound)
                {
                    AddCompoundPdfPage(builder, page, chunks, text, pageIndex, diagnostics, cancellationToken);
                    continue;
                }
                if (IsColorPhotoPage(page, chunks))
                {
                    AddColorPhotoPdfPage(builder, page, chunks, text, pageIndex, options, diagnostics, cancellationToken);
                    continue;
                }
                if (chunks.Any(chunk => chunk.Id == 0x42473434))
                {
                    var (gray, gamma) = DecodeIw44Page(page, WithoutAnnotations(chunks), cancellationToken,
                        callerBytesPerPixel: 1);
                    bool quarterTurn = page.Orientation is DjvuOrientation.Clockwise90 or DjvuOrientation.CounterClockwise90;
                    int pixelWidth = quarterTurn ? page.Height : page.Width;
                    int pixelHeight = quarterTurn ? page.Width : page.Height;
                    var oriented = new byte[gray.Length];
                    for (int y = 0; y < page.Height; y++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        for (int x = 0; x < page.Width; x++)
                        {
                            var (targetX, targetY) = page.Orientation switch
                            {
                                DjvuOrientation.Clockwise90 => (page.Height - 1 - y, x),
                                DjvuOrientation.CounterClockwise90 => (y, page.Width - 1 - x),
                                DjvuOrientation.UpsideDown => (page.Width - 1 - x, page.Height - 1 - y),
                                _ => (x, y)
                            };
                            oriented[targetY * pixelWidth + targetX] = gray[y * page.Width + x];
                        }
                    }
                    double grayWidth = pixelWidth * 72d / page.Dpi;
                    double grayHeight = pixelHeight * 72d / page.Dpi;
                    var grayImage = PdfImage.FromCalibratedGray(pixelWidth, pixelHeight, oriented,
                        new PdfCalGrayColorSpace(whiteX: 0.95047, whiteZ: 1.08883, gamma: gamma));
                    var grayContent = new PdfContentStreamBuilder().DrawImage(grayImage, 0, 0, grayWidth, grayHeight);
                    AddLocatedPdfText(grayContent, text, page, pageIndex, diagnostics, cancellationToken);
                    builder.AddPage(grayWidth, grayHeight, grayContent);
                    continue;
                }
                // A bitonal page (Smmr, or a JB2 mask alone) is read from its kept bits, one byte per pixel, turned.
                var raster = DecodeSupportedPage(page, chunks, cancellationToken, callerBytesPerPixel: 0);
                if (!chunks.Any(chunk => chunk.Id is 0x536d6d72 or 0x536a627a))
                    throw new NotSupportedException("DjVu PDF conversion currently supports only bitonal Smmr and JB2 pages.");
                int frameWidth = raster.Width, frameHeight = raster.Height;
                var (columnOffsets, rowOffsets) = raster.Offsets(Enumerable.Range(0, frameWidth).ToArray(),
                    Enumerable.Range(0, frameHeight).ToArray());
                var samples = new byte[(long)frameWidth * frameHeight];
                for (int y = 0; y < frameHeight; y++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    for (int x = 0; x < frameWidth; x++)
                        samples[(long)y * frameWidth + x] = raster.Channel(rowOffsets[y] + columnOffsets[x]) == 0 ? (byte)0 : (byte)255;
                }
                double width = frameWidth * 72d / Pages[pageIndex].Dpi;
                double height = frameHeight * 72d / Pages[pageIndex].Dpi;
                // Lossless JBIG2 when it is smaller than CCITT G4 or Flate: about a third smaller on text pages.
                var image = PdfImage.FromBitonal(frameWidth, frameHeight, samples, allowJbig2: true);
                var content = new PdfContentStreamBuilder().DrawImage(image, 0, 0, width, height);
                AddLocatedPdfText(content, text, Pages[pageIndex], pageIndex, diagnostics, cancellationToken);
                builder.AddPage(width, height, content);
            }
            catch (Exception error) when (error is InvalidDataException or NotSupportedException or ArgumentException
                or OverflowException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string message = $"Page {pageIndex + 1} could not be converted.";
                diagnostics.Add(new("PageNotConverted", message + " " + error.Message) { PageIndex = pageIndex });
                var font = PdfStandardFontSubstitutes.Find("Helvetica");
                var content = new PdfContentStreamBuilder().BeginText().SetFont(font, 12)
                    .SetTextMatrix(1, 0, 0, 1, 36, 756).ShowUnicodeText(message).EndText();
                builder.AddPage(612, 792, content);
            }
        }
        AddBookmarks(navigation, 0);
        cancellationToken.ThrowIfCancellationRequested();
        byte[] result = builder.Build();
        cancellationToken.ThrowIfCancellationRequested();
        return result;

        void AddBookmarks(IReadOnlyList<DjvuNavigationEntry> entries, int level)
        {
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    int destination = ResolveNavigationTarget(entry.Target, cancellationToken: cancellationToken);
                    builder.AddBookmark(entry.Label, destination, level);
                }
                catch (Exception error) when (error is InvalidDataException or NotSupportedException or ArgumentException)
                {
                    // A bookmark that names no page (an external link, a viewer command, a missing page) is left out and
                    // its children take its place; it does not stop the conversion.
                    diagnostics.Add(new("BookmarkNotConverted", $"Bookmark '{entry.Label}' was left out of the PDF: " + error.Message));
                    AddBookmarks(entry.Children, level);
                    continue;
                }
                AddBookmarks(entry.Children, level + 1);
            }
        }
    }

    private static DjvuDiagnostic TextLeftOut(int pageIndex, string reason)
        => new("TextNotConverted", $"Page {pageIndex + 1} text was left out of the PDF: {reason.TrimEnd('.')}. " +
            "The page image converted.") { PageIndex = pageIndex };

    // One run of the page text written at a rectangle in native pixels (left, bottom, width, height). Structural runs
    // are the separators the DjVu text model keeps between zones, which no leaf zone covers.
    private readonly record struct LocatedSegment(double Left, double Bottom, double Width, double Height, string Text,
        bool Structural);

    // The separators DjVu text layers put between zones: U+0000 ends the page, U+001C through U+001F end the
    // columns, regions and paragraphs. They are written as they are.
    private static bool IsTextModelSeparator(System.Text.Rune rune) => rune.Value is 0 or >= 0x1c and <= 0x1f;

    // Writes the page's located text as an invisible layer, or, when the text cannot be placed faithfully, leaves it
    // out with a notice naming the page; text never stops the conversion. Every check runs before anything is written.
    private static void AddLocatedPdfText(PdfContentStreamBuilder content, DjvuTextPage text,
        DjvuPageInfo page, int pageIndex, ICollection<DjvuDiagnostic> diagnostics, CancellationToken cancellationToken)
    {
        if (text.Text.Length == 0) return;
        var font = PdfStandardFontSubstitutes.Find("Helvetica");
        var (segments, reason) = PlanLocatedText(text, page, cancellationToken);
        if (reason is not null)
        {
            diagnostics.Add(TextLeftOut(pageIndex, reason));
            return;
        }
        // A character the text font cannot represent (a control character, or one outside the font) is written as
        // U+FFFD. U+FFFD itself stands for source bytes that were not UTF-8. The invisible layer carries it through
        // the ToUnicode map only, never as a drawn glyph, so copy and search see a replacement where the source was
        // unreadable instead of the whole page losing its text. The text model's own separators between zones are
        // not unreadable bytes and are carried as they are.
        int replaced = 0;
        for (int index = 0; index < segments.Count; index++)
        {
            var builder = new System.Text.StringBuilder(segments[index].Text.Length);
            foreach (var rune in segments[index].Text.EnumerateRunes())
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool drawable = System.Text.Rune.IsWhiteSpace(rune) || rune == System.Text.Rune.ReplacementChar
                    || (segments[index].Structural && IsTextModelSeparator(rune)) || font.GetGlyphId(rune.Value) != 0;
                if (!drawable) replaced++;
                builder.Append(drawable ? rune.ToString() : "�");
            }
            segments[index] = segments[index] with { Text = builder.ToString() };
        }
        if (replaced > 0)
            diagnostics.Add(new("TextCharactersReplaced", $"Page {pageIndex + 1} text has {replaced} characters the " +
                "PDF text layer cannot represent, such as control characters. They are written as replacement characters.")
                { PageIndex = pageIndex });
        var bounds = new TrueTypeOutlineReader(font).Bounds(font.GetGlyphId('A'))!.Value;
        double scale = 72d / page.Dpi;
        content.SaveState();
        switch (page.Orientation)
        {
            case DjvuOrientation.Clockwise90:
                content.Transform(0, -1, 1, 0, 0, page.Width * scale); break;
            case DjvuOrientation.CounterClockwise90:
                content.Transform(0, 1, -1, 0, page.Height * scale, 0); break;
            case DjvuOrientation.UpsideDown:
                content.Transform(-1, 0, 0, -1, page.Width * scale, page.Height * scale); break;
        }
        content.BeginText().SetTextRenderingMode(PdfTextRenderingMode.Invisible).SetFont(font, 1);
        foreach (var segment in segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double sx = segment.Width * scale * 1000 / (bounds.Right - bounds.Left);
            double sy = segment.Height * scale * 1000 / (bounds.Top - bounds.Bottom);
            // One invisible glyph transports the original logical range at its actual zone bounds.
            // It does not assert source positions for individual characters inside that zone.
            content.SetTextMatrix(sx, 0, 0, sy, segment.Left * scale - bounds.Left * sx / 1000,
                    segment.Bottom * scale - bounds.Bottom * sy / 1000)
                .ShowUnicodeText("A", segment.Text);
        }
        content.EndText().RestoreState();
    }

    // The page text in reading order as runs with a rectangle each, or why the text cannot be placed faithfully.
    // Each character belongs to the deepest zone whose text range holds it. A leaf zone places its own text at its own
    // rectangle. Characters a parent holds beyond its children (the separators between lines, paragraphs, regions
    // and the page, or a character the OCR kept outside both neighbouring words) form a run per parent: it sits
    // between the rectangles of the children before and after it, and when that gap is empty (the last child ends
    // where the parent ends, or the next one starts on another line) it takes its proportional share of the parent's
    // width by character offset. A run of whitespace alone that no leaf holds joins the run before it, or the first
    // run when it leads the text, so line ends stay with the line they end.
    private static (List<LocatedSegment> Segments, string? Reason) PlanLocatedText(DjvuTextPage text,
        DjvuPageInfo page, CancellationToken cancellationToken)
    {
        List<LocatedSegment> segments = [];
        if (text.Zones.Count == 0) return (segments, "nonempty text has no located zones");
        int length = text.Text.Length;
        var hasChildren = new bool[text.Zones.Count];
        var depth = new int[text.Zones.Count];
        for (int index = 0; index < text.Zones.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var zone = text.Zones[index];
            if (zone.Left < 0 || zone.Bottom < 0 || zone.Left + zone.Width > page.Width
                || zone.Bottom + zone.Height > page.Height)
                return (segments, "a text zone lies outside the native page");
            if (zone.ParentIndex >= 0)
            {
                hasChildren[zone.ParentIndex] = true;
                depth[index] = depth[zone.ParentIndex] + 1;
            }
        }
        var leaves = new List<int>();
        for (int index = 0; index < text.Zones.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!hasChildren[index] && text.Zones[index].Length != 0) leaves.Add(index);
        }
        // Bound the existing substitute-code allocator and each logical replacement string.
        if (leaves.Count > 4096)
            return (segments, "conversion requires between 1 and 4096 nonempty leaf zones per page");
        leaves.Sort((first, second) => text.Zones[first].Start.CompareTo(text.Zones[second].Start));
        var owner = new int[length];
        Array.Fill(owner, -1);
        int previousEnd = 0;
        foreach (int leaf in leaves)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var zone = text.Zones[leaf];
            if (zone.Start < previousEnd) return (segments, "leaf text ranges overlap");
            previousEnd = zone.Start + zone.Length;
            Array.Fill(owner, leaf, zone.Start, zone.Length);
        }
        // The rest goes to the deepest parent that holds it: deepest parents first, each taking what is still free.
        // next[k] is the first free character at or after k, so every character is visited once.
        var next = new int[length + 1];
        for (int k = 0; k <= length; k++) next[k] = k < length && owner[k] >= 0 ? k + 1 : k;
        int Free(int k)
        {
            int root = k;
            while (next[root] != root) root = next[root];
            while (next[k] != root)
            {
                int following = next[k];
                next[k] = root;
                k = following;
            }
            return root;
        }
        var parents = new List<int>();
        for (int index = 0; index < text.Zones.Count; index++)
            if (hasChildren[index] && text.Zones[index].Length != 0) parents.Add(index);
        parents.Sort((first, second) => depth[first] != depth[second] ? depth[second].CompareTo(depth[first])
            : first.CompareTo(second));
        foreach (int parent in parents)
        {
            var zone = text.Zones[parent];
            int end = zone.Start + zone.Length;
            for (int k = Free(zone.Start); k < end; k = Free(k + 1))
            {
                cancellationToken.ThrowIfCancellationRequested();
                owner[k] = parent;
                next[k] = k + 1;
            }
        }
        List<int>[]? children = null;
        string leading = string.Empty;
        int separatorRuns = 0;
        for (int start = 0; start < length;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int id = owner[start], end = start + 1;
            while (end < length && owner[end] == id) end++;
            string run = text.Text[start..end];
            if (id >= 0 && !hasChildren[id])
            {
                var zone = text.Zones[id];
                segments.Add(new(zone.Left, zone.Bottom, zone.Width, zone.Height, leading + run, false));
                leading = string.Empty;
            }
            else if (run.All(char.IsWhiteSpace))
            {
                if (segments.Count == 0) leading += run;
                else segments[^1] = segments[^1] with { Text = segments[^1].Text + run };
            }
            else if (id < 0) return (segments, "non-whitespace text lacks a zone");
            else
            {
                if (++separatorRuns > 4096)
                    return (segments, "conversion supports at most 4096 text runs between word zones per page");
                children ??= BuildChildren(text, hasChildren, cancellationToken);
                var (left, bottom, width, height) = GapRectangle(text.Zones, children, id, start, end);
                segments.Add(new(left, bottom, width, height, leading + run, true));
                leading = string.Empty;
            }
            start = end;
        }
        if (segments.Count == 0) return (segments, "conversion requires between 1 and 4096 nonempty leaf zones per page");
        foreach (var segment in segments)
            if (segment.Text.Length > 65536)
                return (segments, "a logical text zone exceeds the 65536 UTF16 unit conversion limit");
        return (segments, null);
    }

    private static List<int>[] BuildChildren(DjvuTextPage text, bool[] hasChildren, CancellationToken cancellationToken)
    {
        var children = new List<int>[text.Zones.Count];
        for (int index = 0; index < text.Zones.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (hasChildren[index]) children[index] = [];
        }
        for (int index = 0; index < text.Zones.Count; index++)
            if (text.Zones[index].ParentIndex >= 0 && text.Zones[index].Length != 0)
                children[text.Zones[index].ParentIndex].Add(index);
        return children;
    }

    // Where characters [start, end) that parent zone `id` holds beyond its children go: across the parent's height,
    // between the child text ends before and after them, or, when that gap is empty, their proportional share of the
    // parent's width by character offset.
    private static (double Left, double Bottom, double Width, double Height) GapRectangle(
        IReadOnlyList<DjvuTextZone> zones, List<int>[] children, int id, int start, int end)
    {
        var parent = zones[id];
        double left = parent.Left, right = parent.Left + parent.Width;
        long before = -1, after = long.MaxValue;
        foreach (int child in children[id])
        {
            var zone = zones[child];
            if (zone.Start + zone.Length <= start && zone.Start + zone.Length > before)
            {
                before = zone.Start + zone.Length;
                left = Math.Max(left, zone.Left + zone.Width);
            }
            else if (zone.Start >= end && zone.Start < after)
            {
                after = zone.Start;
                right = Math.Min(right, zone.Left);
            }
        }
        if (right > left) return (left, parent.Bottom, right - left, parent.Height);
        double share = (double)parent.Width / parent.Length;
        return (parent.Left + share * (start - parent.Start), parent.Bottom, share * (end - start), parent.Height);
    }
}
