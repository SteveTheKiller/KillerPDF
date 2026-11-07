using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Syntax;

namespace KillerPdf.Engine.Authoring;

// This accepts only extractor-created closed assets, never caller-supplied PDF bytes.
internal sealed class PdfImageAssetGraphWriter
{
    private const int MaximumBytes = 5 * 1024 * 1024;
    private readonly Dictionary<PdfImageAsset, PdfStream> _images = [];
    private readonly Dictionary<PdfImageAsset, PdfIndirectReference> _references = [];

    internal PdfVersion MinimumVersion(PdfImageAsset asset, CancellationToken cancellationToken = default) =>
        ReadImage(asset, cancellationToken).Dictionary.ContainsKey(Name("Filter"))
            ? new PdfVersion(1, 2) : new PdfVersion(1, 0);

    internal PdfIndirectReference Write(PdfImageAsset asset,
        Func<PdfIndirectReference> reserve, Action<PdfIndirectReference, PdfObject> define,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_references.TryGetValue(asset, out var known)) return known;
        var image = ReadImage(asset, cancellationToken);
        var reference = reserve();
        cancellationToken.ThrowIfCancellationRequested();
        define(reference, image);
        _references.Add(asset, reference);
        return reference;
    }

    private PdfStream ReadImage(PdfImageAsset asset, CancellationToken cancellationToken)
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
                or "BitsPerComponent" or "Length" or "Filter" or "Interpolate"))
                throw new NotSupportedException("Detached image authoring does not support image property: " + key + ".");
            // The extractor normalizes all accepted metadata to direct immutable scalars.
            if (entry.Value is not (PdfName or PdfInteger or PdfBoolean))
                throw new NotSupportedException("Detached image authoring requires direct scalar image metadata.");
        }
        bool IsName(string key, string expected) => dictionary.TryGetValue(Name(key), out var value)
            && value is PdfName name && name.ValueAsLatin1() == expected;
        bool IsInteger(string key, long expected) => dictionary.TryGetValue(Name(key), out var value)
            && value is PdfInteger integer && integer.Value == expected;
        if (!IsName("Type", "XObject") || !IsName("Subtype", "Image")
            || !IsName("ColorSpace", "DeviceRGB") || !IsInteger("BitsPerComponent", 8)
            || !IsInteger("Width", asset.Width) || !IsInteger("Height", asset.Height)
            || asset.Width <= 0 || asset.Height <= 0
            || (long)asset.Width * asset.Height > MaximumBytes / 3
            || image.EncodedData.Length > MaximumBytes)
            throw new NotSupportedException("Detached image authoring requires a bounded eight-bit DeviceRGB image.");
        if (dictionary.ContainsKey(Name("Filter")) && !IsName("Filter", "FlateDecode"))
            throw new NotSupportedException("Detached image authoring supports only raw or single FlateDecode image data.");
        if (dictionary.TryGetValue(Name("Interpolate"), out var interpolate) && interpolate is not PdfBoolean)
            throw new NotSupportedException("Detached image interpolation must be a boolean.");
        if (!dictionary.ContainsKey(Name("Filter"))
            && image.EncodedData.Length != (long)asset.Width * asset.Height * 3)
            throw new FormatException("The detached image payload does not match its dimensions.");
        cancellationToken.ThrowIfCancellationRequested();
        var copy = new PdfStream(new PdfDictionary(dictionary), image.EncodedData.Span);
        _images.Add(asset, copy);
        return copy;
    }

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
}
