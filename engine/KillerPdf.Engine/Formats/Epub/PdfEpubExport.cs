using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;

namespace KillerPdf.Engine.Formats.Epub;

/// <summary>
/// Rebuilds a PDF's text flow as a reflowable EPUB 3 book. A tagged PDF's structure tree supplies
/// the reading order, headings and paragraphs; an untagged one is read geometrically, with
/// columns, paragraphs, headings, hyphenation and running page furniture recovered from layout.
/// </summary>
public static partial class PdfEpubExport
{
    private static readonly XNamespace Xhtml = "http://www.w3.org/1999/xhtml";
    private static readonly XNamespace Ops = "http://www.idpf.org/2007/ops";
    private static readonly XNamespace OpfNs = "http://www.idpf.org/2007/opf";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    private static readonly DateTimeOffset ZipTimestamp = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Writes the selected pages, or every page, as an EPUB 3 package.</summary>
    public static byte[] Write(PdfDocument document, IEnumerable<int>? pageIndices = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        int pageCount = PdfPageTree.Read(document).Pages.Count;
        int[] indices = pageIndices?.ToArray() ?? [.. Enumerable.Range(0, pageCount)];
        if (indices.Distinct().Count() != indices.Length || indices.Any(index => index < 0 || index >= pageCount))
            throw new ArgumentOutOfRangeException(nameof(pageIndices),
                "Page indices must be unique and within the document.");

        var reader = new PdfPageContentReader(document);
        var pages = new List<SourcePage>(indices.Length);
        foreach (int index in indices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            pages.Add(new SourcePage(index, reader.Read(index, cancellationToken)));
        }

        List<Block> blocks = ReadTagged(document, pages, cancellationToken)
            ?? ReadVisual(pages, cancellationToken);
        var images = new List<byte[]>();
        PlaceImages(document, pages, blocks, images, cancellationToken);

        PdfDocumentInformation? information = null;
        try { information = PdfDocumentInformation.Read(document); }
        catch (Exception exception) when (exception is FormatException or InvalidOperationException) { }
        return Package(blocks, images, information);
    }

    private sealed record SourcePage(int Index, PdfPageContent Content);

    private enum BlockKind { Paragraph, Heading, Image }

    private sealed class Block
    {
        public required BlockKind Kind { get; init; }
        public required int PageIndex { get; init; }
        public string Text { get; set; } = "";
        public int Level { get; set; }
        public double Top { get; init; }
        public double Size { get; init; }
        public int ImageNumber { get; init; }
        public string? Alt { get; init; }
    }

    // A run of letters on one baseline, broken at wide gaps so that columns stay apart.
    private sealed class Line
    {
        public required string Text { get; init; }
        public required PdfContentBounds Bounds { get; init; }
        public required double Baseline { get; init; }
        public required double Size { get; init; }
        public required bool Bold { get; init; }
    }

    private static List<Line> BuildLines(IEnumerable<PdfExtractedLetter> letters)
    {
        var lines = new List<Line>();
        var current = new List<PdfExtractedLetter>();
        void Flush()
        {
            Line? line = MakeLine(current);
            if (line is not null) lines.Add(line);
            current.Clear();
        }
        foreach (PdfExtractedLetter letter in letters)
        {
            if (current.Count > 0)
            {
                PdfExtractedLetter previous = current[^1];
                double em = Math.Max(1, Math.Min(previous.PointSize, letter.PointSize));
                double gap = letter.StartBaseLine.X - previous.EndBaseLine.X;
                double across = Math.Abs(letter.StartBaseLine.Y - previous.EndBaseLine.Y);
                if (across > em * 0.35 || gap > em * 1.5 || gap < -em * 0.5) Flush();
            }
            current.Add(letter);
        }
        Flush();
        return lines;
    }

