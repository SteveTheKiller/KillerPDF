namespace KillerPdf.Engine.Rendering;

internal abstract class PdfColorTransform
{
    internal abstract int Components { get; }
    internal abstract bool CanConvertFromXyz { get; }
    internal virtual bool SupportsBlending => true;
    internal virtual double ClampComponent(int component, double value) => Math.Clamp(value, 0, 1);
    internal abstract void ToXyz(ReadOnlySpan<double> device, Span<double> xyz);
    internal abstract void FromXyz(ReadOnlySpan<double> xyz, Span<double> device);

    internal void ConvertTo(PdfColorTransform destination, ReadOnlySpan<double> source, Span<double> result)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (source.Length != Components || result.Length != destination.Components)
            throw new ArgumentException("Color transform channel counts do not match.");
        if (ReferenceEquals(this, destination))
        {
            for (int channel = 0; channel < source.Length; channel++)
                if (!double.IsFinite(source[channel])) throw new ArgumentException("Color values must be finite.");
            source.CopyTo(result);
            for (int channel = 0; channel < result.Length; channel++) result[channel] = ClampComponent(channel, result[channel]);
            return;
        }
        Span<double> xyz = stackalloc double[3];
        ToXyz(source, xyz);
        destination.FromXyz(xyz, result);
    }
}
