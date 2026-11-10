using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Fonts;

namespace KillerPdf.Engine.Formats.Djvu;

public sealed partial class DjvuDocument
{
    /// <summary>Converts supported bitonal and grayscale pages, completely located text, and local bookmarks to a separate PDF.</summary>
    public byte[] ConvertToPdf(CancellationToken cancellationToken = default)
        => ConvertToPdf(new List<DjvuDiagnostic>(), cancellationToken);

    /// <summary>Converts as <see cref="ConvertToPdf(CancellationToken)"/> does and adds a notice for each page whose content was kept only in part.</summary>
    public byte[] ConvertToPdf(ICollection<DjvuDiagnostic> diagnostics, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        cancellationToken.ThrowIfCancellationRequested();
        var navigation = ReadNavigation(cancellationToken);
        if (Pages.Count == 0)
            throw new NotSupportedException("DjVu PDF conversion requires supported native pages.");
        var builder = new PdfDocumentBuilder();
        for (int pageIndex = 0; pageIndex < Pages.Count; pageIndex++)
        {
            DjvuTextPage text;
            try
            {
                text = ReadTextPage(pageIndex, cancellationToken);
                foreach (var notice in text.Diagnostics) diagnostics.Add(notice);
            }
            catch (InvalidDataException error)
            {
                // A malformed text layer costs only that page's text; the page image still converts.
                text = new DjvuTextPage(string.Empty, []);
                diagnostics.Add(TextLeftOut(pageIndex, error.Message));
            }
            var (page, chunks) = ReadPageChunks(pageIndex, cancellationToken);
            if (Classify(page, chunks) == PageLayers.Compound)
                throw new NotSupportedException("DjVu PDF conversion does not yet support compound pages with foreground colors or a reduced background.");
            if (chunks.Any(chunk => chunk.Id == 0x42473434))
            {
                (page, chunks) = ReadSupportedPage(pageIndex, cancellationToken);
                var (gray, gamma) = DecodeIw44Page(page, WithoutAnnotations(chunks), cancellationToken);
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
            var image = PdfImage.FromBitonal(frameWidth, frameHeight, samples);
            var content = new PdfContentStreamBuilder().DrawImage(image, 0, 0, width, height);
            AddLocatedPdfText(content, text, Pages[pageIndex], pageIndex, diagnostics, cancellationToken);
            builder.AddPage(width, height, content);
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
                int destination;
                try { destination = ResolveNavigationTarget(entry.Target, cancellationToken: cancellationToken); }
                catch (Exception error) when (error is InvalidDataException or NotSupportedException or ArgumentException)
                {
                    // A bookmark that names no page (an external link, a viewer command, a missing page) is left out and
                    // its children take its place; it does not stop the conversion.
                    AddBookmarks(entry.Children, level);
                    continue;
                }
                builder.AddBookmark(entry.Label, destination, level);
                AddBookmarks(entry.Children, level + 1);
            }
        }
    }

    private static DjvuDiagnostic TextLeftOut(int pageIndex, string reason)
        => new("TextNotConverted", $"Page {pageIndex + 1} text was left out of the PDF: {reason.TrimEnd('.')}. " +
            "The page image converted.");

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
        // unreadable instead of the whole page losing its text.
        int replaced = 0;
        for (int index = 0; index < segments.Count; index++)
        {
            var builder = new System.Text.StringBuilder(segments[index].Text.Length);
            foreach (var rune in segments[index].Text.EnumerateRunes())
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool drawable = System.Text.Rune.IsWhiteSpace(rune) || rune == System.Text.Rune.ReplacementChar
                    || font.GetGlyphId(rune.Value) != 0;
                if (!drawable) replaced++;
                builder.Append(drawable ? rune.ToString() : "�");
            }
            segments[index] = (segments[index].Zone, builder.ToString());
        }
        if (replaced > 0)
            diagnostics.Add(new("TextCharactersReplaced", $"Page {pageIndex + 1} text has {replaced} characters the " +
                "PDF text layer cannot represent, such as control characters. They are written as replacement characters."));
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
        foreach (var (zone, segment) in segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double sx = zone.Width * scale * 1000 / (bounds.Right - bounds.Left);
            double sy = zone.Height * scale * 1000 / (bounds.Top - bounds.Bottom);
            // One invisible glyph transports the original logical range at its actual zone bounds.
            // It does not assert source positions for individual characters inside that zone.
            content.SetTextMatrix(sx, 0, 0, sy, zone.Left * scale - bounds.Left * sx / 1000,
                    zone.Bottom * scale - bounds.Bottom * sy / 1000)
                .ShowUnicodeText("A", segment);
        }
        content.EndText().RestoreState();
    }

    // Each leaf zone with the logical text it carries (the separators before it, and after the last one), or why the
    // text cannot be placed faithfully.
    private static (List<(DjvuTextZone Zone, string Text)> Segments, string? Reason) PlanLocatedText(DjvuTextPage text,
        DjvuPageInfo page, CancellationToken cancellationToken)
    {
        List<(DjvuTextZone Zone, string Text)> segments = [];
        if (text.Zones.Count == 0) return (segments, "nonempty text has no located zones");
        var hasChildren = new bool[text.Zones.Count];
        foreach (var zone in text.Zones)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (zone.Left < 0 || zone.Bottom < 0 || zone.Left + zone.Width > page.Width
                || zone.Bottom + zone.Height > page.Height)
                return (segments, "a text zone lies outside the native page");
            if (zone.ParentIndex >= 0) hasChildren[zone.ParentIndex] = true;
        }
        var leaves = new List<DjvuTextZone>();
        for (int index = 0; index < text.Zones.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!hasChildren[index] && text.Zones[index].Length != 0) leaves.Add(text.Zones[index]);
        }
        // Bound the existing substitute-code allocator and each logical replacement string.
        if (leaves.Count is < 1 or > 4096)
            return (segments, "conversion requires between 1 and 4096 nonempty leaf zones per page");
        leaves.Sort((first, second) => first.Start.CompareTo(second.Start));
        int cursor = 0;
        for (int index = 0; index < leaves.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var zone = leaves[index];
            if (zone.Start < cursor) return (segments, "leaf text ranges overlap");
            if (!Whitespace(cursor, zone.Start)) return (segments, "non-whitespace text lacks a leaf zone");
            int end = zone.Start + zone.Length;
            if (index == leaves.Count - 1)
            {
                if (!Whitespace(end, text.Text.Length)) return (segments, "non-whitespace text lacks a leaf zone");
                end = text.Text.Length;
            }
            if (end - cursor > 65536)
                return (segments, "a logical text zone exceeds the 65536 UTF16 unit conversion limit");
            segments.Add((zone, text.Text[cursor..end]));
            cursor = end;
        }
        return (segments, null);

        bool Whitespace(int start, int end)
        {
            for (int index = start; index < end; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!char.IsWhiteSpace(text.Text[index])) return false;
            }
            return true;
        }
    }
}
