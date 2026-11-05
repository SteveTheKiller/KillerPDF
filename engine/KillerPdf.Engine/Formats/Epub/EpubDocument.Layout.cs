using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Filters;

namespace KillerPdf.Engine.Formats.Epub;

public sealed partial class EpubDocument
{
    /// <summary>Lays out native EPUB pages in CSS pixels using an explicit fallback font.</summary>
    public EpubLayoutDocument CreateLayout(EpubLayoutOptions options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.DefaultFont);
        if (!double.IsFinite(options.PageWidth) || options.PageWidth is <= 0 or > 32768)
            throw new ArgumentOutOfRangeException(nameof(options), "Native page width must be in (0,32768].");
        if (!double.IsFinite(options.PageHeight) || options.PageHeight is <= 0 or > 32768)
            throw new ArgumentOutOfRangeException(nameof(options), "Native page height must be in (0,32768].");
        if (!double.IsFinite(options.Margin) || options.Margin < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Native page margin must be finite and nonnegative.");
        double contentWidth = options.PageWidth - 2 * options.Margin;
        double contentHeight = options.PageHeight - 2 * options.Margin;
        if (contentWidth <= 0 || contentHeight <= 0 || !double.IsFinite(options.FontSize)
            || options.FontSize <= 0 || options.FontSize > contentHeight)
            throw new ArgumentOutOfRangeException(nameof(options), "Native typography must fit a positive content box.");
        if ((long)Math.Ceiling(options.PageWidth) * (long)Math.Ceiling(options.PageHeight)
            > 128L * 1024 * 1024 / (2 * 4 * sizeof(float)))
            throw new InvalidDataException("The native page exceeds its combined 128 MiB pixel storage limit.");
        if (Layout != "reflowable" || PageProgressionDirection is not ("default" or "ltr")
            || Spine.Count == 0)
            throw new NotSupportedException("Native EPUB layout requires linear, reflowable, left-to-right spine items.");
        var pages = new List<EpubLayoutPage>();
        var anchors = new List<(int SpineIndex, string Id, int Offset)>();
        var reader = new TrueTypeOutlineReader(options.DefaultFont);
        int remainingTextLength = 100_000;
        int remainingStylesheetBytes = 1_000_000;
        int remainingStylesheetReferences = 1024;
        var imageCache = new Dictionary<string, PngDecodedImage>(StringComparer.Ordinal);
        long retainedImageBytes = 0;
        for (int spineIndex = 0; spineIndex < Spine.Count; spineIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AppendNativeSpinePages(spineIndex, options, contentWidth, reader, pages, anchors,
                ref remainingTextLength, ref remainingStylesheetBytes, ref remainingStylesheetReferences,
                imageCache, ref retainedImageBytes, cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new EpubLayoutDocument(options, pages, Spine.Select(entry => entry.Item.Href), anchors);
    }

    private void AppendNativeSpinePages(int spineIndex, EpubLayoutOptions options, double contentWidth,
        TrueTypeOutlineReader reader, List<EpubLayoutPage> pages,
        List<(int SpineIndex, string Id, int Offset)> anchors, ref int remainingTextLength,
        ref int remainingStylesheetBytes, ref int remainingStylesheetReferences,
        Dictionary<string, PngDecodedImage> imageCache, ref long retainedImageBytes, CancellationToken cancellationToken)
    {
        var entry = Spine[spineIndex];
        if (!entry.IsLinear || !string.IsNullOrWhiteSpace(entry.Properties))
            throw new NotSupportedException("Native EPUB layout requires linear, reflowable, left-to-right spine items.");
        var resource = entry.Item;
        if (resource.MediaType != "application/xhtml+xml" || !string.IsNullOrWhiteSpace(resource.Properties))
            throw new NotSupportedException("Native EPUB layout requires plain unscripted XHTML.");
        if (_resources[resource.Href].Length > MaxEntryBytes)
            throw new InvalidDataException("Native EPUB XHTML exceeds its 8 MiB resource limit.");
        var content = new EpubNativeContent(options.FontSize, options.PageHeight - 2 * options.Margin);
        LoadNativeStylesheets(resource.Href, content, ref remainingStylesheetBytes,
            ref remainingStylesheetReferences, cancellationToken);
        string text = ReadStructuralText(spineIndex, cancellationToken, content);
        if (text.Length > remainingTextLength)
            throw new InvalidDataException("Native EPUB text exceeds its 100,000 placement limit.");
        remainingTextLength -= text.Length;
        foreach (var anchor in content.Anchors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (anchors.Count >= 100_000)
                throw new InvalidDataException("Native EPUB anchors exceed their 100,000 destination limit.");
            anchors.Add((spineIndex, anchor.Id, Math.Min(anchor.Offset, text.Length)));
        }
        var font = options.DefaultFont;
        var placements = new List<EpubTextPlacement>(text.Length);
        var glyphs = new List<NativePagePaint>(text.Length);
        int pageStart = 0;
        double left = options.Margin;
        double top = options.Margin;
        var line = new List<(int Index, double Left, double Width, PdfGlyphOutline? Outline,
            PngDecodedImage? Image, EpubNativeContent.Style Style, EpubNativeContent.Style BlockStyle)>();
        int lineStart = 0;
        EpubNativeContent.Element? lineBlock = null;
        int imageIndex = 0;
        for (int index = 0; index <= text.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            while (imageIndex < content.Images.Count && content.Images[imageIndex].Offset == index)
            {
                var reference = content.Images[imageIndex++];
                BeginInline(reference.Owner, index);
                var image = ReadNativeImage(resource.Href, reference.Source, imageCache, ref retainedImageBytes, cancellationToken);
                if (left + image.Width > options.PageWidth - options.Margin)
                    throw new NotSupportedException("The native EPUB image exceeds the available line width.");
                line.Add((index, left, image.Width, null, image, reference.Owner.Style, reference.Owner.BlockStyle));
                left += image.Width;
            }
            if (index == text.Length) break;
            char character = text[index];
            if (character == '\n')
            {
                var source = content.Characters[index];
                bool explicitBreak = source?.Node.Owner.Name == "br";
                bool paragraph = !explicitBreak && index + 1 < text.Length && text[index + 1] == '\n'
                    && content.Characters[index + 1] is null;
                int separatorLength = paragraph ? 2 : 1;
                // Separators retain their source indices but have no placement.
                if (explicitBreak && line.Count == 0)
                {
                    double height = Math.Max(AboveBaseline(source!.BlockStyle), AboveBaseline(source.Style))
                        + Math.Max(BelowBaseline(source.BlockStyle), BelowBaseline(source.Style));
                    if (height > options.PageHeight - 2 * options.Margin)
                        throw new NotSupportedException("The native EPUB line exceeds the available page height.");
                    if (top + height > options.PageHeight - options.Margin)
                    {
                        FinishPage(index);
                        top = options.Margin;
                    }
                    top += height;
                }
                else FlushLine();
                if (paragraph) top += options.FontSize;
                if (top + options.FontSize > options.PageHeight - options.Margin)
                {
                    FinishPage(index + separatorLength);
                    top = options.Margin;
                }
                left = options.Margin;
                lineStart = index + separatorLength;
                index += separatorLength - 1;
                continue;
            }
            if (character is < ' ' or > '~')
                throw new NotSupportedException("Native EPUB layout currently supports only printable ASCII text.");
            if (content.Characters[index] is { } characterSource) BeginInline(characterSource.Node.Owner, index);
            ushort glyph = font.GetGlyphId(character);
            if (glyph == 0 && character != ' ')
                throw new NotSupportedException($"The native EPUB font has no glyph for U+{(int)character:X4}.");
            PdfGlyphOutline outline = reader.Outline(glyph)
                ?? throw new NotSupportedException("The native EPUB glyph outline format is not implemented.");
            // The public advance API rounds to 1000 units; finer font-metric precision is a later slice.
            double width = font.GetPdfAdvanceWidth(glyph) / 1000d * StyleAt(index).FontSize;
            if (character == ' ')
            {
                // Each collapsed separator scans only the following word, keeping lookahead linear.
                double wordWidth = 0;
                for (int next = index + 1; next < text.Length && text[next] is not (' ' or '\n'); next++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    char nextCharacter = text[next];
                    if (nextCharacter is < ' ' or > '~')
                        throw new NotSupportedException("Native EPUB layout currently supports only printable ASCII text.");
                    ushort nextGlyph = font.GetGlyphId(nextCharacter);
                    if (nextGlyph == 0)
                        throw new NotSupportedException($"The native EPUB font has no glyph for U+{(int)nextCharacter:X4}.");
                    wordWidth += font.GetPdfAdvanceWidth(nextGlyph) / 1000d * StyleAt(next).FontSize;
                }
                if (wordWidth > contentWidth)
                    throw new NotSupportedException("The native EPUB word exceeds the available line width.");
                if (left + width + wordWidth > options.PageWidth - options.Margin)
                {
                    FlushLine();
                    if (top + options.FontSize > options.PageHeight - options.Margin)
                    {
                        FinishPage(index + 1);
                        top = options.Margin;
                    }
                    left = options.Margin;
                    lineStart = index + 1;
                    continue;
                }
            }
            if (left + width > options.PageWidth - options.Margin)
                throw new NotSupportedException("The native EPUB word exceeds the available line width.");
            line.Add((index, left, width, outline, null, StyleAt(index),
                content.Characters[index]?.BlockStyle ?? content.DefaultStyle));
            left += width;
        }
        cancellationToken.ThrowIfCancellationRequested();
        FlushLine();
        FinishPage(text.Length);

        EpubNativeContent.Style StyleAt(int index) => content.Characters[index]?.Style ?? content.DefaultStyle;

        void BeginInline(EpubNativeContent.Element owner, int index)
        {
            var block = owner;
            while (block.Name != "p" && block.Parent is { } parent) block = parent;
            if (line.Count > 0 && !ReferenceEquals(lineBlock, block))
            {
                FlushLine();
                top += options.FontSize;
                left = options.Margin;
                lineStart = index;
            }
            lineBlock = block;
        }

        void FlushLine()
        {
            if (line.Count == 0) return;
            var block = line[0].BlockStyle;
            double ascent = AboveBaseline(block), descent = BelowBaseline(block);
            foreach (var item in line)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ascent = Math.Max(ascent, item.Image?.Height ?? AboveBaseline(item.Style));
                descent = Math.Max(descent, item.Image is null ? BelowBaseline(item.Style) : 0);
            }
            double height = ascent + descent;
            if (height > options.PageHeight - 2 * options.Margin)
                throw new NotSupportedException("The native EPUB line exceeds the available page height.");
            if (top + height > options.PageHeight - options.Margin)
            {
                FinishPage(lineStart);
                top = options.Margin;
            }
            double baseline = top + ascent;
            foreach (var item in line)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (item.Image is { } image)
                {
                    glyphs.Add(new NativeImagePaint(image, item.Left, baseline - image.Height));
                    continue;
                }
                double size = item.Style.FontSize;
                double logicalTop = baseline - font.Ascender * size / font.UnitsPerEm;
                placements.Add(new EpubTextPlacement(spineIndex, item.Index, 1, item.Left, logicalTop, item.Width, size));
                if (item.Outline!.Contours.Count != 0)
                    glyphs.Add(new NativeGlyphPaint(item.Outline, item.Left, baseline, size / 1000, item.Style.Color));
            }
            top += height;
            line.Clear();
        }

        double AboveBaseline(EpubNativeContent.Style style) =>
            font.Ascender * style.FontSize / font.UnitsPerEm + HalfLeading(style);

        double BelowBaseline(EpubNativeContent.Style style) =>
            -font.Descender * style.FontSize / font.UnitsPerEm + HalfLeading(style);

        double HalfLeading(EpubNativeContent.Style style) =>
            style.LineHeight is double height
                ? (height - (font.Ascender - font.Descender) * style.FontSize / font.UnitsPerEm) / 2
                : 0;

        void FinishPage(int end)
        {
            pages.Add(new EpubLayoutPage(text[pageStart..end], pageStart, spineIndex, placements, glyphs, content.CanvasBackground));
            pageStart = end;
            placements.Clear();
            glyphs.Clear();
        }
    }

}
