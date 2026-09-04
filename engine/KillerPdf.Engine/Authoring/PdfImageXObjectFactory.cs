using System.Text;
using KillerPdf.Engine.Objects;

namespace KillerPdf.Engine.Authoring;

internal static class PdfImageXObjectFactory
{
    internal static PdfStream Create(PdfImage image, PdfIndirectReference? softMaskReference)
    {
        string colorSpace = image.ColorSpace switch
        {
            PdfImageColorSpace.Gray => "DeviceGray",
            PdfImageColorSpace.Rgb => "DeviceRGB",
            PdfImageColorSpace.Cmyk => "DeviceCMYK",
            _ => throw new ArgumentOutOfRangeException(nameof(image))
        };
        var entries = new List<(string Name, PdfObject Value)>
        {
            ("Type", Name("XObject")), ("Subtype", Name("Image")),
            ("Width", new PdfInteger(image.Width)), ("Height", new PdfInteger(image.Height)),
            ("ColorSpace", Name(colorSpace)),
            ("BitsPerComponent", new PdfInteger(image.BitsPerComponent)),
            ("Filter", Name(image.Filter))
        };
        if (image.InvertComponents)
            entries.Add(("Decode", new PdfArray(Enumerable.Range(0, 4)
                .SelectMany(_ => new PdfObject[] { new PdfInteger(1), new PdfInteger(0) }))));
        if (image.Filter == "CCITTFaxDecode")
            entries.Add(("DecodeParms", Dictionary(
                ("K", new PdfInteger(-1)),
                ("Columns", new PdfInteger(image.Width)),
                ("Rows", new PdfInteger(image.Height)),
                ("BlackIs1", new PdfBoolean(false)))));
        else if (image.PngPredictorColors > 0)
            entries.Add(("DecodeParms", Dictionary(
                ("Predictor", new PdfInteger(15)),
                ("Colors", new PdfInteger(image.PngPredictorColors)),
                ("BitsPerComponent", new PdfInteger(8)),
                ("Columns", new PdfInteger(image.Width)))));
        if (softMaskReference is not null) entries.Add(("SMask", softMaskReference));
        return new PdfStream(Dictionary([.. entries]), image.Data.Span);
    }

    private static PdfDictionary Dictionary(params (string Name, PdfObject Value)[] entries) =>
        new(entries.Select(entry => new KeyValuePair<PdfName, PdfObject>(Name(entry.Name), entry.Value)));
    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
}
