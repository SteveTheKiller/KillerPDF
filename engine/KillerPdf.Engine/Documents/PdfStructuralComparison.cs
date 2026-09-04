using System.Security.Cryptography;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Writing;

namespace KillerPdf.Engine.Documents;

/// <summary>A category of structural page-content change.</summary>
public enum PdfStructuralChangeKind
{
    /// <summary>A page exists only in the changed document.</summary>
    PageAdded,
    /// <summary>A page exists only in the original document.</summary>
    PageRemoved,
    /// <summary>The page dimensions changed.</summary>
    PageSize,
    /// <summary>Extracted text or its geometry and font metadata changed.</summary>
    Text,
    /// <summary>Image placements changed.</summary>
    Images,
    /// <summary>Vector paths changed.</summary>
    Paths,
    /// <summary>Shading placements changed.</summary>
    Shadings,
    /// <summary>The effective page resource graph changed.</summary>
    Resources,
    /// <summary>The decoded page-content instruction sequence changed.</summary>
    Instructions
}

/// <summary>One page-level structural difference between two documents.</summary>
public sealed record PdfStructuralChange(
    int PageIndex,
    PdfStructuralChangeKind Kind,
    int OriginalCount,
    int ChangedCount);

/// <summary>A structural comparison of interpreted page content.</summary>
public sealed class PdfStructuralComparison
{
    private PdfStructuralComparison(IEnumerable<PdfStructuralChange> changes)
    {
        Changes = Array.AsReadOnly(changes.ToArray());
    }

    /// <summary>Gets structural changes in page and category order.</summary>
    public IReadOnlyList<PdfStructuralChange> Changes { get; }
    /// <summary>Gets whether any structural page-content change was found.</summary>
    public bool HasChanges => Changes.Count > 0;

    /// <summary>Compares interpreted content, effective resources, and page geometry.</summary>
    public static PdfStructuralComparison Compare(PdfDocument original, PdfDocument changed,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(changed);
        var before = new PdfPageContentReader(original);
        var after = new PdfPageContentReader(changed);
        PdfPageTree beforeTree = PdfPageTree.Read(original);
        PdfPageTree afterTree = PdfPageTree.Read(changed);
        var changes = new List<PdfStructuralChange>();
        int sharedPages = Math.Min(before.PageCount, after.PageCount);
        for (int pageIndex = 0; pageIndex < sharedPages; pageIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PdfPageContent left = before.Read(pageIndex, cancellationToken);
            PdfPageContent right = after.Read(pageIndex, cancellationToken);
            if (left.Width != right.Width || left.Height != right.Height)
                changes.Add(new(pageIndex, PdfStructuralChangeKind.PageSize, 1, 1));
            AddDifference(changes, pageIndex, PdfStructuralChangeKind.Text,
                left.Letters, right.Letters);
            AddDifference(changes, pageIndex, PdfStructuralChangeKind.Images,
                left.Images, right.Images);
            AddDifference(changes, pageIndex, PdfStructuralChangeKind.Paths,
                left.Paths.Select(PathSignature), right.Paths.Select(PathSignature));
            AddDifference(changes, pageIndex, PdfStructuralChangeKind.Shadings,
                left.Shadings, right.Shadings);
            AddDifference(changes, pageIndex, PdfStructuralChangeKind.Instructions,
                left.Instructions.Select(InstructionSignature),
                right.Instructions.Select(InstructionSignature));
            AddResourceDifference(changes, pageIndex,
                original, beforeTree.Pages[pageIndex], changed, afterTree.Pages[pageIndex],
                cancellationToken);
        }
        for (int pageIndex = sharedPages; pageIndex < before.PageCount; pageIndex++)
            changes.Add(new(pageIndex, PdfStructuralChangeKind.PageRemoved, 1, 0));
        for (int pageIndex = sharedPages; pageIndex < after.PageCount; pageIndex++)
            changes.Add(new(pageIndex, PdfStructuralChangeKind.PageAdded, 0, 1));
        return new PdfStructuralComparison(changes);
    }

    private static void AddDifference<T>(List<PdfStructuralChange> changes, int pageIndex,
        PdfStructuralChangeKind kind, IEnumerable<T> original, IEnumerable<T> changed)
    {
        T[] left = [.. original];
        T[] right = [.. changed];
        if (!left.SequenceEqual(right))
            changes.Add(new(pageIndex, kind, left.Length, right.Length));
    }

    private static string PathSignature(PdfExtractedPath path) => string.Join('|',
        path.PaintOperator, path.IsClippingPath, path.BoundingBox,
        string.Join(';', path.Segments.Select(segment =>
            $"{segment.Operator}:{string.Join(',', segment.Points)}")));