    private static Line? MakeLine(List<PdfExtractedLetter> letters)
    {
        var text = new StringBuilder();
        PdfExtractedLetter? previous = null;
        foreach (PdfExtractedLetter letter in letters)
        {
            if (previous is not null && text.Length > 0 && text[^1] != ' '
                && !string.IsNullOrWhiteSpace(letter.Value))
            {
                double em = Math.Max(1, Math.Min(previous.PointSize, letter.PointSize));
                if (letter.StartBaseLine.X - previous.EndBaseLine.X > em * 0.22) text.Append(' ');
            }
            if (string.IsNullOrWhiteSpace(letter.Value))
            {
                if (text.Length > 0 && text[^1] != ' ') text.Append(' ');
            }
            else text.Append(letter.Value);
            previous = letter;
        }
        string value = text.ToString().Trim();
        if (value.Length == 0) return null;
        var visible = letters.Where(letter => !string.IsNullOrWhiteSpace(letter.Value)).ToArray();
        double[] sizes = [.. visible.Select(letter => letter.PointSize).Order()];
        int bold = visible.Count(letter => IsBold(letter.FontName));
        return new Line
        {
            Text = value,
            Bounds = PdfContentBounds.Union(visible.Select(letter => letter.BoundingBox)),
            Baseline = visible.Average(letter => letter.StartBaseLine.Y),
            Size = sizes[sizes.Length / 2],
            Bold = bold * 2 > visible.Length
        };
    }

    private static bool IsBold(string fontName) =>
        fontName.Contains("Bold", StringComparison.OrdinalIgnoreCase)
        || fontName.Contains("Black", StringComparison.OrdinalIgnoreCase)
        || fontName.Contains("Heavy", StringComparison.OrdinalIgnoreCase)
        || fontName.Contains("Semibold", StringComparison.OrdinalIgnoreCase);

    // Joins one line onto the paragraph text, mending words broken across lines by a hyphen.
    private static string JoinLine(string paragraph, string line)
    {
        if (paragraph.Length == 0) return line;
        if (paragraph[^1] == '­') return paragraph[..^1] + line;
        if (paragraph.Length >= 2 && paragraph[^1] is '-' or '‐' && char.IsLetter(paragraph[^2])
            && line.Length > 0 && char.IsLower(line[0]))
            return paragraph[..^1] + line;
        return paragraph + " " + line;
    }

    private static string Clean(string text) => text.Replace("­", "", StringComparison.Ordinal);

    // ---------------------------------------------------------------- untagged layout reading

    private static List<Block> ReadVisual(List<SourcePage> pages, CancellationToken cancellationToken)
    {
        var pageLines = pages.Select(page => BuildLines(page.Content.Letters)).ToList();
        RemoveFurniture(pages, pageLines);

        var paragraphs = new List<(Block Block, int Lines, bool Bold)>();
        Line? previous = null;
        (Block Block, int Lines, bool Bold)? open = null;
        for (int pageNumber = 0; pageNumber < pages.Count; pageNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<Line> ordered = OrderColumns(pageLines[pageNumber], pages[pageNumber].Content.Width);
            double leading = TypicalLeading(ordered);
            foreach (Line line in ordered)
            {
                bool continues = open is not null && previous is not null
                    && Continues(previous, line, leading, pageChanged: open.Value.Block.PageIndex != pages[pageNumber].Index);
                if (continues)
                {
                    var value = open!.Value;
                    value.Block.Text = JoinLine(value.Block.Text, line.Text);
                    open = (value.Block, value.Lines + 1, value.Bold && line.Bold);
                    paragraphs[^1] = open.Value;
                }
                else
                {
                    var block = new Block
                    {
                        Kind = BlockKind.Paragraph,
                        PageIndex = pages[pageNumber].Index,
                        Text = line.Text,
                        Top = line.Bounds.Top,
                        Size = line.Size
                    };
                    open = (block, 1, line.Bold);
                    paragraphs.Add(open.Value);
                }
                previous = line;
            }
        }

        // The body size is the size most of the text is set in; clearly larger, short
        // paragraphs are headings, ranked by size.
        var weights = new Dictionary<double, int>();
        foreach (var paragraph in paragraphs)
        {
            double key = Math.Round(paragraph.Block.Size * 2) / 2;
            weights[key] = weights.GetValueOrDefault(key) + paragraph.Block.Text.Length;
        }
        double body = weights.Count == 0 ? 0 : weights.MaxBy(pair => pair.Value).Key;
        // Sentences set a little large (lead-ins, pull quotes) end in punctuation; headings do not.
        bool IsHeading((Block Block, int Lines, bool Bold) paragraph) =>
            body > 0 && paragraph.Block.Size >= body * 1.2
            && paragraph.Block.Text.Length <= 150 && paragraph.Lines <= 3
            && paragraph.Block.Text.Count(char.IsLetterOrDigit) >= 2
            && !Regex.IsMatch(paragraph.Block.Text, @"[.,;:]$");
        double[] headingSizes = [.. paragraphs.Where(IsHeading)
            .Select(paragraph => Math.Round(paragraph.Block.Size * 2) / 2).Distinct().OrderDescending()];
        foreach (var paragraph in paragraphs)
        {
            paragraph.Block.Text = Clean(paragraph.Block.Text);
            if (!IsHeading(paragraph)) continue;
            int rank = Array.IndexOf(headingSizes, Math.Round(paragraph.Block.Size * 2) / 2);
            paragraph.Block.Level = Math.Min(6, rank + 1);
        }
        return [.. paragraphs.Select(paragraph => paragraph.Block.Level > 0
            ? new Block
            {
                Kind = BlockKind.Heading,
                PageIndex = paragraph.Block.PageIndex,
                Text = paragraph.Block.Text,
                Level = paragraph.Block.Level,
                Top = paragraph.Block.Top,
                Size = paragraph.Block.Size
            }
            : paragraph.Block)];
    }

