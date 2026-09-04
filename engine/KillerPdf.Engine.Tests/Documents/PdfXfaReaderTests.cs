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
        Assert.Equal(PdfXfaFormType.Unknown, info.FormType);
    }

    [Theory]
    [InlineData("required", PdfXfaFormType.Dynamic)]
    [InlineData("prohibited", PdfXfaFormType.Static)]
    public void DetectsFormTypeFromConfigPacket(string dynamicRender, PdfXfaFormType expected)
    {
        string config = $"<config><present><pdf><dynamicRender>{dynamicRender}</dynamicRender></pdf></present></config>";
        PdfDocument document = Document("<template/>", "<datasets/>", config);

        PdfXfaInfo info = Assert.IsType<PdfXfaInfo>(PdfXfaReader.Read(document));

        Assert.Equal(expected, info.FormType);
    }

    [Fact]
    public void ReadsUnicodeAndRepeatedDatasetValuesByQualifiedPath()
    {
        const string datasets = """
            <xfa:datasets xmlns:xfa="http://www.xfa.org/schema/xfa-data/1.0/">
              <xfa:data><form><name>Zo&#235;</name><colors>red</colors><colors>blue</colors></form></xfa:data>
            </xfa:datasets>
            """;
        PdfXfaInfo info = Assert.IsType<PdfXfaInfo>(
            PdfXfaReader.Read(Document("<template/>", datasets)));

        PdfFormDataSet values = PdfXfaDatasets.Read(info);

        Assert.Equal(["form.name", "form.colors"], values.Fields.Select(field => field.Name));
        Assert.Equal(["Zoë"], values.Fields[0].Values);
        Assert.Equal(["red", "blue"], values.Fields[1].Values);
        Assert.False(values.ContainsJavaScript);
    }

    [Fact]
    public void WritesQualifiedUnicodeAndRepeatedDatasetValues()
    {
        var values = new PdfFormDataSet
        {
            Fields = [
                new PdfFormDataField { Name = "form.name", Values = ["Zoë"] },
                new PdfFormDataField { Name = "form.colors", Values = ["red", "blue"] }]
        };

        byte[] packet = PdfXfaDatasets.Write(values);
        PdfFormDataSet restored = PdfXfaDatasets.Read(new PdfXfaInfo
        {
            Packets = [new PdfXfaPacket("datasets", packet)]
        });

        Assert.Equal(["form.name", "form.colors"],
            restored.Fields.Select(field => field.Name));
        Assert.Equal(["red", "blue"], restored.Fields[1].Values);
        Assert.Throws<ArgumentException>(() => PdfXfaDatasets.Write(new PdfFormDataSet
        {
            Fields = [
                new PdfFormDataField { Name = "form", Values = ["value"] },
                new PdfFormDataField { Name = "form.child", Values = ["value"] }]
        }));
    }

    [Fact]
    public void ReplacesDatasetsWithoutChangingOtherPacketsOrScriptWarning()
    {
        PdfXfaInfo source = Assert.IsType<PdfXfaInfo>(PdfXfaReader.Read(Document(
            "<template><script>preserved()</script></template>",
            "<datasets><data><old>value</old></data></datasets>")));
        byte[] originalTemplate = source.Packets[0].Data.ToArray();

        PdfXfaInfo replaced = PdfXfaDatasets.Replace(source, new PdfFormDataSet
        {
            Fields = [new PdfFormDataField { Name = "form.name", Values = ["Zoë"] }]
        });

        Assert.True(replaced.ContainsScript);
        Assert.Equal(source.Packets.Select(packet => packet.Name),
            replaced.Packets.Select(packet => packet.Name));
        Assert.Equal(originalTemplate, replaced.Packets[0].Data.ToArray());
        Assert.Equal(["Zoë"], PdfXfaDatasets.Read(replaced).Fields[0].Values);
        Assert.Contains("<old>value</old>",
            Encoding.UTF8.GetString(source.Packets[1].Data.Span), StringComparison.Ordinal);
        Assert.Throws<NotSupportedException>(() => PdfXfaDatasets.Replace(
            source with { IsPacketArray = false }, new PdfFormDataSet()));
    }

    [Fact]
    public void EditsOneDatasetOccurrenceAndPreservesUnrelatedContent()
    {
        const string datasets = """
            <xfa:datasets xmlns:xfa="http://www.xfa.org/schema/xfa-data/1.0/">
              <xfa:data><form version="7"><name>Old</name><color>red</color><color>blue</color></form></xfa:data>
            </xfa:datasets>
            """;
        PdfXfaInfo source = Assert.IsType<PdfXfaInfo>(PdfXfaReader.Read(
            Document("<template><script>preserved()</script></template>", datasets)));
        byte[] template = source.Packets[0].Data.ToArray();

        PdfXfaInfo changed = PdfXfaDatasets.SetValue(source, "form.color", 1, "grün");
        PdfFormDataSet values = PdfXfaDatasets.Read(changed);
        string changedXml = Encoding.UTF8.GetString(changed.Packets[1].Data.Span);

        Assert.Equal(["red", "grün"], values.Fields.Single(field =>
            field.Name == "form.color").Values);
        Assert.Contains("version=\"7\"", changedXml, StringComparison.Ordinal);
        Assert.Contains("<name>Old</name>", changedXml, StringComparison.Ordinal);
        Assert.Equal(template, changed.Packets[0].Data.ToArray());
        Assert.True(changed.ContainsScript);
        Assert.Equal(["red", "blue"], PdfXfaDatasets.Read(source).Fields.Single(field =>
            field.Name == "form.color").Values);
        Assert.Throws<KeyNotFoundException>(() =>
            PdfXfaDatasets.SetValue(source, "form.color", 2, "green"));
        Assert.Throws<NotSupportedException>(() =>
            PdfXfaDatasets.SetValue(source with { IsPacketArray = false },
                "form.color", 0, "green"));
    }

    [Fact]
    public void ReadsTemplateFieldHierarchyBindingsAndSafeBehaviorFlags()
    {
        const string template = """
            <template xmlns="http://www.xfa.org/schema/xfa-template/3.3/">
              <subform name="invoice">
                <field name="total">
                  <ui><numericEdit/></ui>
                  <bind ref="$record.invoice.total"/>
                  <calculate><script contentType="application/x-formcalc">1 + 1</script></calculate>
                  <validate><script contentType="application/x-javascript">unsafe()</script></validate>
                  <format><picture>num{z,zz9.99}</picture></format>
                </field>
                <subform name="customer"><field name="name"><ui><textEdit/></ui></field></subform>
              </subform>
            </template>
            """;
        PdfXfaInfo source = Assert.IsType<PdfXfaInfo>(PdfXfaReader.Read(
            Document(template, "<datasets><data/></datasets>")));

        PdfXfaTemplateInfo inspected = PdfXfaTemplate.Read(source);

        Assert.Equal(2, inspected.Fields.Count);
        PdfXfaTemplateField total = inspected.Fields[0];
        Assert.Equal("invoice.total", total.Path);
        Assert.Equal("$record.invoice.total", total.Binding);
        Assert.Equal("numericEdit", total.ControlType);
        Assert.True(total.HasCalculation);
        Assert.True(total.HasValidation);
        Assert.True(total.HasFormatting);
        Assert.Equal("invoice.customer.name", inspected.Fields[1].Path);
        Assert.Equal("textEdit", inspected.Fields[1].ControlType);
        Assert.Equal(2, inspected.ScriptCount);
        Assert.True(source.ContainsScript);
    }

    [Fact]
    public void TemplateInspectionRejectsMissingAndMalformedTemplates()
    {
        Assert.Throws<InvalidOperationException>(() => PdfXfaTemplate.Read(new PdfXfaInfo
        {
            Packets = [new PdfXfaPacket("datasets", "<datasets/>"u8.ToArray())]
        }));
        Assert.Throws<InvalidOperationException>(() => PdfXfaTemplate.Read(new PdfXfaInfo
        {
            Packets = [new PdfXfaPacket("template", "<template><field/></template>"u8.ToArray())]
        }));
    }

    private static PdfDocument Document(string template, string datasets, string? config = null)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /AcroForm 4 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>",
            config is null
                ? "<< /XFA [(template) 5 0 R (datasets) 6 0 R] >>"
                : "<< /XFA [(template) 5 0 R (datasets) 6 0 R (config) 7 0 R] >>",
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
        foreach (int offset in offsets)
            pdf.Append($"{offset:0000000000} 00000 n \n");
        pdf.Append($"trailer << /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return PdfDocument.Open(Encoding.Latin1.GetBytes(pdf.ToString()));
    }
}
