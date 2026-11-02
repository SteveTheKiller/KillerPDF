using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Objects;
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

    private static string Text(string name, int top, string extra = "") =>
        $"<< /Type /Annot /Subtype /Widget /FT /Tx /T ({name}) /DA (/Helv 10 Tf 0 g) /P 3 0 R /Rect [10 {top} 110 {top + 20}] {extra} >>";

    private static string Combo(string name, int top, string options, string value) =>
        $"<< /Type /Annot /Subtype /Widget /FT /Ch /Ff 131072 /T ({name}) /Opt [{options}] /V ({value}) /DA (/Helv 10 Tf 0 g) /P 3 0 R /Rect [10 {top} 110 {top + 20}] >>";

    // Every object from 4 on is a field and a widget on the one page.
    private static byte[] Form(params string[] fields)
    {
        string references = string.Join(' ', Enumerable.Range(4, fields.Length).Select(number => $"{number} 0 R"));
        return Assemble(
        [
            $"<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [{references}] /DR << /Font << /Helv << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Annots [{references}] >>",
            .. fields
        ]);
    }

    private static string[] Values(byte[] saved) => [.. Widgets(saved).Select(widget => widget.Value ?? string.Empty)];

    // R2-008
    [Fact]
    public void ARadioGroupWithoutThePickedOptionTurnsOffAndIsNamed()
    {
        PdfRadioButtonOption[] Small(double top) => [new(0, 10, top, 20, 20, "S"), new(0, 40, top, 20, 20, "M")];
        PdfRadioButtonOption[] Large(double top) => [new(0, 10, top, 20, 20, "L"), new(0, 40, top, 20, 20, "XL")];
        byte[] bytes = WithSecondFieldRenamed(OnePage()
            .AddRadioGroup("same", Small(10), "S")
            .AddRadioGroup("sama", Large(60), "XL"), "same", "sama");
        byte[] saved = [];
        var editor = new PdfIncrementalPageEditor(PdfDocument.Open(bytes)).SetRadioButtonValue("same", "L");

        Exception? error = Record.Exception(() => saved = editor.Build());

        Assert.Null(error);
        Assert.Equal(["/Off", "/Off", "/L", "/L"], Values(saved));
        Assert.Equal(["same"], editor.FieldsThatKeptTheirValue);
    }

    // R2-009
    [Fact]
    public void AComboBoxWithoutThePickedOptionKeepsItsOwnSelection()
    {
        byte[] bytes = WithSecondFieldRenamed(OnePage()
            .AddComboBox(0, "same", 10, 10, 100, 20, ["A", "B"], "A")
            .AddComboBox(0, "sama", 10, 40, 100, 20, ["A", "C"], "C"), "same", "sama");
        byte[] saved = [];

        Exception? error = Record.Exception(() => saved = new PdfIncrementalPageEditor(PdfDocument.Open(bytes))
            .SetChoiceFieldValue("same", "B").Build());

        Assert.Null(error);
        Assert.Equal(["B", "C"], Values(saved));
    }

    // R2-010
    [Theory]
    [InlineData("/MaxLen 10")]
    [InlineData("/MaxLen 10 /Ff 16777216")]
    public void ATextFieldTooShortForTheValueKeepsItsOwn(string shortTwin)
    {
        byte[] bytes = Form(Text("Name", 10, "/MaxLen 30 /V (Alice)"), Text("Name", 40, shortTwin + " /V (Bob)"));
        byte[] saved = [];

        Exception? error = Record.Exception(() => saved = new PdfIncrementalPageEditor(PdfDocument.Open(bytes))
            .SetTextFieldValue("Name", "12345678901234567890").Build());

        Assert.Null(error);
        Assert.Equal(["12345678901234567890", "Bob"], Values(saved));
    }

    [Fact]
    public void AMalformedTwinNeverBlocksSavingTheOtherOne()
    {
        byte[] bytes = Form(Combo("Dept", 10, "(A) (B)", "A"), Combo("Dept", 40, "", "A"));
        byte[] saved = [];

        Exception? error = Record.Exception(() => saved = new PdfIncrementalPageEditor(PdfDocument.Open(bytes))
            .SetChoiceFieldValue("Dept", "B").Build());

        Assert.Null(error);
        Assert.Equal(["B", "A"], Values(saved));
    }

    // A twin that fails on its second widget must not keep what was already done to its first.
    [Fact]
    public void ATwinThatFailsPartWayThroughItsWidgetsIsLeftUntouched()
    {
        string Kid(int top, string rectangle) =>
            $"<< /Type /Annot /Subtype /Widget /Parent 5 0 R /P 3 0 R /Rect {rectangle} >>";
        byte[] bytes = Assemble(
        [
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R 5 0 R] /DR << /Font << /Helv << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Annots [4 0 R 6 0 R] >>",
            Text("Name", 10, "/V (Alice)"),
            "<< /FT /Tx /T (Name) /V (Bob) /DA (/Helv 10 Tf 0 g) /Kids [6 0 R 7 0 R] >>",
            Kid(40, "[10 40 110 60]"), Kid(70, "[10 70 110]")
        ]);
        byte[] saved = [];

        Exception? error = Record.Exception(() => saved = new PdfIncrementalPageEditor(PdfDocument.Open(bytes))
            .SetTextFieldValue("Name", "typed").Build());

        Assert.Null(error);
        PdfDocument document = PdfDocument.Open(saved);
        Assert.Equal("Bob", Assert.Single(Widgets(saved), widget => widget.ObjectNumber == 6).Value);
        var firstKid = Assert.IsType<PdfDictionary>(document.Resolve(new PdfIndirectReference(6, 0)));
        Assert.False(firstKid.ContainsKey(new PdfName("AP"u8)));
        Assert.Equal("typed", Assert.Single(Widgets(saved), widget => widget.ObjectNumber == 4).Value);
    }

    [Fact]
    public void AValueNoTwinCanTakeIsStillRefused()
    {
        byte[] bytes = Form(Text("Name", 10, "/MaxLen 10"), Text("Name", 40, "/MaxLen 5"));

        Assert.Throws<ArgumentException>(() => new PdfIncrementalPageEditor(PdfDocument.Open(bytes))
            .SetTextFieldValue("Name", "12345678901234567890").Build());
    }

    [Fact]
    public void AChangeOfFontSizeOrBackgroundKeepsEachTwinsOwnValue()
    {
        byte[] bytes = Form(Text("same", 10, "/V (Alice)"), Text("same", 40, "/V (Bob)"));
        var yellow = new PdfRgbColor(1, 1, 0);

        byte[] resized = new PdfIncrementalPageEditor(PdfDocument.Open(bytes))
            .SetTextFieldFontSize("same", 14).Build();
        byte[] colored = new PdfIncrementalPageEditor(PdfDocument.Open(bytes))
            .SetTextFieldBackground("same", yellow).Build();

        Assert.Equal(["Alice", "Bob"], Values(resized));
        Assert.Equal(["Alice", "Bob"], Values(colored));
        Assert.All(Widgets(colored), widget => Assert.Equal(yellow, widget.BackgroundColor));
    }

    [Fact]
    public void AChangeOfLookAsksForTheFontOfEachTwinsOwnValue()
    {
        byte[] bytes = Form(Text("same", 10, "/V (Alice)"), Text("same", 40, "/V (Bob)"));
        var asked = new List<string>();

        new PdfIncrementalPageEditor(PdfDocument.Open(bytes))
            .SetTextFieldFontSize("same", 14, value => { asked.Add(value); return null; }).Build();

        Assert.Equal(["Alice", "Bob"], asked);
    }

    // R2-011
    [Fact]
    public void RemovingARepeatedNameRemovesEveryFieldOfThatName()
    {
        byte[] bytes = Form(Text("Texte44", 10, "/V (Alice)"), Text("Texte44", 40, "/MaxLen 5 /V (Bob)"), Text("other", 70));
        byte[] saved = [];

        Exception? error = Record.Exception(() => saved = new PdfIncrementalPageEditor(PdfDocument.Open(bytes))
            .RemoveFormField("Texte44").Build());

        Assert.Null(error);
        Assert.Equal("other", Assert.Single(Widgets(saved)).FieldName);
    }

    // R2-021: a parent that only groups fields holds no value, so it stands in no field's way.
    [Fact]
    public void AStructuralParentBesideATerminalFieldOfItsNameIsPassedOver()
    {
        byte[] bytes = Assemble(
        [
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R 5 0 R] /DR << /Font << /Helv << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Annots [4 0 R 6 0 R] >>",
            Text("A", 10, "/V (old)"), "<< /T (A) /Kids [6 0 R] >>", Text("x", 40, "/Parent 5 0 R /V (kept)")
        ]);
        byte[] saved = [];

        Exception? error = Record.Exception(() => saved = new PdfIncrementalPageEditor(PdfDocument.Open(bytes))
            .SetTextFieldValue("A", "typed").Build());

        Assert.Null(error);
        Assert.Equal(["typed", "kept"], Values(saved));
    }

    // Fields of different types under one name have no single right answer, so the refusal stands.
    [Fact]
    public void ATextFieldAndACheckBoxUnderOneNameAreStillRefused()
    {
        byte[] bytes = Assemble(
        [
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R 5 0 R] /DR << /Font << /Helv << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Annots [4 0 R 5 0 R] >>",
            Text("A", 10),
            "<< /Type /Annot /Subtype /Widget /FT /Btn /T (A) /P 3 0 R /Rect [10 40 30 60] /AS /Off /AP << /N << /Yes 6 0 R /Off 6 0 R >> >> >>",
            "<< /Length 0 >>\nstream\n\nendstream"
        ]);

        Assert.Throws<InvalidOperationException>(() => new PdfIncrementalPageEditor(PdfDocument.Open(bytes))
            .SetTextFieldValue("A", "typed").Build());
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
