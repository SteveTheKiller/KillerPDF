using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Filters;
using System.Globalization;
using System.Text;

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
        var familyFonts = new Dictionary<EpubFontFamily, TrueTypeFont?>();
        var retainedFonts = new HashSet<TrueTypeFont> { options.DefaultFont };
        long retainedFontBytes = 0;
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
                imageCache, ref retainedImageBytes, ResolveFamily, cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new EpubLayoutDocument(options, pages, Spine.Select(entry => entry.Item.Href), anchors);

        TrueTypeFont? ResolveFamily(EpubFontFamily family)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (options.FontFamilyResolver is null) return null;
            var key = family with { Name = family.Name.ToUpperInvariant() };
            if (familyFonts.TryGetValue(key, out var cached)) return cached;
            if (familyFonts.Count >= 256)
                throw new InvalidDataException("Native EPUB exceeds its 256 font-family lookup limit.");
            var selected = options.FontFamilyResolver(family);
            if (selected is not null && retainedFonts.Add(selected))
            {
                retainedFontBytes += selected.FontData.Length;
                if (retainedFonts.Count > 64 || retainedFontBytes > 64L << 20)
                    throw new InvalidDataException("Native EPUB exceeds its retained font limit.");
            }
            familyFonts.Add(key, selected);
            return selected;
        }
    }

    private void AppendNativeSpinePages(int spineIndex, EpubLayoutOptions options, double contentWidth,
        TrueTypeOutlineReader reader, List<EpubLayoutPage> pages,
        List<(int SpineIndex, string Id, int Offset)> anchors, ref int remainingTextLength,
        ref int remainingStylesheetBytes, ref int remainingStylesheetReferences,
        Dictionary<string, PngDecodedImage> imageCache, ref long retainedImageBytes,
        Func<EpubFontFamily, TrueTypeFont?> resolveFamily, CancellationToken cancellationToken)
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
        var substitutions = new Dictionary<TrueTypeFont, OpenTypeGlyphSubstitution>();
        var readers = new Dictionary<TrueTypeFont, TrueTypeOutlineReader> { [font] = reader };
        int fontWork = 0;
        var clusters = new Dictionary<int, EpubGlyphCluster>();
        var inlineOffsets = content.Inlines.Select(item => item.Offset).ToHashSet();
        int shapingWork = 0;
        for (int runStart = 0; runStart < text.Length;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (text[runStart] == '\n') { runStart++; continue; }
            int runEnd = runStart;
            var run = new List<EpubGlyphCluster>();
            var owner = content.Characters[runStart]?.Node.Owner;
            font = FontFor(owner?.Style ?? content.DefaultStyle, ReadScalar(runStart).Value);
            bool latin = IsLatin(runStart);
            while (runEnd < text.Length && text[runEnd] != '\n'
                && ReferenceEquals(content.Characters[runEnd]?.Node.Owner, owner)
                && IsLatin(runEnd) == latin
                && (runEnd == runStart || !inlineOffsets.Contains(runEnd)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                Rune scalar = ReadScalar(runEnd);
                if (!ReferenceEquals(font, FontFor(owner?.Style ?? content.DefaultStyle, scalar.Value))) break;
                ushort glyph = font.GetGlyphId(scalar.Value);
                if (glyph == 0 && scalar.Value != ' ')
                    throw new NotSupportedException($"The native EPUB font has no glyph for U+{scalar.Value:X4}.");
                run.Add(Cluster(runEnd, scalar.Utf16SequenceLength, glyph));
                runEnd += scalar.Utf16SequenceLength;
            }
            if (latin)
                foreach (var lookup in Substitution(font).Lookups)
                {
                    var selected = new List<EpubGlyphCluster>(run.Count);
                    for (int at = 0; at < run.Count; at++)
                    {
                        ChargeShapingWork();
                        var chosen = run[at];
                        foreach (var ligature in lookup)
                        {
                            ChargeShapingWork();
                            if (ligature.Components.Length > run.Count - at) continue;
                            bool match = true;
                            for (int c = 0; c < ligature.Components.Length; c++)
                            {
                                ChargeShapingWork();
                                if (run[at + c].Glyphs[0].GlyphId != ligature.Components[c]) { match = false; break; }
                            }
                            if (!match) continue;
                            int length = run[at + ligature.Components.Length - 1].Start
                                + run[at + ligature.Components.Length - 1].Length - run[at].Start;
                            chosen = Cluster(run[at].Start, length, ligature.Output, true);
                            at += ligature.Components.Length - 1;
                            break;
                        }
                        selected.Add(chosen);
                    }
                    run = selected;
                }
            foreach (var cluster in run) clusters.Add(cluster.Start, cluster);
            runStart = runEnd;
        }
        void ChargeShapingWork()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++shapingWork > 1_000_000)
                throw new InvalidDataException("The EPUB GSUB shaping work limit was exceeded.");
        }
        bool IsLatin(int at) => text[at] is >= 'A' and <= 'Z' or >= 'a' and <= 'z'
            or >= '\u00C0' and <= '\u02FF' or >= '\u1E00' and <= '\u1EFF';
        EpubGlyphCluster Cluster(int start, int length, ushort glyph, bool substituted = false) => new(start, length, font,
            [new(glyph, font.GetPdfAdvanceWidth(glyph) / 1000d, 0, 0)], substituted);
        OpenTypeGlyphSubstitution Substitution(TrueTypeFont selected)
        {
            if (!substitutions.TryGetValue(selected, out var value))
                substitutions.Add(selected, value = new OpenTypeGlyphSubstitution(selected, cancellationToken));
            return value;
        }
        TrueTypeFont FontFor(EpubNativeContent.Style style, int scalar = ' ')
        {
            foreach (var family in style.FontFamilies ?? [])
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++fontWork > 1_000_000)
                    throw new InvalidDataException("Native EPUB font matching exceeds its work limit.");
                if (resolveFamily(family) is { } selected && selected.GetGlyphId(scalar) != 0) return selected;
            }
            return options.DefaultFont;
        }
        var placements = new List<EpubTextPlacement>(text.Length);
        var glyphs = new List<NativePagePaint>(text.Length);
        var blockBackgrounds = new List<NativePagePaint>();
        var inlineBackgrounds = new List<(int Depth, NativePagePaint Paint)>();
        int pageStart = 0;
        double left = options.Margin;
        double top = options.Margin;
        var line = new List<(int Index, int Length, double Left, double Width, TrueTypeFont Font, PdfGlyphOutline? Outline,
            PngDecodedImage? Image, EpubNativeContent.Style Style, EpubNativeContent.Style BlockStyle,
            EpubNativeContent.Element? Owner)>();
        int lineStart = 0;
        EpubNativeContent.Element? lineBlock = null;
        int inlineIndex = 0;
        for (int index = 0; index <= text.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            while (inlineIndex < content.Inlines.Count && content.Inlines[inlineIndex].Offset == index)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var reference = content.Inlines[inlineIndex++];
                BeginInline(reference.Owner, index);
                if (reference.Source is null)
                {
                    if (line.Count == 0)
                    {
                        double height = Math.Max(AboveBaseline(reference.Owner.BlockStyle), AboveBaseline(reference.Owner.Style))
                            + Math.Max(BelowBaseline(reference.Owner.BlockStyle), BelowBaseline(reference.Owner.Style));
                        if (height > options.PageHeight - 2 * options.Margin)
                            throw new NotSupportedException("The native EPUB line exceeds the available page height.");
                        if (top + height > options.PageHeight - options.Margin)
                        {
                            FinishPage(index);
                            top = options.Margin;
                        }
                        if (reference.Owner.BlockStyle.Background?.Color is { } blankBackground)
                            blockBackgrounds.Add(new NativeRectanglePaint(options.Margin, top, contentWidth, height, blankBackground));
                        if (blockBackgrounds.Count + inlineBackgrounds.Count + glyphs.Count > 100_000)
                            throw new InvalidDataException("The native page exceeds its paint limit.");
                        top += height;
                    }
                    else FlushLine();
                    left = options.Margin;
                    lineStart = index;
                    continue;
                }
                var image = ReadNativeImage(resource.Href, reference.Source, imageCache, ref retainedImageBytes, cancellationToken);
                if (left + image.Width > options.PageWidth - options.Margin)
                    throw new NotSupportedException("The native EPUB image exceeds the available line width.");
                line.Add((index, 0, left, image.Width, FontFor(reference.Owner.Style), null, image,
                    reference.Owner.Style, reference.Owner.BlockStyle, reference.Owner));
                left += image.Width;
            }
            if (index == text.Length) break;
            char character = text[index];
            if (character == '\n')
            {
                var source = content.Characters[index];
                bool explicitBreak = source?.Node.Owner.Name == "br";
                if (explicitBreak)
                {
                    lineStart = index + 1;
                    continue;
                }
                bool paragraph = !explicitBreak && index + 1 < text.Length && text[index + 1] == '\n'
                    && content.Characters[index + 1] is null;
                int separatorLength = paragraph ? 2 : 1;
                // Separators retain their source indices but have no placement.
                FlushLine();
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
            var cluster = clusters[index];
            if (content.Characters[index] is { } characterSource) BeginInline(characterSource.Node.Owner, index);
            ushort glyph = cluster.Glyphs[0].GlyphId;
            if (glyph == 0 && character != ' ')
                throw new NotSupportedException("The native EPUB font has no glyph for this cluster.");
            if (!readers.TryGetValue(cluster.Font, out var selectedReader))
                readers.Add(cluster.Font, selectedReader = new TrueTypeOutlineReader(cluster.Font));
            PdfGlyphOutline outline = selectedReader.Outline(glyph)
                ?? throw new NotSupportedException("The native EPUB glyph outline format is not implemented.");
            // The public advance API rounds to 1000 units; finer font-metric precision is a later slice.
            double width = cluster.Advance * StyleAt(index).FontSize;
            if (character == ' ')
            {
                // Each collapsed separator scans only the following word, keeping lookahead linear.
                double wordWidth = 0;
                for (int next = index + 1; next < text.Length && text[next] is not (' ' or '\n');)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var nextCluster = clusters[next];
                    wordWidth += nextCluster.Advance * StyleAt(next).FontSize;
                    next += nextCluster.Length;
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
            line.Add((index, cluster.Length, left, width, cluster.Font, outline, null, StyleAt(index),
                content.Characters[index]?.BlockStyle ?? content.DefaultStyle, content.Characters[index]?.Node.Owner));
            left += width;
            index += cluster.Length - 1;
        }
        cancellationToken.ThrowIfCancellationRequested();
        FlushLine();
        FinishPage(text.Length);

        EpubNativeContent.Style StyleAt(int index) => content.Characters[index]?.Style ?? content.DefaultStyle;

        Rune ReadScalar(int index)
        {
            if (!Rune.TryGetRuneAt(text, index, out Rune scalar))
                throw new NotSupportedException("Native EPUB layout requires valid Unicode scalar text.");
            int value = scalar.Value;
            if (value is >= ' ' and <= '~') return scalar;
            UnicodeCategory category = Rune.GetUnicodeCategory(scalar);
            if (value is >= 0x20A0 and <= 0x20CF && category == UnicodeCategory.CurrencySymbol)
                return scalar;
            // General punctuation does not enable embedded direction or formatting controls.
            if (value is >= 0x2000 and <= 0x206F
                && category is UnicodeCategory.ConnectorPunctuation or UnicodeCategory.DashPunctuation
                    or UnicodeCategory.OpenPunctuation or UnicodeCategory.ClosePunctuation
                    or UnicodeCategory.InitialQuotePunctuation or UnicodeCategory.FinalQuotePunctuation
                    or UnicodeCategory.OtherPunctuation) return scalar;
            // Unshaped LTR text is limited to precomposed Latin, Greek, Cyrillic, and mathematical alphanumerics.
            if ((value is >= 0x00A0 and <= 0x02FF or >= 0x0370 and <= 0x052F
                    or >= 0x1E00 and <= 0x1FFF
                    || value is >= 0x1D400 and <= 0x1D7FF
                        && category is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter
                            or UnicodeCategory.DecimalDigitNumber)
                && category is not (UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark
                    or UnicodeCategory.EnclosingMark or UnicodeCategory.Format or UnicodeCategory.Control
                    or UnicodeCategory.OtherNotAssigned)) return scalar;
            throw new NotSupportedException($"Native EPUB layout requires unsupported shaping or bidirectional processing for U+{value:X4}.");
        }

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
                ascent = Math.Max(ascent, item.Image?.Height ?? AboveBaseline(item.Style, item.Font));
                descent = Math.Max(descent, item.Image is null ? BelowBaseline(item.Style, item.Font) : 0);
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
            if (block.Background?.Color is { } paragraphBackground)
                blockBackgrounds.Add(new NativeRectanglePaint(options.Margin, top, contentWidth, height, paragraphBackground));
            var fragments = new List<(EpubNativeContent.Element Owner, double Left, double Right, double Top, double Bottom)>();
            var fragmentIndices = new Dictionary<EpubNativeContent.Element, int>(ReferenceEqualityComparer.Instance);
            var ancestors = new Stack<EpubNativeContent.Element>();
            foreach (var item in line)
            {
                for (var owner = item.Owner; owner is not null && owner.Name != "p"; owner = owner.Parent)
                    if (owner.Style.Background?.Color is not null) ancestors.Push(owner);
                while (ancestors.TryPop(out var owner))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var selected = FontFor(owner.Style);
                    double fragmentTop = baseline - selected.Ascender * owner.Style.FontSize / selected.UnitsPerEm;
                    double fragmentBottom = baseline - selected.Descender * owner.Style.FontSize / selected.UnitsPerEm;
                    if (item.Image is { } image) fragmentTop = Math.Min(fragmentTop, baseline - image.Height);
                    if (fragmentIndices.TryGetValue(owner, out int at))
                    {
                        var fragment = fragments[at];
                        fragments[at] = (owner, fragment.Left, item.Left + item.Width,
                            Math.Min(fragment.Top, fragmentTop), Math.Max(fragment.Bottom, fragmentBottom));
                    }
                    else
                    {
                        fragmentIndices.Add(owner, fragments.Count);
                        fragments.Add((owner, item.Left, item.Left + item.Width, fragmentTop, fragmentBottom));
                    }
                }
            }
            foreach (var fragment in fragments)
            {
                int depth = 0;
                for (var owner = fragment.Owner; owner.Name != "p" && owner.Parent is { } parent; owner = parent) depth++;
                inlineBackgrounds.Add((depth, new NativeRectanglePaint(fragment.Left, fragment.Top, fragment.Right - fragment.Left,
                    fragment.Bottom - fragment.Top, fragment.Owner.Style.Background!.Value.Color!.Value)));
            }
            foreach (var item in line)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (item.Image is { } image)
                {
                    glyphs.Add(new NativeImagePaint(image, item.Left, baseline - image.Height));
                    continue;
                }
                double size = item.Style.FontSize;
                double logicalTop = baseline - item.Font.Ascender * size / item.Font.UnitsPerEm;
                placements.Add(new EpubTextPlacement(spineIndex, item.Index, item.Length, item.Left, logicalTop, item.Width, size)
                {
                    Color = item.Style.Color
                });
                if (item.Outline!.Contours.Count != 0)
                    glyphs.Add(new NativeGlyphPaint(item.Outline, item.Left, baseline, size / 1000, item.Style.Color));
            }
            if (blockBackgrounds.Count + inlineBackgrounds.Count + glyphs.Count > 100_000)
                throw new InvalidDataException("The native page exceeds its paint limit.");
            top += height;
            line.Clear();
        }

        double AboveBaseline(EpubNativeContent.Style style, TrueTypeFont? selected = null)
        {
            selected ??= FontFor(style);
            return selected.Ascender * style.FontSize / selected.UnitsPerEm + HalfLeading(style, selected);
        }

        double BelowBaseline(EpubNativeContent.Style style, TrueTypeFont? selected = null)
        {
            selected ??= FontFor(style);
            return -selected.Descender * style.FontSize / selected.UnitsPerEm + HalfLeading(style, selected);
        }

        double HalfLeading(EpubNativeContent.Style style, TrueTypeFont selected) =>
            style.LineHeight is double height
                ? (height - (selected.Ascender - selected.Descender) * style.FontSize / selected.UnitsPerEm) / 2
                : 0;

        void FinishPage(int end)
        {
            var paints = blockBackgrounds.Concat(inlineBackgrounds.OrderBy(fill => fill.Depth).Select(fill => fill.Paint))
                .Concat(glyphs);
            pages.Add(new EpubLayoutPage(text[pageStart..end], pageStart, spineIndex, placements, paints,
                content.CanvasBackground, placements.Select(placement => clusters[placement.Start])));
            pageStart = end;
            placements.Clear();
            glyphs.Clear();
            blockBackgrounds.Clear();
            inlineBackgrounds.Clear();
        }
    }

}