    private static bool Continues(Line previous, Line line, double leading, bool pageChanged)
    {
        if (Math.Abs(previous.Size - line.Size) > Math.Max(previous.Size, line.Size) * 0.15) return false;
        if (previous.Bold != line.Bold) return false;
        bool sentenceOpen = !Regex.IsMatch(previous.Text, @"[.!?:;""”’)]$")
            && line.Text.Length > 0 && char.IsLower(line.Text[0]);
        // A new column or page: carry on only when the sentence plainly runs on.
        if (pageChanged || line.Baseline > previous.Baseline + 1) return sentenceOpen;
        double drop = previous.Baseline - line.Baseline;
        if (drop > leading * 1.4) return false;
        double em = Math.Max(1, line.Size);
        if (line.Bounds.Left > previous.Bounds.Left + em) return false;
        return true;
    }

    private static double TypicalLeading(List<Line> lines)
    {
        var drops = new List<double>();
        for (int index = 1; index < lines.Count; index++)
        {
            double drop = lines[index - 1].Baseline - lines[index].Baseline;
            double size = Math.Max(lines[index].Size, 1);
            if (drop > size * 0.5 && drop <= size * 2.5
                && Math.Abs(lines[index - 1].Size - lines[index].Size) < 0.5)
                drops.Add(drop);
        }
        if (drops.Count == 0)
            return lines.Count == 0 ? 14 : lines.Select(line => line.Size).Max() * 1.25;
        drops.Sort();
        return drops[drops.Count / 2];
    }

    // Splits a page at a vertical gutter that narrow lines on both sides respect. Lines that
    // cross the gutter, such as titles, divide the page into bands read top to bottom; within a
    // band the left column is read before the right.
    private static List<Line> OrderColumns(List<Line> lines, double pageWidth)
    {
        List<Line> byPosition = [.. lines.OrderByDescending(line => Math.Round(line.Baseline))
            .ThenBy(line => line.Bounds.Left)];
        if (lines.Count < 4 || pageWidth <= 0) return byPosition;
        var narrow = lines.Where(line => line.Bounds.Width < pageWidth * 0.55)
            .Select(line => (line.Bounds.Left, line.Bounds.Right)).OrderBy(span => span.Left).ToList();
        (double Start, double End)? gutter = null;
        double reach = double.NegativeInfinity;
        foreach (var span in narrow)
        {
            if (reach > double.NegativeInfinity && span.Left - reach >= 10)
            {
                double middle = (reach + span.Left) / 2;
                if (middle > pageWidth * 0.3 && middle < pageWidth * 0.7)
                {
                    gutter = (reach, span.Left);
                    break;
                }
            }
            reach = Math.Max(reach, span.Right);
        }
        if (gutter is null) return byPosition;
        bool Left(Line line) => line.Bounds.Right <= gutter.Value.Start + 2;
        bool Right(Line line) => line.Bounds.Left >= gutter.Value.End - 2;
        if (lines.Count(Left) < 2 || lines.Count(Right) < 2) return byPosition;

        var ordered = new List<Line>();
        var leftBand = new List<Line>();
        var rightBand = new List<Line>();
        void FlushBand()
        {
            ordered.AddRange(leftBand);
            ordered.AddRange(rightBand);
            leftBand.Clear();
            rightBand.Clear();
        }
        foreach (Line line in byPosition)
        {
            if (Left(line)) leftBand.Add(line);
            else if (Right(line)) rightBand.Add(line);
            else
            {
                FlushBand();
                ordered.Add(line);
            }
        }
        FlushBand();
        return ordered;
    }

