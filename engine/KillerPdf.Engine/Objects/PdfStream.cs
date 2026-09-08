namespace KillerPdf.Engine.Objects;

/// <summary>A PDF stream dictionary and its encoded, undecoded payload bytes.</summary>
public sealed class PdfStream : PdfObject
{
    private readonly ReadOnlyMemory<byte> _encodedData;

    /// <summary>Creates a stream from its dictionary and encoded payload bytes.</summary>
    public PdfStream(PdfDictionary dictionary, ReadOnlySpan<byte> encodedData)
    {
        Dictionary = dictionary ?? throw new ArgumentNullException(nameof(dictionary));
        _encodedData = encodedData.ToArray();
    }

    private PdfStream(PdfDictionary dictionary, ReadOnlyMemory<byte> ownedData)
    {
        Dictionary = dictionary;
        _encodedData = ownedData;
    }

    // Only document-owned immutable storage may be shared. Public construction still copies.
    internal static PdfStream FromOwnedData(PdfDictionary dictionary, ReadOnlyMemory<byte> data) =>
        new(dictionary, data);

    /// <summary>Gets the stream dictionary.</summary>
    public PdfDictionary Dictionary { get; }
    /// <summary>Gets the encoded stream payload.</summary>
    public ReadOnlyMemory<byte> EncodedData => _encodedData;
}
