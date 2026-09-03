using System.Text;
using KillerPdf.Engine.Objects;

namespace KillerPdf.Engine.Documents;

/// <summary>Describes raster-image characteristics that are useful when rebuilding PDF pages.</summary>
public static class PdfPageRasterInformation
{
    /// <summary>
    /// Identifies pages whose image XObjects are all one-bit DeviceGray images.
    /// A page without image XObjects, or one containing another XObject type, returns false.
    /// </summary>
    public static IReadOnlyList<bool> ReadBitonalImagePageHints(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        PdfPageTree tree = PdfPageTree.Read(document);
        var result = new bool[tree.Pages.Count];
        for (int index = 0; index < tree.Pages.Count; index++)
            result[index] = IsBitonalImagePage(document, tree.Pages[index]);
        return result;
    }

    /// <summary>
    /// Identifies pages whose image XObjects are all JPEG images.
    /// A page without image XObjects, or one containing another XObject type, returns false.
    /// </summary>
    public static IReadOnlyList<bool> ReadJpegImagePageHints(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        PdfPageTree tree = PdfPageTree.Read(document);
        var result = new bool[tree.Pages.Count];
        for (int index = 0; index < tree.Pages.Count; index++)
            result[index] = IsJpegImagePage(document, tree.Pages[index]);
        return result;
    }

    private static bool IsBitonalImagePage(PdfDocument document, PdfPageTreeEntry page)
    {
        if (!page.InheritedValues.TryGetValue(Name("Resources"), out PdfObject? resourcesValue)
            || Resolve(document, resourcesValue) is not PdfDictionary resources
            || !resources.TryGetValue(Name("XObject"), out PdfObject? xObjectsValue)
            || Resolve(document, xObjectsValue) is not PdfDictionary xObjects
            || xObjects.Count == 0)
            return false;

        foreach (PdfObject value in xObjects.Values)
        {
            if (Resolve(document, value) is not PdfStream stream
                || !IsName(document, stream.Dictionary, "Subtype", "Image")
                || !IsInteger(document, stream.Dictionary, "BitsPerComponent", 1)
                || !IsName(document, stream.Dictionary, "ColorSpace", "DeviceGray"))
                return false;
        }
        return true;
    }

    private static bool IsJpegImagePage(PdfDocument document, PdfPageTreeEntry page)
    {
        if (!page.InheritedValues.TryGetValue(Name("Resources"), out PdfObject? resourcesValue)
            || Resolve(document, resourcesValue) is not PdfDictionary resources
            || !resources.TryGetValue(Name("XObject"), out PdfObject? xObjectsValue)
            || Resolve(document, xObjectsValue) is not PdfDictionary xObjects
            || xObjects.Count == 0)
            return false;

        foreach (PdfObject value in xObjects.Values)
        {
            if (Resolve(document, value) is not PdfStream stream
                || !IsName(document, stream.Dictionary, "Subtype", "Image")
                || !HasFilter(document, stream.Dictionary, "DCTDecode"))
                return false;
        }
        return true;
    }

    private static bool HasFilter(
        PdfDocument document, PdfDictionary dictionary, string expected)
    {
        if (!dictionary.TryGetValue(Name("Filter"), out PdfObject? value))
            return false;
        PdfObject resolved = Resolve(document, value);
        if (resolved is PdfName name)
            return name.ValueAsLatin1() == expected;
        return resolved is PdfArray array && array.Any(item =>
            Resolve(document, item) is PdfName filter
            && filter.ValueAsLatin1() == expected);
    }

    private static bool IsName(
        PdfDocument document, PdfDictionary dictionary, string key, string expected) =>
        dictionary.TryGetValue(Name(key), out PdfObject? value)
        && Resolve(document, value) is PdfName name
        && name.ValueAsLatin1() == expected;

    private static bool IsInteger(
        PdfDocument document, PdfDictionary dictionary, string key, long expected) =>
        dictionary.TryGetValue(Name(key), out PdfObject? value)
        && Resolve(document, value) is PdfInteger integer
        && integer.Value == expected;

    private static PdfObject Resolve(PdfDocument document, PdfObject value)
    {
        var visited = new HashSet<(int, int)>();
        while (value is PdfIndirectReference reference)
        {
            if (!visited.Add((reference.ObjectNumber, reference.Generation)) || visited.Count > 32)
                return PdfNull.Instance;
            value = document.Resolve(reference);
        }
        return value;
    }

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
}
