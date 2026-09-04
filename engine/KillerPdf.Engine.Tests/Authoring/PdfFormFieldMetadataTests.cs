using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using Xunit;

namespace KillerPdf.Engine.Tests.Authoring;

public sealed class PdfFormFieldMetadataTests
{
    [Fact]
    public void EveryFieldType_WritesTooltipAndMappingName()
    {
        static PdfFormFieldMetadata Metadata(string value) => new()
        {
            Tooltip = $"{value} tooltip",
            MappingName = $"{value}.export"
        };
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage().AddBlankPage()
            .AddTextField(0, "text", 10, 10, 100, 20, fieldMetadata: Metadata("text"))
            .AddCheckBox(0, "check", 10, 40, 20, 20, fieldMetadata: Metadata("check"))
            .AddRadioGroup("radio",
            [
                new PdfRadioButtonOption(0, 10, 70, 20, 20, "A"),
                new PdfRadioButtonOption(1, 10, 70, 20, 20, "B")
            ], fieldMetadata: Metadata("radio"))
            .AddComboBox(0, "choice", 10, 100, 100, 20, ["A", "B"],
                fieldMetadata: Metadata("choice"))
            .Build());
        PdfDictionary catalog = ResolveDictionary(document, document.Trailer[Name("Root")]);
        PdfArray fields = Assert.IsType<PdfArray>(
            Assert.IsType<PdfDictionary>(catalog[Name("AcroForm")])[Name("Fields")]);

