using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Fonts;

namespace KillerPdf.Engine.Formats.Djvu;

public sealed partial class DjvuDocument
{
    /// <summary>Converts supported bitonal and grayscale pages, completely located text, and local bookmarks to a separate PDF.</summary>
    public byte[] ConvertToPdf(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var navigation = ReadNavigation(cancellationToken);
        if (Pages.Count == 0)
            throw new NotSupportedException("DjVu PDF conversion requires supported native pages.");
        var builder = new PdfDocumentBuilder();
        for (int pageIndex = 0; pageIndex < Pages.Count; pageIndex++)
        {
            var text = ReadTextPage(pageIndex, cancellationToken);
            var (page, chunks) = ReadSupportedPage(pageIndex, cancellationToken);
            if (chunks.Any(chunk => chunk.Id == 0x42473434))
            {
                var (gray, gamma) = DecodeIw44Page(page, chunks, cancellationToken);
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
                    new PdfCalGrayColorSpace(whiteX: 0.95047, whiteZ: 1.08883, gamma: gamma / 10d));
                var grayContent = new PdfContentStreamBuilder().DrawImage(grayImage, 0, 0, grayWidth, grayHeight);
                AddLocatedPdfText(grayContent, text, page, cancellationToken);
                builder.AddPage(grayWidth, grayHeight, grayContent);
                continue;
            }
            var frame = RenderSupportedPage(page, chunks, cancellationToken);
            if (!chunks.Any(chunk => chunk.Id == 0x536d6d72))
                throw new NotSupportedException("DjVu PDF conversion currently supports only bitonal Smmr pages.");
            var samples = new byte[frame.Width * frame.Height];
            for (int y = 0; y < frame.Height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (int x = 0; x < frame.Width; x++)
                {
                    int index = y * frame.Width + x;
                    samples[index] = frame.Pixels[index * 4] == 0 ? (byte)0 : (byte)255;
                }
            }
            double width = frame.Width * 72d / Pages[pageIndex].Dpi;
            double height = frame.Height * 72d / Pages[pageIndex].Dpi;
            var image = PdfImage.FromBitonal(frame.Width, frame.Height, samples);
            var content = new PdfContentStreamBuilder().DrawImage(image, 0, 0, width, height);
            AddLocatedPdfText(content, text, Pages[pageIndex], cancellationToken);
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
                int destination = ResolveNavigationTarget(entry.Target, cancellationToken: cancellationToken);
                builder.AddBookmark(entry.Label, destination, level);
                AddBookmarks(entry.Children, level + 1);
            }
        }
    }

    private static void AddLocatedPdfText(PdfContentStreamBuilder content, DjvuTextPage text,
        DjvuPageInfo page, CancellationToken cancellationToken)
    {
        if (text.Text.Length == 0) return;
        if (text.Zones.Count == 0)
            throw UnsupportedText("nonempty text has no located zones");
        var hasChildren = new bool[text.Zones.Count];
        foreach (var zone in text.Zones)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (zone.Left < 0 || zone.Bottom < 0 || zone.Left + zone.Width > page.Width
                || zone.Bottom + zone.Height > page.Height)
                throw UnsupportedText("a text zone lies outside the native page");
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
            throw UnsupportedText("conversion requires between 1 and 4096 nonempty leaf zones per page");
        leaves.Sort((first, second) => first.Start.CompareTo(second.Start));
        var font = PdfStandardFontSubstitutes.Find("Helvetica");
        foreach (var rune in text.Text.EnumerateRunes())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!System.Text.Rune.IsWhiteSpace(rune) && font.GetGlyphId(rune.Value) == 0)
                throw UnsupportedText("the bundled text font cannot represent a source character");
        }
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
        int cursor = 0;
        for (int index = 0; index < leaves.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var zone = leaves[index];
            if (zone.Start < cursor)
                throw UnsupportedText("leaf text ranges overlap");
            RequireWhitespace(cursor, zone.Start);
            int end = zone.Start + zone.Length;
            if (index == leaves.Count - 1)
            {
                RequireWhitespace(end, text.Text.Length);
                end = text.Text.Length;
            }
            if (end - cursor > 65536)
                throw UnsupportedText("a logical text zone exceeds the 65536 UTF16 unit conversion limit");
            double sx = zone.Width * scale * 1000 / (bounds.Right - bounds.Left);
            double sy = zone.Height * scale * 1000 / (bounds.Top - bounds.Bottom);
            // One invisible glyph transports the original logical range at its actual zone bounds.
            // It does not assert source positions for individual characters inside that zone.
            content.SetTextMatrix(sx, 0, 0, sy, zone.Left * scale - bounds.Left * sx / 1000,
                    zone.Bottom * scale - bounds.Bottom * sy / 1000)
                .ShowUnicodeText("A", text.Text[cursor..end]);
            cursor = end;
        }
        content.EndText().RestoreState();

        void RequireWhitespace(int start, int end)
        {
            for (int index = start; index < end; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!char.IsWhiteSpace(text.Text[index]))
                    throw UnsupportedText("non-whitespace text lacks a leaf zone");
            }
        }

        static NotSupportedException UnsupportedText(string reason)
            => new($"DjVu PDF conversion cannot preserve text: {reason}.");
    }
}
