using System.IO.Compression;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Parsing;
using KillerPdf.Engine.Writing;
using Matrix = KillerPdf.Engine.Parsing.PdfTextContentReader.Matrix;

namespace KillerPdf.Engine.Documents;

/// <summary>Why a line of page text cannot be rewritten in the page content.</summary>
public enum PdfTextLineBlocker
{
    /// <summary>The line can be rewritten.</summary>
    None,
    /// <summary>No readable text lies in the area.</summary>
    NoText,
    /// <summary>The text is drawn invisibly, as on a scan with a recognized text layer.</summary>
    InvisibleText,
    /// <summary>The text has a zero font size.</summary>
    ZeroSizeText,
    /// <summary>The text uses a font that cannot be decoded.</summary>
    UnreadableText
}

/// <summary>What a line edit would replace.</summary>
/// <param name="Glyphs">The glyphs of the line the area holds.</param>
/// <param name="Blocker">Why the line cannot be rewritten, or <see cref="PdfTextLineBlocker.None"/>.</param>
/// <param name="FontName">The PostScript name of the font the line starts in.</param>
/// <param name="FontSize">The size the line starts at on the page, in points.</param>
public sealed record PdfTextLineInspection(int Glyphs, PdfTextLineBlocker Blocker, string FontName, double FontSize)
{
    /// <summary>True when the line can be rewritten in the page content.</summary>
    public bool CanEdit => Blocker == PdfTextLineBlocker.None;
}

/// <summary>A replacement for the text drawn in one area of a page.</summary>
/// <param name="PageIndex">The zero-based page.</param>
/// <param name="Region">The area in unrotated points relative to the page's crop box corner. A
/// glyph is replaced when its center lies inside and it belongs to the line the area holds, so a
/// numeral or watermark that also reaches the area stays.</param>
/// <param name="Text">The new text; line breaks start new lines below the first.</param>
public sealed record PdfTextLineEdit(int PageIndex, PdfContentBounds Region, string Text)
{
    /// <summary>The embeddable font used when the original font cannot show the new text, or when
    /// <see cref="UseSubstitute"/> asks for it.</summary>
    public TrueTypeFont? Substitute { get; init; }
    /// <summary>Writes the new text in <see cref="Substitute"/> even when the original font could show it.</summary>
    public bool UseSubstitute { get; init; }
    /// <summary>The new size in points, or null to keep the original size.</summary>
    public double? FontSize { get; init; }
    /// <summary>The new fill color, or null to keep the original color.</summary>
    public PdfRgbColor? FillColor { get; init; }
    /// <summary>The new space added after each character in points, or null to keep the original spacing.</summary>
    public double? CharacterSpacing { get; init; }
    /// <summary>Draws a line under the new text.</summary>
    public bool Underline { get; init; }
    /// <summary>Draws a line through the new text.</summary>
    public bool Strike { get; init; }
    internal IReadOnlyList<PdfParagraphLine>? ParagraphLines { get; init; }
    // The ink of a paragraph replacement, which later page content must not cover.
    internal PdfContentBounds? ParagraphBox { get; init; }
    // The source lines of a paragraph, whose underlines leave with them.
    internal IReadOnlyList<PdfParagraphRowSpan>? ParagraphRows { get; init; }
}

/// <summary>Which font the new text was written in.</summary>
public enum PdfTextLineFont
{
    /// <summary>The font the replaced text used.</summary>
    Original,
    /// <summary>The substitute font, embedded in the document.</summary>
    Substitute
}

/// <summary>The edited document and how the new text was written.</summary>
/// <param name="Document">The document with the edit as an incremental update.</param>
/// <param name="Font">Which font the new text was written in.</param>
/// <param name="RemovedGlyphs">How many of the old glyphs left the page content.</param>
public sealed record PdfTextLineEditResult(byte[] Document, PdfTextLineFont Font, int RemovedGlyphs);

/// <summary>
/// Rewrites a line of text inside the page content itself. The old glyphs leave the content stream
/// and the new text is written after the last of them, so it keeps the original font, size, color
/// and stacking order unless the edit changes them. No cover or other drawing is added over the
/// page, and an edit that later content would hide or a clip would cut off is refused.
/// </summary>
public static class PdfTextLineEditor
{
    /// <summary>Reports whether the text in an area can be rewritten, without changing the document.</summary>
    public static PdfTextLineInspection Inspect(PdfDocument document, int pageIndex, PdfContentBounds region,
        CancellationToken cancellationToken = default) =>
        PdfContentRedaction.InspectLine(document, pageIndex, region, cancellationToken);

    /// <summary>Replaces the text in an area and returns the document with an incremental update.</summary>
    /// <exception cref="NotSupportedException">The line cannot be rewritten, the new text needs a
    /// substitute font that was not given or cannot be used, or the page would hide or cut off the
    /// new text.</exception>
    public static PdfTextLineEditResult Replace(PdfDocument document, PdfTextLineEdit edit,
        CancellationToken cancellationToken = default) =>
        PdfContentRedaction.ReplaceLine(document, edit, cancellationToken);
}

public static partial class PdfContentRedaction
{
    private const double ReplacementSpaceUnits = 250;
    private const string LineCutOff = "The new text would be cut off by the area this line is clipped to.";
    // The part of the em a line's letters fill, around the baseline.
    private const double LineInkAscent = 0.7, LineInkDescent = 0.15;
    // A shape thinner than this share of the em crosses a line without hiding its letters, and
    // one narrower than a letter hides no more than a letter's middle.
    private const double ThinShare = 0.15, LetterShare = 0.6;

    internal static PdfTextLineInspection InspectLine(PdfDocument document, int pageIndex, PdfContentBounds region,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        LineProbe probe = Probe(document, pageIndex, region, null, cancellationToken);
        return probe.Inspection;
    }

