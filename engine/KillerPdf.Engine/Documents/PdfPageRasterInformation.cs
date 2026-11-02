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
    public static IReadOnlyList<bool> ReadBitonalImagePageHints(PdfDocument document) =>
        ReadBitonalImagePageHints(document, CancellationToken.None);

    /// <summary>The bitonal hints of <see cref="ReadBitonalImagePageHints(PdfDocument)"/>. A cancel
    /// stops the walk between pages with <see cref="OperationCanceledException"/>.</summary>
    public static IReadOnlyList<bool> ReadBitonalImagePageHints(PdfDocument document, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        PdfPageTree tree = PdfPageTree.Read(document);
        var result = new bool[tree.Pages.Count];
        for (int index = 0; index < tree.Pages.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result[index] = IsBitonalImagePage(document, tree.Pages[index]);
        }
        return result;
    }

    /// <summary>
    /// Identifies pages whose image XObjects are all JPEG images.
    /// A page without image XObjects, or one containing another XObject type, returns false.
    /// </summary>
    public static IReadOnlyList<bool> ReadJpegImagePageHints(PdfDocument document) =>
        ReadJpegImagePageHints(document, CancellationToken.None);

    /// <summary>The JPEG hints of <see cref="ReadJpegImagePageHints(PdfDocument)"/>. A cancel stops the
    /// walk between pages with <see cref="OperationCanceledException"/>.</summary>
    public static IReadOnlyList<bool> ReadJpegImagePageHints(PdfDocument document, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        PdfPageTree tree = PdfPageTree.Read(document);
        var result = new bool[tree.Pages.Count];
        for (int index = 0; index < tree.Pages.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result[index] = IsJpegImagePage(document, tree.Pages[index]);
        }
        return result;
    }

    /// <summary>
    /// Reads each page's size in pixels at the resolution of its largest image, turned the way the
    /// page is shown, which for a scan is the resolution it was scanned at. A page with no image,
    /// or one that cannot be read, returns null.
    /// </summary>
    public static IReadOnlyList<(int Width, int Height)?> ReadScanSizes(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var reader = new PdfPageContentReader(document);
        IReadOnlyList<PdfPageInformation> pages = PdfPageInformation.Read(document);
        var result = new (int Width, int Height)?[pages.Count];
        for (int index = 0; index < result.Length; index++) result[index] = ScanSize(reader, pages[index], index, 0);
        return result;
    }

    /// <summary>
    /// Reads a page's size in pixels when one image covers it, as a scan or an imported picture does,
    /// turned the way the page is shown. A page that is not mostly one image returns null, since
    /// the resolution of a logo says nothing about the page around it.
    /// </summary>
    public static (int Width, int Height)? ReadPageImageSize(PdfDocument document, int pageIndex)
    {
        ArgumentNullException.ThrowIfNull(document);
        return ReadPageImageSize(new PdfPageContentReader(document), PdfPageInformation.Read(document), pageIndex);
    }

    /// <summary>
    /// Reads a page's size in pixels as <see cref="ReadPageImageSize(PdfDocument, int)"/> does, from a
    /// content reader and page information the caller made once for every page, so a loop over the
    /// pages does not read the page tree again for each.
    /// </summary>
    public static (int Width, int Height)? ReadPageImageSize(
        PdfPageContentReader reader, IReadOnlyList<PdfPageInformation> pages, int pageIndex)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pageIndex, pages.Count);
        return ScanSize(reader, pages[pageIndex], pageIndex, 0.9);
    }

    private static (int Width, int Height)? ScanSize(PdfPageContentReader reader, PdfPageInformation page, int index, double minimumCoverage)
    {
        PdfExtractedImage? image;
        try { image = reader.Read(index).Images.MaxBy(item => (long)item.PixelWidth * item.PixelHeight); }
        catch (Exception exception) when (PdfReaderDamage.Is(exception)) { return null; }
        if (image is null) return null;
        double drawn = Math.Max(image.BoundingBox.Width, image.BoundingBox.Height);
        if (drawn <= 0 || image.PixelWidth <= 0 || image.PixelHeight <= 0) return null;
        if (image.BoundingBox.Width * image.BoundingBox.Height < minimumCoverage * page.Width * page.Height) return null;
        double perPoint = Math.Max(image.PixelWidth, image.PixelHeight) / drawn;
        bool quarterTurn = page.Rotation is 90 or 270;
        return ((int)Math.Round((quarterTurn ? page.Height : page.Width) * perPoint),
            (int)Math.Round((quarterTurn ? page.Width : page.Height) * perPoint));
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
        if (page.Dictionary.ContainsKey(Name("Annots"))
            || page.InheritedValues.TryGetValue(Name("Rotate"), out PdfObject? rotateValue)
            && Resolve(document, rotateValue) is PdfInteger rotate
            && rotate.Value % 360 != 0)
            return false;

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
            || placement.Placement is not { } matrix
            || Math.Abs(matrix.A - content.Width) > 0.01
            || Math.Abs(matrix.D - content.Height) > 0.01
            || Math.Abs(matrix.B) > 0.01 || Math.Abs(matrix.C) > 0.01
            || Math.Abs(matrix.E) > 0.01 || Math.Abs(matrix.F) > 0.01
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
