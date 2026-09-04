using System.Text;
using KillerPdf.Engine.Authoring;
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

    /// <summary>
    /// Reads a direct, unmasked JPEG when it is the page's only painted content and covers the
    /// complete crop box. Returns false for annotations, nested forms, clipping, or extra content.
    /// </summary>
    public static bool TryReadFullPageJpeg(
        PdfDocument document, int pageIndex, out PdfImage? image)
    {
        ArgumentNullException.ThrowIfNull(document);
        PdfPageTree tree = PdfPageTree.Read(document);
        if (pageIndex < 0 || pageIndex >= tree.Pages.Count)
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        image = null;
        PdfPageTreeEntry page = tree.Pages[pageIndex];
        if (page.Dictionary.ContainsKey(Name("Annots"))) return false;

        PdfPageContent content;
        try { content = new PdfPageContentReader(document).Read(pageIndex); }
        catch (Exception exception) when (exception is FormatException or NotSupportedException)
        {
            return false;
        }
        if (content.Diagnostics.Count != 0 || content.Images.Count != 1
            || content.Letters.Count != 0 || content.Paths.Count != 0
            || content.Shadings.Count != 0)
            return false;
        PdfExtractedImage placement = content.Images[0];
        if (placement.IsInline || string.IsNullOrEmpty(placement.ResourceName)
            || !CoversPage(placement.BoundingBox, content.Width, content.Height)
            || content.Instructions.Any(instruction =>
                instruction.Operator is not ("q" or "Q" or "cm" or "Do"))
            || content.Instructions.Count(instruction => instruction.Operator == "Do") != 1)
            return false;

        if (!page.InheritedValues.TryGetValue(Name("Resources"), out PdfObject? resourcesValue)
            || Resolve(document, resourcesValue) is not PdfDictionary resources
            || !resources.TryGetValue(Name("XObject"), out PdfObject? xObjectsValue)
            || Resolve(document, xObjectsValue) is not PdfDictionary xObjects
            || !xObjects.TryGetValue(Name(placement.ResourceName), out PdfObject? imageValue)
            || Resolve(document, imageValue) is not PdfStream stream
            || !IsName(document, stream.Dictionary, "Subtype", "Image")
            || !HasJpegFilter(document, stream.Dictionary)
            || !IsInteger(document, stream.Dictionary, "BitsPerComponent", 8)
            || !IsDeviceColorSpace(document, stream.Dictionary)
            || stream.Dictionary.ContainsKey(Name("Mask"))
            || stream.Dictionary.ContainsKey(Name("SMask"))
            || stream.Dictionary.ContainsKey(Name("Decode")))
            return false;

        PdfImage candidate;
        try { candidate = PdfImage.FromJpeg(stream.EncodedData); }
        catch (Exception exception) when (exception is FormatException or NotSupportedException)
        {
            return false;
        }
        if (candidate.Width != placement.PixelWidth || candidate.Height != placement.PixelHeight)
            return false;
        image = candidate;
        return true;
    }

    private static bool CoversPage(PdfContentBounds bounds, double width, double height)
    {
        const double tolerance = 0.01;
        return Math.Abs(bounds.Left) <= tolerance
            && Math.Abs(bounds.Bottom) <= tolerance
            && Math.Abs(bounds.Right - width) <= tolerance
            && Math.Abs(bounds.Top - height) <= tolerance;
    }

    private static bool IsDeviceColorSpace(
        PdfDocument document, PdfDictionary dictionary)
    {
        if (!dictionary.TryGetValue(Name("ColorSpace"), out PdfObject? value)
            || Resolve(document, value) is not PdfName name)
            return false;
        return name.ValueAsLatin1() is "DeviceGray" or "DeviceRGB" or "DeviceCMYK";
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
            || Resolve(document, resourcesValue) is not PdfDictionary resources)
            return false;

        bool foundImage = false;
        return ContainsOnlyJpegImages(
            document, resources, [], ref foundImage) && foundImage;
    }

    private static bool ContainsOnlyJpegImages(
        PdfDocument document, PdfDictionary resources,
        HashSet<(int ObjectNumber, int Generation)> visited,
        ref bool foundImage)
    {
        if (!resources.TryGetValue(Name("XObject"), out PdfObject? xObjectsValue)
            || Resolve(document, xObjectsValue) is not PdfDictionary xObjects)
            return true;

        foreach (PdfObject value in xObjects.Values)
        {
            if (value is PdfIndirectReference reference
                && !visited.Add((reference.ObjectNumber, reference.Generation)))
                continue;
            if (Resolve(document, value) is not PdfStream stream)
                return false;
            if (IsName(document, stream.Dictionary, "Subtype", "Image"))
            {
                foundImage = true;
                if (!HasJpegFilter(document, stream.Dictionary))
                    return false;
                continue;
            }
            if (!IsName(document, stream.Dictionary, "Subtype", "Form"))
                return false;
            if (stream.Dictionary.TryGetValue(Name("Resources"), out PdfObject? nestedValue)
                && Resolve(document, nestedValue) is PdfDictionary nestedResources
                && !ContainsOnlyJpegImages(
                    document, nestedResources, visited, ref foundImage))
                return false;
        }
        return true;
    }

    private static bool HasJpegFilter(
        PdfDocument document, PdfDictionary dictionary)
    {
        if (!dictionary.TryGetValue(Name("Filter"), out PdfObject? value))
            return false;
        PdfObject resolved = Resolve(document, value);
        if (resolved is PdfName name)
            return name.ValueAsLatin1() is "DCTDecode" or "DCT";
        return resolved is PdfArray array && array.Any(item =>
            Resolve(document, item) is PdfName filter
            && filter.ValueAsLatin1() is "DCTDecode" or "DCT");
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