    private static string InstructionSignature(
        KillerPdf.Engine.Parsing.PdfContentInstruction instruction)
    {
        using var output = new MemoryStream();
        foreach (PdfObject operand in instruction.Operands)
        {
            PdfObjectWriter.Write(output, operand);
            output.WriteByte(0);
        }
        return instruction.Operator + ":" + Convert.ToHexString(output.ToArray()) + ":"
            + (instruction.InlineImageData.HasValue
                ? Convert.ToHexString(instruction.InlineImageData.Value.Span) : string.Empty);
    }

    private static void AddResourceDifference(
        List<PdfStructuralChange> changes, int pageIndex,
        PdfDocument original, PdfPageTreeEntry originalPage,
        PdfDocument changed, PdfPageTreeEntry changedPage,
        CancellationToken cancellationToken)
    {
        (int leftCount, byte[] leftHash) = ResourceSignature(
            original, originalPage, cancellationToken);
        (int rightCount, byte[] rightHash) = ResourceSignature(
            changed, changedPage, cancellationToken);
        if (!leftHash.AsSpan().SequenceEqual(rightHash))
            changes.Add(new(pageIndex, PdfStructuralChangeKind.Resources,
                leftCount, rightCount));
    }

    private static (int Count, byte[] Hash) ResourceSignature(
        PdfDocument document, PdfPageTreeEntry page, CancellationToken cancellationToken)
    {
        PdfName resourcesName = PdfPageTree.Name("Resources");
        if (!page.InheritedValues.TryGetValue(resourcesName, out PdfObject? resources))
            return (0, SHA256.HashData([0]));

        PdfObject resolved = resources is PdfIndirectReference reference
            ? document.Resolve(reference)
            : resources;
        int count = resolved is PdfDictionary dictionary ? dictionary.Count : 0;
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        var references = new Dictionary<(int ObjectNumber, int Generation), int>();
        WriteObject(resources, 0);
        return (count, SHA256.HashData(output.GetBuffer().AsSpan(0, checked((int)output.Length))));

        void WriteObject(PdfObject value, int depth)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (depth >= 256)
                throw new InvalidOperationException(
                    "A page resource graph exceeds the supported nesting depth.");
            if (value is PdfIndirectReference indirect)
            {
                var identity = (indirect.ObjectNumber, indirect.Generation);
                if (references.TryGetValue(identity, out int existing))
                {
                    writer.Write((byte)'R');
                    writer.Write(existing);
                    return;
                }
                int ordinal = references.Count;
                references.Add(identity, ordinal);
                writer.Write((byte)'r');
                writer.Write(ordinal);
                WriteObject(document.Resolve(indirect), depth + 1);
                return;
            }

            switch (value)
            {
                case PdfNull:
                    writer.Write((byte)'0');
                    break;
                case PdfBoolean boolean:
                    writer.Write((byte)'b');
                    writer.Write(boolean.Value);
                    break;
                case PdfInteger integer:
                    writer.Write((byte)'i');
                    writer.Write(integer.Value);
                    break;
                case PdfReal real:
                    writer.Write((byte)'f');
                    writer.Write(real.Value);
                    break;
                case PdfName name:
                    writer.Write((byte)'n');
                    WriteBytes(name.Bytes.Span);
                    break;
                case PdfString text:
                    writer.Write((byte)'s');
                    WriteBytes(text.Bytes.Span);
                    break;
                case PdfArray array:
                    writer.Write((byte)'a');
                    writer.Write(array.Count);
                    foreach (PdfObject item in array)
                        WriteObject(item, depth + 1);
                    break;
                case PdfDictionary dictionary:
                    writer.Write((byte)'d');
                    writer.Write(dictionary.Count);
                    foreach (KeyValuePair<PdfName, PdfObject> entry in dictionary.OrderBy(
                                 entry => Convert.ToHexString(entry.Key.Bytes.Span),
                                 StringComparer.Ordinal))
                    {
                        WriteBytes(entry.Key.Bytes.Span);
                        WriteObject(entry.Value, depth + 1);
                    }
                    break;
                case PdfStream stream:
                    writer.Write((byte)'t');
                    WriteObject(stream.Dictionary, depth + 1);
                    WriteBytes(stream.EncodedData.Span);
                    break;
                default:
                    throw new NotSupportedException(
                        $"PDF object type {value.GetType().FullName} cannot be compared.");
            }
        }

        void WriteBytes(ReadOnlySpan<byte> value)
        {
            writer.Write(value.Length);
            writer.Write(value);
        }
    }
}
