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
        => CreateLayout(options, null, cancellationToken);

    /// <summary>
    /// Lays out the book once to find its page breaks. Each finished page is handed to <paramref name="residentPage"/>
    /// with its index while its whole layout is still in memory; afterwards the book keeps only a compact page index
    /// and lays a chapter out again when its placements, paints or clusters are next needed.
    /// </summary>
    internal EpubLayoutDocument CreateLayout(EpubLayoutOptions options, Action<EpubLayoutPage, int>? residentPage,
        CancellationToken cancellationToken)
        => CreateLayout(options, residentPage, inBackground: false, cancellationToken);

    /// <summary>
    /// Lays out the first chapter in reading order and returns, while a background thread paginates the rest of the
    /// book in reading order and adds each chapter's pages as it finishes them (<see cref="EpubLayoutDocument.IsPaginating"/>).
    /// The finished book is the one <see cref="CreateLayout(EpubLayoutOptions, CancellationToken)"/> lays out. The token
    /// cancels the first chapter only; closing the book stops the background pagination.
    /// </summary>
    internal EpubLayoutDocument CreateLayoutInBackground(EpubLayoutOptions options, CancellationToken cancellationToken)
        => CreateLayout(options, null, inBackground: true, cancellationToken);

    private EpubLayoutDocument CreateLayout(EpubLayoutOptions options, Action<EpubLayoutPage, int>? residentPage,
        bool inBackground, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.DefaultFont);
        if (options.TableFlowWorkLimit is < 1 or > 16_000_000)
            throw new ArgumentOutOfRangeException(nameof(options), "The table work limit must be from 1 through 16,000,000.");
        if (EpubLayoutOptions.PageLimitError(options.PageWidth, options.PageHeight, options.Margin, options.FontSize) is { } limit)
            throw limit;
        double contentWidth = options.PageWidth - 2 * options.Margin;
        if (Layout != "reflowable" || PageProgressionDirection is not ("default" or "ltr")
            || Spine.Count == 0)
            throw new NotSupportedException("Native EPUB layout requires linear, reflowable, left-to-right spine items.");
        var pages = new List<EpubLayoutPage>();
        var anchors = new List<(int SpineIndex, string Id, int Offset)>();
        var cellAnchors = new Dictionary<(int SpineIndex, string Id, int Offset), EpubNavigationDestination>();
        var reader = new TrueTypeOutlineReader(options.DefaultFont);
        var familyFonts = new Dictionary<EpubFontFamily, TrueTypeFont?>();
        var faceFonts = new Dictionary<EpubFontFaceRequest, EpubFontFaceResult?>();
        var retainedFonts = new HashSet<TrueTypeFont> { options.DefaultFont };
        long retainedFontBytes = 0;
        // Book-wide limits sized from the 36-book Gutenberg corpus: the largest book (Complete Shakespeare)
        // has 7.2 MB of XHTML, so 16,000,000 laid-out characters leave over twice that headroom while still
        // bounding work below the 64 MiB package limit; stylesheets are charged once each (largest 43 KB).
        int remainingTextLength = 16_000_000;
        int remainingStylesheetBytes = 8 * 1024 * 1024;
        int remainingStylesheetReferences = 65_536;
        var chargedStylesheets = new HashSet<string>(StringComparer.Ordinal);
        // The box an image first decodes at is kept per image, so every later use and every redraw decodes it identically.
        var imageBoxes = new Dictionary<string, (double Width, double Height)>(StringComparer.Ordinal);
        var images = new EpubImageStore(key => DecodeNativeImage(key, CancellationToken.None));
        var bookReaders = new Dictionary<TrueTypeFont, TrueTypeOutlineReader>(ReferenceEqualityComparer.Instance) { [options.DefaultFont] = reader };
        var chapterDiagnostics = new List<EpubChapterDiagnostic>();
        var contentDiagnostics = new List<EpubChapterDiagnostic>();
        var ignoredDeclarations = new List<EpubIgnoredDeclarations>();
        // Decision 0009: non-linear items (notes, answers) follow the linear reading order, each in spine order.
        var readingOrder = Enumerable.Range(0, Spine.Count).Where(index => Spine[index].IsLinear)
            .Concat(Enumerable.Range(0, Spine.Count).Where(index => !Spine[index].IsLinear)).ToArray();
        // Font lookups made while laying a chapter out again follow that work's token, not the finished open's.
        var layoutToken = cancellationToken;
        var firstPageOf = new Dictionary<int, int>();
        var resumePointsOf = new Dictionary<int, EpubResumePoint?[]>();
        var chapterPages = new EpubChapterPages(Relayout, readingOrder);
        var document = new EpubLayoutDocument(options, Spine.Select(entry => entry.Item.Href)) { ChapterPages = chapterPages };
        int completed = 0;
        if (!inBackground)
        {
            // Nobody can read the book before this returns, so its chapters need no layout lock.
            while (completed < readingOrder.Length)
            {
                PaginateNext(cancellationToken);
                // The next spine item starts by checking cancellation, so a Cancel raised here stops layout.
                options.ChapterProgress?.Invoke(completed, Spine.Count);
            }
            cancellationToken.ThrowIfCancellationRequested();
            layoutToken = CancellationToken.None;
            document.EndPagination();
            return document;
        }
        PaginateNext(cancellationToken);
        options.ChapterProgress?.Invoke(completed, Spine.Count);
        cancellationToken.ThrowIfCancellationRequested();
        if (completed == readingOrder.Length)
        {
            layoutToken = CancellationToken.None;
            document.EndPagination();
            return document;
        }
        // Normal priority, as for layout ahead: a reader waiting for the layout lock waits on this thread's next check.
        new Thread(PaginateRest) { IsBackground = true, Name = "EPUB pagination" }.Start();
        return document;

        void PaginateRest()
        {
            try
            {
                while (completed < readingOrder.Length)
                {
                    chapterPages.Paginate(token => { PaginateNext(token); return true; });
                    options.ChapterProgress?.Invoke(completed, Spine.Count);
                }
                document.EndPagination();
            }
            catch (OperationCanceledException) when (chapterPages.Closing.IsCancellationRequested)
            {
                document.EndPagination(closed: true);
            }
            // Pages already shown stay; the rest of the book is reported as the error that stopped it.
            catch (Exception error)
            {
                document.EndPagination(error);
            }
        }

        // Lays out the next chapter in reading order and publishes it whole. Until it is published, a cancellation
        // (a reader who needs the layout lock) puts every list and budget back as it was, so the chapter can be laid
        // out again from the start with exactly the same result.
        void PaginateNext(CancellationToken token)
        {
            int spineIndex = readingOrder[completed];
            token.ThrowIfCancellationRequested();
            layoutToken = token;
            int pageMark = pages.Count, anchorMark = anchors.Count, contentMark = contentDiagnostics.Count;
            int chapterMark = chapterDiagnostics.Count, ignoredMark = ignoredDeclarations.Count;
            int textBefore = remainingTextLength, stylesheetBytesBefore = remainingStylesheetBytes;
            int stylesheetReferencesBefore = remainingStylesheetReferences;
            var chargedBefore = new HashSet<string>(chargedStylesheets, StringComparer.Ordinal);
            bool laidOut = false;
            var points = new List<EpubResumePoint?>();
            var destinations = new Dictionary<(int SpineIndex, int Offset), EpubNavigationDestination>();
            try
            {
                try
                {
                    int ignored = AppendNativeSpinePages(spineIndex, options, contentWidth, reader, pages, anchors, cellAnchors,
                        ref remainingTextLength, ref remainingStylesheetBytes, ref remainingStylesheetReferences,
                        imageBoxes, images, bookReaders, ResolveFamily, contentDiagnostics, chargedStylesheets, token,
                        resumePoints: points);
                    if (ignored > 0) ignoredDeclarations.Add(new(spineIndex, Spine[spineIndex].Item.Href, ignored));
                    laidOut = true;
                }
                catch (Exception error) when (options.IsolateChapterFailures
                    && error is not (OperationCanceledException or OutOfMemoryException))
                {
                    // Work already spent stays charged to the shared budgets; only the chapter's output is withdrawn.
                    token.ThrowIfCancellationRequested();
                    WithdrawChapter();
                    string href = Spine[spineIndex].Item.Href;
                    chapterDiagnostics.Add(new(spineIndex, href, error.Message) { Reason = OmissionReason(error) });
                    pages.Add(ChapterDiagnosisPage(spineIndex, options, reader, href, error.Message,
                        () => ResolveFamily(null, 400, EpubFontStyle.Normal), token));
                }
                if (laidOut)
                    EpubLayoutDocument.RecordAnchorDestinations(pages, pageMark, spineIndex,
                        anchors.Skip(anchorMark).Select(anchor => anchor.Offset), options.Margin, destinations, token);
            }
            catch (OperationCanceledException)
            {
                // Stopped before publishing: the chapter leaves no trace and its budgets are charged again next time.
                WithdrawChapter();
                chapterDiagnostics.RemoveRange(chapterMark, chapterDiagnostics.Count - chapterMark);
                ignoredDeclarations.RemoveRange(ignoredMark, ignoredDeclarations.Count - ignoredMark);
                remainingTextLength = textBefore;
                remainingStylesheetBytes = stylesheetBytesBefore;
                remainingStylesheetReferences = stylesheetReferencesBefore;
                chargedStylesheets.Clear();
                chargedStylesheets.UnionWith(chargedBefore);
                throw;
            }
            // From here on nothing checks for cancellation: the chapter is committed.
            for (int pageIndex = pageMark; pageIndex < pages.Count; pageIndex++)
            {
                if (residentPage is null) break;
                if (laidOut) { residentPage(pages[pageIndex], pageIndex); continue; }
                // A notice page the host cannot write becomes a plain page with the same text, so one unwritable
                // notice never ends the conversion of the book.
                try { residentPage(pages[pageIndex], pageIndex); }
                catch (Exception error) when (error is not (OperationCanceledException or OutOfMemoryException))
                {
                    pages[pageIndex] = new EpubLayoutPage(pages[pageIndex].Text, 0, spineIndex, [], []);
                    residentPage(pages[pageIndex], pageIndex);
                }
            }
            // A laid-out chapter keeps only its index entries; a diagnosis page is small and stays whole.
            if (laidOut)
            {
                firstPageOf[spineIndex] = pageMark;
                chapterPages.Register(spineIndex, pages.Count - pageMark);
                // One resume point per page (the first page has none); the chapter's last page break adds one more.
                resumePointsOf[spineIndex] = points.Take(pages.Count - pageMark).ToArray();
                // The chapter the reader opens to, or turns into next, is already laid out in full.
                if (inBackground) chapterPages.Paginated(spineIndex, pages.GetRange(pageMark, pages.Count - pageMark));
                for (int pageIndex = pageMark; pageIndex < pages.Count; pageIndex++)
                    pages[pageIndex] = pages[pageIndex].Indexed(chapterPages, pageIndex - pageMark);
            }
            document.Publish(spineIndex, pages.GetRange(pageMark, pages.Count - pageMark), anchors.Skip(anchorMark),
                cellAnchors.Where(item => item.Key.SpineIndex == spineIndex), destinations,
                chapterDiagnostics.Skip(chapterMark), contentDiagnostics.Skip(contentMark),
                ignoredDeclarations.Count > ignoredMark ? ignoredDeclarations[^1] : null);
            completed++;

            void WithdrawChapter()
            {
                pages.RemoveRange(pageMark, pages.Count - pageMark);
                anchors.RemoveRange(anchorMark, anchors.Count - anchorMark);
                contentDiagnostics.RemoveRange(contentMark, contentDiagnostics.Count - contentMark);
                foreach (var key in cellAnchors.Keys.Where(key => key.SpineIndex == spineIndex).ToList())
                    cellAnchors.Remove(key);
            }
        }

        // Lays one chapter out again exactly as the first time: the same fonts, image boxes and stylesheets, with the
        // book's work budgets already proven by the first layout, and its diagnostics and anchors already recorded.
        // From the page asked for, it resumes at the state recorded for that page instead of the chapter start.
        EpubChapterRun Relayout(int spineIndex, int fromPage, int stopAfterPages, CancellationToken relayoutToken)
        {
            // The chapter starts at the same book page as before: the first page of a book collapses its top margin,
            // and cell anchors and the page limit count book pages, so empty places stand in for the earlier pages.
            int firstPage = firstPageOf[spineIndex];
            var points = resumePointsOf[spineIndex];
            var resume = fromPage > 0 && fromPage < points.Length ? points[fromPage] : null;
            var again = new List<EpubLayoutPage>(firstPage + 16);
            again.AddRange(Enumerable.Repeat<EpubLayoutPage>(null!, firstPage));
            int text = int.MaxValue, stylesheetBytes = int.MaxValue, stylesheetReferences = int.MaxValue;
            IReadOnlyList<EpubLayoutPage> laid;
            // Font lookups follow this work's token. The layout lock keeps pagination from running meanwhile, and its next
            // chapter sets its own token again.
            layoutToken = relayoutToken;
            try
            {
                AppendNativeSpinePages(spineIndex, options, contentWidth, reader, again,
                    new List<(int SpineIndex, string Id, int Offset)>(),
                    new Dictionary<(int SpineIndex, string Id, int Offset), EpubNavigationDestination>(),
                    ref text, ref stylesheetBytes, ref stylesheetReferences, imageBoxes, images, bookReaders, ResolveFamily,
                    new List<EpubChapterDiagnostic>(), new HashSet<string>(StringComparer.Ordinal), relayoutToken,
                    stopAfterPages, resume,
                    // Where the page after the last one asked for begins, so glyphs past it are not shaped.
                    stopAtOffset: stopAfterPages < points.Length ? pages[firstPage + stopAfterPages].TextStart : int.MaxValue);
                laid = again.GetRange(firstPage, again.Count - firstPage);
            }
            catch (EpubLayoutStopped stopped) { laid = stopped.Pages; }
            // The page the resumed block began on lacks what came before the block, so it is dropped with the rest.
            var kept = new EpubLayoutPage?[laid.Count];
            for (int page = Math.Max(0, fromPage); page < laid.Count; page++) kept[page] = laid[page];
            return new EpubChapterRun(kept, laid.Count - (resume?.Page ?? 0));
        }

        TrueTypeFont? ResolveFamily(EpubFontFamily? family, double weight, EpubFontStyle style)
        {
            layoutToken.ThrowIfCancellationRequested();
            if (options.FontFaceResolver is { } resolveFace)
            {
                var request = new EpubFontFaceRequest(family, weight, options.DefaultFont) { Style = style };
                var faceKey = request with { Family = family is null ? null : family with { Name = family.Name.ToUpperInvariant() } };
                if (faceFonts.TryGetValue(faceKey, out var cachedFace)) return cachedFace?.Font;
                // EP-BF-031: past 256 distinct lookups, further families fall back to the default face.
                if (faceFonts.Count >= 256) return null;
                var result = resolveFace(request);
                layoutToken.ThrowIfCancellationRequested();
                // EP-BF-021: CFF-outline faces cannot be drawn yet, so they are passed over like a missing face.
                if (result?.Font is { HasCffOutlines: true }) result = null;
                // A face beyond the retained-font budget falls back the same way instead of refusing the chapter.
                if (result?.Font is { } candidate && !CanRetain(candidate)) result = null;
                if (result is not null)
                {
                    if (result.Font is null || !double.IsFinite(result.ActualWeight) || result.ActualWeight is < 1 or > 1000
                        || !Enum.IsDefined(result.Match) || (result.Match == EpubFontMatch.Exact) != (result.ActualWeight == weight)
                        || !Enum.IsDefined(result.ActualStyle) || result.ActualStyle != style)
                        throw new InvalidDataException("The native EPUB font resolver returned an inconsistent face match.");
                    RetainFace(result.Font);
                }
                faceFonts.Add(faceKey, result);
                return result?.Font;
            }
            if (weight != 400 || style != EpubFontStyle.Normal || family is null || options.FontFamilyResolver is null) return null;
            var key = family with { Name = family.Name.ToUpperInvariant() };
            if (familyFonts.TryGetValue(key, out var cached)) return cached;
            if (familyFonts.Count >= 256) return null;
            var selected = options.FontFamilyResolver(family);
            layoutToken.ThrowIfCancellationRequested();
            if (selected is { HasCffOutlines: true } || selected is not null && !CanRetain(selected)) selected = null;
            if (selected is not null) RetainFace(selected);
            familyFonts.Add(key, selected);
            return selected;
        }

        bool CanRetain(TrueTypeFont selected) => retainedFonts.Contains(selected)
            || retainedFonts.Count < 64 && retainedFontBytes + selected.FontData.Length <= 64L << 20;

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

    // The one place a chapter refusal is sorted into its stable category; the message stays the English detail.
    private static EpubOmissionReason OmissionReason(Exception error) => error switch
    {
        EpubImageUnavailableException image => image.Reason,
        _ when error.Message.Contains("encrypted", StringComparison.OrdinalIgnoreCase) => EpubOmissionReason.Encrypted,
        _ when error.Message.Contains("Scripted", StringComparison.Ordinal) => EpubOmissionReason.Scripting,
        _ when error.Message.Contains("shaping or bidirectional", StringComparison.Ordinal) => EpubOmissionReason.UnsupportedScript,
        InvalidDataException when error.Message.Contains("limit", StringComparison.OrdinalIgnoreCase) => EpubOmissionReason.LimitExceeded,
        InvalidDataException => EpubOmissionReason.InvalidContent,
        NotSupportedException => EpubOmissionReason.UnsupportedLayout,
        _ => EpubOmissionReason.Other
    };

    // HTML ol type: 1 decimal, a/A bijective base-26 letters, i/I roman numerals (1 to 3999, decimal otherwise).
    private static string ListMarkerText(int ordinal, string? type)
    {
        switch (type)
        {
            case "a" or "A" when ordinal > 0:
            {
                var letters = new StringBuilder();
                for (int value = ordinal; value > 0; value = (value - 1) / 26)
                    letters.Insert(0, (char)((type == "a" ? 'a' : 'A') + (value - 1) % 26));
                return letters.ToString();
            }
            case "i" or "I" when ordinal is > 0 and < 4000:
            {
                var roman = new StringBuilder();
                int value = ordinal;
                foreach (var (amount, symbol) in new[] { (1000, "M"), (900, "CM"), (500, "D"), (400, "CD"), (100, "C"), (90, "XC"),
                    (50, "L"), (40, "XL"), (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I") })
                    for (; value >= amount; value -= amount) roman.Append(symbol);
                return type == "i" ? roman.ToString().ToLowerInvariant() : roman.ToString();
            }
            default:
                return ordinal.ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// Draws the caller's label, the chapter path and the diagnosis. Each character uses the fallback font when it maps the
    /// character, then the host's default face, and otherwise shows the fallback font's missing-glyph box.
    /// </summary>
    private static EpubLayoutPage ChapterDiagnosisPage(int spineIndex, EpubLayoutOptions options,
        TrueTypeOutlineReader reader, string href, string message, Func<TrueTypeFont?> resolveHostFace,
        CancellationToken cancellationToken)
    {
        var font = options.DefaultFont;
        double size = options.FontSize, lineHeight = size * 1.2;
        double left = options.Margin, right = options.PageWidth - options.Margin;
        double bottom = options.PageHeight - options.Margin;
        var text = new StringBuilder();
        var placements = new List<EpubTextPlacement>();
        var glyphs = new List<NativePagePaint>();
        // One unsubstituted cluster per drawn run lets PDF conversion emit the diagnosis as selectable text.
        var clusters = new List<EpubGlyphCluster>();
        var readers = new Dictionary<TrueTypeFont, TrueTypeOutlineReader> { [font] = reader };
        TrueTypeFont? hostFace = null;
        bool hostFaceAsked = false;
        double top = options.Margin;
        foreach (string paragraph in new[] { options.UnavailableChapterLabel ?? "", href, message })
        {
            var line = new StringBuilder();
            double width = 0;
            foreach (string word in paragraph.Split(' '))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string candidate = line.Length == 0 ? word : " " + word;
                double candidateWidth = Measure(candidate);
                if (line.Length > 0 && left + width + candidateWidth > right)
                {
                    Emit(line.ToString(), separator: ' ');
                    line.Clear(); width = 0; candidate = word; candidateWidth = Measure(word);
                }
                line.Append(candidate);
                width += candidateWidth;
            }
            Emit(line.ToString(), separator: '\n');
        }
        // The full diagnosis stays readable for search, copy and assistive technology even when the page clips it.
        return new EpubLayoutPage(text.ToString().TrimEnd('\n'), 0, spineIndex, placements, glyphs, clusters: clusters);

        // The font and glyph that draw a character; glyph 0 in the fallback font means no font maps it.
        (TrueTypeFont Font, ushort Glyph) Choose(int scalar)
        {
            ushort glyph = font.GetGlyphId(scalar);
            if (glyph != 0) return (font, glyph);
            if (!hostFaceAsked)
            {
                hostFaceAsked = true;
                try { hostFace = resolveHostFace(); }
                catch (Exception error) when (error is not (OperationCanceledException or OutOfMemoryException)) { hostFace = null; }
                if (hostFace is { HasCffOutlines: true }) hostFace = null;
            }
            if (hostFace is not null && hostFace.GetGlyphId(scalar) is var hostGlyph and not 0) return (hostFace, hostGlyph);
            return (font, 0);
        }

        double Measure(string value)
        {
            double total = 0;
            foreach (var rune in value.EnumerateRunes())
            {
                var (face, glyph) = Choose(rune.Value);
                total += face.GetPdfAdvanceWidth(glyph) * size / 1000;
            }
            return total;
        }

        void Emit(string value, char separator)
        {
            if (top + lineHeight > bottom)
            {
                text.Append(value).Append(separator);
                return;
            }
            double x = left, baseline = top + size;
            int at = 0;
            while (at < value.Length)
            {
                // A run is consecutive characters drawn by one font; a character no font maps is a run of its own.
                var first = Choose(Rune.GetRuneAt(value, at).Value);
                bool missing = first.Glyph == 0;
                int end = at;
                var runGlyphs = new List<EpubClusterGlyph>();
                double runLeft = x;
                int runStart = text.Length + at;
                while (end < value.Length)
                {
                    var rune = Rune.GetRuneAt(value, end);
                    var (face, glyph) = Choose(rune.Value);
                    if (!ReferenceEquals(face, first.Font) || (glyph == 0) != missing || (missing && end > at)) break;
                    if (!missing)
                    {
                        if (!readers.TryGetValue(face, out var faceReader)) readers.Add(face, faceReader = new TrueTypeOutlineReader(face));
                        if (faceReader.Outline(glyph) is { } outline)
                            glyphs.Add(new NativeGlyphPaint(outline, x, baseline, size / 1000, options.TextColor) { SourceOffset = runStart });
                    }
                    else if (missing && !char.IsWhiteSpace(value, end) && !char.IsControl(value, end)
                        && reader.Outline(0) is { } box)
                        glyphs.Add(new NativeGlyphPaint(box, x, baseline, size / 1000, options.TextColor) { SourceOffset = runStart });
                    runGlyphs.Add(new(glyph, face.GetAdvanceWidth(glyph) / (double)face.UnitsPerEm, 0, 0));
                    x += face.GetPdfAdvanceWidth(glyph) * size / 1000;
                    end += rune.Utf16SequenceLength;
                }
                clusters.Add(new(runStart, end - at, first.Font, runGlyphs) { Missing = missing });
                placements.Add(new(spineIndex, runStart, end - at, runLeft, top, x - runLeft, lineHeight));
                at = end;
            }
            if (value.Length == 0) placements.Add(new(spineIndex, text.Length, 0, left, top, 0, lineHeight));
            text.Append(value).Append(separator);
            top += lineHeight;
        }
    }

    /// <returns>The number of CSS declarations and rules the chapter ignored.</returns>
    private int AppendNativeSpinePages(int spineIndex, EpubLayoutOptions options, double availableWidth,
        TrueTypeOutlineReader reader, List<EpubLayoutPage> destinationPages,
        List<(int SpineIndex, string Id, int Offset)> anchors,
        Dictionary<(int SpineIndex, string Id, int Offset), EpubNavigationDestination> cellAnchors,
        ref int remainingTextLength,
        ref int remainingStylesheetBytes, ref int remainingStylesheetReferences,
        Dictionary<string, (double Width, double Height)> imageBoxes, EpubImageStore images,
        Dictionary<TrueTypeFont, TrueTypeOutlineReader> bookReaders,
        Func<EpubFontFamily?, double, EpubFontStyle, TrueTypeFont?> resolveFamily,
        List<EpubChapterDiagnostic> contentDiagnostics, HashSet<string> chargedStylesheets, CancellationToken cancellationToken,
        int stopAfterPages = int.MaxValue, EpubResumePoint? resume = null, List<EpubResumePoint?>? resumePoints = null,
        int stopAtOffset = int.MaxValue)
    {
        // Itemref properties (page-spread-*, rendition:*) only affect synthetic spreads and fixed layout; they are ignored.
        var resource = Spine[spineIndex].Item;
        if (_oversizedResources.Contains(resource.Href)) throw OversizedResource(resource.Href);
        // Only scripting refuses a chapter, as in ReadStructuralText; nav, mathml and remote-resources are not fetched or are ignored.
        if (resource.MediaType != "application/xhtml+xml" || Tokens(resource.Properties).Contains("scripted", StringComparer.Ordinal))
            throw new NotSupportedException("Native EPUB layout requires unscripted XHTML with supported resource properties.");
        if (_encryptedResources.Contains(resource.Href)) throw EncryptedChapter(resource.Href);
        if (_resources[resource.Href].Length > MaxEntryBytes)
            throw new InvalidDataException("Native EPUB XHTML exceeds its 8 MiB resource limit.");
        var content = new EpubNativeContent(options.FontSize, options.PageHeight - 2 * options.Margin, options.TextColor);
        LoadNativeStylesheets(resource.Href, content, ref remainingStylesheetBytes,
            ref remainingStylesheetReferences, chargedStylesheets, cancellationToken);
        string text = ReadStructuralText(spineIndex, cancellationToken, content);
        foreach (string unresolved in content.UnresolvedLinks.Distinct(StringComparer.Ordinal).Take(1000))
            contentDiagnostics.Add(new(spineIndex, resource.Href,
                "The link target " + unresolved + " is not in the book, so the link is inactive.")
            { Reason = EpubOmissionReason.MissingResource, ItemHref = PackagePathOf(resource.Href, unresolved) });
        foreach (string drawing in content.OmittedDrawings.Take(1000))
            contentDiagnostics.Add(new(spineIndex, resource.Href, "An SVG drawing was left out: " + drawing)
            { Reason = EpubOmissionReason.UnsupportedLayout });
        var linkTargets = new Dictionary<string, string?>(StringComparer.Ordinal);
        var formatting = new EpubFormattingTree(content, cancellationToken);
        var bodyMargins = content.ResolveBodyMargins(availableWidth);
        var bodyPadding = content.ResolveBodyPadding(availableWidth);
        double initialContentLeft = options.Margin + bodyMargins.Left + bodyPadding.Left;
        double initialContentRight = options.PageWidth - options.Margin - bodyMargins.Right - bodyPadding.Right;
        if (initialContentRight <= initialContentLeft || bodyMargins.Top + bodyPadding.Top >= options.PageHeight - 2 * options.Margin)
            throw new NotSupportedException("Native EPUB body padding requires a positive content box within the page.");
        if (text.Length > remainingTextLength)
            throw new InvalidDataException("Native EPUB text exceeds its 16,000,000-character layout limit.");
        remainingTextLength -= text.Length;
        var anchorsByCell = new Dictionary<EpubNativeContent.Element, List<(string Id, int Offset)>>();
        var anchorIds = new HashSet<string>(StringComparer.Ordinal);
        var repeatedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var anchor in content.Anchors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // HTML: the first element with an id is the one a fragment finds; a repeat is reported once and ignored.
            if (!anchorIds.Add(anchor.Id))
            {
                if (repeatedIds.Add(anchor.Id) && repeatedIds.Count <= 1000)
                    contentDiagnostics.Add(new(spineIndex, resource.Href,
                        "The id " + anchor.Id + " is used more than once, so links to it go to its first use.")
                    // A repeated id is malformed markup; the existing reason keeps the app's localized wording.
                    { Reason = EpubOmissionReason.InvalidContent });
                continue;
            }
            if (anchors.Count >= 100_000)
                throw new InvalidDataException("Native EPUB anchors exceed their 100,000 destination limit.");
            int offset = Math.Min(anchor.Offset, text.Length);
            anchors.Add((spineIndex, anchor.Id, offset));
            var cell = anchor.Owner.Name == "td" ? anchor.Owner : anchor.Owner.TableCellOwner;
            if (cell is not null)
            {
                if (!anchorsByCell.TryGetValue(cell, out var owned)) anchorsByCell.Add(cell, owned = []);
                owned.Add((anchor.Id, offset));
            }
        }
        var font = options.DefaultFont;
        var substitutions = new Dictionary<(TrueTypeFont Font, bool SmallCaps, bool Ligatures), OpenTypeGlyphSubstitution>();
        bool reportedSmallCaps = false, reportedListMarker = false;
        // One outline reader per face for the whole book, so chapters share glyph outlines instead of rebuilding them.
        var readers = bookReaders;
        int fontWork = 0;
        int decorationWork = 0;
        int inlineMetricWork = 0;
        var clusters = new Dictionary<int, EpubGlyphCluster>();
        var blankAdvances = new Dictionary<int, double>();
        var missingGlyphs = new HashSet<int>();
        var reportedMissing = new HashSet<int>();
        var inlineOffsets = content.Inlines.Select(item => item.Offset).ToHashSet();
        int shapingWork = 0;
        var coverage = new Dictionary<IReadOnlyList<OpenTypeGlyphSubstitution.SubstitutionRule>,
            ILookup<ushort, OpenTypeGlyphSubstitution.SubstitutionRule>>(ReferenceEqualityComparer.Instance);
        // Per-chapter work caps scale with the chapter's own character bound so long chapters fit.
        const int chapterWork = 8 * EpubNativeContent.MaximumChapterCharacters;
        // How far the current line has advanced, for preformatted tab stops.
        double lineAdvance = 0;
        var (shapeStart, shapeStop) = ShapedRange();
        for (int runStart = shapeStart; runStart < shapeStop;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (text[runStart] == '\n') { runStart++; lineAdvance = 0; continue; }
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
                bool substituted = glyph == 0 && scalar.Value == 0xA0;
                if (substituted) glyph = BlankSpace(font);
                double size = (owner?.Style ?? content.DefaultStyle).FontSize;
                if (scalar.Value == '\t')
                {
                    // CSS Text 3 tab-size 8: stops every eight space advances of this font from the line start.
                    ushort spaceGlyph = font.GetGlyphId(' ');
                    double stop = 8 * size * (spaceGlyph == 0 ? .5 : font.GetAdvanceWidth(spaceGlyph) / (double)font.UnitsPerEm);
                    double next = (Math.Floor(lineAdvance / stop + 1e-9) + 1) * stop;
                    glyph = BlankSpace(font);
                    blankAdvances[runEnd] = (next - lineAdvance) / size;
                }
                else if (BlankAdvance(scalar.Value, font) is { } blankAdvance)
                {
                    glyph = BlankSpace(font);
                    blankAdvances[runEnd] = blankAdvance;
                }
                else if (glyph == 0 && scalar.Value != ' ')
                {
                    // No font maps it: the missing-glyph box stands in, and the chapter reports the character once.
                    missingGlyphs.Add(runEnd);
                    if (reportedMissing.Add(scalar.Value))
                        contentDiagnostics.Add(new(spineIndex, resource.Href,
                            $"No available font has a glyph for U+{scalar.Value:X4}, so a missing-glyph box is shown.")
                        { Reason = EpubOmissionReason.MissingGlyph });
                }
                run.Add(Cluster(runEnd, scalar.Utf16SequenceLength, glyph, substituted));
                lineAdvance += size * (blankAdvances.TryGetValue(runEnd, out double blankEm) ? blankEm
                    : font.GetAdvanceWidth(glyph) / (double)font.UnitsPerEm);
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
                    // Coverage index: only rules whose first component is this glyph are tried, in lookup order.
                    if (!coverage.TryGetValue(lookup, out var byFirst))
                        coverage.Add(lookup, byFirst = lookup.Where(rule => rule.Components.Length > 0).ToLookup(rule => rule.Components[0]));
                    for (int at = 0; at < shaped.Count; at++)
                    {
                        ChargeShapingWork();
                        bool replaced = false;
                        foreach (var rule in byFirst[shaped[at].Glyph])
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
                    output.Add(new(item.Glyph, blankAdvances.TryGetValue(item.Start, out double blank)
                        ? blank : font.GetAdvanceWidth(item.Glyph) / (double)font.UnitsPerEm, 0, 0));
                } while (at < shaped.Count && shaped[at].Start < end);
                clusters.Add(first.Start, new(first.Start, end - first.Start, font, output, substituted)
                {
                    Blank = blankAdvances.ContainsKey(first.Start), Missing = missingGlyphs.Contains(first.Start)
                });
            }
            runStart = runEnd;
        }
        // A page laid out alone needs glyphs only from the block its layout resumes at through the text its last line can
        // reach: the inline content holding the first character after the stopping page (whose first line ends that page)
        // and, when that character is in a table, the whole table, which is measured before any of it is placed. Runs
        // start at an element's text, so starting at the resumed block's start shapes every later run as the full pass did.
        (int Start, int Stop) ShapedRange()
        {
            int start = 0, stop = text.Length;
            if (resume is not null)
            {
                var block = formatting.Root;
                foreach (int child in resume.Path) block = (EpubFormattingTree.Block)block.Children[child];
                start = Math.Min(block.Source.Offset, text.Length);
            }
            if (stopAtOffset < text.Length)
            {
                int at = Math.Max(stopAtOffset, start);
                while (at < text.Length && (text[at] == '\n' || content.Characters[at] is null)) at++;
                stop = at;
                if (at < text.Length)
                {
                    if (formatting.TextSequences.TryGetValue(at, out var sequence))
                        foreach (var atom in sequence.Atoms) stop = Math.Max(stop, atom.Offset + 2);
                    for (var owner = content.Characters[at]?.Node.Owner; owner is not null; owner = owner.Parent)
                        if (owner.Display == EpubNativeContent.DisplayKind.Table) stop = Math.Max(stop, owner.EndOffset);
                }
                stop = Math.Min(text.Length, Math.Max(stop, at + 2));
            }
            return (start, stop);
        }
        void ChargeShapingWork()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++shapingWork > chapterWork)
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
            if (smallCaps && !value.HasSmallCaps && !reportedSmallCaps)
            {
                reportedSmallCaps = true;
                contentDiagnostics.Add(new(spineIndex, resource.Href,
                    "The font has no small capitals feature, so ordinary text is shown.")
                { Reason = EpubOmissionReason.UnsupportedLayout });
            }
            return value;
        }
        TrueTypeFont FontFor(EpubNativeContent.Style style, int? scalar = null)
        {
            TrueTypeFont? spaceFallback = null;
            bool Accept(TrueTypeFont selected)
            {
                // A tab draws nothing, so it stays in the run's font and measures that font's spaces.
                if (scalar is null or '\t' || selected.GetGlyphId(scalar.Value) != 0
                    || BlankAdvance(scalar.Value, selected) is not null) return true;
                if (scalar == 0xA0 && spaceFallback is null && BlankSpace(selected) != 0)
                    spaceFallback = selected;
                return false;
            }
            foreach (var family in style.FontFamilies ?? [])
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++fontWork > chapterWork)
                    throw new InvalidDataException("Native EPUB font matching exceeds its work limit.");
                if (resolveFamily(family, style.FontWeight, style.FontStyle) is { } selected
                    && Accept(selected)) return selected;
            }
            if (options.FontFaceResolver is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++fontWork > chapterWork)
                    throw new InvalidDataException("Native EPUB font matching exceeds its work limit.");
                if (resolveFamily(null, style.FontWeight, style.FontStyle) is { } selected
                    && Accept(selected)) return selected;
            }
            if (style.FontStyle == EpubFontStyle.Normal && style.FontWeight == 400 && Accept(options.DefaultFont))
                return options.DefaultFont;
            if (spaceFallback is not null) return spaceFallback;
            // With no face for the requested weight or style, text uses the default face, as a
            // user agent falls back when a family lacks a face, instead of refusing the chapter.
            return options.DefaultFont;
        }
        // Unicode 16 chapter 6, table 6-2 widths in em. Figure and punctuation spaces take the font's digit and
        // period advances; hair space, defined only as narrower than thin (1/5), is 1/10. Soft hyphen, zero-width
        // space and zero-width no-break space advance nothing. All of them draw no ink.
        static double? BlankAdvance(int scalar, TrueTypeFont selected)
        {
            return scalar switch
            {
                0x00AD or 0x200B or 0x2060 or 0xFEFF => 0,
                0x2000 or 0x2002 => .5,
                0x2001 or 0x2003 => 1,
                0x2004 => 1d / 3,
                0x2005 => .25,
                0x2006 => 1d / 6,
                0x2007 => Advance('0', .5),
                0x2008 => Advance('.', .25),
                0x2009 => .2,
                0x200A => .1,
                _ => null
            };
            double Advance(char model, double fallback) => selected.GetGlyphId(model) is var glyph and not 0
                ? selected.GetAdvanceWidth(glyph) / (double)selected.UnitsPerEm : fallback;
        }
        ushort BlankSpace(TrueTypeFont selected)
        {
            ushort glyph = selected.GetGlyphId(' ');
            if (glyph == 0) return 0;
            if (!readers.TryGetValue(selected, out var selectedReader))
                readers.Add(selected, selectedReader = new TrueTypeOutlineReader(selected));
            return selectedReader.Outline(glyph) is { Contours.Count: 0 } ? glyph : (ushort)0;
        }
        Rune ReadScalar(int index)
        {
            if (!Rune.TryGetRuneAt(text, index, out Rune scalar))
                throw new NotSupportedException("Native EPUB layout requires valid Unicode scalar text.");
            int value = scalar.Value;
            if (value is >= ' ' and <= '~' or 0x2122 or '\t') return scalar;
            // Blank format and space characters need no shaping: they advance without ink.
            if (BlankAdvance(value, options.DefaultFont) is not null) return scalar;
            // Overlay marks (canonical combining class 1) are drawn centered over their base with no anchor
            // height, so a zero-advance overstrike is their intended look without GPOS mark positioning.
            // Accents and other marks still need mark attachment and stay refused below.
            if (value is >= 0x0334 and <= 0x0338) return scalar;
            UnicodeCategory category = Rune.GetUnicodeCategory(scalar);
            if (value is >= 0x20A0 and <= 0x20CF && category == UnicodeCategory.CurrencySymbol)
                return scalar;
            // Symbols, arrows, dingbats and number forms in the BMP need no shaping or direction handling.
            if (value is >= 0x2070 and <= 0x2BFF
                && category is UnicodeCategory.OtherSymbol or UnicodeCategory.MathSymbol or UnicodeCategory.OtherNumber
                    or UnicodeCategory.LetterNumber or UnicodeCategory.ModifierSymbol or UnicodeCategory.CurrencySymbol)
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

        int tableFlowWork = 0, tableFlowDepth = 0;
        void ChargeTableFlowWork()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++tableFlowWork > options.TableFlowWorkLimit)
                throw new InvalidDataException($"Native EPUB table flow exceeds its {options.TableFlowWorkLimit}-step work limit.");
        }
        var imageKeys = new Dictionary<PngDecodedImage, EpubImageKey>(ReferenceEqualityComparer.Instance);
        var flow = LayoutFlow(formatting.Root, initialContentLeft, initialContentRight,
            options.Margin + bodyMargins.Top + bodyPadding.Top);
        destinationPages.AddRange(flow.Pages);
        return content.IgnoredDeclarations;

        // Loads a raster through the book's store and remembers which key reproduces it when the page is drawn.
        PngDecodedImage LoadImage(EpubImageKey key)
        {
            var image = images.Get(key);
            imageKeys[image] = key;
            return image;
        }

        (IReadOnlyList<EpubLayoutPage> Pages, double Bottom) LayoutFlow(
            EpubFormattingTree.Block flowRoot, double contentLeft, double contentRight, double top)
        {
            int existingPageCount = destinationPages.Count;
            bool chapterRoot = ReferenceEquals(flowRoot, formatting.Root);
            int flowStart = chapterRoot ? 0 : Math.Min(flowRoot.Source.Offset, text.Length);
            int flowEnd = chapterRoot ? text.Length : Math.Min(flowRoot.Source.EndOffset, text.Length);
            var pages = new List<EpubLayoutPage>();
            double contentWidth = contentRight - contentLeft;
            var placements = new List<EpubTextPlacement>(flowEnd - flowStart);
            var navigationPlacements = new List<EpubTextPlacement>();
            var glyphs = new List<NativePagePaint>(flowEnd - flowStart);
            var backgrounds = new List<(int Depth, NativePagePaint Paint)>();
            var listMarkers = new List<EpubListMarker>();
            int pageStart = flowStart;
            bool pageHasContent = false;
            bool pendingBreakAfter = false;
            double left = contentLeft;
            var line = new List<(int Index, int Length, double Left, double Width, TrueTypeFont Font, PdfGlyphOutline? Outline,
                PngDecodedImage? Image, EpubNativeContent.Style Style, EpubNativeContent.Style BlockStyle,
                EpubNativeContent.Element? Owner)>();
            int lineStart = 0;
            EpubFormattingTree.Block lineBlock = flowRoot;
            var startedBlocks = new HashSet<EpubFormattingTree.Block>();
            var markedItems = new HashSet<EpubFormattingTree.ListItem>();
            var itemContentLeft = new Dictionary<EpubFormattingTree.ListItem, double>();
            var initialPadding = new Dictionary<EpubFormattingTree.Block,
                (double Height, int PaintStart, double BorderHeight, PdfRgbColor? BorderColor)>();
            var activeBounds = new Dictionary<EpubFormattingTree.Block, (double Left, double Width, double ContainingWidth)>();
            // Cells of a row taller than a page, laid out one after another as ordinary blocks.
            var blockCells = new HashSet<EpubFormattingTree.Block>();
            double pendingParagraphGap = 0;
            // Resume points, chapter flow only. While resuming, the blocks on the recorded path are entered again without
            // repeating what they already did on earlier pages, down to the block whose start was recorded; from there
            // layout runs as it did the first time. The pages before the recorded one are placeholders.
            int[]? resumePath = null;
            int resumeLevel = 0;
            EpubFormattingTree.Block? candidateBlock = null;
            EpubResumePoint? candidate = null;
            if (chapterRoot && resume is not null)
            {
                for (int page = 0; page < resume.Page; page++) pages.Add(null!);
                top = resume.Top;
                pendingParagraphGap = resume.PendingGap;
                pendingBreakAfter = resume.PendingBreakAfter;
                pageHasContent = resume.PageHasContent;
                pageStart = resume.PageStart;
                resumePath = resume.Path;
            }
            // The chapter's first page needs no resume point.
            if (chapterRoot) resumePoints?.Add(null);
            try
            {
                LayoutBlock(flowRoot);
                cancellationToken.ThrowIfCancellationRequested();
                if (chapterRoot && bodyPadding.Bottom > 0 && top + bodyPadding.Bottom > options.PageHeight - options.Margin)
                    throw new NotSupportedException("Native EPUB terminal body padding overflow is not implemented.");
                FinishPage(flowEnd);
            }
            catch (EpubCellFlowOverflow) { return ([], top); }
            return (pages, top);

            void LayoutBlock(EpubFormattingTree.Block block)
            {
                if (tableFlowDepth > 0) ChargeTableFlowWork();
                // A block on the resume path above the recorded one began on an earlier page: it is entered again for its
                // box, and what its start did then (breaks, top padding, its margin) is not repeated. The recorded block
                // itself, and every block after it, lays out as the first time.
                int level = resumeLevel;
                bool resumedThrough = resumePath is not null && level < resumePath.Length;
                if (resumePath is not null && !resumedThrough) resumePath = null;
                if (!resumedThrough) RecordCandidate(block);
                else
                {
                    if ((resume!.Started >> level & 1) != 0) startedBlocks.Add(block);
                    if (block is EpubFormattingTree.ListItem resumedItem && (resume.Marked >> level & 1) != 0)
                        markedItems.Add(resumedItem);
                }
                double parentLeft = contentLeft, parentRight = contentRight, parentWidth = contentWidth;
                var margins = block.Parent is null ? (Left: 0d, Right: 0d) : block.Source.HorizontalInsets(parentWidth);
                var padding = block.Parent is null ? (Left: 0d, Right: 0d) : block.Source.HorizontalInsets(parentWidth, padding: true);
                contentLeft += margins.Left;
                contentRight -= margins.Right;
                if (block.Source.Width is { Length: { } specifiedWidth }
                    && !(ReferenceEquals(block, flowRoot) && block is EpubFormattingTree.Cell))
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
                if (block is EpubFormattingTree.ListItem listItem) itemContentLeft.Add(listItem, contentLeft);
                if (contentWidth <= 0)
                    throw new NotSupportedException("Native EPUB paragraph insets require a positive content box.");
                activeBounds.Add(block, (backgroundBounds.Left, backgroundBounds.Width, parentWidth));
                try
                {
                    if (!resumedThrough && block.Parent is not null && pageHasContent
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
                    if (!resumedThrough && (paddingTop > 0 || paddingBottom > 0 || borderTop > 0) && block.Source.IsParagraph)
                    {
                        pendingParagraphGap = Math.Max(pendingParagraphGap, block.Source.VerticalMargin(0,
                            activeBounds[block].ContainingWidth, pageHasContent ? options.FontSize : 0));
                        top += !pageHasContent && existingPageCount + pages.Count == 0 && bodyPadding.Top == 0
                            ? Math.Max(0, pendingParagraphGap - bodyMargins.Top) : pendingParagraphGap;
                        pendingParagraphGap = 0;
                    }
                    int initialPaintStart = backgrounds.Count;
                    PdfRgbColor? borderColor = block.Source.Style.Hidden ? null : topBorder is { Color: { } specified }
                        ? specified.Color : block.Source.Style.Color ?? options.TextColor;
                    double emptyBorderRemaining = 0;
                    if (block.Children.Count == 0 && block.Replaced is null)
                    {
                        emptyBorderRemaining = borderTop;
                        paddingBottom += paddingTop + borderTop;
                        paddingTop = 0;
                        borderTop = 0;
                    }
                    if (resumedThrough)
                    {
                        // Its top border and padding were drawn when it began; what still waits to move with its first
                        // line is as recorded.
                        foreach (var waiting in resume!.Padding ?? [])
                            if (waiting.Depth == level)
                                initialPadding[block] = (waiting.Height, waiting.PaintStart, waiting.BorderHeight, waiting.BorderColor);
                    }
                    else
                    {
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
                    }
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

                    if (block is EpubFormattingTree.ListItem completedItem && completedItem.Source.ListOrdinal is null
                        && !markedItems.Contains(completedItem) && !completedItem.Source.Style.Hidden && !reportedListMarker)
                    {
                        reportedListMarker = true;
                        contentDiagnostics.Add(new(spineIndex, resource.Href,
                            "A list marker was left out because its item has no supported first line.")
                        { Reason = EpubOmissionReason.UnsupportedLayout });
                    }

                    void PaintPadding(double height)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (height <= 0) return;
                        for (var box = block; box.Parent is not null && !ReferenceEquals(box, flowRoot.Parent); box = box.Parent)
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
                            if ((!atom.IsSpace || atom.Owner.Style.PreserveWhitespace) && (atom.Reference is not { Source: not null } image
                                || image.Owner.MaximumWidth is not { Length.Value: 0 })) return true;
                        }
                    if (child is EpubFormattingTree.Block nested && HasFlowContent(nested)) return true;
                }
                return false;
            }

            void LayoutBlockContent(EpubFormattingTree.Block block)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (block is EpubFormattingTree.Table table)
                {
                    tableFlowDepth++;
                    try { ChargeTableFlowWork(); LayoutTable(table); }
                    finally { tableFlowDepth--; }
                    return;
                }
                if (block is EpubFormattingTree.RowGroup or EpubFormattingTree.Row
                    || block is EpubFormattingTree.Cell && !ReferenceEquals(block, flowRoot) && !blockCells.Contains(block))
                    throw new NotSupportedException("Native EPUB table parts require their table flow.");
                double indent = block.Source.Style.TextIndentPercentage
                    ? block.Source.Style.TextIndent * contentWidth / 100 : block.Source.Style.TextIndent;
                if (!double.IsFinite(indent) || Math.Abs(indent) > 32768)
                    throw new NotSupportedException("Native EPUB text indentation exceeds the supported page dimensions.");
                int boundary = Math.Min(block.Source.Offset, text.Length);
                while (boundary < text.Length && content.Characters[boundary] is null) boundary++;
                // Resuming through this block: its top margin is already in the recorded pending gap, and the children
                // before the one on the path were laid out on earlier pages.
                bool resumedHere = resumePath is not null;
                int firstPart = resumedHere ? resumePath![resumeLevel] : 0;
                if (!resumedHere && block.Source.HasBlockMargins && block.Source.VerticalMargin(0,
                    activeBounds[block].ContainingWidth, 0, padding: true) == 0
                    && block.Source.VerticalMargin(2, activeBounds[block].ContainingWidth, 0, padding: true) == 0
                    && block.Source.TopBorder is not { Visible: true, Width.Value: > 0 })
                {
                    pendingParagraphGap = Math.Max(pendingParagraphGap, block.Source.VerticalMargin(0,
                        activeBounds[block].ContainingWidth, block.Source.IsParagraph && pageHasContent ? options.FontSize : 0));
                }
                if (block.Replaced is { Source: { } source } replaced)
                {
                    lineBlock = block;
                    if (TryReadImage(source) is not { } image) return;
                    var imageSize = ImageSize(image, replaced.Owner);
                    double paintLeft = contentLeft, paintTop = 0, flowHeight = imageSize.Height;
                    if (replaced.Owner.SvgCover is { } cover)
                    {
                        flowHeight = options.PageHeight - 2 * options.Margin - bodyMargins.Top
                            - bodyMargins.Bottom - bodyPadding.Top - bodyPadding.Bottom;
                        if (!double.IsFinite(flowHeight) || flowHeight <= 0)
                            throw new NotSupportedException("Native EPUB SVG covers require a positive body viewport.");
                        // SVG 1.1 section 7.8: the viewBox maps into the body viewport, then the raster maps into the
                        // image rectangle; what falls outside the image rectangle or the viewport is clipped.
                        var outer = cover.Aspect.Fit(cover.Width, cover.Height, contentWidth, flowHeight);
                        var inner = cover.ImageAspect.Fit(image.Width, image.Height, cover.ImageWidth, cover.ImageHeight);
                        double X(double user) => outer.OffsetX + (user - cover.MinX) * outer.ScaleX;
                        double Y(double user) => outer.OffsetY + (user - cover.MinY) * outer.ScaleY;
                        // SVG 1.1 section 7.6: the image's transform maps its fitted raster and its clip rectangle into
                        // viewBox units. A transform that keeps the axes turns or flips the raster whole, so both
                        // rectangles stay axis-aligned; a rotation or skew off the axes resamples the raster into its
                        // bounding box, transparent outside its turned clip rectangle.
                        var turn = cover.Transform;
                        var raster = (X: cover.ImageX + inner.OffsetX, Y: cover.ImageY + inner.OffsetY,
                            Width: image.Width * inner.ScaleX, Height: image.Height * inner.ScaleY);
                        var clip = (X: cover.ImageX, Y: cover.ImageY, Width: cover.ImageWidth, Height: cover.ImageHeight);
                        var untransformed = raster;
                        var untransformedClip = clip;
                        if (turn != EpubSvgMatrix.Identity)
                        {
                            raster = turn.Bounds(raster.X, raster.Y, raster.Width, raster.Height);
                            clip = turn.Bounds(clip.X, clip.Y, clip.Width, clip.Height);
                            if (turn.KeepsAxes && (turn.A <= 0 || turn.D <= 0))
                                image = LoadImage(imageKeys[image] with
                                {
                                    TurnA = Math.Sign(turn.A), TurnB = Math.Sign(turn.B), TurnC = Math.Sign(turn.C), TurnD = Math.Sign(turn.D)
                                });
                        }
                        double rasterLeft = X(raster.X), rasterTop = Y(raster.Y);
                        double rasterWidth = raster.Width * outer.ScaleX, rasterHeight = raster.Height * outer.ScaleY;
                        if (!turn.KeepsAxes)
                        {
                            // The output keeps the source's pixel size on the page: one output pixel per source pixel along
                            // x. Source pixel (x, y) lies at viewBox point raster + (x, y) * inner scale before the transform.
                            double perPixelX = inner.ScaleX * outer.ScaleX, perPixelY = inner.ScaleY * outer.ScaleY;
                            double density = 1 / Math.Sqrt(Math.Abs(perPixelX * perPixelY));
                            int warpWidth = (int)Math.Ceiling(rasterWidth * density - 1e-9);
                            int warpHeight = (int)Math.Ceiling(rasterHeight * density - 1e-9);
                            if (warpWidth <= 0 || warpHeight <= 0) return;
                            if (warpWidth > 8192 || warpHeight > 8192 || (long)warpWidth * warpHeight > 16_000_000)
                                throw new EpubImageUnavailableException("Native EPUB SVG image is too large to resample.", EpubOmissionReason.ImageTooLarge);
                            // Output pixel coordinates: page offsets from the bounding box's corner, times the density.
                            double a = density * outer.ScaleX * turn.A * inner.ScaleX, b = density * outer.ScaleY * turn.B * inner.ScaleX;
                            double c = density * outer.ScaleX * turn.C * inner.ScaleY, d = density * outer.ScaleY * turn.D * inner.ScaleY;
                            double originX = turn.A * untransformed.X + turn.C * untransformed.Y + turn.E;
                            double originY = turn.B * untransformed.X + turn.D * untransformed.Y + turn.F;
                            var warp = new EpubImageWarp(a, b, c, d, density * (X(originX) - rasterLeft), density * (Y(originY) - rasterTop),
                                warpWidth, warpHeight,
                                (untransformedClip.X - untransformed.X) / inner.ScaleX, (untransformedClip.Y - untransformed.Y) / inner.ScaleY,
                                (untransformedClip.X + untransformedClip.Width - untransformed.X) / inner.ScaleX,
                                (untransformedClip.Y + untransformedClip.Height - untransformed.Y) / inner.ScaleY);
                            image = LoadImage(imageKeys[image] with { Warp = warp });
                            rasterWidth = warpWidth / density;
                            rasterHeight = warpHeight / density;
                        }
                        double clipLeft = Math.Max(0, X(clip.X)), clipTop = Math.Max(0, Y(clip.Y));
                        double clipRight = Math.Min(contentWidth, X(clip.X + clip.Width));
                        double clipBottom = Math.Min(flowHeight, Y(clip.Y + clip.Height));
                        // Crop whole source pixels that lie beyond the clip; the paint covers exactly what remains.
                        int firstColumn = (int)Math.Clamp(Math.Floor((clipLeft - rasterLeft) / rasterWidth * image.Width + 1e-9), 0, image.Width);
                        int lastColumn = (int)Math.Clamp(Math.Ceiling((clipRight - rasterLeft) / rasterWidth * image.Width - 1e-9), firstColumn, image.Width);
                        int firstRow = (int)Math.Clamp(Math.Floor((clipTop - rasterTop) / rasterHeight * image.Height + 1e-9), 0, image.Height);
                        int lastRow = (int)Math.Clamp(Math.Ceiling((clipBottom - rasterTop) / rasterHeight * image.Height - 1e-9), firstRow, image.Height);
                        if (lastColumn == firstColumn || lastRow == firstRow) return;
                        double pixelWidth = rasterWidth / image.Width, pixelHeight = rasterHeight / image.Height;
                        paintLeft += rasterLeft + firstColumn * pixelWidth;
                        paintTop = rasterTop + firstRow * pixelHeight;
                        imageSize = ((lastColumn - firstColumn) * pixelWidth, (lastRow - firstRow) * pixelHeight);
                        if (firstColumn != 0 || firstRow != 0 || lastColumn != image.Width || lastRow != image.Height)
                            image = LoadImage(imageKeys[image] with
                            {
                                CropLeft = firstColumn, CropTop = firstRow,
                                CropWidth = lastColumn - firstColumn, CropHeight = lastRow - firstRow
                            });
                    }
                    if (imageSize.Width == 0) return;
                    if (pendingParagraphGap > 0)
                    {
                        top += !pageHasContent && existingPageCount + pages.Count == 0 && bodyPadding.Top == 0
                            ? Math.Max(0, pendingParagraphGap - bodyMargins.Top) : pendingParagraphGap;
                        pendingParagraphGap = 0;
                    }
                    if (top + flowHeight > options.PageHeight - options.Margin)
                    {
                        if (pageHasContent || !chapterRoot || pages.Count != 0 || resume is not null)
                            FinishPage(replaced.Offset);
                        top = options.Margin;
                    }
                    AddBlockBackgrounds(flowHeight);
                    if (!replaced.Owner.Style.Hidden)
                        glyphs.Add(new EpubDeferredImagePaint(imageKeys[image], paintLeft, top + paintTop, imageSize.Width, imageSize.Height));
                    if (backgrounds.Count + glyphs.Count > 100_000)
                        throw new InvalidDataException("The native page exceeds its paint limit.");
                    top += flowHeight;
                    pageHasContent = true;
                    return;
                }
                for (int partIndex = firstPart; partIndex < block.Children.Count; partIndex++)
                {
                    var part = block.Children[partIndex];
                    cancellationToken.ThrowIfCancellationRequested();
                    if (part is EpubFormattingTree.Block child)
                    {
                        if (resumedHere && partIndex == firstPart) resumeLevel++;
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
                if (block.Source.HasBlockMargins)
                {
                    bool hasExtent = pageHasContent
                        || block.Source.TopBorder is { Visible: true, Width.Value: > 0 }
                        || block.Source.VerticalMargin(0, activeBounds[block].ContainingWidth, 0, padding: true) > 0
                        || block.Source.VerticalMargin(2, activeBounds[block].ContainingWidth, 0, padding: true) > 0;
                    pendingParagraphGap = Math.Max(pendingParagraphGap, block.Source.VerticalMargin(2,
                        activeBounds[block].ContainingWidth, block.Source.IsParagraph && hasExtent ? options.FontSize : 0));
                }
            }

            void LayoutTable(EpubFormattingTree.Table table)
            {
                const string columnsUnsupported = "Native EPUB explicit table columns must fill the width and match across rows.";
                double[]? columns = null;
                bool specified = table.Source.Width?.Length is not null;
                bool automatic = !specified || Rows().SelectMany(row => row.Children)
                    .OfType<EpubFormattingTree.Cell>().All(cell => cell.Source.Width?.Length is null);
                if ((specified && table.Source.Width is not { Length: { Value: > 0 } }) || table.Children.Count == 0)
                    throw new NotSupportedException("Native EPUB tables require explicit width and row content.");
                if (automatic)
                {
                    // CSS Tables automatic layout: the widest row's spans set the column count; single-column cells
                    // size their columns, and a spanning cell wider than its columns widens them evenly.
                    int columnCount = 0;
                    foreach (var row in Rows())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (row.Children.Count == 0 || row.Children.Any(child => child is not EpubFormattingTree.Cell))
                            throw new NotSupportedException("Native EPUB rows require direct cells.");
                        columnCount = Math.Max(columnCount, row.Children.Sum(child => Span((EpubFormattingTree.Cell)child)));
                    }
                    double[]? minimums = null;
                    var spanning = new List<(int First, int Span, (double Minimum, double Maximum) Intrinsic)>();
                    if (columnCount > 0)
                    {
                        columns = new double[columnCount];
                        minimums = new double[columnCount];
                    }
                    foreach (var row in Rows())
                    {
                        int column = 0;
                        foreach (var child in row.Children)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var cell = (EpubFormattingTree.Cell)child;
                            if (cell.Source.Width?.Length is not null)
                                throw new NotSupportedException("Native EPUB automatic tables currently require automatic cells.");
                            int span = Span(cell);
                            var intrinsic = IntrinsicWidths(cell);
                            if (span == 1)
                            {
                                columns![column] = Math.Max(columns[column], intrinsic.Maximum);
                                minimums![column] = Math.Max(minimums[column], intrinsic.Minimum);
                            }
                            else spanning.Add((column, span, intrinsic));
                            column += span;
                        }
                    }
                    foreach (var (first, span, intrinsic) in spanning)
                    {
                        double maximumShare = Math.Max(0, intrinsic.Maximum - columns!.Skip(first).Take(span).Sum()) / span;
                        double minimumShare = Math.Max(0, intrinsic.Minimum - minimums!.Skip(first).Take(span).Sum()) / span;
                        for (int column = first; column < first + span; column++)
                        {
                            columns![column] += maximumShare;
                            minimums![column] += minimumShare;
                        }
                    }
                    // A column of only images or empty cells keeps a share of the width so its content can shrink into it.
                    if (columns is not null)
                        for (int column = 0; column < columns.Length; column++)
                            if (columns[column] <= 0) columns[column] = Math.Max(1, contentWidth / (4 * columns.Length));
                    double width = columns?.Sum() ?? 0;
                    double minimum = minimums?.Sum() ?? 0;
                    if (columns is null || columns.Any(value => value <= 0) || !double.IsFinite(width) || minimum > contentWidth)
                        throw new NotSupportedException("Native EPUB intrinsic table columns require positive widths that fit the page.");
                    if (width > contentWidth)
                    {
                        double fraction = (contentWidth - minimum) / (width - minimum);
                        for (int column = 0; column < columns.Length; column++)
                            columns[column] = minimums![column] + (columns[column] - minimums[column]) * fraction;
                        width = contentWidth;
                    }
                    if (specified && width < contentWidth)
                    {
                        double share = (contentWidth - width) / columns.Length;
                        for (int column = 0; column < columns.Length; column++) columns[column] += share;
                        width = contentWidth;
                    }
                    double remaining = contentWidth - width;
                    if (table.Source.Margins?[3] is { Auto: true })
                        contentLeft += table.Source.Margins?[1] is { Auto: true } ? remaining / 2 : remaining;
                    contentWidth = width;
                    contentRight = contentLeft + width;
                    var bounds = activeBounds[table];
                    activeBounds[table] = (contentLeft, width, bounds.ContainingWidth);
                }
                if (pendingParagraphGap > 0)
                {
                    top += !pageHasContent && existingPageCount + pages.Count == 0 && bodyPadding.Top == 0
                        ? Math.Max(0, pendingParagraphGap - bodyMargins.Top) : pendingParagraphGap;
                    pendingParagraphGap = 0;
                }
                // A caption (HTML caption-side: top) lays out above the rows at the table's width.
                foreach (var part in table.Children)
                    if (part is EpubFormattingTree.Block caption and not (EpubFormattingTree.Row or EpubFormattingTree.RowGroup))
                        LayoutBlock(caption);
                bool hadRow = false;
                // CSS 2.1 section 17.5.2.1 fixed layout: the first row whose cells each span one column and give a width
                // sets the columns; a spanning cell, or one without a width, covers the columns it spans.
                if (!automatic)
                    foreach (var candidate in Rows())
                        if (candidate.Children.Count > 0 && candidate.Children.All(child => child is EpubFormattingTree.Cell { Source.Name.Length: > 0 } cell
                            && Span(cell) == 1 && cell.Source.Width is { Length: { Value: > 0 } }))
                        {
                            columns = candidate.Children.Select(child => GivenWidth((EpubFormattingTree.Cell)child)).ToArray();
                            break;
                        }
                foreach (var row in Rows())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    hadRow = true;
                    if (row.Children.Count == 0 || row.Children.Any(child => child is not EpubFormattingTree.Cell))
                        throw new NotSupportedException("Native EPUB rows require direct cells.");
                    int columnIndex = 0;
                    var cells = new List<(EpubNativeContent.Element Source, EpubLayoutPage Page, double Left)>();
                    var widths = new List<double>();
                    int rowPaintCount = 0;
                    double rowLeft = contentLeft, rowHeight = 0;
                    bool tallRow = false;
                    foreach (var child in row.Children)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var cell = (EpubFormattingTree.Cell)child;
                        bool given = cell.Source.Width is { Length: { Value: > 0 } };
                        int span = Span(cell);
                        // An anonymous cell (CSS 2.1 section 17.2.1) takes the rest of its row in a fixed-width table.
                        if (!automatic && cell.Source.Name.Length == 0 && columns is not null) span = Math.Max(1, columns.Length - columnIndex);
                        double cellWidth;
                        if (automatic) cellWidth = columns!.Skip(columnIndex).Take(span).Sum();
                        else if (columns is not null && (span > 1 || !given))
                        {
                            if (columnIndex + span > columns.Length) throw new NotSupportedException(columnsUnsupported);
                            cellWidth = columns.Skip(columnIndex).Take(span).Sum();
                            if (given && Math.Abs(GivenWidth(cell) - cellWidth) > 0.0000001)
                                throw new NotSupportedException(columnsUnsupported);
                        }
                        else
                        {
                            if (!given) throw new NotSupportedException("Native EPUB cells require explicit positive widths.");
                            cellWidth = GivenWidth(cell);
                            if (columns is not null && (columnIndex >= columns.Length || Math.Abs(columns[columnIndex] - cellWidth) > 0.0000001))
                                throw new NotSupportedException(columnsUnsupported);
                        }
                        columnIndex += span;
                        if (!double.IsFinite(cellWidth) || rowLeft + cellWidth - contentRight > 0.0000001)
                            throw new NotSupportedException("Native EPUB cell widths exceed the table content box.");
                        widths.Add(cellWidth);
                        var measured = LayoutFlow(cell, rowLeft, rowLeft + cellWidth, options.Margin);
                        if (measured.Pages.Count != 1)
                        {
                            tallRow = true;
                            break;
                        }
                        rowPaintCount += measured.Pages[0].PaintCount;
                        if (rowPaintCount > 100_000)
                            throw new InvalidDataException("The native page exceeds its paint limit.");
                        cells.Add((cell.Source, measured.Pages[0], rowLeft));
                        rowHeight = Math.Max(rowHeight, measured.Bottom - options.Margin);
                        rowLeft += cellWidth;
                    }
                    if (tallRow)
                    {
                        LayoutRowAsBlocks(row);
                        continue;
                    }
                    // An automatic row with fewer cells leaves its last columns empty, as CSS Tables does.
                    if (!automatic && Math.Abs(rowLeft - contentRight) > 0.0000001)
                        throw new NotSupportedException(columnsUnsupported);
                    if (row.Children.All(child => Span((EpubFormattingTree.Cell)child) == 1)) columns ??= widths.ToArray();
                    if (top + rowHeight > options.PageHeight - options.Margin)
                    {
                        FinishPage(Math.Min(row.Source.Offset, text.Length));
                        top = options.Margin;
                        pendingParagraphGap = 0;
                        pendingBreakAfter = false;
                    }
                    lineBlock = table;
                    AddBlockBackgrounds(rowHeight);
                    if (backgrounds.Count + glyphs.Count + rowPaintCount > 100_000)
                        throw new InvalidDataException("The native page exceeds its paint limit.");
                    double shift = top - options.Margin;
                    double firstBaseline = cells.SelectMany(cell => cell.Page.Glyphs).OfType<NativeGlyphPaint>()
                        .FirstOrDefault()?.Baseline + shift ?? top + options.FontSize;
                    PaintListMarker(table, firstBaseline);
                    foreach (var (source, cell, cellLeft) in cells)
                    {
                        if (anchorsByCell.TryGetValue(source, out var ownedAnchors))
                        {
                            int navigationIndex = 0;
                            foreach (var anchor in ownedAnchors)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                while (navigationIndex < cell.NavigationPlacements.Count
                                    && cell.NavigationPlacements[navigationIndex].Start < anchor.Offset)
                                {
                                    cancellationToken.ThrowIfCancellationRequested();
                                    navigationIndex++;
                                }
                                double anchorLeft = cellLeft, anchorTop = options.Margin;
                                if (navigationIndex < cell.NavigationPlacements.Count)
                                {
                                    var next = cell.NavigationPlacements[navigationIndex];
                                    anchorLeft = next.Left;
                                    anchorTop = next.Top;
                                }
                                else if (navigationIndex > 0)
                                {
                                    var previous = cell.NavigationPlacements[navigationIndex - 1];
                                    anchorLeft = previous.Left + previous.Width;
                                    anchorTop = previous.Top;
                                }
                                cellAnchors[(spineIndex, anchor.Id, anchor.Offset)] = new(
                                    existingPageCount + pages.Count, spineIndex, anchor.Offset, anchorLeft, anchorTop + shift);
                            }
                        }
                        foreach (var placement in cell.TextPlacements)
                            placements.Add(placement with { Top = placement.Top + shift });
                        foreach (var placement in cell.NavigationPlacements)
                            navigationPlacements.Add(placement with { Top = placement.Top + shift });
                        foreach (var marker in cell.Markers)
                            listMarkers.Add(marker with { Baseline = marker.Baseline + shift });
                        foreach (var paint in cell.StoredPaints)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            glyphs.Add(paint switch
                            {
                                NativeGlyphPaint glyph => glyph with { Baseline = glyph.Baseline + shift },
                                NativeMarkerPaint marker => marker with { CenterY = marker.CenterY + shift },
                                EpubDeferredImagePaint image => image with { Top = image.Top + shift },
                                NativeRectanglePaint rectangle => rectangle with { Top = rectangle.Top + shift },
                                _ => throw new NotSupportedException("Native EPUB cell paint is not implemented.")
                            });
                        }
                    }
                    if (backgrounds.Count + glyphs.Count > 100_000)
                        throw new InvalidDataException("The native page exceeds its paint limit.");
                    top += rowHeight;
                    pageHasContent |= rowHeight > 0;
                }
                if (!hadRow)
                    throw new NotSupportedException("Native EPUB tables require explicit width and row content.");

                // A row that cannot fit one page keeps all of its content: its cells flow down the table's
                // content box one after another and paginate like any block, instead of refusing the chapter.
                void LayoutRowAsBlocks(EpubFormattingTree.Row row)
                {
                    var added = new List<EpubFormattingTree.Block>();
                    for (EpubFormattingTree.Block? box = row; box is not null && !ReferenceEquals(box, table); box = box.Parent)
                        if (activeBounds.TryAdd(box, (contentLeft, contentWidth, contentWidth))) added.Add(box);
                    try
                    {
                        foreach (var child in row.Children)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var cell = (EpubFormattingTree.Cell)child;
                            blockCells.Add(cell);
                            try { LayoutBlock(cell); }
                            finally { blockCells.Remove(cell); }
                        }
                    }
                    finally
                    {
                        foreach (var box in added) activeBounds.Remove(box);
                    }
                    lineBlock = table;
                }

                // CSS Tables: a cell's minimum is its widest unbreakable word and its maximum its widest line.
                // Block content in a cell contributes its own lines; line breaks end a line and images add nothing
                // here because they shrink to fit the column that results.
                (double Minimum, double Maximum) IntrinsicWidths(EpubFormattingTree.Block cell)
                {
                    ChargeTableFlowWork();
                    // A nested table's row is as wide as its cells together; the table takes its widest row.
                    if (cell is EpubFormattingTree.Row nestedRow)
                    {
                        double rowMinimum = 0, rowMaximum = 0;
                        foreach (var part in nestedRow.Children)
                            if (part is EpubFormattingTree.Block nestedCell)
                            {
                                var widths = IntrinsicWidths(nestedCell);
                                rowMinimum += widths.Minimum;
                                rowMaximum += widths.Maximum;
                            }
                        return (rowMinimum, rowMaximum);
                    }
                    double minimum = 0, maximum = 0;
                    foreach (var part in cell.Children)
                    {
                        if (part is EpubFormattingTree.Block nested)
                        {
                            var inner = IntrinsicWidths(nested);
                            minimum = Math.Max(minimum, inner.Minimum);
                            maximum = Math.Max(maximum, inner.Maximum);
                            continue;
                        }
                        if (part is not EpubFormattingTree.InlineSequence sequence) continue;
                        int first = 0, last = sequence.Atoms.Count - 1;
                        while (first <= last && sequence.Atoms[first].IsSpace) first++;
                        while (last >= first && sequence.Atoms[last].IsSpace) last--;
                        double lineWidth = 0, wordWidth = 0;
                        EpubNativeContent.Style? previous = null;
                        EpubNativeContent.Style? wordPrevious = null;
                        for (int at = first; at <= last; at++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (++inlineMetricWork > chapterWork)
                                throw new InvalidDataException("Native EPUB inline metrics exceed their work limit.");
                            ChargeTableFlowWork();
                            var atom = sequence.Atoms[at];
                            if (atom.Reference is not null || atom.HardBreak)
                            {
                                minimum = Math.Max(minimum, wordWidth);
                                if (atom.Reference is { Source: null } || atom.HardBreak)
                                {
                                    maximum = Math.Max(maximum, lineWidth);
                                    lineWidth = 0;
                                }
                                wordWidth = 0;
                                previous = wordPrevious = null;
                                continue;
                            }
                            if (InlineCluster(atom) is not { } cluster) continue;
                            var style = atom.Owner.Style;
                            lineWidth += Tracking(previous, style) + InlineAdvance(cluster, style, atom.IsSpace);
                            if (atom.IsSpace)
                            {
                                minimum = Math.Max(minimum, wordWidth);
                                wordWidth = 0;
                                wordPrevious = null;
                            }
                            else
                            {
                                wordWidth += Tracking(wordPrevious, style) + InlineAdvance(cluster, style, false);
                                wordPrevious = style;
                            }
                            previous = style;
                        }
                        minimum = Math.Max(minimum, wordWidth);
                        maximum = Math.Max(maximum, lineWidth);
                    }
                    return (minimum, maximum);
                }

                IEnumerable<EpubFormattingTree.Row> Rows()
                {
                    foreach (var part in table.Children)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (part is EpubFormattingTree.Row row) yield return row;
                        else if (part is EpubFormattingTree.RowGroup group)
                            foreach (var child in group.Children)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                if (child is not EpubFormattingTree.Row groupedRow)
                                    throw new NotSupportedException("Native EPUB row groups require direct rows.");
                                yield return groupedRow;
                            }
                        else if (part is not EpubFormattingTree.Block)
                            throw new NotSupportedException("Native EPUB tables require row content.");
                    }
                }

                double GivenWidth(EpubFormattingTree.Cell cell)
                {
                    var given = cell.Source.Width?.Length ?? default;
                    return given.Percentage ? contentWidth * given.Value / 100
                        : given.Em ? cell.Source.Style.FontSize * given.Value : given.Value;
                }

                // HTML colspan: a valid nonnegative integer, zero and invalid values meaning 1, capped at 1000.
                static int Span(EpubFormattingTree.Cell cell)
                    => cell.Source.Identity.Attributes.TryGetValue(("", "colspan"), out string? value)
                        && int.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int span) && span > 0
                        ? Math.Min(span, 1000) : 1;
            }

            void LayoutInline(EpubFormattingTree.InlineSequence sequence)
            {
                bool firstIndentedWord = !startedBlocks.Contains(lineBlock) && left > contentLeft;
                bool indentedWordMeasured = false;
                int first = 0, last = sequence.Atoms.Count - 1;
                while (first <= last && IsSpace(sequence.Atoms[first])) first++;
                while (last >= first && IsSpace(sequence.Atoms[last])) last--;
                if (first <= last) lineStart = sequence.Atoms[first].Offset;
                for (int at = first; at <= last; at++)
                {
                    if (tableFlowDepth > 0) ChargeTableFlowWork();
                    var atom = sequence.Atoms[at];
                    int index = atom.Offset;
                    cancellationToken.ThrowIfCancellationRequested();
                    if (pendingParagraphGap > 0)
                    {
                        top += !pageHasContent && existingPageCount + pages.Count == 0 && bodyPadding.Top == 0
                            ? Math.Max(0, pendingParagraphGap - bodyMargins.Top) : pendingParagraphGap;
                        pendingParagraphGap = 0;
                    }
                    var inlineReference = atom.Reference ?? (atom.HardBreak ? new EpubNativeContent.InlineReference(index, null, atom.Owner) : null);
                    if (inlineReference is { } reference)
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
                        if (TryReadImage(reference.Source) is not { } image) continue;
                        // An inline image shares its line with the block's descent, so it shrinks to leave room for it.
                        var imageSize = ImageSize(image, reference.Owner,
                            BelowBaseline(lineBlock.Source.Style) + BelowBaseline(reference.Owner.Style) + 1);
                        if (imageSize.Width == 0) continue;
                        double imageTracking = line.Count == 0 || line[^1].Image is not null ? 0
                            : (line[^1].Style.LetterSpacing + reference.Owner.Style.LetterSpacing) / 2;
                        if (imageTracking + imageSize.Width > contentRight - left && line.Count > 0)
                        {
                            // An image that does not fit the rest of the line starts the next line.
                            FlushLine(finalLine: false);
                            left = contentLeft;
                            lineStart = index;
                            imageTracking = 0;
                        }
                        left += imageTracking;
                        line.Add((index, 0, left, imageSize.Width, FontFor(reference.Owner.Style), null, image,
                            reference.Owner.Style, lineBlock.Source.Style, reference.Owner));
                        left += imageSize.Width;
                        firstIndentedWord = false;
                        continue;
                    }
                    // Ligature and surrogate continuations keep their source indices but have no separate placement.
                    var style = atom.Owner.Style;
                    if (InlineCluster(atom) is not { } cluster) continue;
                    char character = atom.IsSpace ? ' ' : text[index];
                    if (atom.IsSpace && line.Count == 0 && !style.PreserveWhitespace) continue;
                    ushort glyph = cluster.Glyphs[0].GlyphId;
                    if (glyph == 0 && character != ' ' && !cluster.Blank && !cluster.Missing)
                        throw new NotSupportedException("The native EPUB font has no glyph for this cluster.");
                    if (!readers.TryGetValue(cluster.Font, out var selectedReader))
                        readers.Add(cluster.Font, selectedReader = new TrueTypeOutlineReader(cluster.Font));
                    PdfGlyphOutline? outline = cluster.Blank ? null : selectedReader.Outline(glyph)
                        ?? throw new NotSupportedException("The native EPUB glyph outline format is not implemented.");
                    double width = InlineAdvance(cluster, style, character == ' ');
                    double tracking = Tracking(line.Count == 0 ? null : line[^1].Style, style);
                    // A preformatted tab reaches the next stop measured from the start of the line it is laid out on.
                    bool tab = atom.HasSource && text[index] == '\t' && style.PreserveWhitespace;
                    if (tab) PlaceTab();
                    if (character == ' ' && !style.PreserveWhitespace)
                    {
                        firstIndentedWord = false;
                        // Each collapsed separator scans only the following word, keeping lookahead linear.
                        double wordWidth = WordWidth(at + 1, style.LetterSpacing, out double leadingTracking, out int wordHyphen);
                        // A word whose first part ends at a soft hyphen needs room for the hyphen a break there shows.
                        if (wordHyphen >= 0) wordWidth += HyphenWidth(wordHyphen);
                        // A word wider than the whole line still starts its own line, then breaks where it overflows.
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
                    // An indented first word may overflow its line, unless it could not fit even an unindented one.
                    if (firstIndentedWord && !style.PreserveWhitespace && !indentedWordMeasured)
                    {
                        indentedWordMeasured = true;
                        if (WordWidth(at, 0, out _, out _) > contentWidth) firstIndentedWord = false;
                    }
                    if (tracking + width > contentRight - left && (!firstIndentedWord || style.PreserveWhitespace) && line.Count > 0)
                    {
                        // Decision 0009 emergency break: with no break opportunity left, the word (or preformatted
                        // line) breaks before the first character that no longer fits. A lone character wider than
                        // the line is placed anyway and clipped.
                        FlushLine(finalLine: false);
                        left = contentLeft;
                        lineStart = index;
                        tracking = 0;
                        firstIndentedWord = false;
                        if (tab) PlaceTab();
                    }
                    left += tracking;
                    line.Add((index, cluster.Length, left, width, cluster.Font, atom.HasSource ? outline : null, null, style,
                        lineBlock.Source.Style, atom.Owner));
                    left += width;

                    // CSS Text 3 tab-size 8: stops every eight space advances of the tab's font from the line start, and
                    // a tab that would advance less than half the width of its font's "0" (0.5em without one) goes on to
                    // the following stop. The cluster keeps the advance, so drawing and the PDF place the next letter there.
                    void PlaceTab()
                    {
                        var face = cluster.Font;
                        double size = style.FontSize;
                        double Em(ushort id) => face.GetAdvanceWidth(id) / (double)face.UnitsPerEm;
                        ushort spaceGlyph = face.GetGlyphId(' '), zeroGlyph = face.GetGlyphId('0');
                        double stop = 8 * size * (spaceGlyph == 0 ? .5 : Em(spaceGlyph));
                        double half = size * (zeroGlyph == 0 ? .5 : Em(zeroGlyph)) / 2;
                        double from = left + tracking - contentLeft;
                        double next = (Math.Floor(from / stop + 1e-9) + 1) * stop;
                        if (next - from < half - 1e-9) next += stop;
                        cluster = new(cluster.Start, cluster.Length, face, [new(cluster.Glyphs[0].GlyphId, (next - from) / size, 0, 0)],
                            cluster.Substituted) { Blank = cluster.Blank, Missing = cluster.Missing };
                        clusters[index] = cluster;
                        width = InlineAdvance(cluster, style, false);
                    }
                    // UAX #14: soft hyphen, zero-width space and the fixed-width spaces other than figure space allow
                    // a break after them; the line breaks there when the following segment does not fit.
                    if (atom.HasSource && BreakAfter(text[index]) && at < last && !IsSpace(sequence.Atoms[at + 1])
                        && sequence.Atoms[at + 1].Reference is null)
                    {
                        double segment = WordWidth(at + 1, style.LetterSpacing, out double segmentTracking, out int segmentHyphen);
                        // Breaking at the next soft hyphen would show a hyphen there, so the segment needs its width too.
                        if (segmentHyphen >= 0) segment += HyphenWidth(segmentHyphen);
                        if (left + segmentTracking + segment > contentRight)
                        {
                            if (text[index] == (char)0x00AD) ShowHyphen(index, style);
                            FlushLine(finalLine: false);
                            left = contentLeft;
                            lineStart = sequence.Atoms[at + 1].Offset;
                            firstIndentedWord = false;
                        }
                    }
                }

                // CSS Text 3 hyphens: manual: the line ends at the soft hyphen, so it shows the font's hyphen (U+2010, or the
                // hyphen-minus). The page text keeps the soft hyphen, so copy and search read the word whole, and the PDF
                // draws the hyphen glyph as text U+00AD, which readers join across the break.
                void ShowHyphen(int at, EpubNativeContent.Style shown)
                {
                    var item = line[^1];
                    if (item.Index != at || !clusters.TryGetValue(at, out var blank)) return;
                    var face = blank.Font;
                    ushort glyph = face.GetGlyphId(0x2010) is var dash and not 0 ? dash : face.GetGlyphId('-');
                    if (glyph == 0) return;
                    if (!readers.TryGetValue(face, out var faceReader))
                        readers.Add(face, faceReader = new TrueTypeOutlineReader(face));
                    if (faceReader.Outline(glyph) is not { } outline) return;
                    double advance = face.GetAdvanceWidth(glyph) / (double)face.UnitsPerEm;
                    clusters[at] = new(at, blank.Length, face, [new(glyph, advance, 0, 0)], substituted: true);
                    item.Width = advance * shown.FontSize;
                    item.Outline = outline;
                    line[^1] = item;
                    left = item.Left + item.Width;
                }

                bool IsSpace(EpubFormattingTree.Atom atom) => atom.IsSpace && !atom.Owner.Style.PreserveWhitespace;
                static bool BreakAfter(char value) => value is (char)0x00AD or (char)0x200B
                    or >= (char)0x2000 and <= (char)0x200A and not (char)0x2007;

                // The width of the hyphen a line break at this soft hyphen shows, as ShowHyphen draws it.
                double HyphenWidth(int at)
                {
                    if (!clusters.TryGetValue(at, out var blank)) return 0;
                    var face = blank.Font;
                    ushort glyph = face.GetGlyphId(0x2010) is var dash and not 0 ? dash : face.GetGlyphId('-');
                    return glyph == 0 ? 0 : face.GetAdvanceWidth(glyph) / (double)face.UnitsPerEm * StyleAt(at).FontSize;
                }

                // Measures the word starting at an atom up to the next space or replaced element, or through the next
                // break opportunity; softHyphen is that opportunity's index when it is a soft hyphen, otherwise -1.
                double WordWidth(int from, double previousTracking, out double leadingTracking, out int softHyphen)
                {
                    double wordWidth = 0;
                    leadingTracking = 0;
                    softHyphen = -1;
                    bool firstCharacter = true;
                    for (int next = from; next <= last; next++)
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
                            wordWidth += InlineAdvance(nextCluster, nextStyle, false);
                            previousTracking = nextStyle.LetterSpacing;
                            firstCharacter = false;
                            // A break opportunity after this character ends the segment that must fit.
                            if (BreakAfter(text[nextAtom.Offset]))
                            {
                                if (text[nextAtom.Offset] == (char)0x00AD) softHyphen = nextAtom.Offset;
                                break;
                            }
                        }
                    }
                    return wordWidth;
                }
            }

            string? ResolveLink(EpubNativeContent.Element? owner)
            {
                if (owner?.LinkReference is not { } reference) return null;
                if (reference.Length > 4096)
                    throw new InvalidDataException("Native EPUB hyperlink targets exceed their 4,096-character limit.");
                if (!linkTargets.TryGetValue(reference, out string? target))
                {
                    string trimmed = reference.Trim(' ', '\t', '\r', '\n');
                    // A target outside the book leaves the text without a link (reported once per chapter).
                    target = TryExternalHyperlink(trimmed, out string external)
                        ? external : TryNavigationTarget(resource.Href, trimmed);
                    if (target?.Length > 4096)
                        throw new InvalidDataException("Native EPUB hyperlink targets exceed their 4,096-character limit.");
                    linkTargets.Add(reference, target);
                }
                return target;
            }

            EpubNativeContent.Style StyleAt(int index) => content.Characters[index]?.Style ?? content.DefaultStyle;

            EpubGlyphCluster? InlineCluster(EpubFormattingTree.Atom atom)
            {
                if (atom.HasSource) return clusters.GetValueOrDefault(atom.Offset);
                var spaceFont = FontFor(atom.Owner.Style);
                ushort spaceGlyph = spaceFont.GetGlyphId(' ');
                return new(atom.Offset, 0, spaceFont,
                    [new(spaceGlyph, spaceFont.GetAdvanceWidth(spaceGlyph) / (double)spaceFont.UnitsPerEm, 0, 0)]);
            }

            double InlineAdvance(EpubGlyphCluster cluster, EpubNativeContent.Style style, bool space)
                => cluster.Advance * style.FontSize
                    + (space || cluster.Length == 1 && text[cluster.Start] == '\u00A0' ? style.WordSpacing : 0);

            static double Tracking(EpubNativeContent.Style? previous, EpubNativeContent.Style current)
                => previous is null ? 0 : (previous.LetterSpacing + current.LetterSpacing) / 2;

            static int SourceDepth(EpubNativeContent.Element source)
            {
                int depth = 0;
                while (source.Parent is { } parent) { depth++; source = parent; }
                return depth;
            }

            void AddBlockBackgrounds(double height)
            {
                for (var box = lineBlock; box.Parent is not null && !ReferenceEquals(box, flowRoot.Parent); box = box.Parent)
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
                        if (pageHasContent || !chapterRoot || pages.Count != 0 || resume is not null
                            || line.All(item => item.Image is null)) FinishPage(start);
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
                    && !line[^1].Style.PreserveWhitespace
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
                        if (++inlineMetricWork > chapterWork)
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
                if (block.TextAlign == "justify" && !finalLine && !block.PreserveWhitespace)
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
                PaintListMarker(lineBlock, baseline);
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
                            glyphs.Add(new EpubDeferredImagePaint(imageKeys[image], item.Left, baseline - imageHeight, item.Width, imageHeight));
                        }
                        continue;
                    }
                    if (item.Length == 0) continue;
                    double size = item.Style.FontSize;
                    double logicalTop = baseline - item.Font.Ascender * size / item.Font.UnitsPerEm;
                    var placement = new EpubTextPlacement(spineIndex, item.Index, item.Length, item.Left, logicalTop, item.Width, size)
                    {
                        Color = item.Style.Color,
                        LinkTarget = ResolveLink(item.Owner)
                    };
                    navigationPlacements.Add(placement);
                    if (item.Style.Hidden) continue;
                    placements.Add(placement);
                    if (item.Owner is { HasUnderline: true } decoratedOwner)
                    {
                        for (var owner = decoratedOwner; owner is not null; owner = owner.Parent)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (++decorationWork > chapterWork)
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
                    // Blank characters keep their placement for selection but paint nothing.
                    if (cluster.Blank) continue;
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

            void PaintListMarker(EpubFormattingTree.Block firstBlock, double baseline)
            {
                for (var owner = firstBlock; owner is not null && !ReferenceEquals(owner, flowRoot.Parent); owner = owner.Parent)
                    if (owner is EpubFormattingTree.ListItem item)
                    {
                        if (markedItems.Add(item) && !item.Source.Style.Hidden)
                        {
                            var style = item.Source.Style;
                            if (item.Source.ListOrdinal is { } ordinal) PaintNumber(item, style, ordinal, baseline);
                            else glyphs.Add(new NativeMarkerPaint(itemContentLeft[item] - .8 * style.FontSize,
                                baseline - .35 * style.FontSize, .2 * style.FontSize, style.Color ?? options.TextColor));
                        }
                        break;
                    }
            }

            // An ordered-list marker is "N. " in the item's font, right-aligned to end where the item content starts.
            void PaintNumber(EpubFormattingTree.ListItem item, EpubNativeContent.Style style, int ordinal, double baseline)
            {
                string marker = ListMarkerText(ordinal, item.Source.ListType) + ".";
                var markerFont = FontFor(style);
                if (!readers.TryGetValue(markerFont, out var markerReader))
                    readers.Add(markerFont, markerReader = new TrueTypeOutlineReader(markerFont));
                double unit = style.FontSize / markerFont.UnitsPerEm;
                double width = markerFont.GetAdvanceWidth(markerFont.GetGlyphId(' ')) * unit;
                foreach (char character in marker) width += markerFont.GetAdvanceWidth(markerFont.GetGlyphId(character)) * unit;
                double x = itemContentLeft[item] - width;
                listMarkers.Add(new(marker, markerFont, x, baseline, style.FontSize, style.Color));
                foreach (char character in marker)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ushort glyph = markerFont.GetGlyphId(character);
                    if (markerReader.Outline(glyph) is { } outline)
                        glyphs.Add(new NativeGlyphPaint(outline, x, baseline, style.FontSize / 1000, style.Color ?? options.TextColor));
                    x += markerFont.GetAdvanceWidth(glyph) * unit;
                }
            }

            (double Width, double Height) ImageSize(PngDecodedImage image, EpubNativeContent.Element owner, double reserved = 0)
            {
                double width = image.Width;
                if (owner.Width is { Length: { } specified })
                {
                    double value = specified.Percentage ? contentWidth * specified.Value / 100
                        : specified.Em ? owner.Style.FontSize * specified.Value : specified.Value;
                    if (double.IsFinite(value) && value >= 0) width = value;
                }
                if (owner.MaximumWidth is { Length: { } maximum })
                {
                    double limit = maximum.Percentage ? contentWidth * maximum.Value / 100
                        : maximum.Em ? owner.Style.FontSize * maximum.Value : maximum.Value;
                    if (!double.IsFinite(limit) || limit < 0 || limit > 32768)
                        throw new NotSupportedException("Native EPUB maximum image width exceeds the supported dimensions.");
                    width = Math.Min(width, limit);
                }
                // Images shrink to fit the content box, keeping their aspect ratio (CSS max-width:100% behavior readers apply).
                double height = image.Height * width / image.Width;
                double availableHeight = options.PageHeight - 2 * options.Margin - bodyMargins.Top - bodyMargins.Bottom
                    - bodyPadding.Top - bodyPadding.Bottom - reserved;
                double scale = Math.Min(1, Math.Min(contentWidth / Math.Max(width, double.Epsilon),
                    Math.Max(1, availableHeight) / Math.Max(height, double.Epsilon)));
                return (width * scale, height * scale);
            }

            PngDecodedImage? TryReadImage(string source)
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string target = ResolveNativeImage(resource.Href, source);
                    (double Width, double Height) box = imageBoxes.TryGetValue(target, out var first) ? first
                        : (contentWidth, options.PageHeight - 2 * options.Margin);
                    var image = LoadImage(new EpubImageKey(target, box.Width, box.Height));
                    imageBoxes.TryAdd(target, box);
                    return image;
                }
                catch (EpubImageUnavailableException error)
                {
                    // One image the reader cannot show is omitted; the chapter keeps its text.
                    contentDiagnostics.Add(new(spineIndex, resource.Href, error.Message)
                    { Reason = error.Reason, ItemHref = PackagePathOf(resource.Href, source) });
                    return null;
                }
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

            // Remembers the layout state as this block begins, so a page that begins after it can resume here. Blocks in
            // tables are passed over (their cells lay out in flows of their own), so a page inside a table resumes from
            // the last block before the table.
            void RecordCandidate(EpubFormattingTree.Block block)
            {
                if (!chapterRoot || resumePoints is null || IsTablePart(block)) return;
                int depth = 0;
                for (var up = block.Parent; up is not null; up = up.Parent)
                {
                    if (IsTablePart(up)) return;
                    depth++;
                }
                if (depth >= 64) return;
                ulong started = 0, marked = 0;
                List<EpubResumePadding>? waiting = null;
                int ancestorLevel = depth - 1;
                for (var up = block.Parent; up is not null; up = up.Parent, ancestorLevel--)
                {
                    if (startedBlocks.Contains(up)) started |= 1UL << ancestorLevel;
                    if (up is EpubFormattingTree.ListItem item && markedItems.Contains(item)) marked |= 1UL << ancestorLevel;
                    if (initialPadding.TryGetValue(up, out var pad))
                        (waiting ??= []).Add(new(ancestorLevel, pad.Height, pad.PaintStart, pad.BorderHeight, pad.BorderColor));
                }
                candidateBlock = block;
                // The path is found only when a page actually ends after this block.
                candidate = new EpubResumePoint(depth == 0 ? [] : null!, pages.Count, top, pendingParagraphGap, pageStart,
                    (byte)((pendingBreakAfter ? 1 : 0) | (pageHasContent ? 2 : 0)), started, marked, waiting?.ToArray());
            }

            static bool IsTablePart(EpubFormattingTree.Block block) => block is EpubFormattingTree.Table
                or EpubFormattingTree.RowGroup or EpubFormattingTree.Row or EpubFormattingTree.Cell;

            // The resume point for the page that begins next: the last block that began before it.
            EpubResumePoint? NextResumePoint()
            {
                if (candidate is null || candidate.Path is not null) return candidate;
                var path = new List<int>();
                for (var block = candidateBlock!; block.Parent is { } parent; block = parent)
                    path.Add(parent.Children.IndexOf(block));
                path.Reverse();
                return candidate = candidate with { Path = path.ToArray() };
            }

            void FinishPage(int end)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // A cell flow that needs a second page marks its row as taller than a page; the table lays it out as blocks.
                if (!chapterRoot && end < flowEnd) throw new EpubCellFlowOverflow();
                if (existingPageCount + pages.Count >= 10_000)
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
                    content.CanvasBackground, placements.Select(placement => clusters[placement.Start]), hidden, navigationPlacements,
                    listMarkers, images));
                pageStart = end;
                pageHasContent = false;
                listMarkers.Clear();
                placements.Clear();
                navigationPlacements.Clear();
                glyphs.Clear();
                backgrounds.Clear();
                if (chapterRoot) resumePoints?.Add(NextResumePoint());
                if (chapterRoot && pages.Count >= stopAfterPages) throw new EpubLayoutStopped(pages);
            }
        }
    }

    private sealed class EpubCellFlowOverflow : Exception { }

    /// <summary>Ends a chapter's layout once it has the pages a reader asked for; finished pages never change.</summary>
    private sealed class EpubLayoutStopped(List<EpubLayoutPage> pages) : Exception
    {
        internal List<EpubLayoutPage> Pages { get; } = pages;
    }

}
