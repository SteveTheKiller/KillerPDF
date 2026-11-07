using System.Text;
using KillerPdf.Engine.Objects;

namespace KillerPdf.Engine.Authoring;

internal static class PdfCalibratedColorSpaceWriter
{
    internal static PdfArray Create(PdfCalibratedColorSpace colorSpace)
    {
        var entries = new List<(string Name, PdfObject Value)>
        {
            ("WhitePoint", new PdfArray([
                Number(colorSpace.WhiteX), Number(colorSpace.WhiteY), Number(colorSpace.WhiteZ)])),
            ("BlackPoint", new PdfArray([
                Number(colorSpace.BlackX), Number(colorSpace.BlackY), Number(colorSpace.BlackZ)]))
        };
        string name;
        switch (colorSpace)
        {
            case PdfCalGrayColorSpace gray:
                name = "CalGray";
                entries.Add(("Gamma", Number(gray.Gamma)));
                break;
            case PdfCalRgbColorSpace rgb:
                name = "CalRGB";
                entries.Add(("Gamma", new PdfArray(rgb.Gamma.Select(Number))));
                entries.Add(("Matrix", new PdfArray(rgb.Matrix.Select(Number))));
                break;
            default:
                throw new NotSupportedException(
                    $"Calibrated color space {colorSpace.GetType().FullName} cannot be authored.");
        }
        return new PdfArray([Name(name), new PdfDictionary(entries.Select(entry =>
            new KeyValuePair<PdfName, PdfObject>(Name(entry.Name), entry.Value)))]);
    }

    private static PdfObject Number(double value) =>
        value == Math.Truncate(value) && value is >= long.MinValue and <= long.MaxValue
            ? new PdfInteger((long)value)
            : new PdfReal(value);

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
}
