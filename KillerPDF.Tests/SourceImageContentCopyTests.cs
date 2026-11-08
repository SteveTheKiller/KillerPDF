using System.IO;
using System.Text;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class SourceImageContentCopyTests
{
    [Fact]
    public void RepeatedPictureProbesReuseOneStrictParseOfTheSameContent()
    {
        string folder = Path.Combine(Path.GetTempPath(), "kp-source-copy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string path = Path.Combine(folder, "picture.pdf");
            File.WriteAllBytes(path, Pdf("q 50 0 0 50 120 120 cm /Im1 Do Q"));
            using PdfWorkingDocument document = PdfWorkingDocument.Open(path, null);
            var first = document.OpenStrictForContentCopy();
            Assert.NotNull(first);
            Assert.Same(first, document.OpenStrictForContentCopy());
        }
        finally { Directory.Delete(folder, true); }
    }

    private static byte[] Pdf(string content)
    {
        var objects = new List<byte[]>
        {
            Ascii("<< /Type /Catalog /Pages 2 0 R >>"),
            Ascii("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Ascii("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /XObject << /Im1 5 0 R >> >> /Contents 4 0 R >>"),
            Stream("", Ascii(content)),
            Stream("/Type /XObject /Subtype /Image /Width 2 /Height 2 /ColorSpace /DeviceRGB /BitsPerComponent 8",
                [255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 0])
        };
        using var output = new MemoryStream();
        output.Write(Ascii("%PDF-1.4\n"));
        var offsets = new List<long>();
        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(output.Position);
            output.Write(Ascii((i + 1) + " 0 obj\n"));
            output.Write(objects[i]);
            output.Write(Ascii("\nendobj\n"));
        }
        long xref = output.Position;
        var table = new StringBuilder("xref\n0 " + (objects.Count + 1) + "\n0000000000 65535 f \n");
        foreach (long offset in offsets) table.Append(offset.ToString("D10") + " 00000 n \n");
        table.Append("trailer\n<< /Size " + (objects.Count + 1) + " /Root 1 0 R >>\nstartxref\n" + xref + "\n%%EOF\n");
        output.Write(Ascii(table.ToString()));
        return output.ToArray();
    }

    private static byte[] Ascii(string text) => Encoding.Latin1.GetBytes(text);

    private static byte[] Stream(string dictionary, byte[] data)
    {
        using var buffer = new MemoryStream();
        buffer.Write(Ascii("<< " + dictionary + " /Length " + data.Length + " >>\nstream\n"));
        buffer.Write(data);
        buffer.Write(Ascii("\nendstream"));
        return buffer.ToArray();
    }
}