    internal static PdfTextLineEditResult ReplaceLine(PdfDocument document, PdfTextLineEdit edit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(edit);
        ArgumentNullException.ThrowIfNull(edit.Text);
        if (edit.FontSize is double requested && (!double.IsFinite(requested) || requested <= 0 || requested > 1000))
            throw new ArgumentOutOfRangeException(nameof(edit), "The font size must be above 0 and at most 1000 points.");
        if (edit.CharacterSpacing is double spacing && (!double.IsFinite(spacing) || Math.Abs(spacing) > 1000))
            throw new ArgumentOutOfRangeException(nameof(edit), "The character spacing must be finite.");
        if (edit.FillColor is PdfRgbColor color && new[] { color.Red, color.Green, color.Blue }
                .Any(component => !double.IsFinite(component) || component is < 0 or > 1))
            throw new ArgumentOutOfRangeException(nameof(edit), "Color components must be from 0 through 1.");
        if (edit.UseSubstitute && edit.Substitute is null)
            throw new ArgumentException("A substitute font is required.", nameof(edit));

        var used = new Dictionary<object, Dictionary<uint, PdfDecodedCharacter>>();
        LineProbe probe = Probe(document, edit.PageIndex, edit.Region, used, cancellationToken,
            paragraph: edit.ParagraphLines is not null);
        if (!probe.Inspection.CanEdit)
            throw new NotSupportedException(probe.Inspection.Blocker switch
            {
                PdfTextLineBlocker.NoText => "No readable text lies in the area.",
                PdfTextLineBlocker.InvisibleText => "The text in the area is drawn invisibly.",
                PdfTextLineBlocker.ZeroSizeText => "The text in the area has a zero font size.",
                _ => "The text in the area uses a font that cannot be decoded."
            });
        TextState first = probe.Insertion.State!;
        PdfExtractionFont font = first.Font!;
        string[] lines = ReplacementLines(edit.Text);

        List<EncodedLine>? encoded = null;
        PdfDictionary? fontDictionary = first.FontObject is null ? null : Resolve(document, first.FontObject) as PdfDictionary;
        if (!edit.UseSubstitute && fontDictionary is not null)
        {
            object? key = FontKey(document, first.FontObject);
            bool composite = IsNamed(fontDictionary, SubtypeName, "Type0");
            bool embedded = IsEmbedded(document, fontDictionary);
            encoded = EncodeOriginal(font, composite, embedded, Used(key), lines);
            if (encoded is null && embedded && key is not null && probe.Tree.Pages.Count > 1)
            {
                // A subset holds the glyphs the document draws, so look at the other pages too.
                var fonts = new Dictionary<PdfDictionary, PdfExtractionFont?>(ReferenceEqualityComparer.Instance);
                for (int index = 0; index < probe.Tree.Pages.Count; index++)
                {
                    if (index == edit.PageIndex) continue;
                    PdfPageTreeEntry other = probe.Tree.Pages[index];
                    // This only looks for more glyphs, so a page that cannot be read is one with none to offer.
                    try
                    {
                        new ContentRewriter(document, null, [], false, fonts, cancellationToken, glyphsOnly: true,
                            usedCodes: used).RewritePage(other, Geometry(document, other));
                    }
                    catch (Exception error) when (error is FormatException or InvalidOperationException
                        or NotSupportedException or ArgumentException or Filters.PdfFilterException)
                    {
                    }
                }
                encoded = EncodeOriginal(font, composite, embedded, Used(key), lines);
            }
        }

        var update = new PdfIncrementalUpdateBuilder(document);
        PdfIndirectReference? substituteReference = null;
        PdfTextLineFont written = PdfTextLineFont.Original;
        if (encoded is null)
        {
            TrueTypeFont substitute = edit.Substitute
                ?? throw new NotSupportedException("The original font cannot show the new text.");
            if (font.IsVertical)
                throw new NotSupportedException("Vertical text can only be rewritten in its original font.");
            // A Type 3 font sets its own glyph space, so its size means nothing to another font.
            if (font.IsType3 && fontDictionary?.TryGetValue(Name("FontMatrix"), out PdfObject? glyphSpace) == true
                && Resolve(document, glyphSpace) is PdfArray { Count: 6 } matrix && Math.Abs(Number(document, matrix[3]) - 0.001) > 1e-6)
                throw new NotSupportedException("The font of this line has no letters for the new text, and its size does not carry over to another font.");
            if (!substitute.EmbeddingAllowed)
                throw new NotSupportedException($"The embedding permissions in {substitute.PostScriptName} prohibit PDF embedding.");
            var usage = new EmbeddedFontUsage(substitute, Name("KPEF"));
            encoded = EncodeSubstitute(substitute, usage, lines);
            if (usage.Mappings.Count > 0) substituteReference = AddEmbeddedFont(update, substitute, usage);
            written = PdfTextLineFont.Substitute;
        }
        if (font.IsVertical && (edit.Underline || edit.Strike))
            throw new NotSupportedException("Vertical text cannot be underlined or struck through.");

        // Sizes and spacing given in points convert through the text's own transformation.
        Matrix device = probe.Insertion.Glyph.Then(first.Ctm);
        double vertical = Math.Sqrt(device.C * device.C + device.D * device.D);
        double horizontal = Math.Sqrt(device.A * device.A + device.B * device.B);
        double? sizeFactor = null;
        if (edit.FontSize is double points && vertical > 0 && first.Size != 0)
        {
            double factor = points / (Math.Abs(first.Size) * vertical);
            if (Math.Abs(factor - 1) > 1e-6) sizeFactor = factor;
        }
        double? characterSpacing = edit.CharacterSpacing is double tracking && horizontal > 0 && first.Scale != 0
            ? tracking / (horizontal * first.Scale) : null;

        double newSize = first.Size * (sizeFactor ?? 1);
        double leading = first.Leading > 0 ? first.Leading * (sizeFactor ?? 1) : Math.Abs(newSize) * 1.2;
        var writer = new LineWriter(encoded, substituteReference, sizeFactor, edit.FillColor, characterSpacing,
            edit.Underline, edit.Strike, edit.ParagraphLines,
            edit.ParagraphLines is null ? null : probe.Insertion.DominantFill, leading);
        PdfContentBounds? newBox = edit.ParagraphBox;
        double emOnPage = Math.Abs(newSize) * vertical;
        bool outgrowsClip = false;
        if (edit.ParagraphLines is null)
        {
            newBox = NewLineBox(encoded, device, first, newSize, leading, characterSpacing, probe.Insertion.SourceBox);
            // A line drawn wholly outside its clip never showed, so the edit cuts nothing off.
            outgrowsClip = newBox is { } measured && Overlaps(probe.Insertion.SourceBox, probe.Insertion.LastClip)
                && !LetterClipHolds(probe.Insertion.LastClip, measured, emOnPage);
        }
        var insertion = new LineInsertion
        {
            Writer = writer,
            WriteAtGlyph = probe.Insertion.Glyphs,
            ProtectedBox = newBox,
            SourceRows = edit.ParagraphRows,
            LineEm = edit.ParagraphLines is null ? emOnPage : 0,
            LookGlyph = probe.Insertion.LookGlyph,
            AfterPieces = outgrowsClip
        };
        PdfPageTreeEntry page = probe.Tree.Pages[edit.PageIndex];
        var rewriter = new ContentRewriter(document, update, [edit.Region], false,
            new Dictionary<PdfDictionary, PdfExtractionFont?>(ReferenceEqualityComparer.Instance),
            cancellationToken, glyphsOnly: true, insertion: insertion,
            glyphFilter: probe.Line is { } lineGlyphs ? (_, state, text) => lineGlyphs.Holds(state, text) : null);
        List<PdfContentInstruction> content = rewriter.RewritePage(page, probe.Geometry);
        if (!insertion.Written)
            throw new InvalidOperationException("The page changed while its text was being rewritten.");
        if (insertion.Covered)
            throw edit.ParagraphLines is null
                ? new NotSupportedException("Content painted after this line would cover the new text.")
                : new PdfParagraphCollisionException("Content painted after the paragraph would cover the new box. Make the box smaller or move it clear of that content.");
        if (insertion.AfterPieces) PlaceAfterPieces(content, insertion, newBox!.Value, emOnPage, probe.Geometry);
        if (insertion.AfterText is { } rules) content.AddRange(rules);
        if (edit.ParagraphRows is { } sourceRows) content = WithoutUnderlines(content, sourceRows, probe.Geometry);

        var instructions = new List<PdfContentInstruction>(content.Count + 2) { Op("q") };
        instructions.AddRange(content);
        instructions.Add(Op("Q"));
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(PdfContentStreamWriter.Write(instructions));
        PdfIndirectReference contentReference = update.AddObject(new PdfStream(
            new PdfDictionary(new Dictionary<PdfName, PdfObject> { [Name("Filter")] = Name("FlateDecode") }),
            compressed.ToArray()));
        var entries = page.Dictionary.ToDictionary(entry => entry.Key, entry => entry.Value);
        entries[ContentsName] = contentReference;
        entries[ResourcesName] = rewriter.PageScope!.Build();
        update.ReplaceObject(page.Reference.ObjectNumber, new PdfDictionary(entries));
        return new PdfTextLineEditResult(update.Build(), written, rewriter.RemovedGlyphs);

        Dictionary<uint, PdfDecodedCharacter> Used(object? key) =>
            key is not null && used.TryGetValue(key, out Dictionary<uint, PdfDecodedCharacter>? codes) ? codes : [];
    }

