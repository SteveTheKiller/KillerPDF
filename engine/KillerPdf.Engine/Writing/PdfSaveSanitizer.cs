using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;

namespace KillerPdf.Engine.Writing;

/// <summary>Repairs harmless structural artifacts before application-authored output is finalized.</summary>
public static class PdfSaveSanitizer
{
    private static readonly PdfName OutlinesName = Name("Outlines");
    private static readonly PdfName FirstName = Name("First");
    private static readonly PdfName CropBoxName = Name("CropBox");
    private static readonly PdfName MediaBoxName = Name("MediaBox");

    /// <summary>
    /// Removes an empty or dangling outline root and direct crop boxes that are degenerate or
    /// outside the effective media box. Returns the original bytes when no repair is required.
    /// </summary>
    public static byte[] RepairHarmlessArtifacts(PdfDocument document)
    {
        return CreateRepairPlan(document).Apply();
    }

    /// <summary>Inspects harmless structural artifacts without changing the document.</summary>
    public static PdfSaveRepairPlan CreateRepairPlan(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        PdfPageTree tree = PdfPageTree.Read(document);
        var changes = new List<PdfSaveRepairChange>();

        if (tree.Catalog.TryGetValue(OutlinesName, out PdfObject? outlinesValue))
        {
            PdfObject outlines = Resolve(document, outlinesValue);
            if (outlines is not PdfDictionary dictionary || !dictionary.ContainsKey(FirstName))
            {
                changes.Add(new PdfSaveRepairChange(PdfSaveRepairKind.RemoveDanglingOutlines,
                    tree.CatalogReference.ObjectNumber, null,
                    "Remove the empty or dangling document outline root."));
            }
        }

        foreach (PdfPageTreeEntry page in tree.Pages)
        {
            if (!page.Dictionary.TryGetValue(CropBoxName, out PdfObject? cropValue)
                || !TryBox(document, cropValue, out Box crop)) continue;
            if (!page.InheritedValues.TryGetValue(MediaBoxName, out PdfObject? mediaValue)
                || !TryBox(document, mediaValue, out Box media)) continue;
            bool invalid = crop.Width < 1 || crop.Height < 1
                || crop.Left < media.Left - .01 || crop.Bottom < media.Bottom - .01
                || crop.Right > media.Right + .01 || crop.Top > media.Top + .01;
            if (!invalid) continue;
            changes.Add(new PdfSaveRepairChange(PdfSaveRepairKind.RemoveInvalidCropBox,
                page.Reference.ObjectNumber, page.Index,
                "Remove a degenerate crop box or one outside the effective media box."));
        }

        return new PdfSaveRepairPlan(document, changes);
    }

    internal static byte[] ApplyPlan(PdfDocument document, IReadOnlyList<PdfSaveRepairChange> changes)
    {
        if (changes.Count == 0) return document.Source.ToArray();
        var update = new PdfIncrementalUpdateBuilder(document);
        foreach (PdfSaveRepairChange change in changes)
        {
            PdfDictionary dictionary = Resolve(document,
                new PdfIndirectReference(change.ObjectNumber, 0)) as PdfDictionary
                ?? throw new InvalidOperationException("A planned repair object is no longer a dictionary.");
            PdfName removedName = change.Kind switch
            {
                PdfSaveRepairKind.RemoveDanglingOutlines => OutlinesName,
                PdfSaveRepairKind.RemoveInvalidCropBox => CropBoxName,
                _ => throw new InvalidOperationException("The repair kind is unsupported.")
            };
            update.ReplaceObject(change.ObjectNumber,
                new PdfDictionary(dictionary.Where(entry => !entry.Key.Equals(removedName))));
        }
        return update.Build();
    }

    private static bool TryBox(PdfDocument document, PdfObject value, out Box box)
    {
        value = Resolve(document, value);
        if (value is not PdfArray { Count: 4 } array
            || !TryNumber(document, array[0], out double x1)
            || !TryNumber(document, array[1], out double y1)
            || !TryNumber(document, array[2], out double x2)
            || !TryNumber(document, array[3], out double y2))
        { box = default; return false; }
        box = new Box(Math.Min(x1, x2), Math.Min(y1, y2),
            Math.Max(x1, x2), Math.Max(y1, y2));
        return true;
    }

    private static bool TryNumber(PdfDocument document, PdfObject value, out double number)
    {
        value = Resolve(document, value);
        if (value is PdfInteger integer) { number = integer.Value; return true; }
        if (value is PdfReal real && double.IsFinite(real.Value)) { number = real.Value; return true; }
        number = 0;
        return false;
    }

    private static PdfObject Resolve(PdfDocument document, PdfObject value)
    {
        var visited = new HashSet<(int, int)>();
        for (int depth = 0; value is PdfIndirectReference reference; depth++)
        {
            if (depth >= 32 || !visited.Add((reference.ObjectNumber, reference.Generation)))
                return PdfNull.Instance;
            value = document.Resolve(reference);
        }
        return value;
    }

    private static PdfName Name(string value) => new(System.Text.Encoding.ASCII.GetBytes(value));
    private readonly record struct Box(double Left, double Bottom, double Right, double Top)
    {
        internal double Width => Right - Left;
        internal double Height => Top - Bottom;
    }
}

/// <summary>Identifies a nonvisual repair that can be applied before saving.</summary>
public enum PdfSaveRepairKind
{
    /// <summary>Removes an empty or dangling outline root.</summary>
    RemoveDanglingOutlines,
    /// <summary>Removes a direct crop box that cannot describe visible page content.</summary>
    RemoveInvalidCropBox
}

/// <summary>Describes one proposed save repair.</summary>
public sealed record PdfSaveRepairChange(
    PdfSaveRepairKind Kind, int ObjectNumber, int? PageIndex, string Description);

/// <summary>An immutable, inspectable set of harmless repairs for one open document.</summary>
public sealed class PdfSaveRepairPlan
{
    private readonly PdfDocument _document;

    internal PdfSaveRepairPlan(PdfDocument document, IEnumerable<PdfSaveRepairChange> changes)
    {
        _document = document;
        Changes = Array.AsReadOnly(changes.ToArray());
    }

    /// <summary>Gets the source byte count before repairs.</summary>
    public int OriginalSize => _document.Source.Length;
    /// <summary>Gets the repairs in deterministic application order.</summary>
    public IReadOnlyList<PdfSaveRepairChange> Changes { get; }
    /// <summary>Gets whether the plan will change the document.</summary>
    public bool HasChanges => Changes.Count > 0;
    /// <summary>Applies exactly the reported repairs.</summary>
    public byte[] Apply() => PdfSaveSanitizer.ApplyPlan(_document, Changes);
}