    [GeneratedRegex(@"^(page\s*)?#+(\s*(of|/)\s*#+)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PageNumberPattern();

    [GeneratedRegex(@"^[ivxlcdm]{1,7}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RomanPattern();

    [GeneratedRegex(@"\d+", RegexOptions.CultureInvariant)]
    private static partial Regex DigitsPattern();

    // Running headers, footers and page numbers sit in the top or bottom tenth of a page and
    // repeat from page to page, often with only the number changing.
    private static void RemoveFurniture(List<SourcePage> pages, List<List<Line>> pageLines)
    {
        static string Key(string text) => DigitsPattern().Replace(text.Trim().ToLowerInvariant(), "#");
        bool InMargin(Line line, PdfPageContent page) =>
            line.Bounds.Bottom >= page.Height * 0.9 || line.Bounds.Top <= page.Height * 0.1;
        var counts = new Dictionary<string, int>();
        for (int index = 0; index < pages.Count; index++)
            foreach (string key in pageLines[index].Where(line => InMargin(line, pages[index].Content))
                .Select(line => Key(line.Text)).Distinct())
                counts[key] = counts.GetValueOrDefault(key) + 1;
        // Section running heads repeat only within their section, so a few repeats is enough.
        int threshold = pages.Count <= 3 ? 2 : 3;
        for (int index = 0; index < pages.Count; index++)
        {
            PdfPageContent page = pages[index].Content;
            pageLines[index].RemoveAll(line =>
            {
                if (!InMargin(line, page)) return false;
                string key = Key(line.Text);
                return PageNumberPattern().IsMatch(key)
                    || RomanPattern().IsMatch(line.Text.Trim())
                    || counts.GetValueOrDefault(key) >= threshold;
            });
        }
    }

    // ---------------------------------------------------------------- tagged structure reading

    private static readonly HashSet<string> BlockRoles = new(StringComparer.Ordinal)
    {
        "P", "H", "H1", "H2", "H3", "H4", "H5", "H6", "Title", "Caption", "BlockQuote",
        "LBody", "LI", "TOCI", "Note", "Code", "TD", "TH"
    };

    private sealed record TaggedBlock(string Role, List<(int Page, int Mcid)> Content, string? ActualText);

    private static List<Block>? ReadTagged(PdfDocument document, List<SourcePage> pages,
        CancellationToken cancellationToken)
    {
        List<TaggedBlock>? tagged;
        try { tagged = ReadStructure(document); }
        catch (Exception exception) when (exception is FormatException or InvalidOperationException
            or InvalidCastException or OverflowException or KeyNotFoundException)
        {
            return null;
        }
        if (tagged is null || tagged.Count == 0) return null;

        var selected = pages.ToDictionary(page => page.Index);
        var letters = new Dictionary<(int, int), List<PdfExtractedLetter>>();
        int tagged_letters = 0, all_letters = 0;
        foreach (SourcePage page in pages)
            foreach (PdfExtractedLetter letter in page.Content.Letters)
            {
                if (string.IsNullOrWhiteSpace(letter.Value)) continue;
                all_letters++;
                if (letter.MarkedContentId is not int mcid) continue;
                tagged_letters++;
                if (!letters.TryGetValue((page.Index, mcid), out var list))
                    letters[(page.Index, mcid)] = list = [];
                list.Add(letter);
            }
        // A tree that reaches none of the page text is no guide to reading it.
        if (all_letters > 0 && tagged_letters == 0) return null;

        var blocks = new List<Block>();
        foreach (TaggedBlock item in tagged)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pieces = item.Content.Where(content => selected.ContainsKey(content.Page)).ToList();
            if (pieces.Count == 0) continue;
            var blockLetters = new List<PdfExtractedLetter>();
            foreach (var piece in pieces)
                if (letters.TryGetValue(piece, out var list))
                    blockLetters.AddRange(list);
            if (blockLetters.Count == 0 && item.ActualText is null) continue;
            string text;
            if (item.ActualText is not null) text = item.ActualText.Trim();
            else
            {
                text = "";
                foreach (Line line in BuildLines(blockLetters)) text = JoinLine(text, line.Text);
                text = Clean(text);
            }
            if (text.Length == 0) continue;
            int level = HeadingLevel(item.Role);
            PdfContentBounds bounds = PdfContentBounds.Union(blockLetters.Select(letter => letter.BoundingBox));
            blocks.Add(new Block
            {
                Kind = level > 0 ? BlockKind.Heading : BlockKind.Paragraph,
                PageIndex = pieces[0].Page,
                Text = text,
                Level = level,
                Top = blockLetters.Count > 0 ? bounds.Top : 0
            });
        }
        return blocks.Count == 0 ? null : blocks;
    }

    private static int HeadingLevel(string role) => role switch
    {
        "Title" or "H1" => 1,
        "H" or "H2" => 2,
        "H3" => 3,
        "H4" => 4,
        "H5" => 5,
        "H6" => 6,
        _ => 0
    };

    private static List<TaggedBlock>? ReadStructure(PdfDocument document)
    {
        PdfPageTree tree = PdfPageTree.Read(document);
        if (!tree.Catalog.TryGetValue(Name("StructTreeRoot"), out PdfObject? rootValue)) return null;
        if (Resolve(document, rootValue) is not PdfDictionary root) return null;
        var roleMap = new Dictionary<string, string>(StringComparer.Ordinal);
        if (root.TryGetValue(Name("RoleMap"), out PdfObject? mapValue)
            && Resolve(document, mapValue) is PdfDictionary map)
            foreach (var pair in map)
                if (Resolve(document, pair.Value) is PdfName target)
                    roleMap[pair.Key.ValueAsLatin1()] = target.ValueAsLatin1();
        var pageIndexes = tree.Pages.ToDictionary(
            page => (page.Reference.ObjectNumber, page.Reference.Generation), page => page.Index);
        var visited = new HashSet<(int, int)>();
        var blocks = new List<TaggedBlock>();
        if (root.TryGetValue(Name("K"), out PdfObject? kids)) Visit(kids, null, null, 0);
        return blocks;

        string Standard(string role)
        {
            for (int step = 0; step < 16 && roleMap.TryGetValue(role, out string? mapped); step++)
                role = mapped;
            return role;
        }

        void Visit(PdfObject value, TaggedBlock? block, int? page, int depth)
        {
            if (depth >= 256) throw new InvalidOperationException("The structure tree is too deep.");
            if (value is PdfIndirectReference reference
                && !visited.Add((reference.ObjectNumber, reference.Generation))) return;
            PdfObject resolved = Resolve(document, value);
            switch (resolved)
            {
                case PdfArray array:
                    foreach (PdfObject child in array) Visit(child, block, page, depth + 1);
                    return;
                case PdfInteger mcid:
                    Add(block, page, mcid.Value);
                    return;
                case not PdfDictionary:
                    return;
            }
            var dictionary = (PdfDictionary)resolved;
            int? ownPage = PageOf(dictionary) ?? page;
            if (IsName(dictionary, "Type", "MCR"))
            {
                if (dictionary.TryGetValue(Name("MCID"), out PdfObject? id) && id is PdfInteger number)
                    Add(block, ownPage, number.Value);
                return;
            }
            if (IsName(dictionary, "Type", "OBJR")) return;
            if (!dictionary.TryGetValue(Name("S"), out PdfObject? roleValue)
                || Resolve(document, roleValue) is not PdfName roleName) return;
            string role = Standard(roleName.ValueAsLatin1());
            if (role is "Artifact" or "Figure" or "Formula") return;
            string? actualText = dictionary.TryGetValue(Name("ActualText"), out PdfObject? actual)
                && Resolve(document, actual) is PdfString actualString
                ? PdfUnicodeEncoding.DecodeTextString(actualString.Bytes.Span, "A structure element /ActualText value")
                : null;
            TaggedBlock? target = block;
            if (block is null && (BlockRoles.Contains(role) || actualText is not null))
            {
                target = new TaggedBlock(BlockRoles.Contains(role) ? role : "P", [], actualText);
                blocks.Add(target);
                if (actualText is not null) return;
            }
            if (dictionary.TryGetValue(Name("K"), out PdfObject? children))
                Visit(children, target, ownPage, depth + 1);
        }

        void Add(TaggedBlock? block, int? page, long mcid)
        {
            if (page is null || mcid < 0 || mcid > int.MaxValue) return;
            if (block is null)
            {
                // Content tagged directly under a grouping element still reads as a paragraph.
                block = new TaggedBlock("P", [], null);
                blocks.Add(block);
            }
            block.Content.Add((page.Value, (int)mcid));
        }

        int? PageOf(PdfDictionary dictionary) =>
            dictionary.TryGetValue(Name("Pg"), out PdfObject? pageValue)
            && pageValue is PdfIndirectReference pageReference
            && pageIndexes.TryGetValue((pageReference.ObjectNumber, pageReference.Generation), out int index)
                ? index : null;
    }

    private static bool IsName(PdfDictionary dictionary, string key, string expected) =>
        dictionary.TryGetValue(Name(key), out PdfObject? value)
        && value is PdfName name && name.ValueAsLatin1() == expected;

    private static PdfObject Resolve(PdfDocument document, PdfObject value)
    {
        for (int step = 0; step < 32 && value is PdfIndirectReference reference; step++)
            value = document.Resolve(reference);
        return value is PdfIndirectReference ? PdfNull.Instance : value;
    }

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));

    // ---------------------------------------------------------------- images

    private static void PlaceImages(PdfDocument document, List<SourcePage> pages, List<Block> blocks,
        List<byte[]> images, CancellationToken cancellationToken)
    {
        var renderer = new Rendering.PdfPageRenderer(document);
        foreach (SourcePage page in pages)
        {
            foreach (PdfExtractedImage image in page.Content.Images)
            {
                cancellationToken.ThrowIfCancellationRequested();
                byte[]? png = RenderImage(renderer, page.Content, image, cancellationToken);
                if (png is null) continue;
                images.Add(png);
                var block = new Block
                {
                    Kind = BlockKind.Image,
                    PageIndex = page.Index,
                    Top = image.BoundingBox.Top,
                    ImageNumber = images.Count
                };
                double middle = (image.BoundingBox.Top + image.BoundingBox.Bottom) / 2;
                int lastOnPage = blocks.FindLastIndex(item => item.PageIndex == page.Index);
                int below = blocks.FindIndex(item => item.PageIndex == page.Index
                    && item.Kind != BlockKind.Image && item.Top < middle);
                int insertAt = below >= 0 ? below
                    : lastOnPage >= 0 ? lastOnPage + 1
                    : InsertionForEmptyPage(blocks, page.Index);
                blocks.Insert(insertAt, block);
            }
        }
    }

    private static int InsertionForEmptyPage(List<Block> blocks, int pageIndex)
    {
        int next = blocks.FindIndex(item => item.PageIndex > pageIndex);
        return next >= 0 ? next : blocks.Count;
    }

    internal static byte[]? RenderImage(Rendering.PdfPageRenderer renderer, PdfPageContent page,
        PdfExtractedImage image, CancellationToken cancellationToken)
    {
        if (page.Instructions.Any(instruction => instruction.Operator is "gs" or "W" or "W*")) return null;
        PdfContentBounds bounds = image.BoundingBox;
        if (bounds.Left < 0 || bounds.Bottom < 0 || bounds.Right > page.Width || bounds.Top > page.Height) return null;
        Rendering.PdfRenderedPage? bitmap = renderer.RenderExportImage(image, cancellationToken);
        return bitmap is null ? null : PdfOfficeImage.Encode(bitmap, cancellationToken);
    }

    // ---------------------------------------------------------------- package writing

    private static byte[] Package(List<Block> blocks, List<byte[]> images, PdfDocumentInformation? information)
    {
        string? firstHeading = blocks.FirstOrDefault(block => block.Kind == BlockKind.Heading)?.Text;
        string title = Sanitize(!string.IsNullOrWhiteSpace(information?.Title) ? information!.Title!.Trim()
            : firstHeading ?? "Untitled");
        if (title.Length == 0) title = "Untitled";
        string language = !string.IsNullOrWhiteSpace(information?.Language) ? information!.Language!.Trim() : "und";
        string? author = string.IsNullOrWhiteSpace(information?.Author) ? null : Sanitize(information!.Author!.Trim());
        DateTimeOffset modified = information?.ModificationDate ?? information?.CreationDate ?? ZipTimestamp;
        string modifiedText = modified.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

        // Chapters start at each top-level heading.
        int topLevel = blocks.Where(block => block.Kind == BlockKind.Heading).Select(block => block.Level)
            .DefaultIfEmpty(1).Min();
        var chapters = new List<List<Block>> { new() };
        foreach (Block block in blocks)
        {
            if (block.Kind == BlockKind.Heading && block.Level == topLevel && chapters[^1].Count > 0)
                chapters.Add([]);
            chapters[^1].Add(block);
        }

        var headings = new List<(int Level, string Text, string Href)>();
        var chapterDocuments = new List<byte[]>();
        int headingNumber = 0;
        for (int chapter = 0; chapter < chapters.Count; chapter++)
        {
            string file = ChapterName(chapter);
            var body = new XElement(Xhtml + "body");
            foreach (Block block in chapters[chapter])
            {
                switch (block.Kind)
                {
                    case BlockKind.Heading:
                        string id = $"h{++headingNumber}";
                        int level = Math.Clamp(block.Level - topLevel + 1, 1, 6);
                        body.Add(new XElement(Xhtml + $"h{level}", new XAttribute("id", id), Sanitize(block.Text)));
                        headings.Add((level, Sanitize(block.Text), $"{file}#{id}"));
                        break;
                    case BlockKind.Image:
                        body.Add(new XElement(Xhtml + "figure",
                            new XElement(Xhtml + "img",
                                new XAttribute("src", $"../images/image{block.ImageNumber}.png"),
                                new XAttribute("alt", Sanitize(block.Alt ?? "")))));
                        break;
                    default:
                        body.Add(new XElement(Xhtml + "p", Sanitize(block.Text)));
                        break;
                }
            }
            chapterDocuments.Add(Xml(Page(title, language, body)));
        }

        var nav = new XElement(Xhtml + "nav", new XAttribute(XNamespace.Xmlns + "epub", Ops),
            new XAttribute(Ops + "type", "toc"), new XAttribute("id", "toc"),
            new XElement(Xhtml + "h1", "Contents"));
        nav.Add(headings.Count == 0
            ? new XElement(Xhtml + "ol", new XElement(Xhtml + "li",
                new XElement(Xhtml + "a", new XAttribute("href", ChapterName(0)), title)))
            : NestedList(headings));
        byte[] navDocument = Xml(Page(title, language, new XElement(Xhtml + "body", nav)));

        string identifier = "urn:uuid:" + StableGuid(title, blocks, images).ToString("D");
        var metadata = new XElement(OpfNs + "metadata",
            new XAttribute(XNamespace.Xmlns + "dc", Dc),
            new XElement(Dc + "identifier", new XAttribute("id", "book-id"), identifier),
            new XElement(Dc + "title", title),
            new XElement(Dc + "language", language),
            new XElement(OpfNs + "meta", new XAttribute("property", "dcterms:modified"), modifiedText));
        if (author is not null) metadata.Add(new XElement(Dc + "creator", author));
        var manifest = new XElement(OpfNs + "manifest",
            new XElement(OpfNs + "item", new XAttribute("id", "nav"), new XAttribute("href", "nav.xhtml"),
                new XAttribute("media-type", "application/xhtml+xml"), new XAttribute("properties", "nav")),
            new XElement(OpfNs + "item", new XAttribute("id", "css"), new XAttribute("href", "styles/book.css"),
                new XAttribute("media-type", "text/css")));
        var spine = new XElement(OpfNs + "spine");
        for (int chapter = 0; chapter < chapterDocuments.Count; chapter++)
        {
            string id = $"chapter{chapter + 1}";
            manifest.Add(new XElement(OpfNs + "item", new XAttribute("id", id),
                new XAttribute("href", ChapterName(chapter)), new XAttribute("media-type", "application/xhtml+xml")));
            spine.Add(new XElement(OpfNs + "itemref", new XAttribute("idref", id)));
        }
        for (int image = 1; image <= images.Count; image++)
            manifest.Add(new XElement(OpfNs + "item", new XAttribute("id", $"image{image}"),
                new XAttribute("href", $"images/image{image}.png"), new XAttribute("media-type", "image/png")));
        var package = new XDocument(new XElement(OpfNs + "package",
            new XAttribute("version", "3.0"), new XAttribute("unique-identifier", "book-id"),
            new XAttribute(XNamespace.Xml + "lang", language),
            metadata, manifest, spine));

        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            Entry(archive, "mimetype", Encoding.ASCII.GetBytes("application/epub+zip"), CompressionLevel.NoCompression);
            Entry(archive, "META-INF/container.xml", Encoding.UTF8.GetBytes(
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
                "<container version=\"1.0\" xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\">" +
                "<rootfiles><rootfile full-path=\"OEBPS/content.opf\" media-type=\"application/oebps-package+xml\"/>" +
                "</rootfiles></container>"));
            Entry(archive, "OEBPS/content.opf", Xml(package));
            Entry(archive, "OEBPS/nav.xhtml", navDocument);
            Entry(archive, "OEBPS/styles/book.css", Encoding.UTF8.GetBytes(
                "body{margin:0 5%;line-height:1.4}p{margin:0 0 .8em}" +
                "figure{margin:1em 0;text-align:center}img{max-width:100%;height:auto}"));
            for (int chapter = 0; chapter < chapterDocuments.Count; chapter++)
                Entry(archive, "OEBPS/" + ChapterName(chapter), chapterDocuments[chapter]);
            for (int image = 0; image < images.Count; image++)
                Entry(archive, $"OEBPS/images/image{image + 1}.png", images[image], CompressionLevel.NoCompression);
        }
        return output.ToArray();
    }

