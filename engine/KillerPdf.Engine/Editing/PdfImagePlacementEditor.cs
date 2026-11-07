using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Parsing;

namespace KillerPdf.Engine.Editing;

/// <summary>Edits a supported image occurrence through a detached incremental revision.</summary>
public static class PdfImagePlacementEditor
{
    /// <summary>Returns revised bytes with one image occurrence moved or resized.</summary>
    public static byte[] SetBounds(PdfDocument document, PdfImagePlacementSelection selection,
        PdfContentBounds bounds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(selection);
        cancellationToken.ThrowIfCancellationRequested();
        if (!ReferenceEquals(document, selection.Owner))
            throw new ArgumentException("The selection belongs to a different document instance.", nameof(selection));
        if (!selection.CanSetBounds)
            throw new NotSupportedException("Image occurrence bounds editing is unsupported: " + selection.BoundsUnsupportedReason);
        if (!double.IsFinite(bounds.Left) || !double.IsFinite(bounds.Bottom)
            || !double.IsFinite(bounds.Right) || !double.IsFinite(bounds.Top)
            || !double.IsFinite(bounds.Width) || !double.IsFinite(bounds.Height)
            || bounds.Width <= 0 || bounds.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(bounds), "Image bounds must be finite with positive dimensions.");
        var matrix = selection.Placement!.Value;
        double sx = bounds.Width / selection.BoundingBox.Width;
        double sy = bounds.Height / selection.BoundingBox.Height;
        double desiredE = sx * matrix.E + bounds.Left - sx * selection.BoundingBox.Left;
        double desiredF = sy * matrix.F + bounds.Bottom - sy * selection.BoundingBox.Bottom;
        double tx = (desiredE - matrix.E) / matrix.A;
        double ty = (desiredF - matrix.F) / matrix.D;
        if (!double.IsFinite(sx) || !double.IsFinite(sy) || sx <= 0 || sy <= 0
            || !double.IsFinite(desiredE) || !double.IsFinite(desiredF)
            || !double.IsFinite(tx) || !double.IsFinite(ty)
            || !double.IsFinite(sx * matrix.A) || !double.IsFinite(sy * matrix.D))
            throw new ArgumentOutOfRangeException(nameof(bounds), "Image bounds produce a nonfinite transformation.");
        if (selection.Ordinal < 0 || selection.Ordinal >= selection.Instructions.Count)
            throw new InvalidOperationException("The selected root instruction is unavailable.");
        var expected = selection.Instructions[selection.Ordinal];
        if (expected.Operator != "Do" || expected.Operands.Count != 1
            || expected.Operands[0] is not PdfName name || name.ValueAsLatin1() != selection.ResourceName)
            throw new InvalidOperationException("The selected root instruction is not the expected image invocation.");
        var tree = PdfPageTree.Read(document);
        var page = tree.Pages[selection.PageIndex];
        if (!page.InheritedValues.TryGetValue(new PdfName("Resources"u8), out var resourceValue)
            || Resolve(resourceValue) is not PdfDictionary resources
            || !resources.TryGetValue(new PdfName("XObject"u8), out var xobjectsValue)
            || Resolve(xobjectsValue) is not PdfDictionary xobjects
            || !xobjects.TryGetValue(name, out var imageValue)
            || !ReferenceEquals(Resolve(imageValue), selection.Resource))
            throw new InvalidOperationException("The selected image resource is no longer available.");
        var instructions = new List<PdfContentInstruction>(selection.Instructions.Count + 3);
        for (int ordinal = 0; ordinal < selection.Instructions.Count; ordinal++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ordinal == selection.Ordinal)
            {
                instructions.Add(new("q", 0, []));
                instructions.Add(new("cm", 0, [new PdfReal(sx), new PdfInteger(0), new PdfInteger(0),
                    new PdfReal(sy), new PdfReal(tx), new PdfReal(ty)]));
                instructions.Add(expected);
                instructions.Add(new("Q", 0, []));
            }
            else instructions.Add(selection.Instructions[ordinal]);
        }
        var editor = new PdfIncrementalPageEditor(document);
        editor.SetPageContent(selection.PageIndex, instructions);
        cancellationToken.ThrowIfCancellationRequested();
        byte[] result = editor.Build();
        cancellationToken.ThrowIfCancellationRequested();
        return result;

        PdfObject Resolve(PdfObject value)
        {
            for (int depth = 0; value is PdfIndirectReference reference; depth++)
            {
                if (depth >= 32) throw new FormatException("Resource reference nesting limit exceeded.");
                value = document.Resolve(reference);
            }
            return value;
        }
    }

    /// <summary>Returns revised bytes without changing the selection's source document.</summary>
    /// <remarks>Requires the original document instance and honors modification, certification and structure restrictions.</remarks>
    public static byte[] Remove(PdfDocument document, PdfImagePlacementSelection selection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(selection);
        cancellationToken.ThrowIfCancellationRequested();
        if (!ReferenceEquals(document, selection.Owner))
            throw new ArgumentException("The selection belongs to a different document instance.", nameof(selection));
        if (!selection.CanRemove)
            throw new NotSupportedException("Image occurrence removal is unsupported: " + selection.UnsupportedReason);
        if (selection.Ordinal < 0 || selection.Ordinal >= selection.Instructions.Count)
            throw new InvalidOperationException("The selected root instruction is unavailable.");
        var expected = selection.Instructions[selection.Ordinal];
        if (expected.Operator != "Do" || expected.Operands.Count != 1
            || expected.Operands[0] is not PdfName name || name.ValueAsLatin1() != selection.ResourceName)
            throw new InvalidOperationException("The selected root instruction is not the expected image invocation.");
        var tree = PdfPageTree.Read(document);
        var page = tree.Pages[selection.PageIndex];
        if (!page.InheritedValues.TryGetValue(new PdfName("Resources"u8), out var resourceValue)
            || Resolve(resourceValue) is not PdfDictionary resources
            || !resources.TryGetValue(new PdfName("XObject"u8), out var xobjectsValue)
            || Resolve(xobjectsValue) is not PdfDictionary xobjects
            || !xobjects.TryGetValue(name, out var imageValue)
            || !ReferenceEquals(Resolve(imageValue), selection.Resource))
            throw new InvalidOperationException("The selected image resource is no longer available.");
        var editor = new PdfIncrementalPageEditor(document);
        editor.SetPageContent(selection.PageIndex,
            selection.Instructions.Where((_, ordinal) => ordinal != selection.Ordinal));
        cancellationToken.ThrowIfCancellationRequested();
        byte[] result = editor.Build();
        cancellationToken.ThrowIfCancellationRequested();
        return result;

        PdfObject Resolve(PdfObject value)
        {
            for (int depth = 0; value is PdfIndirectReference reference; depth++)
            {
                if (depth >= 32) throw new FormatException("Resource reference nesting limit exceeded.");
                value = document.Resolve(reference);
            }
            return value;
        }
    }
}
