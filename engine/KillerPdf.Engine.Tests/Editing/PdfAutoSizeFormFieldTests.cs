using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Filters;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Editing;

public sealed class PdfAutoSizeFormFieldTests
{
    [Fact]
    public void SavingAnAutomaticSizeTextFieldPreservesItsValueAndAutomaticSetting()
    {
        byte[] source = Form("/Helv 0 Tf 0 g");

        byte[] saved = new PdfIncrementalPageEditor(PdfDocument.Open(source))
            .SetTextFieldValue("Name", "Ada").Build();

        Assert.Equal(source, saved[..source.Length]);
        var document = PdfDocument.Open(saved);
        var field = Assert.Single(PdfFormWidgetReader.ReadPage(document, 0));
        Assert.Equal("Ada", field.Value);
        var widget = Assert.IsType<PdfDictionary>(document.Resolve(new PdfIndirectReference(4, 0)));
        Assert.Equal("/Helv 0 Tf 0 g", Encoding.Latin1.GetString(
            Assert.IsType<PdfString>(widget[new PdfName("DA"u8)]).Bytes.Span));
        PdfObject Resolve(PdfObject value) => value is PdfIndirectReference reference
            ? document.Resolve(reference) : value;
        var appearances = Assert.IsType<PdfDictionary>(Resolve(widget[new PdfName("AP"u8)]));
        var normal = Assert.IsType<PdfStream>(Resolve(appearances[new PdfName("N"u8)]));
        Assert.Contains("(Ada) Tj", Encoding.Latin1.GetString(PdfStreamDecoder.Decode(normal)));
        var frame = new PdfPageRenderer(document).Render(0, new PdfRenderOptions(200, 100));
        Assert.Empty(frame.Diagnostics);
        Assert.Contains(frame.Pixels.ToArray(), channel => channel != 255);
    }

    [Fact]
    public void SavingAnUnstyledFieldDoesNotPaintABackgroundOrBorder()
    {
        byte[] saved = new PdfIncrementalPageEditor(PdfDocument.Open(Form("/Helv 0 Tf 0 g")))
            .SetTextFieldValue("Name", "").Build();

        var frame = new PdfPageRenderer(PdfDocument.Open(saved))
            .Render(0, new PdfRenderOptions(200, 100, transparentBackground: true));

        Assert.Empty(frame.Diagnostics);
        byte[] pixels = frame.Pixels.ToArray();
        for (int offset = 3; offset < pixels.Length; offset += 4)
            Assert.Equal(0, pixels[offset]);
    }

    private static byte[] Form(string appearance)
    {
        string[] objects = [
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] /DA (/Helv 10 Tf 0 g) /DR << /Font << /Helv 5 0 R >> >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Annots [4 0 R] >>",
            $"<< /Type /Annot /Subtype /Widget /FT /Tx /T (Name) /DA ({appearance}) /P 3 0 R /Rect [10 10 110 30] >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>" ];
        var text = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int index = 0; index < objects.Length; index++)
        {
            offsets.Add(text.Length);
            text.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        int table = text.Length;
        text.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) text.Append($"{offset:D10} 00000 n \n");
        text.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{table}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(text.ToString());
    }
}