    // An underline drawn as a thin filled box sits in the band just below a line's baseline, no wider
    // than the line. It belongs to that line, so it neither covers the replacement nor outlives the text.
    private static bool IsUnderline(PdfContentBounds area, IReadOnlyList<PdfParagraphRowSpan> rows) => rows.Any(row =>
        area.Height < row.Size * 0.15 && area.Top <= row.Baseline + row.Size * 0.02 && area.Top >= row.Baseline - row.Size * 0.35
        && area.Left >= row.Left - 1 && area.Right <= row.Right + 1);

    private static List<PdfContentInstruction> WithoutUnderlines(List<PdfContentInstruction> content,
        IReadOnlyList<PdfParagraphRowSpan> rows, PageGeometry geometry)
    {
        var result = new List<PdfContentInstruction>(content.Count);
        var saved = new Stack<(Matrix Ctm, double Width)>();
        var ctm = new Matrix(1, 0, 0, 1, -geometry.CropLeft, -geometry.CropBottom);
        double lineWidth = 1;
        for (int index = 0; index < content.Count; index++)
        {
            PdfContentInstruction instruction = content[index];
            switch (instruction.Operator)
            {
                case "q": saved.Push((ctm, lineWidth)); break;
                case "Q": if (saved.TryPop(out (Matrix Ctm, double Width) restored)) (ctm, lineWidth) = restored; break;
                case "cm" when Numbers(instruction, 6) is { } values:
                    ctm = new Matrix(values[0], values[1], values[2], values[3], values[4], values[5]).Then(ctm);
                    break;
                case "w" when Numbers(instruction, 1) is { } width: lineWidth = width[0]; break;
                case "re" or "m" when SimpleShape(content, index, ctm, lineWidth) is { } shape && IsUnderline(shape.Area, rows):
                    index = shape.Paint;
                    continue;
            }
            result.Add(instruction);
        }
        return result;
    }

    // The path starting here, when it is a shape a line's underline is drawn with: a filled box, a
    // filled four-point outline or one stroked line. A path with more to it is not an underline.
    private static (int Paint, PdfContentBounds Area)? SimpleShape(List<PdfContentInstruction> content, int start,
        Matrix ctm, double lineWidth)
    {
        if (ctm.B != 0 || ctm.C != 0
            || start > 0 && content[start - 1].Operator is "m" or "l" or "c" or "v" or "y" or "re" or "h") return null;
        var points = new List<PdfPoint>();
        int index = start;
        if (content[index].Operator == "re")
        {
            if (Numbers(content[index], 4) is not { } box) return null;
            points.Add(ctm.Point(box[0], box[1]));
            points.Add(ctm.Point(box[0] + box[2], box[1] + box[3]));
            index++;
        }
        else
        {
            for (; index < content.Count && content[index].Operator is "m" or "l"; index++)
            {
                if (index > start && content[index].Operator == "m" || Numbers(content[index], 2) is not { } point) return null;
                points.Add(ctm.Point(point[0], point[1]));
            }
            if (index < content.Count && content[index].Operator == "h") index++;
        }
        if (index >= content.Count) return null;
        string paint = content[index].Operator;
        bool filled = paint is "f" or "F" or "f*" && (content[start].Operator == "re" || points.Count is 4 or 5);
        bool stroked = paint is "S" or "s" && content[start].Operator == "m" && points.Count == 2;
        if (!filled && !stroked) return null;
        double reach = stroked ? Math.Abs(lineWidth * ctm.D) / 2 : 0;
        return (index, new PdfContentBounds(points.Min(point => point.X), points.Min(point => point.Y) - reach,
            points.Max(point => point.X), points.Max(point => point.Y) + reach));
    }

    private static double[]? Numbers(PdfContentInstruction instruction, int count)
    {
        if (instruction.Operands.Count != count) return null;
        var values = new double[count];
        for (int index = 0; index < count; index++)
        {
            if (instruction.Operands[index] is PdfInteger integer) values[index] = integer.Value;
            else if (instruction.Operands[index] is PdfReal real) values[index] = real.Value;
            else return null;
        }
        return values;
    }

    // Where the new text lands on the page, measured from its encoded widths. Vertical text has no
    // widths to go by, so its old place stands for it.
    private static PdfContentBounds? NewLineBox(List<EncodedLine> lines, Matrix device, TextState first, double size,
        double leading, double? characterSpacing, PdfContentBounds source)
    {
        if (first.Font?.IsVertical == true) return source;
        double em = Math.Abs(size), spacing = characterSpacing ?? first.CharacterSpacing;
        var rows = new List<PdfContentBounds>();
        for (int index = 0; index < lines.Count; index++)
        {
            EncodedLine line = lines[index];
            double width = (line.Units / 1000 * size + line.Glyphs * spacing + line.Spaces * first.WordSpacing) * first.Scale;
            if (width == 0) continue;
            double baseline = first.Rise - index * leading;
            PdfPoint[] corners = [device.Point(0, baseline - em * LineInkDescent), device.Point(width, baseline - em * LineInkDescent),
                device.Point(0, baseline + em * LineInkAscent), device.Point(width, baseline + em * LineInkAscent)];
            rows.Add(new PdfContentBounds(corners.Min(point => point.X), corners.Min(point => point.Y),
                corners.Max(point => point.X), corners.Max(point => point.Y)));
        }
        return rows.Count == 0 ? null : PdfContentBounds.Union(rows);
    }

    // Side bearings are not ink, so a clip that only shaves the ends of the advances cuts nothing.
    private static bool LetterClipHolds(PdfContentBounds clip, PdfContentBounds box, double em) =>
        ClipHolds(clip, new PdfContentBounds(Math.Min(box.Left + em * ThinShare, box.Right), box.Bottom,
            Math.Max(box.Right - em * ThinShare, box.Left), box.Top));

    private static Matrix Inverse(Matrix matrix)
    {
        double determinant = matrix.A * matrix.D - matrix.B * matrix.C;
        if (!double.IsFinite(determinant) || Math.Abs(determinant) < 1e-12)
            throw new NotSupportedException("The line is drawn under a transformation that cannot be undone.");
        return new Matrix(matrix.D / determinant, -matrix.B / determinant, -matrix.C / determinant, matrix.A / determinant,
            (matrix.C * matrix.F - matrix.D * matrix.E) / determinant, (matrix.B * matrix.E - matrix.A * matrix.F) / determinant);
    }

    private sealed record GraphicsLevel(Matrix Ctm, PdfContentBounds Clip, PdfContentInstruction[] States);

