using System.Buffers.Binary;
using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;

namespace KillerPdf.Engine.Documents;

internal static class PdfImageAssetIccProfile
{
    private const int MaximumBytes = 5 * 1024 * 1024;

    internal static PdfStream Copy(PdfDocument document, PdfObject value,
        Func<PdfObject, PdfObject> resolve, PdfName renderingIntent, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (resolve(value) is not PdfArray { Count: 2 } space
            || resolve(space[0]) is not PdfName name || name.ValueAsLatin1() != "ICCBased"
            || resolve(space[1]) is not PdfStream profile)
            throw new NotSupportedException("Detached image color requires DeviceRGB or a supported RGB ICC profile.");
        foreach (var entry in profile.Dictionary)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Key.ValueAsLatin1() is not ("N" or "Alternate" or "Range" or "Filter" or "Length"))
                throw new NotSupportedException("Detached image ICC metadata is not implemented: " + entry.Key.ValueAsLatin1() + ".");
        }
        if (!profile.Dictionary.TryGetValue(N("N"), out var components)
            || resolve(components) is not PdfInteger { Value: 3 })
            throw new NotSupportedException("Detached images require a three-component RGB ICC profile.");
        if (profile.Dictionary.TryGetValue(N("Alternate"), out var alternate)
            && (resolve(alternate) is not PdfName alternateName || alternateName.ValueAsLatin1() != "DeviceRGB"))
            throw new NotSupportedException("Detached image ICC alternates require DeviceRGB.");
        if (profile.Dictionary.TryGetValue(N("Range"), out var ranges))
        {
            if (resolve(ranges) is not PdfArray { Count: 6 } range)
                throw new NotSupportedException("Detached image ICC ranges require unit RGB components.");
            for (int index = 0; index < range.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                double number = resolve(range[index]) switch { PdfInteger integer => integer.Value,
                    PdfReal real => real.Value, _ => double.NaN };
                if (number != index % 2)
                    throw new NotSupportedException("Detached image ICC ranges require unit RGB components.");
            }
        }
        bool flate = false;
        if (profile.Dictionary.TryGetValue(N("Filter"), out var filter))
        {
            filter = resolve(filter);
            if (filter is PdfArray { Count: 1 } filters) filter = resolve(filters[0]);
            if (filter is not PdfName filterName || filterName.ValueAsLatin1() != "FlateDecode")
                throw new NotSupportedException("Detached image ICC data supports raw or single FlateDecode streams.");
            flate = true;
        }
        if (profile.EncodedData.Length > MaximumBytes)
            throw new NotSupportedException("The encoded image ICC profile exceeds its size limit.");
        byte[] decoded = document.DecodeStream(profile, MaximumBytes);
        cancellationToken.ThrowIfCancellationRequested();
        if (flate)
        {
            if (profile.EncodedData.Length < 6)
                throw new FormatException("The image ICC profile has no complete zlib trailer.");
            uint first = 1, second = 0;
            for (int index = 0; index < decoded.Length; index++)
            {
                if ((index & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                first = (first + decoded[index]) % 65521;
                second = (second + first) % 65521;
            }
            if (BinaryPrimitives.ReadUInt32BigEndian(profile.EncodedData.Span[^4..]) != ((second << 16) | first))
                throw new FormatException("The image ICC profile has an invalid or incomplete zlib checksum.");
        }
        var validated = PdfIccProfile.Load(decoded);
        if (validated.ColorSpace != "RGB")
            throw new NotSupportedException("Detached images require RGB ICC input components.");
        if (BinaryPrimitives.ReadUInt32BigEndian(decoded.AsSpan(8)) != 0x02100000)
            throw new NotSupportedException("Detached image ICC profile revisions other than 2.1 are not implemented.");
        if (Encoding.ASCII.GetString(decoded, 12, 4) is not ("scnr" or "mntr" or "prtr" or "spac"))
            throw new NotSupportedException("Detached image ICC profile class is not supported.");
        _ = new PdfIccProfileTransform(decoded, IntentIndex(renderingIntent));
        cancellationToken.ThrowIfCancellationRequested();
        var entries = new Dictionary<PdfName, PdfObject> { [N("N")] = new PdfInteger(3) };
        if (alternate is not null) entries.Add(N("Alternate"), N("DeviceRGB"));
        if (flate) entries.Add(N("Filter"), N("FlateDecode"));
        return new PdfStream(new PdfDictionary(entries), profile.EncodedData.Span);
    }

    internal static int IntentIndex(PdfName name) => name.ValueAsLatin1() switch
    {
        "Perceptual" => 0, "RelativeColorimetric" => 1, "Saturation" => 2, "AbsoluteColorimetric" => 3,
        _ => throw new NotSupportedException("Detached image rendering intent requires a standard name.")
    };

    private static PdfName N(string value) => new(Encoding.ASCII.GetBytes(value));
}
