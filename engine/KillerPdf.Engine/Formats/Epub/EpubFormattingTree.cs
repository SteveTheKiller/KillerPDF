namespace KillerPdf.Engine.Formats.Epub;

// Formatting boxes retain source identity without introducing extraction characters.
internal sealed class EpubFormattingTree
{
    internal abstract class Part;
    internal class Block(EpubNativeContent.Element source, Block? parent) : Part
    {
        internal EpubNativeContent.Element Source { get; } = source;
        internal Block? Parent { get; } = parent;
        internal List<Part> Children { get; } = new();
        internal EpubNativeContent.InlineReference? Replaced { get; set; }
    }
    internal sealed class Table(EpubNativeContent.Element source, Block parent) : Block(source, parent);
    internal sealed class RowGroup(EpubNativeContent.Element source, Block parent) : Block(source, parent);
    internal sealed class Row(EpubNativeContent.Element source, Block parent) : Block(source, parent);
    internal sealed class Cell(EpubNativeContent.Element source, Block parent) : Block(source, parent);
    internal sealed class ListItem(EpubNativeContent.Element source, Block parent) : Block(source, parent);
    internal sealed class InlineSequence(Block block) : Part
    {
        internal Block Block { get; } = block;
        internal List<Atom> Atoms { get; } = new();
    }
    internal readonly record struct Atom(int Offset, EpubNativeContent.Element Owner,
        EpubNativeContent.InlineReference? Reference = null, bool IsSpace = false, bool HasSource = true, bool HardBreak = false);

    // Formatting visits each node and each source character a bounded number of times.
    private const int WorkLimit = 4 * EpubNativeContent.MaximumChapterCharacters;

    internal Block Root { get; }
    internal Dictionary<int, InlineSequence> TextSequences { get; } = new();

