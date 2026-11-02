using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using Xunit;

namespace KillerPdf.Engine.Tests.Editing;

public sealed class PdfDuplicateFieldNameTests
{
    private static string Field(string name, int top) =>
        $"<< /Type /Annot /Subtype /Widget /FT /Tx /T ({name}) /DA (/Helv 10 Tf 0 g) /P 3 0 R /Rect [10 {top} 110 {top + 20}] >>";

    // Written by hand because the builder refuses to author the repeated name.
    private static byte[] RepeatedNameForm() => Assemble(
    [
        "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R 5 0 R 6 0 R] /DR << /Font << /Helv << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >> >> >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Annots [4 0 R 5 0 R 6 0 R] >>",
        Field("Texte44", 10), Field("Texte44", 40), Field("other", 70)
    ]);

    private static byte[] Assemble(string[] objects)
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
        return Encoding.ASCII.GetBytes(text.ToString());
    }

    [Fact]
    public void AFileThatAlreadyRepeatsAFieldNameStillSavesAnotherField()
    {
        byte[] bytes = RepeatedNameForm();

        byte[] saved = new PdfIncrementalPageEditor(PdfDocument.Open(bytes))
            .SetTextFieldValue("other", "typed").Build();

        Assert.True(saved.Length > bytes.Length);
    }

    // The builder refuses the repeated name, so a second field is built under a name of the same
    // length and renamed in the bytes, which leaves every offset where it was.
    private static byte[] WithSecondFieldRenamed(PdfDocumentBuilder builder, string first, string second)
    {
        Assert.Equal(first.Length, second.Length);
        string text = Encoding.Latin1.GetString(builder.Build());
        string Hex(string name) => "<FEFF" + string.Concat(name.Select(letter => ((int)letter).ToString("X4"))) + ">";
        Assert.Contains(Hex(second), text, StringComparison.OrdinalIgnoreCase);
        return Encoding.Latin1.GetBytes(text.Replace(Hex(second), Hex(first), StringComparison.OrdinalIgnoreCase));
    }

    private static PdfDocumentBuilder OnePage() => new PdfDocumentBuilder()
        .AddPage(300, 300, new PdfContentStreamBuilder());

    private static IReadOnlyList<PdfFormWidgetInfo> Widgets(byte[] saved) =>
        PdfFormWidgetReader.ReadPage(PdfDocument.Open(saved), 0);

    [Fact]
    public void TypingInARepeatedTextFieldFillsEveryFieldOfThatName()
    {
        byte[] bytes = WithSecondFieldRenamed(OnePage()
            .AddTextField(0, "same", 10, 10, 100, 20)
            .AddTextField(0, "sama", 10, 40, 100, 20), "same", "sama");

        byte[] saved = new PdfIncrementalPageEditor(PdfDocument.Open(bytes))
            .SetTextFieldValue("same", "typed").Build();

        Assert.All(Widgets(saved), widget => Assert.Equal("typed", widget.Value));
        Assert.Equal(2, Widgets(saved).Count);
    }

    [Fact]
    public void TickingARepeatedCheckBoxTicksEveryFieldOfThatName()
    {
        byte[] bytes = WithSecondFieldRenamed(OnePage()
            .AddCheckBox(0, "same", 10, 10, 20, 20)
            .AddCheckBox(0, "sama", 10, 40, 20, 20), "same", "sama");

        byte[] saved = new PdfIncrementalPageEditor(PdfDocument.Open(bytes))
            .SetCheckBoxValue("same", true).Build();

        Assert.All(Widgets(saved), widget => Assert.Equal("/Yes", widget.Value));
        Assert.Equal(2, Widgets(saved).Count);
    }

    [Fact]
    public void PickingARepeatedRadioGroupSelectsInEveryGroupOfThatName()
    {
        PdfRadioButtonOption[] Options(double top) =>
            [new(0, 10, top, 20, 20, "Red"), new(0, 40, top, 20, 20, "Blue")];
        byte[] bytes = WithSecondFieldRenamed(OnePage()
            .AddRadioGroup("same", Options(10))
            .AddRadioGroup("sama", Options(60)), "same", "sama");

        byte[] saved = new PdfIncrementalPageEditor(PdfDocument.Open(bytes))
            .SetRadioButtonValue("same", "Blue").Build();

        Assert.All(Widgets(saved), widget => Assert.Equal("/Blue", widget.Value));
        Assert.Equal(4, Widgets(saved).Count);
    }

    [Fact]
    public void ChoosingInARepeatedComboBoxChoosesInEveryFieldOfThatName()
    {
        byte[] bytes = WithSecondFieldRenamed(OnePage()
            .AddComboBox(0, "same", 10, 10, 100, 20, ["A", "B"])
            .AddComboBox(0, "sama", 10, 40, 100, 20, ["A", "B"]), "same", "sama");

        byte[] saved = new PdfIncrementalPageEditor(PdfDocument.Open(bytes))
            .SetChoiceFieldValue("same", "B").Build();

        Assert.All(Widgets(saved), widget => Assert.Equal("B", widget.Value));
        Assert.Equal(2, Widgets(saved).Count);
    }

    [Fact]
    public void ResettingARepeatedFieldResetsEveryFieldOfThatName()
    {
        byte[] bytes = WithSecondFieldRenamed(OnePage()
            .AddTextField(0, "same", 10, 10, 100, 20, "typed", defaultValue: "start")
            .AddTextField(0, "sama", 10, 40, 100, 20, "typed", defaultValue: "start"), "same", "sama");

        byte[] saved = new PdfIncrementalPageEditor(PdfDocument.Open(bytes))
            .ResetFormField("same").Build();

        Assert.All(Widgets(saved), widget => Assert.Equal("start", widget.Value));
        Assert.Equal(2, Widgets(saved).Count);
    }

    [Fact]
    public void RestylingARepeatedTextFieldRestylesEveryFieldOfThatName()
    {
        byte[] bytes = WithSecondFieldRenamed(OnePage()
            .AddTextField(0, "same", 10, 10, 100, 20, "kept")
            .AddTextField(0, "sama", 10, 40, 100, 20, "kept"), "same", "sama");
        var yellow = new PdfRgbColor(1, 1, 0);

        byte[] saved = new PdfIncrementalPageEditor(PdfDocument.Open(bytes))
            .SetTextFieldAppearance("same", new PdfFormFieldAppearanceStyle { BackgroundColor = yellow }).Build();

        Assert.All(Widgets(saved), widget => Assert.Equal(yellow, widget.BackgroundColor));
        Assert.All(Widgets(saved), widget => Assert.Equal("kept", widget.Value));
        Assert.Equal(2, Widgets(saved).Count);
    }

    [Fact]
    public void RenamingAParentWhoseKidsRepeatANameMovesEveryKid()
    {
        string Kid(int top) =>
            $"<< /Type /Annot /Subtype /Widget /FT /Tx /T (x) /Parent 4 0 R /DA (/Helv 10 Tf 0 g) /P 3 0 R /Rect [10 {top} 110 {top + 20}] >>";
        byte[] bytes = Assemble(
        [
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] /DR << /Font << /Helv << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Annots [5 0 R 6 0 R] >>",
            "<< /T (A) /Kids [5 0 R 6 0 R] >>", Kid(10), Kid(40)
        ]);

        byte[] saved = new PdfIncrementalPageEditor(PdfDocument.Open(bytes))
            .RenameFormField("A", "B").Build();

        Assert.Equal(["B.x", "B.x"], Widgets(saved).Select(widget => widget.FieldName));
    }

    [Fact]
    public void ARenameOntoAnExistingNameIsStillRefused()
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(300, 300, new PdfContentStreamBuilder())
            .AddTextField(0, "first", 10, 10, 100, 20)
            .AddTextField(0, "second", 10, 40, 100, 20)
            .Build());

        Assert.Throws<InvalidOperationException>(() =>
            new PdfIncrementalPageEditor(document).RenameFormField("second", "first").Build());
    }
}
