using System.Text;
using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfFormScriptTests
{
    [Fact]
    public void NumberFormatAppliesSeparatorCurrencyAndNegativeStyles()
    {
        var options = new PdfFormNumberFormatOptions
        {
            DecimalPlaces = 2,
            SeparatorStyle = PdfFormSeparatorStyle.CommaGroupPointDecimal,
            NegativeStyle = PdfFormNegativeStyle.ParenthesesRed,
            CurrencySymbol = "$",
            PrependCurrency = true
        };

        Assert.Equal("$1,234.50", PdfFormFieldFormat.Number(1234.5, options));
        Assert.Equal("($1,234.50)", PdfFormFieldFormat.Number(-1234.5, options));
        Assert.True(PdfFormFieldFormat.UsesRedNegative(options.NegativeStyle));
        Assert.Equal("1.234,50", PdfFormFieldFormat.Number(1234.5, new PdfFormNumberFormatOptions
        {
            SeparatorStyle = PdfFormSeparatorStyle.PointGroupCommaDecimal
        }));
        Assert.Equal("7.5%", PdfFormFieldFormat.Percent(0.075, 1,
            PdfFormSeparatorStyle.CommaGroupPointDecimal));
    }

    [Fact]
    public void NumberParsingAcceptsGroupedAndParenthesizedValues()
    {
        Assert.True(PdfFormFieldFormat.TryParseNumber("1.234,56",
            PdfFormSeparatorStyle.PointGroupCommaDecimal, out double european));
        Assert.Equal(1234.56, european);
        Assert.True(PdfFormFieldFormat.TryParseNumber("($1,234.50)",
            PdfFormSeparatorStyle.CommaGroupPointDecimal, out double negative));
        Assert.Equal(-1234.5, negative);
        Assert.False(PdfFormFieldFormat.TryParseNumber("abc",
            PdfFormSeparatorStyle.CommaGroupPointDecimal, out _));
    }

    [Fact]
    public void DateAndSpecialFormatsFollowTheBuiltInPictures()
    {
        Assert.Equal("Sep 18, 2026",
            PdfFormFieldFormat.Date(new DateTime(2026, 9, 18), "mmm d, yyyy"));
        Assert.Equal("09/18/2026",
            PdfFormFieldFormat.Date(new DateTime(2026, 9, 18), PdfFormFieldFormat.DatePicture(4)));
        Assert.True(PdfFormFieldFormat.TryParseDate("09/18/2026", "mm/dd/yyyy",
            out DateTime parsed));
        Assert.Equal(new DateTime(2026, 9, 18), parsed);
        Assert.Equal("(555) 123-4567",
            PdfFormFieldFormat.Special("5551234567", PdfFormSpecialFormat.PhoneNumber));
        Assert.Equal("123-45-6789",
            PdfFormFieldFormat.Special("123456789", PdfFormSpecialFormat.SocialSecurityNumber));
        Assert.Equal("12345-6789",
            PdfFormFieldFormat.Special("123456789", PdfFormSpecialFormat.ZipCodePlusFour));
        Assert.False(PdfFormFieldFormat.IsSpecialValid("1234", PdfFormSpecialFormat.ZipCode));
    }

    [Fact]
    public void SimpleCalculateAggregatesNamedFields()
    {
        var values = new Dictionary<string, string>
        {
            ["a"] = "10",
            ["b"] = "2.5",
            ["c"] = string.Empty
        };

        Assert.Equal(12.5, PdfFormScript.Calculate(
            "AFSimple_Calculate(\"SUM\", new Array(\"a\", \"b\", \"c\"));", values).Number);
        Assert.Equal(25, PdfFormScript.Calculate(
            "AFSimple_Calculate(\"PRD\", \"a, b\");", values).Number);
        Assert.Equal(2.5, PdfFormScript.Calculate(
            "AFSimple_Calculate(\"MIN\", \"a, b\");", values).Number);
        Assert.Equal(PdfFormScriptStatus.Failed, PdfFormScript.Calculate(
            "AFSimple_Calculate(\"SUM\", \"missing\");", values).Status);
    }

    [Fact]
    public void CalculateEvaluatesTheBoundedFieldExpressionSubset()
    {
        var values = new Dictionary<string, string>
        {
            ["Price"] = "10.005",
            ["Qty"] = "3"
        };

        PdfFormScriptResult result = PdfFormScript.Calculate("""
            // line total
            var total = this.getField("Price").value * this.getField("Qty").value;
            event.value = Math.round(total * 100) / 100;
            """, values);

        Assert.Equal(PdfFormScriptStatus.Evaluated, result.Status);
        Assert.Equal(30.02, result.Number);
        Assert.Equal("30.02", result.Text);
        Assert.Equal(10, PdfFormScript.Calculate(
            "event.value = getField(\"Qty\").value > 2 ? 10 : 20;", values).Number);
    }

    [Fact]
    public void CalculateReportsScriptsOutsideTheSafeSubsetWithoutRunningThem()
    {
        var values = new Dictionary<string, string> { ["a"] = "1" };

        Assert.Equal(PdfFormScriptStatus.Unsupported,
            PdfFormScript.Calculate("app.alert(\"hello\");", values).Status);
        Assert.Equal(PdfFormScriptStatus.Unsupported,
            PdfFormScript.Calculate("this.submitForm(\"https://example.test\");", values).Status);
        Assert.Equal(PdfFormScriptStatus.Unsupported,
            PdfFormScript.Calculate("var x = 1;", values).Status);
        Assert.Equal(PdfFormScriptStatus.Failed,
            PdfFormScript.Calculate("event.value = 1 / 0;", values).Status);
        Assert.Equal(PdfFormScriptStatus.Failed, PdfFormScript.Calculate(
            "event.value = this.getField(\"missing\").value;", values).Status);
    }

    [Fact]
    public void FormatAndValidateApplyTheBuiltInFunctions()
    {
        PdfFormScriptResult formatted = PdfFormScript.Format(
            "AFNumber_Format(2, 0, 3, 0, \"$\", true);", "-1234.5");
        Assert.Equal("($1,234.50)", formatted.Text);
        Assert.True(formatted.IsRed);

        Assert.Equal("Sep 18, 2026",
            PdfFormScript.Format("AFDate_FormatEx(\"mmm d, yyyy\");", "2026-09-18").Text);
        Assert.Equal(string.Empty,
            PdfFormScript.Format("AFNumber_Format(2, 0, 0, 0, \"\", true);", "").Text);

        Assert.True(PdfFormScript.Validate("AFRange_Validate(true, 1, true, 10);", "5").IsValid);
        Assert.False(PdfFormScript.Validate("AFRange_Validate(true, 1, true, 10);", "12").IsValid);
        Assert.Equal(PdfFormScriptStatus.Unsupported,
            PdfFormScript.Format("this.getField(\"a\").display = 0;", "1").Status);
    }

    [Fact]
    public void EvaluateRunsFieldScriptsInCalculationOrder()
    {
        PdfDocument document = Form();

        PdfFormCalculationReport report = PdfFormCalculation.Evaluate(document);

        PdfFormCalculationEntry subtotal = report.Fields.Single(
            field => field.FieldName == "subtotal");
        PdfFormCalculationEntry tax = report.Fields.Single(field => field.FieldName == "tax");
        Assert.Equal("30", subtotal.Value);
        Assert.Equal("$30.00", subtotal.DisplayValue);
        Assert.Equal("3", tax.Value);
        Assert.Equal(PdfFormScriptStatus.Evaluated, subtotal.Status);
        Assert.Equal(1, report.UnsupportedCount);
        Assert.Equal(PdfFormScriptStatus.Unsupported,
            report.Fields.Single(field => field.FieldName == "notes").Status);
    }

    [Fact]
    public void EvaluateAppliesSuppliedValuesAndReadsScriptsWithoutExecutingThem()
    {
        PdfDocument document = Form();

        PdfFormCalculationReport report = PdfFormCalculation.Evaluate(document,
            new Dictionary<string, string> { ["qty"] = "5" });

        Assert.Equal("50", report.Fields.Single(field => field.FieldName == "subtotal").Value);
        Assert.Throws<KeyNotFoundException>(() => PdfFormCalculation.Evaluate(document,
            new Dictionary<string, string> { ["absent"] = "1" }));

        IReadOnlyList<PdfFormFieldScript> scripts = PdfFormCalculation.ReadScripts(document);
        Assert.Equal(4, scripts.Count);
        Assert.Contains(scripts, script => script.FieldName == "subtotal"
            && script.Trigger == PdfFormScriptTrigger.Format);
        Assert.Contains(scripts, script => script.FieldName == "notes"
            && script.Script.Contains("app.alert"));
    }

    private static PdfDocument Form() => Document(
        "<< /Fields [5 0 R 6 0 R 7 0 R 8 0 R 9 0 R] /CO [7 0 R 8 0 R] >>",
        "<< /FT /Tx /T (qty) /V (3) >>",
        "<< /FT /Tx /T (price) /V (10) >>",
        "<< /FT /Tx /T (subtotal) /AA << /C << /S /JavaScript /JS "
            + "(event.value = this.getField\\(\"qty\"\\).value * this.getField\\(\"price\"\\).value;) >> "
            + "/F << /S /JavaScript /JS (AFNumber_Format\\(2, 0, 0, 0, \"$\", true\\);) >> >> >>",
        "<< /FT /Tx /T (tax) /AA << /C << /S /JavaScript /JS "
            + "(event.value = this.getField\\(\"subtotal\"\\).value * 0.1;) >> >> >>",
        "<< /FT /Tx /T (notes) /AA << /C << /S /JavaScript /JS (app.alert\\(\"hi\"\\);) >> >> >>");

    private static PdfDocument Document(string acroForm, params string[] extras)
    {
        string[] objects =
        [
            $"<< /Type /Catalog /Pages 2 0 R /AcroForm {acroForm} >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 100 100] >>",
            "<< /Type /Page /Parent 2 0 R >>",
            "<< >>",
            .. extras
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
        foreach (int offset in offsets) pdf.Append($"{offset:0000000000} 00000 n \n");
        pdf.Append($"trailer << /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return PdfDocument.Open(Encoding.Latin1.GetBytes(pdf.ToString()));
    }
}
