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
        var faceFonts = new Dictionary<EpubFontFaceRequest, EpubFontFaceResult?>();
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

        TrueTypeFont? ResolveFamily(EpubFontFamily? family, double weight)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (options.FontFaceResolver is { } resolveFace)
            {
                var request = new EpubFontFaceRequest(family, weight, options.DefaultFont);
                var faceKey = request with { Family = family is null ? null : family with { Name = family.Name.ToUpperInvariant() } };
                if (faceFonts.TryGetValue(faceKey, out var cachedFace)) return cachedFace?.Font;
                if (faceFonts.Count >= 256)
                    throw new InvalidDataException("Native EPUB exceeds its 256 font-face lookup limit.");
                var result = resolveFace(request);
                cancellationToken.ThrowIfCancellationRequested();
                if (result is not null)
                {
                    if (result.Font is null || !double.IsFinite(result.ActualWeight) || result.ActualWeight is < 1 or > 1000
                        || !Enum.IsDefined(result.Match) || (result.Match == EpubFontMatch.Exact) != (result.ActualWeight == weight))
                        throw new InvalidDataException("The native EPUB font resolver returned an inconsistent face match.");
                    RetainFace(result.Font);
                }
                faceFonts.Add(faceKey, result);
                return result?.Font;
            }
            if (weight != 400 || family is null || options.FontFamilyResolver is null) return null;
            var key = family with { Name = family.Name.ToUpperInvariant() };
            if (familyFonts.TryGetValue(key, out var cached)) return cached;
            if (familyFonts.Count >= 256)
                throw new InvalidDataException("Native EPUB exceeds its 256 font-family lookup limit.");
            var selected = options.FontFamilyResolver(family);
            cancellationToken.ThrowIfCancellationRequested();
            if (selected is not null) RetainFace(selected);
            familyFonts.Add(key, selected);
            return selected;
        }

        void RetainFace(TrueTypeFont selected)
        {
            if (retainedFonts.Add(selected))
            {
                retainedFontBytes += selected.FontData.Length;
                if (retainedFonts.Count > 64 || retainedFontBytes > 64L << 20)
                    throw new InvalidDataException("Native EPUB exceeds its retained font limit.");
            }
        }
    }

    private void AppendNativeSpinePages(int spineIndex, EpubLayoutOptions options, double contentWidth,
        TrueTypeOutlineReader reader, List<EpubLayoutPage> pages,
        List<(int SpineIndex, string Id, int Offset)> anchors, ref int remainingTextLength,
        ref int remainingStylesheetBytes, ref int remainingStylesheetReferences,
        Dictionary<string, PngDecodedImage> imageCache, ref long retainedImageBytes,
        Func<EpubFontFamily?, double, TrueTypeFont?> resolveFamily, CancellationToken cancellationToken)
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
        var formatting = new EpubFormattingTree(content, cancellationToken);
        var bodyMargins = content.ResolveBodyMargins(contentWidth);
        var bodyPadding = content.ResolveBodyPadding(contentWidth);
        double contentLeft = options.Margin + bodyMargins.Left + bodyPadding.Left;
        double contentRight = options.PageWidth - options.Margin - bodyMargins.Right - bodyPadding.Right;
        contentWidth = contentRight - contentLeft;
        if (contentWidth <= 0 || bodyMargins.Top + bodyPadding.Top >= options.PageHeight - 2 * options.Margin)
            throw new NotSupportedException("Native EPUB body padding requires a positive content box within the page.");
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
        var substitutions = new Dictionary<(TrueTypeFont Font, bool SmallCaps), OpenTypeGlyphSubstitution>();
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
            bool smallCaps = (owner?.Style ?? content.DefaultStyle).SmallCaps;
            font = FontFor(owner?.Style ?? content.DefaultStyle, ReadScalar(runStart).Value);
            bool latin = IsLatin(runStart);
            while (runEnd < text.Length && text[runEnd] != '\n'
                && ReferenceEquals(content.Characters[runEnd]?.Node.Owner, owner)
                && ReferenceEquals(formatting.TextSequences.GetValueOrDefault(runEnd), formatting.TextSequences.GetValueOrDefault(runStart))
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
            if (!latin && smallCaps && run.Any(cluster => Rune.GetUnicodeCategory(ReadScalar(cluster.Start))
                    is UnicodeCategory.LowercaseLetter or UnicodeCategory.UppercaseLetter or UnicodeCategory.TitlecaseLetter))
                throw new NotSupportedException("Native EPUB small capitals for this script are not implemented.");
            var shaped = run.Select(cluster => (cluster.Start, cluster.Length,
                Glyph: cluster.Glyphs[0].GlyphId, cluster.Substituted)).ToList();
            if (latin)
                foreach (var lookup in Substitution(font, smallCaps).Lookups)
                {
                    var selected = new List<(int Start, int Length, ushort Glyph, bool Substituted)>(shaped.Count);
                    for (int at = 0; at < shaped.Count; at++)
                    {
                        ChargeShapingWork();
                        bool replaced = false;
                        foreach (var rule in lookup)
                        {
                            ChargeShapingWork();
                            if (rule.Components.Length > shaped.Count - at) continue;
                            bool match = true;
                            int start = shaped[at].Start, end = start + shaped[at].Length;
                            for (int c = 0; c < rule.Components.Length; c++)
                            {
                                ChargeShapingWork();
                                var input = shaped[at + c];
                                if (input.Glyph != rule.Components[c]) { match = false; break; }
                                start = Math.Min(start, input.Start);
                                end = Math.Max(end, input.Start + input.Length);
                            }
                            if (!match) continue;
                            foreach (ushort output in rule.Outputs)
                            {
                                ChargeShapingWork();
                                selected.Add((start, end - start, output, true));
                            }
                            at += rule.Components.Length - 1;
                            replaced = true;
                            break;
                        }
                        if (!replaced) selected.Add(shaped[at]);
                    }
                    shaped = selected;
                }
            for (int at = 0; at < shaped.Count;)
            {
                var first = shaped[at];
                int end = first.Start + first.Length;
                bool substituted = false;
                var output = new List<EpubClusterGlyph>();
                do
                {
                    ChargeShapingWork();
                    var item = shaped[at++];
                    end = Math.Max(end, item.Start + item.Length);
                    substituted |= item.Substituted;
                    output.Add(new(item.Glyph, font.GetAdvanceWidth(item.Glyph) / (double)font.UnitsPerEm, 0, 0));
                } while (at < shaped.Count && shaped[at].Start < end);
                clusters.Add(first.Start, new(first.Start, end - first.Start, font, output, substituted));
            }
            runStart = runEnd;
        }
        void ChargeShapingWork()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++shapingWork > 1_000_000)
                throw new InvalidDataException("The EPUB GSUB shaping work limit was exceeded.");
        }
        bool IsLatin(int at) => text[at] is >= 'A' and <= 'Z' or >= 'a' and <= 'z'
            or >= '\u00C0' and <= '\u02FF' or >= '\u1E00' and <= '\u1EFF'
            or >= '\uFB00' and <= '\uFB06';
        EpubGlyphCluster Cluster(int start, int length, ushort glyph, bool substituted = false) => new(start, length, font,
            [new(glyph, font.GetAdvanceWidth(glyph) / (double)font.UnitsPerEm, 0, 0)], substituted);
        OpenTypeGlyphSubstitution Substitution(TrueTypeFont selected, bool smallCaps)
        {
            if (!substitutions.TryGetValue((selected, smallCaps), out var value))
                substitutions.Add((selected, smallCaps), value = new OpenTypeGlyphSubstitution(selected, cancellationToken, smallCaps));
            if (smallCaps && !value.HasSmallCaps)
                throw new NotSupportedException("Native EPUB synthetic small capitals are not implemented for fonts without the selected feature.");
            return value;
        }
        TrueTypeFont FontFor(EpubNativeContent.Style style, int? scalar = null)
        {
            foreach (var family in style.FontFamilies ?? [])
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++fontWork > 1_000_000)
                    throw new InvalidDataException("Native EPUB font matching exceeds its work limit.");
                if (resolveFamily(family, style.FontWeight) is { } selected
                    && (scalar is null || selected.GetGlyphId(scalar.Value) != 0)) return selected;
            }
            if (options.FontFaceResolver is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++fontWork > 1_000_000)
                    throw new InvalidDataException("Native EPUB font matching exceeds its work limit.");
                if (resolveFamily(null, style.FontWeight) is { } selected
                    && (scalar is null || selected.GetGlyphId(scalar.Value) != 0)) return selected;
            }
            if (style.FontWeight != 400)
                throw new NotSupportedException("Native EPUB has no resolved font face for the requested weight.");
            return options.DefaultFont;
        }
        var placements = new List<EpubTextPlacement>(text.Length);
        var glyphs = new List<NativePagePaint>(text.Length);
        var backgrounds = new List<(int Depth, NativePagePaint Paint)>();
        int pageStart = 0;
        bool pageHasContent = false;
        double left = contentLeft;
        double top = options.Margin + bodyMargins.Top + bodyPadding.Top;
        var line = new List<(int Index, int Length, double Left, double Width, TrueTypeFont Font, PdfGlyphOutline? Outline,
            PngDecodedImage? Image, EpubNativeContent.Style Style, EpubNativeContent.Style BlockStyle,
            EpubNativeContent.Element? Owner)>();
        int lineStart = 0;
        EpubFormattingTree.Block lineBlock = formatting.Root;
        var startedBlocks = new HashSet<EpubFormattingTree.Block>();
        var activeBounds = new Dictionary<EpubFormattingTree.Block, (double Left, double Width)>();
        bool pendingParagraphGap = false;
        long imageBytes = retainedImageBytes;
        LayoutBlock(formatting.Root);
        retainedImageBytes = imageBytes;
        cancellationToken.ThrowIfCancellationRequested();
        if (bodyPadding.Bottom > 0 && top + bodyPadding.Bottom > options.PageHeight - options.Margin)
            throw new NotSupportedException("Native EPUB terminal body padding overflow is not implemented.");
        FinishPage(text.Length);

        void LayoutBlock(EpubFormattingTree.Block block)
        {
            double parentLeft = contentLeft, parentRight = contentRight, parentWidth = contentWidth;
            var margins = block.Parent is null ? (Left: 0d, Right: 0d) : block.Source.HorizontalInsets(parentWidth);
            var padding = block.Parent is null ? (Left: 0d, Right: 0d) : block.Source.HorizontalInsets(parentWidth, padding: true);
            contentLeft += margins.Left;
            contentRight -= margins.Right;
            var backgroundBounds = (Left: contentLeft, Width: contentRight - contentLeft);
            contentLeft += padding.Left;
            contentRight -= padding.Right;
            contentWidth = contentRight - contentLeft;
            if (contentWidth <= 0)
                throw new NotSupportedException("Native EPUB paragraph insets require a positive content box.");
            activeBounds.Add(block, backgroundBounds);
            try { LayoutBlockContent(block); }
            finally
            {
                activeBounds.Remove(block);
                contentLeft = parentLeft;
                contentRight = parentRight;
                contentWidth = parentWidth;
            }
        }

        void LayoutBlockContent(EpubFormattingTree.Block block)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double indent = block.Source.Style.TextIndentPercentage
                ? block.Source.Style.TextIndent * contentWidth / 100 : block.Source.Style.TextIndent;
            if (!double.IsFinite(indent) || Math.Abs(indent) > 32768)
                throw new NotSupportedException("Native EPUB text indentation exceeds the supported page dimensions.");
            int boundary = Math.Min(block.Source.Offset, text.Length);
            while (boundary < text.Length && content.Characters[boundary] is null) boundary++;
            if (block.Parent is not null && block.Source.Style.PageBreakBefore && pageHasContent)
            {
                FinishPage(boundary);
                top = options.Margin;
                pendingParagraphGap = false;
            }
            if (block.Source.Name == "p" && pageHasContent) pendingParagraphGap = true;
            if (block.Replaced is { Source: { } source } replaced)
            {
                lineBlock = block;
                var image = ReadNativeImage(resource.Href, source, imageCache, ref imageBytes, cancellationToken);
                if (image.Width > contentWidth || image.Height > options.PageHeight - 2 * options.Margin)
                    throw new NotSupportedException("The native EPUB block image exceeds the available page dimensions.");
                if (pendingParagraphGap)
                {
                    if (pageHasContent) top += options.FontSize;
                    pendingParagraphGap = false;
                }
                if (top + image.Height > options.PageHeight - options.Margin)
                {
                    FinishPage(replaced.Offset);
                    top = options.Margin;
                }
                AddBlockBackgrounds(image.Height);
                glyphs.Add(new NativeImagePaint(image, contentLeft, top));
                if (backgrounds.Count + glyphs.Count > 100_000)
                    throw new InvalidDataException("The native page exceeds its paint limit.");
                top += image.Height;
                pageHasContent = true;
                return;
            }
            foreach (var part in block.Children)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (part is EpubFormattingTree.Block child)
                {
                    LayoutBlock(child);
                    continue;
                }
                lineBlock = block;
                var sequence = (EpubFormattingTree.InlineSequence)part;
                lineStart = sequence.Atoms[0].Offset;
                left = contentLeft + (startedBlocks.Contains(block) ? 0 : indent);
                LayoutInline(sequence);
                FlushLine();
            }
            if (block.Source.Name == "p" && pageHasContent) pendingParagraphGap = true;
        }

        void LayoutInline(EpubFormattingTree.InlineSequence sequence)
        {
            bool firstIndentedWord = !startedBlocks.Contains(lineBlock) && left > contentLeft;
            int first = 0, last = sequence.Atoms.Count - 1;
            while (first <= last && IsSpace(sequence.Atoms[first])) first++;
            while (last >= first && IsSpace(sequence.Atoms[last])) last--;
            if (first <= last) lineStart = sequence.Atoms[first].Offset;
            for (int at = first; at <= last; at++)
            {
                var atom = sequence.Atoms[at];
                int index = atom.Offset;
                cancellationToken.ThrowIfCancellationRequested();
                if (pendingParagraphGap)
                {
                    if (pageHasContent) top += options.FontSize;
                    pendingParagraphGap = false;
                }
                if (atom.Reference is { } reference)
                {
                    if (reference.Source is null)
                    {
                        firstIndentedWord = false;
                        if (line.Count == 0)
                        {
                            var blockStyle = lineBlock.Source.Style;
                            double height = Math.Max(AboveBaseline(blockStyle), AboveBaseline(reference.Owner.Style))
                                + Math.Max(BelowBaseline(blockStyle), BelowBaseline(reference.Owner.Style));
                            if (height > options.PageHeight - 2 * options.Margin)
                                throw new NotSupportedException("The native EPUB line exceeds the available page height.");
                            if (top + height > options.PageHeight - options.Margin)
                            {
                                FinishPage(index);
                                top = options.Margin;
                            }
                            AddBlockBackgrounds(height);
                            if (backgrounds.Count + glyphs.Count > 100_000)
                                throw new InvalidDataException("The native page exceeds its paint limit.");
                            pageHasContent = true;
                            startedBlocks.Add(lineBlock);
                            top += height;
                        }
                        else FlushLine();
                        left = contentLeft;
                        lineStart = index < text.Length && text[index] == '\n' ? index + 1 : index;
                        continue;
                    }
                    var image = ReadNativeImage(resource.Href, reference.Source, imageCache, ref imageBytes, cancellationToken);
                    if (left + image.Width > contentRight)
                        throw new NotSupportedException("The native EPUB image exceeds the available line width.");
                    line.Add((index, 0, left, image.Width, FontFor(reference.Owner.Style), null, image,
                        reference.Owner.Style, lineBlock.Source.Style, reference.Owner));
                    left += image.Width;
                    firstIndentedWord = false;
                    continue;
                }
                // Ligature and surrogate continuations keep their source indices but have no separate placement.
                EpubGlyphCluster? cluster;
                var style = atom.Owner.Style;
                if (atom.HasSource)
                {
                    if (!clusters.TryGetValue(index, out cluster)) continue;
                }
                else
                {
                    var spaceFont = FontFor(style);
                    ushort spaceGlyph = spaceFont.GetGlyphId(' ');
                    cluster = new(index, 0, spaceFont,
                        [new(spaceGlyph, spaceFont.GetAdvanceWidth(spaceGlyph) / (double)spaceFont.UnitsPerEm, 0, 0)]);
                }
                char character = atom.IsSpace ? ' ' : text[index];
                if (atom.IsSpace && line.Count == 0) continue;
                ushort glyph = cluster.Glyphs[0].GlyphId;
                if (glyph == 0 && character != ' ')
                    throw new NotSupportedException("The native EPUB font has no glyph for this cluster.");
                if (!readers.TryGetValue(cluster.Font, out var selectedReader))
                    readers.Add(cluster.Font, selectedReader = new TrueTypeOutlineReader(cluster.Font));
                PdfGlyphOutline outline = selectedReader.Outline(glyph)
                    ?? throw new NotSupportedException("The native EPUB glyph outline format is not implemented.");
                double width = cluster.Advance * style.FontSize;
                if (character == ' ')
                {
                    firstIndentedWord = false;
                    // Each collapsed separator scans only the following word, keeping lookahead linear.
                    double wordWidth = 0;
                    for (int next = at + 1; next <= last; next++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var nextAtom = sequence.Atoms[next];
                        if (nextAtom.Reference is not null || nextAtom.IsSpace) break;
                        if (clusters.TryGetValue(nextAtom.Offset, out var nextCluster))
                            wordWidth += nextCluster.Advance * StyleAt(nextAtom.Offset).FontSize;
                    }
                    if (wordWidth > contentWidth)
                        throw new NotSupportedException("The native EPUB word exceeds the available line width.");
                    if (left + width + wordWidth > contentRight)
                    {
                        FlushLine(finalLine: false);
                        if (top + options.FontSize > options.PageHeight - options.Margin)
                        {
                            FinishPage(at < last ? sequence.Atoms[at + 1].Offset : text.Length);
                            top = options.Margin;
                        }
                        left = contentLeft;
                        lineStart = at < last ? sequence.Atoms[at + 1].Offset : text.Length;
                        continue;
                    }
                }
                if (left + width > contentRight && !firstIndentedWord)
                    throw new NotSupportedException("The native EPUB word exceeds the available line width.");
                line.Add((index, cluster.Length, left, width, cluster.Font, atom.HasSource ? outline : null, null, style,
                    lineBlock.Source.Style, atom.Owner));
                left += width;
            }

            bool IsSpace(EpubFormattingTree.Atom atom) => atom.IsSpace;
        }

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
                    or >= 0x1E00 and <= 0x1FFF or >= 0xFB00 and <= 0xFB06
                    || value is >= 0x1D400 and <= 0x1D7FF
                        && category is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter
                            or UnicodeCategory.DecimalDigitNumber)
                && category is not (UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark
                    or UnicodeCategory.EnclosingMark or UnicodeCategory.Format or UnicodeCategory.Control
                    or UnicodeCategory.OtherNotAssigned)) return scalar;
            throw new NotSupportedException($"Native EPUB layout requires unsupported shaping or bidirectional processing for U+{value:X4}.");
        }

        static int SourceDepth(EpubNativeContent.Element source)
        {
            int depth = 0;
            while (source.Parent is { } parent) { depth++; source = parent; }
            return depth;
        }

        void AddBlockBackgrounds(double height)
        {
            for (var box = lineBlock; box.Parent is not null; box = box.Parent)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (box.Source.Style.Background?.Color is { } fill)
                {
                    var bounds = activeBounds[box];
                    backgrounds.Add((SourceDepth(box.Source), new NativeRectanglePaint(bounds.Left, top, bounds.Width, height, fill)));
                }
            }
        }

        void FlushLine(bool finalLine = true)
        {
            while (line.Count > 0 && line[^1].Image is null
                && (line[^1].Length == 0 || line[^1].Length == 1 && text[line[^1].Index] == ' '))
                line.RemoveAt(line.Count - 1);
            if (line.Count == 0) return;
            startedBlocks.Add(lineBlock);
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
            double remainingWidth = contentRight - (line[^1].Left + line[^1].Width);
            double alignmentOffset = block.TextAlign switch
            {
                "center" => remainingWidth / 2,
                "right" => remainingWidth,
                _ => 0
            };
            int spaces = 0;
            if (block.TextAlign == "justify" && !finalLine)
                foreach (var item in line)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (item.Image is null && (item.Length == 0 || item.Length == 1 && text[item.Index] == ' ')) spaces++;
                }
            double extraSpace = spaces == 0 ? 0 : remainingWidth / spaces;
            if (alignmentOffset != 0 || extraSpace != 0)
                for (int at = 0; at < line.Count; at++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var item = line[at];
                    item.Left += alignmentOffset;
                    if (extraSpace != 0 && item.Image is null && (item.Length == 0 || item.Length == 1 && text[item.Index] == ' '))
                    {
                        item.Width += extraSpace;
                        alignmentOffset += extraSpace;
                    }
                    line[at] = item;
                }
            double baseline = top + ascent;
            AddBlockBackgrounds(height);
            var fragments = new List<(EpubNativeContent.Element Owner, double Left, double Right, double Top, double Bottom)>();
            var fragmentIndices = new Dictionary<EpubNativeContent.Element, int>(ReferenceEqualityComparer.Instance);
            var ancestors = new Stack<EpubNativeContent.Element>();
            foreach (var item in line)
            {
                for (var owner = item.Owner; owner is not null && !ReferenceEquals(owner, lineBlock.Source); owner = owner.Parent)
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
                backgrounds.Add((SourceDepth(fragment.Owner), new NativeRectanglePaint(fragment.Left, fragment.Top, fragment.Right - fragment.Left,
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
                if (item.Length == 0) continue;
                double size = item.Style.FontSize;
                double logicalTop = baseline - item.Font.Ascender * size / item.Font.UnitsPerEm;
                placements.Add(new EpubTextPlacement(spineIndex, item.Index, item.Length, item.Left, logicalTop, item.Width, size)
                {
                    Color = item.Style.Color
                });
                var cluster = clusters[item.Index];
                double advance = 0;
                foreach (var selected in cluster.Glyphs)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var outline = readers[item.Font].Outline(selected.GlyphId)
                        ?? throw new NotSupportedException("The native EPUB glyph outline format is not implemented.");
                    // Empty outlines also retain source ownership for explicit PDF conversion.
                    glyphs.Add(new NativeGlyphPaint(outline, item.Left + (advance + selected.OffsetX) * size,
                        baseline - selected.OffsetY * size, size / 1000, item.Style.Color) { SourceOffset = item.Index });
                    if (backgrounds.Count + glyphs.Count > 100_000)
                        throw new InvalidDataException("The native page exceeds its paint limit.");
                    advance += selected.Advance;
                }
            }
            if (backgrounds.Count + glyphs.Count > 100_000)
                throw new InvalidDataException("The native page exceeds its paint limit.");
            top += height;
            pageHasContent = true;
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
            var paints = backgrounds.OrderBy(fill => fill.Depth).Select(fill => fill.Paint)
                .Concat(glyphs);
            pages.Add(new EpubLayoutPage(text[pageStart..end], pageStart, spineIndex, placements, paints,
                content.CanvasBackground, placements.Select(placement => clusters[placement.Start])));
            pageStart = end;
            pageHasContent = false;
            placements.Clear();
            glyphs.Clear();
            backgrounds.Clear();
        }
    }

}
