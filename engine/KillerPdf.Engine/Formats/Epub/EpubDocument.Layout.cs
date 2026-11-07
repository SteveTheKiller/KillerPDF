using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Filters;
using System.Globalization;
using System.Text;
using System.Buffers.Binary;

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
        if (resource.MediaType != "application/xhtml+xml" || Tokens(resource.Properties).Any(property => property != "svg"))
            throw new NotSupportedException("Native EPUB layout requires unscripted XHTML with supported resource properties.");
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
        var substitutions = new Dictionary<(TrueTypeFont Font, bool SmallCaps, bool Ligatures), OpenTypeGlyphSubstitution>();
        var readers = new Dictionary<TrueTypeFont, TrueTypeOutlineReader> { [font] = reader };
        int fontWork = 0;
        int decorationWork = 0;
        int inlineMetricWork = 0;
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
                foreach (var lookup in Substitution(font, smallCaps, (owner?.Style ?? content.DefaultStyle).LetterSpacing == 0).Lookups)
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
        OpenTypeGlyphSubstitution Substitution(TrueTypeFont selected, bool smallCaps, bool ligatures)
        {
            if (!substitutions.TryGetValue((selected, smallCaps, ligatures), out var value))
                substitutions.Add((selected, smallCaps, ligatures), value = new OpenTypeGlyphSubstitution(selected, cancellationToken, smallCaps, ligatures));
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
        var navigationPlacements = new List<EpubTextPlacement>();
        var glyphs = new List<NativePagePaint>(text.Length);
        var backgrounds = new List<(int Depth, NativePagePaint Paint)>();
        int pageStart = 0;
        bool pageHasContent = false;
        bool pendingBreakAfter = false;
        double left = contentLeft;
        double top = options.Margin + bodyMargins.Top + bodyPadding.Top;
        var line = new List<(int Index, int Length, double Left, double Width, TrueTypeFont Font, PdfGlyphOutline? Outline,
            PngDecodedImage? Image, EpubNativeContent.Style Style, EpubNativeContent.Style BlockStyle,
            EpubNativeContent.Element? Owner)>();
        int lineStart = 0;
        EpubFormattingTree.Block lineBlock = formatting.Root;
        var startedBlocks = new HashSet<EpubFormattingTree.Block>();
        var initialPadding = new Dictionary<EpubFormattingTree.Block,
            (double Height, int PaintStart, double BorderHeight, PdfRgbColor? BorderColor)>();
        var activeBounds = new Dictionary<EpubFormattingTree.Block, (double Left, double Width, double ContainingWidth)>();
        double pendingParagraphGap = 0;
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
            if (block.Source.Width is { Length: { } specifiedWidth })
            {
                double width = specifiedWidth.Percentage ? parentWidth * specifiedWidth.Value / 100
                    : specifiedWidth.Em ? block.Source.Style.FontSize * specifiedWidth.Value : specifiedWidth.Value;
                if (!double.IsFinite(width) || width < 0 || width > 32768)
                    throw new NotSupportedException("Native EPUB block width exceeds the supported dimensions.");
                bool autoLeft = block.Source.Margins?[3] is { Auto: true };
                bool autoRight = block.Source.Margins?[1] is { Auto: true };
                if (autoLeft)
                {
                    double remaining = Math.Max(0, parentWidth - margins.Left - margins.Right
                        - padding.Left - padding.Right - width);
                    contentLeft += autoRight ? remaining / 2 : remaining;
                }
                contentRight = contentLeft + padding.Left + width + padding.Right;
            }
            var backgroundBounds = (Left: contentLeft, Width: contentRight - contentLeft);
            contentLeft += padding.Left;
            contentRight -= padding.Right;
            contentWidth = contentRight - contentLeft;
            if (contentWidth <= 0)
                throw new NotSupportedException("Native EPUB paragraph insets require a positive content box.");
            activeBounds.Add(block, (backgroundBounds.Left, backgroundBounds.Width, parentWidth));
            try
            {
                if (block.Parent is not null && pageHasContent
                    && (block.Source.Style.PageBreakBefore || pendingBreakAfter && HasFlowContent(block)))
                {
                    int boundary = Math.Min(block.Source.Offset, text.Length);
                    while (boundary < text.Length && content.Characters[boundary] is null) boundary++;
                    FinishPage(boundary);
                    top = options.Margin;
                    pendingParagraphGap = 0;
                    pendingBreakAfter = false;
                }
                double paddingTop = block.Parent is null ? 0 : block.Source.VerticalMargin(0, parentWidth, 0, padding: true);
                double paddingBottom = block.Parent is null ? 0 : block.Source.VerticalMargin(2, parentWidth, 0, padding: true);
                var topBorder = block.Source.TopBorder;
                double borderTop = topBorder is { Visible: true } side
                    ? side.Width.Value * (side.Width.Em ? block.Source.Style.FontSize : 1) : 0;
                if (!double.IsFinite(borderTop) || borderTop > 32768)
                    throw new NotSupportedException("Native EPUB top border exceeds the supported dimensions.");
                if ((paddingTop > 0 || paddingBottom > 0 || borderTop > 0) && block.Source.Name == "p")
                {
                    pendingParagraphGap = Math.Max(pendingParagraphGap, block.Source.VerticalMargin(0,
                        activeBounds[block].ContainingWidth, pageHasContent ? options.FontSize : 0));
                    top += !pageHasContent && pages.Count == 0 && bodyPadding.Top == 0
                        ? Math.Max(0, pendingParagraphGap - bodyMargins.Top) : pendingParagraphGap;
                    pendingParagraphGap = 0;
                }
                int initialPaintStart = backgrounds.Count;
                PdfRgbColor? borderColor = block.Source.Style.Hidden ? null : topBorder is { Color: { } specified }
                    ? specified.Color : block.Source.Style.Color ?? new PdfRgbColor(0, 0, 0);
                double emptyBorderRemaining = 0;
                if (block.Children.Count == 0 && block.Replaced is null)
                {
                    emptyBorderRemaining = borderTop;
                    paddingBottom += paddingTop + borderTop;
                    paddingTop = 0;
                    borderTop = 0;
                }
                if (borderTop > 0)
                {
                    PaintPadding(borderTop);
                    if (borderColor is { } fill)
                        backgrounds.Add((SourceDepth(block.Source), new NativeRectanglePaint(
                            backgroundBounds.Left, top, backgroundBounds.Width, borderTop, fill)));
                    top += borderTop;
                }
                if (paddingTop + borderTop > 0)
                    initialPadding[block] = (paddingTop + borderTop, initialPaintStart, borderTop, borderColor);
                PaintPadding(paddingTop);
                top += paddingTop;
                LayoutBlockContent(block);
                double remainingPadding = paddingBottom;
                int? paddingEnd = null;
                while (remainingPadding > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    double available = Math.Max(0, options.PageHeight - options.Margin - top);
                    double fragment = Math.Min(remainingPadding, available);
                    PaintPadding(fragment);
                    double borderFragment = Math.Min(fragment, emptyBorderRemaining);
                    if (borderFragment > 0 && borderColor is { } emptyFill)
                        backgrounds.Add((SourceDepth(block.Source), new NativeRectanglePaint(
                            backgroundBounds.Left, top, backgroundBounds.Width, borderFragment, emptyFill)));
                    emptyBorderRemaining -= borderFragment;
                    top += fragment;
                    remainingPadding -= fragment;
                    if (remainingPadding <= 0) break;
                    if (paddingEnd is null)
                    {
                        int end = Math.Min(block.Source.Offset, text.Length);
                        while (end < text.Length)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (content.Characters[end] is { } character)
                            {
                                var owner = character.Node.Owner;
                                while (owner is not null && owner != block.Source) owner = owner.Parent;
                                if (owner is null) break;
                            }
                            end++;
                        }
                        paddingEnd = end;
                    }
                    FinishPage(Math.Max(pageStart, paddingEnd.Value));
                    top = options.Margin;
                }

                void PaintPadding(double height)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (height <= 0) return;
                    for (var box = block; box.Parent is not null; box = box.Parent)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (box.Source.Style.Hidden || box.Source.Style.Background?.Color is not { } fill) continue;
                        if (backgrounds.Count + glyphs.Count >= 100_000)
                            throw new InvalidDataException("The native page exceeds its paint limit.");
                        var bounds = activeBounds[box];
                        backgrounds.Add((SourceDepth(box.Source), new NativeRectanglePaint(
                            bounds.Left, top, bounds.Width, height, fill)));
                    }
                }
            }
            finally
            {
                initialPadding.Remove(block);
                activeBounds.Remove(block);
                contentLeft = parentLeft;
                contentRight = parentRight;
                contentWidth = parentWidth;
            }
            if (block.Parent is not null && block.Source.Style.PageBreakAfter)
                pendingBreakAfter = true;
        }

        bool HasFlowContent(EpubFormattingTree.Block block)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (block.Replaced is { } replaced)
                return replaced.Owner.MaximumWidth is not { Length.Value: 0 };
            if (block.Source.Padding is { } padding && (padding[0] is { Value: > 0 } || padding[2] is { Value: > 0 })
                || block.Source.TopBorder is { Visible: true, Width.Value: > 0 }) return true;
            foreach (var child in block.Children)
            {
                if (child is EpubFormattingTree.InlineSequence sequence)
                    foreach (var atom in sequence.Atoms)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!atom.IsSpace && (atom.Reference is not { Source: not null } image
                            || image.Owner.MaximumWidth is not { Length.Value: 0 })) return true;
                    }
                if (child is EpubFormattingTree.Block nested && HasFlowContent(nested)) return true;
            }
            return false;
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
            if (block.Source.Name == "p" && block.Source.VerticalMargin(0,
                activeBounds[block].ContainingWidth, 0, padding: true) == 0
                && block.Source.VerticalMargin(2, activeBounds[block].ContainingWidth, 0, padding: true) == 0
                && block.Source.TopBorder is not { Visible: true, Width.Value: > 0 })
            {
                pendingParagraphGap = Math.Max(pendingParagraphGap, block.Source.VerticalMargin(0,
                    activeBounds[block].ContainingWidth, pageHasContent ? options.FontSize : 0));
            }
            if (block.Replaced is { Source: { } source } replaced)
            {
                lineBlock = block;
                var image = ReadNativeImage(resource.Href, source, imageCache, ref imageBytes, cancellationToken);
                var imageSize = ImageSize(image, replaced.Owner);
                double paintLeft = contentLeft, paintTop = 0, flowHeight = imageSize.Height;
                if (replaced.Owner.SvgViewBox is { } viewBox)
                {
                    flowHeight = options.PageHeight - 2 * options.Margin - bodyMargins.Top
                        - bodyMargins.Bottom - bodyPadding.Top - bodyPadding.Bottom;
                    if (!double.IsFinite(flowHeight) || flowHeight <= 0)
                        throw new NotSupportedException("Native EPUB SVG covers require a positive body viewport.");
                    double viewportScale = Math.Min(contentWidth / viewBox.Width, flowHeight / viewBox.Height);
                    double imageScale = Math.Min(viewBox.Width / image.Width, viewBox.Height / image.Height) * viewportScale;
                    imageSize = (image.Width * imageScale, image.Height * imageScale);
                    paintLeft += (contentWidth - imageSize.Width) / 2;
                    paintTop = (flowHeight - imageSize.Height) / 2;
                }
                if (imageSize.Width == 0) return;
                if (imageSize.Width > contentWidth || imageSize.Height > options.PageHeight - 2 * options.Margin)
                    throw new NotSupportedException("The native EPUB block image exceeds the available page dimensions.");
                if (pendingParagraphGap > 0)
                {
                    top += !pageHasContent && pages.Count == 0 && bodyPadding.Top == 0
                        ? Math.Max(0, pendingParagraphGap - bodyMargins.Top) : pendingParagraphGap;
                    pendingParagraphGap = 0;
                }
                if (top + flowHeight > options.PageHeight - options.Margin)
                {
                    FinishPage(replaced.Offset);
                    top = options.Margin;
                }
                AddBlockBackgrounds(flowHeight);
                if (!replaced.Owner.Style.Hidden)
                    glyphs.Add(new NativeImagePaint(image, paintLeft, top + paintTop) { Width = imageSize.Width, Height = imageSize.Height });
                if (backgrounds.Count + glyphs.Count > 100_000)
                    throw new InvalidDataException("The native page exceeds its paint limit.");
                top += flowHeight;
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
            if (block.Source.Name == "p")
            {
                bool hasExtent = pageHasContent
                    || block.Source.TopBorder is { Visible: true, Width.Value: > 0 }
                    || block.Source.VerticalMargin(0, activeBounds[block].ContainingWidth, 0, padding: true) > 0
                    || block.Source.VerticalMargin(2, activeBounds[block].ContainingWidth, 0, padding: true) > 0;
                pendingParagraphGap = Math.Max(pendingParagraphGap, block.Source.VerticalMargin(2,
                    activeBounds[block].ContainingWidth, hasExtent ? options.FontSize : 0));
            }
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
                if (pendingParagraphGap > 0)
                {
                    top += !pageHasContent && pages.Count == 0 && bodyPadding.Top == 0
                        ? Math.Max(0, pendingParagraphGap - bodyMargins.Top) : pendingParagraphGap;
                    pendingParagraphGap = 0;
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
                            FitLine(height, index);
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
                    var imageSize = ImageSize(image, reference.Owner);
                    if (imageSize.Width == 0) continue;
                    double imageTracking = line.Count == 0 || line[^1].Image is not null ? 0
                        : (line[^1].Style.LetterSpacing + reference.Owner.Style.LetterSpacing) / 2;
                    if (imageTracking + imageSize.Width > contentRight - left)
                        throw new NotSupportedException("The native EPUB image exceeds the available line width.");
                    left += imageTracking;
                    line.Add((index, 0, left, imageSize.Width, FontFor(reference.Owner.Style), null, image,
                        reference.Owner.Style, lineBlock.Source.Style, reference.Owner));
                    left += imageSize.Width;
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
                if (character == ' ') width += style.WordSpacing;
                double tracking = line.Count == 0 ? 0 : (line[^1].Style.LetterSpacing + style.LetterSpacing) / 2;
                if (character == ' ')
                {
                    firstIndentedWord = false;
                    // Each collapsed separator scans only the following word, keeping lookahead linear.
                    double wordWidth = 0;
                    double leadingTracking = 0, previousTracking = style.LetterSpacing;
                    bool firstCharacter = true;
                    for (int next = at + 1; next <= last; next++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var nextAtom = sequence.Atoms[next];
                        if (nextAtom.Reference is not null || nextAtom.IsSpace) break;
                        if (clusters.TryGetValue(nextAtom.Offset, out var nextCluster))
                        {
                            var nextStyle = StyleAt(nextAtom.Offset);
                            double between = (previousTracking + nextStyle.LetterSpacing) / 2;
                            if (firstCharacter) leadingTracking = between;
                            else wordWidth += between;
                            wordWidth += nextCluster.Advance * nextStyle.FontSize;
                            previousTracking = nextStyle.LetterSpacing;
                            firstCharacter = false;
                        }
                    }
                    if (wordWidth > contentWidth)
                        throw new NotSupportedException("The native EPUB word exceeds the available line width.");
                    if (tracking + width + leadingTracking + wordWidth > contentRight - left)
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
                if (tracking + width > contentRight - left && !firstIndentedWord)
                    throw new NotSupportedException("The native EPUB word exceeds the available line width.");
                left += tracking;
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
                if (!box.Source.Style.Hidden && box.Source.Style.Background?.Color is { } fill)
                {
                    var bounds = activeBounds[box];
                    backgrounds.Add((SourceDepth(box.Source), new NativeRectanglePaint(bounds.Left, top, bounds.Width, height, fill)));
                }
            }
        }

        void FitLine(double height, int start)
        {
            if (pendingBreakAfter)
            {
                if (pageHasContent)
                {
                    FinishPage(start);
                    top = options.Margin;
                    pendingParagraphGap = 0;
                }
                pendingBreakAfter = false;
            }
            if (top + height > options.PageHeight - options.Margin)
            {
                bool movePadding = initialPadding.TryGetValue(lineBlock, out var initial)
                    && !startedBlocks.Contains(lineBlock);
                if (movePadding && backgrounds.Count > initial.PaintStart)
                    backgrounds.RemoveRange(initial.PaintStart, backgrounds.Count - initial.PaintStart);
                void PaintInitial(double fragment, double offset)
                {
                    AddBlockBackgrounds(fragment);
                    double borderFragment = Math.Min(fragment, Math.Max(0, initial.BorderHeight - offset));
                    if (borderFragment > 0 && initial.BorderColor is { } color)
                    {
                        var bounds = activeBounds[lineBlock];
                        backgrounds.Add((SourceDepth(lineBlock.Source), new NativeRectanglePaint(
                            bounds.Left, top, bounds.Width, borderFragment, color)));
                    }
                }
                if (movePadding && initial.Height + height > options.PageHeight - 2 * options.Margin)
                {
                    top -= initial.Height;
                    double remaining = initial.Height;
                    while (remaining > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        double fragment = Math.Min(remaining, Math.Max(0, options.PageHeight - options.Margin - top));
                        PaintInitial(fragment, initial.Height - remaining);
                        top += fragment;
                        remaining -= fragment;
                        if (remaining <= 0) break;
                        FinishPage(start);
                        top = options.Margin;
                    }
                    if (top + height > options.PageHeight - options.Margin)
                    {
                        FinishPage(start);
                        top = options.Margin;
                    }
                }
                else
                {
                    FinishPage(start);
                    top = options.Margin;
                    if (movePadding)
                    {
                        PaintInitial(initial.Height, 0);
                        top += initial.Height;
                    }
                }
            }
        }


        void FlushLine(bool finalLine = true)
        {
            while (line.Count > 0 && line[^1].Image is null
                && (line[^1].Length == 0 || line[^1].Length == 1 && text[line[^1].Index] == ' '))
                line.RemoveAt(line.Count - 1);
            if (line.Count == 0) return;
            var block = line[0].BlockStyle;
            double ascent = AboveBaseline(block), descent = BelowBaseline(block);
            var measuredInlineOwners = new HashSet<EpubNativeContent.Element>(ReferenceEqualityComparer.Instance);
            foreach (var item in line)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ascent = Math.Max(ascent, item.Image is { } sizedImage
                    ? sizedImage.Height * item.Width / sizedImage.Width : AboveBaseline(item.Style, item.Font));
                descent = Math.Max(descent, item.Image is null ? BelowBaseline(item.Style, item.Font) : 0);
                for (var owner = item.Owner; owner is { IsBlock: false } && measuredInlineOwners.Add(owner); owner = owner.Parent)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (++inlineMetricWork > 1_000_000)
                        throw new InvalidDataException("Native EPUB inline metrics exceed their work limit.");
                    ascent = Math.Max(ascent, AboveBaseline(owner.Style));
                    descent = Math.Max(descent, BelowBaseline(owner.Style));
                }
            }
            double height = ascent + descent;
            if (height > options.PageHeight - 2 * options.Margin)
                throw new NotSupportedException("The native EPUB line exceeds the available page height.");
            FitLine(height, lineStart);
            startedBlocks.Add(lineBlock);
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
                    if (!owner.Style.Hidden && owner.Style.Background?.Color is not null) ancestors.Push(owner);
                while (ancestors.TryPop(out var owner))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var selected = FontFor(owner.Style);
                    double fragmentTop = baseline - selected.Ascender * owner.Style.FontSize / selected.UnitsPerEm;
                    double fragmentBottom = baseline - selected.Descender * owner.Style.FontSize / selected.UnitsPerEm;
                    if (item.Image is { } image)
                        fragmentTop = Math.Min(fragmentTop, baseline - image.Height * item.Width / image.Width);
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
            var underlineFragments = new Dictionary<EpubNativeContent.Element, int>(ReferenceEqualityComparer.Instance);
            foreach (var item in line)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (item.Image is { } image)
                {
                    if (!item.Style.Hidden)
                    {
                        double imageHeight = image.Height * item.Width / image.Width;
                        glyphs.Add(new NativeImagePaint(image, item.Left, baseline - imageHeight) { Width = item.Width, Height = imageHeight });
                    }
                    continue;
                }
                if (item.Length == 0) continue;
                double size = item.Style.FontSize;
                double logicalTop = baseline - item.Font.Ascender * size / item.Font.UnitsPerEm;
                var placement = new EpubTextPlacement(spineIndex, item.Index, item.Length, item.Left, logicalTop, item.Width, size)
                {
                    Color = item.Style.Color
                };
                navigationPlacements.Add(placement);
                if (item.Style.Hidden) continue;
                placements.Add(placement);
                if (item.Owner is { HasUnderline: true } decoratedOwner)
                {
                    for (var owner = decoratedOwner; owner is not null; owner = owner.Parent)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (++decorationWork > 1_000_000)
                            throw new InvalidDataException("Native EPUB text decorations exceed their work limit.");
                        if (owner.Style.Underline) ancestors.Push(owner);
                    }
                    while (ancestors.TryPop(out var owner))
                    {
                        var underlineFont = FontFor(owner.Style);
                        double underlineSize = owner.Style.FontSize;
                        double offset = underlineSize / 10, thickness = underlineSize / 20;
                        if (underlineFont.TryGetTableRange("post", out int postOffset, out int postLength) && postLength >= 12)
                        {
                            var post = underlineFont.FontData.Span.Slice(postOffset, postLength);
                            offset = -BinaryPrimitives.ReadInt16BigEndian(post[8..]) * underlineSize / underlineFont.UnitsPerEm;
                            int specifiedThickness = BinaryPrimitives.ReadInt16BigEndian(post[10..]);
                            if (specifiedThickness > 0) thickness = specifiedThickness * underlineSize / underlineFont.UnitsPerEm;
                        }
                        if (underlineFragments.TryGetValue(owner, out int paintIndex)
                            && glyphs[paintIndex] is NativeRectanglePaint previous
                            && Math.Abs(previous.Left + previous.Width - item.Left) < 0.0000001)
                            glyphs[paintIndex] = previous with { Width = item.Left + item.Width - previous.Left };
                        else
                        {
                            underlineFragments[owner] = glyphs.Count;
                            glyphs.Add(new NativeRectanglePaint(item.Left, baseline + offset, item.Width, thickness,
                                owner.Style.Color ?? options.TextColor));
                        }
                    }
                }
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

        (double Width, double Height) ImageSize(PngDecodedImage image, EpubNativeContent.Element owner)
        {
            double width = image.Width;
            if (owner.MaximumWidth is { Length: { } maximum })
            {
                double limit = maximum.Percentage ? contentWidth * maximum.Value / 100
                    : maximum.Em ? owner.Style.FontSize * maximum.Value : maximum.Value;
                if (!double.IsFinite(limit) || limit < 0 || limit > 32768)
                    throw new NotSupportedException("Native EPUB maximum image width exceeds the supported dimensions.");
                width = Math.Min(width, limit);
            }
            return (width, image.Height * width / image.Width);
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
            cancellationToken.ThrowIfCancellationRequested();
            if (pages.Count >= 10_000)
                throw new InvalidDataException("Native EPUB layout exceeds its 10000-page limit.");
            var paints = backgrounds.OrderBy(fill => fill.Depth).Select(fill => fill.Paint)
                .Concat(glyphs);
            var hidden = new List<(int Start, int Length)>();
            int hiddenStart = -1;
            for (int index = pageStart; index < end; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (content.Characters[index]?.Style.Hidden == true)
                {
                    if (hiddenStart < 0) hiddenStart = index;
                }
                else if (hiddenStart >= 0)
                {
                    hidden.Add((hiddenStart - pageStart, index - hiddenStart));
                    hiddenStart = -1;
                }
            }
            if (hiddenStart >= 0) hidden.Add((hiddenStart - pageStart, end - hiddenStart));
            pages.Add(new EpubLayoutPage(text[pageStart..end], pageStart, spineIndex, placements, paints,
                content.CanvasBackground, placements.Select(placement => clusters[placement.Start]), hidden, navigationPlacements));
            pageStart = end;
            pageHasContent = false;
            placements.Clear();
            navigationPlacements.Clear();
            glyphs.Clear();
            backgrounds.Clear();
        }
    }

}