    // A line drawn in pieces can give each piece a clip of its own, as browsers do, and then no
    // piece's clip holds the whole line. The graphics state the pieces share does, so the new text
    // goes in a block of its own right after the last piece closes, with the first glyph's
    // transformation and graphics states put back.
    private static void PlaceAfterPieces(List<PdfContentInstruction> content, LineInsertion insertion,
        PdfContentBounds box, double em, PageGeometry geometry)
    {
        var level = new GraphicsLevel(new Matrix(1, 0, 0, 1, -geometry.CropLeft, -geometry.CropBottom),
            new PdfContentBounds(0, 0, geometry.Width, geometry.Height), []);
        var saved = new Stack<GraphicsLevel>();
        var path = new List<PdfPoint>();
        GraphicsLevel? firstLevel = null;
        bool clipping = false, lastSeen = false;
        int shared = -1;
        for (int index = 0; index < content.Count; index++)
        {
            PdfContentInstruction instruction = content[index];
            if (ReferenceEquals(instruction, insertion.FirstMark))
            {
                firstLevel = level;
                shared = saved.Count;
            }
            else if (ReferenceEquals(instruction, insertion.LastMark))
            {
                if (firstLevel is null || saved.Count <= shared) break;
                lastSeen = true;
            }
            switch (instruction.Operator)
            {
                case "q": saved.Push(level); break;
                case "Q":
                    if (saved.TryPop(out GraphicsLevel? restored)) level = restored;
                    if (!lastSeen)
                    {
                        if (firstLevel is not null) shared = Math.Min(shared, saved.Count);
                        break;
                    }
                    if (saved.Count > shared) break;
                    if (!LetterClipHolds(level.Clip, box, em)) throw new NotSupportedException(LineCutOff);
                    Matrix move = insertion.State!.Ctm.Then(Inverse(level.Ctm));
                    content.InsertRange(index + 1, [Op("q"), .. firstLevel!.States.Skip(level.States.Length),
                        Op("cm", move.A, move.B, move.C, move.D, move.E, move.F), .. insertion.Block, Op("Q")]);
                    content.RemoveAll(item => ReferenceEquals(item, insertion.FirstMark) || ReferenceEquals(item, insertion.LastMark));
                    return;
                case "cm" when Numbers(instruction, 6) is { } values:
                    level = level with { Ctm = new Matrix(values[0], values[1], values[2], values[3], values[4], values[5]).Then(level.Ctm) };
                    break;
                case "gs":
                    level = level with { States = [.. level.States, instruction] };
                    break;
                case "re" when Numbers(instruction, 4) is { } rectangle:
                    path.Add(level.Ctm.Point(rectangle[0], rectangle[1]));
                    path.Add(level.Ctm.Point(rectangle[0] + rectangle[2], rectangle[1] + rectangle[3]));
                    break;
                case "m" or "l" when Numbers(instruction, 2) is { } point:
                    path.Add(level.Ctm.Point(point[0], point[1]));
                    break;
                case "c" when Numbers(instruction, 6) is { } curve:
                    path.Add(level.Ctm.Point(curve[0], curve[1]));
                    path.Add(level.Ctm.Point(curve[2], curve[3]));
                    path.Add(level.Ctm.Point(curve[4], curve[5]));
                    break;
                case "v" or "y" when Numbers(instruction, 4) is { } curve:
                    path.Add(level.Ctm.Point(curve[0], curve[1]));
                    path.Add(level.Ctm.Point(curve[2], curve[3]));
                    break;
                case "W" or "W*": clipping = true; break;
                case "n" or "S" or "s" or "f" or "F" or "f*" or "B" or "B*" or "b" or "b*":
                    if (clipping && path.Count > 0)
                        level = level with
                        {
                            Clip = new PdfContentBounds(Math.Max(level.Clip.Left, path.Min(point => point.X)),
                                Math.Max(level.Clip.Bottom, path.Min(point => point.Y)),
                                Math.Min(level.Clip.Right, path.Max(point => point.X)),
                                Math.Min(level.Clip.Top, path.Max(point => point.Y)))
                        };
                    path.Clear();
                    clipping = false;
                    break;
            }
        }
        throw new NotSupportedException(LineCutOff);
    }

    // The line an area holds: the largest group of glyphs on one baseline at one size. A glyph riding
    // on that baseline belongs with them, as a superscript or a word in another size does, and a
    // glyph that only reaches the area, as a large numeral or a watermark behind the line does, is
    // left alone. Invisible text and zero sizes stay in the group because they stop the edit.
    private sealed record LineBaseline(PdfPoint Origin, double AxisX, double AxisY, double Size)
    {
        // Null when the area needs no narrowing, or when nothing in it looks like one line, as on
        // text set along a curve.
        internal static LineBaseline? Dominant(List<ParagraphGlyph> glyphs, PdfContentBounds region)
        {
            var groups = new List<(LineBaseline Line, int Inside, int Count)>();
            var reach = new PdfContentBounds(region.Left - 1, region.Bottom - 1, region.Right + 1, region.Top + 1);
            foreach (ParagraphGlyph glyph in glyphs)
            {
                LineBaseline placed = Of(glyph.State, glyph.Glyph);
                int inside = PdfContentRedaction.Inside(glyph.Bounds, reach) ? 1 : 0;
                int group = groups.FindIndex(item => item.Line.SameLine(placed));
                if (group < 0) groups.Add((placed, inside, 1));
                else groups[group] = (groups[group].Line, groups[group].Inside + inside, groups[group].Count + 1);
            }
            if (groups.Count < 2) return null;
            // A glyph that fits the area says more than one that only has its center there.
            LineBaseline line = groups.MaxBy(item => (item.Inside, item.Count)).Line;
            return glyphs.Count(glyph => line.Holds(glyph.State, glyph.Glyph)) * 3 < glyphs.Count ? null : line;
        }

        internal bool Holds(TextState state, Matrix glyph)
        {
            if (state.RenderMode is 3 or 7 || state.Size == 0) return true;
            LineBaseline placed = Of(state, glyph);
            if (!Parallel(placed)) return false;
            double across = Across(placed);
            return across <= Math.Max(1, Math.Min(placed.Size, Size) * 0.35) || placed.Size < Size && across <= Size * 0.6;
        }

        // A smaller glyph riding off the baseline, such as a stray empty-paragraph space under a title,
        // belongs to the line but never gives it its look (#431). Smaller text on the baseline still does.
        internal bool LineSized(TextState state, Matrix glyph)
        {
            LineBaseline placed = Of(state, glyph);
            return placed.Size >= Size * 0.98 || Across(placed) <= Math.Max(0.5, Size * 0.1);
        }

        private bool SameLine(LineBaseline other) => Parallel(other) && Math.Abs(other.Size - Size) <= Size * 0.02
            && Across(other) <= Math.Max(0.1, Size * 0.02);

        private bool Parallel(LineBaseline other) => Math.Abs(AxisX * other.AxisY - AxisY * other.AxisX) < 0.05;

        private double Across(LineBaseline other) =>
            Math.Abs((other.Origin.Y - Origin.Y) * AxisX - (other.Origin.X - Origin.X) * AxisY);

        private static LineBaseline Of(TextState state, Matrix glyph)
        {
            Matrix device = glyph.Then(state.Ctm);
            bool vertical = state.Font?.IsVertical == true;
            double x = vertical ? device.C : device.A, y = vertical ? device.D : device.B;
            double length = Math.Sqrt(x * x + y * y);
            return new LineBaseline(device.Point(0, state.Rise), length > 0 ? x / length : 1, length > 0 ? y / length : 0,
                Math.Abs(state.Size) * Math.Sqrt(device.C * device.C + device.D * device.D));
        }
    }