    internal EpubFormattingTree(EpubNativeContent content, CancellationToken cancellationToken)
    {
        Root = new(content.Body ?? throw new InvalidDataException("Missing EPUB formatting body."), null);
        var references = new Dictionary<EpubNativeContent.Element, EpubNativeContent.InlineReference>();
        foreach (var reference in content.Inlines)
            if (!reference.Paragraph) references.Add(reference.Owner, reference);
        int work = 0;
        VisitChildren(Root.Source, Root);

        void VisitChildren(EpubNativeContent.Element element, Block originalContaining)
        {
            // CSS 2.1 section 17.2.1: consecutive content a row holds outside cells shares one anonymous cell, and a
            // table or row group wraps it in an anonymous row as well; a real row or cell ends the anonymous box.
            Block? anonymous = null;
            bool tablePart = element.Display is EpubNativeContent.DisplayKind.Table
                or EpubNativeContent.DisplayKind.TableRowGroup or EpubNativeContent.DisplayKind.TableRow;
            foreach (var child in element.Children)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++work > WorkLimit)
                    throw new InvalidDataException("Native EPUB formatting exceeds its box work limit.");
                var containing = originalContaining;
                if (tablePart && Wrapped(child)) containing = Anonymous(child);
                else if (tablePart && child is EpubNativeContent.Element { IsDisplayNone: false }) anonymous = null;
                if (child is EpubNativeContent.Text text)
                {
                    if ((element.Name is "ul" or "ol" || tablePart)
                        && EpubNativeContent.IsFormattingWhitespace(text.Value)) continue;
                    var positions = new Dictionary<int, int>();
                    foreach (int index in text.Positions)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (index >= content.Characters.Count) break;
                        positions.Add(content.Characters[index]!.Offset, index);
                    }
                    int cursor = Math.Min(text.Offset, content.Characters.Count);
                    for (int raw = 0; raw < text.Value.Length; raw++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (++work > WorkLimit)
                            throw new InvalidDataException("Native EPUB formatting exceeds its box work limit.");
                        bool space = text.Value[raw] is ' ' or '\t' or '\r' or '\n';
                        var sequence = Sequence(containing);
                        bool mapped = positions.TryGetValue(raw, out int index);
                        if (mapped)
                        {
                            TextSequences.Add(index, sequence);
                            cursor = index + 1;
                        }
                        else if (!space)
                            throw new InvalidDataException("Native EPUB source text has no normalized position.");
                        bool preserved = text.Owner.Style.PreserveWhitespace;
                        // The CR of a preformatted CR LF pair has no text of its own; its LF ends the line.
                        if (preserved && !mapped && text.Value[raw] == '\r') continue;
                        if (preserved && !mapped)
                            throw new InvalidDataException("Native EPUB preserved text has no source position.");
                        if (!preserved && space && sequence.Atoms.LastOrDefault() is { IsSpace: true }) continue;
                        sequence.Atoms.Add(new(mapped ? index : cursor, text.Owner, IsSpace: space, HasSource: mapped,
                            HardBreak: preserved && text.Value[raw] is '\n' or '\r'));
                    }
                    continue;
                }
                var source = (EpubNativeContent.Element)child;
                if (source.IsDisplayNone) continue;
                Block target = containing;
                if (source.IsBlock)
                {
                    target = source.Display switch
                    {
                        EpubNativeContent.DisplayKind.Table => new Table(source, containing),
                        EpubNativeContent.DisplayKind.TableRowGroup => new RowGroup(source, containing),
                        EpubNativeContent.DisplayKind.TableRow => new Row(source, containing),
                        EpubNativeContent.DisplayKind.TableCell => new Cell(source, containing),
                        EpubNativeContent.DisplayKind.ListItem => new ListItem(source, containing),
                        _ => new Block(source, containing)
                    };
                    containing.Children.Add(target);
                }
                if (references.TryGetValue(source, out var reference))
                {
                    if (source.IsBlock && reference.Source is not null) target.Replaced = reference;
                    else Sequence(target).Atoms.Add(new(reference.Offset, source, reference));
                }
                else VisitChildren(source, target);
            }

            // Content outside the table structure: visible text, or an element that is not a part this table level
            // expects (a table keeps its caption and other blocks as captions, as before).
            bool Wrapped(EpubNativeContent.SourceNode child) => child switch
            {
                EpubNativeContent.Text text => !EpubNativeContent.IsFormattingWhitespace(text.Value),
                EpubNativeContent.Element { IsDisplayNone: true } => false,
                EpubNativeContent.Element part => element.Display switch
                {
                    EpubNativeContent.DisplayKind.TableRow => part.Display != EpubNativeContent.DisplayKind.TableCell,
                    EpubNativeContent.DisplayKind.TableRowGroup => part.Display != EpubNativeContent.DisplayKind.TableRow,
                    _ => !part.IsBlock || part.Display == EpubNativeContent.DisplayKind.TableCell
                },
                _ => false
            };

            Block Anonymous(EpubNativeContent.SourceNode child)
            {
                int start = child is EpubNativeContent.Text text ? text.Offset : ((EpubNativeContent.Element)child).Offset;
                int end = child is EpubNativeContent.Text node ? (node.Positions.Count == 0 ? node.Offset : node.Positions[^1] + 1)
                    : ((EpubNativeContent.Element)child).EndOffset;
                // A cell a table or row group holds directly needs only an anonymous row around it.
                bool cell = child is EpubNativeContent.Element { Display: EpubNativeContent.DisplayKind.TableCell };
                // A cell after anonymous content joins that content's anonymous row.
                if (cell && anonymous is Cell { Parent: Row anonymousParent }) anonymous = anonymousParent;
                if (anonymous is null)
                {
                    Block parent = originalContaining;
                    if (element.Display != EpubNativeContent.DisplayKind.TableRow)
                    {
                        var row = new Row(EpubNativeContent.AnonymousTableBox(element, EpubNativeContent.DisplayKind.TableRow, start), parent);
                        parent.Children.Add(row);
                        parent = row;
                    }
                    if (cell) anonymous = parent;
                    else
                    {
                        anonymous = new Cell(EpubNativeContent.AnonymousTableBox(parent.Source, EpubNativeContent.DisplayKind.TableCell, start), parent);
                        parent.Children.Add(anonymous);
                    }
                }
                else if (!cell && anonymous is Row anonymousRow)
                {
                    anonymous = new Cell(EpubNativeContent.AnonymousTableBox(anonymousRow.Source, EpubNativeContent.DisplayKind.TableCell, start), anonymousRow);
                    anonymousRow.Children.Add(anonymous);
                }
                // The anonymous boxes reach to the end of what they hold, so their flows lay out all of it.
                for (Block? box = anonymous; box is not null && !ReferenceEquals(box, originalContaining); box = box.Parent)
                    box.Source.EndOffset = Math.Max(box.Source.EndOffset, end);
                return anonymous;
            }
        }

        static InlineSequence Sequence(Block block)
        {
            if (block.Children.LastOrDefault() is InlineSequence sequence) return sequence;
            var created = new InlineSequence(block);
            block.Children.Add(created);
            return created;
        }
    }
}
