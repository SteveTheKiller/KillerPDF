using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;

namespace KillerPdf.Engine.Editing;

/// <summary>Removes a supported image occurrence through a detached incremental revision.</summary>
public static class PdfImagePlacementEditor
{
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