    private static string ChapterName(int chapter) => $"text/chapter{chapter + 1:D3}.xhtml";

    private static XDocument Page(string title, string language, XElement body)
    {
        bool nav = body.Element(Xhtml + "nav") is not null;
        return new XDocument(new XElement(Xhtml + "html",
            new XAttribute(XNamespace.Xmlns + "epub", Ops),
            new XAttribute(XNamespace.Xml + "lang", language),
            new XAttribute("lang", language),
            new XElement(Xhtml + "head",
                new XElement(Xhtml + "meta", new XAttribute("charset", "utf-8")),
                new XElement(Xhtml + "title", title),
                new XElement(Xhtml + "link", new XAttribute("rel", "stylesheet"), new XAttribute("type", "text/css"),
                    new XAttribute("href", nav ? "styles/book.css" : "../styles/book.css"))),
            body));
    }

    private static XElement NestedList(List<(int Level, string Text, string Href)> headings)
    {
        // lists[k] is the open list at depth k + 1; a heading may go at most one level deeper
        // than the one before it, so every nested list sits inside an item.
        var root = new XElement(Xhtml + "ol");
        var lists = new List<XElement> { root };
        var items = new List<XElement>();
        foreach (var heading in headings)
        {
            int depth = Math.Clamp(heading.Level, 1, lists.Count + (items.Count == lists.Count ? 1 : 0));
            while (lists.Count > depth) lists.RemoveAt(lists.Count - 1);
            while (items.Count > depth) items.RemoveAt(items.Count - 1);
            if (lists.Count < depth)
            {
                var child = new XElement(Xhtml + "ol");
                items[depth - 2].Add(child);
                lists.Add(child);
            }
            var item = new XElement(Xhtml + "li",
                new XElement(Xhtml + "a", new XAttribute("href", heading.Href), heading.Text));
            lists[depth - 1].Add(item);
            if (items.Count >= depth) items[depth - 1] = item; else items.Add(item);
        }
        return root;
    }

