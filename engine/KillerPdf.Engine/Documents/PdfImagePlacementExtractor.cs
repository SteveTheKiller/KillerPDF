using System.Buffers.Binary;
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
        bool maskedOccurrence = selection.UnsupportedReason == "ImageMask"
            && selection.Resource is { } maskedImage && maskedImage.Dictionary.ContainsKey(Name("SMask"))
            && !maskedImage.Dictionary.ContainsKey(Name("Mask")) && !maskedImage.Dictionary.ContainsKey(Name("ImageMask"))
            && !maskedImage.Dictionary.ContainsKey(Name("OC"))
            && selection.Placement is { } placement
            && double.IsFinite(placement.A) && double.IsFinite(placement.D)
            && double.IsFinite(placement.E) && double.IsFinite(placement.F)
            && placement.B == 0 && placement.C == 0 && placement.A != 0 && placement.D != 0
            && double.IsFinite(placement.E + placement.A) && double.IsFinite(placement.F + placement.D)
            && double.IsFinite(selection.BoundingBox.Width) && double.IsFinite(selection.BoundingBox.Height)
            && selection.BoundingBox.Width > 0 && selection.BoundingBox.Height > 0
            && selection.BoundingBox == new PdfContentBounds(Math.Min(placement.E, placement.E + placement.A),
                Math.Min(placement.F, placement.F + placement.D), Math.Max(placement.E, placement.E + placement.A),
                Math.Max(placement.F, placement.F + placement.D));
        if ((!selection.CanSetBounds && !maskedOccurrence) || selection.Resource is not { } image)
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
        foreach (string key in new[] { "DecodeParms", "Decode", "Mask", "ImageMask", "SMaskInData", "OC", "Alternates", "OPI" })
            if (dictionary.ContainsKey(Name(key)))
                throw new NotSupportedException("Image extraction does not yet preserve image property: " + key + ".");
        bool flate = false, jpeg = false;
        // The encoded bytes the asset keeps. Usually the image's own; for an ASCII text layer, the data under it.
        PdfStream source = image;
        if (dictionary.TryGetValue(Name("Filter"), out var filterValue))
        {
            var filters = Resolve(filterValue) is PdfArray array ? array.Select(Resolve).ToList() : [Resolve(filterValue)];
            // ReportLab and other producers spell the compressed bytes out in ASCII first ([/ASCII85Decode /DCTDecode]).
            // That layer changes no pixel, so it is decoded here and the Flate or JPEG data under it is kept as it is.
            PdfName? text = null;
            if (filters.Count == 2 && filters[0] is PdfName outer
                && outer.ValueAsLatin1() is "ASCII85Decode" or "A85" or "ASCIIHexDecode" or "AHx")
            {
                text = outer;
                filters.RemoveAt(0);
            }
            if (filters.Count != 1 || filters[0] is not PdfName filterName
                || filterName.ValueAsLatin1() is not ("FlateDecode" or "DCTDecode"))
                throw new NotSupportedException("Image extraction supports only single FlateDecode or supported DCTDecode data.");
            jpeg = filterName.ValueAsLatin1() == "DCTDecode";
            flate = !jpeg;
            if (text is not null)
            {
                // ASCII85 spends five characters on four bytes and ASCIIHex two on one, so the text is bounded first.
                if (image.EncodedData.Length > (long)MaximumAssetBytes * 2 + 64)
                    throw new NotSupportedException("The encoded image exceeds the detached asset size limit.");
                byte[] inner = Filters.PdfStreamDecoder.Decode(
                    new PdfStream(new PdfDictionary([new(Name("Filter"), text)]), image.EncodedData.Span), MaximumAssetBytes);
                cancellationToken.ThrowIfCancellationRequested();
                var innerEntries = dictionary.ToDictionary(pair => pair.Key, pair => pair.Value);
                innerEntries[Name("Filter")] = Name(filterName.ValueAsLatin1());
                innerEntries[Name("Length")] = new PdfInteger(inner.Length);
                source = new PdfStream(new PdfDictionary(innerEntries), inner);
            }
        }
        if (jpeg && dictionary.ContainsKey(Name("SMask")))
            throw new NotSupportedException("Detached JPEG soft masks are not implemented.");
        if (!dictionary.TryGetValue(Name("ColorSpace"), out var colorValue))
            throw new NotSupportedException("Image extraction requires an explicit source color space.");
        PdfStream? profile = null;
        PdfName renderingIntent = Name("RelativeColorimetric");
        if (Resolve(colorValue) is not PdfName color || color.ValueAsLatin1() != "DeviceRGB")
        {
            if (jpeg) throw new NotSupportedException("Detached JPEG assets require direct DeviceRGB.");
            if (dictionary.TryGetValue(Name("Intent"), out var intent))
                renderingIntent = Resolve(intent) as PdfName
                    ?? throw new NotSupportedException("Image rendering intent requires one standard name.");
            else renderingIntent = EffectiveIntent();
            profile = PdfImageAssetIccProfile.Copy(document, colorValue, Resolve, renderingIntent, cancellationToken);
        }
        else if (dictionary.ContainsKey(Name("Intent")))
            throw new NotSupportedException("Image extraction does not yet preserve DeviceRGB image rendering intent.");
        int width = Integer("Width"), height = Integer("Height");
        if (Integer("BitsPerComponent") != 8)
            throw new NotSupportedException("Image extraction currently requires eight-bit samples.");
        long pixels = (long)width * height;
        PdfStream? softMask = null;
        if (dictionary.TryGetValue(Name("SMask"), out var maskValue))
        {
            int nodes = 0;
            PdfObject ResolveMask(PdfObject value)
            {
                for (int depth = 0; ; depth++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (depth >= 32 || ++nodes > 128)
                        throw new NotSupportedException("The soft-mask graph exceeds its nesting or node limit.");
                    if (value is not PdfIndirectReference reference) return value;
                    value = document.Resolve(reference);
                }
            }
            if (ResolveMask(maskValue) is not PdfStream mask)
                throw new NotSupportedException("Image extraction requires a DeviceGray soft-mask stream.");
            var maskEntries = new Dictionary<PdfName, PdfObject>();
            bool maskFlate = false;
            foreach (var entry in mask.Dictionary)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++nodes > 128)
                    throw new NotSupportedException("The soft-mask graph exceeds its node limit.");
                string key = entry.Key.ValueAsLatin1();
                if (key is not ("Type" or "Subtype" or "Width" or "Height" or "BitsPerComponent"
                    or "ColorSpace" or "Length" or "Interpolate" or "Filter"))
                    throw new NotSupportedException("Image extraction does not yet preserve soft-mask property: " + key + ".");
                var value = ResolveMask(entry.Value);
                if (key == "Filter")
                {
                    if (value is PdfArray { Count: 1 } filters) value = ResolveMask(filters[0]);
                    if (value is not PdfName filter || filter.ValueAsLatin1() != "FlateDecode")
                        throw new NotSupportedException("Soft-mask extraction supports only a single FlateDecode filter.");
                    value = Name("FlateDecode");
                    maskFlate = true;
                }
                if (value is not (PdfName or PdfInteger or PdfBoolean))
                    throw new NotSupportedException("Soft-mask metadata requires scalar values.");
                maskEntries.Add(entry.Key, value);
            }
            bool MaskName(string key, string expected) => maskEntries.TryGetValue(Name(key), out var value)
                && value is PdfName name && name.ValueAsLatin1() == expected;
            bool MaskInteger(string key, long expected) => maskEntries.TryGetValue(Name(key), out var value)
                && value is PdfInteger integer && integer.Value == expected;
            if (!MaskName("Subtype", "Image") || !MaskName("ColorSpace", "DeviceGray")
                || maskEntries.ContainsKey(Name("Type")) && !MaskName("Type", "XObject")
                || !MaskInteger("Width", width) || !MaskInteger("Height", height) || !MaskInteger("BitsPerComponent", 8)
                || maskEntries.TryGetValue(Name("Interpolate"), out var interpolation) && interpolation is not PdfBoolean)
                throw new NotSupportedException("Image extraction requires a matching eight-bit DeviceGray soft mask.");
            if (pixels > MaximumAssetBytes / 4
                || (long)source.EncodedData.Length + mask.EncodedData.Length > MaximumAssetBytes)
                throw new NotSupportedException("The image and soft mask exceed the detached asset size limit.");
            byte[]? maskSamples = maskFlate
                ? document.DecodeImageSamples(mask, MaximumAssetBytes, (int)pixels) : null;
            cancellationToken.ThrowIfCancellationRequested();
            if ((maskSamples?.Length ?? mask.EncodedData.Length) != pixels)
                throw new FormatException("The soft-mask payload does not match its dimensions.");
            if (maskSamples is not null)
            {
                if (mask.EncodedData.Length < 6)
                    throw new FormatException("The Flate soft-mask payload has no complete zlib trailer.");
                uint first = 1, second = 0;
                for (int index = 0; index < maskSamples.Length; index++)
                {
                    if ((index & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                    first = (first + maskSamples[index]) % 65521;
                    second = (second + first) % 65521;
                }
                if (BinaryPrimitives.ReadUInt32BigEndian(mask.EncodedData.Span[^4..]) != ((second << 16) | first))
                    throw new FormatException("The Flate soft-mask payload has an invalid or incomplete zlib checksum.");
            }
            softMask = new PdfStream(new PdfDictionary(maskEntries), mask.EncodedData.Span);
        }
        if (pixels > MaximumAssetBytes / (softMask is null ? 3 : 4))
            throw new NotSupportedException("The image exceeds the detached asset size limit.");
        if (source.EncodedData.Length > MaximumAssetBytes)
            throw new NotSupportedException("The encoded image exceeds the detached asset size limit.");
        if (jpeg) PdfImageAssetJpegValidator.Validate(source.EncodedData, width, height, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        byte[]? samples = flate
            ? document.DecodeImageSamples(source, MaximumAssetBytes, (int)(pixels * 3)) : null;
        int sampleCount = samples?.Length ?? source.EncodedData.Length;
        cancellationToken.ThrowIfCancellationRequested();
        if (!jpeg && sampleCount != pixels * 3)
            throw new FormatException("The image payload does not match its dimensions.");
        if (samples is not null)
        {
            if (source.EncodedData.Length < 6)
                throw new FormatException("The Flate image payload has no complete zlib trailer.");
            uint first = 1, second = 0;
            for (int index = 0; index < samples.Length; index++)
            {
                if ((index & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                first = (first + samples[index]) % 65521;
                second = (second + first) % 65521;
            }
            if (BinaryPrimitives.ReadUInt32BigEndian(source.EncodedData.Span[^4..]) != ((second << 16) | first))
                throw new FormatException("The Flate image payload has an invalid or incomplete zlib checksum.");
        }
        var entries = new Dictionary<PdfName, PdfObject>
        {
            [Name("Type")] = Name("XObject"), [Name("Subtype")] = Name("Image"),
            [Name("Width")] = new PdfInteger(width), [Name("Height")] = new PdfInteger(height),
            [Name("ColorSpace")] = Name("DeviceRGB"), [Name("BitsPerComponent")] = new PdfInteger(8)
        };
        if (flate) entries.Add(Name("Filter"), Name("FlateDecode"));
        if (jpeg) entries.Add(Name("Filter"), Name("DCTDecode"));
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
        if (softMask is not null) entries[Name("SMask")] = update.AddObject(softMask);
        if (profile is not null)
        {
            entries[Name("ColorSpace")] = new PdfArray([Name("ICCBased"), update.AddObject(profile)]);
            entries[Name("Intent")] = Name(renderingIntent.ValueAsLatin1());
        }
        var imageReference = update.AddObject(new PdfStream(new PdfDictionary(entries), source.EncodedData.Span));
        // The asset page shows the picture the way the source page did, so a copy kept as these bytes stays mirrored or flipped.
        bool mirrored = selection.Placement is { A: < 0 }, flipped = selection.Placement is { D: < 0 };
        var content = string.Create(CultureInfo.InvariantCulture,
            $"q {(mirrored ? -width : width)} 0 0 {(flipped ? -height : height)} {(mirrored ? width : 0)} {(flipped ? height : 0)} cm /Image Do Q\n");
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
        return new PdfImageAsset(width, height, result,
            mirrored, flipped);

        PdfName EffectiveIntent()
        {
            PdfName intent = Name("RelativeColorimetric");
            var stack = new Stack<PdfName>();
            for (int index = 0; index < selection.Ordinal; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var instruction = selection.Instructions[index];
                switch (instruction.Operator)
                {
                    case "q":
                        if (instruction.Operands.Count != 0 || stack.Count >= 256)
                            throw new FormatException("Invalid image rendering-intent graphics state.");
                        stack.Push(intent);
                        break;
                    case "Q":
                        if (instruction.Operands.Count != 0 || !stack.TryPop(out var saved))
                            throw new FormatException("Invalid image rendering-intent graphics state.");
                        intent = saved;
                        break;
                    case "ri":
                        if (instruction.Operands.Count != 1 || instruction.Operands[0] is not PdfName selectedIntent)
                            throw new NotSupportedException("Image rendering intent requires one standard name.");
                        _ = PdfImageAssetIccProfile.IntentIndex(selectedIntent);
                        intent = selectedIntent;
                        break;
                }
            }
            return intent;
        }

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
