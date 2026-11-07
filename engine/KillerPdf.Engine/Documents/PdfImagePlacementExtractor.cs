using System.Globalization;
using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Security;
using KillerPdf.Engine.Writing;

namespace KillerPdf.Engine.Documents;

/// <summary>Detaches supported image resources without rasterizing their owning page.</summary>
public static class PdfImagePlacementExtractor
{
    private const int MaximumAssetBytes = 5 * 1024 * 1024;

    /// <summary>Copies one original image resource from its document-bound selection.</summary>
    public static PdfImageAsset Extract(PdfDocument document, PdfImagePlacementSelection selection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(selection);
        cancellationToken.ThrowIfCancellationRequested();
        if (!ReferenceEquals(document, selection.Owner))
            throw new ArgumentException("The selection belongs to a different document instance.", nameof(selection));
        if (document.PasswordAuthenticationRole == PdfPasswordAuthenticationRole.User
            && (document.DeclaredPermissions is not PdfDocumentPermissions permissions || !permissions.AllowContentCopying))
            throw new InvalidOperationException("The PDF user password does not permit copying image content.");
        if (!selection.CanSetBounds || selection.Resource is not { } image)
            throw new NotSupportedException("Image resource extraction requires a complete simple image occurrence.");
        var page = PdfPageTree.Read(document).Pages[selection.PageIndex];
        if (!page.InheritedValues.TryGetValue(Name("Resources"), out var resourceValue)
            || Resolve(resourceValue) is not PdfDictionary resources
            || !resources.TryGetValue(Name("XObject"), out var objectsValue)
            || Resolve(objectsValue) is not PdfDictionary objects
            || selection.ResourceName is not { } resourceName
            || !objects.TryGetValue(Name(resourceName), out var selected)
            || !ReferenceEquals(Resolve(selected), image))
            throw new InvalidOperationException("The selected image resource is no longer available.");
        if (resources.TryGetValue(Name("ColorSpace"), out var spacesValue)
            && (Resolve(spacesValue) is not PdfDictionary spaces || spaces.ContainsKey(Name("DefaultRGB"))))
            throw new NotSupportedException("Image extraction does not yet preserve default color-space substitutions.");
        var dictionary = image.Dictionary;
        foreach (string key in new[] { "Filter", "DecodeParms", "Decode", "Mask", "SMask", "ImageMask", "SMaskInData", "OC", "Alternates", "OPI", "Intent" })
            if (dictionary.ContainsKey(Name(key)))
                throw new NotSupportedException("Image extraction does not yet preserve image property: " + key + ".");
        if (!dictionary.TryGetValue(Name("ColorSpace"), out var colorValue)
            || Resolve(colorValue) is not PdfName color || color.ValueAsLatin1() != "DeviceRGB")
            throw new NotSupportedException("Image extraction currently requires untagged DeviceRGB samples.");
        int width = Integer("Width"), height = Integer("Height");
        if (Integer("BitsPerComponent") != 8)
            throw new NotSupportedException("Image extraction currently requires eight-bit samples.");
        long pixels = (long)width * height;
        if (pixels > MaximumAssetBytes / 3)
            throw new NotSupportedException("The image exceeds the detached asset size limit.");
        if (image.EncodedData.Length != pixels * 3)
            throw new FormatException("The unfiltered image payload does not match its dimensions.");
        var entries = new Dictionary<PdfName, PdfObject>
        {
            [Name("Type")] = Name("XObject"), [Name("Subtype")] = Name("Image"),
            [Name("Width")] = new PdfInteger(width), [Name("Height")] = new PdfInteger(height),
            [Name("ColorSpace")] = Name("DeviceRGB"), [Name("BitsPerComponent")] = new PdfInteger(8)
        };
        if (dictionary.TryGetValue(Name("Interpolate"), out var interpolate))
        {
            if (Resolve(interpolate) is not PdfBoolean flag)
                throw new FormatException("The image interpolation flag is not a boolean.");
            entries.Add(Name("Interpolate"), flag);
        }
        cancellationToken.ThrowIfCancellationRequested();
        var seed = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage(width, height).Build());
        var seedPage = PdfPageTree.Read(seed).Pages[0];
        var update = new PdfIncrementalUpdateBuilder(seed);
        var imageReference = update.AddObject(new PdfStream(new PdfDictionary(entries), image.EncodedData.Span));
        var content = string.Create(CultureInfo.InvariantCulture, $"q {width} 0 0 {height} 0 0 cm /Image Do Q\n");
        var contentReference = update.AddObject(new PdfStream(new PdfDictionary([]), Encoding.ASCII.GetBytes(content)));
        var pageEntries = seedPage.Dictionary.ToDictionary(pair => pair.Key, pair => pair.Value);
        pageEntries[Name("Resources")] = new PdfDictionary([
            new(Name("XObject"), new PdfDictionary([new(Name("Image"), imageReference)]))]);
        pageEntries[Name("Contents")] = contentReference;
        update.ReplaceObject(seedPage.Reference.ObjectNumber, new PdfDictionary(pageEntries));
        cancellationToken.ThrowIfCancellationRequested();
        byte[] result = update.Build();
        cancellationToken.ThrowIfCancellationRequested();
        if (result.Length > MaximumAssetBytes)
            throw new NotSupportedException("The detached image asset exceeds its size limit.");
        return new PdfImageAsset(width, height, result);

        int Integer(string key)
        {
            if (!dictionary.TryGetValue(Name(key), out var value)
                || Resolve(value) is not PdfInteger number || number.Value <= 0 || number.Value > int.MaxValue)
                throw new FormatException("The image dimension or sample depth is invalid: " + key + ".");
            return (int)number.Value;
        }

        PdfObject Resolve(PdfObject value)
        {
            for (int depth = 0; value is PdfIndirectReference reference; depth++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (depth >= 32) throw new FormatException("Image resource reference nesting limit exceeded.");
                value = document.Resolve(reference);
            }
            return value;
        }
    }

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
}
