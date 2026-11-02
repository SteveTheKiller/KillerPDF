using System.IO;
using System.Text;
using System.Windows.Media;
using KillerPDF.Services;
using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPDF.Tests;

// R2-012: two fields that share a name hold their own values, and a change of look touches neither.
public sealed class RepeatedFormFieldTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"killerpdf-repeated-{Guid.NewGuid():N}.pdf");

    public void Dispose() { if (File.Exists(_path)) File.Delete(_path); }

    private static string Field(string name, int top, string value) =>
        $"<< /Type /Annot /Subtype /Widget /FT /Tx /T ({name}) /V {value} /DA (/Helv 10 Tf 0 g) /P 3 0 R /Rect [10 {top} 110 {top + 20}] >>";

    private void WriteTwins(string first, string second)
    {
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R 5 0 R] /DR << /Font << /Helv << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Annots [4 0 R 5 0 R] >>",
            Field("same", 10, first), Field("same", 40, second)
        ];
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
        File.WriteAllBytes(_path, Encoding.ASCII.GetBytes(text.ToString()));
    }

    private IReadOnlyList<PdfFormWidgetInfo> Widgets() =>
        PdfFormWidgetReader.ReadPage(PdfDocument.Open(File.ReadAllBytes(_path)), 0);

    private static PdfEngineIntegration.FormEdits FontSize(string name, double size) => new(
        new Dictionary<string, string>(), new Dictionary<string, string>(),
        new Dictionary<string, IReadOnlyList<string>>(), new Dictionary<string, bool>(),
        new Dictionary<string, string>(), new Dictionary<string, double> { [name] = size });

    [Fact]
    public void ChangingTheFontSizeKeepsBothValues()
    {
        WriteTwins("(Alice)", "(Bob)");

        PdfEngineIntegration.ApplyFormValues(_path, FontSize("same", 14));

        Assert.Equal(["Alice", "Bob"], Widgets().Select(widget => widget.Value));
    }

    [Fact]
    public void ChangingTheBackgroundKeepsBothValues()
    {
        WriteTwins("(Alice)", "(Bob)");

        PdfEngineIntegration.SetTextFieldBackground(_path, "same", Color.FromRgb(255, 255, 0), null);

        Assert.Equal(["Alice", "Bob"], Widgets().Select(widget => widget.Value));
    }

    [Fact]
    public void ChangingTheBackgroundAndFontSizeTogetherKeepsBothValues()
    {
        WriteTwins("(Alice)", "(Bob)");

        PdfEngineIntegration.SetTextFieldBackground(_path, "same", Color.FromRgb(255, 255, 0), 14);

        Assert.Equal(["Alice", "Bob"], Widgets().Select(widget => widget.Value));
    }
}