    private sealed record LineProbe(PdfTextLineInspection Inspection, LineInsertion Insertion,
        PdfPageTree Tree, PageGeometry Geometry, LineBaseline? Line);

    private static LineProbe Probe(PdfDocument document, int pageIndex, PdfContentBounds region,
        Dictionary<object, Dictionary<uint, PdfDecodedCharacter>>? used, CancellationToken cancellationToken,
        bool paragraph = false)
    {
        if (!double.IsFinite(region.Left) || !double.IsFinite(region.Bottom) || !double.IsFinite(region.Right)
            || !double.IsFinite(region.Top) || region.Right <= region.Left || region.Top <= region.Bottom)
            throw new ArgumentOutOfRangeException(nameof(region), "The area must be finite and not empty.");
        PdfPageTree tree = PdfPageTree.Read(document);
        if (pageIndex < 0 || pageIndex >= tree.Pages.Count) throw new ArgumentOutOfRangeException(nameof(pageIndex));
        PdfPageTreeEntry page = tree.Pages[pageIndex];
        PageGeometry geometry = Geometry(document, page);
        // A paragraph takes all its area holds, and a line only the glyphs of that line.
        List<ParagraphGlyph>? recorded = paragraph ? null : [];
        var insertion = new LineInsertion { InspectParagraph = paragraph, Recording = recorded };
        var rewriter = new ContentRewriter(document, null, [region], false,
            new Dictionary<PdfDictionary, PdfExtractionFont?>(ReferenceEqualityComparer.Instance),
            cancellationToken, glyphsOnly: true, insertion: insertion, usedCodes: used);
        rewriter.RewritePage(page, geometry);
        if (recorded is null) return Summarize(insertion, Unreadable(rewriter), tree, geometry);
        LineBaseline? line = LineBaseline.Dominant(recorded, region);
        ParagraphGlyph[] held = [.. recorded.Where(glyph => line?.Holds(glyph.State, glyph.Glyph) != false)];
        var lineGlyphs = new LineInsertion { LookGlyph = FirstShown(held, line) };
        foreach (ParagraphGlyph glyph in held) lineGlyphs.Observe(glyph.State, glyph.Glyph, glyph.Barred, glyph.Bounds);
        return Summarize(lineGlyphs, Unreadable(rewriter), tree, geometry, line);
    }

    // A line drawn twice, as text with a shadow is, shows its last copy, so the first glyph that no
    // later glyph lands on is where the look comes from. Counted from one.
    private static int FirstShown(ParagraphGlyph[] glyphs, LineBaseline? line = null)
    {
        bool anySized = line is not null && glyphs.Any(glyph => line.LineSized(glyph.State, glyph.Glyph));
        for (int index = 0; index < glyphs.Length - 1; index++)
        {
            if (anySized && !line!.LineSized(glyphs[index].State, glyphs[index].Glyph)) continue;
            Matrix device = glyphs[index].Glyph.Then(glyphs[index].State.Ctm);
            double reach = Math.Abs(glyphs[index].State.Size) * Math.Sqrt(device.C * device.C + device.D * device.D) * 0.2;
            if (!glyphs.Skip(index + 1).Any(later =>
            {
                Matrix other = later.Glyph.Then(later.State.Ctm);
                return Math.Abs(other.E - device.E) < reach && Math.Abs(other.F - device.F) < reach;
            })) return index + 1;
        }
        if (anySized)
            for (int index = glyphs.Length - 1; index >= 0; index--)
                if (line!.LineSized(glyphs[index].State, glyphs[index].Glyph)) return index + 1;
        return Math.Max(1, glyphs.Length);
    }

    private static bool Unreadable(ContentRewriter rewriter) =>
        rewriter.Reasons.Contains("UnreadableFont") || rewriter.Reasons.Contains("UndecodableText");

    private static LineProbe Summarize(LineInsertion insertion, bool unreadable, PdfPageTree tree, PageGeometry geometry,
        LineBaseline? line = null)
    {
        PdfTextLineBlocker blocker = insertion.Glyphs == 0
            ? unreadable ? PdfTextLineBlocker.UnreadableText : PdfTextLineBlocker.NoText
            : insertion.Invisible ? PdfTextLineBlocker.InvisibleText
            : insertion.ZeroSize ? PdfTextLineBlocker.ZeroSizeText
            : PdfTextLineBlocker.None;
        string fontName = insertion.State?.Font?.FontName ?? string.Empty;
        double size = 0;
        if (insertion.State is TextState state)
        {
            Matrix device = insertion.Glyph.Then(state.Ctm);
            size = Math.Abs(state.Size) * Math.Sqrt(device.C * device.C + device.D * device.D);
        }
        return new LineProbe(new PdfTextLineInspection(insertion.Glyphs, blocker, fontName, size), insertion, tree, geometry, line);
    }

    private sealed record ParagraphGlyph(PdfContentBounds Bounds, TextState State, Matrix Glyph, bool Barred);

    /// <summary>One pass over a page's text, so a page with hundreds of paragraphs is walked once
    /// instead of once per paragraph.</summary>
    internal sealed class ParagraphPaintMap
    {
        private readonly PdfDocument _document;
        private readonly PdfPageTree _tree;
        private readonly PageGeometry _geometry;
        private readonly List<ParagraphGlyph> _glyphs;
        private readonly bool _unreadable;

        internal ParagraphPaintMap(PdfDocument document, int pageIndex)
        {
            _document = document;
            _tree = PdfPageTree.Read(document);
            PdfPageTreeEntry page = _tree.Pages[pageIndex];
            _geometry = Geometry(document, page);
            _glyphs = [];
            var rewriter = new ContentRewriter(document, null,
                [new PdfContentBounds(0, 0, _geometry.Width, _geometry.Height)], false,
                new Dictionary<PdfDictionary, PdfExtractionFont?>(ReferenceEqualityComparer.Instance),
                default, glyphsOnly: true, insertion: new LineInsertion { Recording = _glyphs });
            rewriter.RewritePage(page, _geometry);
            _unreadable = Unreadable(rewriter);
        }

        internal (PdfRgbColor Color, PdfContentBounds Clip, bool MixedFills) Inspect(PdfContentBounds region)
        {
            var insertion = new LineInsertion { InspectParagraph = true };
            foreach (ParagraphGlyph glyph in _glyphs)
                if (ContainsCenter(glyph.Bounds, region))
                    insertion.Observe(glyph.State, glyph.Glyph, glyph.Barred, glyph.Bounds);
            return ParagraphPaint(_document, Summarize(insertion, _unreadable, _tree, _geometry));
        }
    }

    private static string[] ReplacementLines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Replace('\t', ' ').Split('\n');

    // Fonts are told apart by object number, since one font object may resolve more than once.
    private static object? FontKey(PdfDocument document, PdfObject? fontObject) => fontObject switch
    {
        null => null,
        PdfIndirectReference reference => reference.ObjectNumber,
        _ => Resolve(document, fontObject) as PdfDictionary
    };

