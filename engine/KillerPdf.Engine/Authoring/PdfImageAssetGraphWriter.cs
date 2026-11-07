using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Syntax;

namespace KillerPdf.Engine.Authoring;

// This accepts only extractor-created closed assets, never caller-supplied PDF bytes.
internal sealed class PdfImageAssetGraphWriter
{
    private const int MaximumBytes = 5 * 1024 * 1024;
    private sealed record ImageGraph(PdfStream Image, PdfStream? Profile, PdfStream? SoftMask);
    private readonly Dictionary<PdfImageAsset, ImageGraph> _images = [];
    private readonly Dictionary<PdfImageAsset, PdfIndirectReference> _references = [];

    internal PdfVersion MinimumVersion(PdfImageAsset asset, CancellationToken cancellationToken = default)
    {
        var graph = ReadImage(asset, cancellationToken);
        return graph.SoftMask is not null ? new PdfVersion(1, 4) : graph.Profile is not null ? new PdfVersion(1, 3)
            : graph.Image.Dictionary.ContainsKey(Name("Filter")) ? new PdfVersion(1, 2) : new PdfVersion(1, 0);
    }

    internal PdfIndirectReference Write(PdfImageAsset asset,
        Func<PdfIndirectReference> reserve, Action<PdfIndirectReference, PdfObject> define,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_references.TryGetValue(asset, out var known)) return known;
        var graph = ReadImage(asset, cancellationToken);
        var image = graph.Image;
        if (graph.SoftMask is not null)
        {
            var maskReference = reserve();
            define(maskReference, graph.SoftMask);
            var entries = image.Dictionary.ToDictionary(pair => pair.Key, pair => pair.Value);
            entries[Name("SMask")] = maskReference;
            image = new PdfStream(new PdfDictionary(entries), image.EncodedData.Span);
        }
        if (graph.Profile is not null)
        {
            var profileReference = reserve();
            define(profileReference, graph.Profile);
            var entries = image.Dictionary.ToDictionary(pair => pair.Key, pair => pair.Value);
            entries[Name("ColorSpace")] = new PdfArray([Name("ICCBased"), profileReference]);
            image = new PdfStream(new PdfDictionary(entries), image.EncodedData.Span);
        }
        var reference = reserve();
        cancellationToken.ThrowIfCancellationRequested();
        define(reference, image);
        _references.Add(asset, reference);
        return reference;
    }

    private ImageGraph ReadImage(PdfImageAsset asset, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_images.TryGetValue(asset, out var known)) return known;
        var document = asset.OpenClosedDocument(cancellationToken);
        int nodes = 0;
        PdfObject Resolve(PdfObject value)
        {
            for (int depth = 0; ; depth++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (depth >= 32 || ++nodes > 128)
                    throw new NotSupportedException("The detached image graph exceeds its nesting or node limit.");
                if (value is not PdfIndirectReference reference) return value;
                value = document.Resolve(reference);
            }
        }
        var tree = PdfPageTree.Read(document);
        if (tree.Pages.Count != 1
            || Resolve(tree.Pages[0].Dictionary[Name("Resources")]) is not PdfDictionary resources
            || resources.Count != 1
            || Resolve(resources[Name("XObject")]) is not PdfDictionary objects
            || objects.Count != 1
            || !objects.TryGetValue(Name("Image"), out var root)
            || Resolve(root) is not PdfStream image)
            throw new NotSupportedException("The detached asset is not a canonical single-image graph.");
        var dictionary = image.Dictionary;
        foreach (var entry in dictionary)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++nodes > 128)
                throw new NotSupportedException("The detached image graph exceeds its node limit.");
            string key = entry.Key.ValueAsLatin1();
            if (key is not ("Type" or "Subtype" or "Width" or "Height" or "ColorSpace"
                or "BitsPerComponent" or "Length" or "Filter" or "Interpolate" or "Intent" or "SMask"))
                throw new NotSupportedException("Detached image authoring does not support image property: " + key + ".");
            // Only the captured color profile and soft mask may be dependencies.
            if (key is not ("ColorSpace" or "SMask") && entry.Value is not (PdfName or PdfInteger or PdfBoolean))
                throw new NotSupportedException("Detached image authoring requires direct scalar image metadata.");
        }
        bool IsName(string key, string expected) => dictionary.TryGetValue(Name(key), out var value)
            && value is PdfName name && name.ValueAsLatin1() == expected;
        bool IsInteger(string key, long expected) => dictionary.TryGetValue(Name(key), out var value)
            && value is PdfInteger integer && integer.Value == expected;
        PdfStream? profile = null;
        PdfStream? softMask = null;
        if (dictionary.TryGetValue(Name("SMask"), out var maskValue))
        {
            if (maskValue is not PdfIndirectReference
                || Resolve(maskValue) is not PdfStream mask)
                throw new NotSupportedException("Detached soft masks require an owned image graph.");
            foreach (var entry in mask.Dictionary)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++nodes > 128)
                    throw new NotSupportedException("The detached image graph exceeds its node limit.");
                if (entry.Key.ValueAsLatin1() is not ("Type" or "Subtype" or "Width" or "Height"
                    or "ColorSpace" or "BitsPerComponent" or "Length" or "Interpolate" or "Filter")
                    || entry.Value is not (PdfName or PdfInteger or PdfBoolean))
                    throw new NotSupportedException("Detached soft-mask metadata must be scalar image metadata.");
            }
            bool MaskName(string key, string expected) => mask.Dictionary.TryGetValue(Name(key), out var value)
                && value is PdfName name && name.ValueAsLatin1() == expected;
            bool MaskInteger(string key, long expected) => mask.Dictionary.TryGetValue(Name(key), out var value)
                && value is PdfInteger integer && integer.Value == expected;
            if (!MaskName("Subtype", "Image") || !MaskName("ColorSpace", "DeviceGray")
                || mask.Dictionary.ContainsKey(Name("Type")) && !MaskName("Type", "XObject")
                || !MaskInteger("Width", asset.Width) || !MaskInteger("Height", asset.Height)
                || !MaskInteger("BitsPerComponent", 8)
                || mask.Dictionary.TryGetValue(Name("Interpolate"), out var interpolation) && interpolation is not PdfBoolean)
                throw new NotSupportedException("Detached soft masks require matching eight-bit DeviceGray samples.");
            long pixels = (long)asset.Width * asset.Height;
            bool maskFlate = mask.Dictionary.ContainsKey(Name("Filter"));
            if (maskFlate && !MaskName("Filter", "FlateDecode"))
                throw new NotSupportedException("Detached soft masks support only raw or single FlateDecode data.");
            if (!maskFlate && mask.EncodedData.Length != pixels)
                throw new FormatException("The detached soft-mask payload does not match its dimensions.");
            if (pixels > MaximumBytes / 4 || (long)image.EncodedData.Length + mask.EncodedData.Length > MaximumBytes)
                throw new NotSupportedException("The detached image and soft mask exceed their combined size limit.");
            softMask = new PdfStream(new PdfDictionary(mask.Dictionary), mask.EncodedData.Span);
        }
        if (!IsName("ColorSpace", "DeviceRGB"))
        {
            if (!dictionary.TryGetValue(Name("ColorSpace"), out var color)
                || color is not PdfArray { Count: 2 } space
                || space[0] is not PdfName spaceName || spaceName.ValueAsLatin1() != "ICCBased"
                || space[1] is not PdfIndirectReference)
                throw new NotSupportedException("Detached image authoring requires an owned RGB ICC graph.");
            if (!dictionary.TryGetValue(Name("Intent"), out var intent) || intent is not PdfName renderingIntent)
                throw new NotSupportedException("Detached ICC images require their captured rendering intent.");
            profile = PdfImageAssetIccProfile.Copy(document, color, Resolve, renderingIntent, cancellationToken);
        }
        else if (dictionary.ContainsKey(Name("Intent")))
            throw new NotSupportedException("Detached DeviceRGB image rendering intent is not implemented.");
        if (!IsName("Type", "XObject") || !IsName("Subtype", "Image")
            || !IsInteger("BitsPerComponent", 8)
            || !IsInteger("Width", asset.Width) || !IsInteger("Height", asset.Height)
            || asset.Width <= 0 || asset.Height <= 0
            || (long)asset.Width * asset.Height > MaximumBytes / 3
            || image.EncodedData.Length > MaximumBytes)
            throw new NotSupportedException("Detached image authoring requires a bounded eight-bit RGB image.");
        if (dictionary.ContainsKey(Name("Filter")) && !IsName("Filter", "FlateDecode"))
            throw new NotSupportedException("Detached image authoring supports only raw or single FlateDecode image data.");
        if (dictionary.TryGetValue(Name("Interpolate"), out var interpolate) && interpolate is not PdfBoolean)
            throw new NotSupportedException("Detached image interpolation must be a boolean.");
        if (!dictionary.ContainsKey(Name("Filter"))
            && image.EncodedData.Length != (long)asset.Width * asset.Height * 3)
            throw new FormatException("The detached image payload does not match its dimensions.");
        cancellationToken.ThrowIfCancellationRequested();
        var copy = new ImageGraph(new PdfStream(new PdfDictionary(dictionary), image.EncodedData.Span), profile, softMask);
        _images.Add(asset, copy);
        return copy;
    }

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
}
