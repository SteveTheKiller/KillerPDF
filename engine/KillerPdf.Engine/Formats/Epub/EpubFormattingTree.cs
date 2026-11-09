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

        void VisitChildren(EpubNativeContent.Element element, Block containing)
        {
            foreach (var child in element.Children)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++work > WorkLimit)
                    throw new InvalidDataException("Native EPUB formatting exceeds its box work limit.");
                if (child is EpubNativeContent.Text text)
                {
                    if ((element.Name is "ul" or "ol" || element.Display is EpubNativeContent.DisplayKind.Table or EpubNativeContent.DisplayKind.TableRowGroup or EpubNativeContent.DisplayKind.TableRow)
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