    private static bool IsEmbedded(PdfDocument document, PdfDictionary font)
    {
        if (IsNamed(font, SubtypeName, "Type3")) return true;
        PdfDictionary described = font;
        if (IsNamed(font, SubtypeName, "Type0") && font.TryGetValue(Name("DescendantFonts"), out PdfObject? descendants)
            && Resolve(document, descendants) is PdfArray array && array.Count > 0
            && Resolve(document, array[0]) is PdfDictionary descendant)
            described = descendant;
        return described.TryGetValue(Name("FontDescriptor"), out PdfObject? value)
            && Resolve(document, value) is PdfDictionary descriptor
            && (descriptor.ContainsKey(Name("FontFile")) || descriptor.ContainsKey(Name("FontFile2"))
                || descriptor.ContainsKey(Name("FontFile3")));
    }

    private sealed record EncodedLine(IReadOnlyList<PdfObject> Items, double Units, int Glyphs, int Spaces = 0);

    // An embedded font can only show the glyphs its program holds, and a subset holds the ones the
    // document draws, so only codes already drawn in that font are used. A font that is not
    // embedded comes from the reader's own copy, so any code it maps with a width will do.
    // A space the font cannot show becomes a gap of the same width instead.
    private static List<EncodedLine>? EncodeOriginal(PdfExtractionFont font, bool composite, bool embedded,
        Dictionary<uint, PdfDecodedCharacter> used, string[] lines)
    {
        var byText = new Dictionary<string, PdfDecodedCharacter>(StringComparer.Ordinal);
        foreach (PdfDecodedCharacter character in used.Values) Consider(character);
        if (!embedded)
        {
            foreach (PdfDecodedCharacter mapping in font.Unicode.Mappings) Consider(mapping);
            if (!composite)
                for (int code = 0; code < 256; code++)
                {
                    IReadOnlyList<PdfDecodedCharacter> decoded;
                    try { decoded = font.Decode(new[] { (byte)code }); }
                    catch (Exception error) when (error is FormatException or ArgumentException or NotSupportedException)
                    {
                        continue;
                    }
                    if (decoded.Count == 1 && decoded[0].ByteLength == 1) Consider(decoded[0]);
                }
        }
        int longest = byText.Count == 0 ? 1 : Math.Max(1, byText.Keys.Max(key => key.Length));

        var result = new List<EncodedLine>(lines.Length);
        foreach (string line in lines)
        {
            var items = new List<PdfObject>();
            var pending = new List<byte>();
            double units = 0;
            int glyphs = 0, spaces = 0;
            int index = 0;
            while (index < line.Length)
            {
                PdfDecodedCharacter? match = null;
                int length = Math.Min(longest, line.Length - index);
                for (; length > 0; length--)
                {
                    if (index + length < line.Length && char.IsLowSurrogate(line[index + length])) continue;
                    if (byText.TryGetValue(line.Substring(index, length), out PdfDecodedCharacter found))
                    {
                        match = found;
                        break;
                    }
                }
                if (match is PdfDecodedCharacter character)
                {
                    for (int shift = (character.ByteLength - 1) * 8; shift >= 0; shift -= 8)
                        pending.Add((byte)(character.Code >> shift));
                    units += font.GetWidth(character.Code);
                    glyphs++;
                    if (character.ByteLength == 1 && character.Code == 32) spaces++;
                    index += length;
                    continue;
                }
                if (line[index] != ' ') return null;
                if (pending.Count > 0)
                {
                    items.Add(new PdfString(pending.ToArray(), PdfStringForm.Hexadecimal));
                    pending.Clear();
                }
                items.Add(new PdfReal(-ReplacementSpaceUnits));
                units += ReplacementSpaceUnits;
                index++;
            }
            if (pending.Count > 0) items.Add(new PdfString(pending.ToArray(), PdfStringForm.Hexadecimal));
            result.Add(new EncodedLine(Merge(items), units, glyphs, spaces));
        }
        return result;

        void Consider(PdfDecodedCharacter character)
        {
            if (string.IsNullOrEmpty(character.Text) || character.Text.Contains('�', StringComparison.Ordinal)
                || char.IsControl(character.Text[0]) || byText.ContainsKey(character.Text)) return;
            if (!used.ContainsKey(character.Code) && !(font.GetWidth(character.Code) > 0)) return;
            byText[character.Text] = character;
        }

        static List<PdfObject> Merge(List<PdfObject> items)
        {
            var merged = new List<PdfObject>();
            foreach (PdfObject item in items)
            {
                if (item is PdfReal real && merged.Count > 0 && merged[^1] is PdfReal previous)
                    merged[^1] = new PdfReal(previous.Value + real.Value);
                else merged.Add(item);
            }
            return merged;
        }
    }

    private static List<EncodedLine> EncodeSubstitute(TrueTypeFont font, EmbeddedFontUsage usage, string[] lines)
    {
        var result = new List<EncodedLine>(lines.Length);
        foreach (string line in lines)
        {
            var bytes = new List<byte>();
            double units = 0;
            int glyphs = 0;
            foreach (FontGlyphMapping mapping in font.MapText(line))
            {
                if (mapping.Glyph == 0)
                    throw new NotSupportedException($"{font.PostScriptName} has no glyph for {mapping.UnicodeSequence}.");
                ushort code = usage.AddMapping(mapping.Glyph, mapping.UnicodeSequence);
                bytes.Add((byte)(code >> 8));
                bytes.Add((byte)code);
                units += font.GetPdfAdvanceWidth(mapping.Glyph);
                glyphs++;
            }
            result.Add(new EncodedLine(bytes.Count == 0 ? [] : [new PdfString(bytes.ToArray(), PdfStringForm.Hexadecimal)],
                units, glyphs));
        }
        return result;
    }

    private static PdfIndirectReference AddEmbeddedFont(PdfIncrementalUpdateBuilder update, TrueTypeFont font,
        EmbeddedFontUsage usage)
    {
        PdfIndirectReference type0 = update.ReserveObject();
        PdfIndirectReference cidFont = update.ReserveObject();
        PdfIndirectReference descriptor = update.ReserveObject();
        PdfIndirectReference fontFile = update.ReserveObject();
        PdfIndirectReference toUnicode = update.ReserveObject();
        PdfIndirectReference encoding = update.ReserveObject();
        EmbeddedTrueTypeFontObjects values = PdfEmbeddedTrueTypeFontFactory.Create(
            font, usage.Mappings, type0, cidFont, descriptor, fontFile, toUnicode, encoding);
        update.SetObject(type0, values.Type0).SetObject(cidFont, values.CidFont)
            .SetObject(descriptor, values.Descriptor).SetObject(fontFile, values.FontFile)
            .SetObject(toUnicode, values.ToUnicode).SetObject(encoding, values.Encoding);
        return type0;
    }

    private sealed record LineWriter(IReadOnlyList<EncodedLine> Lines, PdfIndirectReference? Substitute,
        double? SizeFactor, PdfRgbColor? Fill, double? CharacterSpacing, bool Underline, bool Strike,
        IReadOnlyList<PdfParagraphLine>? ParagraphLines, PdfContentInstruction[]? ParagraphFill, double Leading);

