using System.Text;
using KillerPdf.Engine.Objects;

namespace KillerPdf.Engine.Documents;

/// <summary>A process or named spot colorant found while inspecting page content and resources.</summary>
public sealed record PdfSeparationColorant(
    string Name, bool IsProcess, IReadOnlyList<int> PageIndexes);

/// <summary>An inspectable inventory for preparing a color-separation preview.</summary>
public sealed class PdfSeparationReport
{
    internal PdfSeparationReport(IEnumerable<PdfSeparationColorant> colorants)
        => Colorants = Array.AsReadOnly(colorants.ToArray());

    /// <summary>Gets process and spot colorants in deterministic order.</summary>
    public IReadOnlyList<PdfSeparationColorant> Colorants { get; }
}

/// <summary>Inspects process-CMYK use and declared Separation or DeviceN colorants.</summary>
public static class PdfSeparationInspection
{
    /// <summary>Reads a document without rendering or changing it.</summary>
    public static PdfSeparationReport Inspect(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!document.IsDecrypted)
            throw new InvalidOperationException(
                "Authenticate the document before inspecting color separations.");

        var pagesByColorant = new Dictionary<(string Name, bool Process), HashSet<int>>();
        PdfPageTree tree = PdfPageTree.Read(document);
        var contentReader = new PdfPageContentReader(document);
        foreach (PdfPageTreeEntry page in tree.Pages)
        {
            if (contentReader.Read(page.Index).Instructions.Any(instruction =>
                    instruction.Operator is "k" or "K"))
            {
                foreach (string process in new[] { "Cyan", "Magenta", "Yellow", "Black" })
                    Add(process, process: true, page.Index);
            }
            if (page.InheritedValues.TryGetValue(Name("Resources"), out PdfObject? resources))
                InspectResources(resources, page.Index, new HashSet<(int, int)>());
        }

        return new PdfSeparationReport(pagesByColorant
            .OrderBy(item => item.Key.Process ? 0 : 1)
            .ThenBy(item => item.Key.Name, StringComparer.Ordinal)
            .Select(item => new PdfSeparationColorant(item.Key.Name, item.Key.Process,
                Array.AsReadOnly(item.Value.Order().ToArray()))));

        void Add(string name, bool process, int pageIndex)
        {
            var key = (name, process);
            if (!pagesByColorant.TryGetValue(key, out HashSet<int>? pages))
                pagesByColorant.Add(key, pages = []);
            pages.Add(pageIndex);
        }

        void InspectResources(PdfObject value, int pageIndex, HashSet<(int, int)> visited)
        {
            PdfDictionary resources = Resolve(value) as PdfDictionary
                ?? throw new FormatException("A page or form resource value is not a dictionary.");
            if (resources.TryGetValue(Name("ColorSpace"), out PdfObject? spacesValue))
            {
                PdfDictionary spaces = Resolve(spacesValue) as PdfDictionary
                    ?? throw new FormatException("A color-space resource value is not a dictionary.");
                foreach (KeyValuePair<PdfName, PdfObject> entry in spaces)
                    InspectColorSpace(entry.Value, pageIndex);
            }
            if (!resources.TryGetValue(Name("XObject"), out PdfObject? xObjectsValue)) return;
            PdfDictionary xObjects = Resolve(xObjectsValue) as PdfDictionary
                ?? throw new FormatException("An XObject resource value is not a dictionary.");
            foreach (PdfObject xObjectValue in xObjects.Select(item => item.Value))
            {
                if (xObjectValue is PdfIndirectReference reference
                    && !visited.Add((reference.ObjectNumber, reference.Generation)))
                    continue;
                if (Resolve(xObjectValue) is PdfStream stream
                    && IsName(stream.Dictionary, "Subtype", "Form")
                    && stream.Dictionary.TryGetValue(Name("Resources"), out PdfObject? nested))
                    InspectResources(nested, pageIndex, visited);
            }
        }

        void InspectColorSpace(PdfObject value, int pageIndex)
        {
            if (Resolve(value) is not PdfArray colorSpace || colorSpace.Count < 2
                || Resolve(colorSpace[0]) is not PdfName family) return;
            if (family.ValueAsLatin1() == "Separation"
                && Resolve(colorSpace[1]) is PdfName spot)
                Add(spot.ValueAsLatin1(), process: false, pageIndex);
            else if (family.ValueAsLatin1() == "DeviceN"
                && Resolve(colorSpace[1]) is PdfArray names)
                foreach (PdfObject item in names)
                    if (Resolve(item) is PdfName colorant)
                        Add(colorant.ValueAsLatin1(), process: false, pageIndex);
        }

        PdfObject Resolve(PdfObject value)
        {
            var visited = new HashSet<(int, int)>();
            while (value is PdfIndirectReference reference)
            {
                if (!visited.Add((reference.ObjectNumber, reference.Generation)))
                    throw new FormatException("A separation inspection reference contains a cycle.");
                value = document.Resolve(reference);
            }
            return value;
        }
    }

    private static bool IsName(PdfDictionary dictionary, string key, string expected) =>
        dictionary.TryGetValue(Name(key), out PdfObject? value)
        && value is PdfName name && name.ValueAsLatin1() == expected;

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
}
