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

    private void WriteTwins(string first, string second, string secondExtra = "") => Write(
        "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R 5 0 R] /DR << /Font << /Helv << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >> >> >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Annots [4 0 R 5 0 R] >>",
        Field("same", 10, first), Field("same", 40, second).Replace("/DA", secondExtra + " /DA"));

    private void Write(params string[] objects)
    {
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

    private static PdfEngineIntegration.FormEdits Values(Dictionary<string, string>? text = null, Dictionary<string, string>? radio = null) => new(
        text ?? [], new Dictionary<string, string>(), new Dictionary<string, IReadOnlyList<string>>(),
        new Dictionary<string, bool>(), radio ?? [], new Dictionary<string, double>());

    // R3-015: one typed value goes to both, and the save says when one could not take it.
    [Fact]
    public void ATwinThatCannotTakeTheValueIsNamed()
    {
        WriteTwins("(Alice)", "(Bob)", "/MaxLen 5");

        IReadOnlyCollection<string> kept = PdfEngineIntegration.ApplyFormValues(
            _path, Values(new Dictionary<string, string> { ["same"] = "Seven77" }));

        Assert.Equal(["Seven77", "Bob"], Widgets().Select(widget => widget.Value));
        Assert.Equal(["same"], kept);
    }

    [Fact]
    public void TwinsThatBothTakeTheValueNameNothing()
    {
        WriteTwins("(Alice)", "(Bob)");

        Assert.Empty(PdfEngineIntegration.ApplyFormValues(_path, Values(new Dictionary<string, string> { ["same"] = "Carol" })));
    }

    // R2-010: the viewer holds a repeated name to its tightest limit, so the typing never reaches this.
    [Fact]
    public void TheSmallestLimitOfARepeatedNameHoldsThemAll()
    {
        WriteTwins("(Alice)", "(Bob)", "/MaxLen 5");

        IReadOnlyDictionary<string, int> limits = FormTextLimit.SmallestByName(PdfDocument.Open(File.ReadAllBytes(_path)));

        Assert.Equal(5, limits.GetValueOrDefault("same"));
    }

    private const string RadioFlags = "/FT /Btn /Ff 49152 /T (choice)";

    private static string RadioWidget(int parent, string state, string states) =>
        $"<< /Type /Annot /Subtype /Widget /Parent {parent} 0 R /P 3 0 R /Rect [10 10 30 30] /AS /{state} /AP << /N << {states} >> >> >>";

    // R2-008: a radio group must not show two selections, so a twin with no such state turns off.
    [Fact]
    public void ARadioTwinWithoutTheStateTurnsOffAndIsNamed()
    {
        Write(
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R 5 0 R] >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Annots [6 0 R 7 0 R 9 0 R] >>",
            $"<< {RadioFlags} /V /A /Kids [6 0 R 9 0 R] >>",
            $"<< {RadioFlags} /V /C /Kids [7 0 R] >>",
            RadioWidget(4, "A", "/A 8 0 R /Off 8 0 R"),
            RadioWidget(5, "C", "/C 8 0 R /Off 8 0 R"),
            "<< /Type /XObject /Subtype /Form /BBox [0 0 20 20] /Length 0 >>\nstream\n\nendstream",
            RadioWidget(4, "Off", "/B 8 0 R /Off 8 0 R"));

        IReadOnlyCollection<string> kept = PdfEngineIntegration.ApplyFormValues(
            _path, Values(radio: new Dictionary<string, string> { ["choice"] = "/B" }));

        PdfDocument document = PdfDocument.Open(File.ReadAllBytes(_path));
        Assert.Equal(["choice"], kept);
        var name = new KillerPdf.Engine.Objects.PdfName("V"u8);
        var state = new KillerPdf.Engine.Objects.PdfName("AS"u8);
        Assert.Equal("B", Assert.IsType<KillerPdf.Engine.Objects.PdfName>(((KillerPdf.Engine.Objects.PdfDictionary)document.Resolve(4))[name]).ValueAsLatin1());
        Assert.Equal("Off", Assert.IsType<KillerPdf.Engine.Objects.PdfName>(((KillerPdf.Engine.Objects.PdfDictionary)document.Resolve(5))[name]).ValueAsLatin1());
        Assert.Equal("Off", Assert.IsType<KillerPdf.Engine.Objects.PdfName>(((KillerPdf.Engine.Objects.PdfDictionary)document.Resolve(7))[state]).ValueAsLatin1());
    }
}