    private static Guid StableGuid(string title, List<Block> blocks, List<byte[]> images)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(title));
        foreach (Block block in blocks)
        {
            hash.AppendData(Encoding.UTF8.GetBytes($"\n{block.Kind}:{block.Level}:"));
            hash.AppendData(Encoding.UTF8.GetBytes(block.Text));
        }
        foreach (byte[] image in images) hash.AppendData(image);
        byte[] bytes = hash.GetHashAndReset()[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);   // version 5 layout: name-based
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes, bigEndian: true);
    }

    private static string Sanitize(string text)
    {
        var output = new StringBuilder(text.Length);
        for (int index = 0; index < text.Length; index++)
        {
            char character = text[index];
            if (char.IsHighSurrogate(character) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
            {
                output.Append(character).Append(text[++index]);
                continue;
            }
            if (XmlConvert.IsXmlChar(character)) output.Append(character);
        }
        return output.ToString();
    }

    private static byte[] Xml(XDocument document)
    {
        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false),
            Indent = false
        }))
            document.Save(writer);
        return stream.ToArray();
    }

    private static void Entry(ZipArchive archive, string name, byte[] data,
        CompressionLevel level = CompressionLevel.Optimal)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name, level);
        entry.LastWriteTime = ZipTimestamp;
        using Stream stream = entry.Open();
        stream.Write(data);
    }
}
