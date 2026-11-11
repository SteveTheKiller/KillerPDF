namespace KillerPdf.Engine.Rendering;

// Shared by a profile's intent variants and fallback attempts. Overlapping tag slices
// must not multiply decoded sample storage beyond the cache's eightfold expansion.
internal sealed class PdfIccStorageBudget(int encodedBytes)
{
    private long _remaining = (long)encodedBytes * 8;
    private readonly Dictionary<ReadOnlyMemory<byte>, PdfIccCurve> _curves = [];

    internal void Reserve(long bytes)
    {
        if (bytes < 0 || bytes > _remaining)
            throw new NotSupportedException("ICC decoded samples exceed the profile storage limit.");
        _remaining -= bytes;
    }

    internal PdfIccCurve ReadCurve(ReadOnlyMemory<byte> data)
    {
        if (_curves.TryGetValue(data, out var curve)) return curve;
        curve = new PdfIccCurve(data, this);
        _curves.Add(data, curve);
        return curve;
    }
}
