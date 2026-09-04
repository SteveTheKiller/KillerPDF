using System.Text;
using KillerPdf.Engine.Filters;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Parsing;

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
            foreach (PdfContentInstruction instruction in contentReader.Read(page.Index)
                .Instructions.Where(instruction => instruction.Operator is "k" or "K"))
            {
                if (instruction.Operands.Count != 4)
                    throw new FormatException("A CMYK color instruction does not have four components.");
                string[] processNames = ["Cyan", "Magenta", "Yellow", "Black"];
                for (int component = 0; component < processNames.Length; component++)
                    if (ColorNumber(instruction.Operands[component]) > 0)
                        Add(processNames[component], process: true, page.Index);
            }
            if (page.Dictionary.TryGetValue(Name("Contents"), out PdfObject? content))
            {
                PdfDictionary resources = page.InheritedValues.TryGetValue(
                    Name("Resources"), out PdfObject? resourceValue)
                    ? Resolve(resourceValue) as PdfDictionary
                        ?? throw new FormatException("A page resource value is not a dictionary.")
                    : new PdfDictionary([]);
                InspectSpotUsage(content, resources, page.Index,
                    new HashSet<(int, int)>(), 0);
            }
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

        void InspectSpotUsage(PdfObject content, PdfDictionary resources, int pageIndex,
            HashSet<(int, int)> activeForms, int depth)
        {
            if (depth >= 32) throw new FormatException("Form nesting limit exceeded.");
            byte[] bytes = ContentBytes(content);
            PdfObject? fillColorSpace = null;
            PdfObject? strokeColorSpace = null;
            var stack = new Stack<(PdfObject? Fill, PdfObject? Stroke)>();
            foreach (PdfContentInstruction instruction in PdfContentStreamReader.Read(bytes))
            {
                IReadOnlyList<PdfObject> operands = instruction.Operands;
                switch (instruction.Operator)
                {
                    case "q":
                        stack.Push((fillColorSpace, strokeColorSpace));
                        break;
                    case "Q":
                        if (!stack.TryPop(out var saved))
                            throw new FormatException("Unbalanced graphics state in page content.");
                        fillColorSpace = saved.Fill;
                        strokeColorSpace = saved.Stroke;
                        break;
                    case "cs":
                        fillColorSpace = SelectedColorSpace(operands, resources);
                        break;
                    case "CS":
                        strokeColorSpace = SelectedColorSpace(operands, resources);
                        break;
                    case "sc":
                    case "scn":
                        InspectTint(fillColorSpace, operands, pageIndex);
                        break;
                    case "SC":
                    case "SCN":
                        InspectTint(strokeColorSpace, operands, pageIndex);
                        break;
                    case "Do":
                        if (operands.Count != 1 || operands[0] is not PdfName xObjectName)
                            throw new FormatException("An XObject instruction is malformed.");
                        PdfObject xObjectValue = Resource(resources, "XObject", xObjectName);
                        (int, int)? identity = xObjectValue is PdfIndirectReference reference
                            ? (reference.ObjectNumber, reference.Generation) : null;
                        if (Resolve(xObjectValue) is not PdfStream form
                            || !IsName(form.Dictionary, "Subtype", "Form"))
                            break;
                        if (identity.HasValue && !activeForms.Add(identity.Value))
                            throw new FormatException("A separation inspection form contains a cycle.");
                        try
                        {
                            PdfDictionary formResources = form.Dictionary.TryGetValue(
                                Name("Resources"), out PdfObject? nestedResources)
                                ? Resolve(nestedResources) as PdfDictionary
                                    ?? throw new FormatException(
                                        "A form resource value is not a dictionary.")
                                : resources;
                            InspectSpotUsage(form, formResources, pageIndex, activeForms, depth + 1);
                        }
                        finally
                        {
                            if (identity.HasValue) activeForms.Remove(identity.Value);
                        }
                        break;
                }
            }
        }

        byte[] ContentBytes(PdfObject value)
        {
            using var output = new MemoryStream();
            PdfObject resolved = Resolve(value);
            IEnumerable<PdfObject> items = resolved is PdfArray array ? array : [resolved];
            foreach (PdfObject item in items)
            {
                if (Resolve(item) is PdfNull) continue;
                PdfStream stream = Resolve(item) as PdfStream
                    ?? throw new FormatException("Page or form content is not a stream.");
                byte[] decoded = PdfStreamDecoder.Decode(stream, document.Resolve,
                    PdfContentStreamReader.MaximumSourceBytes);
                if (output.Length + decoded.Length + 1 > PdfContentStreamReader.MaximumSourceBytes)
                    throw new FormatException("Separation inspection content exceeds the size limit.");
                output.Write(decoded);
                output.WriteByte((byte)'\n');
            }
            return output.ToArray();
        }

        PdfObject SelectedColorSpace(IReadOnlyList<PdfObject> operands,
            PdfDictionary resources)
        {
            if (operands.Count != 1 || operands[0] is not PdfName name)
                throw new FormatException("A color-space selection is malformed.");
            return name.ValueAsLatin1() is "DeviceGray" or "DeviceRGB" or "DeviceCMYK"
                ? name : Resource(resources, "ColorSpace", name);
        }

        void InspectTint(PdfObject? value, IReadOnlyList<PdfObject> operands, int pageIndex)
        {
            if (value is null || Resolve(value) is not PdfArray colorSpace || colorSpace.Count < 2
                || Resolve(colorSpace[0]) is not PdfName family) return;
            if (family.ValueAsLatin1() == "Separation"
                && Resolve(colorSpace[1]) is PdfName spot)
            {
                if (operands.Count < 1)
                    throw new FormatException("A Separation tint instruction has no component.");
                string name = spot.ValueAsLatin1();
                if (name != "None" && ColorNumber(operands[0]) > 0)
                    Add(name, process: false, pageIndex);
            }
            else if (family.ValueAsLatin1() == "DeviceN"
                && Resolve(colorSpace[1]) is PdfArray names)
            {
                if (operands.Count < names.Count)
                    throw new FormatException(
                        "A DeviceN tint instruction has too few components.");
                for (int index = 0; index < names.Count; index++)
                    if (Resolve(names[index]) is PdfName colorant
                        && colorant.ValueAsLatin1() != "None"
                        && ColorNumber(operands[index]) > 0)
                        Add(colorant.ValueAsLatin1(), process: false, pageIndex);
            }
        }

        PdfObject Resource(PdfDictionary resources, string category, PdfName name)
        {
            if (!resources.TryGetValue(Name(category), out PdfObject? categoryValue)
                || Resolve(categoryValue) is not PdfDictionary entries
                || !entries.TryGetValue(name, out PdfObject? value))
                throw new FormatException(
                    $"A separation inspection resource /{category}/{name.ValueAsLatin1()} is missing.");
            return value;
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

        static double ColorNumber(PdfObject value) => value switch
        {
            PdfInteger integer => integer.Value,
            PdfReal real => real.Value,
            _ => throw new FormatException("A CMYK color component is not numeric.")
        };
    }

    private static bool IsName(PdfDictionary dictionary, string key, string expected) =>
        dictionary.TryGetValue(Name(key), out PdfObject? value)
        && value is PdfName name && name.ValueAsLatin1() == expected;

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
}
