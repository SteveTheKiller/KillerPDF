using System.Text;
using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfXfaAcroFormConverterTests
{
    [Fact]
    public void ConvertPreservesPageAndCreatesEditableFieldWithDatasetValue()
    {
        PdfDocument source = Document(
            """<template><subform name="form" layout="position"><field name="name" x="10pt" y="20pt" w="70pt" h="15pt"><bind ref="$record.person.name"/><ui><textEdit/></ui></field></subform></template>""",
            """<datasets><data><person><name>Ada</name></person></data></datasets>""");

        PdfDocument converted = PdfDocument.Open(PdfXfaAcroFormConverter.Convert(source));

        Assert.Null(PdfXfaReader.Read(converted));
        PdfFormWidgetInfo widget = Assert.Single(PdfFormWidgetReader.ReadPage(converted, 0));
        Assert.Equal("form.name", widget.FieldName);
        Assert.Equal("Ada", widget.Value);
        Assert.Equal(10, widget.Left);
        Assert.Equal(65, widget.Bottom);
        Assert.Equal(80, widget.Right);
        Assert.Equal(80, widget.Top);
    }

    [Fact]
    public void ConvertPreservesCheckButtonAndSignatureFieldSemantics()
    {
        PdfDocument source = Document(
            """<template><subform name="form" layout="position"><field name="approved" x="10pt" y="10pt" w="12pt" h="12pt"><ui><checkButton/></ui></field><field name="signature" x="30pt" y="10pt" w="50pt" h="12pt"><ui><signature/></ui></field></subform></template>""",
            """<datasets><data><form><approved>true</approved><signature/></form></data></datasets>""");

        PdfDocument converted = PdfDocument.Open(PdfXfaAcroFormConverter.Convert(source));
        Dictionary<string, PdfFormWidgetInfo> widgets = PdfFormWidgetReader.ReadPage(converted, 0)
            .ToDictionary(widget => widget.FieldName, StringComparer.Ordinal);

        Assert.Equal(PdfFormFieldKind.Button, widgets["form.approved"].FieldKind);
        Assert.Equal("/Yes", widgets["form.approved"].Value);
        Assert.Equal(PdfFormFieldKind.Signature, widgets["form.signature"].FieldKind);
    }

    [Fact]
    public void ConvertPreservesChoiceDisplayAndSavedValues()
    {
        PdfDocument source = Document(
            """<template><subform name="form" layout="position"><field name="color" x="10pt" y="10pt" w="70pt" h="15pt"><ui><choiceList/></ui><items><text>Red</text><text>Blue</text></items><items save="1"><text>r</text><text>b</text></items></field></subform></template>""",
            """<datasets><data><form><color>b</color></form></data></datasets>""");

        PdfFormWidgetInfo widget = Assert.Single(PdfFormWidgetReader.ReadPage(
            PdfDocument.Open(PdfXfaAcroFormConverter.Convert(source)), 0));

        Assert.Equal(PdfFormFieldKind.Choice, widget.FieldKind);
        Assert.Equal("b", widget.Value);
        Assert.Equal(["Red", "Blue"], widget.Options.Select(option => option.DisplayValue));
        Assert.Equal(["r", "b"], widget.Options.Select(option => option.ExportValue));
    }

    [Fact]
    public void ConvertCanFlattenGeneratedAppearancesIntoStandardPageContent()
    {
        PdfDocument source = Document(
            """<template><subform name="form" layout="position"><field name="name" x="10pt" y="20pt" w="70pt" h="15pt"><ui><textEdit/></ui></field></subform></template>""",
            """<datasets><data><form><name>Ada</name></form></data></datasets>""");

        PdfDocument converted = PdfDocument.Open(PdfXfaAcroFormConverter.Convert(
            source, PdfXfaConversionMode.Flattened));

        Assert.Null(PdfXfaReader.Read(converted));
        Assert.Empty(PdfFormWidgetReader.ReadPage(converted, 0));
        Assert.Contains("Ada", string.Concat(new PdfPageContentReader(converted).Read(0)
            .Letters.Select(letter => letter.Value)), StringComparison.Ordinal);
    }

    [Fact]
    public void ConvertPaginatesRepeatedDynamicValuesAsEditableFields()
    {
        PdfDocument source = Document(
            """<template><subform name="rows" layout="tb"><field name="item" w="70pt" h="40pt"><bind ref="$record.order.item"/><ui><textEdit/></ui></field></subform></template>""",
            """<datasets><data><order><item>one</item><item>two</item><item>three</item></order></data></datasets>""",
            """<config><present><pdf><dynamicRender>required</dynamicRender></pdf></present></config>""");

        PdfDocument converted = PdfDocument.Open(PdfXfaAcroFormConverter.Convert(source));

        Assert.Equal(2, new PdfPageContentReader(converted).PageCount);
        Assert.Equal(["one", "two"], PdfFormWidgetReader.ReadPage(converted, 0)
            .Select(widget => widget.Value));
        PdfFormWidgetInfo third = Assert.Single(PdfFormWidgetReader.ReadPage(converted, 1));
        Assert.Equal("rows.item[2]", third.FieldName);
        Assert.Equal("three", third.Value);
    }

    private static PdfDocument Document(string template, string datasets, string? config = null)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /AcroForm 4 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>",
            config is null
                ? "<< /Fields [] /XFA [(template) 5 0 R (datasets) 6 0 R] >>"
                : "<< /Fields [] /XFA [(template) 5 0 R (datasets) 6 0 R (config) 7 0 R] >>",
            $"<< /Length {Encoding.UTF8.GetByteCount(template)} >>\nstream\n{template}\nendstream",
            $"<< /Length {Encoding.UTF8.GetByteCount(datasets)} >>\nstream\n{datasets}\nendstream"
        };
        if (config is not null)
            objects.Add($"<< /Length {Encoding.UTF8.GetByteCount(config)} >>\nstream\n{config}\nendstream");
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int index = 0; index < objects.Count; index++)
        {
            offsets.Add(Encoding.Latin1.GetByteCount(pdf.ToString()));
            pdf.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        int xref = Encoding.Latin1.GetByteCount(pdf.ToString());
        pdf.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) pdf.Append($"{offset:0000000000} 00000 n \n");
        pdf.Append($"trailer << /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return PdfDocument.Open(Encoding.Latin1.GetBytes(pdf.ToString()));
    }
}