    /// <summary>
    /// Follows the glyphs of an edited line through the content. The first one gives the new text
    /// its look and its place. The last one marks where it goes: right after the text-showing
    /// operator that held that glyph, inside the same text object, so it paints over anything drawn
    /// between the pieces of the line. After the new text the text state and position are put back
    /// exactly, so everything drawn later in that text object stays in place.
    /// </summary>
    private sealed class LineInsertion
    {
        private static readonly PdfName FontCategory = Name("Font");
        private readonly Dictionary<string, (int Glyphs, PdfContentInstruction[] Fill)> _fills = new(StringComparer.Ordinal);
        private PdfContentInstruction[]? _lastFill;
        private string _lastFillKey = string.Empty;
        private bool _pending;
        private ResourceScope? _firstScope;

        internal bool InspectParagraph { get; init; }
        // Which glyph gives the new text its look and place, counted from one.
        internal int LookGlyph { get; init; } = 1;
        internal List<ParagraphGlyph>? Recording { get; init; }
        internal int WriteAtGlyph { get; init; }
        internal LineWriter? Writer { get; init; }
        internal TextState? State { get; private set; }
        internal Matrix Glyph { get; private set; }
        internal int Glyphs { get; private set; }
        internal bool Invisible { get; private set; }
        internal bool ZeroSize { get; private set; }
        internal bool ParagraphUnsafe { get; private set; }
        internal bool ClipUnsafe { get; private set; }
        internal bool HasType3 { get; private set; }
        internal PdfContentInstruction[]? DominantFill => _fills.Count == 0 ? null
            : _fills.Values.MaxBy(item => item.Glyphs).Fill;
        internal bool MixedFills => _fills.Count > 1;
        internal PdfContentBounds? ProtectedBox { get; init; }
        internal IReadOnlyList<PdfParagraphRowSpan>? SourceRows { get; init; }
        // The em of a line's new text on the page. A paragraph has none, and any overlap covers it.
        internal double LineEm { get; init; }
        internal bool Covered { get; private set; }
        internal PdfContentBounds LastClip { get; private set; }
        internal PdfContentBounds SourceBox { get; private set; }
        // The new text outgrows the last glyph's clip, so it is held back as a block for after the pieces.
        internal bool AfterPieces { get; init; }
        internal PdfContentInstruction FirstMark { get; } = new("%first", 0, []);
        internal PdfContentInstruction LastMark { get; } = new("%last", 0, []);
        internal List<PdfContentInstruction> Block { get; } = [];
        internal bool Written { get; private set; }
        internal List<PdfContentInstruction>? AfterText { get; set; }
        internal int AfterTextDepth { get; private set; }

        // Producers rarely write a plain identity. Ghostscript scales by 0.12 then 8.333 with a 0.999
        // text matrix, and browsers flip the page and the text back. pdfTeX's font expansion stretches
        // each line by up to 2 percent. Text that lands upright and that close to uniform can be
        // replaced through the same transform; anything else cannot.
        private static bool IsUprightText(Matrix glyph, Matrix ctm)
        {
            Matrix device = glyph.Then(ctm);
            return Math.Abs(glyph.B) < 1e-8 && Math.Abs(glyph.C) < 1e-8 && Math.Abs(ctm.B) < 1e-8
                && Math.Abs(ctm.C) < 1e-8 && ctm.A != 0 && ctm.D != 0 && device.A > 0 && device.D > 0
                && Math.Abs(device.A / device.D - 1) < 0.03;
        }

        // The same color written twice is the same fill, and no operators at all mean black.
        private static string FillKey(PdfContentInstruction[] fill) =>
            Convert.ToHexString(PdfContentStreamWriter.Write(fill.Length > 0 ? fill : [new PdfContentInstruction("g", 0, [new PdfInteger(0)])]));

        internal void Observe(TextState state, Matrix glyph, bool barredFromParagraphs, PdfContentBounds bounds)
        {
            if (Recording is { } recording)
            {
                recording.Add(new ParagraphGlyph(bounds, state, glyph, barredFromParagraphs));
                return;
            }
            Glyphs++;
            if (state.RenderMode is 3 or 7) Invisible = true;
            if (state.Size == 0) ZeroSize = true;
            if (state.Font?.IsType3 == true) HasType3 = true;
            if (!ReferenceEquals(state.Fill, _lastFill))
            {
                _lastFill = state.Fill;
                _lastFillKey = FillKey(state.Fill);
            }
            _fills[_lastFillKey] = (_fills.GetValueOrDefault(_lastFillKey).Glyphs + 1, state.Fill);
            if (InspectParagraph)
            {
                if (!state.ClipIsRectangle) ClipUnsafe = true;
                if (barredFromParagraphs || state.SoftMasked || state.Size <= 0 || state.RenderMode != 0
                    || state.Scale != 1 || state.Font?.IsVertical == true || !IsUprightText(glyph, state.Ctm)
                    || (State is not null && Math.Abs(Glyph.D - glyph.D) > 1e-6))
                    ParagraphUnsafe = true;
                if (State is { } first && (first.Ctm != state.Ctm || first.Clip != state.Clip || first.ExtGStates != state.ExtGStates))
                    ParagraphUnsafe = true;
            }
            // A paragraph can span text objects with other content painted between them, so its
            // replacement waits for the last source glyph and paints over all of that content.
            if (Glyphs == WriteAtGlyph) _pending = true;
            LastClip = state.Clip;
            SourceBox = Glyphs == 1 ? bounds : new PdfContentBounds(Math.Min(SourceBox.Left, bounds.Left),
                Math.Min(SourceBox.Bottom, bounds.Bottom), Math.Max(SourceBox.Right, bounds.Right), Math.Max(SourceBox.Top, bounds.Top));
            if (Glyphs != LookGlyph) return;
            State = state;
            Glyph = glyph;
        }

        // Called for each fill, image and shading. Once the replacement is written, anything drawn over
        // its box paints on top of it, and the page would show that content instead of the new text.
        internal void Paint(PdfContentBounds area)
        {
            if (Written && ProtectedBox is { } box && Overlaps(area, box) && !SparesLetters(area, box)
                && !(SourceRows is { } rows && IsUnderline(area, rows))) Covered = true;
        }

        // A rule across a line or a neighbor's letter leaves the new letters readable. So does
        // whatever lay over the old text: it let that text show or hid it already, and the edit
        // changes nothing there.
        private bool SparesLetters(PdfContentBounds area, PdfContentBounds box)
        {
            if (LineEm == 0) return false;
            var overlap = new PdfContentBounds(Math.Max(area.Left, box.Left), Math.Max(area.Bottom, box.Bottom),
                Math.Min(area.Right, box.Right), Math.Min(area.Top, box.Top));
            double slack = LineEm * ThinShare;
            return overlap.Width < LineEm * LetterShare || overlap.Height < slack || Inside(overlap, new PdfContentBounds(
                SourceBox.Left - slack, SourceBox.Bottom - slack, SourceBox.Right + slack, SourceBox.Top + slack));
        }

