using System.Text;
using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfXfaReaderTests
{
    [Fact]
    public void ReadsPacketArraysAndReportsScriptsWithoutExecutingThem()
    {
        const string template = "<template><script>unsafe()</script></template>";
        const string datasets = "<datasets><value>Ada</value></datasets>";
        PdfDocument document = Document(template, datasets);

        PdfXfaInfo info = Assert.IsType<PdfXfaInfo>(PdfXfaReader.Read(document));

        Assert.True(info.IsPacketArray);
        Assert.True(info.ContainsScript);
        Assert.Equal(["template", "datasets"],
            info.Packets.Select(packet => packet.Name));
        Assert.Equal(datasets,
            Encoding.UTF8.GetString(info.Packets[1].Data.Span));
    }

    private static PdfDocument Document(string template, string datasets)
    {
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R /AcroForm 4 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>",
            "<< /XFA [(template) 5 0 R (datasets) 6 0 R] >>",
            $"<< /Length {Encoding.UTF8.GetByteCount(template)} >>\nstream\n{template}\nendstream",
            $"<< /Length {Encoding.UTF8.GetByteCount(datasets)} >>\nstream\n{datasets}\nendstream"
        ];
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int index = 0; index < objects.Length; index++)
        {
            offsets.Add(Encoding.Latin1.GetByteCount(pdf.ToString()));
            pdf.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        int xref = Encoding.Latin1.GetByteCount(pdf.ToString());
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets)
            pdf.Append($"{offset:0000000000} 00000 n \n");
        pdf.Append($"trailer << /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return PdfDocument.Open(Encoding.Latin1.GetBytes(pdf.ToString()));
    }
}
