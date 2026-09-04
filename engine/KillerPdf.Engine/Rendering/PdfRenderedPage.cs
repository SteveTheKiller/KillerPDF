namespace KillerPdf.Engine.Rendering;

/// <summary>An immutable BGRA32 page bitmap produced by the engine.</summary>
public sealed class PdfRenderedPage
{
    internal PdfRenderedPage(int width, int height, byte[] pixels,
        IEnumerable<string>? diagnostics = null)
    {
        Width = width;
        Height = height;
        Pixels = new ReadOnlyMemory<byte>(pixels);
        Diagnostics = Array.AsReadOnly((diagnostics ?? []).ToArray());
    }

    /// <summary>Gets the bitmap width.</summary>
    public int Width { get; }
    /// <summary>Gets the bitmap height.</summary>
    public int Height { get; }
    /// <summary>Gets tightly packed BGRA32 pixels with a top-left origin.</summary>
    public ReadOnlyMemory<byte> Pixels { get; }
    /// <summary>Gets compatibility or incomplete-render diagnostics.</summary>
    public IReadOnlyList<string> Diagnostics { get; }
}