        string[] names = ["text", "check", "radio", "choice"];
        for (int index = 0; index < names.Length; index++)
        {
            PdfDictionary field = ResolveDictionary(document, fields[index]);
            Assert.Equal($"{names[index]} tooltip",
                DecodeUnicode(Assert.IsType<PdfString>(field[Name("TU")])));
            Assert.Equal($"{names[index]}.export",
                DecodeUnicode(Assert.IsType<PdfString>(field[Name("TM")])));
        }
    }

    [Fact]
    public void FieldMetadata_RejectsEmptyNames()
    {
        Assert.Throws<ArgumentException>(() => new PdfDocumentBuilder()
            .AddBlankPage()
            .AddTextField(0, "text", 10, 10, 100, 20,
                fieldMetadata: new PdfFormFieldMetadata { Tooltip = " " }));
    }

    [Fact]
    public void EveryFieldType_WritesCommonFieldFlags()
    {
        var common = new PdfFormFieldOptions { ReadOnly = true, Required = true, NoExport = true };
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage()
            .AddTextField(0, "text", 10, 10, 100, 20,
                options: new PdfTextFieldOptions { ReadOnly = true, Required = true, NoExport = true })
            .AddCheckBox(0, "check", 10, 40, 20, 20, options: common)
            .AddRadioGroup("radio",
            [
                new PdfRadioButtonOption(0, 10, 70, 20, 20, "A"),
                new PdfRadioButtonOption(0, 40, 70, 20, 20, "B")
            ], fieldOptions: common)
            .AddComboBox(0, "choice", 10, 100, 100, 20, ["A", "B"], fieldOptions: common)
            .Build());
        PdfDictionary catalog = ResolveDictionary(document, document.Trailer[Name("Root")]);
        PdfArray fields = Assert.IsType<PdfArray>(
            Assert.IsType<PdfDictionary>(catalog[Name("AcroForm")])[Name("Fields")]);

        Assert.Equal(7, FieldFlags(document, fields[0]));
        Assert.Equal(7, FieldFlags(document, fields[1]));
        Assert.Equal((1 << 15) | 7, FieldFlags(document, fields[2]));
        Assert.Equal((1 << 17) | 7, FieldFlags(document, fields[3]));
    }

    [Fact]
    public void EveryFieldType_WritesTypedWidgetVisibility()
    {
        var hidden = new PdfFormFieldOptions { Visibility = PdfFormFieldVisibility.Hidden };
        var printOnly = new PdfFormFieldOptions
        {
            Visibility = PdfFormFieldVisibility.HiddenButPrintable
        };
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage()
            .AddTextField(0, "text", 10, 10, 100, 20,
                options: new PdfTextFieldOptions
                {
                    Visibility = PdfFormFieldVisibility.VisibleButDoesNotPrint
                })
            .AddCheckBox(0, "check", 10, 40, 20, 20, options: hidden)
            .AddRadioGroup("radio",
            [
                new PdfRadioButtonOption(0, 10, 70, 20, 20, "A"),
                new PdfRadioButtonOption(0, 40, 70, 20, 20, "B")
            ], fieldOptions: printOnly)
            .AddComboBox(0, "choice", 10, 100, 100, 20, ["A", "B"],
                fieldOptions: printOnly)
            .AddUriPushButton(0, "button", 10, 130, 100, 20, "Open",
                "https://example.com", fieldOptions: hidden)
            .AddSignatureField(0, "signature", 10, 160, 100, 20,
                fieldOptions: printOnly)
            .Build());
        PdfDictionary catalog = ResolveDictionary(document, document.Trailer[Name("Root")]);
        PdfArray fields = Assert.IsType<PdfArray>(
            Assert.IsType<PdfDictionary>(catalog[Name("AcroForm")])[Name("Fields")]);

        Assert.Equal(0, WidgetFlags(document, fields[0]));
        Assert.Equal(2, WidgetFlags(document, fields[1]));
        PdfArray radioWidgets = Assert.IsType<PdfArray>(
            ResolveDictionary(document, fields[2])[Name("Kids")]);
        Assert.All(radioWidgets, widget => Assert.Equal(36, WidgetFlags(document, widget)));
        Assert.Equal(36, WidgetFlags(document, fields[3]));
        Assert.Equal(2, WidgetFlags(document, fields[4]));
        Assert.Equal(36, WidgetFlags(document, fields[5]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PdfDocumentBuilder()
            .AddBlankPage().AddCheckBox(0, "bad", 0, 0, 20, 20,
                options: new PdfFormFieldOptions
                {
                    Visibility = (PdfFormFieldVisibility)99
                }).Build());
    }

    [Fact]
    public void PdfUa2_WritesFormObjectReferencesForEveryWidget()
    {
        static PdfFormFieldMetadata Metadata(string tooltip) => new() { Tooltip = tooltip };
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .SetMetadata(new PdfDocumentMetadata
            {
                Title = "Accessible form",
                Language = "en-US"
            })
            .EnablePdfUa2Conformance()
            .AddBlankPage()
            .AddCheckBox(0, "survey.accepted", 10, 10, 20, 20,
                fieldMetadata: Metadata("Accept the terms"))
            .AddRadioGroup("survey.plan",
            [
                new PdfRadioButtonOption(0, 10, 40, 20, 20, "Free"),
                new PdfRadioButtonOption(0, 40, 40, 20, 20, "Pro")
            ], fieldMetadata: Metadata("Choose a plan"))
            .AddSignatureField(0, "survey.signature", 10, 70, 140, 30,
                fieldMetadata: Metadata("Sign the survey"))
            .AddStructureContainer(PdfStructureType.Document)
            .Build());
        PdfDictionary catalog = ResolveDictionary(document, document.Trailer[Name("Root")]);
        PdfDictionary pages = ResolveDictionary(document, catalog[Name("Pages")]);
        PdfDictionary page = ResolveDictionary(document,
            Assert.IsType<PdfArray>(pages[Name("Kids")])[0]);
        PdfArray annotations = Assert.IsType<PdfArray>(page[Name("Annots")]);
        PdfDictionary structureRoot = ResolveDictionary(document, catalog[Name("StructTreeRoot")]);
        PdfDictionary parentTree = ResolveDictionary(document, structureRoot[Name("ParentTree")]);
        PdfArray numbers = Assert.IsType<PdfArray>(parentTree[Name("Nums")]);

        Assert.Equal(4, annotations.Count);
        Assert.Equal(8, numbers.Count);
        var mappedAnnotations = new HashSet<int>();
        for (int index = 0; index < numbers.Count; index += 2)
        {
            PdfDictionary formElement = ResolveDictionary(document, numbers[index + 1]);
            Assert.Equal("Form",
                Assert.IsType<PdfName>(formElement[Name("S")]).ValueAsLatin1());
            PdfDictionary objectReference = Assert.IsType<PdfDictionary>(formElement[Name("K")]);
            mappedAnnotations.Add(Assert.IsType<PdfIndirectReference>(
                objectReference[Name("Obj")]).ObjectNumber);
        }
        Assert.Equal([.. annotations.Select(annotation =>
            Assert.IsType<PdfIndirectReference>(annotation).ObjectNumber)],
            mappedAnnotations);
        Assert.All(annotations, annotation =>
        {
            PdfDictionary widget = ResolveDictionary(document, annotation);
            Assert.True(widget.ContainsKey(Name("StructParent")));
            Assert.False(string.IsNullOrWhiteSpace(
                DecodeUnicode(Assert.IsType<PdfString>(widget[Name("Contents")]))));
        });

        Assert.Throws<InvalidOperationException>(() => new PdfDocumentBuilder()
            .SetMetadata(new PdfDocumentMetadata { Title = "Form", Language = "en-US" })
            .EnablePdfUa2Conformance().AddBlankPage()
            .AddCheckBox(0, "check", 0, 0, 20, 20)
            .AddStructureContainer(PdfStructureType.Document).Build());
        Assert.Throws<InvalidOperationException>(() => new PdfDocumentBuilder()
            .SetMetadata(new PdfDocumentMetadata { Title = "Form", Language = "en-US" })
            .EnablePdfUa2Conformance().AddBlankPage()
            .AddTextField(0, "text", 0, 0, 100, 20,
                fieldMetadata: Metadata("Enter text"))
            .AddStructureContainer(PdfStructureType.Document).Build());
    }

    private static string DecodeUnicode(PdfString value) =>
        Encoding.BigEndianUnicode.GetString(value.Bytes.Span[2..]);
    private static PdfDictionary ResolveDictionary(PdfDocument document, PdfObject value) =>
        Assert.IsType<PdfDictionary>(document.Resolve(Assert.IsType<PdfIndirectReference>(value)));
    private static long FieldFlags(PdfDocument document, PdfObject value) =>
        Assert.IsType<PdfInteger>(ResolveDictionary(document, value)[Name("Ff")]).Value;
    private static long WidgetFlags(PdfDocument document, PdfObject value) =>
        Assert.IsType<PdfInteger>(ResolveDictionary(document, value)[Name("F")]).Value;
    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
}