        internal void WritePending(List<PdfContentInstruction> output, TextState state, Matrix text, Matrix line,
            ResourceScope scope, int depth)
        {
            // This runs after every operator that lost a glyph, so this is the look glyph's scope.
            if (_firstScope is null && State is not null)
            {
                _firstScope = scope;
                if (AfterPieces && depth == 0) output.Add(FirstMark);
            }
            if (!_pending) return;
            _pending = false;
            LineWriter writer = Writer!;
            TextState first = State!;
            bool lineEdit = writer.ParagraphLines is null;
            List<PdfContentInstruction> target = AfterPieces ? Block : output;
            if (AfterPieces)
            {
                if (depth == 0) output.Add(LastMark);
                target.Add(Op("BT"));
            }
            double size0 = state.Size;
            double factor = writer.SizeFactor ?? 1;
            // The factor is relative to the first glyph, and the text is written at the last, which
            // can be a superscript in another size.
            double size = first.Size * factor;
            bool otherFont = lineEdit && !ReferenceEquals(first.Font, state.Font);
            bool fontChange = AfterPieces || writer.Substitute is not null || writer.SizeFactor is not null
                || otherFont || lineEdit && size != size0;
            PdfName? original = state.FontResource
                ?? (fontChange && state.FontObject is { } fontObject ? scope.Add(FontCategory, "KPEO", fontObject) : null);
            if (fontChange && original is null)
                throw new NotSupportedException("The original font cannot be selected again after the new text.");
            bool sameScope = ReferenceEquals(scope, _firstScope);
            PdfName? chosen = !otherFont ? original
                : sameScope && first.FontResource is { } own ? own : scope.Add(FontCategory, "KPEO", first.FontObject!);
            if (writer.Substitute is { } substitute)
                target.Add(new PdfContentInstruction("Tf", 0, [scope.Add(FontCategory, "KPEF", substitute), new PdfReal(size)]));
            else if (fontChange)
                target.Add(new PdfContentInstruction("Tf", 0, [chosen!, new PdfReal(size)]));
            if (writer.Fill is PdfRgbColor fill) target.Add(Op("rg", fill.Red, fill.Green, fill.Blue));
            // The look comes from the first glyph, or a paragraph's dominant fill, and the text is
            // written at the last glyph, which can be a closing link in another color.
            PdfContentInstruction[] look = writer.ParagraphFill ?? first.Fill;
            bool fillChange = writer.Fill is null && (AfterPieces || FillKey(look) != FillKey(state.Fill));
            if (fillChange && !sameScope && look.Any(item => item.Operands.Any(operand => operand is PdfName)))
                throw new NotSupportedException("The line is drawn in separate parts of the page, and its color cannot be carried across them.");
            if (fillChange) target.AddRange(look.Length > 0 ? look : [Op("g", 0)]);
            var restore = new List<PdfContentInstruction>();
            void Set(string name, double wanted, double current)
            {
                if (wanted == current && !AfterPieces) return;
                target.Add(Op(name, wanted));
                restore.Add(Op(name, current));
            }
            // Justified source lines carry their own spacing, which must not stretch a paragraph.
            Set("Tc", lineEdit ? writer.CharacterSpacing ?? first.CharacterSpacing : 0, state.CharacterSpacing);
            Set("Tw", lineEdit ? first.WordSpacing : 0, state.WordSpacing);
            Set("Ts", lineEdit ? first.Rise : 0, state.Rise);
            if (lineEdit)
            {
                Set("Tz", first.Scale * 100, state.Scale * 100);
                Set("Tr", first.RenderMode, state.RenderMode);
            }
            // The place was found under the first glyph's transformation, which the last can differ from.
            Matrix place = AfterPieces || first.Ctm == state.Ctm ? Glyph : Glyph.Then(first.Ctm).Then(Inverse(state.Ctm));
            target.Add(Op("Tm", place.A, place.B, place.C, place.D, place.E, place.F));
            for (int index = 0; index < writer.Lines.Count; index++)
            {
                // Font expansion is typesetting, so the plan measures and this writes at unit aspect.
                if (writer.ParagraphLines is { } positions && index < positions.Count)
                    target.Add(Op("Tm", positions[index].Scale * Glyph.D * state.Ctm.D / state.Ctm.A, 0, 0, Glyph.D, (positions[index].X - state.Ctm.E) / state.Ctm.A,
                        (positions[index].Baseline - state.Ctm.F) / state.Ctm.D));
                else if (index > 0) target.Add(Op("Td", 0, -writer.Leading));
                if (writer.Lines[index].Items.Count > 0)
                    target.Add(new PdfContentInstruction("TJ", 0, [new PdfArray(writer.Lines[index].Items)]));
            }
            Written = true;
            if (AfterPieces)
            {
                target.Add(Op("ET"));
                if (writer.Underline || writer.Strike) target.AddRange(Rules(writer, first, place, size));
                return;
            }

            if (fontChange) output.Add(new PdfContentInstruction("Tf", 0, [original!, new PdfReal(size0)]));
            if (writer.Fill is not null || fillChange) output.AddRange(state.Fill.Length > 0 ? state.Fill : [Op("g", 0)]);
            output.AddRange(restore);
            output.Add(Op("Tm", line.A, line.B, line.C, line.D, line.E, line.F));
            bool vertical = state.Font?.IsVertical == true;
            double axisX = vertical ? line.C : line.A, axisY = vertical ? line.D : line.B;
            double length = axisX * axisX + axisY * axisY;
            double moved = length > 0 ? ((text.E - line.E) * axisX + (text.F - line.F) * axisY) / length : 0;
            if (Math.Abs(moved) > 1e-9)
            {
                double scale = vertical ? size0 : size0 * state.Scale;
                if (scale == 0) throw new NotSupportedException("The text position cannot be restored at a zero font size.");
                output.Add(new PdfContentInstruction("TJ", 0, [new PdfArray([new PdfReal(-moved * 1000 / scale)])]));
            }

            if (writer.Underline || writer.Strike)
            {
                AfterText = Rules(writer, first, place, size);
                AfterTextDepth = depth;
            }
        }

        // Underline and strikethrough are drawn after the text object ends, since paths cannot be
        // drawn inside one. Nothing changes the transformation inside a text object, so the matrix
        // the text was placed with still places them.
        private static List<PdfContentInstruction> Rules(LineWriter writer, TextState state, Matrix place, double size)
        {
            double spacing = writer.CharacterSpacing ?? state.CharacterSpacing;
            double scale = Math.Sqrt(Math.Abs(place.A * place.D - place.B * place.C));
            var rules = new List<PdfContentInstruction> { Op("q") };
            if (writer.Fill is PdfRgbColor fill) rules.Add(Op("RG", fill.Red, fill.Green, fill.Blue));
            else if (state.Fill.Length == 0) rules.Add(Op("G", 0));
            else rules.AddRange(state.Fill.Select(item => new PdfContentInstruction(
                item.Operator.ToUpperInvariant(), 0, [.. item.Operands])));
            rules.Add(Op("w", Math.Max(0.01, Math.Abs(size) / 16 * scale)));
            for (int index = 0; index < writer.Lines.Count; index++)
            {
                EncodedLine line = writer.Lines[index];
                double width = (line.Units / 1000 * size + line.Glyphs * spacing) * state.Scale;
                if (width <= 0) continue;
                double baseline = -index * writer.Leading;
                foreach (double offset in new[] { writer.Underline ? -0.12 : double.NaN, writer.Strike ? 0.3 : double.NaN })
                {
                    if (double.IsNaN(offset)) continue;
                    PdfPoint start = place.Point(0, baseline + offset * size);
                    PdfPoint end = place.Point(width, baseline + offset * size);
                    rules.Add(Op("m", start.X, start.Y));
                    rules.Add(Op("l", end.X, end.Y));
                }
            }
            rules.Add(Op("S"));
            rules.Add(Op("Q"));
            return rules;
        }
    }
}
